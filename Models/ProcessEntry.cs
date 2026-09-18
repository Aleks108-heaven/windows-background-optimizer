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
        set { _memoryBytes = value; OnChanged(nameof(MemoryBytes)); OnChanged(nameof(MemoryDisplay)); }
    }

    public string MemoryDisplay => $"{MemoryBytes / 1024.0 / 1024.0:F1} MB";

    private string _priority = "";
    public string Priority
    {
        get => _priority;
        set { _priority = value; OnChanged(nameof(Priority)); }
    }

    internal TimeSpan LastCpuTime;
    internal DateTime LastSampleTime;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
