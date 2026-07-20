using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.App.ViewModels;

namespace AntarusPoFinder.App.Views;

/// <summary>Общая для всех ролей страница "Сетевые диски и синхронизация" — раньше пути к дискам,
/// разрешение сканирования и интервал синхронизации жили только в Настройки → Общие, доступной
/// одному администратору, хотя их реально нужно настраивать на каждом компьютере отдельно.</summary>
public partial class NetworkSyncView : UserControl
{
    private readonly AppServices _services;
    private readonly IAppHost _host;

    public NetworkSyncView(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        Loaded += (_, _) => Load();
    }

    /// <summary>Called on every navigation to this page (it's cached, not recreated) so a role
    /// switch immediately shows/hides the admin-only push section without needing a fresh instance.</summary>
    public void RefreshIfActive()
    {
        PushSection.Visibility = _services.Cfg.CurrentRole() == "administrator" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Load()
    {
        RootPathInput.Text = _services.Cfg.RootPath();
        SecondDiskInput.Text = _services.Cfg.SecondDiskPath();
        InspectionFolderInput.Text = _services.Cfg.Get("inspection_folder");
        var dpi = _services.Cfg.ScanResolutionDpi().ToString();
        ScanResolutionCombo.SelectedItem = ScanResolutionCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Content == dpi) ?? ScanResolutionCombo.Items[2];
        SyncIntervalInput.Text = _services.Cfg.SyncIntervalMin().ToString();

        AutoPushCheck.IsChecked = _services.Cfg.ConfigAutoPush();
        PushIntervalInput.Text = _services.Cfg.ConfigPushIntervalMin().ToString();
        var lastSync = _services.Cfg.ConfigLastSyncedAt();
        LastSyncText.Text = string.IsNullOrEmpty(lastSync) ? "" : $"Последняя синхронизация: {lastSync}";

        RefreshIfActive();
    }

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Путь к диску" };
        if (dlg.ShowDialog() == true) RootPathInput.Text = dlg.FolderName;
    }

    private void SaveRoot_Click(object sender, RoutedEventArgs e)
    {
        _services.Cfg.SetRootPath(RootPathInput.Text.Trim());
        _host.ShowStatus("Путь сохранён");
    }

    private void BrowseSecondDisk_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Второй диск" };
        if (dlg.ShowDialog() == true) SecondDiskInput.Text = dlg.FolderName;
    }

    private void SaveSecondDisk_Click(object sender, RoutedEventArgs e)
    {
        _services.Cfg.SetSecondDiskPath(SecondDiskInput.Text.Trim());
        _host.ShowStatus("Путь второго диска сохранён");
    }

    private void BrowseInspectionFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Папка осмотра" };
        if (dlg.ShowDialog() == true) InspectionFolderInput.Text = dlg.FolderName;
    }

    private void SaveInspectionFolder_Click(object sender, RoutedEventArgs e)
    {
        _services.Cfg.SetInspectionFolder(InspectionFolderInput.Text.Trim());
        _host.ShowStatus("Папка осмотра сохранена");
    }

    private void SaveScanResolution_Click(object sender, RoutedEventArgs e)
    {
        if (ScanResolutionCombo.SelectedItem is ComboBoxItem { Content: string dpiText } && int.TryParse(dpiText, out var dpi))
            _services.Cfg.SetScanResolutionDpi(dpi);
        _host.ShowStatus("Разрешение сканирования сохранено");
    }

    private void SaveSyncInterval_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SyncIntervalInput.Text.Trim(), out var v))
        {
            AppMessageBox.Show("Введите целое число минут.", "Интервал", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        v = Math.Max(1, v);
        _services.Cfg.SetSyncIntervalMin(v);
        _host.SetSyncIntervalMinutes(v);
        _host.ShowStatus($"Интервал синхронизации: {v} мин");
    }

    private void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show("Сетевой диск недоступен.", "Синхронизация", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var info = ConfigSyncService.CheckForUpdate(_services, out var error);
        if (error is not null)
        {
            AppMessageBox.Show($"Не удалось проверить обновление конфига:\n{error}", "Синхронизация", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (info is null)
        {
            LastSyncText.Text = $"Изменений нет. Последняя синхронизация: {_services.Cfg.ConfigLastSyncedAt()}";
            _host.ShowStatus("Изменений на диске нет — конфиг уже актуален");
            return;
        }

        var result = ConfigSyncService.Apply(_services, info.ConfigPath, root);
        _host.ReloadSidebarApps();
        LastSyncText.Text = $"Последняя синхронизация: {result.ExportedAt} (от {result.ExportedBy})";
        AppMessageBox.Show(
            $"Экспорт от: {result.ExportedAt} ({result.ExportedBy})\n\n" +
            $"Настроек применено: {result.SettingsApplied}\n" +
            $"Изменений в справочнике: {result.Counts.TotalChanges}",
            "Синхронизация завершена", MessageBoxButton.OK, MessageBoxImage.Information);
        _host.ShowStatus($"Конфиг обновлён: настроек {result.SettingsApplied}, изменений {result.Counts.TotalChanges}");
    }

    private void AutoPushCheck_Changed(object sender, RoutedEventArgs e)
    {
        _services.Cfg.SetConfigAutoPush(AutoPushCheck.IsChecked == true);
        _host.RefreshConfigSync();
    }

    private void SavePushInterval_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PushIntervalInput.Text.Trim(), out var v))
        {
            AppMessageBox.Show("Введите целое число минут.", "Интервал", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        v = Math.Max(1, v);
        _services.Cfg.SetConfigPushIntervalMin(v);
        _host.RefreshConfigSync();
        _host.ShowStatus($"Интервал отправки: {v} мин");
    }

    private void PushNow_Click(object sender, RoutedEventArgs e)
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show("Сетевой диск недоступен.", "Отправка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var exportedBy = $"{Environment.UserName} ({RolesConfig.RoleLabel(_services.Cfg.CurrentRole())})";
            var result = ConfigSyncService.Export(_services, root, exportedBy);
            LastPushText.Text = $"Последняя отправка: {result.ExportedAt}";
            AppMessageBox.Show(
                $"Отправлено:\nПрошивок: {result.Hierarchy.FwVersions.Count}\nФайлов параметров: {result.Hierarchy.ParamFiles.Count}\n" +
                $"Групп: {result.Hierarchy.EquipmentGroups.Count}, Модификаций: {result.Hierarchy.ControllerModifications.Count}\n" +
                $"Тегов: {result.Hierarchy.Tags?.Count ?? 0}, Резервов номеров: {result.Hierarchy.Reservations.Count}",
                "Конфиг отправлен на диск", MessageBoxButton.OK, MessageBoxImage.Information);
            _host.ShowStatus("Конфиг отправлен на диск");
        }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось отправить конфиг:\n{ex.Message}", "Отправка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
