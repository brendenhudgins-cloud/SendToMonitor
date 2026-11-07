namespace SendToMonitor;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One instance per user session.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\SendToMonitor.{6F1C2B8E-3D4A-4E7B-9A51-2C8D0F7E4B19}", out bool isFirst);
        if (!isFirst) return;

        // Per-Monitor V2 is already set by app.manifest; this is a no-op safety net.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // A background utility should never show a crash dialog; log instead.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Error("Unhandled exception", ex);
        };

        DarkMenus.Enable();

        try
        {
            Application.Run(new TrayApp());
        }
        catch (Exception ex)
        {
            Log.Error("Startup", ex);
            MessageBox.Show($"SendToMonitor could not start:\n\n{ex.Message}", "SendToMonitor",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
