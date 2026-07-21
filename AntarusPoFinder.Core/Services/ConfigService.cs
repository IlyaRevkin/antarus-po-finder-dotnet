using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

public class QuickApp
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
}

/// <summary>Typed settings access backed by the SQLite settings table. Mirrors config_service.py.</summary>
public class ConfigService
{
    public static readonly string AppData =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AntarusPOFinder");
    public static readonly string DbPath = Path.Combine(AppData, "po_finder.db");
    public static readonly string LocalFw = Path.Combine(AppData, "firmware");
    public static readonly string LocalTemplates = Path.Combine(AppData, "templates");

    /// <summary>Тип пуска — fixed set, matches the Python app's LAUNCH_TYPES.</summary>
    public static readonly string[] LaunchTypes = ["УПП", "ПП", "ПЧ", "КПЧ"];

    private static readonly Dictionary<string, string> Defaults = new()
    {
        ["root_path"] = @"Z:\Software\Antarus Finder",
        ["second_disk_path"] = "",
        ["inspection_folder"] = "",
        ["admin_password"] = "12345",
        ["programmer_password"] = "",
        ["current_role"] = "naladchik",
        ["theme"] = "light",
        ["keep_archives"] = "false",
        ["image_server_port"] = "9876",
        ["ad_domain"] = "",
        ["ad_group_administrator"] = "",
        ["ad_group_programmer"] = "",
        ["ad_group_naladchik"] = "",
        ["ad_auth_mode"] = "ldap",
        ["ad_http_url"] = "",
        ["sync_interval_min"] = "5",
        ["quick_apps"] = "[]",
        ["app_update_path"] = "",
        ["app_auto_update"] = "false",
        ["fw_auto_update_dirs"] = "[]",
        ["config_last_synced_at"] = "",
        ["scan_resolution_dpi"] = "200",
        ["config_auto_push"] = "false",
        ["config_push_interval_min"] = "30",
        ["reservation_ttl_hours"] = "72",
        ["onboarding_shown"] = "false",
        ["notification_categories_disabled"] = "[]",
        ["close_action"] = "close",
        ["inspection_auto_cleanup_days"] = "0",
        ["inspection_auto_cleanup_minutes"] = "",
        ["quick_apps_display_mode"] = "sidebar",
        ["app_start_minimized"] = "true",
    };

    private readonly Database _db;

    public ConfigService(Database db)
    {
        _db = db;
        Directory.CreateDirectory(AppData);
        Directory.CreateDirectory(LocalFw);
        Directory.CreateDirectory(LocalTemplates);
    }

    public string Get(string key) => _db.GetSetting(key, Defaults.GetValueOrDefault(key, ""));
    public void Set(string key, string value) => _db.SetSetting(key, value);

    public string RootPath() => Get("root_path");
    public void SetRootPath(string path) => Set("root_path", path);

    public string SecondDiskPath() => Get("second_disk_path");
    public void SetSecondDiskPath(string path) => Set("second_disk_path", path);

    /// <summary>Сетевая папка с релизными .exe приложения (см. AppUpdateService) — отдельная от root_path,
    /// т.к. обновление приложения логически не связано с диском прошивок.</summary>
    public string AppUpdatePath() => Get("app_update_path");
    public void SetAppUpdatePath(string path) => Set("app_update_path", path);

    /// <summary>Если включено — найденное при запуске обновление ставится без подтверждения.
    /// Если выключено — показывается плашка с кнопкой «Обновить», которую нажимает пользователь.</summary>
    public bool AppAutoUpdate() => Get("app_auto_update").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetAppAutoUpdate(bool value) => Set("app_auto_update", value ? "true" : "false");

    /// <summary>Папка осмотра (фото/сканы). Defaults to LocalFw if not set.</summary>
    public string InspectionFolder()
    {
        var v = Get("inspection_folder");
        return string.IsNullOrEmpty(v) ? LocalFw : v;
    }
    public void SetInspectionFolder(string path) => Set("inspection_folder", path);
    public string ProtocolFolder() => InspectionFolder();

    /// <summary>DPI used both to request a resolution from the scanner (best-effort — not every
    /// driver honors it) and to size the resulting PDF page to the document's real physical size.</summary>
    public int ScanResolutionDpi() => int.TryParse(Get("scan_resolution_dpi"), out var v) && v > 0 ? v : 200;
    public void SetScanResolutionDpi(int dpi) => Set("scan_resolution_dpi", dpi.ToString());

    /// <summary>exported_at value of the last shared config this machine actually applied — lets
    /// the startup check tell "a newer export exists on the share" from "we're already current".</summary>
    public string ConfigLastSyncedAt() => Get("config_last_synced_at");
    public void SetConfigLastSyncedAt(string exportedAt) => Set("config_last_synced_at", exportedAt);

    /// <summary>exported_at value this machine last wrote to the share (manual "Отправить сейчас" or
    /// the administrator's auto-push timer) — surfaced passively on NetworkSyncView instead of a
    /// status-bar toast on every auto-push tick.</summary>
    public string ConfigLastPushedAt() => Get("config_last_pushed_at");
    public void SetConfigLastPushedAt(string exportedAt) => Set("config_last_pushed_at", exportedAt);

    /// <summary>"ldap" (default — unchanged behaviour for existing installs) = only способ №1 (прямой
    /// LDAP-бинд, требует сетевого доступа к контроллеру домена); "http" = только способ №2 (HTTP-
    /// запрос к AdHttpUrl() с NTLM/Negotiate); "both" = пробовать LDAP, и только если домен
    /// недоступен (не если пароль неверный) — попробовать HTTP как запасной вариант. См.
    /// AntarusPoFinder.App.AdCredentialValidatorFactory за тем, как это значение превращается в
    /// конкретный IAdCredentialValidator.</summary>
    public string AdAuthMode() => Get("ad_auth_mode") switch { "http" => "http", "both" => "both", _ => "ldap" };
    public void SetAdAuthMode(string mode) => Set("ad_auth_mode", mode is "http" or "both" ? mode : "ldap");

    /// <summary>Базовый URL внутреннего веб-сервера компании для способа №2 (HTTP-проверка пароля,
    /// см. HttpAdCredentialValidator) — например https://disk.antarus.su/. Пусто по умолчанию:
    /// реальный адрес НЕ хардкодится в код, администратор вписывает его сюда, когда IT подтвердит
    /// рабочий формат (см. AdHttpUrlPlaceholder в SettingsView для подсказки в самом поле).</summary>
    public string AdHttpUrl() => Get("ad_http_url");
    public void SetAdHttpUrl(string url) => Set("ad_http_url", url.Trim());

    public string AdminPassword() => Get("admin_password");
    public string ProgrammerPassword() => Get("programmer_password");

    public string CurrentRole() => Get("current_role");
    public void SetRole(string role) => Set("current_role", role);

    public string Theme() => Get("theme");
    public void SetTheme(string theme) => Set("theme", theme);

    public bool KeepArchives() => Get("keep_archives").Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>0 = automatic pull sync disabled on this machine (see MainWindowViewModel.StartTimers/
    /// SetSyncIntervalMinutes) — manual "Синхронизировать сейчас" still works either way.</summary>
    public int SyncIntervalMin()
    {
        return int.TryParse(Get("sync_interval_min"), out var v) ? Math.Max(0, v) : 5;
    }
    public void SetSyncIntervalMin(int minutes) => Set("sync_interval_min", Math.Max(0, minutes).ToString());

    /// <summary>Administrator-only: periodically export the local config to the shared drive so
    /// naladchik/programmer clients pick up hierarchy/tag/reservation changes without the admin
    /// having to remember to click "Отправить сейчас" — see Настройки → Общие → "СИНХРОНИЗАЦИЯ".</summary>
    public bool ConfigAutoPush() => Get("config_auto_push").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetConfigAutoPush(bool value) => Set("config_auto_push", value ? "true" : "false");

    /// <summary>0 = automatic push disabled (see MainWindowViewModel.RefreshConfigSync) — manual
    /// "Отправить сейчас" still works either way.</summary>
    public int ConfigPushIntervalMin()
    {
        return int.TryParse(Get("config_push_interval_min"), out var v) ? Math.Max(0, v) : 30;
    }
    public void SetConfigPushIntervalMin(int minutes) => Set("config_push_interval_min", Math.Max(0, minutes).ToString());

    /// <summary>Whether the first-launch interactive onboarding tour has already run on this machine
    /// (per-machine, not synced — see ConfigSyncService.SkipSettingsKeys). The manual replay button
    /// in MainWindow ignores this flag entirely; it only gates the automatic one-time trigger.</summary>
    public bool OnboardingShown() => Get("onboarding_shown").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetOnboardingShown(bool value) => Set("onboarding_shown", value ? "true" : "false");

    /// <summary>Default lifetime for a new version reservation before Database.ExpireStaleReservations
    /// auto-cancels it — see Настройки → Резервация номеров. 0 = reservations never expire by default;
    /// a programmer can still override this per-reservation (see UploadView.ReserveVersion_Click).</summary>
    public int ReservationTtlHours() => int.TryParse(Get("reservation_ttl_hours"), out var v) && v >= 0 ? v : 72;
    public void SetReservationTtlHours(int hours) => Set("reservation_ttl_hours", Math.Max(0, hours).ToString());

    public List<QuickApp> QuickApps()
    {
        try { return JsonSerializer.Deserialize<List<QuickApp>>(Get("quick_apps")) ?? new(); }
        catch { return new(); }
    }

    public void SetQuickApps(List<QuickApp> apps) => Set("quick_apps", JsonSerializer.Serialize(apps));

    public int ImageServerPort() => int.TryParse(Get("image_server_port"), out var v) ? v : 9876;

    /// <summary>Local-cache directory names (see LocalFirmwareCache.SanitizeName) the user has opted
    /// into silent auto-update for — everything else just surfaces in the "Обновить" banner/window.</summary>
    public HashSet<string> FwAutoUpdateDirs()
    {
        try { return new HashSet<string>(JsonSerializer.Deserialize<List<string>>(Get("fw_auto_update_dirs")) ?? new(), StringComparer.OrdinalIgnoreCase); }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    public bool IsFwAutoUpdate(string localDir) => FwAutoUpdateDirs().Contains(localDir);

    public void SetFwAutoUpdate(string localDir, bool enabled)
    {
        var set = FwAutoUpdateDirs();
        if (enabled) set.Add(localDir); else set.Remove(localDir);
        Set("fw_auto_update_dirs", JsonSerializer.Serialize(set.ToList()));
    }

    /// <summary>Per-machine (not synced — see ConfigSyncService.SkipSettingsKeys), like scan_resolution_dpi/
    /// onboarding_shown: what one operator wants muted on their PC has nothing to do with another
    /// machine's preferences. All categories are enabled by default (empty disabled-set) so adding
    /// this feature doesn't silently mute anything for existing installs.</summary>
    public HashSet<NotificationCategory> DisabledNotificationCategories()
    {
        try
        {
            var names = JsonSerializer.Deserialize<List<string>>(Get("notification_categories_disabled")) ?? new();
            return new HashSet<NotificationCategory>(names
                .Select(n => Enum.TryParse<NotificationCategory>(n, out var c) ? (NotificationCategory?)c : null)
                .Where(c => c.HasValue)
                .Select(c => c!.Value));
        }
        catch { return new HashSet<NotificationCategory>(); }
    }

    public bool IsNotificationCategoryEnabled(NotificationCategory category) =>
        !DisabledNotificationCategories().Contains(category);

    public void SetNotificationCategoryEnabled(NotificationCategory category, bool enabled)
    {
        var set = DisabledNotificationCategories();
        if (enabled) set.Remove(category); else set.Add(category);
        Set("notification_categories_disabled", JsonSerializer.Serialize(set.Select(c => c.ToString()).ToList()));
    }

    /// <summary>"close" = закрытие окна завершает процесс как раньше (default — не менять поведение
    /// для существующих установок без явного выбора пользователя); "tray" = сворачивать в системный
    /// трей вместо закрытия. Per-machine — трей на одном ПК не должен навязываться другому.</summary>
    public string CloseAction() => Get("close_action");
    public void SetCloseAction(string action) => Set("close_action", action);

    /// <summary>Per-machine (not synced — same reasoning as close_action/theme): whether the window
    /// should start minimized regardless of how the process was launched (double-click, or the
    /// Windows autostart Run-key entry — see AutostartService in AntarusPoFinder.App, which is the
    /// source of truth for whether autostart itself is on, not a setting stored here). Read once at
    /// startup by App.OnStartup, before the window is first shown.</summary>
    public bool AppStartMinimized() => Get("app_start_minimized").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetAppStartMinimized(bool value) => Set("app_start_minimized", value ? "true" : "false");

    /// <summary>0 (default for new installs — never surprise anyone with unexpected deletion) means
    /// auto-cleanup of the Осмотр folder is off. Any other N means files older than N minutes get
    /// deleted from it periodically — see InspectionCleanupService.Cleanup, called from
    /// MainWindowViewModel.RunSync alongside the app's other periodic background checks. Per-machine
    /// (not synced — see ConfigSyncService.SkipSettingsKeys), same reasoning as inspection_folder
    /// itself: what one operator wants cleaned has nothing to do with another machine's folder.
    ///
    /// Round 34: widened from whole days (old key inspection_auto_cleanup_days) to minutes, so the
    /// UI can offer days/hours/minutes inputs instead of days only. The new key defaults to "" (an
    /// explicit "never configured on this machine" sentinel, distinct from "0") — as long as it's
    /// unset, this reads the OLD days key instead and converts it, so an existing install that had
    /// already configured e.g. "5 days" doesn't silently have its cleanup disabled just because it
    /// upgraded before ever touching the new inputs. The very first time this machine saves through
    /// the new UI (even to explicitly disable it, 0/0/0), the new key is written and takes over for
    /// good — the old key is left in place but never consulted again after that.</summary>
    public int InspectionAutoCleanupMinutes()
    {
        var raw = Get("inspection_auto_cleanup_minutes");
        if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out var minutes) && minutes >= 0)
            return minutes;

        var days = int.TryParse(Get("inspection_auto_cleanup_days"), out var d) && d >= 0 ? d : 0;
        return days * 24 * 60;
    }
    public void SetInspectionAutoCleanupMinutes(int minutes) => Set("inspection_auto_cleanup_minutes", Math.Max(0, minutes).ToString());

    /// <summary>"sidebar" (default — unchanged from how it always worked) = Быстрый доступ is a
    /// vertical list of labeled buttons at the bottom of the left sidebar's scrollable area; "top" =
    /// a horizontal row of round icon-only "dock" bubbles above the page content; "top_labeled" =
    /// same horizontal row, each bubble with its shortcut name captioned underneath. Purely a
    /// personal display preference for THIS machine (not synced — see ConfigSyncService.
    /// SkipSettingsKeys, same reasoning as close_action/theme), the underlying shortcut list itself
    /// (QuickApps()) is unaffected either way.</summary>
    public string QuickAppsDisplayMode() => Get("quick_apps_display_mode");
    public void SetQuickAppsDisplayMode(string mode) =>
        Set("quick_apps_display_mode", mode is "top" or "top_labeled" ? mode : "sidebar");
}
