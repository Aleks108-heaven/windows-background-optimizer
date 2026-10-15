using System.Windows;

namespace BgOptimizer;

/// <summary>
/// Asks for the view-only code: one box to enter an existing code, or two to choose a new one. The dialog stays
/// open and shows the reason until the code is accepted or the user cancels, so a typo costs one retry, not a restart.
/// </summary>
public partial class CodeDialog : Window
{
    private readonly Func<string, string?> _check;
    private readonly Func<string>? _mismatchText;

    /// <summary>The accepted code; only meaningful when the dialog returned true.</summary>
    public string Code { get; private set; } = "";

    private CodeDialog(string title, string message, string okText, string cancelText, string? repeatLabel,
        Func<string, string?> check, Func<string>? mismatchText)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = okText;
        CancelButton.Content = cancelText;
        _check = check;
        _mismatchText = mismatchText;
        if (repeatLabel is not null)
        {
            RepeatLabel.Text = repeatLabel;
            RepeatLabel.Visibility = RepeatBox.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) => CodeBox.Focus();
    }

    /// <summary>
    /// Returns the accepted code, or null if cancelled. <paramref name="check"/> gets the typed code and returns the
    /// error to show, or null to accept it. A non-null <paramref name="repeatLabel"/> means "choose a new code":
    /// a second box must then match the first (<paramref name="mismatchText"/> is shown when it does not).
    /// </summary>
    public static string? Ask(Window owner, string title, string message, string okText, string cancelText,
        Func<string, string?> check, string? repeatLabel = null, Func<string>? mismatchText = null)
    {
        var dialog = new CodeDialog(title, message, okText, cancelText, repeatLabel, check, mismatchText) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Code : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Password;
        if (RepeatBox.Visibility == Visibility.Visible && code != RepeatBox.Password)
        {
            ShowError(_mismatchText?.Invoke());
            return;
        }

        var error = _check(code);
        if (error is null)
        {
            Code = code;
            DialogResult = true;
            return;
        }
        // Clear first: emptying a box raises PasswordChanged, which would wipe the message.
        CodeBox.Clear();
        RepeatBox.Clear();
        ShowError(error);
        CodeBox.Focus();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // Typing again clears a stale message.
    private void Input_Changed(object sender, RoutedEventArgs e) => ShowError(null);

    private void ShowError(string? text)
    {
        ErrorText.Text = text ?? "";
        ErrorText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
