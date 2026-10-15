namespace BgOptimizer.Services;

/// <summary>
/// Whether "Optimize automatically when load stays above the limit" should be acting right now. It becomes active
/// when any load has been above the user's limit for several refreshes in a row (a momentary spike is normal) and
/// stays active until every load has been a few points under the limit for as long, so it does not flip on and off
/// around the limit. Unlike <see cref="LoadWatch"/> (one alert per episode) this is a state, not an event. Pure
/// logic, so it is tested without a clock or a window.
/// </summary>
internal sealed class LoadPressure
{
    public const int SustainedSamples = LoadWatch.SustainedSamples;

    /// <summary>Points under the limit that every load must reach before the pressure counts as over.</summary>
    public const int ReleaseMargin = 5;

    private readonly Dictionary<string, int> _over = new(StringComparer.OrdinalIgnoreCase);
    private int _calm;

    public bool Active { get; private set; }

    /// <summary>The loads that have been above the limit long enough to count, while <see cref="Active"/>.</summary>
    public IReadOnlyList<string> Causes { get; private set; } = Array.Empty<string>();

    /// <summary>Feeds one refresh's loads (one call per refresh); a null reading changes nothing. Returns <see cref="Active"/>.</summary>
    public bool Update(IReadOnlyDictionary<string, double?> loads, int limitPercent)
    {
        var releaseBelow = limitPercent - ReleaseMargin;
        var allCalm = true;

        foreach (var (name, value) in loads)
        {
            if (value is not { } v) continue;
            _over[name] = v > limitPercent ? _over.GetValueOrDefault(name) + 1 : 0;
            if (v > releaseBelow) allCalm = false;
        }

        if (!Active)
        {
            var causes = _over.Where(kv => kv.Value >= SustainedSamples).Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (causes.Count > 0)
            {
                Active = true;
                _calm = 0;
                Causes = causes;
            }
        }
        else
        {
            _calm = allCalm ? _calm + 1 : 0;
            if (_calm >= SustainedSamples) Reset();
        }
        return Active;
    }

    /// <summary>Forgets everything, e.g. when the limit changes or the feature is switched off.</summary>
    public void Reset()
    {
        _over.Clear();
        _calm = 0;
        Active = false;
        Causes = Array.Empty<string>();
    }
}
