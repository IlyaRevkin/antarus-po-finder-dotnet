using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Lists locally cached firmware that the server has a newer version of — opened from the
/// MainWindow "Доступно обновление ПО" banner's "Подробно" link. Each row can be updated on its own,
/// selected for a batch update, or opted into silent auto-update going forward.</summary>
public partial class FirmwareUpdatesWindow : Window
{
    private readonly AppServices _services;
    private readonly List<FirmwareUpdateInfo> _updates;
    private readonly Dictionary<FirmwareUpdateInfo, CheckBox> _selectCheckboxes = new();

    /// <summary>How many rows were actually updated while this window was open — the caller uses this
    /// to recompute the remaining banner count/visibility after the dialog closes.</summary>
    public int UpdatedCount { get; private set; }

    public FirmwareUpdatesWindow(AppServices services, IEnumerable<FirmwareUpdateInfo> updates)
    {
        InitializeComponent();
        _services = services;
        _updates = updates.ToList();
        Render();
    }

    private void Render()
    {
        ItemsPanel.Children.Clear();
        _selectCheckboxes.Clear();

        if (_updates.Count == 0)
        {
            ItemsPanel.Children.Add(new TextBlock { Text = "Все локальные прошивки актуальны.", Style = (Style)FindResource("MutedText") });
            UpdateAllButton.IsEnabled = false;
            UpdateSelectedButton.IsEnabled = false;
            return;
        }

        UpdateAllButton.IsEnabled = true;
        UpdateSelectedButton.IsEnabled = true;
        foreach (var u in _updates)
            ItemsPanel.Children.Add(MakeRow(u));
    }

    private Border MakeRow(FirmwareUpdateInfo u)
    {
        var select = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        _selectCheckboxes[u] = select;

        var title = new TextBlock
        {
            Text = $"{u.Name}: {u.CurrentLocalVersion} → {u.Latest.VersionRaw}",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)FindResource("SubtitleText"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(select);
        header.Children.Add(title);

        var changesBtn = new Button { Content = "Что нового", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        changesBtn.Click += (_, _) => ShowChangelog(u);

        var updateBtn = new Button { Content = "Обновить", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        updateBtn.Click += (_, _) => { ApplyUpdate(u); Render(); };

        var autoCheck = new CheckBox
        {
            Content = "Автообновление",
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = _services.Cfg.IsFwAutoUpdate(u.LocalDir),
        };
        autoCheck.Checked += (_, _) => _services.Cfg.SetFwAutoUpdate(u.LocalDir, true);
        autoCheck.Unchecked += (_, _) => _services.Cfg.SetFwAutoUpdate(u.LocalDir, false);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(changesBtn);
        actions.Children.Add(updateBtn);
        actions.Children.Add(autoCheck);

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(actions);

        return new Border { Style = (Style)FindResource("CardBorder"), Margin = new Thickness(0, 0, 0, 10), Child = panel };
    }

    private void ShowChangelog(FirmwareUpdateInfo u)
    {
        var text = string.IsNullOrWhiteSpace(u.Latest.Changelog) ? "Список изменений не указан." : u.Latest.Changelog;
        AppMessageBox.Show(text, $"Что нового — {u.Name} {u.Latest.VersionRaw}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private bool ApplyUpdate(FirmwareUpdateInfo u)
    {
        try
        {
            FirmwareSync.CopyToLocal(SearchService.ToHierarchyResult(u.Latest));
            UpdatedCount++;
            _updates.Remove(u);
            return true;
        }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось обновить «{u.Name}»:\n{ex.Message}", "Обновить", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void UpdateAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var u in _updates.ToList())
            ApplyUpdate(u);
        Render();
    }

    private void UpdateSelected_Click(object sender, RoutedEventArgs e)
    {
        foreach (var kv in _selectCheckboxes.ToList())
            if (kv.Value.IsChecked == true)
                ApplyUpdate(kv.Key);
        Render();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
