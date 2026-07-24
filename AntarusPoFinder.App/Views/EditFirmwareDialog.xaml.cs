using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

public partial class EditFirmwareDialog : Window
{
    private const string NoExecutableText = "— первый подходящий файл в папке —";

    private readonly AppServices _services;
    private readonly Database _db;
    private readonly FwVersionRecord _record;
    private readonly LaunchTypeChecks _checks;

    private readonly string? _plcFolder;
    private readonly string? _hmiFolder;
    private string _plcHint = "";
    private string _hmiHint = "";

    private readonly FilePickerRow _ioMapPicker;
    private readonly FilePickerRow _instrPicker;
    private readonly FilePickerRow _modbusPicker;
    private readonly FilePickerRow _hmiPicker;

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

    public EditFirmwareDialog(AppServices services, FwVersionRecord v, string title)
    {
        InitializeComponent();
        _services = services;
        _db = services.Db;
        _record = v;
        TitleLabel.Text = $"Прошивка: {title}";
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
            HmiExecutableLabel.ToolTip = "Отдельной папки HMI-проекта у этой версии нет — файл панели " +
                "можно указать прямо в папке прошивки (в т.ч. во вложенной), тогда в поиске появится " +
                "кнопка «Открыть HMI проект».";
        }
        if (_hmiFolder is not null)
        {
            _hmiHint = ExecutableHintResolver.Normalize(v.HmiExecutableHint) ?? "";
            HmiExecutableRow.Visibility = Visibility.Visible;
            ExecutablesPanel.Visibility = Visibility.Visible;
        }
        RefreshExecutableTexts();

        _ioMapPicker = new FilePickerRow(p => IoMapInput.Text = p, () => IoMapInput.Text = "", folderDialogTitle: "Выбрать папку");
        _instrPicker = new FilePickerRow(p => InstructionsInput.Text = p, () => InstructionsInput.Text = "", folderDialogTitle: "Выбрать папку");
        _modbusPicker = new FilePickerRow(p => ModbusMapInput.Text = p, () => ModbusMapInput.Text = "", folderDialogTitle: "Выбрать папку");
        _hmiPicker = new FilePickerRow(p => HmiInput.Text = p, () => HmiInput.Text = "",
            fileDialogTitle: "Выбрать файл HMI-проекта",
            fileDialogFilter: "HMI-проект (*.fsprj)|*.fsprj|Все файлы (*.*)|*.*",
            folderDialogTitle: "Выбрать папку HMI-проекта");

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
            }
        }
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

    private void ApplyAttachments()
    {
        if (_names is null || _record.Id is null) return;
        var request = new FirmwareAttachmentsRequest
        {
            RootPath = _services.Cfg.RootPath(),
            GroupName = _names.Value.GroupName,
            SubtypeName = _names.Value.SubtypeName,
            ControllerName = _names.Value.ControllerName,
            IoMapSourcePath = IoMapInput.Text.Trim(),
            ModbusMapSourcePath = ModbusMapInput.Text.Trim(),
            InstructionsSourcePath = InstructionsInput.Text.Trim(),
            HmiSourcePath = HmiInput.Text.Trim(),
        };
        var result = FirmwareAttachmentsService.Apply(_db, _services.Hierarchy, _record, request);
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
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Всё, что диалог применил сам (доп. файлы и подтипы) — одним вызовом на все четыре
    /// места, откуда он открывается.</summary>
    public static void ReportChanges(EditFirmwareDialog dlg, IAppHost host)
    {
        ReportAttachments(dlg.AttachmentsResult, host);
        ReportSubtypes(dlg.SubtypeLinkResult, host);
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
