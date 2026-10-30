using System.Windows;
using System.Windows.Controls;
using BgOptimizer.Localization;
using BgOptimizer.Services;

namespace BgOptimizer;

// Settings tab, "Programs to never touch": what is always protected (shown, so the empty box of one's own names does not
// look like no protection), and a picker of running programs so a name does not have to be typed.
public partial class MainWindow
{
    private void InitProtectedUi()
    {
        BuildBuiltInList();
        BuiltInListCheck.Checked += (_, _) => BuiltInListPanel.Visibility = Visibility.Visible;
        BuiltInListCheck.Unchecked += (_, _) => BuiltInListPanel.Visibility = Visibility.Collapsed;
        Loc.Instance.PropertyChanged += (_, _) => BuildBuiltInList();
    }

    private void BuildBuiltInList()
    {
        var groups = ProtectedItems.BuiltInGroups();
        var count = groups.SelectMany(g => g.Names).Where(n => !n.StartsWith('*')).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        BuiltInProtectedText.Text = Loc.Instance.Format("Set_NeverBuiltIn", count, groups.Count);

        BuiltInListPanel.Children.Clear();
        foreach (var (titleKey, names) in groups)
        {
            BuiltInListPanel.Children.Add(new TextBlock
            {
                Text = Loc.Instance[titleKey], FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            });
            BuiltInListPanel.Children.Add(new TextBlock
            {
                Text = string.Join(", ", names), TextWrapping = TextWrapping.Wrap, FontSize = 11.5,
                Foreground = (System.Windows.Media.Brush)FindResource("TextFaintBrush"),
            });
        }
    }

    private void PickPrograms_Click(object sender, RoutedEventArgs e)
    {
        var candidates = ProtectedItems.PickerCandidates(_processes.Select(p => (p.Name, p.MemoryBytes)));
        if (candidates.Count == 0) { Log(Loc.Instance["Set_NeverPickNone"]); return; }

        var picked = ProgramPickerDialog.Pick(this, Loc.Instance["Set_NeverPickTitle"], Loc.Instance["Set_NeverPickHint"],
            Loc.Instance["Set_NeverPickAdd"], Loc.Instance["Dlg_Cancel"],
            candidates.Select(c => (c.Name, Detail: "   " + CleanupService.FormatBytes(c.Bytes) + (c.Instances > 1 ? $"  ×{c.Instances}" : ""))));
        if (picked is not { Count: > 0 }) return;

        var names = ProtectedItems.ParseUserNames(NeverTouchBox.Text);
        foreach (var name in picked)
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        // The box's own TextChanged applies the new list to the very next action and saves it.
        NeverTouchBox.Text = string.Join(", ", names);
        Log(Loc.Instance.Format("Set_NeverPickAdded", picked.Count));
    }
}
