using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

using AntarusPoFinder.App;

namespace AntarusPoFinder.App.Views;

public partial class CardParamsDialog : Window
{
    private readonly ConfigService _cfg;

    private class FileItem
    {
        public ParamFile File { get; init; } = null!;
        public string Display => $"{File.Filename} [{File.Manufacturer}]";
    }

    public CardParamsDialog(List<ParamFile> files, ConfigService cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        FilesList.ItemsSource = files.Select(f => new FileItem { File = f }).ToList();
    }

    private FileItem? Selected()
    {
        if (FilesList.SelectedItem is FileItem item) return item;
        AppMessageBox.Show("Выберите файл.", "Параметры", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected();
        if (item is null) return;
        var full = Path.Combine(item.File.DiskPath, item.File.Filename);
        if (File.Exists(full)) Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
        else if (Directory.Exists(item.File.DiskPath)) Process.Start(new ProcessStartInfo(item.File.DiskPath) { UseShellExecute = true });
        else AppMessageBox.Show($"Файл не найден:\n{full}", "Параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected();
        if (item is null) return;
        if (Directory.Exists(item.File.DiskPath)) Process.Start(new ProcessStartInfo(item.File.DiskPath) { UseShellExecute = true });
        else AppMessageBox.Show($"Папка не найдена:\n{item.File.DiskPath}", "Параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ToProtocol_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected();
        if (item is null) return;
        var full = Path.Combine(item.File.DiskPath, item.File.Filename);
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

        Directory.CreateDirectory(proto);
        File.Copy(full, Path.Combine(proto, item.File.Filename), overwrite: true);
        AppMessageBox.Show($"Скопировано в протокол: {item.File.Filename}", "Параметры", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
