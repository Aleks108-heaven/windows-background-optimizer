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

    // Refresh runs on a worker thread from the timer and can also be called from the UI thread
    // (manual refresh buttons), so the two must never walk the tracking dictionary at once.
    private readonly object _refreshGate = new();

    public IReadOnlyList<ProcessEntry> Refresh()
    {
        lock (_refreshGate) return RefreshCore();
    }

    private IReadOnlyList<ProcessEntry> RefreshCore()
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

                    var startTime = SafeStartTime(proc);
                    // A PID that exited and was reused between two ticks must not inherit the old
                    // entry's name, protected flag or CPU baseline: treat it as a brand-new process.
                    if (_tracked.TryGetValue(proc.Id, out var existing)
                        && (!string.Equals(existing.Name, proc.ProcessName, StringComparison.OrdinalIgnoreCase)
                            || (existing.StartTime != default && startTime != default && existing.StartTime != startTime)))
                        _tracked.Remove(proc.Id);

                    if (!_tracked.TryGetValue(proc.Id, out var entry))
                    {
                        entry = new ProcessEntry
                        {
                            Pid = proc.Id,
                            Name = proc.ProcessName,
                            IsProtected = ProtectedItems.IsProcessProtected(proc.ProcessName),
                            StartTime = startTime,
                            LastCpuTime = SafeCpuTime(proc),
                            LastSampleTime = now,
                        };
                        _tracked[proc.Id] = entry;
                    }
                    else
                    {
                        var currentCpuTime = SafeCpuTime(proc);
                        var elapsedWall = (now - entry.LastSampleTime).TotalMilliseconds;
                        if (currentCpuTime is { } current && entry.LastCpuTime is { } previous && elapsedWall > 0)
                        {
                            var cpuDeltaMs = (current - previous).TotalMilliseconds;
                            entry.CpuPercent = Math.Max(0, Math.Min(100, cpuDeltaMs / elapsedWall / _processorCount * 100));
                            entry.CpuKnown = true;
                        }
                        else if (currentCpuTime is null)
                        {
                            // Unreadable (access denied): report unknown instead of a fake 0%.
                            entry.CpuPercent = 0;
                            entry.CpuKnown = false;
                        }
                        // Only advance the baseline on a real reading, so one failed read can't
                        // turn the next good one into a bogus 100% spike.
                        if (currentCpuTime is not null) entry.LastCpuTime = currentCpuTime;
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

        // Stable PID order rather than re-sorting by CPU% every tick: the UI keeps the
        // same ProcessEntry instances across refreshes, so a stable order means the grid
        // only has to move the few rows that actually started/exited instead of reshuffling
        // most of the list every 2 seconds. Users can still click the CPU/Memory column
        // headers to sort live via the DataGrid's own sorting.
        return _tracked.Values.OrderBy(p => p.Pid).ToList();
    }

    private static TimeSpan? SafeCpuTime(Process p)
    {
        try { return p.TotalProcessorTime; } catch { return null; }
    }

    private static DateTime SafeStartTime(Process p)
    {
        try { return p.StartTime; } catch { return default; }
    }

    private static long SafeWorkingSet(Process p)
    {
        try { return p.WorkingSet64; } catch { return 0; }
    }

    private static string SafePriority(Process p)
    {
        try { return p.PriorityClass.ToString(); } catch { return "N/A"; }
    }

    /// <summary>
    /// The grid row's name was captured up to a refresh tick ago; if that process exited and
    /// Windows reused its PID for something else (worse: a critical process), acting on the PID
    /// would hit the wrong target. Callers re-check the live name against the expected one and
    /// against the protected list, using the process handle they are about to act on.
    /// </summary>
    private static ActionResult? CheckIdentity(Process proc, string expectedName)
    {
        string live;
        try { live = proc.ProcessName; } catch { return new ActionResult(false, "Msg_ProcessChanged", expectedName, proc.Id); }
        if (!string.Equals(live, expectedName, StringComparison.OrdinalIgnoreCase))
            return new ActionResult(false, "Msg_ProcessChanged", expectedName, proc.Id);
        if (ProtectedItems.IsProcessProtected(live))
            return new ActionResult(false, "Msg_ProcessProtectedModify", expectedName);
        return null;
    }

    public static bool IsRunning(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    public static ActionResult Kill(int pid, string name)
    {
        if (ProtectedItems.IsProcessProtected(name))
            return new ActionResult(false, "Msg_ProcessProtectedKill", name);
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (CheckIdentity(proc, name) is { } changed) return changed;
            proc.Kill();
            return new ActionResult(true, "Msg_Killed", name, pid);
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "Msg_KillFailed", name, ex.Message);
        }
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>
    /// Trims the working set: pages go to the standby list and are reclaimed by Windows
    /// only under pressure, so it is safe for any app (including browsers) and the app
    /// simply faults pages back in if it needs them.
    /// </summary>
    public static ActionResult TrimMemory(int pid, string name)
    {
        if (ProtectedItems.IsProcessProtected(name))
            return new ActionResult(false, "Msg_ProcessProtectedModify", name);
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (CheckIdentity(proc, name) is { } changed) return changed;
            var before = proc.WorkingSet64;
            if (!EmptyWorkingSet(proc.Handle))
                return new ActionResult(false, "Msg_TrimFailed", name, "access denied");
            proc.Refresh();
            var freedMb = Math.Max(0, before - proc.WorkingSet64) / 1024.0 / 1024.0;
            return new ActionResult(true, "Msg_Trimmed", name, pid, freedMb.ToString("F1"));
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "Msg_TrimFailed", name, ex.Message);
        }
    }

    public static ActionResult SetPriority(int pid, string name, ProcessPriorityClass priority, bool allowInteractiveThrottle = false)
    {
        // Security/system-critical processes are never throttleable, regardless of the caller's intent.
        if (ProtectedItems.IsProcessProtected(name))
            return new ActionResult(false, "Msg_ProcessProtectedModify", name);
        if (!allowInteractiveThrottle && priority is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal && ProtectedItems.IsInteractive(name))
            return new ActionResult(false, "Msg_ProcessInteractive", name);
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (CheckIdentity(proc, name) is { } changed) return changed;
            proc.PriorityClass = priority;
            return new ActionResult(true, "Msg_PriorityChanged", name, pid, priority);
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "Msg_PriorityFailed", name, ex.Message);
        }
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);

    /// <summary>
    /// Freezes every thread in the process instantly (no CPU time at all, unlike
    /// even Idle priority) via the same undocumented-but-stable NT call Task
    /// Manager's "Freeze" and the Windows Efficiency Mode UI use. Never applied
    /// to protected, interactive (browser/shell), or security-tool processes —
    /// unlike SetPriority, security tools have no override: a fully-suspended
    /// AV/EDR agent stops protecting the machine, not just runs slower.
    /// </summary>
    public static ActionResult SuspendProcess(int pid, string name)
    {
        if (ProtectedItems.IsProcessProtected(name))
            return new ActionResult(false, "Msg_ProcessProtectedModify", name);
        if (ProtectedItems.IsInteractive(name) || ProtectedItems.IsSecurityTool(name))
            return new ActionResult(false, "Msg_ProcessInteractive", name);
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (CheckIdentity(proc, name) is { } changed) return changed;
            var status = NtSuspendProcess(proc.Handle);
            return status == 0
                ? new ActionResult(true, "Msg_Suspended", name, pid)
                : new ActionResult(false, "Msg_SuspendFailed", name, $"NTSTATUS 0x{status:X8}");
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "Msg_SuspendFailed", name, ex.Message);
        }
    }

    public static ActionResult ResumeProcess(int pid, string name)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            // Same recycled-PID guard as the other actions (name only: resuming a process that is
            // on the protected list is harmless, so CheckIdentity's protected check is skipped).
            if (!string.Equals(proc.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                return new ActionResult(false, "Msg_ProcessChanged", name, pid);
            var status = NtResumeProcess(proc.Handle);
            return status == 0
                ? new ActionResult(true, "Msg_Resumed", name, pid)
                : new ActionResult(false, "Msg_ResumeFailed", name, $"NTSTATUS 0x{status:X8}");
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "Msg_ResumeFailed", name, ex.Message);
        }
    }
}
