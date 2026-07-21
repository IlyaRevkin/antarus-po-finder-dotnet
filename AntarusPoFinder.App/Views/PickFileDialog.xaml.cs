using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace AntarusPoFinder.App.Views;

/// <summary>Asks the operator which file inside a just-picked folder is the actual executable —
/// used when a folder has no file matching a recognized extension (see UploadView), since neither
/// PSL/Kinco content-sniffing nor a plain extension filter can tell in that case. "Пропустить"
/// leaves the hint empty rather than forcing a choice — the whole folder gets copied either way,
/// this is purely a display hint (see FwVersionRecord.ExecutableHint).</summary>
public partial class PickFileDialog : Window
{
    public string? Value { get; private set; }

    public PickFileDialog(string title, string label, IEnumerable<string> fileNames)
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        FilesList.ItemsSource = fileNames;
        if (FilesList.Items.Count > 0) FilesList.SelectedIndex = 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Value = FilesList.SelectedItem as string;
        DialogResult = true;
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        Value = null;
        DialogResult = true;
    }

    private void FilesList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Ok_Click(sender, e);

    /// <summary>Returns the chosen filename, or null if the user skipped (or the dialog was closed).</summary>
    public static string? Pick(Window? owner, string title, string label, IEnumerable<string> fileNames)
    {
        var dlg = new PickFileDialog(title, label, fileNames) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }
}
