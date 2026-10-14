using BgOptimizer.Localization;
using BgOptimizer.Services;

namespace BgOptimizer.Models;

/// <summary>The plain-language text under the Startup grid for the selected item.</summary>
public static class StartupDetails
{
    public static string Describe(StartupEntry? entry)
    {
        if (entry == null) return Loc.Instance["St_DetailNone"];

        var lines = new List<string> { entry.Name };

        // A Run-key entry is its whole command line (arguments matter: "-background"); a shortcut or task is
        // best described by the program it resolves to.
        var runs = entry.Source is StartupSource.StartupFolderUser or StartupSource.StartupFolderCommon or StartupSource.ScheduledTask
            ? entry.TargetPath ?? entry.Command
            : entry.Command;
        lines.Add(Loc.Instance.Format("St_DetailTarget", runs));

        if (!entry.TargetMissing && entry.TargetPath != null)
            lines.Add(string.IsNullOrEmpty(entry.Publisher)
                ? Loc.Instance["St_DetailUnsigned"]
                : Loc.Instance.Format("St_DetailPublisher", entry.Publisher));

        if (entry.TargetMissing) lines.Add(Loc.Instance["St_DetailMissing"]);
        if (entry.Lock != StartupLock.None) lines.Add(Loc.Instance["St_DetailLocked"]);
        if (entry.IsReadOnly) lines.Add(Loc.Instance["St_DetailReadOnly"]);
        return string.Join(Environment.NewLine, lines);
    }
}
