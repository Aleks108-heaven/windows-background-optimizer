using System.Windows;

namespace BgOptimizer;

/// <summary>
/// A themed replacement for MessageBox.Show used for every confirmation in the
/// app, optionally listing the exact items an action affects (e.g. the
/// processes about to be killed) so the user isn't confirming blind.
/// </summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, IReadOnlyList<string>? items, bool danger, string confirmText, string cancelText)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        CancelButton.Content = cancelText;
        ConfirmButton.Content = confirmText;
        ConfirmButton.Style = (Style)FindResource(danger ? "BtnDanger" : "BtnPrimary");

        // Destructive confirmations default to Cancel so a stray Enter can't run the action.
        if (danger)
        {
            CancelButton.IsDefault = true;
            Loaded += (_, _) => CancelButton.Focus();
        }
        else
        {
            ConfirmButton.IsDefault = true;
        }

        if (items is { Count: > 0 })
        {
            ItemsList.ItemsSource = items;
            ListBorder.Visibility = Visibility.Visible;
        }
    }

    public static bool Show(Window owner, string title, string message, IEnumerable<string>? items, bool danger, string confirmText, string cancelText)
    {
        var dialog = new ConfirmDialog(title, message, items?.ToList(), danger, confirmText, cancelText) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
