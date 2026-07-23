using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Infrastructure;

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
        Environment.GetEnvironmentVariable("ANTARUS_TEST_APPDATA") is { Length: > 0 } testDir
            ? testDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AntarusPOFinder");
    public static readonly string DbPath = Path.Combine(AppData, "po_finder.db");
    public static readonly string LocalFw = Path.Combine(AppData, "firmware");
    public static readonly string LocalTemplates = Path.Combine(AppData, "templates");

    /// <summary>Рабочие области лоадера (см. AntarusPoFinder.Core.Loader.LoaderWorkspace) — сборка и
    /// загрузка идут ЛОКАЛЬНО здесь, а не на сетевом диске: приложение не клиент-серверное.</summary>
    public static readonly string LocalLoader = Path.Combine(AppData, "loader");

    /// <summary>Тип пуска — fixed set. Первые четыре пришли из Python-версии (LAUNCH_TYPES); пятый
    /// (<see cref="LaunchTypeNone"/>) добавлен потому, что часть шкафов вообще не имеет типа пуска, а
    /// поле обязательное — раньше в таком случае приходилось ставить заведомо неверную галочку.
    /// Хранится в launch_types (JSON-массив) ровно так же, как остальные четыре — отдельного
    /// значения/флага в схеме нет, чтобы не плодить второй способ выразить одно и то же.</summary>
    public static readonly string[] LaunchTypes = ["УПП", "ПП", "ПЧ", "КПЧ", LaunchTypeNone];

    /// <summary>«Тип пуска отсутствует» — взаимоисключающий с остальными четырьмя (см.
    /// AntarusPoFinder.App.Views.LaunchTypeChecks: при его выборе остальные снимаются и блокируются).</summary>
    public const string LaunchTypeNone = "Отсутствует";

    private static readonly Dictionary<string, string> Defaults = new()
    {
        ["root_path"] = @"Z:\Software\Antarus Finder",
        ["second_disk_path"] = "",
        ["inspection_folder"] = "",
        // Значения ниже — фолбэк ТОЛЬКО для Get("admin_password")/Get("programmer_password"), если
        // строки settings ещё нет вовсе (крайне маловероятно после Database.SeedDefaultAdminPasswordHash,
        // который заводит хешированный "12345" уже при создании/открытии БД — см. её doc-комментарий).
        // Не хеш, потому что PasswordHasher (Core.Infrastructure) сюда не тянется как константа — но
        // VerifyAdminPassword/VerifyProgrammerPassword ниже сравнивают именно хешем, поэтому в реальности
        // этот текстовый фолбэк никогда не участвует в сравнении пароля, только в отображении/логах.
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
        ["config_push_interval_min"] = "0",
        ["reservation_ttl_hours"] = "72",
        ["onboarding_shown"] = "false",
        ["notification_categories_disabled"] = "[]",
        ["notification_categories_muted_unread"] = "[]",
        ["close_action"] = "close",
        ["inspection_auto_cleanup_days"] = "0",
        ["inspection_auto_cleanup_minutes"] = "",
        ["quick_apps_display_mode"] = "sidebar",
        // Раньше "true" — на новых установках больше не навязываем свёрнутый старт (см. также
        // Database.ResetAppStartMinimizedDefaultOnce — разовый сброс этого значения для баз,
        // созданных до этого изменения, у которых оно уже сохранено как "true").
        ["app_start_minimized"] = "false",
        ["layout_fallback_enabled"] = "true",
        ["layout_fallback_threshold"] = "3",
        ["ad_require_login"] = "false",
        ["ad_require_login_default_days"] = "14",
        ["ad_last_login"] = "",
        ["search_auto_sync"] = "true",
        ["loader_exe_path"] = "",
        ["loader_format_default"] = "false",
        ["loader_update_kernel_default"] = "false",
        ["loader_last_target"] = "",
        // Бета-опция UploadView: одна общая drag&drop-зона для файла/папки ПЛК и HMI-проекта вместо
        // двух раздельных зон. Выключено по умолчанию — раздельные зоны остаются поведением по
        // умолчанию для всех существующих и новых установок, пока программист явно не включит эту
        // опцию себе в Настройках (см. UnifiedPlcHmiZoneEnabled ниже).
        ["unified_plc_hmi_zone"] = "false",
    };

    private readonly Database _db;

    public ConfigService(Database db)
    {
        _db = db;
        Directory.CreateDirectory(AppData);
        Directory.CreateDirectory(LocalFw);
        Directory.CreateDirectory(LocalTemplates);
        Directory.CreateDirectory(LocalLoader);
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

    /// <summary>Если включено (по умолчанию) — поиск по прошивкам/параметрам/схемам, не нашедший
    /// ничего по запросу как он введён, повторяет попытку с раскладкой клавиатуры "наоборот"
    /// (см. SearchService.ConvertLayout) — на случай, если оператор забыл переключить раскладку.
    /// Выключение полностью отключает и саму подстановку, и всплывающий вопрос "это точно оно?".</summary>
    public bool LayoutFallbackEnabled() => Get("layout_fallback_enabled").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetLayoutFallbackEnabled(bool value) => Set("layout_fallback_enabled", value ? "true" : "false");

    /// <summary>How many consecutive net "да"/"нет" answers for the exact same query it takes before
    /// Database.RecordLayoutFallbackFeedback stops asking and either always applies the layout
    /// conversion or stops trying it — replaces what used to be the hardcoded
    /// Database.LayoutFallbackDecisionThreshold (still the default here and in the DB layer).</summary>
    public int LayoutFallbackThreshold() =>
        int.TryParse(Get("layout_fallback_threshold"), out var v) && v > 0 ? v : Data.Database.LayoutFallbackDecisionThreshold;
    public void SetLayoutFallbackThreshold(int value) => Set("layout_fallback_threshold", Math.Max(1, value).ToString());

    /// <summary>Автоматически подтягивать найденные поиском прошивки в локальный кэш, вместо кнопки
    /// «Синхронизировать» на каждой карточке (см. SearchView.AutoSyncMissing). Настройка личная,
    /// per-machine: локальный кэш — свойство конкретного ноутбука наладчика, а не орг-политика.</summary>
    public bool SearchAutoSync() => Get("search_auto_sync").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetSearchAutoSync(bool value) => Set("search_auto_sync", value ? "true" : "false");

    /// <summary>Путь к исполняемому файлу лоадера. Пока не используется для реального запуска —
    /// интеграции нет, — но уже сохраняется и показывается в диалоге загрузки, чтобы коллеге,
    /// подключающему настоящий лоадер, не пришлось заново придумывать, где брать этот путь.
    /// См. AntarusPoFinder.Core.Loader.FirmwareLoaderFactory.</summary>
    public string LoaderExePath() => Get("loader_exe_path");
    public void SetLoaderExePath(string path) => Set("loader_exe_path", path.Trim());

    /// <summary>Значения галочек «Форматировать» / «Обновить ядро», предлагаемые при следующем
    /// открытии диалога лоадера — оператор обычно грузит однотипные шкафы подряд.</summary>
    public bool LoaderFormatDefault() => Get("loader_format_default").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetLoaderFormatDefault(bool value) => Set("loader_format_default", value ? "true" : "false");

    public bool LoaderUpdateKernelDefault() => Get("loader_update_kernel_default").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetLoaderUpdateKernelDefault(bool value) => Set("loader_update_kernel_default", value ? "true" : "false");

    /// <summary>Последний введённый порт/адрес контроллера — подставляется в диалог лоадера.</summary>
    public string LoaderLastTarget() => Get("loader_last_target");
    public void SetLoaderLastTarget(string target) => Set("loader_last_target", target.Trim());

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

    /// <summary>When this machine last successfully READ the shared config file — set on every
    /// check (background tick or manual button), whether or not it found anything new to apply.
    /// Deliberately separate from ConfigLastSyncedAt (which only moves on an actual Apply): if this
    /// timestamp isn't advancing, the pull side isn't running at all (dead timer, unreachable share);
    /// if it IS advancing but ConfigLastSyncedAt stays put, checks are running and genuinely finding
    /// nothing to apply. Surfaced on Настройки → Сетевые диски so a "sync isn't arriving" report can
    /// actually be narrowed down instead of guessed at. Per-machine only — never synced (see
    /// ConfigSyncService.SkipSettingsKeys).</summary>
    public string ConfigLastCheckedAt() => Get("config_last_checked_at");
    public void SetConfigLastCheckedAt(string at) => Set("config_last_checked_at", at);

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

    /// <summary>Сигнал (не блокировка) для UI/лога: текущий ad_http_url задан и не https — значит
    /// логин/пароль при способе №2 уходят по сети без шифрования канала (Negotiate/NTLM сам по
    /// себе канал не шифрует, только согласовывает challenge). Не мешает работе способа —
    /// в реальном окружении на момент внедрения единственный доступный адрес мог быть только
    /// HTTP, — но должно быть видно тому, кто это настраивает. См. HttpAdCredentialValidator.IsInsecureUrl
    /// за самой проверкой схемы (общая логика, не дублируется здесь).</summary>
    public bool AdHttpUrlIsInsecure()
    {
        var url = AdHttpUrl();
        return !string.IsNullOrWhiteSpace(url) && HttpAdCredentialValidator.IsInsecureUrl(url);
    }

    /// <summary>Хранимая строка пароля администратора — с этого раунда всегда хеш (см.
    /// PasswordHasher), НИКОГДА не открытый текст пароля. Оставлена публичной только для мест,
    /// которым нужно знать «задан ли вообще пароль» (пустая/непустая строка), а не сам пароль —
    /// для реальной проверки пароля используй <see cref="VerifyAdminPassword"/>, не строковое
    /// сравнение с результатом этого метода (сравнение с хешем никогда не совпадёт с открытым
    /// текстом, который ввёл пользователь).</summary>
    public string AdminPassword() => Get("admin_password");

    /// <summary>То же самое для пароля программиста — пустая строка означает «пароль не задан»
    /// (см. VerifyProgrammerPassword), непустая — всегда хеш, никогда открытый текст.</summary>
    public string ProgrammerPassword() => Get("programmer_password");

    /// <summary>Хеширует и сохраняет новый пароль администратора. В отличие от программиста,
    /// у администратора нет режима «пароль не задан» — пустая строка здесь так и хешируется
    /// (пустой пароль), это осознанное поведение вызывающей стороны, а не сигнал «не менять».</summary>
    public void SetAdminPassword(string plainPassword) => Set("admin_password", PasswordHasher.Hash(plainPassword ?? ""));

    /// <summary>Хеширует и сохраняет новый пароль программиста — кроме одного случая: пустая
    /// строка сохраняется как есть (не хешируется), потому что пустое значение — это сигнал
    /// «пароль для роли программиста не требуется» (см. VerifyProgrammerPassword), а не «пароль —
    /// пустая строка». Если бы пустая строка тоже хешировалась, отличить один случай от другого
    /// было бы уже нельзя (оба выглядели бы как валидный непустой хеш).</summary>
    public void SetProgrammerPassword(string plainPassword) =>
        Set("programmer_password", string.IsNullOrEmpty(plainPassword) ? "" : PasswordHasher.Hash(plainPassword));

    /// <summary>Единственное место, которое должно сравнивать введённый пароль администратора с
    /// сохранённым — заменяет прежнее прямое сравнение строк (input == AdminPassword()), которое
    /// сравнивало открытый текст с открытым текстом; теперь сохранённое значение — хеш, поэтому
    /// сравнение обязано идти через PasswordHasher.Verify (соль, число итераций, сравнение с
    /// постоянным временем — см. класс).</summary>
    public bool VerifyAdminPassword(string input) => PasswordHasher.Verify(input ?? "", Get("admin_password"));

    /// <summary>Как VerifyAdminPassword, но с сохранением прежней логики «пустой пароль
    /// программиста = проверка не требуется, вход в роль пускает без пароля» — это поведение уже
    /// было в коде до этого фикса (см. RoleSwitchDialog/SettingsView до правки:
    /// `!string.IsNullOrEmpty(ProgrammerPassword()) &amp;&amp; password != ProgrammerPassword()`),
    /// здесь оно просто перенесено внутрь ConfigService вместе с самой проверкой.</summary>
    public bool VerifyProgrammerPassword(string input)
    {
        var stored = Get("programmer_password");
        return string.IsNullOrEmpty(stored) || PasswordHasher.Verify(input ?? "", stored);
    }

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
    /// having to remember to click "Отправить сейчас" — see Настройки → Сетевые диски →
    /// "ОТПРАВКА ИЗМЕНЕНИЙ НА ДИСК". 0 = automatic push disabled (see
    /// MainWindowViewModel.RefreshConfigSync) — manual "Отправить сейчас" still works either way.
    /// No separate on/off checkbox — used to have one (config_auto_push), removed as a redundant
    /// second way to express what this field's own 0-means-off already covered, same pattern as
    /// sync_interval_min/inspection_auto_cleanup_days. Defaults to 0 (off) for fresh installs.</summary>
    public int ConfigPushIntervalMin()
    {
        return int.TryParse(Get("config_push_interval_min"), out var v) ? Math.Max(0, v) : 0;
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
        // Self-healing best-effort: a corrupted/pre-migration value here just means the quick-apps
        // row starts back at empty (visibly so — the operator sees an empty list and re-adds them)
        // rather than the app failing to start over one bad setting value.
        try { return JsonSerializer.Deserialize<List<QuickApp>>(Get("quick_apps")) ?? new(); }
        catch { return new(); }
    }

    public void SetQuickApps(List<QuickApp> apps) => Set("quick_apps", JsonSerializer.Serialize(apps));

    public int ImageServerPort() => int.TryParse(Get("image_server_port"), out var v) ? v : 9876;

    /// <summary>Local-cache directory names (see LocalFirmwareCache.SanitizeName) the user has opted
    /// into silent auto-update for — everything else just surfaces in the "Обновить" banner/window.</summary>
    public HashSet<string> FwAutoUpdateDirs()
    {
        // Same self-healing reasoning as QuickApps above: a corrupted value falls back to "nothing
        // opted into auto-update" (the safe default — everything just surfaces via the manual
        // banner/window instead), not a startup failure.
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
        // Corrupted value falls back to "nothing disabled" — the safe default per the doc above
        // (every category enabled), not a startup failure over one bad setting.
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

    /// <summary>Separate from DisabledNotificationCategories above — a category can stay fully
    /// enabled (still shows in the status bar / still lands in history) while being excluded from the
    /// unread badge count on the "Уведомления" sidebar button, for chatty-but-low-priority categories
    /// the operator doesn't want bumping the badge every time (e.g. Sync's routine "Путь сохранён"
    /// toasts). Per-machine, same as notification_categories_disabled — see ConfigSyncService.
    /// SkipSettingsKeys. All categories count toward the badge by default.</summary>
    public HashSet<NotificationCategory> MutedFromUnreadNotificationCategories()
    {
        try
        {
            var names = JsonSerializer.Deserialize<List<string>>(Get("notification_categories_muted_unread")) ?? new();
            return new HashSet<NotificationCategory>(names
                .Select(n => Enum.TryParse<NotificationCategory>(n, out var c) ? (NotificationCategory?)c : null)
                .Where(c => c.HasValue)
                .Select(c => c!.Value));
        }
        // Same self-healing fallback as DisabledNotificationCategories above: corrupted value ->
        // "nothing muted" (every category still counts toward the badge, the safe default).
        catch { return new HashSet<NotificationCategory>(); }
    }

    public bool IsNotificationCategoryCountedUnread(NotificationCategory category) =>
        !MutedFromUnreadNotificationCategories().Contains(category);

    public void SetNotificationCategoryCountedUnread(NotificationCategory category, bool counted)
    {
        var set = MutedFromUnreadNotificationCategories();
        if (counted) set.Remove(category); else set.Add(category);
        Set("notification_categories_muted_unread", JsonSerializer.Serialize(set.Select(c => c.ToString()).ToList()));
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

    /// <summary>Administrator-only, per-machine (this computer's policy, like every other AD setting
    /// below it — see ConfigSyncService.SkipSettingsKeys). Off by default so existing installs keep
    /// starting exactly as before. On = App.OnStartup shows AdStartupLoginDialog before MainWindow
    /// unless this machine already has a still-valid cached session (see AdSessionService) for
    /// whichever login last authenticated here (AdLastLogin below).</summary>
    public bool AdRequireLogin() => Get("ad_require_login").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetAdRequireLogin(bool value) => Set("ad_require_login", value ? "true" : "false");

    /// <summary>Default "remember me" period (days) offered in the AD login UI — used whenever the
    /// operator leaves the picker on "как задано администратором" instead of choosing their own
    /// number of days or "всегда". Replaces what used to be a hardcoded 14.</summary>
    public int AdRequireLoginDefaultDays() => int.TryParse(Get("ad_require_login_default_days"), out var v) && v > 0 ? v : 14;
    public void SetAdRequireLoginDefaultDays(int days) => Set("ad_require_login_default_days", Math.Max(1, days).ToString());

    /// <summary>Normalized AD login (see AppUserAuthService.NormalizeAdLogin) that last successfully
    /// authenticated on THIS machine — the one AdRequireLogin's startup gate checks a cached session
    /// for. Set on every successful AD login (mandatory gate or the optional in-app switch-role
    /// dialog), never on the administrator escape hatch (that one is deliberately never cached).</summary>
    public string AdLastLogin() => Get("ad_last_login");
    public void SetAdLastLogin(string normalizedLogin) => Set("ad_last_login", normalizedLogin);

    /// <summary>Бета-опция: на странице «Загрузка прошивки» вместо двух раздельных drag&amp;drop-зон
    /// (прошивка ПЛК сверху + отдельная HMI-зона под галочкой «Добавить HMI») показывается ОДНА общая
    /// зона — файл/папку ПЛК и HMI-проект можно кинуть в неё вместе или по очереди, а приложение само
    /// определяет, что есть что, по расширению файла (см. UploadView.ClassifyAndAssignOne); если
    /// определить однозначно не удалось — переспрашивает диалогом. Выключено по умолчанию: раздельные
    /// зоны — проверенное поведение, единая зона — новая экспериментальная функция, которая может
    /// ошибиться в распознавании файлов на нестандартной структуре проекта. Задумана как per-machine
    /// настройка, как и остальные поля вкладки «Общие» (переключатель решает, как выглядит форма
    /// загрузки НА ЭТОМ компьютере, а не орг-политика) — НО в этой волне правок сознательно НЕ
    /// добавлена в ConfigSyncService.SkipSettingsKeys (синхронизация настроек вне рамок задачи), так
    /// что пока что значение может утечь в общий конфиг и подтянуться на другую машину при экспорте/
    /// импорте — если это станет проблемой, добавить "unified_plc_hmi_zone" в SkipSettingsKeys.</summary>
    public bool UnifiedPlcHmiZoneEnabled() => Get("unified_plc_hmi_zone").Equals("true", StringComparison.OrdinalIgnoreCase);
    public void SetUnifiedPlcHmiZoneEnabled(bool value) => Set("unified_plc_hmi_zone", value ? "true" : "false");
}
