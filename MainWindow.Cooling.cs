using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BgOptimizer.Localization;
using BgOptimizer.Services;

namespace BgOptimizer;

// Settings tab, Cooling section: a read-only readout of what Windows reports about heat, and the CPU speed cap
// (see CoolingManager for why this is a cap and not a fan controller). The readout is only sampled while the
// Settings tab is open, so it costs nothing in the tray.
public partial class MainWindow
{
    private readonly ThermalSampler _thermalSampler = new();
    private readonly CoolingManager _cooling = new();
    private ThermalReading? _lastThermal;
    private (int Ac, int Dc)? _cpuLimit;   // null: this plan does not expose it (or not read yet)
    private bool _cpuLimitRead;

    private void InitCoolingTab()
    {
        FillCpuLimitCombo();
        Loc.Instance.PropertyChanged += (_, _) => { FillCpuLimitCombo(); UpdateCoolingReadout(); UpdateCpuLimitStatus(); };
        UpdateCoolingReadout();
        UpdateCpuLimitStatus();
        _ = RefreshCpuLimitAsync();
    }

    /// <summary>"No limit" then the presets; rebuilt on a language change with the same choice kept.</summary>
    private void FillCpuLimitCombo()
    {
        var keep = (CpuLimitCombo.SelectedItem as ComboBoxItem)?.Tag as int?;
        CpuLimitCombo.Items.Clear();
        foreach (var percent in CoolingManager.Presets)
        {
            var text = percent >= CoolingManager.NoLimit ? Loc.Instance["Set_CoolNone"] : $"{percent}%";
            CpuLimitCombo.Items.Add(new ComboBoxItem { Content = text, Tag = percent });
        }
        SelectPreset(keep);
    }

    private void SelectPreset(int? percent)
    {
        CpuLimitCombo.SelectedItem = CpuLimitCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == percent);
    }

    private async Task RefreshCpuLimitAsync()
    {
        try
        {
            _cpuLimit = await Task.Run(() => _cooling.ReadLimit());
            _cpuLimitRead = true;
            // Pre-select what the plan has now, when plugged in and on battery agree and it is one of the choices.
            SelectPreset(_cpuLimit is { } l && l.Ac == l.Dc ? l.Ac : null);
            UpdateCpuLimitStatus();
        }
        catch (Exception ex) { Log(ex.Message); }
    }

    private void UpdateCpuLimitStatus()
    {
        var known = _cpuLimit is not null;
        CpuLimitCombo.IsEnabled = !_cpuLimitRead || known;
        CpuLimitApplyButton.IsEnabled = !_cpuLimitRead || known;
        CpuLimitStatusText.Text = !_cpuLimitRead ? ""
            : _cpuLimit is { } l ? Loc.Instance.Format("Set_CoolCurrent", l.Ac, l.Dc)
            : Loc.Instance["Set_CoolUnavailable"];
    }

    private void UpdateCoolingReadout(ThermalReading? reading = null)
    {
        if (reading is { } fresh) _lastThermal = fresh;
        if (_lastThermal is not { } r)
        {
            CoolTempText.Text = CoolSpeedText.Text = CoolStatusText.Text = "";
            return;
        }

        CoolTempText.Text = r.TemperatureC is { } c
            ? Loc.Instance.Format("Set_CoolTemp", c.ToString("0.#", CultureInfo.CurrentCulture))
            : Loc.Instance["Set_CoolTempNone"];
        CoolSpeedText.Text = r.CpuSpeedPercent is { } speed
            ? Loc.Instance.Format("Set_CoolSpeed", speed.ToString("0", CultureInfo.CurrentCulture),
                r.CpuMhz is { } mhz ? $" ({mhz.ToString("0", CultureInfo.CurrentCulture)} MHz)" : "")
            : "";

        // The state is stated in words (and a marker), never by colour alone.
        if (r.Throttled)
        {
            CoolStatusText.Text = Loc.Instance.Format("Set_CoolThrottled", r.PassiveLimitPercent!.Value);
            CoolStatusText.Foreground = (System.Windows.Media.Brush)FindResource("WarnBrush");
        }
        else
        {
            CoolStatusText.Text = r.PassiveLimitPercent is not null ? Loc.Instance["Set_CoolOk"] : "";
            CoolStatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
        }
    }

    private async void CpuLimitApply_Click(object sender, RoutedEventArgs e)
    {
        if (ViewOnlyBlocks()) return;
        if (CpuLimitCombo.SelectedItem is not ComboBoxItem { Tag: int percent }) return;

        var message = percent >= CoolingManager.NoLimit
            ? Loc.Instance["Dlg_CoolNone"]
            : Loc.Instance.Format("Dlg_CoolLimit", percent);
        if (!ConfirmDialog.Show(this, Loc.Instance["Set_CoolTitle"], message, null,
                danger: false, Loc.Instance["Dlg_Confirm"], Loc.Instance["Dlg_Cancel"]))
            return;

        if (!BeginBusy()) return;
        try
        {
            var previous = _cpuLimit;
            // powercfg runs as child processes, so it stays off the UI thread like the other system changes.
            var result = await Task.Run(() => _cooling.SetLimit(percent));
            Log(result);
            if (result.Ok && previous is { } was)
                _undoStack.Push(() => _cooling.SetLimit(was.Ac, was.Dc, restoring: true));
        }
        catch (Exception ex) { Log(ex.Message); }
        finally { EndBusy(); }

        await RefreshCpuLimitAsync();
    }
}
