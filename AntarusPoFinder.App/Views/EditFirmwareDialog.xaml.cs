using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
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

    public EditFirmwareDialog(AppServices services, FwVersionRecord v, string title)
    {
        InitializeComponent();
        _services = services;
        _db = services.Db;
        _record = v;
        TitleLabel.Text = $"Прошивка: {title}";
        DescriptionInput.Text = v.Description;
        TagsEditor.Configure(v.Tags.Split(' ', System.StringSplitOptions.RemoveEmptyEntries), () => _db.GetAllTags());

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
            }
        }
    }

    private (string GroupName, string SubtypeName, string ControllerName)? _names;

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
        ResultTags = string.Join(' ', tags);
        ResultLaunchTypes = _checks.Selected;
        if (_plcFolder is not null) ResultExecutableHint = _plcHint;
        if (_hmiFolder is not null) ResultHmiExecutableHint = _hmiHint;
        ApplyAttachments();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

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
