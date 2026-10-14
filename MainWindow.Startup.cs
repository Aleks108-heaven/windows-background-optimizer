using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using BgOptimizer.Localization;
using BgOptimizer.Models;

namespace BgOptimizer;

// Startup tab extras: the details line for the selected item, the shortcut to Windows' own startup settings,
// and noticing a program that switches itself back on after being disabled here.
public partial class MainWindow
{
    // Items this session switched off and has not switched back on. If one shows up enabled again at the next
    // refresh, the program rewrote its own startup entry (or someone re-enabled it elsewhere).
    private readonly HashSet<string> _disabledHere = new();

    private static string StartupKey(StartupEntry e) => $"{e.Source}|{e.Name}";

    private void InitStartupTab()
    {
        Loc.Instance.PropertyChanged += (_, _) => UpdateStartupDetails();
        UpdateStartupDetails();
    }

    private void StartupGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateStartupDetails();

    private void UpdateStartupDetails() =>
        StartupDetailsText.Text = StartupDetails.Describe(StartupGrid.SelectedItems.Count == 1 ? StartupGrid.SelectedItem as StartupEntry : null);

    private void RememberStartupChange(StartupEntry entry, bool enabled)
    {
        if (enabled) _disabledHere.Remove(StartupKey(entry));
        else _disabledHere.Add(StartupKey(entry));
    }

    /// <summary>Called with a fresh list: logs (once) every item we disabled that is enabled again.</summary>
    private void NoteSelfReenabled(IEnumerable<StartupEntry> items)
    {
        if (_disabledHere.Count == 0) return;
        foreach (var item in items)
        {
            if (!item.Enabled || !_disabledHere.Remove(StartupKey(item))) continue;
            Log(Loc.Instance.Format("Msg_StartupReenabled", item.Name));
        }
    }

    private void OpenStartupSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // A fixed Windows Settings page, not a path taken from any entry.
            Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { Log(ex.Message); }
    }
}
