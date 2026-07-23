using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App.Views;

/// <summary>Общая для всех ролей страница "Сетевые диски и синхронизация" — раньше пути к дискам
/// и интервал синхронизации жили только в Настройки → Общие, доступной одному администратору, хотя
/// их реально нужно настраивать на каждом компьютере отдельно. Качество сканирования сюда не
/// относится (используется только в Осмотре при самом сканировании) — живёт в InspectionView.</summary>
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
    /// switch immediately shows/hides the admin-only push section without needing a fresh instance.
    /// Also re-reads the last-sync/last-push timestamps — these are updated silently in the
    /// background (auto-pull/auto-push timers no longer show a banner, see MainWindowViewModel.
    /// CheckForConfigUpdate), so this is the only place the user sees them, and it must reflect
    /// whatever happened since the page was last visited, not just its own construction time.</summary>
    public void RefreshIfActive()
    {
        PushSection.Visibility = _services.Cfg.CurrentRole() == "administrator" ? Visibility.Visible : Visibility.Collapsed;

        var lastSync = _services.Cfg.ConfigLastSyncedAt();
        LastSyncText.Text = string.IsNullOrEmpty(lastSync) ? "" : $"Последняя синхронизация: {lastSync}";

        var lastCheck = _services.Cfg.ConfigLastCheckedAt();
        LastCheckText.Text = string.IsNullOrEmpty(lastCheck) ? "" : $"Последняя проверка диска: {lastCheck}";

        var lastPush = _services.Cfg.ConfigLastPushedAt();
        LastPushText.Text = string.IsNullOrEmpty(lastPush) ? "" : $"Последняя отправка: {lastPush}";

        // Задача 2 — watermark ревизии маркера (config_last_synced_revision), читается напрямую
        // через ConfigService.Get, т.к. типизированного свойства для него нет (см. ConfigSyncService.
        // LocalWatermarkRevision — это его же ключ). "0"/пусто — либо синхронизации ещё не было,
        // либо общий диск ещё не знает о ревизиях (общий конфиг от версии приложения без маркера).
        var revision = _services.Cfg.Get("config_last_synced_revision");
        RevisionText.Text = string.IsNullOrEmpty(revision) || revision == "0" ? "" : $"Ревизия конфига на этой машине: {revision}";

        var conflictCount = _services.Db.PendingHierarchyConflictCount();
        ConflictStatusPanel.Visibility = conflictCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (conflictCount > 0)
            ConflictStatusText.Text = $"Конфликты синхронизации, требуют решения: {conflictCount}";
    }

    private void ShowConflicts_Click(object sender, RoutedEventArgs e)
    {
        var pending = _services.Db.GetPendingHierarchyConflicts();
        if (pending.Count == 0)
        {
            RefreshIfActive();
            return;
        }

        var dlg = new ConflictResolutionDialog(_services, pending) { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();
        if (dlg.ResolvedCount > 0)
            _host.ShowStatus($"Разрешено конфликтов синхронизации: {dlg.ResolvedCount}", category: NotificationCategory.Sync);
        RefreshIfActive();
    }

    private void Load()
    {
        RootPathInput.Text = _services.Cfg.RootPath();
        SecondDiskInput.Text = _services.Cfg.SecondDiskPath();
        InspectionFolderInput.Text = _services.Cfg.Get("inspection_folder");

        PushIntervalInput.Text = _services.Cfg.ConfigPushIntervalMin().ToString();

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
        _host.ShowStatus("Путь сохранён", category: NotificationCategory.Sync);
        // Create the folder tree on the new path and refresh the footer "Диск: …" indicator right
        // away — otherwise the footer stays stale (contradicting the toast above) until the next
        // periodic sync tick, and on sync_interval_min=0 it never updates until the app restarts.
        _host.OnRootPathChanged();
    }

    private void BrowseSecondDisk_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Второй диск" };
        if (dlg.ShowDialog() == true) SecondDiskInput.Text = dlg.FolderName;
    }

    private void SaveSecondDisk_Click(object sender, RoutedEventArgs e)
    {
        _services.Cfg.SetSecondDiskPath(SecondDiskInput.Text.Trim());
        _host.ShowStatus("Путь второго диска сохранён", category: NotificationCategory.Sync);
    }

    private void BrowseInspectionFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Папка осмотра" };
        if (dlg.ShowDialog() == true) InspectionFolderInput.Text = dlg.FolderName;
    }

    private void SaveInspectionFolder_Click(object sender, RoutedEventArgs e)
    {
        _services.Cfg.SetInspectionFolder(InspectionFolderInput.Text.Trim());
        _host.ShowStatus("Папка осмотра сохранена", category: NotificationCategory.Sync);
    }


    /// <summary>Асинхронная, как и фоновый тик синхронизации: обе долгие части (чтение общего
    /// конфига с шары и досмотр папок версий) уходят в фоновый поток, а внизу окна всё это время
    /// висит индикатор — раньше нажатие «Синхронизировать сейчас» просто вешало окно до конца.</summary>
    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show("Сетевой диск недоступен.", "Синхронизация", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Повторный клик, пока идёт первый, дал бы два параллельных импорта одного и того же файла.
        SyncNowButton.IsEnabled = false;
        try
        {
            ConfigUpdateInfo? info;
            string? error;
            SharedConfigSnapshot? snapshot;
            using (_host.BeginBusy("Проверка обновлений на диске…"))
                (info, error, snapshot) = await ConfigSyncService.CheckForUpdateAsync(_services);

            if (error is not null)
            {
                AppMessageBox.Show($"Не удалось проверить обновление конфига:\n{error}", "Синхронизация", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (info is null || snapshot is null)
            {
                LastSyncText.Text = $"Изменений нет. Последняя синхронизация: {_services.Cfg.ConfigLastSyncedAt()}";
                _host.ShowStatus("Изменений на диске нет — конфиг уже актуален", category: NotificationCategory.Sync);
                return;
            }

            ConfigApplyResult result;
            using (_host.BeginBusy("Синхронизация прошивок с диском…"))
                result = await ConfigSyncService.ApplyAsync(_services, snapshot, root);

            ShowSyncResult(result);
        }
        finally
        {
            SyncNowButton.IsEnabled = true;
        }
    }

    private void ShowSyncResult(ConfigApplyResult result)
    {
        _host.ReloadSidebarApps();
        LastSyncText.Text = $"Последняя синхронизация: {result.ExportedAt} (от {result.ExportedBy})";

        var conflictNote = result.Counts.ConflictsFound > 0 ? $"\nКонфликтов, требующих решения: {result.Counts.ConflictsFound}" : "";
        AppMessageBox.Show(
            $"Экспорт от: {result.ExportedAt} ({result.ExportedBy})\n\n" +
            $"Настроек применено: {result.SettingsApplied}\n" +
            $"Изменений в справочнике: {result.Counts.TotalChanges}" + conflictNote,
            "Синхронизация завершена", MessageBoxButton.OK, MessageBoxImage.Information);
        _host.ShowStatus($"Конфиг обновлён: настроек {result.SettingsApplied}, изменений {result.Counts.TotalChanges}", category: NotificationCategory.Sync);

        // A manual sync is already a deliberate, blocking action — open the resolution dialog right
        // here instead of just raising the passive banner the periodic auto-pull uses (see
        // MainWindowViewModel.CheckForHierarchyConflicts), so the operator resolves it immediately
        // while they're already looking at this page.
        var pending = _services.Db.GetPendingHierarchyConflicts();
        if (pending.Count > 0)
        {
            var dlg = new ConflictResolutionDialog(_services, pending) { Owner = Application.Current.MainWindow };
            dlg.ShowDialog();
            if (dlg.ResolvedCount > 0)
                _host.ShowStatus($"Разрешено конфликтов синхронизации: {dlg.ResolvedCount}", category: NotificationCategory.Sync);
        }
    }

    /// <summary>Same "0 = off, any other number = on with that interval" pattern as Осмотра's
    /// auto-cleanup — a separate "отправлять автоматически" checkbox used to sit next to this field,
    /// redundant with it (the interval already had its own "0 disables" meaning, see the footnote
    /// text below the field), so it was removed rather than kept as a second way to express the same
    /// on/off state.</summary>
    private void SavePushInterval_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PushIntervalInput.Text.Trim(), out var v) || v < 0)
        {
            AppMessageBox.Show("Введите целое число минут (0 — отключить автоотправку).", "Интервал", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _services.Cfg.SetConfigPushIntervalMin(v);
        _host.RefreshConfigSync();
        _host.ShowStatus(v == 0 ? "Автоотправка на диск отключена" : $"Интервал отправки: {v} мин", category: NotificationCategory.Sync);
    }

    private async void PushNow_Click(object sender, RoutedEventArgs e)
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show("Сетевой диск недоступен.", "Отправка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var exportedBy = $"{_services.CurrentUserName} ({RolesConfig.RoleLabel(_services.Cfg.CurrentRole())})";
            ConfigExportResult result;
            using (_host.BeginBusy("Отправка конфига на диск…"))
                result = await ConfigSyncService.ExportAsync(_services, root, exportedBy);
            LastPushText.Text = $"Последняя отправка: {result.ExportedAt}";
            AppMessageBox.Show(
                $"Отправлено:\nПрошивок: {result.Hierarchy.FwVersions.Count}\nФайлов параметров: {result.Hierarchy.ParamFiles.Count}\n" +
                $"Групп: {result.Hierarchy.EquipmentGroups.Count}, Модификаций: {result.Hierarchy.ControllerModifications.Count}\n" +
                $"Тегов: {result.Hierarchy.Tags?.Count ?? 0}, Резервов номеров: {result.Hierarchy.Reservations.Count}",
                "Конфиг отправлен на диск", MessageBoxButton.OK, MessageBoxImage.Information);
            _host.ShowStatus("Конфиг отправлен на диск", category: NotificationCategory.Sync);
        }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось отправить конфиг:\n{ex.Message}", "Отправка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>«Сделать это состояние эталонным для всех» — в отличие от обычной PushNow_Click
    /// (аддитивная отправка: получатели только дополняют свой справочник), authoritative-экспорт
    /// говорит получателям при следующем их Apply() ПОЛНОСТЬЮ заменить восемь справочных сущностей
    /// (типы шкафов/подтипы/контроллеры/модификации/производители/теги/оба списка расширений) —
    /// удалить у себя всё, чего нет в этом снимке (см. Database.ImportHierarchyData(authoritative),
    /// FK-предохранитель там же). Прошивки/параметры/резервы/пользователей это НЕ касается вообще —
    /// см. SkipKeys/ImportHierarchyDataCore. Необратимая для чужих машин операция.
    ///
    /// Задача 1: раньше здесь был просто текстовый YesNo без списка того, что реально изменится —
    /// оператор подтверждал операцию вслепую. Теперь ПЕРЕД подтверждением считается и показывается
    /// разница (Database.PreviewAuthoritativeDiff/ConfigSyncService.PreviewAuthoritativeSyncAsync —
    /// свой справочник против того, что СЕЙЧАС на диске) в AuthoritativeDiffDialog: что добавится и
    /// что удалится по каждой категории. Точное число записей, которые уедут как удаление НА КАЖДОЙ
    /// ИЗ чужих машин, всё равно показать нельзя — эта машина не видит их локальные БД, только диск
    /// как приближение к тому, что получатели уже применили; сам диалог явно об этом предупреждает.</summary>
    private async void PushAuthoritative_Click(object sender, RoutedEventArgs e)
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show("Сетевой диск недоступен.", "Эталонная синхронизация", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AuthoritativeSyncDiff? diff;
        string? diffError;
        using (_host.BeginBusy("Сравнение справочника с диском…"))
            (diff, diffError) = await ConfigSyncService.PreviewAuthoritativeSyncAsync(_services, root);

        if (diffError is not null)
        {
            AppMessageBox.Show($"Не удалось сравнить справочник с диском:\n{diffError}", "Эталонная синхронизация", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var diffDialog = new AuthoritativeDiffDialog(diff!) { Owner = Application.Current.MainWindow };
        diffDialog.ShowDialog();
        if (!diffDialog.Confirmed) return;

        PushAuthoritativeButton.IsEnabled = false;
        try
        {
            var exportedBy = $"{_services.CurrentUserName} ({RolesConfig.RoleLabel(_services.Cfg.CurrentRole())})";
            ConfigExportResult result;
            using (_host.BeginBusy("Отправка эталонного справочника на диск…"))
                result = await ConfigSyncService.ExportAsync(_services, root, exportedBy, authoritative: true);

            LastPushText.Text = $"Последняя отправка: {result.ExportedAt}";
            AppMessageBox.Show(
                $"Эталонный снимок отправлен на диск ({result.ExportedAt}).\n\n" +
                $"Типов шкафов: {result.Hierarchy.EquipmentGroups.Count}, Подтипов: {result.Hierarchy.EquipmentSubtypes.Count}\n" +
                $"Контроллеров: {result.Hierarchy.ControllerModels.Count}, Модификаций: {result.Hierarchy.ControllerModifications.Count}\n" +
                $"Производителей: {result.Hierarchy.ParamManufacturers?.Count ?? 0}, Тегов: {result.Hierarchy.Tags?.Count ?? 0}\n\n" +
                "На остальных компьютерах записи справочника, которых нет в этом списке, удалятся при их следующей синхронизации с диском.",
                "Эталонная синхронизация отправлена", MessageBoxButton.OK, MessageBoxImage.Information);
            _host.ShowStatus("Эталонный справочник отправлен на диск", category: NotificationCategory.Sync);
        }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось отправить эталонный справочник:\n{ex.Message}", "Эталонная синхронизация", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            PushAuthoritativeButton.IsEnabled = true;
        }
    }
}
