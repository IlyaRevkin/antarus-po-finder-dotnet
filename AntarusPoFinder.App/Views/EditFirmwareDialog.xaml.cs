using System.Collections.Generic;
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

    public string ResultDescription { get; private set; } = "";
    public string ResultTags { get; private set; } = "";
    public List<string> ResultLaunchTypes { get; private set; } = new();

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
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ResultDescription = DescriptionInput.Text.Trim();
        var tags = TagsEditor.Tags;
        foreach (var tag in tags) _db.AddTag(tag);
        ResultTags = string.Join(' ', tags);
        ResultLaunchTypes = _checks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
