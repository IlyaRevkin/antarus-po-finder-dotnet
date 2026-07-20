using System.Windows;

namespace AntarusPoFinder.App.Views;

/// <summary>Shown right after Database.ReserveNextVersion — displays the locked-in version number
/// as a ready-to-paste label for embedding into the firmware source before it's built.</summary>
public partial class ReserveVersionDialog : Window
{
    public ReserveVersionDialog(string label)
    {
        InitializeComponent();
        LabelText.Text = label;
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(LabelText.Text);

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
