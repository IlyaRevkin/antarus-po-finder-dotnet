using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Таблица параметров ПЧ/УПП: что выставлять на объекте и что менялось от редакции к
/// редакции.
///
/// Собрано из двух уже работающих образцов, а не придумано заново: список ревизий слева — как в
/// «Истории изменений программы» (AppChangelogWindow), «сверху таблица, снизу разбор через
/// GridSplitter» с подсветкой строк — как в «Истории версий» (HistoryDialog).
///
/// Наладчику здесь чтение и отбор по аппарату; правка — программисту и администратору
/// (ParamTableEditing.CanEdit). Причина не в недоверии: документ ездит в общем конфиге, и правка
/// «по месту, чтобы сходилось» разошлась бы с замыслом программиста молча и сразу у всех.</summary>
public partial class ParamTableWindow : Window
{
    private readonly AppServices _services;
    private readonly IAppHost _host;
    private readonly string _diskPath;
    private readonly string _filename;

    private List<ParamTableRevision> _revisions = new();
    private List<ParamTableRow> _rows = new();
    private ParamTableDiff.Result? _diff;
    private List<ParamTableColumn> _ownColumns = new();
    private int _builtInColumns;

    /// <summary>К каким типам и подтипам шкафов относится файл документа. Пересчитывается при
    /// открытии и после каждой правки: привязка живёт не в документе, а в записях файла параметров,
    /// и меняют её отсюда же.</summary>
    private ParamTableBinding.Result _binding = new(new(), null);

    /// <summary>Код строки, к которой надо подмотать таблицу при открытии (пришли из поиска).
    /// Гасится после первого показа: дальше человек крутит таблицу сам, и лезть в это уже нельзя.</summary>
    private string _focusCode = "";

    /// <summary>Строка таблицы для показа: сами данные плюс пометка «что с ней стало относительно
    /// предыдущей ревизии».</summary>
    public sealed class RowVm
    {
        public string GroupName { get; init; } = "";
        public string Code { get; init; } = "";
        public string Title { get; init; } = "";
        public string ValueDisplay { get; init; } = "";
        public string Factory { get; init; } = "";
        public string Unit { get; init; } = "";
        public string Description { get; init; } = "";
        public string Applicability { get; init; } = "";
        public string AppliesWhen { get; init; } = "";
        public bool IsNote { get; init; }

        /// <summary>Значения своих столбцов документа ПО МЕСТУ — в том же порядке, в каком колонки
        /// для них заведены в коде (см. ShowOwnColumns). По месту, а не по названию: заголовок
        /// пишет человек, и путь привязки со скобками и пробелами внутри разбирается не так.</summary>
        public List<string> Extras { get; init; } = new();

        /// <summary>Имя изменения ровно как у ParamTableDiff.ChangeKind — по нему подсвечивает
        /// DataGrid.RowStyle. Пусто — строка не менялась.</summary>
        public string Change { get; init; } = "";

        public string ChangeMark { get; init; } = "";
    }

    private sealed class RevisionVm
    {
        public ParamTableRevision Source { get; init; } = new();

        /// <summary>Номер ПОКАЗА, а не хранимый: хранимый присвоила заведшая машина, и после обмена
        /// конфигом «третьих» в одном документе бывает две (см. ParamTableNumbering).</summary>
        public string Header => $"Ревизия {Source.DisplayNumber}";

        /// <summary>Дата, автор и — если номера разошлись — под каким номером ревизию завели.
        /// Молчать об этом нельзя: под старым номером она названа в чужих Summary и в разговоре.</summary>
        public string Subheader => string.Join(" · ", new[]
        {
            DateOnly(Source.CreatedAt),
            Source.Author,
            Source.Number > 0 && Source.Number != Source.DisplayNumber ? $"заведена как {Source.Number}" : "",
        }.Where(s => s.Length > 0));

        private static string DateOnly(string iso) =>
            iso.Length >= 10 ? iso[..10] : iso;
    }

    /// <param name="selectTableId">Какой документ раскрыть сразу. Нужен поиску: он находит
    /// КОНКРЕТНЫЙ документ, а у файла их бывает несколько, и открыть первый попавшийся значило бы
    /// показать не то, что нашлось.</param>
    /// <param name="focusCode">Код настройки, из-за которого документ нашёлся. Таблица подматывается
    /// к этой строке и выделяет её: иначе человек, искавший «P0-10», получает полсотни строк и ищет
    /// нужную глазами заново.</param>
    public ParamTableWindow(AppServices services, IAppHost host, string diskPath, string filename, string fileLabel,
        int? selectTableId = null, string? focusCode = null)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        _diskPath = diskPath;
        _filename = filename;

        FileLabel.Text = "Файл параметров: " + fileLabel;
        // Сколько колонок задано в XAML — до них своих столбцов не бывает, и при перестроении
        // трогать их нельзя. Считаем один раз здесь, а не числом в коде: добавится встроенный
        // столбец — и число молча разошлось бы с разметкой.
        _builtInColumns = RowsGrid.Columns.Count;
        var canEdit = ParamTableEditing.CanEdit(_services.Cfg.CurrentRole());
        EditBtn.IsEnabled = canEdit;
        RenameBtn.IsEnabled = canEdit;
        DeleteBtn.IsEnabled = canEdit;
        ImportBtn.IsEnabled = canEdit;
        ColumnsBtn.IsEnabled = canEdit;
        if (!canEdit)
        {
            var hint = "Правка таблицы — у программиста и администратора. Здесь можно смотреть и отбирать строки по аппарату.";
            EditBtn.ToolTip = hint;
            ImportBtn.ToolTip = hint;
        }

        _focusCode = (focusCode ?? "").Trim();
        LoadDocuments(selectTableId);
        ShowBinding();
    }

    private ParamTable? Current => DocumentCombo.SelectedItem as ParamTable;

    private void LoadDocuments(int? selectId)
    {
        var documents = _services.Db.GetParamTablesForFile(_diskPath, _filename);
        DocumentCombo.ItemsSource = documents;
        if (documents.Count == 0)
        {
            DocumentCombo.SelectedIndex = -1;
            ShowEmpty();
            return;
        }

        DocumentCombo.SelectedItem = documents.FirstOrDefault(d => d.Id == selectId) ?? documents[0];
    }

    private void ShowEmpty()
    {
        RevisionsList.ItemsSource = null;
        RowsGrid.ItemsSource = null;
        ApplicabilityCombo.ItemsSource = null;
        ShowTags();
        ReasonText.Text = "У этого файла параметров таблицы ещё нет.";
        AuthorText.Text = ParamTableEditing.CanEdit(_services.Cfg.CurrentRole())
            ? "«Новый документ из файла…» разберёт накопленный txt в таблицу — с предпросмотром и правкой до сохранения."
            : "Завести её может программист или администратор.";
        SummaryText.Text = "";
    }

    private void Document_Changed(object sender, SelectionChangedEventArgs e)
    {
        LoadRevisions();
        ShowTags();
    }

    private void LoadRevisions()
    {
        if (Current?.Id is not int tableId)
        {
            ShowEmpty();
            return;
        }

        // Список ревизий — через ParamTableNumbering: номер показа считается по времени заведения,
        // а не берётся из чужой базы, иначе после обмена конфигом в списке бывают две «третьих».
        _revisions = ParamTableNumbering.LiveRevisions(_services.Db, tableId);
        RevisionsList.ItemsSource = _revisions.Select(r => new RevisionVm { Source = r }).ToList();
        if (_revisions.Count > 0) RevisionsList.SelectedIndex = 0;
        else ShowEmpty();
    }

    private void Revision_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RevisionsList.SelectedItem is not RevisionVm selected || selected.Source.Id is not int revisionId)
            return;

        _rows = _services.Db.GetParamTableRows(revisionId);

        // Сравниваем с ревизией, идущей СЛЕДОМ в списке, а не с самой свежей: список отсортирован от
        // новых к старым, поэтому «предыдущая» — это соседняя снизу. Смотрят обычно не последнюю
        // ревизию, а ту, по которой шкаф настраивали, и «что изменилось» ей нужно своё.
        var at = RevisionsList.SelectedIndex;
        var previous = at >= 0 && at + 1 < _revisions.Count ? _revisions[at + 1] : null;
        var before = previous?.Id is int previousId ? _services.Db.GetParamTableRows(previousId) : null;
        _diff = previous is null ? null : ParamTableDiff.Compare(before, _rows);

        ReasonText.Text = selected.Source.Reason.Length > 0
            ? "Зачем: " + selected.Source.Reason
            : "Зачем правили — не записано.";
        AuthorText.Text = string.Join(" · ", new[]
        {
            "Ревизия " + ParamTableNumbering.Label(selected.Source),
            selected.Source.CreatedAt,
            selected.Source.Author.Length > 0 ? "автор: " + selected.Source.Author : "",
        }.Where(s => s.Length > 0));

        SummaryText.Text = previous is null
            ? selected.Source.Summary.Length > 0 ? selected.Source.Summary : "Первая редакция документа — сравнивать не с чем."
            : $"Изменения относительно ревизии {previous.DisplayNumber}. " + ParamTableDiff.Describe(_diff!);

        var applicabilities = ParamTableEditing.Applicabilities(_rows);
        var wanted = ApplicabilityCombo.SelectedItem as string;
        ApplicabilityCombo.ItemsSource = applicabilities;
        ApplicabilityCombo.SelectedItem = applicabilities.Contains(wanted ?? "") ? wanted : applicabilities[0];

        ShowRows();
    }

    private void Applicability_Changed(object sender, SelectionChangedEventArgs e) => ShowRows();

    private void ShowRows()
    {
        if (RowsGrid is null) return;

        var shown = ParamTableEditing.FilterByApplicability(_rows, ApplicabilityCombo.SelectedItem as string);
        var ordered = ParamTableEditing.Ordered(shown, _services.Db.GetParamGroupOrder());
        ShowOwnColumns();

        var shownRows = ordered.Select(row =>
        {
            var change = _diff?.KindOf(row)?.ToString() ?? "";
            var extra = ParamRowExtra.Parse(row.Extra);

            return new RowVm
            {
                Extras = _ownColumns.Select(c => extra.TryGetValue(c.Key, out var value) ? value : "").ToList(),
                GroupName = row.GroupName,
                Code = row.Code,
                Title = row.Title,
                ValueDisplay = row.Kind == ParamRowKind.Note ? "" : row.ValueDisplay,
                Factory = row.Factory,
                Unit = row.Unit,
                Description = row.Description,
                Applicability = row.Applicability,
                AppliesWhen = row.AppliesWhen,
                IsNote = row.Kind == ParamRowKind.Note,
                Change = change,
                ChangeMark = change switch
                {
                    nameof(ParamTableDiff.ChangeKind.Added) => "новая",
                    nameof(ParamTableDiff.ChangeKind.ValueChanged) => "значение",
                    nameof(ParamTableDiff.ChangeKind.ExtraChanged) => "свой столбец",
                    nameof(ParamTableDiff.ChangeKind.Edited) => "правка",
                    _ => "",
                },
            };
        }).ToList();

        // Разделы таблицы — группировкой представления, а не своими строками-врезками в списке:
        // врезка была бы обычной строкой, и её пришлось бы прятать от отбора, от подсветки
        // изменений и от выделения. Порядок разделов при этом остаётся тем, в котором строки
        // пришли (см. ParamTableEditing.Ordered) — своих SortDescriptions у представления нет.
        var view = new ListCollectionView(shownRows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RowVm.GroupName)));
        RowsGrid.ItemsSource = view;

        FocusFoundRow(shownRows);
    }

    /// <summary>Подмотать таблицу к строке, из-за которой документ нашёлся поиском, и выделить её.
    /// Ровно один раз: дальше человек крутит таблицу сам.</summary>
    private void FocusFoundRow(List<RowVm> shown)
    {
        if (_focusCode.Length == 0) return;

        var found = shown.FirstOrDefault(r => string.Equals(r.Code, _focusCode, System.StringComparison.OrdinalIgnoreCase));
        _focusCode = "";
        if (found is null) return;

        RowsGrid.SelectedItem = found;
        // Через Dispatcher: строки ещё не построены (группировка отключает виртуализацию не сразу),
        // и ScrollIntoView прямо здесь промахивается на первый экран.
        Dispatcher.BeginInvoke(new System.Action(() => RowsGrid.ScrollIntoView(found)),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    // ── Привязка к типам и подтипам шкафов ───────────────────────────────────────────────────

    /// <summary>Показать, к каким шкафам относится документ. Привязка ВЫВОДИТСЯ из записей файла
    /// параметров (см. Core/Services/ParamTableBinding.cs) — своей у документа нет и быть не
    /// должно.</summary>
    private void ShowBinding()
    {
        _binding = ParamTableBinding.For(_services.Db, _diskPath, _filename,
            _services.Hierarchy, _services.Cfg.RootPath());

        BindingLabel.Text = _binding.Describe();
        // Непривязанный документ — не мелочь: наладчик не поймёт, к какому шкафу таблица. Поэтому
        // строка не просто «пустая», а помечена цветом предупреждения.
        BindingLabel.Foreground = (System.Windows.Media.Brush)FindResource(
            _binding.Links.Count == 0 ? "WarningBrush" : "TextBrush");
        BindingLabel.ToolTip = _binding.Links.Count == 0
            ? "Файл параметров в базе не значится: он есть на диске, но программа не знает, к какому шкафу он относится. «Подтипы…» это исправит."
            : _binding.Describe();

        SubtypesBtn.IsEnabled = ParamTableEditing.CanEdit(_services.Cfg.CurrentRole());
    }

    private void ShowTags()
    {
        var tags = TagString.Parse(Current?.Tags);
        TagsBtn.IsEnabled = Current is not null && ParamTableEditing.CanEdit(_services.Cfg.CurrentRole());
        TagsLabel.Text = Current is null
            ? ""
            : tags.Count == 0 ? "Тегов нет — по словам этот документ не найдётся"
                              : "Теги: " + string.Join(", ", tags);
    }

    private void Subtypes_Click(object sender, RoutedEventArgs e)
    {
        var primary = _binding.Primary;
        if (primary is null)
        {
            primary = RegisterFile();
            if (primary is null) return;
        }

        var dialog = new EditParamSubtypesDialog(_services, primary, _filename) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var result = dialog.Result;
        if (result is not null)
        {
            if (result.Warnings.Count > 0)
                AppMessageBox.Show(string.Join("\n", result.Warnings), "Подтипы",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            var parts = new List<string>();
            if (result.Added.Count > 0) parts.Add("добавлено: " + string.Join(", ", result.Added));
            if (result.Removed.Count > 0) parts.Add("убрано: " + string.Join(", ", result.Removed));
            if (parts.Count > 0) Announce($"Подтипы файла {_filename} — {string.Join("; ", parts)}");
        }

        ShowBinding();
    }

    /// <summary>Завести запись файла параметров для документа, у которого её нет.
    ///
    /// Такое бывает: файл на диске лежит, а в базе не значится — запись удалили, либо документ
    /// приехал с машины, где диск смонтирован иначе. Пока записи нет, привязывать нечего: подтипы
    /// живут именно на ней (см. ParamTableBinding). Спрашиваем ОСНОВНОЙ подтип — тот, в чьей папке
    /// файл считается лежащим, — и заводим запись; остальные подтипы добавляются потом обычным
    /// окном, вместе с ярлыками в их папках.</summary>
    private ParamFile? RegisterFile()
    {
        if (Current is null)
        {
            AppMessageBox.Show("Сначала заведите документ — привязывать пока нечего.", "Подтипы",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var candidates = ParamTableBinding.Candidates(_services.Db);
        if (candidates.Count == 0)
        {
            AppMessageBox.Show("В справочнике нет ни одного подтипа шкафа — заводить привязку не к чему.",
                "Подтипы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var answer = AppMessageBox.Show(
            $"Файл «{_filename}» в базе не значится — программа не знает, к какому шкафу он относится.\n\n"
            + "Завести для него запись? Сам файл на диске не тронется: он уже там, копировать нечего.",
            "Подтипы", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return null;

        var options = candidates
            .OrderBy(c => c.GroupName, System.StringComparer.CurrentCulture)
            .ThenBy(c => c.Display, System.StringComparer.CurrentCulture)
            .Select(c => new PickOptionDialog.Option(c.Id, c.FullDisplay))
            .ToList();
        var pick = PickOptionDialog.Pick(this, "Основной подтип",
            "В папке какого подтипа лежит этот файл параметров:", options, options[0].Id);
        if (pick is not int subtypeId) return null;

        var file = ParamTableBinding.Register(_services.Db, Current, subtypeId);
        Announce($"Файл параметров {_filename} заведён в базе");
        ShowBinding();
        return file;
    }

    private void Tags_Click(object sender, RoutedEventArgs e)
    {
        if (Current?.Id is not int tableId) return;

        var dialog = new EditParamTagsDialog(_services.Db, Current.Tags, Current.Name, "Документ") { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _services.Db.UpdateParamTableTags(tableId, dialog.ResultTags);
        var at = DocumentCombo.SelectedIndex;
        LoadDocuments(tableId);
        if (at >= 0 && DocumentCombo.Items.Count > at && DocumentCombo.SelectedIndex < 0)
            DocumentCombo.SelectedIndex = at;
        Announce($"Теги документа «{Current?.Name}» изменены");
    }

    /// <summary>Свои столбцы документа — колонками справа от встроенных, заново под каждую
    /// показанную ревизию.
    ///
    /// Заново, а не один раз при открытии, потому что набор зависит от РЕВИЗИИ: снятый столбец,
    /// по которому в этой редакции осталось содержимое, показывать надо (иначе человек увидит
    /// меньше, чем в редакции записано, и не узнает об этом), а в редакции, где его не заполняли, —
    /// не надо. Правило считает ParamTableColumnEditing.Visible, оно под тестами.</summary>
    private void ShowOwnColumns()
    {
        var all = Current?.Id is int tableId
            ? _services.Db.AllParamTableColumnsIncludingDeleted(tableId)
            : new List<ParamTableColumn>();
        _ownColumns = ParamTableColumnEditing.Visible(all, _rows);

        while (RowsGrid.Columns.Count > _builtInColumns)
            RowsGrid.Columns.RemoveAt(RowsGrid.Columns.Count - 1);

        var cellStyle = (Style)FindResource("DataGridCellText");
        for (var i = 0; i < _ownColumns.Count; i++)
            RowsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = _ownColumns[i].Title,
                Binding = new System.Windows.Data.Binding($"Extras[{i}]"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 110,
                ElementStyle = cellStyle,
            });
    }

    // ── Действия ─────────────────────────────────────────────────────────────────────────────

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбрать текстовый файл параметров",
            Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы|*.*",
        };
        if (picker.ShowDialog() != true) return;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(picker.FileName);
        }
        catch (IOException ex)
        {
            AppMessageBox.Show("Не удалось прочитать файл: " + ex.Message, "Таблица параметров",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new ParamTableEditorDialog(_services.Db.GetParamGroups(), bytes,
            Path.GetFileName(picker.FileName)) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var (tableId, _) = ParamTableEditing.CreateFromImport(_services.Db, new ParamTable
        {
            DiskPath = _diskPath,
            Filename = _filename,
            Name = dialog.ResultName,
            Manufacturer = Current?.Manufacturer ?? "",
        }, dialog.ResultRows, dialog.ResultReason, _services.CurrentUserName);

        LoadDocuments(tableId);
        Announce($"Заведена таблица параметров «{dialog.ResultName}»: строк {dialog.ResultRows.Count}");
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Current?.Id is not int tableId) return;

        var dialog = new ParamTableEditorDialog(_services.Db.GetParamGroups(),
            _services.Db.GetParamTableColumns(tableId), Current.Name, _rows) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        if (!string.Equals(dialog.ResultName, Current.Name, System.StringComparison.Ordinal))
            _services.Db.UpdateParamTable(tableId, dialog.ResultName, Current.Manufacturer);

        var (_, diff) = ParamTableEditing.SaveRevision(_services.Db, tableId, dialog.ResultRows,
            dialog.ResultReason, _services.CurrentUserName);

        LoadDocuments(tableId);
        Announce(diff.Any
            ? $"Новая ревизия таблицы «{dialog.ResultName}»: " + ParamTableDiff.Describe(diff)
            : $"Новая ревизия таблицы «{dialog.ResultName}» — строки не изменились");
    }

    private void Columns_Click(object sender, RoutedEventArgs e)
    {
        if (Current?.Id is not int tableId) return;

        var dialog = new ParamTableColumnsDialog(_services.Db, tableId, Current.Name) { Owner = this };
        dialog.ShowDialog();
        // Правки в том окне записываются сразу, поэтому смотрим на его Changed, а не на
        // DialogResult: окно закрывают и крестиком тоже.
        if (!dialog.Changed) return;

        var at = RevisionsList.SelectedIndex;
        LoadRevisions();
        if (at >= 0 && at < _revisions.Count) RevisionsList.SelectedIndex = at;

        // Свои столбцы встают ПОСЛЕДНИМИ, а встроенных девять — на обычном экране только что
        // заведённый столбец оказывается за правым краем, и выглядит это как «кнопка ничего не
        // сделала». Подматываем таблицу к нему один раз, сразу после действия человека: дальше он
        // крутит её сам, и лезть в это уже нельзя.
        if (RowsGrid.Items.Count > 0 && RowsGrid.Columns.Count > _builtInColumns)
            RowsGrid.ScrollIntoView(RowsGrid.Items[0], RowsGrid.Columns[^1]);

        Announce($"Поправлены свои столбцы документа «{Current.Name}»");
    }

    private void EditReason_Click(object sender, RoutedEventArgs e)
    {
        if (RevisionsList.SelectedItem is not RevisionVm selected || selected.Source.Id is not int revisionId) return;

        var text = TextPromptDialog.Prompt(this, "Зачем правили",
            $"Ревизия {selected.Source.DisplayNumber} — одна строка о том, зачем её завели:", selected.Source.Reason);
        if (text is null) return;

        // Правится ТОЛЬКО «зачем». Строки ревизии — снимок: переписать их задним числом значит
        // рассказать про прошлое неправду.
        _services.Db.UpdateParamTableRevisionReason(revisionId, text);
        var at = RevisionsList.SelectedIndex;
        LoadRevisions();
        if (at >= 0 && at < _revisions.Count) RevisionsList.SelectedIndex = at;
        Announce("Поправлено «зачем» у ревизии " + selected.Source.DisplayNumber);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Current?.Id is not int tableId) return;
        var name = Current.Name;

        var answer = AppMessageBox.Show(
            $"Убрать таблицу «{name}» вместе со всеми её ревизиями?\n\n"
            + "Файл параметров на диске останется как есть — убирается только документ программы.",
            "Таблица параметров", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        _services.Db.TombstoneParamTable(tableId);
        LoadDocuments(null);
        Announce($"Таблица параметров «{name}» убрана");
    }

    /// <summary>Сказать вслух и подтолкнуть отправку конфига. Второе обязательно: документ живёт в
    /// общем конфиге, и без толчка правка ждала бы ближайшей плановой отправки — то есть у коллеги
    /// на объекте её сегодня не было бы.</summary>
    private void Announce(string message)
    {
        _host.ShowStatus(message, category: NotificationCategory.FirmwareAndParams);
        _host.PushCatalogChange(message);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
