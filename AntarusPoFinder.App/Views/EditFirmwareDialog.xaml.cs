using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Loader;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

public partial class EditFirmwareDialog : Window
{
    private const string NoExecutableText = "— первый подходящий файл в папке —";

    private readonly AppServices _services;
    private readonly Database _db;
    private readonly FwVersionRecord _record;

    /// <summary>Человекопонятное имя прошивки — «ПЖ SMH5 2.0.0042.0003», как его собирает вызывающая
    /// сторона (тип шкафа + подтип + контроллер + номер). В сообщениях о правках стоит именно оно, а
    /// не голый VersionRaw: по жалобе «пишет номер прошивки, а не человекопонятное название» — по
    /// «2.0.0042.0003» в уведомлении невозможно понять, о каком шкафе речь. Из самой записи это имя
    /// не собрать: GetFwVersionById читает только столбцы fw_versions, названия типа/подтипа/
    /// контроллера лежат в других таблицах и в записи пустые.</summary>
    private readonly string _title;

    private readonly LaunchTypeChecks _checks;

    private readonly string? _plcFolder;
    private readonly string? _hmiFolder;
    private string _plcHint = "";
    private string _hmiHint = "";

    private readonly FilePickerRow _ioMapPicker;
    private readonly FilePickerRow _instrPicker;
    private readonly FilePickerRow _modbusPicker;
    private readonly FilePickerRow _hmiPicker;
    private readonly FilePickerRow _plcFilePicker;
    private readonly FilePickerRow _pslFilePicker;

    /// <summary>True — версия относится к Segnetics (см. SegneticsProject.IsRelevant): тогда файл
    /// прошивки разведён на «Прошивка ПЛК (.lfs)» + отдельный «Исходник (.psl)». У остальных одно
    /// поле «Файл прошивки», а поле .psl скрыто и в запрос не попадает.</summary>
    private bool _isSegnetics;

    public string ResultDescription { get; private set; } = "";
    public string ResultTags { get; private set; } = "";
    public List<string> ResultLaunchTypes { get; private set; } = new();
    /// <summary>Null when the HMI executable picker wasn't shown (no HMI folder for this version) —
    /// UpdateFwVersion treats null as "leave unchanged", same as the other optional params, so this
    /// dialog never blanks out an existing hint for firmware that doesn't have this panel at all.</summary>
    public string? ResultHmiExecutableHint { get; private set; }
    /// <summary>То же самое для исполняемого файла прошивки ПЛК (FwVersionRecord.ExecutableHint) —
    /// его раньше вообще нельзя было переназначить после загрузки.</summary>
    public string? ResultExecutableHint { get; private set; }

    /// <summary>Результат догрузки доп. файлов — применяется сразу в Save_Click (в отличие от
    /// описания/тегов, которые вызывающий код пишет сам через UpdateFwVersion): копирование файлов
    /// на диск не влезает в контракт «диалог только собирает значения». Вызывающему остаётся только
    /// показать это пользователю.</summary>
    public FirmwareAttachmentsResult? AttachmentsResult { get; private set; }

    /// <summary>Что изменилось в наборе подтипов шкафов — по той же причине, что и AttachmentsResult,
    /// применяется прямо здесь (заведение записей и ярлыков на диске), а вызывающему остаётся только
    /// показать итог. Null, если блок подтипов вообще не показывался.</summary>
    public FirmwareSubtypeLinkService.ApplyResult? SubtypeLinkResult { get; private set; }

    /// <summary>Что изменилось в наборе КОНФИГУРАЦИЙ шкафа — по той же причине, что и SubtypeLinkResult
    /// выше: заведение/удаление строк применяется прямо здесь, вызывающему остаётся показать итог.
    /// Null, если блок конфигураций не показывался (у версии нет папки на диске — вариантам нечего
    /// делить).</summary>
    public FirmwareConfigService.ApplyResult? ConfigResult { get; private set; }

    public EditFirmwareDialog(AppServices services, FwVersionRecord v, string title)
    {
        InitializeComponent();
        _services = services;
        _db = services.Db;
        _record = v;
        _title = title;
        TitleLabel.Text = $"Модерация прошивки: {title}";
        DescriptionInput.Text = v.Description;
        TagsEditor.Configure(AntarusPoFinder.Core.Services.TagString.Parse(v.Tags), () => _db.GetAllTags());

        _checks = new LaunchTypeChecks(LaunchTypesPanel, v.LaunchTypes);

        // Позволяет (пере)выбрать, какой файл внутри загруженной папки открывается по кнопкам карточки
        // — например, при загрузке в папке не было файла с узнаваемым расширением и выбрался не тот
        // (или вообще никакой), либо структура проекта с тех пор изменилась. Строка показывается
        // только если на диске реально лежит ПАПКА: для одиночного файла выбирать нечего.
        if (!string.IsNullOrEmpty(v.DiskPath) && Directory.Exists(v.DiskPath))
        {
            _plcFolder = v.DiskPath;
            _plcHint = ExecutableHintResolver.Normalize(v.ExecutableHint) ?? "";
            PlcExecutableRow.Visibility = Visibility.Visible;
            ExecutablesPanel.Visibility = Visibility.Visible;
        }
        // HMI-файл живёт либо в отдельной папке HMI-проекта (чекбокс «Добавить HMI-проект» при
        // загрузке), либо прямо в папке версии рядом с прошивкой ПЛК — так устроены не только KINCO,
        // а любой проект, где ПЛК и панель собираются в одну папку. Раньше во втором случае строка
        // просто не показывалась, и назначить HMI-файл было нечем — кнопка «Открыть HMI проект»
        // могла опираться только на захардкоженный список KINCO-расширений.
        if (!string.IsNullOrEmpty(v.HmiPath) && Directory.Exists(v.HmiPath))
            _hmiFolder = v.HmiPath;
        else if (_plcFolder is not null)
        {
            _hmiFolder = _plcFolder;
            HmiExecutableLabel.Text = "HMI в папке:";
            // Два разных случая с одинаковым поведением поля, но разным смыслом для модератора:
            // отдельной папки проекта нет вовсе — либо она есть, но записан ОДИН файл (так проекты
            // складывались раньше, и у .fsprj такой файл открывается пустым). Во втором случае
            // выбранный здесь файл ПЕРЕБИВАЕТ записанный путь (см. HmiOpenResolver) — то есть это
            // рабочий способ починки, когда проект панели лежит в самой папке версии; если же он
            // лежал в своей папке, чинить надо полем «HMI-проект» выше, забрав папку целиком.
            HmiExecutableLabel.ToolTip = !string.IsNullOrEmpty(v.HmiPath)
                ? "У этой версии проект панели сохранён на диске ОДНИМ файлом — среда откроет его " +
                  "пустым. Указанный здесь файл из папки версии будет открываться вместо него. Если " +
                  "проект лежит отдельной папкой — выберите его заново полем «HMI-проект» выше, " +
                  "тогда программа заберёт всю папку целиком."
                : "Отдельной папки HMI-проекта у этой версии нет — файл панели можно указать прямо в " +
                  "папке прошивки (в т.ч. во вложенной), тогда в поиске появится кнопка «Открыть HMI проект».";
        }
        if (_hmiFolder is not null)
        {
            _hmiHint = ExecutableHintResolver.Normalize(v.HmiExecutableHint) ?? "";
            HmiExecutableRow.Visibility = Visibility.Visible;
            ExecutablesPanel.Visibility = Visibility.Visible;
        }
        RefreshExecutableTexts();

        // Все диалоги модерации открываются на СЕРВЕРЕ, но каждый — в СВОЕЙ папке (см.
        // SlotStartDirectory): выбирая HMI, модератор попадал в папку прошивки, хотя у версии есть
        // своя «HMI\», и до неё каждый раз приходилось идти руками.
        _ioMapPicker = new FilePickerRow(p => IoMapInput.Text = p, () => IoMapInput.Text = "", folderDialogTitle: "Выбрать папку",
            initialDirectory: () => SlotStartDirectory(HierarchyFolders.IoMap));
        _instrPicker = new FilePickerRow(p => InstructionsInput.Text = p, () => InstructionsInput.Text = "", folderDialogTitle: "Выбрать папку",
            initialDirectory: () => SlotStartDirectory(HierarchyFolders.Instructions));
        _modbusPicker = new FilePickerRow(p => ModbusMapInput.Text = p, () => ModbusMapInput.Text = "", folderDialogTitle: "Выбрать папку",
            initialDirectory: () => SlotStartDirectory(HierarchyFolders.Modbus));
        _hmiPicker = new FilePickerRow(p => { WarnIfHmiSelectionIsDoomed(p); HmiInput.Text = p; }, () => HmiInput.Text = "",
            fileDialogTitle: "Выбрать файл HMI-проекта",
            fileDialogFilter: "HMI-проект (*.fsprj)|*.fsprj|Все файлы (*.*)|*.*",
            folderDialogTitle: "Выбрать папку HMI-проекта",
            initialDirectory: () => SlotStartDirectory(HierarchyFolders.Hmi));
        // Только файл (папку класть в саму папку версии нельзя — она общая для файлов прошивки);
        // фильтр по .lfs/.psl, но с «Все файлы» на случай другого расширения прошивки.
        _plcFilePicker = new FilePickerRow(p => PlcFileInput.Text = p, () => PlcFileInput.Text = "",
            fileDialogTitle: "Выбрать файл прошивки ПЛК",
            fileDialogFilter: "Прошивка ПЛК (*.lfs;*.psl)|*.lfs;*.psl|Все файлы (*.*)|*.*",
            initialDirectory: FirmwareStartDirectory);
        _pslFilePicker = new FilePickerRow(p => PslFileInput.Text = p, () => PslFileInput.Text = "",
            fileDialogTitle: "Выбрать исходник прошивки (.psl)",
            fileDialogFilter: "Исходник Segnetics (*.psl)|*.psl|Все файлы (*.*)|*.*",
            initialDirectory: FirmwareStartDirectory);

        // Блок доп. файлов имеет смысл только когда известно, куда их класть: нужны имена группы/
        // подтипа/контроллера (в записи из поиска их нет — доносим из БД) и доступный сетевой диск.
        if (v.Id is not null)
        {
            var names = _db.GetFwVersionNames(v.Id.Value);
            if (names is not null && !string.IsNullOrEmpty(_services.Cfg.RootPath()))
            {
                _names = names.Value;
                IoMapInput.Text = v.IoMapPath;
                ModbusMapInput.Text = v.ModbusMapPath;
                InstructionsInput.Text = v.InstructionsPath;
                HmiInput.Text = v.HmiPath;
                AttachmentsPanel.Visibility = Visibility.Visible;
                BuildSubtypeChecks();
                BuildConfigs();
            }
            LoadSearchWeights(v.Id.Value);
        }

        ConfigureFirmwareFileFields();
    }

    /// <summary>С какой папки открывать диалоги выбора файлов в модерации — папка версии НА СЕРВЕРЕ.
    /// Модератор выбирает инструкцию, карты и файл панели из того, что лежит рядом с прошивкой, а
    /// системный диалог по умолчанию открывался в последней локальной папке этой машины. Берём
    /// реальную папку сборки, а не записанный disk_path вслепую: её могли переименовать или перезалить
    /// под другой датой (см. FirmwareDiskPresence.ResolveVersionDir). Не нашли — папка контроллера,
    /// а нет и её — пустая строка, тогда диалог откроется как раньше.</summary>
    private string? ServerStartDirectory()
    {
        if (string.IsNullOrEmpty(_record.DiskPath)) return null;
        return FirmwareDiskPresence.ResolveVersionDir(_record.DiskPath, _record.VersionRaw)
            ?? Path.GetDirectoryName(_record.DiskPath);
    }

    /// <summary>Выбран проект-папка, который придётся копировать одним файлом — открывать его потом
    /// будет нечем (см. HmiProjectFormat.SelectionWarning). Не запрет, а предупреждение: модератор
    /// может знать, что делает, но узнать об этом он должен сейчас, а не от наладчика через неделю.</summary>
    private static void WarnIfHmiSelectionIsDoomed(string path)
    {
        if (HmiProjectFormat.SelectionWarning(path) is { } warning)
            AppMessageBox.Show(warning, "HMI-проект", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>С какой папки открывать диалог КОНКРЕТНОГО вложения. Одной «папки версии» на все поля
    /// не хватает: у перестроенной версии документы лежат каждый в своей подпапке, и диалог выбора
    /// HMI открывался в папке прошивки — «указал, что HMI хранится в папке HMI, а открывается папка
    /// прошивки». Спрашиваем ту же папку, из которой этот документ ЧИТАЕТСЯ (VersionLayout), поэтому у
    /// не переехавшей версии это по-прежнему общая папка контроллера, а не пустая папка внутри версии.
    /// Ничего не нашли — прежнее поведение, папка версии.</summary>
    private string? SlotStartDirectory(string slot)
    {
        var versionDir = ServerStartDirectory();
        if (string.IsNullOrEmpty(versionDir)) return versionDir;
        return VersionLayout.SlotBestReadFolder(versionDir, VersionLayout.ControllerFolderOf(versionDir), slot)
               ?? versionDir;
    }

    /// <summary>То же для файлов самой прошивки: «Прошивка\» у перестроенной версии, папка версии у
    /// прежней.</summary>
    private string? FirmwareStartDirectory()
    {
        var versionDir = ServerStartDirectory();
        if (string.IsNullOrEmpty(versionDir)) return versionDir;
        return VersionLayout.FirmwareFolders(versionDir).FirstOrDefault() ?? versionDir;
    }

    /// <summary>Разводит поля файла прошивки по типу проекта. У Segnetics — «Прошивка ПЛК (.lfs)»
    /// (загрузочный) плюс отдельный «Исходник (.psl)» (не загрузочный): это два разных файла, и
    /// оператор может доложить любой. У остальных остаётся одно поле «Файл прошивки». Тип определяется
    /// тем же признаком, что и карточка в поиске (SegneticsProject.IsRelevant, вызывается только на
    /// чтение): по .lfs/.psl в папке версии, затем по подсказке исполняемого файла, затем по имени
    /// контроллера. Поля имеют смысл только когда показан блок доп. файлов (есть папка версии на
    /// доступном диске) — иначе класть файл всё равно некуда.</summary>
    private void ConfigureFirmwareFileFields()
    {
        if (AttachmentsPanel.Visibility != Visibility.Visible) return;

        bool hasLfs = false, hasPsl = false;
        if (_plcFolder is not null)
        {
            try
            {
                hasLfs = Directory.EnumerateFiles(_plcFolder, "*" + LoaderFiles.LfsExtension).Any();
                hasPsl = Directory.EnumerateFiles(_plcFolder, "*" + LoaderFiles.PslExtension).Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        _isSegnetics = SegneticsProject.IsRelevant(_record.CtrlName, _record.ExecutableHint, hasLfs, hasPsl);
        if (!_isSegnetics) return; // не-Segnetics: одно поле «Файл прошивки», как было

        PlcFileLabel.Text = "Прошивка ПЛК (.lfs):";
        PlcFileLabel.ToolTip = "Загрузочный файл прошивки Segnetics (.lfs) — ложится в папку версии " +
            "рядом с прошивкой. Исходный проект .psl докладывается отдельным полем ниже.";
        PslFilePanel.Visibility = Visibility.Visible;
        // Дешёвая отсечка по уже посчитанным флагам: без исходника или с готовым .lfs собирать
        // нечего, и лишний обход папки версии по сети ради этого не нужен.
        if (hasPsl && !hasLfs) RefreshBuildLfs();
    }

    // ── Сборка .psl → .lfs ────────────────────────────────────────────────────
    // Кнопка нужна ровно для жалобы «программист залил psl, а наладчик при поиске должен уже видеть
    // lfs». Раньше .lfs собирался ТОЛЬКО как побочный эффект заливки в контроллер и оседал в
    // локальной рабочей области заливавшего: на сетевой диск он не попадал, и следующий наладчик на
    // другой машине снова видел один исходник.

    /// <summary>Папка версии на диске — та же, куда модерация докладывает файлы прошивки
    /// (путь мог быть записан коллегой в его форме диска, приводим к нашей).</summary>
    private string VersionFolderOnDisk()
    {
        var folder = FirmwarePathLocalizer.Localize(_record.DiskPath, _services.Cfg.RootPath());
        if (string.IsNullOrEmpty(folder)) return "";
        // disk_path мог указывать на одиночный файл — тогда «папка версии» это его родитель.
        return Directory.Exists(folder) ? folder : Path.GetDirectoryName(folder) ?? "";
    }

    /// <summary>Показывает кнопку сборки, только когда собирать реально есть что и есть куда
    /// положить результат: есть .psl в папке версии на диске и нет .lfs.</summary>
    private void RefreshBuildLfs()
    {
        var decision = LfsConversionService.Decide(VersionFolderOnDisk(), null, _record.ExecutableHint);
        if (decision.Need != LfsConversionNeed.Build)
        {
            BuildLfsPanel.Visibility = Visibility.Collapsed;
            return;
        }
        BuildLfsPanel.Visibility = Visibility.Visible;
        BuildLfsHint.Text = "Собранного .lfs у версии нет — в поиске наладчик видит только исходник.";
    }

    private void BuildLfs_Click(object sender, RoutedEventArgs e)
    {
        var folder = VersionFolderOnDisk();
        var decision = LfsConversionService.Decide(folder, null, _record.ExecutableHint);
        if (decision.Need != LfsConversionNeed.Build || decision.Plan is null)
        {
            AppMessageBox.Show(decision.Message, "Сборка LFS", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshBuildLfs();
            return;
        }

        var built = LoaderDialog.ShowBuild(this, _services.Cfg, new LoaderJob
        {
            VersionName = _record.VersionRaw,
            SourcePath = decision.Plan.PslPath,
            NetworkFolder = folder,
        });
        if (!built)
        {
            RefreshBuildLfs();
            return;
        }
        _lfsBuilt = true;
        BuildLfsBtn.Visibility = Visibility.Collapsed;
        BuildLfsHint.Text = "LFS собран и сохранён в папке версии на диске — его увидят все.";
    }

    /// <summary>В папке версии появился собранный .lfs — показанная выдача поиска с её строкой
    /// «Файлы: LFS —» больше не актуальна (см. ReportChanges).</summary>
    private bool _lfsBuilt;

    // ── Вес в поиске (по запросам) ────────────────────────────────────────────

    /// <summary>Мутабельная строка редактора веса — record с get-only не годится для правки прямо
    /// в DataGrid.</summary>
    public sealed class WeightRow
    {
        public string QueryKey { get; set; } = "";
        public int Weight { get; set; }
    }

    private readonly System.Collections.ObjectModel.ObservableCollection<WeightRow> _weightRows = new();

    private void LoadSearchWeights(int fwVersionId)
    {
        foreach (var (q, weight) in _db.GetFwUsageQueriesForVersion(fwVersionId))
            _weightRows.Add(new WeightRow { QueryKey = q, Weight = weight });
        WeightGrid.ItemsSource = _weightRows;
        WeightPanel.Visibility = Visibility.Visible;
    }

    private void AddWeightRow_Click(object sender, RoutedEventArgs e)
    {
        WeightGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var row = new WeightRow { QueryKey = "", Weight = 1 };
        _weightRows.Add(row);
        WeightGrid.SelectedItem = row;
        WeightGrid.ScrollIntoView(row);
    }

    private void RemoveWeightRow_Click(object sender, RoutedEventArgs e)
    {
        if (WeightGrid.SelectedItem is WeightRow row) _weightRows.Remove(row);
    }

    private void ClearWeights_Click(object sender, RoutedEventArgs e) => _weightRows.Clear();

    /// <summary>Сохранить ручной вес: приводим строки редактора к нормализованному запросу
    /// (SearchService.UsageKey — тот же ключ, что пишет обычный выбор из поиска, иначе ручной вес не
    /// сложился бы с накопленным счётчиком по тому же запросу), и синхронизируем с БД — убираем
    /// удалённые строки, проставляем новые значения через SetLocalFwWeight (это ВЕС, отдельный от
    /// счётчика открытий: он складывается со счётчиком, а не заменяет его). Пустой запрос пропускаем.</summary>
    private void ApplySearchWeights()
    {
        if (_record.Id is not int id) return;
        WeightGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var desired = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in _weightRows)
        {
            var key = AntarusPoFinder.Core.Services.SearchService.UsageKey(row.QueryKey ?? "");
            if (string.IsNullOrWhiteSpace(key)) continue;
            desired[key] = Math.Max(0, row.Weight); // 0 = убрать вес (SetLocalFwWeight снимет его)
        }

        var existing = _db.GetFwUsageQueriesForVersion(id).Select(r => r.QueryKey).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in existing.Where(k => !desired.ContainsKey(k)))
            _db.SetLocalFwWeight(gone, id, 0);
        foreach (var (key, weight) in desired)
            _db.SetLocalFwWeight(key, id, weight);
    }

    private (string GroupName, string SubtypeName, string ControllerName)? _names;

    // ── Подтипы шкафов ────────────────────────────────────────────────────────

    private readonly Dictionary<int, CheckBox> _subtypeChecks = new();
    private List<EquipmentSubType> _groupSubtypes = new();

    /// <summary>Чекбоксы по всем подтипам ГРУППЫ этой прошивки: отмечены те, под которыми она уже
    /// заведена. Основной (в чьей папке лежат сами файлы) отмечен и выключен — снять его значило бы
    /// удалить саму прошивку, а не ссылку на неё, и это делается отдельной кнопкой «Удалить прошивку».
    /// Блок не показывается вовсе, если у версии нет папки на диске: связывать тогда нечего.</summary>
    private void BuildSubtypeChecks()
    {
        if (_record.Id is null || string.IsNullOrWhiteSpace(_record.DiskPath)) return;

        var subtype = _db.GetAllEquipmentSubtypes().FirstOrDefault(s => s.Id == _record.SubtypeId);
        if (subtype is null) return;

        _groupSubtypes = _db.GetSubtypesForGroup(subtype.GroupId).Where(s => s.Id is not null).ToList();
        if (_groupSubtypes.Count <= 1) return; // выбирать не из чего — один подтип в группе

        var linked = FirmwareSubtypeLinkService.CurrentLinks(_db, _record)
            .Select(l => l.SubtypeId).ToHashSet();

        foreach (var candidate in _groupSubtypes)
        {
            var id = candidate.Id!.Value;
            var isPrimary = id == _record.SubtypeId;
            var label = candidate.Name == "—" ? candidate.FolderName : $"{candidate.FolderName} ({candidate.Name})";
            var cb = new CheckBox
            {
                Tag = id,
                Content = isPrimary ? $"{label}  —  основной" : label,
                FontWeight = isPrimary ? FontWeights.SemiBold : FontWeights.Normal,
                IsChecked = isPrimary || linked.Contains(id),
                IsEnabled = !isPrimary,
                Margin = new Thickness(4),
                ToolTip = isPrimary
                    ? "Файлы прошивки лежат в папке этого подтипа — отвязать его нельзя"
                    : null,
            };
            _subtypeChecks[id] = cb;
            SubtypesCheckPanel.Children.Add(cb);
        }

        SubtypesPanel.Visibility = Visibility.Visible;
    }

    // ── Конфигурации шкафов ───────────────────────────────────────────────────

    /// <summary>Показывает уже заведённые конфигурации в том же построчном виде, которым их вводят
    /// (см. FirmwareConfigService.FormatBulk/ParseBulk). Блок не показывается вовсе, если у версии нет
    /// папки на диске: вариант — это ссылка на ту же самую прошивку, делить ему тогда нечего.
    ///
    /// Показывается САМА прошивка, а не её вариант: у конфигурации своих конфигураций не бывает
    /// (config_name у неё уже занят), и предлагать заводить их «внутри варианта» значило бы плодить
    /// путаницу. Открытая на конфигурации карточка просто правит её теги обычным редактором выше.</summary>
    private void BuildConfigs()
    {
        if (_record.Id is null || string.IsNullOrWhiteSpace(_record.DiskPath)) return;
        if (!string.IsNullOrEmpty(_record.ConfigName))
        {
            ConfigsHint.Text = $"Это конфигурация «{_record.ConfigName}» — её теги правятся редактором выше.";
            ConfigsHint.Visibility = Visibility.Visible;
            ConfigsInput.Visibility = Visibility.Collapsed;
            ConfigsPanel.Visibility = Visibility.Visible;
            return;
        }

        _configsBefore = FirmwareConfigService.Current(_db, _record);
        ConfigsInput.Text = FirmwareConfigService.FormatBulk(_configsBefore);
        ConfigsHint.Text = _configsBefore.Count == 0
            ? "Пока ни одной конфигурации — прошивка находится по своим тегам как обычно."
            : $"Заведено конфигураций: {_configsBefore.Count}.";
        ConfigsPanel.Visibility = Visibility.Visible;
    }

    private List<FirmwareConfigService.FirmwareConfig> _configsBefore = new();

    private void ApplyConfigs()
    {
        if (ConfigsPanel.Visibility != Visibility.Visible || ConfigsInput.Visibility != Visibility.Visible) return;
        if (_record.Id is null) return;

        // Теги самой прошивки могли поменяться прямо в этом же диалоге (редактор выше), а базовые теги
        // подмешиваются в каждую конфигурацию — берём уже НОВОЕ значение, иначе варианты остались бы
        // с прежним набором до следующей правки.
        var primary = new FwVersionRecord
        {
            Id = _record.Id, SubtypeId = _record.SubtypeId, ControllerId = _record.ControllerId,
            EqPrefix = _record.EqPrefix, SubPrefix = _record.SubPrefix,
            HwVersion = _record.HwVersion, SwVersion = _record.SwVersion, DtStr = _record.DtStr,
            VersionRaw = _record.VersionRaw, Filename = _record.Filename,
            DiskPath = _record.DiskPath, LocalPath = _record.LocalPath,
            Description = ResultDescription, Changelog = _record.Changelog,
            LaunchTypes = ResultLaunchTypes, IoMapPath = IoMapInput.Text, InstructionsPath = InstructionsInput.Text,
            ModbusMapPath = ModbusMapInput.Text, HmiPath = HmiInput.Text,
            ExecutableHint = _record.ExecutableHint, HmiExecutableHint = _record.HmiExecutableHint,
            IsOpc = _record.IsOpc, RequestNum = _record.RequestNum, CabinetSn = _record.CabinetSn,
            AuthorId = _record.AuthorId, Status = _record.Status, Released = _record.Released,
            Tags = ResultTags,
        };

        var desired = FirmwareConfigService.ParseBulk(ConfigsInput.Text);
        var result = FirmwareConfigService.Apply(_db, primary, desired);
        if (result.Changed) ConfigResult = result;
    }

    private void ApplySubtypeLinks()
    {
        if (_subtypeChecks.Count == 0 || _names is null || _record.Id is null) return;

        var desired = _subtypeChecks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
        var result = FirmwareSubtypeLinkService.Apply(_db, _services.Hierarchy, _services.Cfg.RootPath(),
            _record, _names.Value.GroupName, _names.Value.ControllerName,
            _groupSubtypes, desired, new Services.ShortcutCreator());
        if (result.Changed || result.Warnings.Count > 0) SubtypeLinkResult = result;
    }

    private void IoMapBrowseFile_Click(object sender, RoutedEventArgs e) => _ioMapPicker.BrowseFile();
    private void IoMapBrowseFolder_Click(object sender, RoutedEventArgs e) => _ioMapPicker.BrowseFolder();
    private void IoMapClear_Click(object sender, RoutedEventArgs e) => _ioMapPicker.Clear();

    private void ModbusMapBrowseFile_Click(object sender, RoutedEventArgs e) => _modbusPicker.BrowseFile();
    private void ModbusMapBrowseFolder_Click(object sender, RoutedEventArgs e) => _modbusPicker.BrowseFolder();
    private void ModbusMapClear_Click(object sender, RoutedEventArgs e) => _modbusPicker.Clear();

    private void InstructionsBrowseFile_Click(object sender, RoutedEventArgs e) => _instrPicker.BrowseFile();
    private void InstructionsBrowseFolder_Click(object sender, RoutedEventArgs e) => _instrPicker.BrowseFolder();
    private void InstructionsClear_Click(object sender, RoutedEventArgs e) => _instrPicker.Clear();

    private void HmiBrowseFile_Click(object sender, RoutedEventArgs e) => _hmiPicker.BrowseFile();
    private void HmiBrowseFolder_Click(object sender, RoutedEventArgs e) => _hmiPicker.BrowseFolder();
    private void HmiClear_Click(object sender, RoutedEventArgs e) => _hmiPicker.Clear();

    private void PlcFileBrowse_Click(object sender, RoutedEventArgs e) => _plcFilePicker.BrowseFile();
    private void PlcFileClear_Click(object sender, RoutedEventArgs e) => _plcFilePicker.Clear();

    private void PslFileBrowse_Click(object sender, RoutedEventArgs e) => _pslFilePicker.BrowseFile();
    private void PslFileClear_Click(object sender, RoutedEventArgs e) => _pslFilePicker.Clear();

    private void ApplyAttachments()
    {
        if (_names is null || _record.Id is null) return;
        var request = new FirmwareAttachmentsRequest
        {
            RootPath = _services.Cfg.RootPath(),
            // Доложенная позже инструкция обязана лечь туда же, куда легла бы приложенная сразу, —
            // в том числе и на хостинг (см. FirmwareAttachmentsRequest).
            InstructionPublisher = _services.Publisher(),
            GroupName = _names.Value.GroupName,
            SubtypeName = _names.Value.SubtypeName,
            ControllerName = _names.Value.ControllerName,
            IoMapSourcePath = IoMapInput.Text.Trim(),
            ModbusMapSourcePath = ModbusMapInput.Text.Trim(),
            InstructionsSourcePath = InstructionsInput.Text.Trim(),
            HmiSourcePath = HmiInput.Text.Trim(),
            PlcFileSourcePath = PlcFileInput.Text.Trim(),
            // .psl-поле только у Segnetics; у остальных оно скрыто и в запрос не идёт.
            PslFileSourcePath = PslFilePanel.Visibility == Visibility.Visible ? PslFileInput.Text.Trim() : null,
        };
        var result = FirmwareAttachmentsService.Apply(_db, _services.Hierarchy, _record, request,
            new Services.ShortcutCreator(), _services.StubWriter());
        if (result.Applied.Count > 0 || result.Warnings.Count > 0) AttachmentsResult = result;
    }

    private void RefreshExecutableTexts()
    {
        PlcExecutableText.Text = string.IsNullOrEmpty(_plcHint) ? NoExecutableText : _plcHint;
        HmiExecutableText.Text = string.IsNullOrEmpty(_hmiHint) ? NoExecutableText : _hmiHint;
    }

    private void PickPlcExecutable_Click(object sender, RoutedEventArgs e)
    {
        if (_plcFolder is null) return;
        var picked = PickFileDialog.PickRelative(this, "Исполняемый файл прошивки ПЛК",
            "Какой файл открывать по кнопке «Открыть прошивку ПЛК»?\nДвойной клик по папке — зайти внутрь.",
            _plcFolder, _plcHint);
        if (picked.Outcome == PickFileOutcome.Cancelled) return;
        _plcHint = picked.RelativePath ?? "";
        RefreshExecutableTexts();
    }

    private void PickHmiExecutable_Click(object sender, RoutedEventArgs e)
    {
        if (_hmiFolder is null) return;
        var picked = PickFileDialog.PickRelative(this, "Исполняемый файл HMI-проекта",
            "Какой файл открывать по кнопке «Открыть HMI проект»?\nДвойной клик по папке — зайти внутрь.",
            _hmiFolder, _hmiHint);
        if (picked.Outcome == PickFileOutcome.Cancelled) return;
        _hmiHint = picked.RelativePath ?? "";
        RefreshExecutableTexts();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ResultDescription = DescriptionInput.Text.Trim();
        var tags = TagsEditor.Tags;
        foreach (var tag in tags) _db.AddTag(tag);
        ResultTags = AntarusPoFinder.Core.Services.TagString.Join(tags);
        ResultLaunchTypes = _checks.Selected;
        if (_plcFolder is not null) ResultExecutableHint = _plcHint;
        if (_hmiFolder is not null) ResultHmiExecutableHint = _hmiHint;
        ApplyAttachments();
        ApplySubtypeLinks();
        // После ApplySubtypeLinks и после того, как ResultTags/ResultDescription уже посчитаны: базовые
        // теги прошивки подмешиваются в каждую конфигурацию, и брать их надо уже НОВЫМИ.
        ApplyConfigs();
        ApplySearchWeights();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Всё, что диалог применил сам (доп. файлы и подтипы) — одним вызовом на все четыре
    /// места, откуда он открывается.</summary>
    public static void ReportChanges(EditFirmwareDialog dlg, IAppHost host)
    {
        ReportAttachments(dlg.AttachmentsResult, host);
        ReportSubtypes(dlg.SubtypeLinkResult, host);
        ReportConfigs(dlg, host);
        ReportMetadataEdits(dlg, host);
        if (dlg._lfsBuilt) host.InvalidateSearchResults();
    }

    /// <summary>Итог правки набора конфигураций шкафа. Кладётся и в накопитель синхронизации — иначе
    /// заготовленный ряд комплектаций остался бы в локальной базе и коллеги по названиям этих шкафов
    /// ничего бы не нашли (та же причина, что и у ReportMetadataEdits ниже). Выдачу поиска сбрасываем:
    /// конфигурации — полноправные строки поиска, их набор только что изменился.</summary>
    private static void ReportConfigs(EditFirmwareDialog dlg, IAppHost host)
    {
        var r = dlg.ConfigResult;
        if (r is null || !r.Changed) return;

        var parts = new List<string>();
        if (r.Added.Count > 0) parts.Add($"добавлено {r.Added.Count}");
        if (r.Updated.Count > 0) parts.Add($"изменено {r.Updated.Count}");
        if (r.Removed.Count > 0) parts.Add($"убрано {r.Removed.Count}");

        var what = $"{dlg._title}: конфигурации шкафов — {string.Join(", ", parts)}";
        host.ShowStatus(what, category: NotificationCategory.FirmwareAndParams);
        host.PushCatalogChange(what, dlg._record.Id?.ToString() ?? "");
        host.InvalidateSearchResults();
    }

    /// <summary>Описание/теги/типы пуска применяет ВЫЗЫВАЮЩИЙ код (UpdateFwVersion), но до сих пор
    /// эти правки нигде не отмечались как изменение справочника: накопитель синхронизации
    /// (SyncPendingChange) о них не узнавал, плашка «Отправить всё» не поднималась — и поставленный
    /// тег («точное название шкафа») так и оставался в локальной базе. Отсюда жалоба «добавил тег,
    /// а коллега по нему прошивку не находит»: изменение просто не уезжало на диск. Отмечаем правку
    /// здесь, одним местом на все четыре точки открытия диалога. Теги/типы пуска сравниваем
    /// множествами — порядок значения не имеет.</summary>
    private static void ReportMetadataEdits(EditFirmwareDialog dlg, IAppHost host)
    {
        var o = dlg._record;
        var tagsBefore = new HashSet<string>(TagString.Parse(o.Tags), StringComparer.OrdinalIgnoreCase);
        var tagsAfter = new HashSet<string>(TagString.Parse(dlg.ResultTags), StringComparer.OrdinalIgnoreCase);
        bool tagsChanged = !tagsBefore.SetEquals(tagsAfter);
        bool descChanged = (o.Description ?? "") != (dlg.ResultDescription ?? "");
        bool launchChanged = !new HashSet<string>(o.LaunchTypes ?? new(), StringComparer.OrdinalIgnoreCase)
            .SetEquals(dlg.ResultLaunchTypes);
        if (!tagsChanged && !descChanged && !launchChanged) return;

        var parts = new List<string>();
        if (tagsChanged) parts.Add(DescribeTagChange(tagsBefore, tagsAfter));
        if (descChanged) parts.Add("изменено описание");
        if (launchChanged) parts.Add("изменены типы пуска");

        // Куда именно — человекопонятным именем прошивки (см. _title), а не номером версии.
        var what = $"{dlg._title}: {string.Join("; ", parts)}";
        host.ShowStatus(what, category: NotificationCategory.FirmwareAndParams);
        // subjectKey = FwVersionId — чтобы карточка именно этой прошивки в выдаче показала «правки ещё
        // не на диске», пока «Отправить всё» не унесёт накопитель (см. FirmwareCardFlags.TagsPending).
        host.PushCatalogChange(what, o.Id?.ToString() ?? "");
        // Правка тегов/описания могла поменять и порядок/состав поисковой выдачи.
        host.InvalidateSearchResults();
    }

    /// <summary>«теги добавлены: 2 насоса, жокей; убраны: черновик» — вместо прежнего безликого
    /// «изменены теги». Именно этого не хватало в уведомлении и в списке «готово к отправке»:
    /// увидев там одну строку, невозможно было понять, что именно уедет коллегам.
    ///
    /// Порядок сохраняется алфавитный — набор тегов множество, «как ввели» тут смысла не несёт.</summary>
    private static string DescribeTagChange(HashSet<string> before, HashSet<string> after)
    {
        var added = after.Except(before, StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).ToList();
        var removed = before.Except(after, StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).ToList();

        var bits = new List<string>();
        if (added.Count > 0) bits.Add("добавлены: " + string.Join(", ", added));
        if (removed.Count > 0) bits.Add("убраны: " + string.Join(", ", removed));
        return "теги " + string.Join("; ", bits);
    }

    private static void ReportSubtypes(FirmwareSubtypeLinkService.ApplyResult? result, IAppHost host)
    {
        if (result is null) return;
        var parts = new List<string>();
        if (result.Added.Count > 0) parts.Add("добавлены: " + string.Join(", ", result.Added));
        if (result.Removed.Count > 0) parts.Add("убраны: " + string.Join(", ", result.Removed));
        if (parts.Count > 0)
        {
            host.ShowStatus("Подтипы прошивки — " + string.Join("; ", parts),
                category: NotificationCategory.FirmwareAndParams);
            // Записей прошивок стало больше/меньше — показанная выдача поиска больше не актуальна
            // (см. IAppHost.InvalidateSearchResults: сама она не перезапускается).
            host.InvalidateSearchResults();
        }
        if (result.Warnings.Count > 0)
            AppMessageBox.Show(string.Join("\n", result.Warnings), "Подтипы прошивки",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Общая для всех четырёх мест, откуда открывается этот диалог, реакция на догрузку
    /// доп. файлов: что реально доложено — в статус, что не получилось — отдельным окном (иначе
    /// проблема с одним файлом молча растворилась бы в тосте про теги).</summary>
    public static void ReportAttachments(FirmwareAttachmentsResult? result, IAppHost host)
    {
        if (result is null) return;
        if (result.Applied.Count > 0)
            host.ShowStatus("Доп. файлы обновлены: " + string.Join(", ", result.Applied),
                category: NotificationCategory.FirmwareAndParams);
        if (result.Warnings.Count > 0)
            AppMessageBox.Show("Не удалось приложить:\n" + string.Join("\n", result.Warnings),
                "Доп. файлы", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
