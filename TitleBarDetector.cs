using System.Runtime.InteropServices;

namespace SendToMonitor;

/// <summary>
/// Decides whether a screen point is on a top-level window's title bar. Runs inside the
/// low-level mouse hook, so everything here must be fast and must never block for long.
/// </summary>
internal static class TitleBarDetector
{
    private const uint HitTestTimeoutMs = 50;

    private static readonly uint s_ownPid = (uint)Environment.ProcessId;

    /// <summary>Returns the root window whose title bar is at <paramref name="pt"/>, or IntPtr.Zero.</summary>
    public static IntPtr FindTitleBarWindow(POINT pt)
    {
        IntPtr hwnd = Native.WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;

        IntPtr root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;

        uint pid = Native.GetProcessId(root);
        if (pid == 0 || pid == s_ownPid || IsShellWindow(root)) return IntPtr.Zero;

        IntPtr lParam = Native.MakeLParam(pt.X, pt.Y);

        int hit = HitTest(hwnd, lParam);
        bool caption = false;

        if (hit == Native.HTCAPTION)
        {
            // A child with its own caption (e.g. an MDI child) owns that caption: leave it alone.
            caption = hwnd == root || !HasOwnCaption(hwnd);
        }
        else if (root != hwnd && (hit == Native.HTTRANSPARENT || hit == Native.HTNOWHERE || hit == Native.HTERROR))
        {
            // The child under the cursor doesn't claim the point (e.g. Chromium/Electron helper
            // windows, UWP frames), so ask the root. A child that answers HTCLIENT (browser or
            // terminal tabs, page content) keeps the click, even if the root would say HTCAPTION.
            caption = HitTest(root, lParam) == Native.HTCAPTION;
        }

        if (!caption) return IntPtr.Zero;

        // UIPI: an unelevated process can't move elevated windows. Pass the click through.
        if (ProcessIntegrity.ForPid(pid) > ProcessIntegrity.Own) return IntPtr.Zero;

        return root;
    }

    /// <summary>WM_NCHITTEST with a short timeout; failures/timeouts report HTERROR.</summary>
    private static int HitTest(IntPtr hwnd, IntPtr lParam)
    {
        IntPtr ok = Native.SendMessageTimeoutW(hwnd, Native.WM_NCHITTEST, IntPtr.Zero, lParam,
            Native.SMTO_ABORTIFHUNG | Native.SMTO_NORMAL, HitTestTimeoutMs, out IntPtr result);
        return ok == IntPtr.Zero ? Native.HTERROR : (int)result.ToInt64();
    }

    private static bool HasOwnCaption(IntPtr hwnd)
    {
        long style = Native.GetWindowLong(hwnd, Native.GWL_STYLE);
        return (style & Native.WS_CHILD) != 0 && (style & Native.WS_CAPTION) == Native.WS_CAPTION;
    }

    private static bool IsShellWindow(IntPtr root)
    {
        switch (Native.GetClassName(root))
        {
            case "Progman":                     // desktop
            case "WorkerW":                     // desktop (wallpaper host)
            case "Shell_TrayWnd":               // primary taskbar
            case "Shell_SecondaryTrayWnd":      // taskbars on other monitors
            case "NotifyIconOverflowWindow":    // tray overflow
            case "#32768":                      // popup menus
                return true;
            default:
                return false;
        }
    }
}

/// <summary>Mandatory integrity level (RID) of a process: 0x2000 medium, 0x3000 high, 0x4000 system.</summary>
internal static class ProcessIntegrity
{
    private const int Unknown = int.MaxValue; // treat "can't tell" as "don't touch"

    /// <summary>This process's own level.</summary>
    public static readonly int Own = Get(Native.GetCurrentProcess(), ownsHandle: false);

    public static int ForPid(uint pid)
    {
        IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return Unknown;
        return Get(process, ownsHandle: true);
    }

    public static int Get(IntPtr process, bool ownsHandle)
    {
        IntPtr token = IntPtr.Zero, buffer = IntPtr.Zero;
        try
        {
            if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, out token)) return Unknown;
            Native.GetTokenInformation(token, Native.TokenIntegrityLevel, IntPtr.Zero, 0, out int size);
            if (size <= 0) return Unknown;
            buffer = Marshal.AllocHGlobal(size);
            if (!Native.GetTokenInformation(token, Native.TokenIntegrityLevel, buffer, size, out _)) return Unknown;

            // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label { PSID Sid; DWORD Attributes } }
            IntPtr sid = Marshal.ReadIntPtr(buffer);
            int count = Marshal.ReadByte(Native.GetSidSubAuthorityCount(sid));
            if (count == 0) return Unknown;
            return Marshal.ReadInt32(Native.GetSidSubAuthority(sid, (uint)(count - 1)));
        }
        catch
        {
            return Unknown;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) Native.CloseHandle(token);
            if (ownsHandle) Native.CloseHandle(process);
        }
    }
}
