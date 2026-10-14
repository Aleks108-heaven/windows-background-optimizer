using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using BgOptimizer.Localization;
using BgOptimizer.Services;
using Brush = System.Windows.Media.Brush;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace BgOptimizer;

// Settings tab: view-only mode and the System load panel (live CPU / memory / disk / GPU bars, a limit slider
// and an optional alert). Everything here only reads the system; view-only mode is what stops the app changing it.
public partial class MainWindow
{
    private readonly LoadSampler _loadSampler = new();
    private readonly LoadWatch _loadWatch = new();
    private SystemStatsSnapshot _latestStats;
    private ExtraLoad _extraLoad;
    private readonly List<LoadMeter> _meters = new();

    // Processes already reported as "would be acted on" in this episode of memory pressure, so the log
    // gets one line per process instead of one per tick. Negative ids are the would-freeze list.
    private readonly HashSet<int> _wouldAct = new();

    /// <summary>One bar of the System load panel: label, bar with the limit marker, and the value text.</summary>
    private sealed class LoadMeter
    {
        public LoadMeter(string id, Func<string> label) { Id = id; LabelText = label; }

        public string Id { get; }
        public Func<string> LabelText { get; }
        public TextBlock Label { get; } = new() { VerticalAlignment = VerticalAlignment.Center };
        public TextBlock Value { get; } = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        public ProgressBar Bar { get; set; } = null!;
        public ColumnDefinition BeforeLimit { get; } = new();
        public ColumnDefinition AfterLimit { get; } = new();
    }

    private void InitSettingsTab()
    {
        _meters.Add(NewMeter("cpu", () => Loc.Instance["Lbl_Cpu"].TrimEnd(':', ' ')));
        _meters.Add(NewMeter("mem", () => Loc.Instance["Lbl_Memory"].TrimEnd(':', ' ')));
        _meters.Add(NewMeter("disk", () => Loc.Instance["Set_LoadDisk"]));
        _meters.Add(NewMeter("gpu", () => Loc.Instance["Set_LoadGpu"]));
        foreach (var meter in _meters) LoadMeterPanel.Children.Add(BuildMeterRow(meter));

        LoadLimitSlider.ValueChanged += (_, _) => { ApplyLimitChange(); };
        ViewOnlyCheck.Checked += (_, _) => ApplyViewOnlyState(true, announce: true);
        ViewOnlyCheck.Unchecked += (_, _) => ApplyViewOnlyState(false, announce: true);
        LoadAlertCheck.Checked += (_, _) => _loadWatch.Reset();
        Loc.Instance.PropertyChanged += (_, _) => { RefreshMeterText(); UpdateLoadLimitText(); };

        ApplyLimitChange();
        RefreshMeterText();
    }

    private LoadMeter NewMeter(string id, Func<string> label) => new(id, label);

    private FrameworkElement BuildMeterRow(LoadMeter m)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });

        Grid.SetColumn(m.Label, 0);

        m.Bar = new ProgressBar { Style = (Style)FindResource("LoadBar"), VerticalAlignment = VerticalAlignment.Center };
        // The limit marker: a 2 px white line placed by splitting the bar's width into "below the limit" and "above it".
        var marker = new Grid { IsHitTestVisible = false, Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center };
        marker.ColumnDefinitions.Add(m.BeforeLimit);
        marker.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
        marker.ColumnDefinitions.Add(m.AfterLimit);
        var line = new Rectangle { Width = 2, Height = 22, Fill = (Brush)FindResource("TextBrush"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(line, 1);
        marker.Children.Add(line);

        var barHost = new Grid { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        barHost.Children.Add(m.Bar);
        barHost.Children.Add(marker);
        Grid.SetColumn(barHost, 1);

        Grid.SetColumn(m.Value, 2);
        row.Children.Add(m.Label);
        row.Children.Add(barHost);
        row.Children.Add(m.Value);
        return row;
    }

    private void RefreshMeterText()
    {
        foreach (var m in _meters)
        {
            m.Label.Text = m.LabelText();
            System.Windows.Automation.AutomationProperties.SetName(m.Bar, m.LabelText());
        }
        UpdateLoadPanel();
    }

    private int LoadLimit => (int)Math.Round(LoadLimitSlider.Value);

    private void ApplyLimitChange()
    {
        UpdateLoadLimitText();
        foreach (var m in _meters)
        {
            m.BeforeLimit.Width = new GridLength(LoadLimit, GridUnitType.Star);
            m.AfterLimit.Width = new GridLength(Math.Max(100 - LoadLimit, 0.0001), GridUnitType.Star);
        }
        _loadWatch.Reset(); // a new limit starts a new count
        UpdateLoadPanel();
    }

    private void UpdateLoadLimitText() => LoadLimitValueText.Text = Loc.Instance.Format("Set_LoadLimitValue", LoadLimit);

    /// <summary>Paints the four bars from the latest samples and, if the alert is on, announces sustained overload.</summary>
    private void UpdateLoadPanel()
    {
        if (_meters.Count == 0) return;
        var stats = _latestStats;
        var memoryPercent = stats.TotalMemoryBytes > 0 ? stats.UsedMemoryBytes * 100.0 / stats.TotalMemoryBytes : 0;
        var loads = new Dictionary<string, double?>
        {
            ["cpu"] = stats.CpuPercent,
            ["mem"] = memoryPercent,
            ["disk"] = _extraLoad.DiskPercent,
            ["gpu"] = _extraLoad.GpuPercent,
        };

        foreach (var m in _meters) PaintMeter(m, loads[m.Id]);

        if (LoadAlertCheck.IsChecked != true) return;
        foreach (var id in _loadWatch.Evaluate(DateTime.UtcNow, loads, LoadLimit))
            AnnounceHighLoad(id, loads[id]!.Value);
    }

    private void PaintMeter(LoadMeter m, double? value)
    {
        if (value is not { } v)
        {
            m.Bar.Value = 0;
            m.Value.Text = Loc.Instance["Set_LoadUnavailable"];
            m.Value.Foreground = (Brush)FindResource("TextFaintBrush");
            return;
        }

        var above = v > LoadLimit;
        m.Bar.Value = v;
        // Colour is never the only signal: the text also says "above limit".
        m.Bar.Foreground = (Brush)FindResource(above ? "DangerBrush" : "AccentBrush");
        m.Value.Foreground = (Brush)FindResource(above ? "DangerTextBrush" : "TextBrush");
        m.Value.Text = Loc.Instance.Format("Set_LoadLimitValue", Math.Round(v)) + (above ? "  ▲ " + Loc.Instance["Set_LoadAbove"] : "");
    }

    private void AnnounceHighLoad(string id, double value)
    {
        var meter = _meters.First(m => m.Id == id);
        var name = meter.LabelText();
        var busiest = BusiestPrograms(id);
        var message = busiest.Length > 0
            ? Loc.Instance.Format("Msg_LoadHigh", name, Math.Round(value), LoadLimit, busiest)
            : Loc.Instance.Format("Msg_LoadHighPlain", name, Math.Round(value), LoadLimit);

        Log(message);
        // The log is enough while the window is open; a hidden app has no other way to speak up.
        if (!IsVisible)
            _trayIcon?.ShowBalloonTip(5000, Loc.Instance["AppTitle"], message, System.Windows.Forms.ToolTipIcon.Warning);
    }

    // Per name (a browser is dozens of processes) and only for the two loads that have a per-process figure.
    private string BusiestPrograms(string id)
    {
        var top = id switch
        {
            "cpu" => _processes.Where(p => p.CpuKnown && p.CpuPercent >= 1)
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key, Rank: g.Sum(p => p.CpuPercent), Text: $"{g.Sum(p => p.CpuPercent):0}%")),
            "mem" => _processes.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key, Rank: (double)g.Sum(p => p.MemoryBytes), Text: CleanupService.FormatBytes(g.Sum(p => p.MemoryBytes)))),
            _ => Enumerable.Empty<(string Name, double Rank, string Text)>(),
        };
        return string.Join(", ", top.OrderByDescending(t => t.Rank).Take(3).Select(t => $"{t.Name} {t.Text}"));
    }

    // ---------- View-only mode ----------

    /// <summary>Puts the app in or out of view-only mode. Turning it on first releases anything the app had slowed or frozen.</summary>
    private void ApplyViewOnlyState(bool on, bool announce = false)
    {
        ViewOnlyMode.Enabled = on;
        ViewOnlyBanner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!announce) return;

        if (on)
        {
            // Release is an undo, which view-only mode never blocks.
            RestoreAutoThrottled();
            _wouldAct.Clear();
        }
        Log(Loc.Instance[on ? "Msg_ViewOnlyOn" : "Msg_ViewOnlyOff"]);
    }

    /// <summary>For handlers that would open a confirmation dialog or start work: say so up front instead of asking and then refusing.</summary>
    private bool ViewOnlyBlocks()
    {
        if (!ViewOnlyMode.Enabled) return false;
        Log(Loc.Instance["Msg_ViewOnlyNothingChanged"]);
        return true;
    }

    /// <summary>What Auto-Optimize would have done under the current memory pressure, one log line per process and episode.</summary>
    private void ReportWhatWouldHappen(HashSet<int> foreground)
    {
        foreach (var entry in _workloadOptimizer.GetIdleCandidates(_processes, foreground))
            if (_wouldAct.Add(entry.Pid))
                Log(Loc.Instance.Format("Msg_ViewOnlyWouldThrottle", entry.Name, entry.Pid));

        if (AllowSuspendCheck.IsChecked != true) return;
        foreach (var entry in _workloadOptimizer.GetLongIdleCandidates(_processes, foreground))
            if (_wouldAct.Add(-entry.Pid))
                Log(Loc.Instance.Format("Msg_ViewOnlyWouldSuspend", entry.Name, entry.Pid));
    }

    // The tab's SelectionChanged also bubbles up from combo boxes and grids inside the tabs.
    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs) || !SettingsTab.IsSelected) return;
        // Fresh disk/GPU numbers as soon as the tab opens, rather than after the next 5 s tick.
        if (_refreshTimer.IsEnabled) _ = TickAsync();
    }
}
