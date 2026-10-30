using System.Windows;
using System.Windows.Controls;
using BgOptimizer.Localization;
using BgOptimizer.Services;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;

namespace BgOptimizer;

// Power & Cooling tab, power-plan settings: a curated list from PowerCatalog with a plugged-in and an on-battery
// choice per setting and its own Apply. The rows are built from the catalog (adding a setting is one line there), read
// in a single powercfg call when the tab opens, and every change is confirmed, read back, logged and undoable.
// Suggestions sit above the list, because what is worth looking at is the question and the list is the way to act on it.
public partial class MainWindow
{
    private readonly PowerManager _power = new();
    private IReadOnlyDictionary<string, (int Ac, int Dc)> _powerValues = new Dictionary<string, (int, int)>();
    private string? _powerPlanName;
    private bool _powerRead;
    private readonly List<PowerRow> _powerRows = new();

    // A desktop has no "on battery" column to show, and Windows keeps a hidden one that no choice here should touch.
    // Exactly "no battery": an unknown status counts as having one, which only means both columns are shown.
    private static readonly bool HasBattery =
        System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery;

    /// <summary>One row of the list: the setting, its two choices, and the Apply for that row.</summary>
    private sealed class PowerRow
    {
        public PowerRow(PowerSetting setting) => Setting = setting;

        public PowerSetting Setting { get; }
        public ComboBox Ac { get; } = new() { Width = 160, VerticalAlignment = VerticalAlignment.Top };
        public ComboBox Dc { get; } = new() { Width = 160, VerticalAlignment = VerticalAlignment.Top };
        public Button Apply { get; } = new() { VerticalAlignment = VerticalAlignment.Top };
        public TextBlock Unavailable { get; } = new() { VerticalAlignment = VerticalAlignment.Top, TextWrapping = TextWrapping.Wrap };
    }

    private void InitPowerTab()
    {
        PowerAdvancedCheck.Checked += (_, _) => PowerAdvancedRows.Visibility = Visibility.Visible;
        PowerAdvancedCheck.Unchecked += (_, _) => PowerAdvancedRows.Visibility = Visibility.Collapsed;
        BuildPowerRows();
        Loc.Instance.PropertyChanged += (_, _) => { BuildPowerRows(); UpdatePowerAdvice(); UpdatePowerPlanText(); };
        UpdatePowerAdvice();
    }

    private void UpdatePowerPlanText() =>
        PowerPlanText.Text = _powerPlanName is null ? "" : Loc.Instance.Format("Pw_ActivePlan", _powerPlanName);

    // Rebuilt from scratch for a language change (labels live in the controls), so there is one code path for both.
    private void BuildPowerRows()
    {
        _powerRows.Clear();
        PowerRows.Children.Clear();
        PowerAdvancedRows.Children.Clear();
        foreach (var advanced in new[] { false, true })
        {
            var host = advanced ? PowerAdvancedRows : PowerRows;
            host.Children.Add(PowerHeaderRow());
            foreach (var group in PowerCatalog.Groups)
            {
                var settings = PowerCatalog.Settings.Where(s => s.Group == group && s.Advanced == advanced).ToList();
                if (settings.Count == 0) continue;
                host.Children.Add(new TextBlock
                {
                    Text = Loc.Instance["Pw_Group_" + group], FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 14, 0, 4), Foreground = (System.Windows.Media.Brush)FindResource("TextDimBrush"),
                });
                foreach (var setting in settings) host.Children.Add(BuildPowerRow(setting));
            }
        }
        foreach (var row in _powerRows) FillPowerRow(row);
    }

    private static readonly GridLength[] PowerColumns =
    {
        new(1, GridUnitType.Star), new(168), new(168), new(120),
    };

    private Grid NewPowerGrid()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var width in PowerColumns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        if (!HasBattery) grid.ColumnDefinitions[2].Width = new GridLength(0);
        return grid;
    }

    private FrameworkElement PowerHeaderRow()
    {
        var grid = NewPowerGrid();
        grid.Margin = new Thickness(0, 4, 0, 0);
        var faint = (System.Windows.Media.Brush)FindResource("TextFaintBrush");
        AddAt(grid, new TextBlock { Text = Loc.Instance["Pw_PluggedIn"], Foreground = faint, Margin = new Thickness(8, 0, 0, 0) }, 1);
        if (HasBattery)
            AddAt(grid, new TextBlock { Text = Loc.Instance["Pw_OnBattery"], Foreground = faint, Margin = new Thickness(8, 0, 0, 0) }, 2);
        return grid;
    }

    private static void AddAt(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private FrameworkElement BuildPowerRow(PowerSetting setting)
    {
        var row = new PowerRow(setting);
        _powerRows.Add(row);
        var title = Loc.Instance["Pw_" + setting.Id];

        var text = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock
        {
            Text = Loc.Instance["Pw_" + setting.Id + "_d"], TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("TextFaintBrush"), FontSize = 11.5,
        });

        row.Ac.Margin = new Thickness(8, 0, 0, 0);
        row.Dc.Margin = new Thickness(8, 0, 0, 0);
        System.Windows.Automation.AutomationProperties.SetName(row.Ac, $"{title}: {Loc.Instance["Pw_PluggedIn"]}");
        System.Windows.Automation.AutomationProperties.SetName(row.Dc, $"{title}: {Loc.Instance["Pw_OnBattery"]}");

        row.Apply.Content = Loc.Instance["Pw_Apply"];
        row.Apply.Margin = new Thickness(8, 0, 0, 0);
        row.Apply.Tag = row;
        row.Apply.Click += PowerApply_Click;
        System.Windows.Automation.AutomationProperties.SetName(row.Apply, $"{Loc.Instance["Pw_Apply"]}: {title}");

        row.Unavailable.Text = Loc.Instance["Pw_NotAvailable"];
        row.Unavailable.Foreground = (System.Windows.Media.Brush)FindResource("TextFaintBrush");
        row.Unavailable.Margin = new Thickness(8, 4, 0, 0);
        row.Unavailable.Visibility = Visibility.Collapsed;

        var grid = NewPowerGrid();
        AddAt(grid, text, 0);
        AddAt(grid, row.Ac, 1);
        if (HasBattery) AddAt(grid, row.Dc, 2);
        AddAt(grid, row.Apply, 3);
        Grid.SetColumn(row.Unavailable, 1);
        Grid.SetColumnSpan(row.Unavailable, 2);
        grid.Children.Add(row.Unavailable);
        return grid;
    }

    /// <summary>The values offered for a setting, in the order shown: the catalog's own order, or ascending with "never" (0) last.</summary>
    private static List<int> OfferedPowerValues(PowerSetting setting)
    {
        if (setting.Kind == PowerValueKind.Choice) return setting.Choices!.Select(c => c.Value).ToList();
        var values = setting.Presets!.Distinct().OrderBy(v => v).ToList();
        // "Never" is the longest time, so in a list of times it comes last (a percentage of 0 stays first).
        if (setting.Kind == PowerValueKind.Seconds && values.Remove(0)) values.Add(0);
        return values;
    }

    private void FillPowerRow(PowerRow row)
    {
        var known = _powerValues.TryGetValue(row.Setting.Id, out var current);
        // Until the first read finishes nothing is known; after it, a missing setting means this plan or PC does not have it.
        row.Unavailable.Visibility = _powerRead && !known ? Visibility.Visible : Visibility.Collapsed;
        row.Ac.Visibility = row.Dc.Visibility = row.Apply.Visibility = _powerRead && !known ? Visibility.Hidden : Visibility.Visible;
        row.Ac.IsEnabled = row.Dc.IsEnabled = row.Apply.IsEnabled = known;

        FillPowerCombo(row.Ac, row.Setting, known ? current.Ac : null);
        FillPowerCombo(row.Dc, row.Setting, known ? current.Dc : null);
    }

    private static void FillPowerCombo(ComboBox combo, PowerSetting setting, int? current)
    {
        combo.Items.Clear();
        var values = OfferedPowerValues(setting);
        // Whatever the plan has now is always shown, even when it is not one of the usual choices, so nothing is hidden.
        if (current is { } now && !values.Contains(now)) values.Add(now);
        foreach (var value in values)
            combo.Items.Add(new ComboBoxItem { Content = PowerManager.FormatValue(setting, value), Tag = value });
        combo.SelectedItem = current is { } c ? combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == c) : null;
    }

    private async Task RefreshPowerAsync()
    {
        try
        {
            var reading = await Task.Run(() => _power.Read());
            _powerValues = reading.Values;
            _powerPlanName = reading.PlanName;
            _powerRead = true;
            UpdatePowerPlanText();
            foreach (var row in _powerRows) FillPowerRow(row);
            UpdatePowerAdvice();
        }
        catch (Exception ex) { Log(ex.Message); }
    }

    private void UpdatePowerAdvice()
    {
        PowerAdvicePanel.Children.Clear();
        var faint = (System.Windows.Media.Brush)FindResource("TextFaintBrush");
        if (!_powerRead)
        {
            PowerAdvicePanel.Children.Add(new TextBlock { Text = Loc.Instance["Pw_Reading"], Foreground = faint });
            return;
        }
        var advice = PowerCatalog.Advise(_powerValues);
        if (advice.Count == 0)
        {
            PowerAdvicePanel.Children.Add(new TextBlock { Text = Loc.Instance["Pw_AdviceNone"], Foreground = faint, TextWrapping = TextWrapping.Wrap });
            return;
        }
        foreach (var item in advice)
        {
            // A marker and the words, not colour alone, say that this is a suggestion.
            PowerAdvicePanel.Children.Add(new TextBlock
            {
                Text = "▲ " + Loc.Instance.Format(item.Key, item.Args), TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("WarnBrush"), Margin = new Thickness(0, 0, 0, 6),
            });
        }
    }

    /// <summary>"plugged in: 15 min, on battery: 5 min" (or just the one value on a PC without a battery).</summary>
    private static string PowerPairText(PowerSetting setting, int ac, int dc) => HasBattery
        ? Loc.Instance.Format("Pw_PairBoth", PowerManager.FormatValue(setting, ac), PowerManager.FormatValue(setting, dc))
        : PowerManager.FormatValue(setting, ac);

    private async void PowerApply_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PowerRow row) return;
        if (ViewOnlyBlocks()) return;
        if (row.Ac.SelectedItem is not ComboBoxItem { Tag: int ac } || row.Dc.SelectedItem is not ComboBoxItem { Tag: int pickedDc }) return;
        if (!_powerValues.TryGetValue(row.Setting.Id, out var was)) return;
        // On a PC without a battery the on-battery value is not shown, so it is left exactly as it is.
        var dc = HasBattery ? pickedDc : was.Dc;
        if ((ac, dc) == was) { Log(Loc.Instance["Msg_PowerUnchanged"]); return; }

        var title = Loc.Instance["Pw_" + row.Setting.Id];
        var message = Loc.Instance.Format("Dlg_PowerChange", title, PowerPairText(row.Setting, was.Ac, was.Dc), PowerPairText(row.Setting, ac, dc));
        if (!ConfirmDialog.Show(this, Loc.Instance["Pw_Title"], message, null,
                danger: false, Loc.Instance["Dlg_Confirm"], Loc.Instance["Dlg_Cancel"]))
            return;

        if (!BeginBusy()) return;
        try
        {
            // powercfg runs as child processes, so it stays off the UI thread like the other system changes.
            var result = await Task.Run(() => _power.Set(row.Setting, ac, dc));
            Log(result);
            if (result.Ok)
                _undoStack.Push(() => _power.Set(row.Setting, was.Ac, was.Dc, restoring: true));
        }
        catch (Exception ex) { Log(ex.Message); }
        finally { EndBusy(); }

        await RefreshPowerAsync();
        await RefreshCpuLimitAsync(); // the speed limit lives in the same plan and the minimum above must stay under it
    }
}
