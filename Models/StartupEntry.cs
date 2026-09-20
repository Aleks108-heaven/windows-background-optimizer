using System.ComponentModel;
using BgOptimizer.Localization;

namespace BgOptimizer.Models;

public enum StartupSource
{
    RegistryRunHkcu,
    RegistryRunHklm,
    StartupFolderUser,
    StartupFolderCommon,
    ScheduledTask,
}

public class StartupEntry : INotifyPropertyChanged
{
    public StartupEntry()
    {
        Loc.Instance.PropertyChanged += (_, _) => OnChanged(nameof(SourceDisplay));
    }

    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public StartupSource Source { get; set; }
    public bool Enabled { get; set; }
    public string SourceDisplay => Source switch
    {
        StartupSource.RegistryRunHkcu => Loc.Instance["Src_RegistryHkcu"],
        StartupSource.RegistryRunHklm => Loc.Instance["Src_RegistryHklm"],
        StartupSource.StartupFolderUser => Loc.Instance["Src_StartupFolderUser"],
        StartupSource.StartupFolderCommon => Loc.Instance["Src_StartupFolderCommon"],
        StartupSource.ScheduledTask => Loc.Instance["Src_ScheduledTask"],
        _ => Loc.Instance["Src_Unknown"],
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
