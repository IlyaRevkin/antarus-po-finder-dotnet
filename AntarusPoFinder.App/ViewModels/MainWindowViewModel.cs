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
using AntarusPoFinder.Core.Domain;
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
    private DispatcherTimer? _periodicUpdateCheckTimer;
    private DispatcherTimer? _fwUpdateCheckTimer;
    private DispatcherTimer? _configCheckTimer;
    private DispatcherTimer? _configPullRepeatTimer;
    private DispatcherTimer? _configPushTimer;
    private UpdateRelease? _pendingUpdate;
    private int? _lastModerationCount;
    private List<FirmwareUpdateInfo> _pendingFwUpdates = new();
    private List<UnknownEntry> _pendingUnknownItems = new();
    private bool _configPushLastFailed;
    private bool _fwAutoUpdateLastFailed;

    /// <summary>Тик синхронизации теперь асинхронный, значит следующий может прийти, пока предыдущий
    /// ещё ждёт сетевой диск (диск отвечает медленнее, чем sync_interval_min). Раньше такого быть не
    /// могло — всё выполнялось внутри одного Tick на потоке интерфейса. Наложение прогонов не даёт
    /// ничего, кроме второй порции нагрузки на тот же диск, поэтому лишний тик просто пропускается.</summary>
    private bool _syncRunning;
    private bool _configSyncRunning;

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
    [ObservableProperty] private bool _unknownItemsBannerVisible;
    [ObservableProperty] private string _unknownItemsBannerText = "";
    [ObservableProperty] private bool _hierarchyConflictBannerVisible;
    [ObservableProperty] private string _hierarchyConflictBannerText = "";
    [ObservableProperty] private int _unseenNotificationsCount;
    /// <summary>Быстрый доступ display mode — see ConfigService.QuickAppsDisplayMode. Two separate
    /// Visibility-driving flags (rather than one enum bound with a converter) because MainWindow.xaml
    /// needs to combine this with "QuickApps.Count > 0" (an empty list never shows a bar/strip either
    /// way, in EITHER mode) and XAML has no clean way to AND two bindings without a multi-converter.</summary>
    [ObservableProperty] private bool _quickAppsSidebarVisible;
    [ObservableProperty] private bool _quickAppsTopVisible;
    /// <summary>Only meaningful while QuickAppsTopVisible is true — whether each bubble in the top
    /// row also shows its shortcut name underneath ("top_labeled" mode) or is icon-only ("top").</summary>
    [ObservableProperty] private bool _quickAppsTopShowLabels;

    /// <summary>Индикатор фоновой работы в статус-строке (рядом с «Диск: …»). Всё, что ходит на
    /// сетевой диск, обязано открывать здесь область на время работы — иначе для пользователя это
    /// выглядит как зависшая программа, ровно на что и жаловались.</summary>
    public BusyTracker Busy { get; } = new();

    public string CurrentRole { get; private set; } = "naladchik";
    public string CurrentTheme { get; private set; } = "light";
    public string CurrentPageId { get; private set; } = "search";

    public ObservableCollection<NavItem> NavItems { get; } = new();
    public ObservableCollection<QuickAppItem> QuickApps { get; } = new();
    public ObservableCollection<NotificationEntry> NotificationHistory { get; } = new();

    private const int NotificationHistoryLimit = 100;
    private const int BannerAutoHideMs = 10000;

    /// <summary>How often the app re-checks for a new self-update after the one-time startup check —
    /// see StartTimers/_periodicUpdateCheckTimer. Deliberately not a user-facing setting (unlike
    /// sync_interval_min) — 30 minutes is frequent enough to notice a fresh release same-day without
    /// hammering GitHub/the update folder. Exposed as internal so a live UI test can temporarily swap
    /// in a shorter interval without touching the timer wiring itself.</summary>
    internal static TimeSpan PeriodicUpdateCheckInterval { get; set; } = TimeSpan.FromMinutes(30);

    public MainWindowViewModel(AppServices services)
    {
        _services = services;

        foreach (var (pageId, label) in RolesConfig.NavItems)
            NavItems.Add(new NavItem(pageId, label, isCompact: pageId is "network" or "tickets"));

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
        if (pageId == "tickets" && _pageCache[pageId] is TicketsView ticketsView)
            ticketsView.RefreshIfActive();
        // Загрузка ПО / Параметры перечитывают справочники (типы шкафов, подтипы, контроллеры,
        // производители) — иначе в комбобоксах остаётся состояние на момент первой отрисовки
        // страницы, см. UploadView.RefreshIfActive.
        if (pageId == "upload" && _pageCache[pageId] is UploadView uploadView)
            uploadView.RefreshIfActive();
        if (pageId == "params" && _pageCache[pageId] is ParamsView paramsView)
            paramsView.RefreshIfActive();

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
                ShowStatus($"Новая прошивка ожидает модерации (всего в очереди: {count})", 8000, NotificationCategory.FirmwareAndParams);
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
            "tickets" => new TicketsView(_services, this),
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

        // Settings' own tab/field visibility (see SettingsView.ApplyRoleVisibility) is role-dependent
        // TOO, on top of the whole page being allowed/not — and unlike other pages that just get
        // re-rendered wholesale on next Navigate, this one can still be the CURRENT page across a role
        // switch (both administrator and naladchik/programmer can reach "settings" now), so it needs
        // an explicit refresh here rather than relying on Navigate to have re-created it.
        if (_pageCache.TryGetValue("settings", out var settingsPage) && settingsPage is SettingsView settingsView)
            settingsView.ApplyRoleVisibility();

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

    /// <summary>If the category is disabled in Настройки → Уведомления, the message is fully
    /// suppressed — no status-bar flash, no history entry — per the user's explicit request that a
    /// muted category shouldn't show up anywhere, not just skip the history.</summary>
    public void ShowStatus(string message, int ms = 4000, NotificationCategory category = NotificationCategory.General)
    {
        if (!_services.Cfg.IsNotificationCategoryEnabled(category)) return;

        StatusMessage = message;
        if (!string.IsNullOrEmpty(message)) AddNotification(message, category);
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

    /// <summary>Callers that raise a banner directly (app/firmware update) rather than going through
    /// ShowStatus must check IsNotificationCategoryEnabled themselves before calling this AND before
    /// setting their *BannerVisible flag — this only guards the history entry, not the banner.</summary>
    private void AddNotification(string text, NotificationCategory category, Action? reopen = null)
    {
        // Same text as the entry already on top (e.g. the operator clicking "Сохранить папку
        // осмотра" several times in a row) — refresh its timestamp instead of piling up identical
        // rows. Doesn't bump UnseenNotificationsCount either, since it's not actually new information.
        if (NotificationHistory.Count > 0 && NotificationHistory[0].Text == text)
        {
            var existing = NotificationHistory[0];
            NotificationHistory[0] = existing with { When = DateTime.Now, Reopen = reopen ?? existing.Reopen };
            return;
        }

        NotificationHistory.Insert(0, new NotificationEntry(text, DateTime.Now, category, reopen));
        while (NotificationHistory.Count > NotificationHistoryLimit)
            NotificationHistory.RemoveAt(NotificationHistory.Count - 1);
        if (_services.Cfg.IsNotificationCategoryCountedUnread(category))
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
        var win = new NotificationHistoryWindow(NotificationHistory, _services.Cfg) { Owner = Application.Current.MainWindow };
        win.ShowDialog();
    }

    // ── Startup timers (mirrors app.py's exact sequence) ─────────────────────

    private void StartTimers()
    {
        // 1000ms once: ensure disk folder structure exists. Deliberately BEFORE the 1500ms sync tick
        // below (was 2000ms/after, until live-testing Task 3 exposed the race that ordering caused —
        // see EnsureHierarchy's own doc): EnsureStructure silently auto-moves top-level unrecognised
        // names into «Неизвестное» as a side effect, so if CheckForUnknownItems' scan (part of
        // RunSync) ran first, its list could reference a path that got moved out from under it a
        // moment later, and the operator's very first unknown-items banner would already be stale
        // before they ever clicked "Показать". Running structure-and-cleanup first means the first
        // scan the operator sees reflects reality: only genuinely still-unresolved items.
        _hierarchy2sTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _hierarchy2sTimer.Tick += async (_, _) =>
        {
            _hierarchy2sTimer!.Stop();
            await EnsureHierarchyAsync();
        };
        _hierarchy2sTimer.Start();

        // 1500ms once (always), then every sync_interval_min minutes — unless it's 0, which means
        // "periodic auto-sync disabled on this machine" (see ConfigService.SyncIntervalMin); the
        // one-time startup sync above still runs regardless, only the repeat is skipped.
        _sync1500msTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _sync1500msTimer.Tick += async (_, _) =>
        {
            _sync1500msTimer!.Stop();
            await RunSyncAsync();
            var minutes = _services.Cfg.SyncIntervalMin();
            if (minutes <= 0) return;
            _syncRepeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
            _syncRepeatTimer.Tick += async (_, _) => await RunSyncAsync();
            _syncRepeatTimer.Start();
        };
        _sync1500msTimer.Start();

        // A self-update from a previous run may have failed silently after this process had already
        // closed (network share denied the file move, AV briefly locked the staged .exe, etc.) — see
        // AppUpdateService.InstallAndRestart. Surface it now instead of leaving it invisible.
        var lastUpdateError = AppUpdateService.TakeLastUpdateError();
        if (lastUpdateError is not null)
            AddNotification($"Автообновление не удалось: {lastUpdateError}", NotificationCategory.AppUpdates);

        // 2500ms once: check for app updates (folder if configured, else GitHub — see AppUpdateService).
        // Then, while the app stays open, re-check every PeriodicUpdateCheckInterval — a release that
        // ships after the app was already running used to only surface the next time someone
        // restarted it. One timer, reused for every tick (not a new thread/timer per check).
        _updateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        _updateCheckTimer.Tick += async (_, _) =>
        {
            _updateCheckTimer!.Stop();
            await CheckForAppUpdatesAsync();

            _periodicUpdateCheckTimer = new DispatcherTimer { Interval = PeriodicUpdateCheckInterval };
            _periodicUpdateCheckTimer.Tick += async (_, _) => await CheckForAppUpdatesAsync();
            _periodicUpdateCheckTimer.Start();
        };
        _updateCheckTimer.Start();

        // 3000ms once: check whether any locally cached firmware has a newer version on the server.
        _fwUpdateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3000) };
        _fwUpdateCheckTimer.Tick += async (_, _) =>
        {
            _fwUpdateCheckTimer!.Stop();
            await CheckForFirmwareUpdatesAsync();
        };
        _fwUpdateCheckTimer.Start();

        // 3500ms once, then every sync_interval_min minutes: pull+apply a newer shared config
        // automatically — settings are 100% local-only by design (see ConfigSyncService.
        // SkipSettingsKeys), so applying without a confirmation click is safe.
        _configCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3500) };
        _configCheckTimer.Tick += async (_, _) =>
        {
            _configCheckTimer!.Stop();
            await CheckForConfigUpdateAsync();
            var minutes = _services.Cfg.SyncIntervalMin();
            if (minutes <= 0) return;
            _configPullRepeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
            _configPullRepeatTimer.Tick += async (_, _) => await CheckForConfigUpdateAsync();
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
        var notifyEnabled = _services.Cfg.IsNotificationCategoryEnabled(NotificationCategory.AppUpdates);
        if (_services.Cfg.AppAutoUpdate())
        {
            if (notifyEnabled)
            {
                UpdateBannerText = $"Устанавливается версия {latest.Version} (источник: {result.SourceLabel})…";
                UpdateBannerVisible = true;
            }
            await InstallUpdate();
        }
        else if (notifyEnabled)
        {
            UpdateBannerText = $"Доступна новая версия {latest.Version} (текущая {AppUpdateService.CurrentVersion}). Источник: {result.SourceLabel}.";
            UpdateBannerVisible = true;
            AddNotification(UpdateBannerText, NotificationCategory.AppUpdates, reopen: () => UpdateBannerVisible = true);
            ScheduleBannerAutoHide(() => UpdateBannerVisible = false);
        }
    }

    // Same class of bug as the config-push one fixed earlier: when AppAutoUpdate is on but the
    // AppUpdates notification category is off, CheckForAppUpdatesAsync below never turns
    // UpdateBannerVisible on before calling this — so a failure here (download dropped mid-stream,
    // staged .exe briefly locked by AV, network share denied) used to update only UpdateBannerText,
    // which nothing was displaying, and never touched the notification history either. Always
    // forcing the banner visible and always logging to history here means an install failure is
    // never quieter than the "new version available" notice was, regardless of that toggle.
    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (_pendingUpdate is null) return;
        UpdateActionEnabled = false;
        UpdateBannerText = $"Установка версии {_pendingUpdate.Version}…";
        UpdateBannerVisible = true;
        try
        {
            await AppUpdateService.InstallAndRestartAsync(_pendingUpdate);
        }
        catch (Exception ex)
        {
            UpdateBannerText = $"Не удалось установить обновление: {AppUpdateService.DescribeError(ex)}";
            UpdateBannerVisible = true;
            UpdateActionEnabled = true;
            AddNotification(UpdateBannerText, NotificationCategory.AppUpdates, reopen: () => UpdateBannerVisible = true);
        }
    }

    [RelayCommand]
    private void DismissUpdateBanner() => UpdateBannerVisible = false;

    // ── Firmware updates ─────────────────────────────────────────────────────

    private async Task CheckForFirmwareUpdatesAsync()
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

        // A firmware marked for auto-update (Настройки → Прошивки → «Обновлять автоматически»)
        // failing here used to vanish completely: it's not in manualOnes (so no banner either), the
        // count below only reports successes, and this whole scan already runs silently on success
        // by design — same shape as the app auto-update Round 35 bug (a background auto-действие
        // failing with zero trace anywhere). Same "only notify on the transition" rule as
        // PushConfigNow: a share that's briefly unreachable doesn't spam a toast on every tick, but a
        // firmware that's been silently stuck out of date for hours/days is no longer invisible.
        var autoUpdated = 0;
        var autoFailed = new List<string>();
        if (autoOnes.Count > 0)
        {
            // Копирование с сетевого диска — в фоновом потоке и с прогрессом снизу: раньше это
            // молча вешало окно на всё время, пока тянулись все автообновляемые прошивки.
            using var busy = Busy.Begin("Обновление прошивок…");
            for (int i = 0; i < autoOnes.Count; i++)
            {
                var u = autoOnes[i];
                busy.Text = $"Обновление прошивки: {u.Name}";
                busy.Report(i, autoOnes.Count);
                try
                {
                    var source = SearchService.ToHierarchyResult(u.Latest);
                    await Task.Run(() => FirmwareSync.CopyToLocal(source));
                    autoUpdated++;
                }
                catch (Exception ex) { autoFailed.Add($"{u.Name}: {ex.Message}"); }
            }
        }
        if (autoUpdated > 0)
            ShowStatus($"Автоматически обновлено прошивок: {autoUpdated}", 6000, NotificationCategory.FirmwareAndParams);
        if (autoFailed.Count > 0)
        {
            if (!_fwAutoUpdateLastFailed)
            {
                _fwAutoUpdateLastFailed = true;
                AddNotification($"Автообновление прошивок не удалось ({autoFailed.Count}): {string.Join("; ", autoFailed.Take(3))}", NotificationCategory.FirmwareAndParams);
            }
        }
        else if (_fwAutoUpdateLastFailed)
        {
            _fwAutoUpdateLastFailed = false;
        }

        _pendingFwUpdates = manualOnes;
        if (manualOnes.Count == 0) return;
        if (!_services.Cfg.IsNotificationCategoryEnabled(NotificationCategory.FirmwareAndParams)) return;

        FwUpdateBannerText = $"Доступно обновление прошивок: {manualOnes.Count}";
        FwUpdateBannerVisible = true;
        AddNotification(FwUpdateBannerText, NotificationCategory.FirmwareAndParams, reopen: () => FwUpdateBannerVisible = true);
        ScheduleBannerAutoHide(() => FwUpdateBannerVisible = false);
    }

    [RelayCommand]
    private async Task UpdateAllFw()
    {
        // A manual, explicitly-clicked action — same per-item error surfacing as
        // FirmwareUpdatesWindow.ApplyUpdate (the "Показать"/details path for the same banner), which
        // already tells the operator exactly which firmware and why instead of just under-counting.
        var count = 0;
        var stillPending = new List<FirmwareUpdateInfo>();
        var failedMessages = new List<string>();
        var pending = _pendingFwUpdates.ToList();
        using (var busy = Busy.Begin("Обновление прошивок…"))
        {
            for (int i = 0; i < pending.Count; i++)
            {
                var u = pending[i];
                busy.Text = $"Обновление прошивки: {u.Name}";
                busy.Report(i, pending.Count);
                try
                {
                    var source = SearchService.ToHierarchyResult(u.Latest);
                    await Task.Run(() => FirmwareSync.CopyToLocal(source));
                    count++;
                }
                catch (Exception ex)
                {
                    stillPending.Add(u);
                    failedMessages.Add($"{u.Name}: {ex.Message}");
                }
            }
        }
        _pendingFwUpdates = stillPending;
        FwUpdateBannerText = stillPending.Count > 0 ? $"Доступно обновление прошивок: {stillPending.Count}" : FwUpdateBannerText;
        FwUpdateBannerVisible = stillPending.Count > 0;
        ShowStatus($"Обновлено прошивок: {count}", 6000, NotificationCategory.FirmwareAndParams);
        if (failedMessages.Count > 0)
            AddNotification($"Не удалось обновить ({failedMessages.Count}): {string.Join("; ", failedMessages.Take(3))}", NotificationCategory.FirmwareAndParams);
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

    // ── Unknown files/folders (Task 3 — see HierarchyService.ScanUnknownFiles) ──────────────────

    /// <summary>Piggybacks on the same periodic tick as the rest of RunSync (startup + every
    /// sync_interval_min) — same reasoning as CleanupInspectionFolder/EnsureHierarchy above.
    /// ScanUnknownFiles is read-only (nothing gets moved/deleted here, unlike EnsureHierarchy's own
    /// top-level auto-move) — the operator decides what happens to each item via
    /// ShowUnknownItemsDetails, one at a time or in bulk.</summary>
    private async Task CheckForUnknownItemsAsync()
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root)) return;

        List<UnknownEntry> unknown;
        try
        {
            // Имена справочников — из БД здесь, обход диска — в фоне: см. HierarchyService, блок
            // про двухфазные операции (соединение SQLite одно и не потокобезопасно).
            var names = _services.Hierarchy.SnapshotNames();
            using var busy = Busy.Begin("Проверка диска на неизвестные файлы…");
            unknown = await Task.Run(() =>
                System.IO.Directory.Exists(root) ? HierarchyService.ScanUnknownFiles(root, names) : null!);
            if (unknown is null) return;
        }
        catch { return; } // best effort — flaky network mount, next tick retries

        _pendingUnknownItems = unknown;
        if (unknown.Count == 0)
        {
            UnknownItemsBannerVisible = false;
            return;
        }
        if (!_services.Cfg.IsNotificationCategoryEnabled(NotificationCategory.Hierarchy)) return;

        UnknownItemsBannerText = $"Обнаружены неизвестные файлы/папки на диске: {unknown.Count}";
        UnknownItemsBannerVisible = true;
        // Same text as last time (nothing changed since) — AddNotification's own dedup just bumps
        // the timestamp instead of piling up identical history rows.
        AddNotification(UnknownItemsBannerText, NotificationCategory.Hierarchy, reopen: () => UnknownItemsBannerVisible = true);
    }

    [RelayCommand]
    private async Task ShowUnknownItemsDetails()
    {
        // Re-scan right before showing, rather than trust whatever _pendingUnknownItems still holds
        // from the last periodic tick (up to sync_interval_min minutes old, default 5) — on a shared
        // network drive another machine (or this app's own auto-cleanup, see EnsureHierarchy) could
        // have already moved/removed an item by the time the operator gets around to clicking
        // "Показать". A live rescan is what the manual Настройки → Иерархия scan button already did;
        // the notification path deserves the same freshness guarantee.
        await CheckForUnknownItemsAsync();
        if (_pendingUnknownItems.Count == 0) return;

        var dlg = new UnknownFilesDialog(_services, _services.Cfg.RootPath(), _pendingUnknownItems) { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();

        // Re-scan again afterwards rather than trust the dialog's own bookkeeping — a reassign/move/
        // delete can fail partway through (see UnknownFilesDialog's per-item error handling), and the
        // disk is the single source of truth for what's still actually unresolved.
        await CheckForUnknownItemsAsync();
    }

    [RelayCommand]
    private void DismissUnknownItemsBanner() => UnknownItemsBannerVisible = false;

    private void RefreshSearchIfActive()
    {
        if (_pageCache.TryGetValue("search", out var page) && page is SearchView searchView)
            searchView.RefreshIfActive();
    }

    // ── Shared config sync (Настройки → Общие → Экспорт/Импорт) ─────────────

    /// <summary>Every role auto-pulls the shared config on this interval — no confirmation click,
    /// per the operator's decision that naladchik/programmer should just stay in sync in the
    /// background. Applied silently: no banner, no notification-history entry (previously fired on
    /// every tick that found a real diff, which — with several people pushing firmware/config changes
    /// throughout the day — read as constant spam). The result is only visible as a passive "last
    /// synced" timestamp on the Сетевые диски page (NetworkSyncView.RefreshIfActive), which the user
    /// can check whenever they care, rather than being interrupted by it.</summary>
    private async Task CheckForConfigUpdateAsync()
    {
        if (_configSyncRunning) return; // тик пришёл, пока предыдущий ещё тянет диск — просто пропускаем
        _configSyncRunning = true;
        try
        {
            SharedConfigSnapshot? snapshot;
            ConfigUpdateInfo? info;
            string? error;
            using (Busy.Begin("Проверка обновлений на диске…"))
                (info, error, snapshot) = await ConfigSyncService.CheckForUpdateAsync(_services);

            if (error is not null)
            {
                // Root reachable but reading/parsing the shared config itself failed — worth telling
                // the user, unlike an unreachable share (already covered by DiskStatusText) or "no
                // update yet", which stay silent. The app keeps running on the local copy regardless.
                ShowStatus($"Не удалось проверить обновление конфига: {error}", 8000, NotificationCategory.Sync);
                return;
            }
            if (info is null || snapshot is null) return;

            var root = _services.Cfg.RootPath();
            using (Busy.Begin("Синхронизация прошивок с диском…"))
                await ConfigSyncService.ApplyAsync(_services, snapshot, root);

            ReloadSidebarApps();
            RefreshSearchIfActive();
            CheckForHierarchyConflicts();
        }
        finally
        {
            _configSyncRunning = false;
        }
    }

    /// <summary>Surfaces held-back hierarchy conflicts (see Database.ClassifyHierarchyChange) the same
    /// way the unknown-items/firmware-update banners work: a passive banner with a "Показать" button,
    /// not a forced modal popup that interrupts whatever the operator is doing mid-tick. Called after
    /// every silent auto-pull (CheckForConfigUpdate) — NetworkSyncView's manual "Синхронизировать
    /// сейчас" instead opens the resolution dialog directly, since that's already a deliberate,
    /// blocking action.</summary>
    private void CheckForHierarchyConflicts()
    {
        var count = _services.Db.PendingHierarchyConflictCount();
        if (count == 0)
        {
            HierarchyConflictBannerVisible = false;
            return;
        }
        if (!_services.Cfg.IsNotificationCategoryEnabled(NotificationCategory.Sync)) return;

        HierarchyConflictBannerText = $"Конфликты синхронизации, требуют решения: {count}";
        HierarchyConflictBannerVisible = true;
        AddNotification(HierarchyConflictBannerText, NotificationCategory.Sync, reopen: () => HierarchyConflictBannerVisible = true);
    }

    [RelayCommand]
    private void ShowHierarchyConflictsDetails()
    {
        var pending = _services.Db.GetPendingHierarchyConflicts();
        if (pending.Count == 0)
        {
            HierarchyConflictBannerVisible = false;
            return;
        }

        var dlg = new ConflictResolutionDialog(_services, pending) { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();

        if (dlg.ResolvedCount > 0)
        {
            ShowStatus($"Разрешено конфликтов синхронизации: {dlg.ResolvedCount}", 6000, NotificationCategory.Sync);
            ReloadSidebarApps();
            RefreshSearchIfActive();
        }
        CheckForHierarchyConflicts();
    }

    [RelayCommand]
    private void DismissHierarchyConflictsBanner() => HierarchyConflictBannerVisible = false;

    private async Task RunSyncAsync()
    {
        if (_syncRunning) return;
        _syncRunning = true;
        try
        {
            await RefreshDiskStatusAsync();
            RefreshModerationBadge();

            try
            {
                var expired = _services.Db.ExpireStaleReservations();
                if (expired > 0) ShowStatus($"Просрочено резервов номеров: {expired} (номера пропущены навсегда)", 8000, NotificationCategory.FirmwareAndParams);
            }
            catch { /* best effort — next tick will retry */ }

            await ScanDiskForNewFirmwareAsync();
            await CleanupInspectionFolderAsync();
            await CheckForUnknownItemsAsync();
        }
        finally
        {
            _syncRunning = false;
        }
    }

    /// <summary>Досмотр сетевого диска на предмет версий, которых нет в локальной базе.
    ///
    /// Почему это здесь появилось: прошивки, загруженные коллегой, физически лежат на общем диске, но
    /// в базу этой машины попадали ровно одним путём — через общий конфиг, который ОТПРАВЛЯЕТ только
    /// администратор и по умолчанию не отправляет вовсе (config_push_interval_min = 0). Сам обход
    /// диска (HierarchyService.SyncFwFromDisk) вызывался только внутри применения нового конфига —
    /// то есть если конфиг никто не выкладывал, он не запускался НИКОГДА. Отсюда и жалоба: «позагружал
    /// прошивки на компе коллеги, у себя не вижу» — файлы на диске были, показать их было некому.
    /// Теперь диск досматривается сам, тем же периодическим тиком, что и остальная синхронизация, и
    /// от чужих настроек отправки не зависит.
    ///
    /// Фазы те же, что везде: план по БД → обход диска в фоновом потоке → запись результата по БД
    /// (см. блок про двухфазные операции в HierarchyService).</summary>
    private async Task ScanDiskForNewFirmwareAsync()
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root)) return;

        try
        {
            var plan = _services.Hierarchy.PlanFwSync(root);
            FwDiskScan scan;
            using (Busy.Begin("Поиск новых прошивок на диске…"))
                scan = await Task.Run(() => HierarchyService.ScanFwDisk(plan));

            if (scan.Candidates.Count == 0) return;

            var result = _services.Hierarchy.ImportFwCandidates(scan);
            if (result.Added <= 0) return;

            var preview = string.Join(", ", result.AddedItems.Take(3));
            var more = result.AddedItems.Count > 3 ? $" и ещё {result.AddedItems.Count - 3}" : "";
            ShowStatus($"Найдено новых прошивок на диске: {result.Added} ({preview}{more})",
                10000, NotificationCategory.FirmwareAndParams);
            RefreshSearchIfActive();
        }
        catch { /* best effort — повторится на следующем тике, как и остальные шаги RunSync */ }
    }

    /// <summary>Auto-deletes files older than ConfigService.InspectionAutoCleanupMinutes() from the
    /// Осмотр folder — 0 (default) means disabled, see ConfigService for why. Piggybacks on the same
    /// timer as the rest of RunSync (startup + every sync_interval_min) instead of a dedicated timer,
    /// same reasoning as EnsureHierarchy/ExpireStaleReservations above: one more periodic background
    /// check, not a whole new schedule.</summary>
    private async Task CleanupInspectionFolderAsync()
    {
        var minutes = _services.Cfg.InspectionAutoCleanupMinutes();
        if (minutes <= 0) return;

        var folder = _services.Cfg.Get("inspection_folder");
        if (string.IsNullOrEmpty(folder)) return;

        try
        {
            var now = DateTime.Now;
            var result = await Task.Run(() =>
                System.IO.Directory.Exists(folder) ? InspectionCleanupService.Cleanup(folder, minutes, now) : null);
            if (result is null || result.DeletedCount == 0) return;

            var preview = string.Join(", ", result.DeletedNames.Take(3));
            var more = result.DeletedNames.Count > 3 ? $" и ещё {result.DeletedNames.Count - 3}" : "";
            ShowStatus($"Автоочистка папки осмотра: удалено файлов старше {InspectionCleanupService.FormatAge(minutes)} — {result.DeletedCount} ({preview}{more})",
                8000, NotificationCategory.Inspection);
        }
        catch { /* best effort — next tick will retry */ }
    }

    private async Task EnsureHierarchyAsync()
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root)) return;

        // План — по БД (быстро, здесь), создание папок на сетевом диске — в фоне. Сотни CreateDirectory
        // по медленной шаре и были одной из тех «программа не отвечает при запуске» пауз.
        var plan = _services.Hierarchy.PlanStructure(root);
        EnsureStructureResult result;
        using (Busy.Begin("Проверка структуры диска…"))
            result = await Task.Run(() => HierarchyService.ApplyStructurePlan(plan));

        if (result.CreatedCount > 0)
            ShowStatus($"Структура диска создана: {result.CreatedCount} папок", 6000, NotificationCategory.Sync);
        // EnsureStructure also auto-moves top-level unrecognised names into «Неизвестное» — this used
        // to happen completely silently (MovedCount was computed but never surfaced anywhere), which
        // meant a folder could vanish from where the operator expected it with zero explanation. The
        // moved items themselves are then picked up by the very next CheckForUnknownItems tick (they
        // live in «Неизвестное», which ScanUnknownFiles treats as a known/skip name — but this status
        // line is the only place their *disappearance* from the original spot gets explained at all).
        if (result.MovedCount > 0)
            ShowStatus($"Перенесено в «Неизвестное» при проверке структуры диска: {result.MovedCount}", 8000, NotificationCategory.Hierarchy);
    }

    // ── IAppHost ──────────────────────────────────────────────────────────────

    /// <summary>Recomputes only the footer disk indicator. Extracted from RunSync so callers that
    /// change the root path or write to the disk can refresh it on demand instead of leaving it
    /// stale until the next periodic RunSync tick — which, with sync_interval_min=0, never comes.</summary>
    public void RefreshDiskStatus() => _ = RefreshDiskStatusAsync();

    /// <summary>Пересчёт индикатора — это рекурсивный обход ВСЕГО сетевого диска (EnumerateFiles по
    /// всем подпапкам), самая заметная из «программа зависла» пауз: на общей шаре он занимает
    /// секунды. Считаем в фоновом потоке, в БД при этом не ходим вообще.</summary>
    public async Task RefreshDiskStatusAsync()
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root))
        {
            DiskStatusText = "Диск: ✗ недоступен";
            return;
        }

        using var busy = Busy.Begin("Проверка диска…");
        DiskStatusText = await Task.Run(() =>
        {
            try
            {
                if (!System.IO.Directory.Exists(root)) return "Диск: ✗ недоступен";
                var fileCount = System.Linq.Enumerable.Count(
                    System.IO.Directory.EnumerateFiles(root, "*", System.IO.SearchOption.AllDirectories));
                return $"Диск: ✓  ({fileCount} файлов)";
            }
            catch
            {
                // Шара отвалилась посреди обхода — для индикатора это то же самое, что «недоступен».
                return "Диск: ✗ недоступен";
            }
        });
    }

    public void OnRootPathChanged() => _ = OnRootPathChangedAsync();

    private async Task OnRootPathChangedAsync()
    {
        // Same order StartTimers uses (structure first, then status) — EnsureHierarchy may create the
        // tree, which changes the file count RefreshDiskStatus reports.
        await EnsureHierarchyAsync();
        await RefreshDiskStatusAsync();
    }

    public void ReloadSidebarApps()
    {
        QuickApps.Clear();
        foreach (var app in _services.Cfg.QuickApps())
            QuickApps.Add(new QuickAppItem(app.Name, app.Path));

        var mode = _services.Cfg.QuickAppsDisplayMode();
        var onTop = mode is "top" or "top_labeled";
        var hasApps = QuickApps.Count > 0;
        QuickAppsSidebarVisible = hasApps && !onTop;
        QuickAppsTopVisible = hasApps && onTop;
        QuickAppsTopShowLabels = mode == "top_labeled";
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
            _syncRepeatTimer.Tick += async (_, _) => await RunSyncAsync();
            _syncRepeatTimer.Start();
        }

        if (_configPullRepeatTimer is not null)
        {
            _configPullRepeatTimer.Interval = TimeSpan.FromMinutes(minutes);
        }
        else
        {
            _configPullRepeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
            _configPullRepeatTimer.Tick += async (_, _) => await CheckForConfigUpdateAsync();
            _configPullRepeatTimer.Start();
        }
    }

    void IAppHost.Navigate(string pageId) => Navigate(pageId);

    public IBusyScope BeginBusy(string text) => Busy.Begin(text);

    /// <summary>Only the administrator gets an auto-push timer — everyone else just pulls (see
    /// StartTimers/_configPullRepeatTimer above). Safe to call any time (role switch, or right
    /// after NetworkSyncView saves config_push_interval_min) since it always stops any previous
    /// timer before deciding whether to start a new one. No separate on/off checkbox — the interval
    /// alone carries that (0 = disabled), same pattern as sync_interval_min/inspection auto-cleanup.</summary>
    public void RefreshConfigSync()
    {
        _configPushTimer?.Stop();
        _configPushTimer = null;
        if (CurrentRole != "administrator") return;

        var minutes = _services.Cfg.ConfigPushIntervalMin();
        if (minutes <= 0) return; // 0 = auto-push disabled — see footnote in NetworkSyncView

        _configPushTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
        _configPushTimer.Tick += async (_, _) => await PushConfigNowAsync();
        _configPushTimer.Start();
    }

    /// <summary>Изменение ОБЩЕГО справочника (тип/подтип шкафа, контроллер, производитель ПЧ/УПП,
    /// тег, расширение) уезжает на сетевой диск сразу, а не ждёт таймера автоотправки.
    ///
    /// Почему это понадобилось: приём чужого конфига включён у всех и по умолчанию (sync_interval_min
    /// = 5 мин), а ОТПРАВКА — только у администратора и по умолчанию ВЫКЛЮЧЕНА
    /// (config_push_interval_min = 0, см. ConfigService). То есть добавленный производитель ПЧ/УПП
    /// физически не мог доехать до коллег, пока администратор отдельно не зайдёт на страницу
    /// «Сетевые диски» и не нажмёт «Отправить сейчас» — а он про это не знал и справедливо считал,
    /// что синхронизация сломана («добавил производителей — у коллеги их нет»). Правки merge-логики
    /// (Database.FlatLists) этого не лечили: отправлять было нечего.
    ///
    /// Порядок «сначала забрать, потом отдать» обязателен: Export перезаписывает ВЕСЬ общий снимок,
    /// и отдать свой, не забрав перед этим чужой, — известный способ затереть чужие правки (см.
    /// ConfigSyncService.PushAppUsersOnly). CheckForConfigUpdate — ровно то же, что делает
    /// периодический таймер приёма, так что дороже обычного тика это не стоит.
    ///
    /// Только администратор: полный экспорт другим ролям запрещён намеренно (там же). Для них метод
    /// работает как обычный ShowStatus — правка остаётся локальной, ровно как и раньше.</summary>
    public void PushCatalogChange(string what) => _ = PushCatalogChangeAsync(what);

    private async Task PushCatalogChangeAsync(string what)
    {
        if (CurrentRole != "administrator")
        {
            ShowStatus(what, category: NotificationCategory.Hierarchy);
            return;
        }

        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root))
        {
            ShowStatus($"{what}. На сетевой диск не отправлено — диск недоступен, у коллег изменение не появится",
                10000, NotificationCategory.Hierarchy);
            return;
        }

        try
        {
            using var busy = Busy.Begin("Отправка справочника на диск…");
            await CheckForConfigUpdateAsync();
            await ConfigSyncService.ExportAsync(_services, root, $"{_services.CurrentUserName} ({RoleLabel})");
            ShowStatus($"{what} · отправлено на сетевой диск", 5000, NotificationCategory.Hierarchy);
        }
        catch (Exception ex)
        {
            ShowStatus($"{what}. Не удалось отправить на сетевой диск: {ex.Message}", 12000, NotificationCategory.Hierarchy);
        }
    }

    /// <summary>Runs silently on SUCCESS — no status-bar toast — same reasoning as
    /// CheckForConfigUpdate: this fires every config_push_interval_min minutes, and a repeated toast
    /// for routine background activity reads as spam. ConfigSyncService.Export already persists
    /// config_last_pushed_at, which NetworkSyncView.RefreshIfActive surfaces passively whenever the
    /// user opens that page. A FAILURE is different: previously swallowed completely silently forever
    /// (a share that goes unreachable/read-only for hours meant the administrator's "every 1 minute"
    /// setting just quietly did nothing, with zero trace anywhere — colleagues only noticed their
    /// changes weren't reaching other machines, no evidence pointed at the push side at all). Only
    /// notifies on the state TRANSITION (first failure after a success, and recovery after failures)
    /// so a share that's down for an extended stretch doesn't spam one toast per tick.</summary>
    private async Task PushConfigNowAsync()
    {
        var root = _services.Cfg.RootPath();
        try
        {
            var exportedBy = $"{_services.CurrentUserName} ({RoleLabel})";
            using (Busy.Begin("Отправка конфига на диск…"))
                await ConfigSyncService.ExportAsync(_services, root, exportedBy);
            if (_configPushLastFailed)
            {
                _configPushLastFailed = false;
                ShowStatus("Автоотправка конфига на диск восстановлена", 8000, NotificationCategory.Sync);
            }
        }
        catch (Exception ex)
        {
            if (!_configPushLastFailed)
            {
                _configPushLastFailed = true;
                AddNotification($"Автоотправка конфига на диск не удалась: {ex.Message}", NotificationCategory.Sync);
            }
        }
    }
}
