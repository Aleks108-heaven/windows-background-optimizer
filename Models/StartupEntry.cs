using System.ComponentModel;
using BgOptimizer.Localization;
using BgOptimizer.Services;

namespace BgOptimizer.Models;

public enum StartupSource
{
    RegistryRunHkcu,
    RegistryRunHklm,
    StartupFolderUser,
    StartupFolderCommon,
    ScheduledTask,
    // Added later; appended so any stored numeric value of the older sources keeps its meaning.
    RegistryRunHklm32,
    RunOnceHkcu,
    RunOnceHklm,
    PolicyHkcu,
    PolicyHklm,
    StoreApp,
}

public class StartupEntry : INotifyPropertyChanged
{
    public StartupEntry()
    {
        Loc.Instance.PropertyChanged += (_, _) =>
        {
            OnChanged(nameof(SourceDisplay));
            OnChanged(nameof(NoteDisplay));
        };
    }

    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public StartupSource Source { get; set; }
    public bool Enabled { get; set; }
    public ItemCategory Category { get; set; }
    public string CategoryDisplay => Category.ToDisplay();

    /// <summary>The file this entry runs, when it could be worked out (parsed from the command, or a shortcut's target).</summary>
    public string? TargetPath { get; set; }
    /// <summary>True only when Windows says the target is not there; an unreadable path is not "missing".</summary>
    public bool TargetMissing { get; set; }
    /// <summary>Who the file says made it; a hint, not a verification (see <see cref="PublisherInfo"/>).</summary>
    public string? Publisher { get; set; }
    public StartupLock Lock { get; set; }

    public string PublisherDisplay => string.IsNullOrEmpty(Publisher) ? "—" : Publisher;

    /// <summary>Entries Windows consumes or manages itself (run-once, policy, Store apps): shown, never switched here.</summary>
    public bool IsReadOnly => Source is StartupSource.RunOnceHkcu or StartupSource.RunOnceHklm
        or StartupSource.PolicyHkcu or StartupSource.PolicyHklm or StartupSource.StoreApp;

    public string NoteDisplay
    {
        get
        {
            var notes = new List<string>(3);
            if (Lock != StartupLock.None) notes.Add(Loc.Instance[$"St_Lock_{Lock}"]);
            if (IsReadOnly) notes.Add(Loc.Instance["St_Note_ReadOnly"]);
            if (TargetMissing) notes.Add(Loc.Instance["St_Note_TargetMissing"]);
            return string.Join(" · ", notes);
        }
    }

    public string SourceDisplay => Source switch
    {
        StartupSource.RegistryRunHkcu => Loc.Instance["Src_RegistryHkcu"],
        StartupSource.RegistryRunHklm => Loc.Instance["Src_RegistryHklm"],
        StartupSource.StartupFolderUser => Loc.Instance["Src_StartupFolderUser"],
        StartupSource.StartupFolderCommon => Loc.Instance["Src_StartupFolderCommon"],
        StartupSource.ScheduledTask => Loc.Instance["Src_ScheduledTask"],
        StartupSource.RegistryRunHklm32 => Loc.Instance["Src_RegistryHklm32"],
        StartupSource.RunOnceHkcu => Loc.Instance["Src_RunOnceHkcu"],
        StartupSource.RunOnceHklm => Loc.Instance["Src_RunOnceHklm"],
        StartupSource.PolicyHkcu => Loc.Instance["Src_PolicyHkcu"],
        StartupSource.PolicyHklm => Loc.Instance["Src_PolicyHklm"],
        StartupSource.StoreApp => Loc.Instance["Src_StoreApp"],
        _ => Loc.Instance["Src_Unknown"],
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
