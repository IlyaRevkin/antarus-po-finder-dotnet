using System.Diagnostics;
using System.IO;
using System.Windows;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

public partial class CardParamsDialog : Window
{
    private readonly ConfigService _cfg;

    private class FileItem
    {
        public ParamFile File { get; init; } = null!;

        /// <summary>Папка файла, приведённая к корню ЭТОЙ машины. В базе disk_path лежит
        /// абсолютным, с буквой диска той машины, которая файл заливала, и через общий конфиг эта
        /// буква разъезжается по всем. У коллеги с диском под другой буквой (или подключённым по
        /// UNC) прошивки открывались, а параметры — нет: прошивки проходят через
        /// FirmwarePathLocalizer, а параметры шли по сырому пути из базы.</summary>
        public string Folder { get; init; } = "";

        public string Display => $"{File.Filename} [{File.Manufacturer}]";
    }

    public CardParamsDialog(List<ParamFile> files, ConfigService cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        FilesList.ItemsSource = files.Select(f => new FileItem
        {
            File = f,
            Folder = FirmwarePathLocalizer.Localize(f.DiskPath, cfg.RootPath()),
        }).ToList();
    }

    private FileItem? Selected()
    {
        if (FilesList.SelectedItem is FileItem item) return item;
        AppMessageBox.Show("Выберите файл.", "Параметры", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    /// <summary>Двойной клик открывает файл — ровно то же, что кнопка «Открыть». Клик по пустому
    /// месту списка (ниже строк) игнорируем: SelectedItem там остаётся от прошлого выделения, и
    /// открывать по нему файл — не то, что человек имел в виду.</summary>
    private void FilesList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not System.Windows.Controls.ListBoxItem)
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        if (source is null) return;
        OpenSelected();
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        var item = Selected();
        if (item is null) return;
        var full = Path.Combine(item.Folder, item.File.Filename);
        if (File.Exists(full)) Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
        else if (Directory.Exists(item.Folder)) Process.Start(new ProcessStartInfo(item.Folder) { UseShellExecute = true });
        else AppMessageBox.Show($"Файл не найден:\n{full}", "Параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected();
        if (item is null) return;
        if (Directory.Exists(item.Folder)) Process.Start(new ProcessStartInfo(item.Folder) { UseShellExecute = true });
        else AppMessageBox.Show($"Папка не найдена:\n{item.Folder}", "Параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ToProtocol_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected();
        if (item is null) return;
        var full = Path.Combine(item.Folder, item.File.Filename);
        if (!File.Exists(full))
        {
            AppMessageBox.Show($"Файл не найден:\n{full}", "Параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var proto = _cfg.Get("inspection_folder");
        if (string.IsNullOrEmpty(proto))
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Выберите папку осмотра" };
            if (dlg.ShowDialog() != true) return;
            proto = dlg.FolderName;
            _cfg.SetInspectionFolder(proto);
        }

        // InspectionDrop, не File.Copy напрямую: файл параметров мог годами лежать на сервере, и
        // его старая дата изменения перенеслась бы на копию — тогда автоочистка папки осмотра (по
        // возрасту файла) снесла бы его почти сразу после переноса. Хелпер ставит дату «сейчас».
        InspectionDrop.CopyInto(proto, full, System.DateTime.Now);
        AppMessageBox.Show($"Скопировано в протокол: {item.File.Filename}", "Параметры", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
