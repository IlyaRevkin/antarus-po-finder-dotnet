using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AntarusPoFinder.Core.Data;

namespace AntarusPoFinder.App.Services;

public record ConfigUpdateInfo(string ConfigPath, string ExportedAt, string ExportedBy, int SettingsChanged, ImportCounts Diff);
public record ConfigApplyResult(int SettingsApplied, ImportCounts Counts, string ExportedAt, string ExportedBy);
public record ConfigExportResult(string ExportedAt, string ExportedBy, HierarchyExportData Hierarchy);

/// <summary>Shared logic behind Настройки → Общие → «Импорт с диска» AND the startup config-update
/// banner — both must skip/apply the exact same keys, or the banner's preview would promise
/// something the manual button (or vice versa) doesn't actually do.</summary>
public static class ConfigSyncService
{
    /// <summary>Keys that are never copied from the shared config onto this machine: identity
    /// (passwords) and paths that describe THIS machine's own local layout rather than a shared
    /// fact about the equipment catalog. Everything else (equipment catalog data) is NOT read from
    /// this flat key/value map at all — it's read as structured hierarchy data instead and merged
    /// via Database.ImportHierarchyData below, unconditionally.</summary>
    private static readonly HashSet<string> SkipKeys = new()
    {
        "exported_at", "exported_by", "equipment_groups", "equipment_subtypes", "controller_models",
        "controller_modifications", "param_manufacturers", "tags", "allowed_extensions",
        "fw_version_reservations", "fw_versions", "param_files", "app_users",
    };

    /// <summary>Every plain setting key lives on either Настройки → Общие or Настройки → Быстрый
    /// доступ, and per the operator's decision both of those tabs are configured per-machine only —
    /// nothing under them should ever be overwritten by an import from the shared config, nor
    /// offered as a "settings changed" diff in the update banner. That leaves nothing left to sync
    /// through this flat map; equipment catalog data (groups/subtypes/controllers/tags/fw_versions/
    /// etc. — "everything else") already syncs unconditionally via the separate hierarchy merge
    /// above, which doesn't go through this list at all.</summary>
    private static readonly HashSet<string> SkipSettingsKeys = new()
    {
        "root_path", "second_disk_path", "inspection_folder", "admin_password", "programmer_password",
        "current_role", "theme", "keep_archives", "image_server_port", "ad_domain",
        "ad_group_administrator", "ad_group_programmer", "ad_group_naladchik", "ad_auth_mode", "ad_http_url",
        "sync_interval_min", "quick_apps",
        "app_update_path", "app_auto_update", "fw_auto_update_dirs", "config_last_synced_at",
        "scan_resolution_dpi", "config_auto_push", "config_push_interval_min", "onboarding_shown",
        "notification_categories_disabled", "close_action", "inspection_auto_cleanup_days",
        "inspection_auto_cleanup_minutes", "quick_apps_display_mode",
    };

    public static string ConfigPathFor(string root) => Path.Combine(root, "Конфиг", "po_finder_config.json");

    /// <summary>Returns null if there's nothing new to offer (no shared drive, no file, or this
    /// machine already applied that exact export) — that's the expected steady state, so it's
    /// silent. Never throws — but if the share IS reachable and something about the shared config
    /// itself is broken (malformed JSON, permission error), that's an actionable problem, not a
    /// "no update" state, so it comes back via <paramref name="error"/> instead of vanishing —
    /// the caller can surface it (e.g. status bar) without blocking the app on it.</summary>
    public static ConfigUpdateInfo? CheckForUpdate(AppServices services, out string? error)
    {
        error = null;
        try
        {
            var root = services.Cfg.RootPath();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;

            var path = ConfigPathFor(root);
            if (!File.Exists(path)) return null;

            var (rootNode, hierarchyData) = Parse(path);
            var exportedAt = rootNode["exported_at"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(exportedAt) || string.CompareOrdinal(exportedAt, services.Cfg.ConfigLastSyncedAt()) <= 0)
                return null;

            var exportedBy = rootNode["exported_by"]?.GetValue<string>() ?? "?";
            var settingsChanged = CountSettingsChanges(services, rootNode);
            var diff = services.Db.PreviewImportHierarchyData(hierarchyData);
            if (settingsChanged == 0 && diff.TotalChanges == 0) return null;

            return new ConfigUpdateInfo(path, exportedAt, exportedBy, settingsChanged, diff);
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>Applies the shared config for real — used by both the manual Import button and the
    /// banner's "Обновить сейчас". Records exported_at as this machine's new sync watermark so the
    /// banner doesn't nag again for the same export.</summary>
    public static ConfigApplyResult Apply(AppServices services, string configPath, string currentRoot)
    {
        var (rootNode, hierarchyData) = Parse(configPath);
        var oldRoot = rootNode["root_path"]?.GetValue<string>() ?? "";
        var exportedAt = rootNode["exported_at"]?.GetValue<string>() ?? "?";
        var exportedBy = rootNode["exported_by"]?.GetValue<string>() ?? "?";

        int settingsApplied = 0;
        foreach (var kv in rootNode)
        {
            if (SkipKeys.Contains(kv.Key) || SkipSettingsKeys.Contains(kv.Key)) continue;
            services.Cfg.Set(kv.Key, kv.Value?.GetValue<string>() ?? "");
            settingsApplied++;
        }

        if (!string.IsNullOrEmpty(oldRoot) && oldRoot != currentRoot)
            services.Db.RemapFwPaths(oldRoot, currentRoot);

        var counts = services.Db.ImportHierarchyData(hierarchyData);
        services.Hierarchy.SyncFwFromDisk(currentRoot);
        services.Cfg.SetConfigLastSyncedAt(exportedAt);

        return new ConfigApplyResult(settingsApplied, counts, exportedAt, exportedBy);
    }

    /// <summary>Writes the shared config file — used by the manual "Отправить сейчас"/"Экспорт на
    /// диск" buttons and the administrator's periodic auto-push timer. Throws if the shared drive
    /// isn't reachable; callers decide how to surface that (message box vs. a swallowed background
    /// tick).</summary>
    public static ConfigExportResult Export(AppServices services, string root, string exportedBy)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            throw new DirectoryNotFoundException("Сетевой диск недоступен.");

        var configDir = Path.Combine(root, "Конфиг");
        Directory.CreateDirectory(configDir);

        var exportedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        var payload = new JsonObject
        {
            ["exported_at"] = exportedAt,
            ["exported_by"] = exportedBy,
        };
        foreach (var kv in services.Db.GetAllSettings())
            payload[kv.Key] = kv.Value;

        var hierarchy = services.Db.ExportHierarchyData();
        var hierarchyNode = JsonSerializer.SerializeToNode(hierarchy)!.AsObject();
        foreach (var kv in hierarchyNode.ToList())
        {
            hierarchyNode.Remove(kv.Key);
            payload[kv.Key] = kv.Value;
        }

        File.WriteAllText(Path.Combine(configDir, "po_finder_config.json"),
            payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // We're by definition current with what we just wrote — otherwise this same machine's own
        // pull check would immediately offer to "update" from the file it just exported.
        services.Cfg.SetConfigLastSyncedAt(exportedAt);
        services.Cfg.SetConfigLastPushedAt(exportedAt);

        return new ConfigExportResult(exportedAt, exportedBy, hierarchy);
    }

    /// <summary>Lets a non-administrator contribute their own AD-login roster entry to the shared
    /// config. Only the administrator gets the full Export() above (auto-push timer + manual button
    /// — see NetworkSyncView.PushSection/RefreshConfigSync) because it overwrites the WHOLE shared
    /// snapshot, and a random naladchik/programmer machine pushing its own possibly-stale local
    /// hierarchy could wipe tags/manufacturers other machines already have (see the "remove
    /// locally-extra" comments in Database.ImportHierarchyDataCore). That restriction had an
    /// unintended side effect: a first-time AD login on a naladchik/programmer machine created a new
    /// app_users row locally, but that row could never reach the shared file (nothing on that machine
    /// ever pushes), so no OTHER machine — including the administrator's — would ever see that
    /// colleague in Настройки → Пользователи. This reads the existing shared file (if any), merges
    /// ONLY app_users both ways (same last-writer-wins rule as a normal import), and writes back the
    /// merged roster leaving every other key exactly as read — no hierarchy/settings data from this
    /// machine is ever pushed.</summary>
    public static void PushAppUsersOnly(AppServices services, string root, string exportedBy)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            throw new DirectoryNotFoundException("Сетевой диск недоступен.");

        var configDir = Path.Combine(root, "Конфиг");
        Directory.CreateDirectory(configDir);
        var path = Path.Combine(configDir, "po_finder_config.json");

        JsonObject rootNode;
        if (File.Exists(path))
        {
            var (existingRoot, existingHierarchy) = Parse(path);
            rootNode = existingRoot;
            services.Db.MergeAppUsersOnly(existingHierarchy.AppUsers);
        }
        else
        {
            rootNode = new JsonObject();
        }

        var mergedUsers = services.Db.GetAppUsers().Select(u => new ExportedAppUser
        {
            SyncId = u.SyncId, AdLogin = u.AdLogin, Role = u.Role,
            FirstLoginAt = u.FirstLoginAt, LastLoginAt = u.LastLoginAt, RoleUpdatedAt = u.RoleUpdatedAt,
        }).ToList();

        rootNode["app_users"] = JsonSerializer.SerializeToNode(mergedUsers);
        rootNode["exported_at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        rootNode["exported_by"] = exportedBy;

        File.WriteAllText(path, rootNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int CountSettingsChanges(AppServices services, JsonObject rootNode)
    {
        var changed = 0;
        foreach (var kv in rootNode)
        {
            if (SkipKeys.Contains(kv.Key) || SkipSettingsKeys.Contains(kv.Key)) continue;
            var incoming = kv.Value?.GetValue<string>() ?? "";
            if (services.Cfg.Get(kv.Key) != incoming) changed++;
        }
        return changed;
    }

    private static (JsonObject RootNode, HierarchyExportData Hierarchy) Parse(string configPath)
    {
        var text = File.ReadAllText(configPath);
        var rootNode = JsonNode.Parse(text)!.AsObject();
        var hierarchyData = JsonSerializer.Deserialize<HierarchyExportData>(text) ?? new HierarchyExportData();
        return (rootNode, hierarchyData);
    }
}
