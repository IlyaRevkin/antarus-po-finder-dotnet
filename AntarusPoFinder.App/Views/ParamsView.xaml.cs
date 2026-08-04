using System;
using System.Collections.Generic;
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

    /// <summary>Все подтипы справочника вместе с именем своего типа шкафа — пул кандидатов и для
    /// чек-комбобокса загрузки, и для диалога правки подтипов у уже загруженного файла.</summary>
    private List<ParamFileLinkService.SubtypeTarget> _subtypeTargets = new();

    private class ParamFileRow
    {
        public int Id { get; init; }
        public int? SubtypeId { get; init; }
        public string Filename { get; init; } = "";
        public string GroupSubtypeDisplay { get; init; } = "";
        public string Manufacturer { get; init; } = "";
        public string Tags { get; init; } = "";
        public string TagsDisplay => string.IsNullOrWhiteSpace(Tags) ? "—" : Tags;
        public string DateOnly { get; init; } = "";
        public string Description { get; init; } = "";
        public string DiskPath { get; init; } = "";

        /// <summary>Исходная запись целиком — диалогу правки подтипов нужны поля, которых в таблице
        /// нет (дата загрузки в полном виде, описание), а перечитывать её из БД по Id ради этого
        /// незачем: список только что оттуда и прочитан.</summary>
        public ParamFile Source { get; init; } = new();
    }

    public ParamsView(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        Loaded += (_, _) => PopulateCombos();
        Loaded += (_, _) => _ = CleanDiskDuplicatesOnceAsync();
    }

    /// <summary>Разовая чистка файлов-двойников «имя (что-то).ext», уже наплодившихся в папках
    /// параметров (см. ParamFileDuplicateCleanup — там же разбор, почему текущий код приложения их
    /// создать не может и откуда они, скорее всего, взялись). Делается один раз на машину, при
    /// первом открытии страницы «Параметры», и только если сетевой диск сейчас доступен — иначе
    /// флаг не ставится и попытка повторится в следующий раз.
    ///
    /// Обход диска уходит в фон, а вся работа с БД остаётся на UI-потоке: Database — одно соединение
    /// SQLite, и лезть в него из двух потоков одновременно нельзя.</summary>
    private async Task CleanDiskDuplicatesOnceAsync()
    {
        if (_duplicateCleanupStarted) return;
        _duplicateCleanupStarted = true;
        if (_services.Db.GetSetting(ParamFileDuplicateCleanup.DoneFlagKey) == "true") return;

        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;

        var targets = ParamFileDuplicateCleanup.Targets(_services.Db);
        if (targets.Count == 0)
        {
            _services.Db.SetSetting(ParamFileDuplicateCleanup.DoneFlagKey, "true");
            return;
        }

        var paramsRoot = Path.Combine(root, "Параметры");
        ParamFileDuplicateCleanup.Result result;
        try
        {
            result = await Task.Run(() => ParamFileDuplicateCleanup.CleanFolders(targets, paramsRoot));
        }
        catch
        {
            // Диск отвалился посреди обхода — флаг не ставим, попробуем в следующий раз.
            return;
        }

        ParamFileDuplicateCleanup.ArchiveRemovedRows(_services.Db, result.Removed);
        _services.Db.SetSetting(ParamFileDuplicateCleanup.DoneFlagKey, "true");

        if (result.Removed.Count > 0)
        {
            _host.ShowStatus($"Убраны дубликаты файлов параметров: {result.Removed.Count}",
                category: NotificationCategory.FirmwareAndParams);
            ReloadTable();
        }
        // Всё сомнительное — не удалено, а показано человеку: решать ему.
        if (result.Skipped.Count > 0)
            _host.ShowStatus($"Похожие на дубликаты файлы параметров оставлены как есть ({result.Skipped.Count}): "
                + string.Join("; ", result.Skipped.Take(3)), category: NotificationCategory.FirmwareAndParams);
    }

    private bool _duplicateCleanupStarted;

    /// <summary>Та же чистка, но по кнопке и с отчётом: сколько прежних редакций убрано в подпапку,
    /// сколько точных копий удалено, что оставлено «на разбор» и — отдельно — сколько записей
    /// ссылается на файл, которого на диске уже нет.
    ///
    /// Висячие записи здесь только ПОКАЗЫВАЮТСЯ, а не удаляются: ровно так же выглядит временно
    /// отвалившаяся шара, а удалённая запись уедет тумбстоуном ко всем коллегам — цена ошибки
    /// несоразмерна пользе от автоматизма. Убрать их можно построчно, кнопкой «Удалить запись».</summary>
    private async void Tidy_Click(object sender, RoutedEventArgs e)
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show("Сетевой диск сейчас недоступен — чистку папок делать не по чему.",
                "Параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var targets = ParamFileDuplicateCleanup.Targets(_services.Db);
        var rows = _services.Db.GetParamFiles();
        var paramsRoot = Path.Combine(root, "Параметры");

        ParamFileDuplicateCleanup.Result result;
        List<string> missing;
        try
        {
            TidyBtn.IsEnabled = false;
            using (_host.BeginBusy("Проверка папок параметров"))
            {
                result = await Task.Run(() => ParamFileDuplicateCleanup.CleanFolders(targets, paramsRoot));
                missing = await Task.Run(() => rows
                    .Where(r => !string.IsNullOrWhiteSpace(r.DiskPath) && !string.IsNullOrWhiteSpace(r.Filename))
                    .Where(r => !File.Exists(Path.Combine(r.DiskPath, r.Filename)))
                    .Select(r => Path.Combine(r.DiskPath, r.Filename))
                    .Distinct()
                    .ToList());
            }
        }
        catch (Exception ex)
        {
            AppMessageBox.Show(ex.Message, "Параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            TidyBtn.IsEnabled = true;
        }

        ParamFileDuplicateCleanup.ArchiveRemovedRows(_services.Db, result.Removed);
        ReloadTable();

        var report = new List<string>();
        report.Add(result.Tidied.Count > 0
            ? $"Прежних редакций убрано в подпапку «{ParamFileUploadService.ArchiveFolderName}»: {result.Tidied.Count}"
            : "Прежних редакций, лежащих не на своём месте, не нашлось.");
        if (result.Removed.Count > 0) report.Add($"Удалено точных копий-двойников: {result.Removed.Count}");
        if (missing.Count > 0)
            report.Add($"Записей, за которыми на диске нет файла: {missing.Count}. Они не удалены — проверьте и уберите кнопкой «Удалить запись»:"
                + Environment.NewLine + string.Join(Environment.NewLine, missing.Take(10)));
        if (result.Skipped.Count > 0)
            report.Add("Оставлено как есть (отличается от актуального файла либо не прочиталось):"
                + Environment.NewLine + string.Join(Environment.NewLine, result.Skipped.Take(10)));

        AppMessageBox.Show(string.Join(Environment.NewLine + Environment.NewLine, report),
            "Параметры", MessageBoxButton.OK, MessageBoxImage.Information);
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

        // Подтипы — из ВСЕХ типов шкафа сразу, а не только из выбранного: один файл параметров
        // частотника подходит нескольким типам (у прошивки ПЛК так не бывает, поэтому в «Загрузке ПО»
        // список остался в пределах группы). Показывается при этом всё равно выбранный тип — сужением
        // (SetGroupFilter), а не пересборкой списка, иначе отметка подтипа другого типа терялась бы
        // при первом же переключении «Тип шкафа». Остальные типы открывает галочка внутри списка.
        var groupNames = groups.ToDictionary(g => g.Id ?? 0, g => g.Name);
        _subtypeTargets = _services.Db.GetAllEquipmentSubtypes()
            .Where(s => s.Id is not null)
            .Select(s => new ParamFileLinkService.SubtypeTarget(s,
                groupNames.TryGetValue(s.GroupId, out var name) ? name : ""))
            .ToList();

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
        OpenFileBtn.Visibility = rowActionsVisibility;
        TidyBtn.Visibility = rowActionsVisibility;
        OpenFolderBtn.Visibility = rowActionsVisibility;
        EditTagsBtn.Visibility = rowActionsVisibility;
        EditSubtypesBtn.Visibility = rowActionsVisibility;
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
        SubtypesSelect.SetItems(_subtypeTargets.Select(t => t.Subtype),
            groupNamesBySubtypeId: _subtypeTargets.ToDictionary(t => t.Id, t => t.GroupName));
        ApplySubtypeGroupFilter();
    }

    /// <summary>Сужает список подтипов до выбранного типа шкафа — сам набор кандидатов при этом не
    /// меняется, поэтому уже отмеченные подтипы других типов остаются отмеченными и видимыми
    /// (см. SubtypeMultiSelect.SetGroupFilter).</summary>
    private void ApplySubtypeGroupFilter() =>
        SubtypesSelect.SetGroupFilter((GroupCombo.SelectedItem as EquipmentGroup)?.Id);

    private void GroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplySubtypeGroupFilter();

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
        // Тип шкафа берётся у ОСНОВНОГО подтипа, а не из GroupCombo: отмеченным основным может
        // оказаться подтип другого типа (список охватывает все типы), и тогда файл ушёл бы в папку
        // чужого типа. GroupCombo — только сужение списка, не адрес на диске.
        var primaryTarget = _subtypeTargets.FirstOrDefault(t => t.Id == subtype.Id);
        if (primaryTarget is null) return;

        var dstFolder = _services.Hierarchy.ParamsPath(root, primaryTarget.GroupName, subtype.Name, manuf);
        var srcPath = _srcPath;
        var now = DateTime.Now;
        // Перезаливка под тем же именем больше НЕ затирает прежний файл: он уезжает в подпапку
        // «Прежние редакции» под именем «имя (до ГГГГ-ММ-ДД).ext», а новый ложится под исходным
        // именем — «Открыть» всегда ведёт на свежий, за старым идут через «Открыть папку»
        // (см. ParamFileUploadService).
        string? archivedPrevious = null;
        try
        {
            UploadBtn.IsEnabled = false;
            using (_host.BeginBusy($"Загрузка параметров: {Path.GetFileName(srcPath)}"))
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(dstFolder);
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

        var record = new ParamFile
        {
            SubtypeId = subtype.Id,
            Manufacturer = manuf,
            Filename = Path.GetFileName(srcPath),
            DiskPath = dstFolder,
            Description = DescInput.Text.Trim(),
        };
        // Заводит новую запись либо ОБНОВЛЯЕТ существующую (свежая дата + дописанная строка-лог в
        // описании) — раньше перезаливка всегда делала новый INSERT, и таблица обрастала дублями.
        var outcome = ParamFileUploadService.SaveRecord(_services.Db, record, archivedPrevious, now);

        var extraIds = SubtypesSelect.ExtraSubtypes.Select(s => s.Id ?? 0).ToHashSet();
        var link = ParamFileLinkService.LinkToExtraSubtypes(_services.Db, _services.Hierarchy, root,
            subtype.Id!.Value, record, _subtypeTargets.Where(t => extraIds.Contains(t.Id)),
            new Services.ShortcutCreator());

        _host.ShowStatus($"Параметры загружены: {Path.GetFileName(srcPath)}", category: NotificationCategory.FirmwareAndParams);
        var notes = new List<string>();
        if (outcome.Updated)
            notes.Add(archivedPrevious is null
                ? "Запись обновлена — дата загрузки освежена, изменение записано в описание."
                : $"Файл перезалит. Прежняя редакция убрана в подпапку: «{archivedPrevious}».");
        if (link.CreatedIds.Count > 0)
            notes.Add($"Тот же файл добавлен ещё для {link.CreatedIds.Count} подтип(ов) — ярлыком, без копирования.");
        if (link.Warnings.Count > 0)
            notes.Add("Предупреждения:\n" + string.Join("\n", link.Warnings));
        if (notes.Count > 0)
            AppMessageBox.Show(string.Join("\n\n", notes), "Готово", MessageBoxButton.OK, MessageBoxImage.Information);

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
            SubtypeId = f.SubtypeId,
            Filename = f.Filename,
            GroupSubtypeDisplay = string.IsNullOrEmpty(f.SubtypeName) || f.SubtypeName == "—"
                ? f.GroupName
                : $"{f.GroupName} / {f.SubtypeName}",
            Manufacturer = f.Manufacturer,
            Tags = f.Tags,
            DateOnly = f.UploadDate.Length >= 10 ? f.UploadDate[..10] : f.UploadDate,
            Description = f.Description,
            DiskPath = f.DiskPath,
            Source = f,
        }).ToList();

        FilesGrid.ItemsSource = rows;
        CountLabel.Text = $"Записей: {rows.Count}";
    }

    /// <summary>Двойной клик по строке = «Открыть файл». Через DataGridClickGuard, иначе двойной
    /// клик по заголовку колонки (сортировка) открывал бы файл, выделенный когда-то раньше.</summary>
    private void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!DataGridClickGuard.IsOverDataRow(e)) return;
        OpenSelectedFile();
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e) => OpenSelectedFile();

    /// <summary>Файла может не быть на месте (запись есть, а на диске уже нет — типовая ситуация
    /// после ручной чистки папки): тогда открываем хотя бы папку, как это делает то же действие в
    /// окне параметров карточки, и только если и её нет — сообщаем.</summary>
    private void OpenSelectedFile()
    {
        if (FilesGrid.SelectedItem is not ParamFileRow row)
        {
            AppMessageBox.Show("Выберите строку.", "Параметры", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var full = Path.Combine(row.DiskPath, row.Filename);
        if (File.Exists(full)) Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
        else if (Directory.Exists(row.DiskPath)) Process.Start(new ProcessStartInfo(row.DiskPath) { UseShellExecute = true });
        else AppMessageBox.Show($"Файл не найден:\n{full}", "Параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    /// <summary>Правка набора подтипов у уже загруженного файла — те же операции, что при загрузке
    /// (запись + ярлык на общий файл), только применяются к существующей записи.</summary>
    private void EditSubtypes_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not ParamFileRow row)
        {
            AppMessageBox.Show("Выберите строку.", "Параметры", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (row.SubtypeId is null || string.IsNullOrWhiteSpace(row.DiskPath))
        {
            AppMessageBox.Show("У этой записи нет подтипа или папки на диске — привязывать нечего.",
                "Подтипы", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new EditParamSubtypesDialog(_services, row.Source, row.Filename) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        var result = dlg.Result;
        if (result is not null)
        {
            var parts = new List<string>();
            if (result.Added.Count > 0) parts.Add("добавлено: " + string.Join(", ", result.Added));
            if (result.Removed.Count > 0) parts.Add("убрано: " + string.Join(", ", result.Removed));
            if (parts.Count > 0)
                _host.ShowStatus($"Подтипы файла {row.Filename} — {string.Join("; ", parts)}",
                    category: NotificationCategory.FirmwareAndParams);
            if (result.Warnings.Count > 0)
                AppMessageBox.Show(string.Join("\n", result.Warnings), "Предупреждения",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
