using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

public partial class EditFirmwareDialog : Window
{
    private readonly Database _db;
    private readonly Dictionary<string, CheckBox> _checks = new();
    private readonly bool _hmiExecutablePickerShown;

    public string ResultDescription { get; private set; } = "";
    public string ResultTags { get; private set; } = "";
    public List<string> ResultLaunchTypes { get; private set; } = new();
    /// <summary>Null when the HMI executable picker wasn't shown (no HMI folder for this version) —
    /// UpdateFwVersion treats null as "leave unchanged", same as the other optional params, so this
    /// dialog never blanks out an existing hint for firmware that doesn't have this panel at all.</summary>
    public string? ResultHmiExecutableHint { get; private set; }

    public EditFirmwareDialog(Database db, FwVersionRecord v, string title)
    {
        InitializeComponent();
        _db = db;
        TitleLabel.Text = $"Прошивка: {title}";
        DescriptionInput.Text = v.Description;
        TagsEditor.Configure(v.Tags.Split(' ', System.StringSplitOptions.RemoveEmptyEntries), () => _db.GetAllTags());

        foreach (var lt in ConfigService.LaunchTypes)
        {
            var cb = new CheckBox
            {
                Content = lt,
                Margin = new Thickness(0, 0, 12, 0),
                IsChecked = v.LaunchTypes.Contains(lt),
            };
            _checks[lt] = cb;
            LaunchTypesPanel.Children.Add(cb);
        }

        // Lets the operator (re)pick which file inside an uploaded HMI folder is the one that
        // "HMI-проект" should open — e.g. the folder had no recognizable extension at upload time
        // and the wrong file (or none) got picked then, or the HMI project's own layout changed since.
        if (!string.IsNullOrEmpty(v.HmiPath) && Directory.Exists(v.HmiPath))
        {
            List<string> files;
            try { files = Directory.EnumerateFiles(v.HmiPath).Select(Path.GetFileName).ToList()!; }
            catch { files = new(); }
            if (files.Count > 0)
            {
                HmiExecutableCombo.ItemsSource = files;
                HmiExecutableCombo.SelectedItem = files.FirstOrDefault(f => string.Equals(f, v.HmiExecutableHint, System.StringComparison.OrdinalIgnoreCase)) ?? files[0];
                HmiExecutablePanel.Visibility = Visibility.Visible;
                _hmiExecutablePickerShown = true;
            }
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ResultDescription = DescriptionInput.Text.Trim();
        var tags = TagsEditor.Tags;
        foreach (var tag in tags) _db.AddTag(tag);
        ResultTags = string.Join(' ', tags);
        ResultLaunchTypes = _checks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
        if (_hmiExecutablePickerShown)
            ResultHmiExecutableHint = HmiExecutableCombo.SelectedItem as string ?? "";
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
