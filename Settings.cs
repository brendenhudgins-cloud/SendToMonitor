using System.Text.Json;
using Microsoft.Win32;

namespace SendToMonitor;

/// <summary>User settings, persisted to %APPDATA%\SendToMonitor\settings.json.</summary>
internal sealed class Settings
{
    public bool MaximizeOnArrival { get; set; }

    /// <summary>Right-click empty taskbar space to bring a window to that monitor.</summary>
    public bool TaskbarMenu { get; set; } = true;

    /// <summary>While a menu is open, show each monitor's number and name on that monitor.</summary>
    public bool ShowMonitorLabels { get; set; } = true;

    private Dictionary<string, string> _monitorNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The user's monitor names, keyed by <see cref="MonitorDesc.Key"/> (the monitor's device path).
    /// Names of monitors that are unplugged are kept for when they come back.
    /// </summary>
    public Dictionary<string, string> MonitorNames
    {
        get => _monitorNames;
        set
        {
            _monitorNames = new(StringComparer.OrdinalIgnoreCase);
            if (value == null) return;
            foreach (var (key, name) in value)
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(name)) _monitorNames[key] = name;
        }
    }

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SendToMonitor");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), s_json) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Error("Loading settings", ex);
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, s_json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings", ex);
        }
    }
}

/// <summary>"Start with Windows" via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SendToMonitor";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch { return false; }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error("Updating Run key", ex);
        }
    }

    /// <summary>If startup is enabled but the exe has moved, point the Run entry at this copy.</summary>
    public static void RefreshPathIfEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string current &&
                !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, Command);
        }
        catch { }
    }
}

/// <summary>Tiny append-only error log (%APPDATA%\SendToMonitor\log.txt). Never used from the hook.</summary>
internal static class Log
{
    private static readonly object s_lock = new();

    public static void Error(string context, Exception ex) => Write($"{context}: {ex}");

    public static void Write(string message)
    {
        try
        {
            lock (s_lock)
            {
                Directory.CreateDirectory(Settings.Folder);
                string path = Path.Combine(Settings.Folder, "log.txt");
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
