using System.Windows;

namespace AntarusPoFinder.App.Views;

/// <summary>Read-only scrollable full-text view — used where a card/list truncates long text
/// (e.g. FirmwareCard's description) and the operator needs to see the rest.</summary>
public partial class TextViewDialog : Window
{
    public TextViewDialog(string title, string text)
    {
        InitializeComponent();
        Title = title;
        BodyText.Text = text;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    public static void Show(Window? owner, string title, string text)
    {
        var dlg = new TextViewDialog(title, text) { Owner = owner };
        dlg.ShowDialog();
    }
}
