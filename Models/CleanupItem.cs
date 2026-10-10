using System.ComponentModel;
using BgOptimizer.Localization;
using BgOptimizer.Services;

namespace BgOptimizer.Models;

/// <summary>One row in the Cleanup tab: a target plus the user's tick and the last scan result.</summary>
public class CleanupItem : INotifyPropertyChanged
{
    internal CleanupItem(CleanupTarget target)
    {
        Target = target;
        _selected = target.SelectedByDefault;
        // Names and descriptions are localised, so a language switch has to re-read them.
        Loc.Instance.PropertyChanged += (_, _) => OnChanged(string.Empty);
    }

    internal CleanupTarget Target { get; }

    public string Name => Loc.Instance[$"Clean_{Target.Id}"];
    public string Description => Loc.Instance[$"Clean_{Target.Id}_D"];

    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set { _selected = value; OnChanged(nameof(Selected)); }
    }

    private CleanupScan? _scan;
    internal void SetScan(CleanupScan? scan)
    {
        _scan = scan;
        OnChanged(nameof(SizeDisplay));
    }

    public string SizeDisplay => _scan is { } s ? $"{CleanupService.FormatBytes(s.Bytes)} · {s.Files}" : "—";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
