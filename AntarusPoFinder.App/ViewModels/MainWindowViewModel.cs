using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.App.Views;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.App;

namespace AntarusPoFinder.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IAppHost
{
    private readonly AppServices _services;
    private readonly Dictionary<string, object> _pageCache = new();
    private DispatcherTimer? _sync1500msTimer;
    private DispatcherTimer? _syncRepeatTimer;
    private DispatcherTimer? _hierarchy2sTimer;
    private DispatcherTimer? _statusClearTimer;
    private DispatcherTimer? _updateCheckTimer;
    private DispatcherTimer? _fwUpdateCheckTimer;
    private DispatcherTimer? _configCheckTimer;
    private DispatcherTimer? _configPullRepeatTimer;
    private DispatcherTimer? _configPushTimer;
    private UpdateRelease? _pendingUpdate;
    private int? _lastModerationCount;
    private List<FirmwareUpdateInfo> _pendingFwUpdates = new();
    private ConfigUpdateInfo? _pendingConfigUpdate;

    private bool _suppressThemeToggleHandler;

    [ObservableProperty] private string _roleLabel = "";
    [ObservableProperty] private bool _isDarkTheme;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _diskStatusText = "Диск: …";
    [ObservableProperty] private object? _currentPageContent;
    [ObservableProperty] private bool _settingsVisible;
    [ObservableProperty] private bool _isSettingsActive;
    [ObservableProperty] private bool _updateBannerVisible;
    [ObservableProperty] private string _updateBannerText = "";
    [ObservableProperty] private bool _updateActionEnabled = true;
    [ObservableProperty] private bool _fwUpdateBannerVisible;
    [ObservableProperty] private string _fwUpdateBannerText = "";
    [ObservableProperty] private bool _configUpdateBannerVisible;
    [ObservableProperty] private string _configUpdateBannerText = "";
    [ObservableProperty] private int _unseenNotificationsCount;

    public string CurrentRole { get; private set; } = "naladchik";
    public string CurrentTheme { get; private set; } = "light";
    public string CurrentPageId { get; private set; } = "search";

    public ObservableCollection<NavItem> NavItems { get; } = new();
    public ObservableCollection<QuickAppItem> QuickApps { get; } = new();
    public ObservableCollection<NotificationEntry> NotificationHistory { get; } = new();

    private const int NotificationHistoryLimit = 100;
    private const int BannerAutoHideMs = 10000;

    public MainWindowViewModel(AppServices services)
    {
        _services = services;

        foreach (var (pageId, label) in RolesConfig.NavItems)
            NavItems.Add(new NavItem(pageId, label));

        CurrentRole = _services.Cfg.CurrentRole();
        CurrentTheme = _services.Cfg.Theme();
        _suppressThemeToggleHandler = true;
        IsDarkTheme = CurrentTheme == "dark";
        _suppressThemeToggleHandler = false;

        ApplyRole(CurrentRole);
        ThemeManager.Apply(CurrentTheme);
        ReloadSidebarApps();
        Navigate(FirstAllowedPageId(CurrentRole));

        StartTimers();
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Navigate(string pageId)
    {
        if (!_pageCache.ContainsKey(pageId) && !TryCreatePage(pageId, out _))
            return;

        CurrentPageId = pageId;
        CurrentPageContent = _pageCache[pageId];
        if (pageId == "search" && _pageCache[pageId] is SearchView searchView)
            searchView.RefreshIfActive();
        if (pageId == "newversions" && _pageCache[pageId] is NewVersionsView newVersionsView)
            newVersionsView.RefreshIfActive();
        if (pageId == "inspection" && _pageCache[pageId] is InspectionView inspectionView)
            inspectionView.RefreshIfActive();
        if (pageId == "network" && _pageCache[pageId] is NetworkSyncView networkView)
            networkView.RefreshIfActive();

        foreach (var item in NavItems)
            item.IsActive = item.PageId == pageId;
        IsSettingsActive = pageId == "settings";
        RefreshModerationBadge();
    }

    /// <summary>Keeps the "Модерация тегов" sidebar badge in sync with Settings→Прошивки→Модерация's
    /// own counter — refreshed on every navigation (cheap COUNT query) so it never goes stale after
    /// moderating a version and switching tabs, without needing a dedicated changed-event. Also
    /// notifies the administrator specifically (not just a passive badge) the moment the count goes
    /// up — i.e. a new firmware actually started needing moderation, not just "some still do".</summary>
    private void RefreshModerationBadge()
    {
        var item = NavItems.FirstOrDefault(n => n.PageId == "newversions");
        if (item is null) return;
        try
        {
            var count = _services.Db.GetUnreleasedFwVersionsCount();
            item.BadgeCount = count;

            if (CurrentRole == "administrator" && _lastModerationCount.HasValue && count > _lastModerationCount.Value)
                ShowStatus($"Новая прошивка ожидает модерации (всего в очереди: {count})", 8000);
            _lastModerationCount = count;
        }
        catch { /* best effort — badge just won't update this time */ }
    }

    private bool TryCreatePage(string pageId, out object? page)
    {
        page = pageId switch
        {
            "search" => new SearchView(_services, this),
            "inspection" => new InspectionView(_services, this),
            "newversions" => new NewVersionsView(_services, this),
            "upload" => new UploadView(_services, this),
            "params" => new ParamsView(_services, this),
            "settings" => new SettingsView(_services, this),
            "network" => new NetworkSyncView(_services, this),
            _ => null,
        };
        if (page is null) return false;
        _pageCache[pageId] = page;
        return true;
    }

    // ── Roles ─────────────────────────────────────────────────────────────────

    public void ApplyRole(string role)
    {
        CurrentRole = role;
        RoleLabel = RolesConfig.RoleLabel(role);
        var allowed = RolesConfig.RoleAccess.GetValueOrDefault(role, new HashSet<string>());
        foreach (var item in NavItems)
            item.IsVisible = allowed.Contains(item.PageId);
        SettingsVisible = allowed.Contains("settings");

        // Redirect to the first page this role actually has access to if the current page is no
        // longer allowed for the new role — landing on "search" regardless (the old behavior) sent
        // "programmer" (allowed = upload, params only) to a page it can't see, with no active nav
        // button to show for it.
        if (!allowed.Contains(CurrentPageId))
            Navigate(FirstAllowedPageId(role));

        RefreshConfigSync();
    }

    private static string FirstAllowedPageId(string role)
    {
        var allowed = RolesConfig.RoleAccess.GetValueOrDefault(role, new HashSet<string>());
        foreach (var (pageId, _) in RolesConfig.NavItems)
            if (allowed.Contains(pageId)) return pageId;
        return "search";
    }

    public void SwitchRole(string role)
    {
        CurrentRole = role;
        _services.Cfg.SetRole(role);
        ApplyRole(role);
    }

    // ── Theme ─────────────────────────────────────────────────────────────────

    public string ThemeLabel => IsDarkTheme ? "Тёмная тема" : "Светлая тема";

    partial void OnIsDarkThemeChanged(bool value)
    {
        OnPropertyChanged(nameof(ThemeLabel));
        if (_suppressThemeToggleHandler) return;
        CurrentTheme = value ? "dark" : "light";
        _services.Cfg.SetTheme(CurrentTheme);
        ThemeManager.Apply(CurrentTheme);
    }

    // ── Status bar ────────────────────────────────────────────────────────────

    public void ShowStatus(string message, int ms = 4000)
    {
        StatusMessage = message;
        if (!string.IsNullOrEmpty(message)) AddNotification(message);
        _statusClearTimer?.Stop();
        _statusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        _statusClearTimer.Tick += (_, _) =>
        {
            StatusMessage = "";
            _statusClearTimer!.Stop();
        };
        _statusClearTimer.Start();
    }

    // ── Notification center ───────────────────────────────────────────────────
    // Every ShowStatus() call and every banner appearance lands here — a status-bar message that
    // flashed for 4-8 seconds, or a banner that auto-hides after BannerAutoHideMs, is still visible
    // afterwards via the "Уведомления" sidebar button, not gone the moment nobody was looking.

    private void AddNotification(string text, Action? reopen = null)
    {
        NotificationHistory.Insert(0, new NotificationEntry(text, DateTime.Now, reopen));
        while (NotificationHistory.Count > NotificationHistoryLimit)
            NotificationHistory.RemoveAt(NotificationHistory.Count - 1);
        UnseenNotificationsCount++;
    }

    /// <summary>Interactive banners (update available, firmware update available) get 10 seconds
    /// before hiding themselves — long enough to read, short enough not to sit there forever if the
    /// user just doesn't act on it. Reopening from history brings the same interactive banner back
    /// rather than just repeating its text, so "Обновить сейчас" is still one click away.</summary>
    private void ScheduleBannerAutoHide(Action hide)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(BannerAutoHideMs) };
        timer.Tick += (_, _) => { timer.Stop(); hide(); };
        timer.Start();
    }

    [RelayCommand]
    private void ShowNotificationHistory()
    {
        UnseenNotificationsCount = 0;
        var win = new NotificationHistoryWindow(NotificationHistory) { Owner = Application.Current.MainWindow };
        win.ShowDialog();
    }

    // ── Startup timers (mirrors app.py's exact sequence) ─────────────────────

    private void StartTimers()
    {
        // 1500ms once (always), then every sync_interval_min minutes — unless it's 0, which means
        // "periodic auto-sync disabled on this machine" (see ConfigService.SyncIntervalMin); the
        // one-time startup sync above still runs regardless, only the repeat is skipped.
        _sync1500msTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _sync1500msTimer.Tick += (_, _) =>
        {
            _sync1500msTimer!.Stop();
            RunSync();
            var minutes = _services.Cfg.SyncIntervalMin();
            if (minutes <= 0) return;
            _syncRepeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
            _syncRepeatTimer.Tick += (_, _) => RunSync();
            _syncRepeatTimer.Start();
        };
        _sync1500msTimer.Start();

        // 2000ms once: ensure disk folder structure exists.
        _hierarchy2sTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
        _hierarchy2sTimer.Tick += (_, _) =>
        {
            _hierarchy2sTimer!.Stop();
            EnsureHierarchy();
        };
        _hierarchy2sTimer.Start();

        // 2500ms once: check for app updates (folder if configured, else GitHub — see AppUpdateService).
        _updateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        _updateCheckTimer.Tick += async (_, _) =>
        {
            _updateCheckTimer!.Stop();
            await CheckForAppUpdatesAsync();
        };
        _updateCheckTimer.Start();

        // 3000ms once: check whether any locally cached firmware has a newer version on the server.
        _fwUpdateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3000) };
        _fwUpdateCheckTimer.Tick += (_, _) =>
        {
            _fwUpdateCheckTimer!.Stop();
            CheckForFirmwareUpdates();
        };
        _fwUpdateCheckTimer.Start();

        // 3500ms once, then every sync_interval_min minutes: pull+apply a newer shared config
        // automatically — settings are 100% local-only by design (see ConfigSyncService.
        // SkipSettingsKeys), so applying without a confirmation click is safe.
        _configCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3500) };
        _configCheckTimer.Tick += (_, _) =>
        {
            _configCheckTimer!.Stop();
            CheckForConfigUpdate();
            var minutes = _services.Cfg.SyncIntervalMin();
            if (minutes <= 0) return;
            _configPullRepeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
            _configPullRepeatTimer.Tick += (_, _) => CheckForConfigUpdate();
            _configPullRepeatTimer.Start();
        };
        _configCheckTimer.Start();
    }

    // ── App updates ───────────────────────────────────────────────────────────

    private async Task CheckForAppUpdatesAsync()
    {
        UpdateCheckResult result;
        try
        {
            result = await AppUpdateService.CheckForUpdatesAsync(_services.Cfg.AppUpdatePath());
        }
        catch
        {
            // Фоновая проверка при запуске — сеть/GitHub недоступны? Просто тихо пропускаем,
            // пользователь всегда может проверить вручную в Настройках.
            return;
        }

        if (result.Releases.Count == 0) return;
        var latest = result.Releases[0];
        if (latest.Version <= AppUpdateService.CurrentVersion) return;

        _pendingUpdate = latest;
        if (_services.Cfg.AppAutoUpdate())
        {
            UpdateBannerText = $"Устанавливается версия {latest.Version} (источник: {result.SourceLabel})…";
            UpdateBannerVisible = true;
            await InstallUpdate();
        }
        else
        {
            UpdateBannerText = $"Доступна новая версия {latest.Version} (текущая {AppUpdateService.CurrentVersion}). Источник: {result.SourceLabel}.";
            UpdateBannerVisible = true;
            AddNotification(UpdateBannerText, reopen: () => UpdateBannerVisible = true);
            ScheduleBannerAutoHide(() => UpdateBannerVisible = false);
        }
    }

    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (_pendingUpdate is null) return;
        UpdateActionEnabled = false;
        UpdateBannerText = $"Установка версии {_pendingUpdate.Version}…";
        try
        {
            await AppUpdateService.InstallAndRestartAsync(_pendingUpdate);
        }
        catch (Exception ex)
        {
            UpdateBannerText = $"Не удалось установить обновление: {ex.Message}";
            UpdateActionEnabled = true;
        }
    }

    [RelayCommand]
    private void DismissUpdateBanner() => UpdateBannerVisible = false;

    // ── Firmware updates ─────────────────────────────────────────────────────

    private void CheckForFirmwareUpdates()
    {
        List<FirmwareUpdateInfo> updates;
        try
        {
            updates = FirmwareUpdateService.GetAvailableUpdates(_services.Db);
        }
        catch
        {
            // Same reasoning as the app-update check: a background scan shouldn't ever surface
            // an error dialog on startup — the user can still see everything via Search/История.
            return;
        }
        if (updates.Count == 0) return;

        var autoOnes = updates.Where(u => _services.Cfg.IsFwAutoUpdate(u.LocalDir)).ToList();
        var manualOnes = updates.Except(autoOnes).ToList();

        var autoUpdated = 0;
        foreach (var u in autoOnes)
        {
            try
            {
                FirmwareSync.CopyToLocal(SearchService.ToHierarchyResult(u.Latest));
                autoUpdated++;
            }
            catch { /* best effort — still surface the rest via the manual banner below */ }
        }
        if (autoUpdated > 0)
            ShowStatus($"Автоматически обновлено прошивок: {autoUpdated}", 6000);

        _pendingFwUpdates = manualOnes;
        if (manualOnes.Count == 0) return;

        FwUpdateBannerText = $"Доступно обновление прошивок: {manualOnes.Count}";
        FwUpdateBannerVisible = true;
        AddNotification(FwUpdateBannerText, reopen: () => FwUpdateBannerVisible = true);
        ScheduleBannerAutoHide(() => FwUpdateBannerVisible = false);
    }

    [RelayCommand]
    private void UpdateAllFw()
    {
        var count = 0;
        foreach (var u in _pendingFwUpdates.ToList())
        {
            try
            {
                FirmwareSync.CopyToLocal(SearchService.ToHierarchyResult(u.Latest));
                count++;
            }
            catch { /* best effort — leave it for the details window/next scan */ }
        }
        _pendingFwUpdates.Clear();
        FwUpdateBannerVisible = false;
        ShowStatus($"Обновлено прошивок: {count}", 6000);
        RefreshSearchIfActive();
    }

    [RelayCommand]
    private void ShowFwUpdatesDetails()
    {
        var win = new FirmwareUpdatesWindow(_services, _pendingFwUpdates) { Owner = Application.Current.MainWindow };
        win.ShowDialog();

        if (win.UpdatedCount > 0)
            RefreshSearchIfActive();

        // Recompute — the window may have updated some rows and/or flipped auto-update flags.
        _pendingFwUpdates = FirmwareUpdateService.GetAvailableUpdates(_services.Db)
            .Where(u => !_services.Cfg.IsFwAutoUpdate(u.LocalDir))
            .ToList();
        if (_pendingFwUpdates.Count == 0)
        {
            FwUpdateBannerVisible = false;
        }
        else
        {
            FwUpdateBannerText = $"Доступно обновление прошивок: {_pendingFwUpdates.Count}";
        }
    }

    [RelayCommand]
    private void DismissFwUpdateBanner() => FwUpdateBannerVisible = false;

    private void RefreshSearchIfActive()
    {
        if (_pageCache.TryGetValue("search", out var page) && page is SearchView searchView)
            searchView.RefreshIfActive();
    }

    // ── Shared config sync (Настройки → Общие → Экспорт/Импорт) ─────────────

    /// <summary>Every role auto-pulls the shared config on this interval — no confirmation click,
    /// per the operator's decision that naladchik/programmer should just stay in sync in the
    /// background. The banner below is purely informational (what just changed), not a prompt.</summary>
    private void CheckForConfigUpdate()
    {
        var info = ConfigSyncService.CheckForUpdate(_services, out var error);
        if (error is not null)
        {
            // Root reachable but reading/parsing the shared config itself failed — worth telling
            // the user, unlike an unreachable share (already covered by DiskStatusText) or "no
            // update yet", which stay silent. The app keeps running on the local copy regardless.
            ShowStatus($"Не удалось проверить обновление конфига: {error}", 8000);
            return;
        }
        if (info is null) return;

        var root = _services.Cfg.RootPath();
        var result = ConfigSyncService.Apply(_services, info.ConfigPath, root);
        _pendingConfigUpdate = info;
        ReloadSidebarApps();
        RefreshSearchIfActive();

        ConfigUpdateBannerText = $"Конфиг обновлён от {info.ExportedBy} ({info.ExportedAt}): настроек {result.SettingsApplied}, изменений в справочнике {result.Counts.TotalChanges}.";
        ConfigUpdateBannerVisible = true;
        AddNotification(ConfigUpdateBannerText);
        ScheduleBannerAutoHide(() => ConfigUpdateBannerVisible = false);
    }

    [RelayCommand]
    private void ShowConfigUpdateDetails()
    {
        if (_pendingConfigUpdate is null) return;
        var d = _pendingConfigUpdate.Diff;
        AppMessageBox.Show(
            $"От кого: {_pendingConfigUpdate.ExportedBy}\nКогда: {_pendingConfigUpdate.ExportedAt}\n\n" +
            $"Настроек изменится: {_pendingConfigUpdate.SettingsChanged}\n" +
            $"Групп: +{d.GroupsAdded}/~{d.GroupsUpdated}, Подтипов: +{d.SubtypesAdded}/~{d.SubtypesUpdated}\n" +
            $"Контроллеров: +{d.ControllersAdded}/~{d.ControllersUpdated}, Модификаций: +{d.ModificationsAdded}/~{d.ModificationsUpdated}\n" +
            $"Производителей: +{d.Manufacturers}, Тегов: +{d.TagsAdded}/-{d.TagsRemoved}, Расширений: +{d.ExtensionsAdded}/-{d.ExtensionsRemoved}\n" +
            $"Резервов: +{d.ReservationsAdded}/~{d.ReservationsUpdated}\n" +
            $"Прошивок: +{d.FwVersions}, Файлов параметров: +{d.ParamFiles}\n\n" +
            "«+» — добавится, «~» — обновится (переименование/правка), «-» — будет удалено (только теги/расширения).",
            "Что изменится при обновлении конфига", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private void DismissConfigUpdateBanner() => ConfigUpdateBannerVisible = false;

    private void RunSync()
    {
        var root = _services.Cfg.RootPath();
        if (!string.IsNullOrEmpty(root) && System.IO.Directory.Exists(root))
        {
            var fileCount = System.Linq.Enumerable.Count(System.IO.Directory.EnumerateFiles(root, "*", System.IO.SearchOption.AllDirectories));
            DiskStatusText = $"Диск: ✓  ({fileCount} файлов)";
        }
        else
        {
            DiskStatusText = "Диск: ✗ недоступен";
        }
        RefreshModerationBadge();

        try
        {
            var expired = _services.Db.ExpireStaleReservations();
            if (expired > 0) ShowStatus($"Просрочено резервов номеров: {expired} (номера пропущены навсегда)", 8000);
        }
        catch { /* best effort — next tick will retry */ }
    }

    private void EnsureHierarchy()
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root)) return;
        var result = _services.Hierarchy.EnsureStructure(root);
        if (result.CreatedCount > 0)
            ShowStatus($"Структура диска создана: {result.CreatedCount} папок", 6000);
    }

    // ── IAppHost ──────────────────────────────────────────────────────────────

    public void ReloadSidebarApps()
    {
        QuickApps.Clear();
        foreach (var app in _services.Cfg.QuickApps())
            QuickApps.Add(new QuickAppItem(app.Name, app.Path));
    }

    /// <summary>0 disables periodic auto-sync — stops any running repeat timer instead of setting a
    /// zero interval (which would fire continuously). A non-zero value re-arms a repeat timer even
    /// if one wasn't running before (interval was previously 0), reusing the same Tick handlers as
    /// StartTimers.</summary>
    public void SetSyncIntervalMinutes(int minutes)
    {
        if (minutes <= 0)
        {
            _syncRepeatTimer?.Stop();
            _syncRepeatTimer = null;
            _configPullRepeatTimer?.Stop();
            _configPullRepeatTimer = null;
            return;
        }

        if (_syncRepeatTimer is not null)
        {
            _syncRepeatTimer.Interval = TimeSpan.FromMinutes(minutes);
        }
        else
        {
            _syncRepeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
            _syncRepeatTimer.Tick += (_, _) => RunSync();
            _syncRepeatTimer.Start();
        }

        if (_configPullRepeatTimer is not null)
        {
            _configPullRepeatTimer.Interval = TimeSpan.FromMinutes(minutes);
        }
        else
        {
            _configPullRepeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
            _configPullRepeatTimer.Tick += (_, _) => CheckForConfigUpdate();
            _configPullRepeatTimer.Start();
        }
    }

    void IAppHost.Navigate(string pageId) => Navigate(pageId);

    /// <summary>Only the administrator gets an auto-push timer — everyone else just pulls (see
    /// StartTimers/_configPullRepeatTimer above). Safe to call any time (role switch, or right
    /// after NetworkSyncView saves config_auto_push/config_push_interval_min) since it always
    /// stops any previous timer before deciding whether to start a new one.</summary>
    public void RefreshConfigSync()
    {
        _configPushTimer?.Stop();
        _configPushTimer = null;
        if (CurrentRole != "administrator" || !_services.Cfg.ConfigAutoPush()) return;

        var minutes = _services.Cfg.ConfigPushIntervalMin();
        if (minutes <= 0) return; // 0 = auto-push disabled even if the checkbox is on — see footnote in NetworkSyncView

        _configPushTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
        _configPushTimer.Tick += (_, _) => PushConfigNow();
        _configPushTimer.Start();
    }

    private void PushConfigNow()
    {
        var root = _services.Cfg.RootPath();
        try
        {
            var exportedBy = $"{Environment.UserName} ({RoleLabel})";
            var result = ConfigSyncService.Export(_services, root, exportedBy);
            ShowStatus($"Конфиг автоматически отправлен на диск ({result.Hierarchy.FwVersions.Count} прошивок, {result.Hierarchy.EquipmentGroups.Count} групп)", 6000);
        }
        catch (Exception ex)
        {
            // Best-effort background tick — share may be temporarily unreachable; don't nag with a
            // dialog, just leave a status trace. The manual "Отправить сейчас" button surfaces the
            // same exception via a message box if the user wants an explicit answer.
            ShowStatus($"Не удалось автоматически отправить конфиг: {ex.Message}", 8000);
        }
    }
}
