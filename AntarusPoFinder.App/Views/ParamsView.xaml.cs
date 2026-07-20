using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.Core.Domain;

using AntarusPoFinder.App;

namespace AntarusPoFinder.App.Views;

public partial class ParamsView : UserControl
{
    private readonly AppServices _services;
    private readonly IAppHost _host;
    private string? _srcPath;

    private record SubtypeOption(string Label, EquipmentSubType Subtype);

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

    private void PopulateCombos()
    {
        var groups = _services.Db.GetAllEquipmentGroups();
        var manufs = _services.Db.GetParamManufacturers();

        GroupCombo.ItemsSource = groups;
        if (groups.Count > 0) GroupCombo.SelectedIndex = 0;
        PopulateSubtypes();

        ManufCombo.ItemsSource = manufs;
        if (manufs.Count > 0) ManufCombo.SelectedIndex = 0;

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
        if (expanding) ReloadTable();
    }

    private void PopulateSubtypes()
    {
        if (GroupCombo.SelectedItem is not EquipmentGroup group)
        {
            SubCombo.ItemsSource = null;
            return;
        }
        var subtypes = _services.Db.GetSubtypesForGroup(group.Id!.Value);
        var options = subtypes.Select(s => new SubtypeOption(s.Name == "—" ? group.Name : s.Name, s)).ToList();
        SubCombo.ItemsSource = options;
        if (options.Count > 0) SubCombo.SelectedIndex = 0;
    }

    private void GroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => PopulateSubtypes();

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Выбрать файл параметров" };
        if (dlg.ShowDialog() == true)
            SetFile(dlg.FileName);
    }

    private void SetFile(string path)
    {
        _srcPath = path;
        var name = Path.GetFileName(path);
        FileLabel.Text = name;
        DropZoneLabel.Text = name;
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

    private void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_srcPath) || !File.Exists(_srcPath))
        {
            AppMessageBox.Show("Выберите файл параметров.", "Загрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (SubCombo.SelectedItem is not SubtypeOption subOption)
        {
            AppMessageBox.Show("Выберите подтип шкафа.", "Загрузка", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        var dstFolder = _services.Hierarchy.ParamsPath(root, group.Name, subOption.Subtype.Name, manuf);
        try
        {
            Directory.CreateDirectory(dstFolder);
            File.Copy(_srcPath, Path.Combine(dstFolder, Path.GetFileName(_srcPath)), overwrite: true);
        }
        catch (Exception ex)
        {
            AppMessageBox.Show(ex.Message, "Ошибка файла", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _services.Db.AddParamFile(new ParamFile
        {
            SubtypeId = subOption.Subtype.Id,
            Manufacturer = manuf,
            Filename = Path.GetFileName(_srcPath),
            DiskPath = dstFolder,
            Description = DescInput.Text.Trim(),
            UploadDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        });

        _host.ShowStatus($"Параметры загружены: {Path.GetFileName(_srcPath)}");

        DescInput.Text = "";
        _srcPath = null;
        FileLabel.Text = "Файл не выбран";
        DropZoneLabel.Text = "Перетащите файл сюда";

        ReloadTable();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => ReloadTable();
    private void Refresh_Click(object sender, RoutedEventArgs e) => ReloadTable();

    private void ReloadTable()
    {
        if (FilterGroupCombo.SelectedItem is not EquipmentGroup filterGroup) return;
        var manufFilter = FilterManufCombo.SelectedIndex > 0 ? FilterManufCombo.SelectedItem as string : null;

        System.Collections.Generic.List<int>? subtypeIds = null;
        if (filterGroup.Id is not null)
            subtypeIds = _services.Db.GetSubtypesForGroup(filterGroup.Id.Value).Select(s => s.Id!.Value).ToList();

        var files = _services.Db.GetParamFiles(manufacturer: manufFilter);
        if (subtypeIds is not null)
            files = files.Where(f => f.SubtypeId is not null && subtypeIds.Contains(f.SubtypeId.Value)).ToList();

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
        _host.ShowStatus($"Теги обновлены: {row.Filename}");
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
