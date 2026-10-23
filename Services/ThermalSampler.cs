namespace BgOptimizer.Services;

/// <summary>
/// What Windows knows about heat right now. <see cref="TemperatureC"/> is the hottest ACPI thermal zone (many
/// laptops expose only one, and some report a fixed value, so it is a hint rather than a CPU-die sensor);
/// <see cref="PassiveLimitPercent"/> is how much CPU speed Windows still allows for cooling (100 = not limited);
/// the speed fields come from the processor performance counters. Any of them is null when not reported.
/// </summary>
internal readonly record struct ThermalReading(double? TemperatureC, int? PassiveLimitPercent, double? CpuSpeedPercent, double? CpuMhz)
{
    /// <summary>Windows is slowing the CPU down to cool it.</summary>
    public bool Throttled => PassiveLimitPercent is < 100;

    public bool HasAnything => TemperatureC is not null || PassiveLimitPercent is not null || CpuSpeedPercent is not null;
}

/// <summary>
/// Reads the "Thermal Zone Information" and "Processor Information" counters through PDH (read-only, no driver,
/// no administrator rights; English paths on every Windows language). Sampled only while the Settings tab is
/// open, so it costs nothing in the tray.
/// </summary>
internal sealed class ThermalSampler : IDisposable
{
    private readonly PdhQuery? _query;
    private readonly PdhCounter? _tempHighPrecision;
    private readonly PdhCounter? _tempKelvin;
    private readonly PdhCounter? _passiveLimit;
    private readonly PdhCounter? _speedPercent;
    private readonly PdhCounter? _mhz;

    public ThermalSampler()
    {
        _query = PdhQuery.TryOpen();
        if (_query is null) return;
        _tempHighPrecision = _query.AddCounter(@"\Thermal Zone Information(*)\High Precision Temperature");
        _tempKelvin = _query.AddCounter(@"\Thermal Zone Information(*)\Temperature");
        _passiveLimit = _query.AddCounter(@"\Thermal Zone Information(*)\% Passive Limit");
        _speedPercent = _query.AddCounter(@"\Processor Information(_Total)\% of Maximum Frequency");
        _mhz = _query.AddCounter(@"\Processor Information(_Total)\Processor Frequency");
        _query.Collect();
    }

    public ThermalReading Sample()
    {
        if (_query is not { } query || !query.Collect()) return default;
        return Combine(
            _tempHighPrecision?.Values() ?? Array.Empty<(string, double)>(),
            _tempKelvin?.Values() ?? Array.Empty<(string, double)>(),
            _passiveLimit?.Values() ?? Array.Empty<(string, double)>(),
            _speedPercent?.Value(),
            _mhz?.Value());
    }

    /// <summary>
    /// Pure so it is tested without a machine. Temperature: the hottest plausible zone, from the high-precision
    /// counter (tenths of a kelvin) or else the plain one (kelvin); a zone at 0 K or outside -20..150 degrees is
    /// a sensor that is not really there and is ignored. Limit: the strictest zone, ignoring 0 (not reported) and
    /// anything above 100.
    /// </summary>
    internal static ThermalReading Combine(
        IEnumerable<(string Instance, double Value)> highPrecisionDeciKelvin,
        IEnumerable<(string Instance, double Value)> kelvin,
        IEnumerable<(string Instance, double Value)> passiveLimits,
        double? speedPercent, double? mhz)
    {
        var temps = highPrecisionDeciKelvin.Select(z => z.Value / 10 - 273.15).Where(IsPlausible).ToList();
        if (temps.Count == 0) temps = kelvin.Select(z => z.Value - 273.15).Where(IsPlausible).ToList();

        var limits = passiveLimits.Select(z => z.Value).Where(v => v > 0 && v <= 100).ToList();

        return new ThermalReading(
            temps.Count > 0 ? temps.Max() : null, // rounded for display only: both counters end in .x5, so rounding here would just be a tie
            limits.Count > 0 ? (int)Math.Round(limits.Min()) : null,
            speedPercent is { } s && s >= 0 ? Math.Round(s) : null,
            mhz is { } m && m > 0 ? Math.Round(m) : null);
    }

    private static bool IsPlausible(double celsius) => celsius is > -20 and < 150;

    public void Dispose() => _query?.Dispose();
}
