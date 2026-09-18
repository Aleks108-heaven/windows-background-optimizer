using System.Diagnostics;
using BgOptimizer.Localization;
using BgOptimizer.Models;

namespace BgOptimizer.Services;

/// <summary>
/// Tracks per-process CPU% by sampling TotalProcessorTime deltas between calls
/// to Refresh (Process itself has no live CPU% property).
/// </summary>
public class ProcessMonitor
{
    private readonly Dictionary<int, ProcessEntry> _tracked = new();
    private readonly int _processorCount = Environment.ProcessorCount;

    public IReadOnlyList<ProcessEntry> Refresh()
    {
        var now = DateTime.UtcNow;
        var seenPids = new HashSet<int>();

        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
            {
                try
                {
                    seenPids.Add(proc.Id);

                    if (!_tracked.TryGetValue(proc.Id, out var entry))
                    {
                        entry = new ProcessEntry
                        {
                            Pid = proc.Id,
                            Name = proc.ProcessName,
                            IsProtected = ProtectedItems.IsProcessProtected(proc.ProcessName),
                            LastCpuTime = SafeCpuTime(proc),
                            LastSampleTime = now,
                        };
                        _tracked[proc.Id] = entry;
                    }
                    else
                    {
                        var currentCpuTime = SafeCpuTime(proc);
                        var elapsedWall = (now - entry.LastSampleTime).TotalMilliseconds;
                        if (elapsedWall > 0)
                        {
                            var cpuDeltaMs = (currentCpuTime - entry.LastCpuTime).TotalMilliseconds;
                            entry.CpuPercent = Math.Max(0, Math.Min(100, cpuDeltaMs / elapsedWall / _processorCount * 100));
                        }
                        entry.LastCpuTime = currentCpuTime;
                        entry.LastSampleTime = now;
                    }

                    entry.MemoryBytes = SafeWorkingSet(proc);
                    entry.Priority = SafePriority(proc);
                }
                catch (Exception)
                {
                    // Process exited or access denied mid-enumeration; skip it this round.
                }
            }
        }

        foreach (var staleId in _tracked.Keys.Where(id => !seenPids.Contains(id)).ToList())
            _tracked.Remove(staleId);

        return _tracked.Values.OrderByDescending(p => p.CpuPercent).ToList();
    }

    private static TimeSpan SafeCpuTime(Process p)
    {
        try { return p.TotalProcessorTime; } catch { return TimeSpan.Zero; }
    }

    private static long SafeWorkingSet(Process p)
    {
        try { return p.WorkingSet64; } catch { return 0; }
    }

    private static string SafePriority(Process p)
    {
        try { return p.PriorityClass.ToString(); } catch { return "N/A"; }
    }

    public static ActionResult Kill(int pid, string name)
    {
        if (ProtectedItems.IsProcessProtected(name))
            return new ActionResult(false, "Msg_ProcessProtectedKill", name);
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill();
            return new ActionResult(true, "Msg_Killed", name, pid);
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "Msg_KillFailed", name, ex.Message);
        }
    }

    public static ActionResult SetPriority(int pid, string name, ProcessPriorityClass priority)
    {
        if (ProtectedItems.IsProcessProtected(name))
            return new ActionResult(false, "Msg_ProcessProtectedModify", name);
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.PriorityClass = priority;
            return new ActionResult(true, "Msg_PriorityChanged", name, pid, priority);
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "Msg_PriorityFailed", name, ex.Message);
        }
    }
}
