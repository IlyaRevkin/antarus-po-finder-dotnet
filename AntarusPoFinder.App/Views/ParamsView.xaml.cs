using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

using AntarusPoFinder.App;

namespace AntarusPoFinder.App.Views;

public partial class ParamsView : UserControl
{
    private readonly AppServices _services;
    private readonly IAppHost _host;
    private string? _srcPath;

    private class ParamFileRow
    {
        public int Id { get; init; }
        public string Filename { get; init; } = "";
        public string GroupSubtypeDisplay { get; init; } = "";
        public string Manufacturer { get; init; } = "";
        public string Tags { get; init; } = "";
        public string TagsDisplay => string.IsNullOrWhiteSpace(Tags) ? "—" : Tags;
        public string DateOnly { get; init; } = "";
        public string Description { get; init; } = "";
        public string DiskPath { get; init; } = "";
    }

    public ParamsView(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        Loaded += (_, _) => PopulateCombos();
    }

    /// <summary>Страница живёт в кэше между переходами (MainWindowViewModel._pageCache), поэтому
    /// справочники в комбобоксах — те, что были на момент её первой отрисовки. Всё, что поменялось
    /// потом (в Настройках или прилетело синхронизацией с другой машины), до неё не доезжало: новый
    /// производитель не появлялся, удалённый тип шкафа продолжал предлагаться.</summary>
    public void RefreshIfActive() => PopulateCombos();

    private void PopulateCombos()
    {
        var prevGroupId = (GroupCombo.SelectedItem as EquipmentGroup)?.Id;
        var prevManuf = ManufCombo.SelectedItem as string;

        var groups = _services.Db.GetAllEquipmentGroups();
        var manufs = _services.Db.GetParamManufacturers();

        // Ничего не выбирается автоматически при первом открытии страницы (-1), как и в
        // UploadView.ReloadCombos — молчаливый выбор первого типа/подтипа/производителя делал
        // слишком лёгкой загрузку файла параметров не в тот шкаф незаметно для оператора. При
        // повторном заходе на страницу (RefreshIfActive) прежний выбор восстанавливается, если он
        // всё ещё валиден — иначе он бы сбрасывался на каждый переход между вкладками.
        GroupCombo.ItemsSource = groups;
        GroupCombo.SelectedIndex = prevGroupId is not null
            ? Math.Max(0, groups.FindIndex(g => g.Id == prevGroupId))
            : -1;
        PopulateSubtypes();

        ManufCombo.ItemsSource = manufs;
        ManufCombo.SelectedIndex = prevManuf is not null
            ? Math.Max(0, manufs.FindIndex(m => m == prevManuf))
            : -1;

        var filterGroups = new System.Collections.Generic.List<EquipmentGroup> { new() { Id = null, Name = "Все группы" } };
        filterGroups.AddRange(groups);
        FilterGroupCombo.ItemsSource = filterGroups;
        FilterGroupCombo.SelectedIndex = 0;

        var filterManufs = new System.Collections.Generic.List<string> { "Все производители" };
        filterManufs.AddRange(manufs);
        FilterManufCombo.ItemsSource = filterManufs;
        FilterManufCombo.SelectedIndex = 0;
    }

    private void ShowAllButton_Click(object sender, RoutedEventArgs e)
    {
        var expanding = ListContentPanel.Visibility != Visibility.Visible;
        ListContentPanel.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
        ShowAllButton.Content = expanding ? "Свернуть список" : "Все загруженные";
        // Row actions need the table actually open to act on a selection — collapsed together with
        // it, not just left dangling above a hidden grid (see the XAML comment on this StackPanel).
        var rowActionsVisibility = expanding ? Visibility.Visible : Visibility.Collapsed;
        OpenFolderBtn.Visibility = rowActionsVisibility;
        EditTagsBtn.Visibility = rowActionsVisibility;
        DeleteRowBtn.Visibility = rowActionsVisibility;
        if (expanding) ReloadTable();
    }

    /// <summary>Наполняет единый чек-комбобокс подтипов (SubtypesSelect) под текущую группу — как и
    /// в UploadView, раньше это был отдельный ComboBox (основной подтип) плюс SetItems с исключённым
    /// основным для второго контрола, теперь один SetItems на полный список. Текущая отметка
    /// сохраняется по валидности ID (см. SubtypeMultiSelect.SetItems) — первый подтип группы больше
    /// НЕ отмечается автоматически (раньше отмечался, чтобы форма была готова к загрузке без лишнего
    /// клика, но это же незаметно позволяло загрузить файл не в тот подтип): по умолчанию ничего не
    /// выбрано, как и у GroupCombo/ManufCombo (см. PopulateCombos), оператор выбирает подтип явно.</summary>
    private void PopulateSubtypes()
    {
        if (GroupCombo.SelectedItem is not EquipmentGroup group)
        {
            SubtypesSelect.SetItems(Enumerable.Empty<EquipmentSubType>());
            return;
        }
        var subtypes = _services.Db.GetSubtypesForGroup(group.Id!.Value);
        SubtypesSelect.SetItems(subtypes);
    }

    private void GroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => PopulateSubtypes();

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Выбрать файл параметров" };
        if (dlg.ShowDialog() == true)
            SetFile(dlg.FileName);
    }

    /// <summary>Клик по самой drag&amp;drop-зоне — то же самое, что нажать кнопку выбора файла (её
    /// саму убрали, см. XAML) — тот же приём, что DropZone_Click в UploadView.</summary>
    private void DropZone_Click(object sender, MouseButtonEventArgs e) => BrowseFile_Click(sender, e);

    private void SetFile(string path)
    {
        _srcPath = path;
        DropZoneLabel.Text = Path.GetFileName(path);
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

    /// <summary>Копирование на сетевой диск — в фоновом потоке, с индикатором внизу окна: файлы
    /// параметров бывают увесистые, а шара компании регулярно отвечает через раз.</summary>
    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_srcPath) || !File.Exists(_srcPath))
        {
            AppMessageBox.Show("Выберите файл параметров.", "Загрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (SubtypesSelect.MainSubtype is not EquipmentSubType subtype)
        {
            AppMessageBox.Show("Выберите хотя бы один подтип шкафа.", "Загрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var manuf = (ManufCombo.SelectedItem as string)?.Trim();
        if (string.IsNullOrEmpty(manuf))
        {
            AppMessageBox.Show("Выберите производителя.", "Загрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show("Путь к диску не задан. Проверьте Настройки.", "Загрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (GroupCombo.SelectedItem is not EquipmentGroup group) return;

        var dstFolder = _services.Hierarchy.ParamsPath(root, group.Name, subtype.Name, manuf);
        var srcPath = _srcPath;
        try
        {
            UploadBtn.IsEnabled = false;
            using (_host.BeginBusy($"Загрузка параметров: {Path.GetFileName(srcPath)}"))
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(dstFolder);
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

        var record = new ParamFile
        {
            SubtypeId = subtype.Id,
            Manufacturer = manuf,
            Filename = Path.GetFileName(_srcPath),
            DiskPath = dstFolder,
            Description = DescInput.Text.Trim(),
            UploadDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        _services.Db.AddParamFile(record);

        var link = ParamFileLinkService.LinkToExtraSubtypes(_services.Db, _services.Hierarchy, root,
            group, subtype, record, SubtypesSelect.ExtraSubtypes, new Services.ShortcutCreator());

        _host.ShowStatus($"Параметры загружены: {Path.GetFileName(_srcPath)}", category: NotificationCategory.FirmwareAndParams);
        if (link.CreatedIds.Count > 0 || link.Warnings.Count > 0)
        {
            var msg = link.CreatedIds.Count > 0
                ? $"Тот же файл добавлен ещё для {link.CreatedIds.Count} подтип(ов) — ярлыком, без копирования."
                : "";
            if (link.Warnings.Count > 0)
                msg += (msg.Length > 0 ? "\n\nПредупреждения:\n" : "Предупреждения:\n") + string.Join("\n", link.Warnings);
            AppMessageBox.Show(msg, "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        DescInput.Text = "";
        _srcPath = null;
        // Основной подтип оставляем как есть — как правило дальше грузят следующий файл в тот же
        // шкаф/подтип; дополнительные почти всегда другие или их вовсе нет у следующего файла.
        SubtypesSelect.ClearExtras();
        DropZoneLabel.Text = "Перетащите файл сюда, или нажмите для выбора";

        ReloadTable();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ReloadTable();
    private void Refresh_Click(object sender, RoutedEventArgs e) => ReloadTable();

    private void ReloadTable()
    {
        if (FilterGroupCombo.SelectedItem is not EquipmentGroup filterGroup) return;
        var manufFilter = FilterManufCombo.SelectedIndex > 0 ? FilterManufCombo.SelectedItem as string : null;
        var searchText = SearchInput.Text.Trim();

        System.Collections.Generic.List<int>? subtypeIds = null;
        if (filterGroup.Id is not null)
            subtypeIds = _services.Db.GetSubtypesForGroup(filterGroup.Id.Value).Select(s => s.Id!.Value).ToList();

        var files = _services.Db.GetParamFiles(manufacturer: manufFilter);
        if (subtypeIds is not null)
            files = files.Where(f => f.SubtypeId is not null && subtypeIds.Contains(f.SubtypeId.Value)).ToList();
        if (!string.IsNullOrEmpty(searchText))
            files = files.Where(f =>
                f.Filename.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                f.Description.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                f.Tags.Contains(searchText, StringComparison.OrdinalIgnoreCase)).ToList();

        var rows = files.Select(f => new ParamFileRow
        {
            Id = f.Id ?? 0,
            Filename = f.Filename,
            GroupSubtypeDisplay = string.IsNullOrEmpty(f.SubtypeName) || f.SubtypeName == "—"
                ? f.GroupName
                : $"{f.GroupName} / {f.SubtypeName}",
            Manufacturer = f.Manufacturer,
            Tags = f.Tags,
            DateOnly = f.UploadDate.Length >= 10 ? f.UploadDate[..10] : f.UploadDate,
            Description = f.Description,
            DiskPath = f.DiskPath,
        }).ToList();

        FilesGrid.ItemsSource = rows;
        CountLabel.Text = $"Записей: {rows.Count}";
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not ParamFileRow row)
        {
            AppMessageBox.Show("Выберите строку.", "Параметры", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!Directory.Exists(row.DiskPath))
        {
            AppMessageBox.Show($"Папка не найдена:\n{row.DiskPath}", "Параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo(row.DiskPath) { UseShellExecute = true });
    }

    private void EditTags_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not ParamFileRow row)
        {
            AppMessageBox.Show("Выберите строку.", "Параметры", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var file = new ParamFile { Id = row.Id, Tags = row.Tags };
        var dlg = new EditParamTagsDialog(_services.Db, file, row.Filename) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        _services.Db.UpdateParamFileTags(row.Id, dlg.ResultTags);
        _host.ShowStatus($"Теги обновлены: {row.Filename}", category: NotificationCategory.FirmwareAndParams);
        ReloadTable();
    }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not ParamFileRow row)
        {
            AppMessageBox.Show("Выберите строку.", "Параметры", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var result = AppMessageBox.Show(
            $"Удалить запись о файле «{row.Filename}»?\nФайл на диске НЕ удаляется.",
            "Удалить запись", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        _services.Db.DeleteParamFile(row.Id);
        ReloadTable();
    }
}
