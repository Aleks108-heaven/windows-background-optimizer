namespace BgOptimizer.Services;

/// <summary>Disk activity and GPU load in percent; null where this machine does not report it.</summary>
internal readonly record struct ExtraLoad(double? DiskPercent, double? GpuPercent);

/// <summary>
/// The two loads that GetSystemTimes/GlobalMemoryStatusEx cannot give, read through a persistent PDH query
/// (<see cref="PdhQuery"/>) with the same counters Task Manager's numbers come from. This replaced two WMI
/// "formatted performance data" queries that cost about 0.4 CPU-seconds per call on a fast laptop CPU (the GPU
/// one alone took half a second), every few seconds, even while the app sat hidden in the tray; a PDH sample
/// takes about 5 ms. Counter paths are English whatever the Windows language. Callers still sample off the UI
/// thread and only while the load panel or the alert needs it.
/// </summary>
internal sealed class LoadSampler : IDisposable
{
    private readonly PdhQuery? _query;
    private readonly PdhCounter? _diskIdle;
    private readonly PdhCounter? _gpuEngines;

    public LoadSampler()
    {
        _query = PdhQuery.TryOpen();
        if (_query is null) return;
        _diskIdle = _query.AddCounter(@"\PhysicalDisk(_Total)\% Idle Time");
        _gpuEngines = _query.AddCounter(@"\GPU Engine(*)\Utilization Percentage");
        _query.Collect(); // the baseline: a rate needs two samples, so the first Sample() reads against this one
    }

    public ExtraLoad Sample() =>
        _query is { } query && query.Collect() ? new ExtraLoad(ReadDisk(), ReadGpu()) : default;

    // Task Manager's "Active time" is 100 minus the idle time of the whole disk set.
    private double? ReadDisk() =>
        _diskIdle?.Value() is { } idle ? Math.Clamp(100 - idle, 0, 100) : null;

    private double? ReadGpu() =>
        _gpuEngines is null ? null : GpuFromEngines(_gpuEngines.Values());

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

    public void Dispose() => _query?.Dispose();
}
