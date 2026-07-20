using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

using AntarusPoFinder.App;

namespace AntarusPoFinder.App.Views;

/// <summary>Lists whatever HierarchyService.ScanUnknownFiles found (both files AND folders, at any
/// nesting depth — see the recursion fix in HierarchyService for Task 2) and lets the operator act on
/// them: delete outright, park in «Неизвестное» (hide without deleting), or reassign into an existing
/// group/subtype/controller-or-manufacturer (Task 3) — one item at a time or in bulk, via the
/// checkboxes. Reachable both from Настройки → Иерархия → «Сканировать неизвестные файлы» (manual
/// audit) and from the "Обнаружены неизвестные файлы/папки" notification banner (automatic, raised
/// from MainWindowViewModel's periodic sync check).</summary>
public partial class UnknownFilesDialog : Window
{
    private readonly AppServices _services;
    private readonly string _root;
    private readonly List<Item> _items;

    public class Item
    {
        public UnknownEntry Entry { get; init; } = null!;
        public string Display => $"[{Entry.Section}]  {Entry.Name}  —  {Entry.Path}";
        public bool IsChecked { get; set; } = true;
    }

    public int Moved { get; private set; }
    public int Deleted { get; private set; }
    public int Reassigned { get; private set; }

    public UnknownFilesDialog(AppServices services, string root, List<UnknownEntry> entries)
    {
        InitializeComponent();
        _services = services;
        _root = root;
        _items = entries.Select(e => new Item { Entry = e }).ToList();
        Title = $"Неизвестные файлы/папки ({entries.Count})";
        ItemsList.ItemsSource = _items;
    }

    private void CheckAll_Click(object sender, RoutedEventArgs e) => SetAll(true);
    private void UncheckAll_Click(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool value)
    {
        foreach (var item in _items)
            item.IsChecked = value;
        ItemsList.Items.Refresh();
    }

    private List<Item> GetSelected()
    {
        var selected = _items.Where(i => i.IsChecked).ToList();
        if (selected.Count == 0)
            AppMessageBox.Show("Отметьте хотя бы один элемент галочкой.", "Ничего не выбрано", MessageBoxButton.OK, MessageBoxImage.Information);
        return selected;
    }

    private void RemoveProcessed(IEnumerable<Item> processed)
    {
        foreach (var item in processed)
            _items.Remove(item);
        ItemsList.Items.Refresh();
        Title = $"Неизвестные файлы/папки ({_items.Count})";
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelected();
        if (selected.Count == 0) return;

        if (AppMessageBox.Show($"Удалить безвозвратно {selected.Count} элемент(ов)? Отменить это действие нельзя.",
                "Удаление", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        var errors = new List<string>();
        var done = new List<Item>();
        foreach (var item in selected)
        {
            try
            {
                if (item.Entry.Type == "dir") Directory.Delete(item.Entry.Path, recursive: true);
                else File.Delete(item.Entry.Path);
                Deleted++;
                done.Add(item);
            }
            catch (Exception ex) { errors.Add($"{item.Entry.Path}: {ex.Message}"); }
        }
        RemoveProcessed(done);
        if (errors.Count > 0)
            AppMessageBox.Show(string.Join("\n", errors.Take(10)), "Ошибки", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void MoveToUnknownSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelected();
        if (selected.Count == 0) return;

        var errors = new List<string>();
        var done = new List<Item>();
        foreach (var item in selected)
        {
            try
            {
                var unknownDir = item.Entry.Section == "ПО"
                    ? Path.Combine(_root, "ПО", HierarchyFolders.UnknownFw)
                    : Path.Combine(_root, "Параметры", HierarchyFolders.UnknownParams);
                Directory.CreateDirectory(unknownDir);
                var dest = SafeDestination(unknownDir, item.Entry.Name);
                if (item.Entry.Type == "dir") Directory.Move(item.Entry.Path, dest);
                else File.Move(item.Entry.Path, dest);
                Moved++;
                done.Add(item);
            }
            catch (Exception ex) { errors.Add($"{item.Entry.Path}: {ex.Message}"); }
        }
        RemoveProcessed(done);
        if (errors.Count > 0)
            AppMessageBox.Show(string.Join("\n", errors.Take(10)), "Ошибки", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Task 3 — "перераспределить" вместо удаления/переноса в «Неизвестное»: назначить
    /// существующую группу/подтип/контроллер (или производителя для раздела Параметры) и физически
    /// перенести туда, имя файла/папки не меняя. Все выбранные элементы должны быть одного раздела
    /// (ПО либо Параметры) — у них разные наборы целей (контроллер vs производитель), смешивать
    /// в одной операции нет смысла.</summary>
    private void ReassignSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelected();
        if (selected.Count == 0) return;

        var sections = selected.Select(i => i.Entry.Section).Distinct().ToList();
        if (sections.Count > 1)
        {
            AppMessageBox.Show(
                "Выбраны элементы из разных разделов (ПО и Параметры) — у них разные цели переноса. Отметьте элементы только одного раздела за раз.",
                "Перемещение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var section = sections[0];

        var picker = new ReassignUnknownDialog(_services, section, selected.Count) { Owner = this };
        if (picker.ShowDialog() != true) return;

        var errors = new List<string>();
        var done = new List<Item>();
        foreach (var item in selected)
        {
            try
            {
                var destDir = section == "ПО"
                    ? _services.Hierarchy.ControllerFolder(_root, picker.SelectedGroupName!, picker.SelectedSubtypeName,
                        picker.SelectedControllerOrManufacturer, picker.SelectedIsOpc)
                    : _services.Hierarchy.ParamsPath(_root, picker.SelectedGroupName!, picker.SelectedSubtypeName,
                        picker.SelectedControllerOrManufacturer);
                Directory.CreateDirectory(destDir);
                var dest = SafeDestination(destDir, item.Entry.Name);
                if (item.Entry.Type == "dir") Directory.Move(item.Entry.Path, dest);
                else File.Move(item.Entry.Path, dest);
                Reassigned++;
                done.Add(item);
            }
            catch (Exception ex) { errors.Add($"{item.Entry.Path}: {ex.Message}"); }
        }
        RemoveProcessed(done);
        if (errors.Count > 0)
            AppMessageBox.Show(string.Join("\n", errors.Take(10)), "Ошибки", MessageBoxButton.OK, MessageBoxImage.Warning);
        else if (done.Count > 0)
            AppMessageBox.Show($"Перемещено: {done.Count}", "Перемещение", MessageBoxButton.OK, MessageBoxImage.Information);
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
