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

    private readonly Database _db;
    private readonly LaunchTypeChecks _checks;

    private readonly string? _plcFolder;
    private readonly string? _hmiFolder;
    private string _plcHint = "";
    private string _hmiHint = "";

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

    public EditFirmwareDialog(Database db, FwVersionRecord v, string title)
    {
        InitializeComponent();
        _db = db;
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
        if (!string.IsNullOrEmpty(v.HmiPath) && Directory.Exists(v.HmiPath))
        {
            _hmiFolder = v.HmiPath;
            _hmiHint = ExecutableHintResolver.Normalize(v.HmiExecutableHint) ?? "";
            HmiExecutableRow.Visibility = Visibility.Visible;
            ExecutablesPanel.Visibility = Visibility.Visible;
        }
        RefreshExecutableTexts();
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
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
