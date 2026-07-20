using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AntarusPoFinder.Core.Data;

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
}
