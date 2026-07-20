using System.Windows;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App.Views;

/// <summary>Lightweight tags-only editor for a ПЧ/УПП parameter file — parameter files share the
/// same tag pool as firmware (see Database.Tags.cs) but have no description/launch-type fields,
/// so this is a smaller counterpart to EditFirmwareDialog rather than reusing it directly.</summary>
public partial class EditParamTagsDialog : Window
{
    private readonly Database _db;

    public string ResultTags { get; private set; } = "";

    public EditParamTagsDialog(Database db, ParamFile file, string title)
    {
        InitializeComponent();
        _db = db;
        TitleLabel.Text = $"Параметры: {title}";
        TagsEditor.Configure(file.Tags.Split(' ', System.StringSplitOptions.RemoveEmptyEntries), () => _db.GetAllTags());
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var tags = TagsEditor.Tags;
        foreach (var tag in tags) _db.AddTag(tag);
        ResultTags = string.Join(' ', tags);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
