using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Страница «Паспорта шкафов»: загрузить шаблон паспорта, посмотреть/напечатать уже
/// загруженные, поправить теги. Устроена как «Параметры ПЧ/УПП» (та же карточка загрузки + таблица),
/// потому что сущность та же по природе: документ, привязанный к шкафу, а не к версии прошивки —
/// см. Domain.PassportTemplate.</summary>
public partial class PassportsView : UserControl
{
    private readonly AppServices _services;
    private readonly IAppHost _host;
    private string? _srcPath;

    /// <summary>Временная папка для PDF, собранного из docx, когда сама папка паспорта только на
    /// чтение — своя, чтобы не смешивать с PDF инструкций (см. PrintableDocActions).</summary>
    internal const string PdfTempFolder = "AntarusPassport";

    internal const string EditHint = "«Открыть»";

    private class PassportRow
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public string GroupSubtypeDisplay { get; init; } = "";
        public string Filename { get; init; } = "";
        public string Tags { get; init; } = "";
        public string TagsDisplay => string.IsNullOrWhiteSpace(Tags) ? "—" : Tags;
        public string DateOnly { get; init; } = "";
        public string Description { get; init; } = "";
        public PassportTemplate Source { get; init; } = new();
    }

    public PassportsView(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        Loaded += (_, _) => PopulateCombos();
    }

    /// <summary>Страница живёт в кэше между переходами — справочники надо перечитывать при каждом
    /// возврате, иначе в комбобоксах остаётся состояние на момент первой отрисовки (см.
    /// ParamsView.RefreshIfActive).</summary>
    public void RefreshIfActive() => PopulateCombos();

    private void PopulateCombos()
    {
        var prevGroupId = (GroupCombo.SelectedItem as EquipmentGroup)?.Id;
        var prevSubtypeId = (SubtypeCombo.SelectedItem as EquipmentSubType)?.Id;

        var groups = _services.Db.GetAllEquipmentGroups();
        GroupCombo.ItemsSource = groups;
        // Ничего не выбирается автоматически при первом открытии (-1) — как в UploadView/ParamsView:
        // молчаливый выбор первого типа шкафа делал бы слишком лёгкой загрузку паспорта не туда.
        GroupCombo.SelectedIndex = prevGroupId is not null
            ? Math.Max(0, groups.FindIndex(g => g.Id == prevGroupId))
            : -1;
        PopulateSubtypes(prevSubtypeId);

        var filterGroups = new List<EquipmentGroup> { new() { Id = null, Name = "Все типы" } };
        filterGroups.AddRange(groups);
        FilterGroupCombo.ItemsSource = filterGroups;
        FilterGroupCombo.SelectedIndex = 0;
    }

    private void PopulateSubtypes(int? keepSelectedId = null)
    {
        if (GroupCombo.SelectedItem is not EquipmentGroup group || group.Id is null)
        {
            SubtypeCombo.ItemsSource = null;
            return;
        }
        var subtypes = _services.Db.GetSubtypesForGroup(group.Id.Value);
        SubtypeCombo.ItemsSource = subtypes;
        // Подтип-заглушка «—» у групп без деления единственный: выбирать его вручную нечего и незачем.
        SubtypeCombo.SelectedIndex = subtypes.Count == 1
            ? 0
            : keepSelectedId is not null ? subtypes.FindIndex(s => s.Id == keepSelectedId) : -1;
    }

    private void GroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => PopulateSubtypes();

    // ── Выбор файла ───────────────────────────────────────────────────────────

    private void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбрать документ паспорта",
            Filter = "Документы (*.docx;*.doc;*.pdf)|*.docx;*.doc;*.pdf|Все файлы|*.*",
        };
        if (dlg.ShowDialog() == true) SetFile(dlg.FileName);
    }

    private void SetFile(string path)
    {
        _srcPath = path;
        DropZoneLabel.Text = Path.GetFileName(path);
        // Название по умолчанию — имя файла без расширения: чаще всего оператору достаточно его, а
        // пустое поле означало бы папку «Паспорт» для любого паспорта подряд.
        if (string.IsNullOrWhiteSpace(NameInput.Text))
            NameInput.Text = Path.GetFileNameWithoutExtension(path);
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            SetFile(files[0]);
    }

    // ── Загрузка ──────────────────────────────────────────────────────────────

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_srcPath) || !File.Exists(_srcPath))
        {
            AppMessageBox.Show("Выберите документ паспорта.", "Загрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (GroupCombo.SelectedItem is not EquipmentGroup group)
        {
            AppMessageBox.Show("Выберите тип шкафа.", "Загрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (SubtypeCombo.SelectedItem is not EquipmentSubType subtype || subtype.Id is null)
        {
            AppMessageBox.Show("Выберите подтип шкафа.", "Загрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show("Путь к диску не задан или диск недоступен. Проверьте Настройки.",
                "Загрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var srcPath = _srcPath;
        var name = NameInput.Text.Trim();
        if (name.Length == 0) name = Path.GetFileNameWithoutExtension(srcPath);

        var dstFolder = PassportService.Folder(_services.Hierarchy, root, group.Name, subtype.Name, name);
        var now = DateTime.Now;
        string? archivedPrevious = null;
        try
        {
            UploadBtn.IsEnabled = false;
            using (_host.BeginBusy($"Загрузка паспорта: {Path.GetFileName(srcPath)}"))
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(dstFolder);
                    // Прежняя редакция не затирается, а уезжает в подпапку — то же правило, что у
                    // файлов параметров (ParamFileUploadService): «всегда открывать свежую, а кому
                    // нужна старая — пусть откроет папку».
                    archivedPrevious = ParamFileUploadService.ArchivePreviousOnDisk(dstFolder, Path.GetFileName(srcPath), now);
                    File.Copy(srcPath, Path.Combine(dstFolder, Path.GetFileName(srcPath)), overwrite: true);
                });
        }
        catch (Exception ex)
        {
            AppMessageBox.Show(ex.Message, "Ошибка файла", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            UploadBtn.IsEnabled = true;
        }

        var record = new PassportTemplate
        {
            SubtypeId = subtype.Id,
            Name = name,
            Filename = Path.GetFileName(srcPath),
            DiskPath = dstFolder,
            Description = DescInput.Text.Trim(),
        };
        var outcome = PassportService.SaveRecord(_services.Db, record, archivedPrevious, now);

        _host.ShowStatus($"Паспорт загружен: {name}", category: NotificationCategory.FirmwareAndParams);
        if (outcome.Updated)
            AppMessageBox.Show(archivedPrevious is null
                    ? "Запись обновлена — дата загрузки освежена, изменение записано в описание."
                    : $"Паспорт перезалит. Прежняя редакция убрана в подпапку: «{archivedPrevious}».",
                "Готово", MessageBoxButton.OK, MessageBoxImage.Information);

        DescInput.Text = "";
        NameInput.Text = "";
        _srcPath = null;
        DropZoneLabel.Text = "Перетащите документ сюда, или нажмите для выбора";
        ReloadTable();
    }

    // ── Таблица ───────────────────────────────────────────────────────────────

    private void ShowAllButton_Click(object sender, RoutedEventArgs e)
    {
        var expanding = ListContentPanel.Visibility != Visibility.Visible;
        ListContentPanel.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
        ShowAllButton.Content = expanding ? "Свернуть список" : "Все загруженные";
        var rowActions = expanding ? Visibility.Visible : Visibility.Collapsed;
        OpenFileBtn.Visibility = rowActions;
        PrintBtn.Visibility = rowActions;
        OpenFolderBtn.Visibility = rowActions;
        EditTagsBtn.Visibility = rowActions;
        DeleteRowBtn.Visibility = rowActions;
        if (expanding) ReloadTable();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ReloadTable();
    private void Refresh_Click(object sender, RoutedEventArgs e) => ReloadTable();

    private void ReloadTable()
    {
        if (FilterGroupCombo.SelectedItem is not EquipmentGroup filterGroup) return;
        var searchText = SearchInput.Text.Trim();

        var passports = _services.Db.GetPassports();
        if (filterGroup.Id is not null)
        {
            var subtypeIds = _services.Db.GetSubtypesForGroup(filterGroup.Id.Value).Select(s => s.Id!.Value).ToHashSet();
            passports = passports.Where(p => p.SubtypeId is not null && subtypeIds.Contains(p.SubtypeId.Value)).ToList();
        }
        if (!string.IsNullOrEmpty(searchText))
            passports = passports.Where(p =>
                p.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                p.Filename.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                p.Description.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                p.Tags.Contains(searchText, StringComparison.OrdinalIgnoreCase)).ToList();

        var rows = passports.Select(p => new PassportRow
        {
            Id = p.Id ?? 0,
            Name = p.Name,
            GroupSubtypeDisplay = string.IsNullOrEmpty(p.SubtypeName) || p.SubtypeName == "—"
                ? p.GroupName
                : $"{p.GroupName} / {p.SubtypeName}",
            Filename = p.Filename,
            Tags = p.Tags,
            DateOnly = p.UploadDate.Length >= 10 ? p.UploadDate[..10] : p.UploadDate,
            Description = p.Description,
            Source = p,
        }).ToList();

        FilesGrid.ItemsSource = rows;
        CountLabel.Text = $"Записей: {rows.Count}";
    }

    /// <summary>Двойной клик по строке = «Открыть». Через DataGridClickGuard, иначе двойной клик по
    /// заголовку колонки (сортировка) открывал бы когда-то выделенный документ.</summary>
    private void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!DataGridClickGuard.IsOverDataRow(e)) return;
        OpenFile_Click(sender, e);
    }

    private PassportRow? Selected(string action)
    {
        if (FilesGrid.SelectedItem is PassportRow row) return row;
        AppMessageBox.Show("Выберите строку.", action, MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (Selected("Паспорт") is not { } row) return;
        var doc = PassportService.ResolveDoc(row.Source, _services.Cfg.RootPath());
        // Открываем ИСХОДНИК (docx), если он есть: с этой страницы паспорт обычно открывают, чтобы
        // поправить шаблон; печать — отдельной кнопкой, она сама соберёт свежий PDF.
        var path = doc.Docx ?? doc.Newest;
        if (path is null)
        {
            AppMessageBox.Show($"Файл паспорта не найден:\n{row.Source.DiskPath}",
                "Паспорт", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        PrintableDocActions.Open(path);
    }

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (Selected("Паспорт") is not { } row) return;
        var doc = PassportService.ResolveDoc(row.Source, _services.Cfg.RootPath());
        var pdf = await PrintableDocActions.EnsurePdfAsync(doc, _host, "Паспорт", "паспорта", PdfTempFolder, EditHint);
        if (pdf is null) return;
        PrintableDocActions.Print(pdf);
        _host.ShowStatus($"Паспорт отправлен на печать: {row.Name}");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected("Паспорт") is not { } row) return;
        var folder = FirmwarePathLocalizer.Localize(row.Source.DiskPath, _services.Cfg.RootPath());
        if (!Directory.Exists(folder))
        {
            AppMessageBox.Show($"Папка не найдена:\n{folder}", "Паспорт", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private void EditTags_Click(object sender, RoutedEventArgs e)
    {
        if (Selected("Паспорт") is not { } row) return;
        // Тот же диалог тегов, что у файлов параметров: он правит только строку тегов и сам ведёт
        // справочник тегов, про сущность-владельца ничего не зная.
        var dlg = new EditParamTagsDialog(_services.Db, row.Tags, row.Name, "Паспорт") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        _services.Db.UpdatePassportTags(row.Id, dlg.ResultTags);
        _host.ShowStatus($"Теги обновлены: {row.Name}", category: NotificationCategory.FirmwareAndParams);
        ReloadTable();
    }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (Selected("Паспорт") is not { } row) return;
        var reply = AppMessageBox.Show(
            $"Удалить запись о паспорте «{row.Name}»?\nФайлы на диске НЕ удаляются.",
            "Удалить запись", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        _services.Db.DeletePassport(row.Id);
        ReloadTable();
    }
}
