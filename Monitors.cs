using System.Runtime.InteropServices;

namespace SendToMonitor;

internal sealed record MonitorDesc(
    IntPtr Handle,
    string DeviceName,
    string Key,
    RECT Bounds,
    RECT WorkArea,
    bool IsPrimary,
    uint Dpi,
    string FriendlyName,
    string? CustomName,
    int Number)
{
    /// <summary>The user's name for this monitor if they gave it one, otherwise its model name.</summary>
    public string Name => CustomName ?? FriendlyName;

    /// <summary>"2 · DELL U2723QE · 2560×1440 · Primary" (with an accelerator on the number).</summary>
    public string MenuLabel
    {
        get
        {
            string number = Number <= 9 ? $"&{Number}" : Number.ToString();
            string label = $"{number} · {Escape(Name)} · {Bounds.Width}×{Bounds.Height}";
            return IsPrimary ? label + " · Primary" : label;
        }
    }

    /// <summary>"2 · DELL U2723QE", escaped for a menu, no accelerator.</summary>
    public string ShortLabel => $"{Number} · {Escape(Name)}";

    /// <summary>"DELL U2723QE · 2560×1440 · Primary": the model, whatever the user named it. Not escaped.</summary>
    public string Details
    {
        get
        {
            string details = $"{FriendlyName} · {Bounds.Width}×{Bounds.Height}";
            return IsPrimary ? details + " · Primary" : details;
        }
    }

    private static string Escape(string text) => text.Replace("&", "&&");
}

internal static class MonitorEnumerator
{
    /// <summary>
    /// Enumerates the current monitors, numbered left-to-right then top-to-bottom.
    /// Called every time a menu opens so hot-plugged displays show up.
    /// </summary>
    /// <param name="customNames">The user's monitor names, keyed by <see cref="MonitorDesc.Key"/>.</param>
    public static List<MonitorDesc> GetMonitors(IReadOnlyDictionary<string, string>? customNames = null)
    {
        var raw = new List<(IntPtr Handle, MONITORINFOEX Info)>();
        Native.MonitorEnumProc proc = (hMonitor, _, _, _) =>
        {
            if (Native.TryGetMonitorInfo(hMonitor, out var mi)) raw.Add((hMonitor, mi));
            return true;
        };
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);

        Dictionary<string, (string Name, string Key)> ids = GetMonitorIds();

        var ordered = raw
            .OrderBy(m => m.Info.rcMonitor.Left)
            .ThenBy(m => m.Info.rcMonitor.Top)
            .ToList();

        var result = new List<MonitorDesc>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            var (handle, info) = ordered[i];
            int number = i + 1;
            string device = info.szDevice ?? string.Empty;
            ids.TryGetValue(device, out var id);
            string name = string.IsNullOrWhiteSpace(id.Name) ? $"Display {number}" : id.Name;
            // The GDI name (\\.\DISPLAY3) can change between sessions; it's only a fallback if CCD fails.
            string key = string.IsNullOrEmpty(id.Key) ? device : id.Key;
            string? custom = customNames != null && customNames.TryGetValue(key, out var c) && !string.IsNullOrWhiteSpace(c) ? c : null;
            result.Add(new MonitorDesc(
                handle,
                device,
                key,
                info.rcMonitor,
                info.rcWork,
                (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0,
                Native.GetMonitorDpi(handle),
                name,
                custom,
                number));
        }
        return result;
    }

    /// <summary>
    /// Maps GDI device names (\\.\DISPLAY1) to the monitor's EDID friendly name and a stable key,
    /// its device path (\\?\DISPLAY#DELA1B2#5&amp;…&amp;UID4352#{…}), via the CCD API. The key identifies
    /// the physical monitor on its port and survives reboots, resolution changes and rearranging
    /// displays, unlike the GDI name or the left-to-right number.
    /// </summary>
    private static Dictionary<string, (string Name, string Key)> GetMonitorIds()
    {
        var map = new Dictionary<string, (string Name, string Key)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != Native.ERROR_SUCCESS)
                    return map;

                var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                int err = Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (err == Native.ERROR_INSUFFICIENT_BUFFER) continue; // topology changed between calls
                if (err != Native.ERROR_SUCCESS) return map;

                for (int i = 0; i < pathCount; i++)
                {
                    var path = paths[i];

                    var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                    source.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                    source.header.size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                    source.header.adapterId = path.sourceInfo.adapterId;
                    source.header.id = path.sourceInfo.id;
                    if (Native.DisplayConfigGetDeviceInfo(ref source) != Native.ERROR_SUCCESS) continue;

                    var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                    target.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                    target.header.size = Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                    target.header.adapterId = path.targetInfo.adapterId;
                    target.header.id = path.targetInfo.id;
                    if (Native.DisplayConfigGetDeviceInfo(ref target) != Native.ERROR_SUCCESS) continue;

                    string gdi = source.viewGdiDeviceName ?? string.Empty;
                    string friendly = (target.monitorFriendlyDeviceName ?? string.Empty).Trim();
                    string devicePath = (target.monitorDevicePath ?? string.Empty).Trim();
                    if (gdi.Length == 0 || (friendly.Length == 0 && devicePath.Length == 0)) continue;

                    // Cloned displays share one source: list every panel name once, and key the
                    // group by all of its device paths.
                    if (map.TryGetValue(gdi, out var existing))
                    {
                        string name = friendly.Length == 0 || existing.Name.Split(" + ").Contains(friendly)
                            ? existing.Name
                            : (existing.Name.Length == 0 ? friendly : existing.Name + " + " + friendly);
                        string key = string.Join("|", existing.Key.Split('|').Append(devicePath)
                            .Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));
                        map[gdi] = (name, key);
                    }
                    else
                    {
                        map[gdi] = (friendly, devicePath);
                    }
                }
                break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Querying display names", ex);
        }
        return map;
    }
}
