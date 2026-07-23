using System.Windows;
using System.Windows.Controls;

namespace AntarusPoFinder.App.Views;

/// <summary>Wires the "Файл... / Папка... / Очистить" trio shared by every optional attachment field
/// on UploadView (Карта ВВ, Инструкция, Карта modbus, HMI-проект) — four near-identical blocks of
/// OpenFileDialog/OpenFolderDialog boilerplate collapsed into one reusable controller instead of
/// copy-pasted Click handlers per field (Спринт 2, Задача 2).
///
/// Deliberately takes plain apply/clear delegates rather than a TextBox reference — three of the four
/// fields store their path directly in a TextBox.Text (IoMapInput/InstructionsInput/ModbusMapInput),
/// but HMI stores it in a private field + a read-only label plus an executable-hint side effect
/// (OnHmiPathPicked), so a TextBox-shaped API couldn't cover all four without special-casing HMI
/// anyway. Also exposes the drag&amp;drop pair (HandleDrop/HandleDragOver) HMI's drop zone needs — the
/// three textbox fields don't have a drop zone in the XAML, only Файл/Папка/Очистить buttons.</summary>
internal sealed class FilePickerRow
{
    private readonly Action<string> _apply;
    private readonly Action _clear;
    private readonly string _fileDialogTitle;
    private readonly string _fileDialogFilter;
    private readonly string _folderDialogTitle;
    private readonly Func<string[], string>? _combineMultiple;

    /// <param name="combineMultiple">Что делать, если выбрали/перетащили несколько файлов сразу.
    /// Не задан ⇒ прежнее поведение (берётся первый) — так и оставлено для полей доп. файлов, где
    /// набор файлов не имеет смысла. HMI и основная зона прошивки передают сюда сборку во временную
    /// папку (DropStagingService), чтобы дальше это был обычный сценарий «выбрали папку».</param>
    public FilePickerRow(Action<string> apply, Action clear,
        string fileDialogTitle = "", string fileDialogFilter = "", string folderDialogTitle = "",
        Func<string[], string>? combineMultiple = null)
    {
        _apply = apply;
        _clear = clear;
        _fileDialogTitle = fileDialogTitle;
        _fileDialogFilter = fileDialogFilter;
        _folderDialogTitle = folderDialogTitle;
        _combineMultiple = combineMultiple;
    }

    public void BrowseFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = _fileDialogTitle,
            Filter = _fileDialogFilter,
            Multiselect = _combineMultiple is not null,
        };
        if (dlg.ShowDialog() == true) ApplyPaths(dlg.FileNames.Length > 0 ? dlg.FileNames : new[] { dlg.FileName });
    }

    public void BrowseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = _folderDialogTitle };
        if (dlg.ShowDialog() == true) _apply(dlg.FolderName);
    }

    public void Clear() => _clear();

    public void HandleDrop(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            ApplyPaths(paths);
    }

    private void ApplyPaths(string[] paths)
    {
        if (paths.Length == 1 || _combineMultiple is null) _apply(paths[0]);
        else _apply(_combineMultiple(paths));
    }

    public static void HandleDragOver(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
}
