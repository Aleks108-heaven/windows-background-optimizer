using BgOptimizer.Models;

namespace BgOptimizer.Services;

/// <summary>
/// Picks throttle/suspend/hog-restart candidates for "AI Workload Mode" by
/// watching actual CPU idleness rather than guessing app names — a process
/// that's genuinely doing nothing right now is safe to deprioritize, whatever
/// it's called. CPU idleness alone is not enough for the local-AI stack, though:
/// LM Studio and agent runtimes sit near 0% CPU while waiting on each other or on
/// the GPU, so they are exempt by name (ProtectedItems.IsAiTool), as is the browser
/// (ProtectedItems.IsInteractive, never selected even if it briefly idles).
/// </summary>
internal class WorkloadOptimizer
{
    private const double IdleCpuThreshold = 0.5;
    private const long DefaultMinMemoryBytes = 30L * 1024 * 1024;

    private readonly int _shortIdleSamples;
    private readonly int _longIdleSamples;
    private readonly Dictionary<int, int> _idleStreaks = new();

    /// <param name="tickSeconds">Refresh interval; the idle thresholds stay ~6s / ~5min whatever it is.
    /// At least 3 samples are always required so one quiet sample never counts as idle.</param>
    public WorkloadOptimizer(int tickSeconds = 2)
    {
        tickSeconds = Math.Max(1, tickSeconds);
        _shortIdleSamples = Math.Max(3, (int)Math.Ceiling(6.0 / tickSeconds));
        _longIdleSamples = (int)Math.Ceiling(300.0 / tickSeconds);
    }

    /// <summary>
    /// The window the user is using is rarely one process: Electron apps put the UI in
    /// renderer/GPU helpers that share the exe name, and Store apps run behind a frame host.
    /// So the exemption covers every process that owns a foreground window plus every other
    /// process with the same exe name as one of them.
    /// </summary>
    public static HashSet<int> ExpandForeground(IReadOnlyList<ProcessEntry> processes, IReadOnlySet<int> foregroundPids)
    {
        var result = new HashSet<int>(foregroundPids);
        if (foregroundPids.Count == 0) return result;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in processes)
            if (foregroundPids.Contains(p.Pid)) names.Add(p.Name);
        foreach (var p in processes)
            if (names.Contains(p.Name)) result.Add(p.Pid);
        return result;
    }

    /// <summary>Call once per refresh tick before reading candidates, so streaks reflect the latest sample.</summary>
    public void UpdateStreaks(IReadOnlyList<ProcessEntry> processes, IReadOnlySet<int> foreground)
    {
        var seen = new HashSet<int>(processes.Count);
        foreach (var p in processes)
        {
            seen.Add(p.Pid);
            if (!IsEligible(p, foreground))
            {
                _idleStreaks[p.Pid] = 0;
                continue;
            }
            // Unknown CPU (unreadable, or not yet sampled twice) is never evidence of idleness.
            _idleStreaks[p.Pid] = p.CpuKnown && p.CpuPercent <= IdleCpuThreshold
                ? _idleStreaks.GetValueOrDefault(p.Pid) + 1
                : 0;
        }

        // Drop bookkeeping for PIDs that exited.
        foreach (var pid in _idleStreaks.Keys.Where(pid => !seen.Contains(pid)).ToList())
            _idleStreaks.Remove(pid);
        foreach (var pid in _lastTrim.Keys.Where(pid => !seen.Contains(pid)).ToList())
            _lastTrim.Remove(pid);
    }

    /// <summary>
    /// A trimmed program faults its pages straight back in, and Auto-Optimize releases everything when free RAM
    /// recovers (a trim itself makes it look recovered, since trimmed pages count as available). Without a pause
    /// that loops: trim, "recovered", release, pages come back, pressure, trim again. Each loop costs CPU and disk
    /// on exactly the small-RAM laptops that trigger it, so a program is trimmed at most once per this long.
    /// </summary>
    public static readonly TimeSpan TrimCooldown = TimeSpan.FromMinutes(10);

    private readonly Dictionary<int, DateTime> _lastTrim = new();

    /// <summary>True, and the trim is recorded, unless this process was already trimmed within <see cref="TrimCooldown"/>.</summary>
    public bool ClaimTrim(int pid, DateTime nowUtc)
    {
        if (_lastTrim.TryGetValue(pid, out var last) && nowUtc - last < TrimCooldown) return false;
        _lastTrim[pid] = nowUtc;
        return true;
    }

    private static bool IsEligible(ProcessEntry p, IReadOnlySet<int> foreground) =>
        !p.IsProtected && !ProtectedItems.IsInteractive(p.Name) && !ProtectedItems.IsSecurityTool(p.Name)
        && !ProtectedItems.IsNeverTouch(p.Name) && !foreground.Contains(p.Pid);

    public List<ProcessEntry> GetIdleCandidates(IReadOnlyList<ProcessEntry> processes, IReadOnlySet<int> foreground, long minMemoryBytes = DefaultMinMemoryBytes) =>
        processes.Where(p => IsEligible(p, foreground)
            && p.MemoryBytes >= minMemoryBytes
            && _idleStreaks.GetValueOrDefault(p.Pid) >= _shortIdleSamples)
            .ToList();

    public List<ProcessEntry> GetLongIdleCandidates(IReadOnlyList<ProcessEntry> processes, IReadOnlySet<int> foreground, long minMemoryBytes = DefaultMinMemoryBytes) =>
        processes.Where(p => IsEligible(p, foreground)
            && p.MemoryBytes >= minMemoryBytes
            && _idleStreaks.GetValueOrDefault(p.Pid) >= _longIdleSamples)
            .ToList();
}
