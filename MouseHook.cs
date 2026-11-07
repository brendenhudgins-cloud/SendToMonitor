using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SendToMonitor;

/// <summary>
/// WH_MOUSE_LL hook, installed on (and called back on) the UI thread.
/// Swallows right-button-down on a title bar (or on empty taskbar space) plus its matching
/// button-up, and posts a message to the owner window, which shows the menu outside the hook callback.
/// </summary>
internal static class MouseHook
{
    /// <summary>Posted to the owner window. wParam = root HWND, lParam = MAKELPARAM(x, y).</summary>
    public const int WM_SHOW_MONITOR_MENU = Native.WM_APP + 1;

    /// <summary>Posted to the owner window. wParam = taskbar HWND, lParam = MAKELPARAM(x, y).</summary>
    public const int WM_SHOW_TASKBAR_MENU = Native.WM_APP + 2;

    /// <summary>Longest the hook waits for the taskbar UI Automation lookup (normally ~5 ms).</summary>
    private const uint TaskbarProbeTimeoutMs = 150;

    // Static so the GC can never collect the delegate while native code holds it.
    private static readonly Native.LowLevelMouseProc s_proc = HookCallback;

    private static IntPtr s_hook;
    private static IntPtr s_owner;
    private static bool s_swallowNextUp;

    /// <summary>True while one of our own popup menus is open: let all clicks through.</summary>
    public static bool MenuActive { get; set; }

    /// <summary>Whether right-clicking empty taskbar space shows the "bring a window here" menu.</summary>
    public static bool TaskbarMenuEnabled { get; set; } = true;

    /// <summary>Environment.TickCount of the last callback (used by the watchdog).</summary>
    public static int LastCallbackTick { get; private set; }

    public static bool IsInstalled => s_hook != IntPtr.Zero;

    public static void Install(IntPtr ownerWindow)
    {
        if (s_hook != IntPtr.Zero) return;
        s_owner = ownerWindow;
        s_swallowNextUp = false;
        s_hook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, s_proc, Native.GetModuleHandleW(null), 0);
        if (s_hook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(WH_MOUSE_LL) failed");
        LastCallbackTick = Environment.TickCount;
    }

    public static void Uninstall()
    {
        if (s_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(s_hook);
        s_hook = IntPtr.Zero;
        s_swallowNextUp = false;
    }

    private static unsafe IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode == Native.HC_ACTION)
            {
                LastCallbackTick = Environment.TickCount;
                int msg = (int)wParam;

                if (msg == Native.WM_RBUTTONUP)
                {
                    // Always pair a swallowed down with its up, or the native menu appears on button-up.
                    if (s_swallowNextUp)
                    {
                        s_swallowNextUp = false;
                        return (IntPtr)1;
                    }
                }
                else if (msg == Native.WM_RBUTTONDOWN)
                {
                    s_swallowNextUp = false; // a new down means any earlier up is long gone
                    POINT pt = ((MSLLHOOKSTRUCT*)lParam)->pt;
                    if (ShouldIntercept(pt, out int message, out IntPtr hwnd) &&
                        Native.PostMessageW(s_owner, message, hwnd, Native.MakeLParam(pt.X, pt.Y)))
                    {
                        s_swallowNextUp = true;
                        return (IntPtr)1;
                    }
                }
            }
        }
        catch
        {
            // Never let an exception escape into native code; just pass the event on.
        }
        return Native.CallNextHookEx(s_hook, nCode, wParam, lParam);
    }

    private static bool ShouldIntercept(POINT pt, out int message, out IntPtr hwnd)
    {
        message = 0;
        hwnd = IntPtr.Zero;
        if (MenuActive) return false;
        if ((Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0) return false; // Shift: native menu
        if (Native.GetSystemMetrics(Native.SM_CMONITORS) < 2) return false;      // nowhere to send it

        IntPtr taskbar = FindTaskbar(pt);
        if (taskbar != IntPtr.Zero)
        {
            // Only empty space: app buttons keep their jump lists, the tray and clock their menus.
            if (!TaskbarMenuEnabled || TaskbarProbe.IsEmptyArea(pt, TaskbarProbeTimeoutMs) != true) return false;
            message = WM_SHOW_TASKBAR_MENU;
            hwnd = taskbar;
            return true;
        }

        hwnd = TitleBarDetector.FindTitleBarWindow(pt);
        message = WM_SHOW_MONITOR_MENU;
        return hwnd != IntPtr.Zero;
    }

    /// <summary>The taskbar window under <paramref name="pt"/>, if any.</summary>
    private static IntPtr FindTaskbar(POINT pt)
    {
        IntPtr hwnd = Native.WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        IntPtr root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;
        return Native.GetClassName(root) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" ? root : IntPtr.Zero;
    }
}
