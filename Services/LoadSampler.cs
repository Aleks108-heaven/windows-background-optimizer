using System.Management;

namespace BgOptimizer.Services;

/// <summary>Disk activity and GPU load in percent; null where this machine does not report it.</summary>
internal readonly record struct ExtraLoad(double? DiskPercent, double? GpuPercent);

/// <summary>
/// The two loads that GetSystemTimes/GlobalMemoryStatusEx cannot give. Read from the "formatted performance
/// data" WMI classes, whose class and property names are the same in every Windows language (the
/// PerformanceCounter category names are translated, which would break on a localised Windows). A WMI query is
/// slow-ish, so callers sample off the UI thread and only while the load panel or the alert needs it.
/// </summary>
internal sealed class LoadSampler
{
    private bool _gpuUnavailable;
    private bool _diskUnavailable;

    public ExtraLoad Sample() => new(ReadDisk(), ReadGpu());

    // Task Manager's "Active time" is 100 minus the idle time of the whole disk set.
    private double? ReadDisk()
    {
        if (_diskUnavailable) return null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT PercentIdleTime FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name='_Total'");
            using var results = searcher.Get();
            foreach (ManagementBaseObject row in results)
                using (row)
                    return Math.Clamp(100 - Convert.ToDouble(row["PercentIdleTime"]), 0, 100);
            return null;
        }
        catch { _diskUnavailable = true; return null; }
    }

    private double? ReadGpu()
    {
        if (_gpuUnavailable) return null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
            using var results = searcher.Get();
            var engines = new List<(string Name, double Percent)>();
            foreach (ManagementBaseObject row in results)
                using (row)
                    engines.Add((row["Name"] as string ?? "", Convert.ToDouble(row["UtilizationPercentage"] ?? 0)));
            return GpuFromEngines(engines);
        }
        catch { _gpuUnavailable = true; return null; }
    }

    /// <summary>
    /// Instances are per process and per engine ("pid_123_luid_..._engtype_3D"). Like Task Manager: add up the
    /// instances of each engine type (3D, Copy, VideoDecode...) and report the busiest type, capped at 100.
    /// </summary>
    internal static double? GpuFromEngines(IEnumerable<(string Name, double Percent)> engines)
    {
        var byType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, percent) in engines)
        {
            var i = name.LastIndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var type = name[(i + "engtype_".Length)..];
            byType[type] = byType.GetValueOrDefault(type) + percent;
        }
        return byType.Count == 0 ? null : Math.Clamp(byType.Values.Max(), 0, 100);
    }
}
