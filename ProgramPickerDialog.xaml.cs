using System.Windows;

namespace BgOptimizer;

/// <summary>
/// Ticks running programs to add to the "never touch" list, so a name does not have to be typed (and spelled as the
/// process spells it). It only returns names; the caller decides what to do with them.
/// </summary>
public partial class ProgramPickerDialog : Window
{
    public sealed class Item
    {
        public string Name { get; init; } = "";
        public string Detail { get; init; } = "";
        public bool IsChecked { get; set; }
    }

    private readonly List<Item> _items;

    private ProgramPickerDialog(string title, string hint, string addText, string cancelText, IEnumerable<(string Name, string Detail)> candidates)
    {
        InitializeComponent();
        TitleText.Text = title;
        HintText.Text = hint;
        AddButton.Content = addText;
        CancelButton.Content = cancelText;
        _items = candidates.Select(c => new Item { Name = c.Name, Detail = c.Detail }).ToList();
        ItemsList.ItemsSource = _items;
    }

    /// <returns>The ticked names, or null if the dialog was cancelled.</returns>
    public static IReadOnlyList<string>? Pick(Window owner, string title, string hint, string addText, string cancelText,
        IEnumerable<(string Name, string Detail)> candidates)
    {
        var dialog = new ProgramPickerDialog(title, hint, addText, cancelText, candidates) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._items.Where(i => i.IsChecked).Select(i => i.Name).ToList() : null;
    }

    private void Add_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
