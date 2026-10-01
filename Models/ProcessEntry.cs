using System.ComponentModel;
using System.Diagnostics;

namespace BgOptimizer.Models;

public class ProcessEntry : INotifyPropertyChanged
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public bool IsProtected { get; set; }

    private double _cpuPercent;
    public double CpuPercent
    {
        get => _cpuPercent;
        set { _cpuPercent = value; OnChanged(nameof(CpuPercent)); }
    }

    private long _memoryBytes;
    public long MemoryBytes
    {
        get => _memoryBytes;
        set { _memoryBytes = value; OnChanged(nameof(MemoryBytes)); OnChanged(nameof(MemoryDisplay)); OnChanged(nameof(IsHog)); }
    }

    public string MemoryDisplay => $"{MemoryBytes / 1024.0 / 1024.0:F1} MB";

    // Matches SeverityConverters' "Danger" threshold so the red highlight and the
    // AI Workload Mode "Restart" action always agree on what counts as a hog.
    // Protected processes are never offered a Restart: the action would only ever be refused.
    public bool IsHog => !IsProtected && MemoryBytes >= Converters.SeverityRules.MemoryDangerBytes;

    private string _priority = "";
    public string Priority
    {
        get => _priority;
        set { _priority = value; OnChanged(nameof(Priority)); }
    }

    private bool _isForeground;
    public bool IsForeground
    {
        get => _isForeground;
        set { _isForeground = value; OnChanged(nameof(IsForeground)); }
    }

    private bool _isSuspended;
    public bool IsSuspended
    {
        get => _isSuspended;
        set { _isSuspended = value; OnChanged(nameof(IsSuspended)); }
    }

    // Used to detect a recycled PID: same PID but a different start time means a different process.
    internal DateTime StartTime;
    // Null when the process's CPU time can't be read (access denied); distinct from "0% busy".
    internal TimeSpan? LastCpuTime;

    // False while CPU usage is unknown, so idle-detection never mistakes "can't measure" for "idle".
    internal bool CpuKnown { get; set; }
    internal DateTime LastSampleTime;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
