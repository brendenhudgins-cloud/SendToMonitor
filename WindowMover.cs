namespace SendToMonitor;

/// <summary>
/// Moves a top-level window to another monitor. All coordinates are physical pixels
/// (the process is Per-Monitor V2 aware).
/// </summary>
internal static class WindowMover
{
    private const uint MoveFlags = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;

    public static bool Move(IntPtr hwnd, MonitorDesc target, bool maximize)
    {
        if (!Native.IsWindow(hwnd)) return false;
        if (!Native.Sync(hwnd, 300)) return false; // hung, or blocked by UIPI

        long style = Native.GetWindowLong(hwnd, Native.GWL_STYLE);
        long exStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        bool resizable = (style & Native.WS_THICKFRAME) != 0;
        bool canMaximize = (style & Native.WS_MAXIMIZEBOX) != 0 && resizable;
        bool wasMaximized = Native.IsZoomed(hwnd);
        bool wasMinimized = Native.IsIconic(hwnd);
        bool toolWindow = (exStyle & Native.WS_EX_TOOLWINDOW) != 0;

        WINDOWPLACEMENT wp = Native.GetPlacement(hwnd);
        RECT insets = GetInsets(hwnd);

        // 1. The window's normal-state visible rect, and the monitor that rect lives on.
        RECT sourceVisible;
        if (wasMaximized || wasMinimized)
        {
            RECT normal = toolWindow ? wp.rcNormalPosition : WorkspaceToScreen(wp.rcNormalPosition);
            sourceVisible = Deflate(normal, insets);
        }
        else
        {
            sourceVisible = Native.GetVisibleBounds(hwnd);
        }
        IntPtr sourceMonitor = wasMaximized || wasMinimized
            ? Native.MonitorFromRect(ref sourceVisible, Native.MONITOR_DEFAULTTONEAREST)
            : Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        if (!Native.TryGetMonitorInfo(sourceMonitor, out var sourceInfo)) return false;
        uint sourceDpi = Native.GetMonitorDpi(sourceMonitor);

        // 2. Where it should end up (visible bounds) in the target work area.
        RECT finalVisible = MapRect(sourceVisible, sourceInfo.rcWork, sourceDpi, target.WorkArea, target.Dpi, resizable);

        // 3. Step one: get the window, in normal state, inside the target monitor.
        //    A per-monitor-aware app rescales itself by targetDpi/sourceDpi on WM_DPICHANGED,
        //    so stage it at the pre-scaled size; step two then sets the exact final rect.
        double ratio = (double)target.Dpi / sourceDpi;
        int stageW = Math.Clamp((int)Math.Round(finalVisible.Width / ratio), 1, target.WorkArea.Width);
        int stageH = Math.Clamp((int)Math.Round(finalVisible.Height / ratio), 1, target.WorkArea.Height);
        int stageX = Math.Clamp(finalVisible.Left, target.WorkArea.Left, target.WorkArea.Right - stageW);
        int stageY = Math.Clamp(finalVisible.Top, target.WorkArea.Top, target.WorkArea.Bottom - stageH);
        RECT stageWindow = Inflate(new RECT(stageX, stageY, stageX + stageW, stageY + stageH), insets);

        if (wasMaximized || wasMinimized)
        {
            // Restore straight onto the target monitor (no flash on the source monitor).
            wp.length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>();
            wp.flags = 0;
            wp.showCmd = Native.SW_SHOWNORMAL;
            wp.rcNormalPosition = toolWindow ? stageWindow : ScreenToWorkspace(stageWindow, target);
            if (!Native.SetWindowPlacement(hwnd, ref wp)) return false;
        }
        else
        {
            if (!Native.SetWindowPos(hwnd, IntPtr.Zero, stageWindow.Left, stageWindow.Top,
                    stageWindow.Width, stageWindow.Height, MoveFlags | (resizable ? 0 : Native.SWP_NOSIZE)))
                return false;
        }
        Native.Sync(hwnd);

        // 4. Step two: exact size/position at the target DPI (and maximize if requested).
        ApplyFinalRect(hwnd, finalVisible, target.WorkArea, resizable);

        if (maximize && canMaximize)
        {
            Native.ShowWindow(hwnd, Native.SW_MAXIMIZE);
            Native.Sync(hwnd);

            // If an app's DPI handling pushed it onto a neighbour, retry once from scratch.
            if (Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST) != target.Handle)
            {
                Native.ShowWindow(hwnd, Native.SW_RESTORE);
                Native.Sync(hwnd);
                ApplyFinalRect(hwnd, finalVisible, target.WorkArea, resizable);
                Native.ShowWindow(hwnd, Native.SW_MAXIMIZE);
                Native.Sync(hwnd);
            }
        }
        return true;
    }

    /// <summary>
    /// Sets the window's visible bounds to <paramref name="visible"/>, re-measuring the invisible
    /// borders each time (they depend on DPI). Retries because some apps adjust after a move.
    /// If the app enforces its own size, keeps that size and just makes it fully visible.
    /// </summary>
    private static void ApplyFinalRect(IntPtr hwnd, RECT visible, RECT work, bool resizable)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            RECT current = Native.GetVisibleBounds(hwnd);
            bool sizeOk = !resizable || (Near(current.Width, visible.Width) && Near(current.Height, visible.Height));

            if (!sizeOk && attempt > 0)
            {
                // The app refused our size (min/max size, fixed aspect...). Accept it.
                resizable = false;
                sizeOk = true;
            }
            if (!resizable)
            {
                // Keep whatever size it has now; place it at the same anchor, fully visible.
                int w = Math.Min(current.Width, work.Width), h = Math.Min(current.Height, work.Height);
                int x = Math.Clamp(visible.Left, work.Left, work.Right - w);
                int y = Math.Clamp(visible.Top, work.Top, work.Bottom - h);
                visible = new RECT(x, y, x + current.Width, y + current.Height);
            }
            if (sizeOk && Near(current.Left, visible.Left) && Near(current.Top, visible.Top)) return;

            RECT window = Inflate(visible, GetInsets(hwnd));
            Native.SetWindowPos(hwnd, IntPtr.Zero, window.Left, window.Top, window.Width, window.Height,
                MoveFlags | (resizable ? 0 : Native.SWP_NOSIZE));
            Native.Sync(hwnd);
        }
    }

    /// <summary>
    /// Keeps the window's logical (DPI-independent) size, shrinks it proportionally if it doesn't
    /// fit, and keeps its relative position: e.g. flush-right stays flush-right, centered stays centered.
    /// </summary>
    internal static RECT MapRect(RECT src, RECT srcWork, uint srcDpi, RECT dstWork, uint dstDpi, bool resizable)
    {
        double scale = (double)dstDpi / srcDpi;
        double w = src.Width * scale, h = src.Height * scale;
        if (resizable && (w > dstWork.Width || h > dstWork.Height))
        {
            double fit = Math.Min(dstWork.Width / w, dstWork.Height / h);
            w *= fit;
            h *= fit;
        }
        int width = Math.Clamp((int)Math.Round(w), 1, Math.Max(1, dstWork.Width));
        int height = Math.Clamp((int)Math.Round(h), 1, Math.Max(1, dstWork.Height));

        double fx = Fraction(src.Left - srcWork.Left, srcWork.Width - src.Width);
        double fy = Fraction(src.Top - srcWork.Top, srcWork.Height - src.Height);
        int left = dstWork.Left + (int)Math.Round(fx * (dstWork.Width - width));
        int top = dstWork.Top + (int)Math.Round(fy * (dstWork.Height - height));
        return new RECT(left, top, left + width, top + height);
    }

    /// <summary>Where the window sits within the free space of its work area, clamped to 0..1.</summary>
    private static double Fraction(int offset, int freeSpace) =>
        freeSpace <= 0 ? 0 : Math.Clamp((double)offset / freeSpace, 0, 1);

    /// <summary>Invisible border thickness on each side: window rect minus DWM visible bounds.</summary>
    private static RECT GetInsets(IntPtr hwnd)
    {
        Native.GetWindowRect(hwnd, out RECT window);
        RECT visible = Native.GetVisibleBounds(hwnd);
        return new RECT(
            Math.Max(0, visible.Left - window.Left),
            Math.Max(0, visible.Top - window.Top),
            Math.Max(0, window.Right - visible.Right),
            Math.Max(0, window.Bottom - visible.Bottom));
    }

    private static RECT Inflate(RECT r, RECT insets) =>
        new(r.Left - insets.Left, r.Top - insets.Top, r.Right + insets.Right, r.Bottom + insets.Bottom);

    private static RECT Deflate(RECT r, RECT insets) =>
        new(r.Left + insets.Left, r.Top + insets.Top, r.Right - insets.Right, r.Bottom - insets.Bottom);

    private static bool Near(int a, int b) => Math.Abs(a - b) <= 1;

    // WINDOWPLACEMENT.rcNormalPosition uses workspace coordinates: relative to the monitor's
    // work area rather than its bounds. They differ when a taskbar is docked top or left.

    private static RECT WorkspaceToScreen(RECT workspace)
    {
        IntPtr monitor = Native.MonitorFromRect(ref workspace, Native.MONITOR_DEFAULTTONEAREST);
        if (!Native.TryGetMonitorInfo(monitor, out var mi)) return workspace;
        return workspace.Offset(mi.rcWork.Left - mi.rcMonitor.Left, mi.rcWork.Top - mi.rcMonitor.Top);
    }

    private static RECT ScreenToWorkspace(RECT screen, MonitorDesc monitor) =>
        screen.Offset(-(monitor.WorkArea.Left - monitor.Bounds.Left), -(monitor.WorkArea.Top - monitor.Bounds.Top));
}
