namespace BgOptimizer.Services;

/// <summary>
/// The "free RAM below ... GB" setting is an absolute number, but what it means depends on the machine: 2 GB is a
/// quarter of an 8 GB laptop and half of a 4 GB one. On the small one, free memory is almost always below it, so
/// Auto-Optimize would never stand down and would keep trimming and slowing programs for nothing, which is
/// the opposite of helping a weak laptop. The threshold is therefore never allowed to exceed a third of the
/// installed memory, whatever the box says. Pure, so it is tested without a machine.
/// </summary>
internal static class RamPolicy
{
    public const double MaxShareOfInstalled = 1.0 / 3.0;

    /// <summary>The threshold the app acts on: the user's value, but at most a third of installed RAM.</summary>
    public static long EffectiveThresholdBytes(long wantedBytes, long installedBytes)
    {
        if (installedBytes <= 0) return wantedBytes; // total unknown: trust the user's value
        return Math.Min(wantedBytes, (long)(installedBytes * MaxShareOfInstalled));
    }
}
