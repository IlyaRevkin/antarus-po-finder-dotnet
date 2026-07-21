using System.Collections.Generic;
using System.Linq;
using System.Windows;
using AntarusPoFinder.Core.Data;

namespace AntarusPoFinder.App.Views;

/// <summary>«Конфликты синхронизации» — shown when ConfigSyncService.Apply finds hierarchy rows that
/// were edited on BOTH this machine and the shared config since they last agreed (see
/// Database.ClassifyHierarchyChange). Resolution is whole-row only, per the operator's approved
/// design — never per individual field. Reachable both automatically (see MainWindowViewModel's
/// periodic config-pull banner) and manually (NetworkSyncView's "Синхронизировать сейчас").</summary>
public partial class ConflictResolutionDialog : Window
{
    private readonly AppServices _services;
    private readonly List<Item> _items;

    /// <summary>Default KeepLocal=true — never silently adopt the disk copy without an explicit
    /// choice; the operator must actively pick "Оставить с диска" to overwrite their own edit.</summary>
    public class Item
    {
        public HierarchyConflictItem Source { get; init; } = null!;
        public string DisplayLabel => Source.DisplayLabel;
        public IReadOnlyList<HierarchyConflictFieldDiff> Fields => Source.Fields;
        public bool KeepLocal { get; set; } = true;
        public bool KeepIncoming { get; set; }
    }

    /// <summary>How many conflicts were actually resolved when this dialog closed — 0 if the operator
    /// picked "Решить позже" or closed the window without applying.</summary>
    public int ResolvedCount { get; private set; }

    public ConflictResolutionDialog(AppServices services, List<HierarchyConflictItem> conflicts)
    {
        InitializeComponent();
        _services = services;
        _items = conflicts.Select(c => new Item { Source = c }).ToList();
        Title = $"Конфликты синхронизации ({conflicts.Count})";
        ConflictsList.ItemsSource = _items;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
            _services.Db.ResolveHierarchyConflict(item.Source.SyncId, keepIncoming: item.KeepIncoming);

        ResolvedCount = _items.Count;
        DialogResult = true;
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        // Deliberately does nothing to hierarchy_pending_conflicts — every row here stays exactly as
        // it is (local value untouched, conflict still pending) and the same set will resurface next
        // time GetPendingHierarchyConflicts() is checked (next sync tick, or reopening this dialog
        // from the banner/Сетевые диски).
        DialogResult = false;
        Close();
    }
}
