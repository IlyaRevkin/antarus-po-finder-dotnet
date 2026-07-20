using System.Windows;
using System.Windows.Input;

namespace AntarusPoFinder.App.Views;

/// <summary>Reusable single-line text prompt — WPF has no built-in QInputDialog.getText equivalent.</summary>
public partial class TextPromptDialog : Window
{
    public string Value { get; private set; } = "";

    public TextPromptDialog(string title, string label, string defaultText = "")
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        Input.Text = defaultText;
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Value = Input.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Ok_Click(sender, e);
    }

    /// <summary>Returns the entered text, or null if the user cancelled.</summary>
    public static string? Prompt(Window owner, string title, string label, string defaultText = "")
    {
        var dlg = new TextPromptDialog(title, label, defaultText) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }
}
