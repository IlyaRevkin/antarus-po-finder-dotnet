using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>КОРОТКИЙ РЕДАКТОР ТЕГОВ ОДНОЙ ПРОШИВКИ — добавить, убрать, выбрать из уже заведённых.
///
/// <b>Зачем отдельное окно.</b> Теги правят чаще всего остального, а лежали они за окном модерации,
/// где кроме них описание, типы пуска, подтипы шкафов, доп. файлы и выкладка инструкции на хостинг.
/// Открыть всё это, чтобы дописать одно слово, — долго, а главное страшно: рядом лежат поля, которые
/// человек не собирался трогать.
///
/// <b>Что происходит с остальными полями: ничего.</b> Правки из окна модерации никуда не деваются —
/// это окно их просто не трогает. Главный риск двух точек входа, пишущих одну строку, — затирание
/// чужим устаревшим снимком: открыли карточку, кто-то параллельно поправил описание, а мы при
/// сохранении тегов записали поверх СВОЮ копию описания, прочитанную при открытии. Здесь этого не
/// может случиться по устройству: окно вообще не читает и не хранит описание/типы пуска, а
/// <see cref="Apply"/> зовёт <c>Database.UpdateFwVersion</c> ОДНИМ именованным параметром
/// <c>tags:</c>. Там любой параметр null означает «не трогать это поле» — SQL собирается из
/// непустых, и в UPDATE попадает ровно <c>tags=…</c>. Сравните с модерацией
/// (EditFirmwareDialog.ApplyResult): она передаёт сразу шесть полей, потому что все шесть у неё
/// на экране и человек их видел.
///
/// Окно модерации осталось там, где и было до появления кнопки «Теги» — в меню «Ещё» карточки
/// («Модерация прошивки») и в разделе «Модерация прошивок».
///
/// <b>Почему не переиспользован EditParamTagsDialog.</b> Тот ничего не знает о сущности-владельце и
/// в базу не пишет (его вызывающий сам решает, куда класть строку тегов). У прошивки же к записи
/// тегов намертво привязаны две вещи, забыть которые нельзя: отметка о снятии тега (иначе снятый тег
/// вернётся с первой же чужой машины — Database.RecordFwTagChange) и попадание правки в накопитель
/// синхронизации (иначе «добавил тег, а коллега по нему прошивку не находит»). Обе живут в
/// <see cref="Apply"/>, одним местом на всех, кто это окно открывает.</summary>
public partial class EditFwTagsDialog : Window
{
    private readonly Database _db;

    /// <summary>Весь пул тегов, прочитанный один раз при открытии: список перестраивается на каждую
    /// букву в поле отбора и на каждый добавленный/убранный тег, и ходить за ним в базу столько же
    /// раз незачем.</summary>
    private readonly List<string> _pool;

    /// <summary>Итоговая строка тегов — считается при сохранении, применяет <see cref="Apply"/>.</summary>
    public string ResultTags { get; private set; } = "";

    /// <summary>Набор на момент открытия — нужен только для фразы «что изменилось» в уведомлении
    /// (<see cref="TagChangeText"/>), в базу не пишется.</summary>
    private readonly string _tagsBefore;

    private readonly string _title;

    public EditFwTagsDialog(Database db, FwVersionRecord record, string title)
    {
        InitializeComponent();
        _db = db;
        _title = title;
        _tagsBefore = record.Tags;
        TitleLabel.Text = title;
        _pool = db.GetAllTags();
        TagsEditor.Configure(TagString.Parse(record.Tags), () => _pool);
        // Список внизу гаснет ровно на те теги, что уже стоят у прошивки, — значит его надо
        // перерисовывать на каждую правку наверху, иначе он показывал бы уже добавленный тег как
        // доступный к добавлению.
        TagsEditor.TagsChanged += (_, _) => RenderPool();
        RenderPool();
    }

    /// <summary>Обработчик из разметки: до InitializeComponent поля ещё не созданы, и первое же
    /// событие TextChanged (оно прилетает при построении дерева) уронило бы окно.</summary>
    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        RenderPool();
    }

    private void RenderPool()
    {
        if (!IsInitialized) return;

        PoolPanel.Children.Clear();
        var filter = FilterInput.Text.Trim();
        var already = new HashSet<string>(TagsEditor.Tags, StringComparer.OrdinalIgnoreCase);

        var shown = _pool
            .Where(t => !already.Contains(t))
            .Where(t => filter.Length == 0 || t.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var tag in shown)
            PoolPanel.Children.Add(MakePoolChip(tag));

        PoolEmptyLabel.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Чип готового тега. Border с TextBlock, а не Button, по той же причине, что и чипы
    /// подсказок внутри TagBubbleEditor: кнопка забирала бы фокус, а его прямо сейчас может держать
    /// поле ввода нового тега — его LostFocus сработал бы раньше нашего нажатия.</summary>
    private System.Windows.Controls.Border MakePoolChip(string tag)
    {
        var border = new System.Windows.Controls.Border
        {
            Style = (Style)FindResource("TagBubbleBorder"),
            Child = new TextBlock { Text = tag, VerticalAlignment = VerticalAlignment.Center },
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = Cursors.Hand,
            Opacity = 0.75,
            ToolTip = "Добавить этот тег прошивке",
        };
        border.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            var tags = TagsEditor.Tags;
            if (tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))) return;
            tags.Add(tag);
            TagsEditor.Configure(tags, () => _pool);
            RenderPool();
        };
        return border;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var tags = TagsEditor.Tags;
        // Новые теги пополняют общий пул — ровно как в модерации и у файлов параметров: иначе
        // набранный здесь тег не предложился бы ни одной следующей прошивке.
        foreach (var tag in tags) _db.AddTag(tag);
        ResultTags = TagString.Join(tags);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Записать теги и объявить правку. Отдельным методом (а не внутри Save_Click), потому
    /// что писать в базу из обработчика кнопки — это тот же разъезд, от которого спасает
    /// EditFirmwareDialog.ApplyResult: вторая точка открытия рано или поздно забыла бы половину шагов.
    ///
    /// Пишется ТОЛЬКО колонка tags (см. комментарий к классу). Отметку о снятых тегах ставит сам
    /// UpdateFwVersion, а PushCatalogChange кладёт правку в накопитель синхронизации — без него тег
    /// остался бы в локальной базе и коллеги по нему прошивку не нашли бы.</summary>
    public static void Apply(EditFwTagsDialog dlg, AppServices services, IAppHost host, int versionId)
    {
        var what = TagChangeText.Describe(TagString.Parse(dlg._tagsBefore), TagString.Parse(dlg.ResultTags));
        // Ничего не изменилось — молчим и в базу не лезем: лишняя запись подняла бы плашку
        // «есть что отправить» на пустом месте.
        if (what.Length == 0) return;

        services.Db.UpdateFwVersion(versionId, tags: dlg.ResultTags);

        var message = $"{dlg._title}: {what}";
        host.ShowStatus(message, category: NotificationCategory.FirmwareAndParams);
        // subjectKey = FwVersionId — чтобы карточка именно этой прошивки показала «правки ещё не на
        // диске» (FirmwareCardFlags.TagsPending).
        host.PushCatalogChange(message, versionId.ToString());
        // Теги участвуют в поиске — показанная выдача больше не актуальна.
        host.InvalidateSearchResults();
    }
}
