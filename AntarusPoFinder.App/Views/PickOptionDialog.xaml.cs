using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace AntarusPoFinder.App.Views;

/// <summary>Reusable single-choice combo picker — WPF has no built-in "pick one from a list" input
/// box. Used e.g. to reassign a firmware version to another controller from the version history.</summary>
public partial class PickOptionDialog : Window
{
    public record Option(int Id, string Text);

    public int SelectedId { get; private set; }

    public PickOptionDialog(string title, string label, IEnumerable<Option> options, int preselectId)
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        var list = options.ToList();
        OptionsCombo.ItemsSource = list;
        OptionsCombo.SelectedItem = list.FirstOrDefault(o => o.Id == preselectId) ?? list.FirstOrDefault();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (OptionsCombo.SelectedItem is not Option opt) { DialogResult = false; return; }
        SelectedId = opt.Id;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Returns the chosen option id, or null if cancelled / nothing chosen.</summary>
    public static int? Pick(Window? owner, string title, string label, IEnumerable<Option> options, int preselectId)
    {
        var dlg = new PickOptionDialog(title, label, options, preselectId) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.SelectedId : null;
    }
}
