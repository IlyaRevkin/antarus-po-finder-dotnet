using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

public enum PickFileOutcome
{
    /// <summary>Диалог закрыт крестиком/Отменой — вызывающий код ничего не меняет.</summary>
    Cancelled,
    /// <summary>«Пропустить» — подсказки нет (открывать первый подходящий файл), при правке
    /// существующей прошивки это означает «очистить ранее выбранный файл».</summary>
    Skipped,
    Picked,
}

public record PickFileResult(PickFileOutcome Outcome, string? RelativePath);

/// <summary>Спрашивает оператора, какой файл внутри папки является исполняемым — и для прошивки ПЛК,
/// и для HMI-проекта. В отличие от первой версии (плоский список файлов ТОЛЬКО верхнего уровня)
/// умеет заходить в подпапки: реальные проекты часто держат исполняемый файл во вложенной папке
/// рядом с драйверами/ресурсами, и указать его было физически нечем. Возвращает ОТНОСИТЕЛЬНЫЙ путь
/// от корня папки («Driver\App.exe») — см. ExecutableHintResolver, который эти пути и разбирает.
/// Сама папка копируется целиком в любом случае, подсказка влияет только на то, что открывается по
/// кнопкам «Открыть прошивку ПЛК»/«Открыть HMI проект».</summary>
public partial class PickFileDialog : Window
{
    private record Entry(string Display, string Name, bool IsFolder, bool IsUp);

    private readonly string _root;
    private string _relativeDir = "";

    public PickFileResult Result { get; private set; } = new(PickFileOutcome.Cancelled, null);

    public PickFileDialog(string title, string label, string rootFolder, string? initialRelative)
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        _root = rootFolder;

        // Открываемся сразу в папке уже выбранного файла (при повторной правке подсказки), а не в
        // корне — иначе оператор каждый раз заново проходит весь путь вглубь.
        var normalized = ExecutableHintResolver.Normalize(initialRelative);
        if (normalized is not null)
        {
            var dir = Path.GetDirectoryName(normalized);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(Path.Combine(_root, dir))) _relativeDir = dir;
        }

        LoadCurrentDir(Path.GetFileName(normalized ?? ""));
    }

    private string CurrentFullDir => string.IsNullOrEmpty(_relativeDir) ? _root : Path.Combine(_root, _relativeDir);

    private void LoadCurrentDir(string? selectName = null)
    {
        var entries = new List<Entry>();
        if (!string.IsNullOrEmpty(_relativeDir))
            entries.Add(new Entry("[ .. ]  наверх", "", IsFolder: true, IsUp: true));

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(CurrentFullDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                entries.Add(new Entry($"[ {name} ]", name, IsFolder: true, IsUp: false));
            }
            foreach (var file in Directory.EnumerateFiles(CurrentFullDir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                entries.Add(new Entry(name, name, IsFolder: false, IsUp: false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Папку внезапно не прочитать (сеть отвалилась / права) — показываем то, что успели
            // собрать; «Пропустить» остаётся рабочим выходом, подсказка не обязательна.
        }

        FilesList.ItemsSource = entries;
        FilesList.DisplayMemberPath = nameof(Entry.Display);
        CurrentPathText.Text = string.IsNullOrEmpty(_relativeDir)
            ? Path.GetFileName(_root.TrimEnd(Path.DirectorySeparatorChar))
            : Path.Combine(Path.GetFileName(_root.TrimEnd(Path.DirectorySeparatorChar)), _relativeDir);

        var preselect = entries.FirstOrDefault(en => !en.IsFolder && string.Equals(en.Name, selectName, StringComparison.OrdinalIgnoreCase))
                        ?? entries.FirstOrDefault(en => !en.IsFolder);
        FilesList.SelectedItem = preselect;
        UpdateSelectedText();
    }

    private void FilesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateSelectedText();

    private void UpdateSelectedText()
    {
        var selected = SelectedRelativeFile();
        SelectedText.Text = selected is null ? "Файл не выбран" : $"Выбрано: {selected}";
        OkButton.IsEnabled = selected is not null;
    }

    private string? SelectedRelativeFile()
    {
        if (FilesList.SelectedItem is not Entry entry || entry.IsFolder) return null;
        return string.IsNullOrEmpty(_relativeDir) ? entry.Name : Path.Combine(_relativeDir, entry.Name);
    }

    private void FilesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesList.SelectedItem is not Entry entry) return;
        if (!entry.IsFolder) { Ok_Click(sender, e); return; }

        _relativeDir = entry.IsUp
            ? Path.GetDirectoryName(_relativeDir) ?? ""
            : (string.IsNullOrEmpty(_relativeDir) ? entry.Name : Path.Combine(_relativeDir, entry.Name));
        LoadCurrentDir();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRelativeFile();
        if (selected is null) return;
        Result = new PickFileResult(PickFileOutcome.Picked, selected);
        DialogResult = true;
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        Result = new PickFileResult(PickFileOutcome.Skipped, null);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = new PickFileResult(PickFileOutcome.Cancelled, null);
        DialogResult = false;
    }

    /// <summary>Полный результат — нужен там, где «Пропустить» (очистить подсказку) и «Отмена»
    /// (ничего не менять) означают разное: см. EditFirmwareDialog.</summary>
    public static PickFileResult PickRelative(Window? owner, string title, string label, string rootFolder, string? initialRelative = null)
    {
        var dlg = new PickFileDialog(title, label, rootFolder, initialRelative) { Owner = owner };
        dlg.ShowDialog();
        return dlg.Result;
    }

    /// <summary>Упрощённый вариант для загрузки новой прошивки: там «Пропустить» и «Отмена» дают
    /// одно и то же — подсказки нет.</summary>
    public static string? Pick(Window? owner, string title, string label, string rootFolder, string? initialRelative = null) =>
        PickRelative(owner, title, label, rootFolder, initialRelative).RelativePath;
}
