using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace SendToMonitor;

/// <summary>An app window offered in the taskbar menu. <see cref="Blocked"/> says why it can't be moved, if it can't.</summary>
internal sealed record WindowEntry(IntPtr Handle, string Title, IntPtr Monitor, string? Blocked);

/// <summary>Enumerates the app windows a user would see in Alt+Tab, and their icons.</summary>
internal static class WindowList
{
    /// <summary>Visible, uncloaked app windows on the current virtual desktop, front to back.</summary>
    public static List<WindowEntry> GetAppWindows()
    {
        var list = new List<WindowEntry>();
        uint ownPid = (uint)Environment.ProcessId;
        EnumWindowsProc callback = (hwnd, _) =>
        {
            try
            {
                if (Describe(hwnd, ownPid) is { } entry) list.Add(entry);
            }
            catch
            {
                // A window that vanishes mid-enumeration just isn't listed.
            }
            return true;
        };
        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return list;
    }

    private static WindowEntry? Describe(IntPtr hwnd, uint ownPid)
    {
        // Cloaked = on another virtual desktop, a suspended UWP frame, or a hidden shell surface.
        if (!IsWindowVisible(hwnd) || IsCloaked(hwnd)) return null;

        long exStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        if ((exStyle & WS_EX_APPWINDOW) == 0)
        {
            if ((exStyle & (Native.WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return null;
            // Dialogs and other owned popups travel with their owner; list only the owner.
            IntPtr owner = GetWindow(hwnd, GW_OWNER);
            if (owner != IntPtr.Zero && IsWindowVisible(owner)) return null;
        }

        uint pid = Native.GetProcessId(hwnd);
        if (pid == 0 || pid == ownPid || IsShellClass(Native.GetClassName(hwnd))) return null;

        string title = GetTitle(hwnd);
        if (title.Length == 0) return null;

        if (!Native.IsIconic(hwnd))
        {
            RECT bounds = Native.GetVisibleBounds(hwnd);
            if (bounds.Width < 2 || bounds.Height < 2) return null;
        }

        string? blocked =
            ProcessIntegrity.ForPid(pid) > ProcessIntegrity.Own ? "administrator" :
            IsHungAppWindow(hwnd) ? "not responding" : null;

        // For a minimized window this is the monitor it will restore onto.
        IntPtr monitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        return new WindowEntry(hwnd, title, monitor, blocked);
    }

    private static bool IsShellClass(string className) => className switch
    {
        "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" => true,
        "Windows.UI.Core.CoreWindow" => true,          // Start, Search and other shell surfaces
        "XamlExplorerHostIslandWindow" => true,        // Alt+Tab, Snap layouts and similar overlays
        "TopLevelWindowForOverflowXamlIsland" or "NotifyIconOverflowWindow" => true,
        _ => false,
    };

    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static string GetTitle(IntPtr hwnd)
    {
        // For another process's window this reads the stored caption; it never sends WM_GETTEXT, so it can't hang.
        int length = GetWindowTextLengthW(hwnd);
        if (length <= 0) return string.Empty;
        var sb = new StringBuilder(length + 1);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString().Trim();
    }

    // ------------------------------------------------------------------ icons

    /// <summary>
    /// The window's icon as a <paramref name="size"/>-pixel, 32bpp premultiplied-alpha DIB, which menus
    /// draw with transparency. Returns IntPtr.Zero if there's no icon. Caller deletes it with DeleteObject.
    /// </summary>
    public static IntPtr CreateMenuBitmap(IntPtr hwnd, int size)
    {
        IntPtr icon = GetWindowIcon(hwnd, out bool owned);
        if (icon == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            return IconToBitmap(icon, size);
        }
        catch
        {
            return IntPtr.Zero;
        }
        finally
        {
            if (owned) DestroyIcon(icon);
        }
    }

    private static IntPtr GetWindowIcon(IntPtr hwnd, out bool owned)
    {
        owned = false;
        if (!IsHungAppWindow(hwnd))
        {
            foreach (int type in new[] { ICON_SMALL2, ICON_SMALL, ICON_BIG })
            {
                if (Native.SendMessageTimeoutW(hwnd, WM_GETICON, (IntPtr)type, IntPtr.Zero,
                        Native.SMTO_ABORTIFHUNG, 50, out IntPtr icon) != IntPtr.Zero && icon != IntPtr.Zero)
                    return icon;
            }
        }

        IntPtr classIcon = GetClassLongPtrW(hwnd, GCLP_HICONSM);
        if (classIcon == IntPtr.Zero) classIcon = GetClassLongPtrW(hwnd, GCLP_HICON);
        if (classIcon != IntPtr.Zero) return classIcon;

        // Fall back to the exe's own icon. For UWP frames that's the app hosted inside the frame.
        uint pid = Native.GetProcessId(hwnd);
        IntPtr core = FindWindowExW(hwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
        if (core != IntPtr.Zero) pid = Native.GetProcessId(core);
        string? exe = GetProcessPath(pid);
        if (exe != null && ExtractIconExW(exe, 0, IntPtr.Zero, out IntPtr small, 1) > 0 && small != IntPtr.Zero)
        {
            owned = true;
            return small;
        }
        return IntPtr.Zero;
    }

    private static unsafe IntPtr IconToBitmap(IntPtr hIcon, int size)
    {
        using Icon icon = Icon.FromHandle(hIcon); // does not take ownership of the handle
        using Bitmap source = icon.ToBitmap();    // keeps the icon's alpha channel (or builds one from its mask)
        using var scaled = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, size, size));
        }

        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size, // top-down, same row order as the GDI+ bitmap
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr dib = CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) return IntPtr.Zero;

        BitmapData data = scaled.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            int rowBytes = size * 4;
            for (int y = 0; y < size; y++)
                Buffer.MemoryCopy((byte*)data.Scan0 + y * data.Stride, (byte*)bits + y * rowBytes, rowBytes, rowBytes);
        }
        finally
        {
            scaled.UnlockBits(data);
        }
        return dib;
    }

    private static string? GetProcessPath(uint pid)
    {
        IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(process, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    // ------------------------------------------------------------------ native

    private const long WS_EX_APPWINDOW = 0x00040000L, WS_EX_NOACTIVATE = 0x08000000L;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;
    private const int WM_GETICON = 0x007F, ICON_SMALL = 0, ICON_BIG = 1, ICON_SMALL2 = 2;
    private const int GCLP_HICON = -14, GCLP_HICONSM = -34;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern IntPtr GetClassLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string className, string? windowName);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, IntPtr large, out IntPtr small, uint count);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);
}
