using BgOptimizer.Models;

namespace BgOptimizer.Services;

/// <summary>
/// Picks throttle/suspend/hog-restart candidates for "AI Workload Mode" by
/// watching actual CPU idleness rather than guessing app names — a process
/// that's genuinely doing nothing right now is safe to deprioritize, whatever
/// it's called. This keeps LM Studio (busy while inferencing) and the browser
/// (already exempt via ProtectedItems.IsInteractive, and never selected even
/// if it briefly idles) both out of reach automatically.
/// </summary>
internal class WorkloadOptimizer
{
    private const int ShortIdleSamples = 3;   // ~6s at the 2s refresh tick
    private const int LongIdleSamples = 150;  // ~5 minutes
    private const double IdleCpuThreshold = 0.5;
    private const long DefaultMinMemoryBytes = 30L * 1024 * 1024;

    private readonly Dictionary<int, int> _idleStreaks = new();

    /// <summary>Call once per refresh tick before reading candidates, so streaks reflect the latest sample.</summary>
    public void UpdateStreaks(IReadOnlyList<ProcessEntry> processes, int? foregroundPid)
    {
        var seen = new HashSet<int>(processes.Count);
        foreach (var p in processes)
        {
            seen.Add(p.Pid);
            if (p.IsProtected || ProtectedItems.IsInteractive(p.Name) || p.Pid == foregroundPid)
            {
                _idleStreaks[p.Pid] = 0;
                continue;
            }
            _idleStreaks[p.Pid] = p.CpuPercent <= IdleCpuThreshold
                ? _idleStreaks.GetValueOrDefault(p.Pid) + 1
                : 0;
        }

        // Drop bookkeeping for PIDs that exited.
        foreach (var pid in _idleStreaks.Keys.Where(pid => !seen.Contains(pid)).ToList())
            _idleStreaks.Remove(pid);
    }

    private bool IsEligible(ProcessEntry p, int? foregroundPid) =>
        !p.IsProtected && !ProtectedItems.IsInteractive(p.Name) && p.Pid != foregroundPid;

    public List<ProcessEntry> GetIdleCandidates(IReadOnlyList<ProcessEntry> processes, int? foregroundPid, long minMemoryBytes = DefaultMinMemoryBytes) =>
        processes.Where(p => IsEligible(p, foregroundPid)
            && p.MemoryBytes >= minMemoryBytes
            && _idleStreaks.GetValueOrDefault(p.Pid) >= ShortIdleSamples)
            .ToList();

    public List<ProcessEntry> GetLongIdleCandidates(IReadOnlyList<ProcessEntry> processes, int? foregroundPid, long minMemoryBytes = DefaultMinMemoryBytes) =>
        processes.Where(p => IsEligible(p, foregroundPid)
            && p.MemoryBytes >= minMemoryBytes
            && _idleStreaks.GetValueOrDefault(p.Pid) >= LongIdleSamples)
            .ToList();
}
