using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

using AntarusPoFinder.App;

namespace AntarusPoFinder.App.Views;

public partial class UnknownFilesDialog : Window
{
    private readonly string _root;

    public class Item
    {
        public UnknownEntry Entry { get; init; } = null!;
        public string Display => $"[{Entry.Section}]  {Entry.Name}  —  {Entry.Path}";
        public bool IsChecked { get; set; } = true;
    }

    public int Moved { get; private set; }
    public int Deleted { get; private set; }

    public UnknownFilesDialog(string root, List<UnknownEntry> entries)
    {
        InitializeComponent();
        _root = root;
        Title = $"Неизвестные файлы/папки ({entries.Count})";
        ItemsList.ItemsSource = entries.Select(e => new Item { Entry = e }).ToList();
    }

    private void CheckAll_Click(object sender, RoutedEventArgs e) => SetAll(true);
    private void UncheckAll_Click(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool value)
    {
        foreach (var item in ItemsList.Items.Cast<Item>())
            item.IsChecked = value;
        ItemsList.Items.Refresh();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        int moved = 0, deleted = 0;

        foreach (var item in ItemsList.Items.Cast<Item>())
        {
            try
            {
                if (item.IsChecked)
                {
                    var unknownDir = item.Entry.Section == "ПО"
                        ? Path.Combine(_root, "ПО", HierarchyFolders.UnknownFw)
                        : Path.Combine(_root, "Параметры", HierarchyFolders.UnknownParams);
                    Directory.CreateDirectory(unknownDir);
                    var dest = SafeDestination(unknownDir, item.Entry.Name);
                    if (item.Entry.Type == "dir") Directory.Move(item.Entry.Path, dest);
                    else File.Move(item.Entry.Path, dest);
                    moved++;
                }
                else
                {
                    if (item.Entry.Type == "dir") Directory.Delete(item.Entry.Path, recursive: true);
                    else File.Delete(item.Entry.Path);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{item.Entry.Path}: {ex.Message}");
            }
        }

        Moved = moved;
        Deleted = deleted;

        if (errors.Count > 0)
            AppMessageBox.Show(string.Join("\n", errors.Take(10)), "Ошибки", MessageBoxButton.OK, MessageBoxImage.Warning);

        DialogResult = true;
    }

    private static string SafeDestination(string destDir, string name)
    {
        var dest = Path.Combine(destDir, name);
        int suffix = 1;
        while (Directory.Exists(dest) || File.Exists(dest))
        {
            dest = Path.Combine(destDir, $"{name}_{suffix}");
            suffix++;
        }
        return dest;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
