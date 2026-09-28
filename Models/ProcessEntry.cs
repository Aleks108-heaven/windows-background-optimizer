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
    public bool IsHog => MemoryBytes >= 400L * 1024 * 1024;

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

    internal TimeSpan LastCpuTime;
    internal DateTime LastSampleTime;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
