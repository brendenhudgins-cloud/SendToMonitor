using System.Runtime.InteropServices;

namespace SendToMonitor;

/// <summary>
/// Answers "is this point on empty taskbar space?" for the mouse hook.
///
/// The Windows 11 taskbar is one XAML surface, so window hit-testing can't tell an app button
/// from the gap next to it; UI Automation can (buttons, tray icons and the clock are Button
/// elements, the empty space is the TaskbarFrame pane). UIA makes cross-process COM calls, which
/// must not run on the hook's thread (COM would pump messages inside the hook), so a dedicated
/// MTA thread does the lookup while the hook waits on a kernel event with a hard timeout.
/// A lookup takes 2–5 ms; on a timeout or any failure the click is passed through untouched.
///
/// Note: don't use .NET's managed System.Windows.Automation anywhere in this process. It installs
/// in-process proxies that stop ElementFromPoint at the taskbar's XAML host pane, which would make
/// every point look like empty space.
/// </summary>
internal static class TaskbarProbe
{
    private const uint UiaTimeoutMs = 400;

    private const int ResultUnknown = 0, ResultEmpty = 1, ResultOccupied = 2;

    private static readonly AutoResetEvent s_request = new(false);
    private static readonly ManualResetEvent s_done = new(false);
    private static POINT s_point;
    private static int s_seq;      // request number; written only by the hook thread
    private static long s_answer;  // (request number << 2) | result, so a late answer can't be mistaken for a newer one
    private static volatile bool s_busy;
    private static volatile bool s_ready;
    private static Thread? s_thread;

    /// <summary>Starts the worker and warms UIA up at <paramref name="warmUpPoint"/> so the first real lookup is fast.</summary>
    public static void Start(POINT warmUpPoint)
    {
        if (s_thread != null) return;
        s_thread = new Thread(() => Worker(warmUpPoint)) { IsBackground = true, Name = "Taskbar probe" };
        s_thread.SetApartmentState(ApartmentState.MTA);
        s_thread.Start();
    }

    /// <summary>
    /// Called from the hook. true = empty taskbar space, false = a button/icon/clock,
    /// null = don't know (UIA unavailable, busy, or too slow): treat as "leave the click alone".
    /// </summary>
    public static bool? IsEmptyArea(POINT pt, uint timeoutMs)
    {
        if (!s_ready || s_busy) return null;

        s_busy = true;
        int seq = Volatile.Read(ref s_seq) + 1;
        s_point = pt;
        Volatile.Write(ref s_seq, seq);
        s_done.Reset();
        s_request.Set();

        // A plain kernel wait: unlike WaitHandle.WaitOne on an STA thread, it never pumps messages,
        // so nothing can re-enter the hook or dispatch our own posted messages while we wait.
        if (WaitForSingleObject(s_done.SafeWaitHandle.DangerousGetHandle(), timeoutMs) != WAIT_OBJECT_0)
            return null; // too slow: the worker finishes in the background and the click passes through

        // After a timeout, the previous request's completion can wake us; only trust our own answer.
        long answer = Volatile.Read(ref s_answer);
        if ((int)(answer >> 2) != seq) return null;
        return (int)(answer & 3) switch
        {
            ResultEmpty => true,
            ResultOccupied => false,
            _ => null,
        };
    }

    /// <summary>The rule, kept separate so it can be checked against a live taskbar.</summary>
    public static bool IsEmptySpace(UiaElementInfo hit) =>
        // TaskbarFrame / TaskbarFrameRepeater / the XAML host panes between tray icons.
        // Start, Search, Task View, app buttons, tray icons, the clock and Show Desktop are all
        // Buttons (or an Image/Text inside one), so they keep their own menus.
        hit.ControlType is Uia.Pane or Uia.Group;

    private static void Worker(POINT warmUpPoint)
    {
        Uia? uia;
        try
        {
            uia = Uia.TryCreate(UiaTimeoutMs);
            if (uia == null)
            {
                Log.Write("UI Automation is unavailable; the taskbar menu is disabled.");
                return;
            }
            uia.ElementsAt(warmUpPoint);
        }
        catch (Exception ex)
        {
            Log.Error("Starting UI Automation", ex);
            return;
        }

        s_ready = true;
        while (true)
        {
            s_request.WaitOne();
            int seq = Volatile.Read(ref s_seq);
            POINT pt = s_point;
            int result = ResultUnknown;
            try
            {
                List<UiaElementInfo> hit = uia.ElementsAt(pt);
                if (hit.Count > 0) result = IsEmptySpace(hit[0]) ? ResultEmpty : ResultOccupied;
            }
            catch
            {
                // Explorer restarting, UIA timeout, etc.: "unknown" passes the click through.
            }
            Volatile.Write(ref s_answer, ((long)seq << 2) | (uint)result);
            s_busy = false; // before Set, so a hook call right after this answer isn't turned away as "busy"
            s_done.Set();
        }
    }

    private const uint WAIT_OBJECT_0 = 0;

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
