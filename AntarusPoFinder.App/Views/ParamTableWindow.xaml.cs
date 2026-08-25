using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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

        /// <summary>Имя изменения ровно как у ParamTableDiff.ChangeKind — по нему подсвечивает
        /// DataGrid.RowStyle. Пусто — строка не менялась.</summary>
        public string Change { get; init; } = "";

        public string ChangeMark { get; init; } = "";
    }

    private sealed class RevisionVm
    {
        public ParamTableRevision Source { get; init; } = new();
        public string Header => $"Ревизия {Source.Number}";
        public string Subheader => string.Join(" · ",
            new[] { DateOnly(Source.CreatedAt), Source.Author }.Where(s => s.Length > 0));

        private static string DateOnly(string iso) =>
            iso.Length >= 10 ? iso[..10] : iso;
    }

    public ParamTableWindow(AppServices services, IAppHost host, string diskPath, string filename, string fileLabel)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        _diskPath = diskPath;
        _filename = filename;

        FileLabel.Text = "Файл параметров: " + fileLabel;
        var canEdit = ParamTableEditing.CanEdit(_services.Cfg.CurrentRole());
        EditBtn.IsEnabled = canEdit;
        RenameBtn.IsEnabled = canEdit;
        DeleteBtn.IsEnabled = canEdit;
        ImportBtn.IsEnabled = canEdit;
        if (!canEdit)
        {
            var hint = "Правка таблицы — у программиста и администратора. Здесь можно смотреть и отбирать строки по аппарату.";
            EditBtn.ToolTip = hint;
            ImportBtn.ToolTip = hint;
        }

        LoadDocuments(null);
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
        ReasonText.Text = "У этого файла параметров таблицы ещё нет.";
        AuthorText.Text = ParamTableEditing.CanEdit(_services.Cfg.CurrentRole())
            ? "«Новый документ из файла…» разберёт накопленный txt в таблицу — с предпросмотром и правкой до сохранения."
            : "Завести её может программист или администратор.";
        SummaryText.Text = "";
    }

    private void Document_Changed(object sender, SelectionChangedEventArgs e) => LoadRevisions();

    private void LoadRevisions()
    {
        if (Current?.Id is not int tableId)
        {
            ShowEmpty();
            return;
        }

        _revisions = _services.Db.GetParamTableRevisions(tableId);
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
            $"Ревизия {selected.Source.Number}",
            selected.Source.CreatedAt,
            selected.Source.Author.Length > 0 ? "автор: " + selected.Source.Author : "",
        }.Where(s => s.Length > 0));

        SummaryText.Text = previous is null
            ? selected.Source.Summary.Length > 0 ? selected.Source.Summary : "Первая редакция документа — сравнивать не с чем."
            : $"Изменения относительно ревизии {previous.Number}. " + ParamTableDiff.Describe(_diff!);

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

        RowsGrid.ItemsSource = ordered.Select(row =>
        {
            var change = _diff?.KindOf(row)?.ToString() ?? "";

            return new RowVm
            {
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
                    nameof(ParamTableDiff.ChangeKind.Edited) => "правка",
                    _ => "",
                },
            };
        }).ToList();
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

        var dialog = new ParamTableEditorDialog(_services.Db.GetParamGroups(), Current.Name, _rows) { Owner = this };
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

    private void EditReason_Click(object sender, RoutedEventArgs e)
    {
        if (RevisionsList.SelectedItem is not RevisionVm selected || selected.Source.Id is not int revisionId) return;

        var text = TextPromptDialog.Prompt(this, "Зачем правили",
            $"Ревизия {selected.Source.Number} — одна строка о том, зачем её завели:", selected.Source.Reason);
        if (text is null) return;

        // Правится ТОЛЬКО «зачем». Строки ревизии — снимок: переписать их задним числом значит
        // рассказать про прошлое неправду.
        _services.Db.UpdateParamTableRevisionReason(revisionId, text);
        var at = RevisionsList.SelectedIndex;
        LoadRevisions();
        if (at >= 0 && at < _revisions.Count) RevisionsList.SelectedIndex = at;
        Announce("Поправлено «зачем» у ревизии " + selected.Source.Number);
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
