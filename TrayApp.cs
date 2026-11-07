using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SendToMonitor;

/// <summary>Tray icon, hidden owner window, and the popup menus.</summary>
internal sealed class TrayApp : ApplicationContext
{
    // Title-bar and taskbar menu command IDs
    private const int CmdMaximize = 1;
    private const int CmdWindowMenu = 2;
    private const int CmdTaskManager = 3;
    private const int CmdTaskbarSettings = 4;
    private const int CmdLabel = 5; // disabled header rows, never returned
    private const int CmdMonitorBase = 100;
    private const int CmdWindowBase = 1000;

    // Tray menu command IDs
    private const int CmdTrayMaximize = 1;
    private const int CmdTrayStartup = 2;
    private const int CmdTrayPause = 3;
    private const int CmdTrayExit = 4;
    private const int CmdTrayTaskbar = 5;
    private const int CmdTrayLabels = 6;
    private const int CmdTrayNameBase = 100;

    private readonly Settings _settings = Settings.Load();
    private readonly OwnerWindow _owner;
    private readonly NotifyIcon _tray;
    private readonly Icon _icon;
    private readonly Icon _pausedIcon;
    private readonly IntPtr _pausedIconHandle;
    private readonly System.Windows.Forms.Timer _watchdog;

    private bool _paused;
    private MonitorNameDialog? _nameDialog;
    private POINT _lastCursor;
    private int _lastSeenTick;

    public TrayApp()
    {
        StartupRegistration.RefreshPathIfEnabled();

        _owner = new OwnerWindow(this);
        (_icon, _pausedIcon, _pausedIconHandle) = LoadIcons();

        _tray = new NotifyIcon { Icon = _icon, Text = "SendToMonitor", Visible = true };
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button is MouseButtons.Right or MouseButtons.Left) ShowTrayMenu();
        };

        // JIT the hit-test path now, so the first real right-click isn't slow inside the hook.
        if (Native.GetCursorPos(out POINT warm)) TitleBarDetector.FindTitleBarWindow(warm);

        // Same for the monitor labels: the first one costs ~70 ms of font/GDI+ setup. Never shown.
        try { MonitorLabels.Create(MonitorEnumerator.GetMonitors()).Dispose(); }
        catch (Exception ex) { Log.Error("Preparing monitor labels", ex); }

        // UI Automation thread for telling empty taskbar space from taskbar buttons; warm it up on the taskbar.
        MouseHook.TaskbarMenuEnabled = _settings.TaskbarMenu;
        IntPtr taskbar = FindWindowW("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero && Native.GetWindowRect(taskbar, out RECT tr))
            warm = new POINT((tr.Left + tr.Right) / 2, (tr.Top + tr.Bottom) / 2);
        TaskbarProbe.Start(warm);

        MouseHook.Install(_owner.Handle);

        // Windows silently unhooks LL hooks that time out. If the cursor moves but our hook
        // hasn't seen anything, reinstall it.
        _watchdog = new System.Windows.Forms.Timer { Interval = 5000 };
        _watchdog.Tick += (_, _) => CheckHook();
        _watchdog.Start();
    }

    // ------------------------------------------------------------------ title-bar menu

    private void ShowMonitorMenu(IntPtr target, POINT pt)
    {
        if (!Native.IsWindow(target)) return;

        List<MonitorDesc> monitors = MonitorEnumerator.GetMonitors(_settings.MonitorNames);
        if (monitors.Count < 2) return;

        IntPtr current = Native.MonitorFromWindow(target, Native.MONITOR_DEFAULTTONEAREST);
        IntPtr systemMenu = Native.GetSystemMenu(target, false);

        int cmd;
        IntPtr menu = Native.CreatePopupMenu();
        try
        {
            for (int i = 0; i < monitors.Count; i++)
            {
                bool here = monitors[i].Handle == current;
                Append(menu, CmdMonitorBase + i, monitors[i].MenuLabel, isChecked: here, enabled: !here);
            }
            AppendSeparator(menu);
            Append(menu, CmdMaximize, "&Maximize on arrival", isChecked: _settings.MaximizeOnArrival);
            AppendSeparator(menu);
            Append(menu, CmdWindowMenu, "&Window menu…", enabled: systemMenu != IntPtr.Zero);

            cmd = TrackMenu(menu, pt, useTrackPopupMenuEx: true, labels: monitors);
        }
        finally
        {
            Native.DestroyMenu(menu);
        }

        switch (cmd)
        {
            case 0:
                RestoreFocus(target);
                break;

            case CmdMaximize:
                _settings.MaximizeOnArrival = !_settings.MaximizeOnArrival;
                _settings.Save();
                RestoreFocus(target);
                break;

            case CmdWindowMenu:
                ShowSystemMenu(target, pt);
                break;

            case >= CmdMonitorBase when cmd - CmdMonitorBase < monitors.Count:
                try
                {
                    WindowMover.Move(target, monitors[cmd - CmdMonitorBase], _settings.MaximizeOnArrival);
                }
                catch (Exception ex)
                {
                    Log.Error("Moving window", ex);
                }
                if (Native.IsWindow(target)) Native.SetForegroundWindow(target);
                break;
        }
    }

    /// <summary>The window's own system menu (Restore / Move / Size / ... / Close).</summary>
    private void ShowSystemMenu(IntPtr target, POINT pt)
    {
        IntPtr systemMenu = Native.GetSystemMenu(target, false);
        if (systemMenu == IntPtr.Zero)
        {
            RestoreFocus(target);
            return;
        }

        // The window normally refreshes these states itself just before showing the menu.
        bool max = Native.IsZoomed(target), min = Native.IsIconic(target);
        long style = Native.GetWindowLong(target, Native.GWL_STYLE);
        SetItemEnabled(systemMenu, Native.SC_RESTORE, max || min);
        SetItemEnabled(systemMenu, Native.SC_MOVE, !max);
        SetItemEnabled(systemMenu, Native.SC_SIZE, !max && !min && (style & Native.WS_THICKFRAME) != 0);
        SetItemEnabled(systemMenu, Native.SC_MINIMIZE, !min && (style & Native.WS_MINIMIZEBOX) != 0);
        SetItemEnabled(systemMenu, Native.SC_MAXIMIZE, !max && (style & Native.WS_MAXIMIZEBOX) != 0);

        int cmd = TrackMenu(systemMenu, pt, useTrackPopupMenuEx: false);
        if (cmd == 0)
        {
            RestoreFocus(target);
            return;
        }
        Native.SetForegroundWindow(target);
        Native.PostMessageW(target, Native.WM_SYSCOMMAND, (IntPtr)cmd, IntPtr.Zero);
    }

    private static void SetItemEnabled(IntPtr menu, int command, bool enabled) =>
        Native.EnableMenuItem(menu, (uint)command, Native.MF_BYCOMMAND | (enabled ? Native.MF_ENABLED : Native.MF_GRAYED));

    // ------------------------------------------------------------------ taskbar menu

    /// <summary>Right-click on empty taskbar space: list app windows on other monitors and bring the chosen one here.</summary>
    private void ShowTaskbarMenu(IntPtr taskbar, POINT pt)
    {
        List<MonitorDesc> monitors = MonitorEnumerator.GetMonitors(_settings.MonitorNames);
        if (monitors.Count < 2) return;

        IntPtr here = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
        MonitorDesc? target = monitors.Find(m => m.Handle == here);
        if (target == null) return;

        // The click was swallowed, so this is still whatever the user was working in.
        IntPtr previousForeground = Native.GetForegroundWindow();

        List<WindowEntry> windows = WindowList.GetAppWindows();
        int iconSize = Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, target.Dpi);
        var bitmaps = new List<IntPtr>();

        int cmd;
        IntPtr menu = Native.CreatePopupMenu();
        try
        {
            // Icons and check marks share one column instead of two.
            var info = new MENUINFO { cbSize = Marshal.SizeOf<MENUINFO>(), fMask = Native.MIM_STYLE, dwStyle = Native.MNS_CHECKORBMP };
            Native.SetMenuInfo(menu, ref info);

            Append(menu, CmdLabel, $"Bring a window to {target.ShortLabel}", enabled: false);
            AppendSeparator(menu);

            bool any = false;
            foreach (MonitorDesc source in monitors)
            {
                if (source.Handle == target.Handle) continue;
                bool headerAdded = false;
                for (int i = 0; i < windows.Count; i++)
                {
                    WindowEntry w = windows[i];
                    if (w.Monitor != source.Handle) continue;
                    if (!headerAdded)
                    {
                        if (any) AppendSeparator(menu);
                        Append(menu, CmdLabel, $"On {source.ShortLabel}", enabled: false);
                        headerAdded = any = true;
                    }

                    string text = MenuText(w.Title) + (w.Blocked != null ? "\t" + w.Blocked : "");
                    Append(menu, CmdWindowBase + i, text, enabled: w.Blocked == null);
                    IntPtr bitmap = WindowList.CreateMenuBitmap(w.Handle, iconSize);
                    if (bitmap != IntPtr.Zero)
                    {
                        bitmaps.Add(bitmap);
                        var mii = new MENUITEMINFOW { cbSize = Marshal.SizeOf<MENUITEMINFOW>(), fMask = Native.MIIM_BITMAP, hbmpItem = bitmap };
                        Native.SetMenuItemInfoW(menu, (uint)(CmdWindowBase + i), false, ref mii);
                    }
                }
            }
            if (!any) Append(menu, CmdLabel, "No windows on other monitors", enabled: false);

            AppendSeparator(menu);
            Append(menu, CmdMaximize, "&Maximize on arrival", isChecked: _settings.MaximizeOnArrival);
            AppendSeparator(menu);
            Append(menu, CmdTaskManager, "Tas&k Manager");
            Append(menu, CmdTaskbarSettings, "Taskbar &settings");

            // Open above a bottom taskbar (below a top one) without covering it.
            Native.GetWindowRect(taskbar, out RECT bar);
            bool bottomHalf = pt.Y > (target.Bounds.Top + target.Bounds.Bottom) / 2;
            cmd = TrackMenu(menu, pt, useTrackPopupMenuEx: true,
                extraFlags: Native.TPM_VERTICAL | (bottomHalf ? Native.TPM_BOTTOMALIGN : 0), exclude: bar, labels: monitors);
        }
        finally
        {
            Native.DestroyMenu(menu);
            foreach (IntPtr bitmap in bitmaps) Native.DeleteObject(bitmap);
        }

        switch (cmd)
        {
            case 0:
                RestoreFocus(previousForeground);
                break;

            case CmdMaximize:
                _settings.MaximizeOnArrival = !_settings.MaximizeOnArrival;
                _settings.Save();
                RestoreFocus(previousForeground);
                break;

            case CmdTaskManager:
                Launch("taskmgr.exe");
                break;

            case CmdTaskbarSettings:
                Launch("ms-settings:taskbar");
                break;

            case >= CmdWindowBase when cmd - CmdWindowBase < windows.Count:
                IntPtr hwnd = windows[cmd - CmdWindowBase].Handle;
                if (!Native.IsWindow(hwnd)) break;
                try
                {
                    WindowMover.Move(hwnd, target, _settings.MaximizeOnArrival);
                }
                catch (Exception ex)
                {
                    Log.Error("Moving window", ex);
                }
                if (Native.IsWindow(hwnd)) Native.SetForegroundWindow(hwnd);
                break;
        }
    }

    /// <summary>A window title as menu text: one line, at most ~60 characters, '&amp;' shown literally.</summary>
    private static string MenuText(string title)
    {
        const int MaxLength = 60;
        var chars = title.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        string text = new string(chars).Trim();
        if (text.Length > MaxLength) text = text[..(MaxLength - 1)].TrimEnd() + "…";
        return text.Replace("&", "&&");
    }

    private static void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user said no to the UAC prompt.
        }
        catch (Exception ex)
        {
            Log.Error($"Starting {target}", ex);
        }
    }

    /// <summary>After Esc, give focus back to the window. If the user clicked elsewhere, leave it there.</summary>
    private void RestoreFocus(IntPtr target)
    {
        IntPtr fg = Native.GetForegroundWindow();
        if ((fg == IntPtr.Zero || fg == _owner.Handle) && Native.IsWindow(target))
            Native.SetForegroundWindow(target);
    }

    // ------------------------------------------------------------------ tray menu

    private void ShowTrayMenu()
    {
        if (_nameDialog != null)
        {
            _nameDialog.Activate();
            return;
        }

        Native.GetCursorPos(out POINT pt);
        List<MonitorDesc> monitors = MonitorEnumerator.GetMonitors(_settings.MonitorNames);

        int cmd;
        IntPtr menu = Native.CreatePopupMenu();
        try
        {
            IntPtr names = Native.CreatePopupMenu();
            for (int i = 0; i < monitors.Count; i++)
                Append(names, CmdTrayNameBase + i, monitors[i].MenuLabel);
            if (!Native.AppendMenuW(menu, Native.MF_POPUP, (UIntPtr)(nuint)names, "Monitor &names"))
                Native.DestroyMenu(names); // otherwise it's destroyed with its parent
            Append(menu, CmdTrayLabels, "Show names &on monitors", isChecked: _settings.ShowMonitorLabels);
            AppendSeparator(menu);
            Append(menu, CmdTrayMaximize, "&Maximize on arrival", isChecked: _settings.MaximizeOnArrival);
            Append(menu, CmdTrayTaskbar, "&Taskbar right-click menu", isChecked: _settings.TaskbarMenu);
            Append(menu, CmdTrayStartup, "&Start with Windows", isChecked: StartupRegistration.IsEnabled);
            Append(menu, CmdTrayPause, "&Pause", isChecked: _paused);
            AppendSeparator(menu);
            Append(menu, CmdTrayExit, "E&xit");
            cmd = TrackMenu(menu, pt, useTrackPopupMenuEx: true, labels: monitors);
        }
        finally
        {
            Native.DestroyMenu(menu);
        }

        switch (cmd)
        {
            case CmdTrayMaximize:
                _settings.MaximizeOnArrival = !_settings.MaximizeOnArrival;
                _settings.Save();
                break;
            case CmdTrayTaskbar:
                _settings.TaskbarMenu = !_settings.TaskbarMenu;
                _settings.Save();
                MouseHook.TaskbarMenuEnabled = _settings.TaskbarMenu;
                break;
            case CmdTrayLabels:
                _settings.ShowMonitorLabels = !_settings.ShowMonitorLabels;
                _settings.Save();
                break;
            case CmdTrayStartup:
                StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled);
                break;
            case CmdTrayPause:
                SetPaused(!_paused);
                break;
            case CmdTrayExit:
                ExitThread();
                break;
            case >= CmdTrayNameBase when cmd - CmdTrayNameBase < monitors.Count:
                NameMonitor(monitors[cmd - CmdTrayNameBase]);
                break;
        }
    }

    /// <summary>Asks for a name for <paramref name="monitor"/>; it shows up in the title bar and taskbar menus.</summary>
    private void NameMonitor(MonitorDesc monitor)
    {
        using var dialog = new MonitorNameDialog(monitor);
        _nameDialog = dialog;
        // Leave right-clicks alone while the dialog is up, rather than opening menus behind it.
        MouseHook.MenuActive = true;
        DialogResult result;
        try
        {
            result = dialog.ShowDialog();
        }
        finally
        {
            MouseHook.MenuActive = false;
            _nameDialog = null;
        }
        if (result != DialogResult.OK) return;

        string? name = dialog.MonitorName;
        if (name == null || name == monitor.FriendlyName) _settings.MonitorNames.Remove(monitor.Key);
        else _settings.MonitorNames[monitor.Key] = name;
        _settings.Save();
    }

    private void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused)
        {
            MouseHook.Uninstall();
        }
        else
        {
            try { MouseHook.Install(_owner.Handle); }
            catch (Exception ex) { Log.Error("Installing mouse hook", ex); }
        }
        _tray.Icon = paused ? _pausedIcon : _icon;
        _tray.Text = paused ? "SendToMonitor (paused)" : "SendToMonitor";
    }

    // ------------------------------------------------------------------ menu plumbing

    private int TrackMenu(IntPtr menu, POINT pt, bool useTrackPopupMenuEx, uint extraFlags = 0, RECT? exclude = null,
        IReadOnlyList<MonitorDesc>? labels = null)
    {
        // Monitor labels go up first so the menu, shown after them, stays on top; they come down with it.
        using MonitorLabels? shownLabels = labels != null && _settings.ShowMonitorLabels ? MonitorLabels.Show(labels) : null;

        // The owner must be foreground or the menu won't close when the user clicks elsewhere.
        // The click that opened the menu was swallowed, so first give this process the right to
        // take the foreground with an empty injected mouse event.
        var inputs = new[] { new INPUT { type = Native.INPUT_MOUSE } };
        Native.SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        Native.SetForegroundWindow(_owner.Handle);

        MouseHook.MenuActive = true;
        try
        {
            uint flags = Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_NONOTIFY | extraFlags;
            if (useTrackPopupMenuEx && exclude is RECT rect)
            {
                var tpm = new TPMPARAMS { cbSize = Marshal.SizeOf<TPMPARAMS>(), rcExclude = rect };
                return Native.TrackPopupMenuEx(menu, flags, pt.X, pt.Y, _owner.Handle, ref tpm);
            }
            return useTrackPopupMenuEx
                ? Native.TrackPopupMenuEx(menu, flags, pt.X, pt.Y, _owner.Handle, IntPtr.Zero)
                : Native.TrackPopupMenu(menu, flags, pt.X, pt.Y, 0, _owner.Handle, IntPtr.Zero);
        }
        finally
        {
            MouseHook.MenuActive = false;
            Native.PostMessageW(_owner.Handle, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static void Append(IntPtr menu, int id, string text, bool isChecked = false, bool enabled = true) =>
        Native.AppendMenuW(menu,
            Native.MF_STRING | (isChecked ? Native.MF_CHECKED : 0) | (enabled ? 0 : Native.MF_GRAYED),
            (UIntPtr)(uint)id, text);

    private static void AppendSeparator(IntPtr menu) =>
        Native.AppendMenuW(menu, Native.MF_SEPARATOR, UIntPtr.Zero, null);

    // ------------------------------------------------------------------ hook watchdog

    private void CheckHook()
    {
        if (_paused || MouseHook.MenuActive) return;
        if (!Native.GetCursorPos(out POINT p)) return; // e.g. secure desktop is up

        bool cursorMoved = p.X != _lastCursor.X || p.Y != _lastCursor.Y;
        if (cursorMoved && MouseHook.LastCallbackTick == _lastSeenTick)
        {
            try
            {
                MouseHook.Uninstall();
                MouseHook.Install(_owner.Handle);
                Log.Write("Mouse hook stopped receiving events; reinstalled it.");
            }
            catch (Exception ex)
            {
                Log.Error("Reinstalling mouse hook", ex);
            }
        }
        _lastCursor = p;
        _lastSeenTick = MouseHook.LastCallbackTick;
    }

    // ------------------------------------------------------------------ icons / shutdown

    private static (Icon normal, Icon paused, IntPtr pausedHandle) LoadIcons()
    {
        IntPtr primary = Native.MonitorFromWindow(IntPtr.Zero, 1 /* MONITOR_DEFAULTTOPRIMARY */);
        int size = (int)Math.Round(16 * Native.GetMonitorDpi(primary) / 96.0);

        using Stream stream = typeof(TrayApp).Assembly.GetManifestResourceStream("SendToMonitor.app.ico")!;
        var normal = new Icon(stream, size, size);

        using Bitmap src = normal.ToBitmap();
        using var gray = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(gray))
        using (var attributes = new ImageAttributes())
        {
            attributes.SetColorMatrix(new ColorMatrix(new[]
            {
                new[] { 0.30f, 0.30f, 0.30f, 0f, 0f },
                new[] { 0.59f, 0.59f, 0.59f, 0f, 0f },
                new[] { 0.11f, 0.11f, 0.11f, 0f, 0f },
                new[] { 0f, 0f, 0f, 0.55f, 0f },
                new[] { 0f, 0f, 0f, 0f, 1f },
            }));
            g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attributes);
        }
        IntPtr handle = gray.GetHicon();
        return (normal, Icon.FromHandle(handle), handle);
    }

    protected override void ExitThreadCore()
    {
        MouseHook.Uninstall();
        _watchdog.Stop();
        _watchdog.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _owner.DestroyHandle();
        _icon.Dispose();
        _pausedIcon.Dispose();
        DestroyIcon(_pausedIconHandle);
        base.ExitThreadCore();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string className, string? windowName);

    // ------------------------------------------------------------------ hidden owner window

    /// <summary>
    /// Invisible top-level window: receives the hook's "show menu" message and owns the popups.
    /// (Not message-only: a message-only window can't become foreground, which popups need.)
    /// </summary>
    private sealed class OwnerWindow : NativeWindow
    {
        private const int WM_SETTINGCHANGE = 0x001A;
        private readonly TrayApp _app;

        public OwnerWindow(TrayApp app)
        {
            _app = app;
            CreateHandle(new CreateParams
            {
                Caption = "SendToMonitor",
                Style = unchecked((int)Native.WS_POPUP),
                ExStyle = (int)Native.WS_EX_TOOLWINDOW,
            });
            DarkMenus.AllowForWindow(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg is MouseHook.WM_SHOW_MONITOR_MENU or MouseHook.WM_SHOW_TASKBAR_MENU)
            {
                long lp = m.LParam.ToInt64();
                var pt = new POINT((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                try
                {
                    if (m.Msg == MouseHook.WM_SHOW_MONITOR_MENU) _app.ShowMonitorMenu(m.WParam, pt);
                    else _app.ShowTaskbarMenu(m.WParam, pt);
                }
                catch (Exception ex)
                {
                    Log.Error("Showing menu", ex);
                }
                return;
            }

            if (m.Msg == WM_SETTINGCHANGE && m.LParam != IntPtr.Zero &&
                Marshal.PtrToStringUni(m.LParam) == "ImmersiveColorSet")
            {
                DarkMenus.Refresh();
            }

            base.WndProc(ref m);
        }
    }
}

/// <summary>
/// Lets Win32 popup menus follow the system light/dark app theme. Uses long-stable but
/// undocumented uxtheme exports; any failure just leaves menus light.
/// </summary>
internal static class DarkMenus
{
    [DllImport("uxtheme.dll", EntryPoint = "#133")] private static extern bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);
    [DllImport("uxtheme.dll", EntryPoint = "#104")] private static extern void RefreshImmersiveColorPolicyState();

    private static bool Supported => Environment.OSVersion.Version.Build >= 18362;

    public static void Enable()
    {
        if (!Supported) return;
        try
        {
            Native.SetPreferredAppMode(1 /* AllowDark: follow the system setting */);
            Native.FlushMenuThemes();
        }
        catch { }
    }

    public static void AllowForWindow(IntPtr hwnd)
    {
        if (!Supported) return;
        try { AllowDarkModeForWindow(hwnd, true); } catch { }
    }

    public static void Refresh()
    {
        if (!Supported) return;
        try
        {
            RefreshImmersiveColorPolicyState();
            Native.FlushMenuThemes();
        }
        catch { }
    }
}
