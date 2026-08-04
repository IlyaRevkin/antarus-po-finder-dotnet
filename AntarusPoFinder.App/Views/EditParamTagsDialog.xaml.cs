using System.Windows;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App.Views;

/// <summary>Lightweight tags-only editor for a ПЧ/УПП parameter file — parameter files share the
/// same tag pool as firmware (see Database.Tags.cs) but have no description/launch-type fields,
/// so this is a smaller counterpart to EditFirmwareDialog rather than reusing it directly.
///
/// Тем же диалогом правятся теги шаблона паспорта шкафа: он не пишет в базу сам и о сущности-
/// владельце ничего не знает — берёт строку тегов, отдаёт строку тегов (сохраняет вызывающий), —
/// поэтому подпись сверху параметризована (<paramref name="kind"/>), а не зашита словом
/// «Параметры».</summary>
public partial class EditParamTagsDialog : Window
{
    private readonly Database _db;

    public string ResultTags { get; private set; } = "";

    public EditParamTagsDialog(Database db, string currentTags, string title, string kind = "Параметры")
    {
        InitializeComponent();
        _db = db;
        TitleLabel.Text = $"{kind}: {title}";
        TagsEditor.Configure(AntarusPoFinder.Core.Services.TagString.Parse(currentTags), () => _db.GetAllTags());
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var tags = TagsEditor.Tags;
        foreach (var tag in tags) _db.AddTag(tag);
        ResultTags = AntarusPoFinder.Core.Services.TagString.Join(tags);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
