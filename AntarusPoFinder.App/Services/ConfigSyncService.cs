using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Infrastructure;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Services;

/// <summary>Прочитанный и разобранный общий конфиг с сетевого диска — результат дисковой фазы,
/// который дальше разбирают уже против локальной БД (см. ConfigSyncService.CheckForUpdateAsync).
/// Revision/Changes — то, что дал маркер Конфиг\revision.json (см. ISyncTransport); Revision=0 и
/// Changes=null означают «маркера на этом диске ещё нет», т.е. legacy-путь по exported_at.</summary>
public record SharedConfigSnapshot(string Path, JsonObject RootNode, HierarchyExportData Hierarchy, int Revision = 0, List<SyncChangeEntry>? Changes = null)
{
    public string ExportedAt => RootNode["exported_at"]?.GetValue<string>() ?? "";
    public string ExportedBy => RootNode["exported_by"]?.GetValue<string>() ?? "?";

    /// <summary>«Эталонная синхронизация» (см. NetworkSyncView — кнопка «Сделать это состояние
    /// эталонным для всех» и ConfigSyncService.PrepareExport) — true, когда снимок собран
    /// authoritative-экспортом администратора: тогда Apply/ApplyAsync передают этот флаг в
    /// Database.ImportHierarchyData, и для восьми справочных сущностей (типы шкафов, подтипы,
    /// контроллеры, модификации, производители, теги, оба списка расширений) применяется полная
    /// замена — локальное отсутствует во входящем снимке значит «удалить» (с FK-предохранителем),
    /// а не «просто ещё не видели». Читается как computed-свойство из RootNode, а не отдельное
    /// поле записи — так Apply(configPath) (у которого нет отдельно сконструированного снимка с
    /// Authoritative-полем) получает то же значение автоматически, без изменения сигнатуры.
    /// Строковое "true"/"false", как и schema_version ниже — экономит отдельный JsonValue-тип.</summary>
    public bool Authoritative => RootNode["authoritative"]?.GetValue<string>() == "true";
}

internal record SharedConfigRead(SharedConfigSnapshot? Snapshot, string? Error);

/// <summary>Revision/Changes — см. SharedConfigSnapshot. CriticalSchemaMismatch — входящий
/// schema_version отличается от того, что понимает эта версия приложения (см.
/// ConfigSyncService.CurrentSchemaVersion): применяется всё равно (см. п.5 дизайна — «критическое
/// расхождение применяется принудительно с уведомлением»), это поле только чтобы
/// MainWindowViewModel мог отдельно, заметно об этом уведомить.</summary>
public record ConfigUpdateInfo(string ConfigPath, string ExportedAt, string ExportedBy, int SettingsChanged, ImportCounts Diff, int Revision = 0, List<SyncChangeEntry>? Changes = null, bool CriticalSchemaMismatch = false);
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
        "exported_at", "exported_by", "source_root_path", "equipment_groups", "equipment_subtypes",
        "controller_models", "controller_modifications", "param_manufacturers", "tags",
        "allowed_extensions", "allowed_extensions_hmi", "allowed_extensions_schematic", "fw_version_reservations", "fw_versions", "param_files", "app_users",
        "flat_list_state", "schema_version", "authoritative",
        // Признак «снимок умеет sync_id/тумбстоуны у файлов параметров» (см.
        // HierarchyExportData.ParamFilesHaveSync) — скалярный bool в корне файла, как authoritative
        // выше, поэтому проверки «это не массив/объект» мало и имя обязано быть здесь.
        "param_files_have_sync",
    };

    /// <summary>Версия формата общего конфига — сегодня меняется только вместе с реальной сменой
    /// схемы обмена (не с каждым мелким полем). Записывается в каждый экспорт (см. PrepareExport) и
    /// сверяется на приёме (см. Analyze) — расхождение считается «критическим уровнем» из п.5
    /// дизайна синхронизации: применяется принудительно (ничего не блокируем), но
    /// MainWindowViewModel обязан заметно уведомить об этом оператора через
    /// ConfigUpdateInfo.CriticalSchemaMismatch, а не проглотить молча.</summary>
    private const int CurrentSchemaVersion = 1;

    /// <summary>Сколько последних записей журнала изменений хранит маркер ревизии — см.
    /// SyncRevisionMarker.Changes и BumpRevisionMarkerCas.</summary>
    private const int MaxChangelogEntries = 50;

    /// <summary>Единственная точка, через которую ConfigSyncService обращается к каналу обмена —
    /// сегодня всегда файловая шара (см. FileShareTransport), но переключить канал на будущий
    /// HTTPS/WebDAV/обратный прокси — это заменить одну эту строку, без изменений во всей остальной
    /// логике ниже. Публичное поле (не readonly) — тесты могут подменить фабрику своей реализацией
    /// ISyncTransport, не трогая файловую систему вовсе.</summary>
    public static Func<string, ISyncTransport> TransportFactory { get; set; } = root => new FileShareTransport(root);

    /// <summary>Настройка — это всегда скалярное значение; массивы и объекты в корне файла — это
    /// выгрузка иерархии, которая читается отдельно (см. SkipKeys выше). Список имён приходится
    /// дополнять руками при каждом новом разделе выгрузки, и забытое имя раньше означало не мягкий
    /// промах, а исключение GetValue&lt;string&gt;() на всю проверку обновлений («The node must be of
    /// type 'JsonValue'») — то есть баннер обновления молча ломался целиком. Проверка по типу узла
    /// закрывает этот класс ошибок независимо от полноты списка.</summary>
    /// Вторая половина той же страховки: настройка — это всегда СТРОКА (settings хранит строки), а
    /// служебные скаляры выгрузки (authoritative, param_files_have_sync) — bool. Проверка «строка ли
    /// это на самом деле» ловит забытое имя мягким промахом вместо того же исключения
    /// GetValue&lt;string&gt;() («An element of type 'True' cannot be converted to a 'System.String'»).
    private static bool IsSetting(KeyValuePair<string, JsonNode?> kv) =>
        (kv.Value is null || (kv.Value is JsonValue v && v.TryGetValue<string>(out _)))
        && !SkipKeys.Contains(kv.Key) && !SkipSettingsKeys.Contains(kv.Key);

    /// <summary>Every plain setting key lives on either Настройки → Общие or Настройки → Быстрый
    /// доступ, and per the operator's decision both of those tabs are configured per-machine only —
    /// nothing under them should ever be overwritten by an import from the shared config, nor
    /// offered as a "settings changed" diff in the update banner. That leaves nothing left to sync
    /// through this flat map; equipment catalog data (groups/subtypes/controllers/tags/fw_versions/
    /// etc. — "everything else") already syncs unconditionally via the separate hierarchy merge
    /// above, which doesn't go through this list at all.</summary>
    private static readonly HashSet<string> SkipSettingsKeys = new()
    {
        // third_disk_path/third_disk_shortcuts — рядом со вторым диском и по той же причине: буква
        // сетевого диска у каждой машины своя, а «класть ли ярлык на первом» зависит от того, есть
        // ли рядом коллеги со старым клиентом, и это тоже решение конкретной машины.
        "root_path", "second_disk_path", "third_disk_path", "third_disk_shortcuts",
        "inspection_folder", "admin_password", "programmer_password",
        "current_role", "theme", "keep_archives", "image_server_port", "ad_domain",
        "ad_group_administrator", "ad_group_programmer", "ad_group_naladchik", "ad_auth_mode", "ad_http_url",
        "sync_interval_min", "quick_apps",
        // Подключение и лоадер — настройки конкретной машины: свой шнурок/переходник в шкаф, свой
        // экземпляр Loader, свой выбор способа входа (домен может быть недоступен ровно на одной
        // машине). Уехав в общий конфиг, любая из них ломала бы соседей.
        "oidc_authority", "oidc_client_id", "oidc_groups_claim", "sync_transport", "server_url",
        "loader_exe_path", "loader_connection_mode", "loader_plc_ip", "loader_network_adapter",
        "loader_check_link", "loader_link_timeout_ms", "loader_format_default",
        "loader_update_kernel_default", "loader_last_target",
        "app_update_path", "app_auto_update", "fw_auto_update_dirs", "config_last_synced_at", "config_last_checked_at",
        // config_last_pushed_at — тот же per-machine watermark, что и два соседних выше, но раньше
        // (до этого фикса) сюда не был добавлен: FinishExport пишет его ПОСЛЕ того, как первый
        // экспорт уже ушёл на диск, поэтому в первый payload он не попадал, а во ВТОРОЙ — уже попадал
        // (GetAllSettings() к тому моменту его уже видит) и утекал как "чужая настройка изменилась" —
        // ложный SettingsChanged=1 на приёмнике при полностью пустом реальном дифе. Обнаружено этим
        // же раундом правок (Задача 2/3 — тест на "нечего применять после второго экспорта").
        "config_last_pushed_at",
        // Снимок (хеш) синхронизируемого содержимого на момент последней отправки/приёма — чисто
        // машинно-локальный watermark: по нему плашка «готово к отправке» понимает, что правки
        // вернули справочник ровно к тому состоянию, что уже на диске (добавил тип и удалил его,
        // переименовал и вернул имя, поменял и вернул префикс) — отправлять тогда нечего, накопитель
        // очищается. В общий конфиг уходить не должен — у каждой машины он свой.
        "config_last_pushed_sig",
        "config_last_synced_revision",
        "scan_resolution_dpi", "config_push_interval_min", "onboarding_shown",
        "notification_categories_disabled", "notification_categories_muted_unread", "close_action", "inspection_auto_cleanup_days",
        "inspection_auto_cleanup_minutes", "quick_apps_display_mode", "app_start_minimized",
        "layout_fallback_enabled", "layout_fallback_threshold", "fw_usage_threshold", "last_whatsnew_shown_version",
        // Журнал изменений приложения — per-machine, как и last_whatsnew_shown_version рядом: это
        // история обновлений именно ЭТОЙ установки (что и когда здесь обновлялось), а не общий
        // справочник; уехав в общий конфиг, он смешал бы журналы разных машин.
        "app_changelog_history",
        // Принтер этикеток — имя устройства этой машины; у соседа принтер называется иначе, и чужое
        // имя молча увело бы печать в никуда. Остальные настройки печати (адрес, размер этикетки,
        // папка наклеек) СОЗНАТЕЛЬНО синхронизируются: это общая политика предприятия, задаётся один
        // раз администратором и должна доехать до всех.
        "label_printer",
        // Сдвиг макета этикетки — калибровка под конкретный экземпляр принтера (лист он тянет
        // по-своему), поэтому тоже per-machine. Остальной макет (поля, кегли, стиль QR) — общая
        // политика и синхронизируется вместе с размером этикетки.
        "label_offset_x_mm", "label_offset_y_mm",
        // Шаблон паспорта, выбранный в окне печати в прошлый раз — привычка конкретного наладчика, а
        // не политика. Сама папка шаблонов и метка подстановки, наоборот, синхронизируются: шаблоны
        // общие, и метка в них должна быть одна на всех.
        "passport_template_last",
        // Развёрнута ли секция бокового меню «ДЛЯ НАЛАДЧИКА» — состояние окна на конкретной машине,
        // ровно как theme/close_action рядом: у соседа своя привычка, и навязывать ей свою незачем.
        "sidebar_setup_expanded",
        "ad_require_login", "ad_require_login_default_days", "ad_last_login",
        // «Докуда эта машина уже видела тикеты» и «включён ли подробный режим синхронизации» — оба
        // чисто машинно-локальные (см. ConfigService.TicketsLastSeenAt/SyncVerbose): у каждого
        // компьютера свой момент последнего просмотра и своя настройка отладки, в общий конфиг им
        // уезжать нельзя.
        "tickets_last_seen_at", "sync_verbose",
        "search_auto_sync", "loader_exe_path", "loader_format_default", "loader_update_kernel_default",
        "loader_last_target", "unified_plc_hmi_zone",
        // Кто эта машина в общей статистике выборов и какой сброс она уже выполнила у себя (см.
        // Database.FwUsage.cs). Оба ключа обязаны остаться локальными: уехавший в общий конфиг
        // origin сделал бы все машины одним источником — их вклады затирали бы друг друга, а
        // уехавшая отметка «сброс применён» отменяла бы сам сброс на остальных машинах.
        // Сама отметка сброса (fw_usage_reset_at) — наоборот, синхронизируемая, её здесь нет.
        Core.Data.Database.UsageOriginSettingKey, "fw_usage_reset_applied_at",
        // Докуда эта машина проиграла чужие hw-переписывания (см. ConfigService.HwRewriteAppliedAt /
        // ReplayHwRewrites ниже) — локальный watermark ровно как fw_usage_reset_applied_at слева.
        // Уехав в общий конфиг, он навязал бы всем чужую отметку и часть машин пропустила бы
        // переименования (или проиграла их не в том порядке).
        "hw_rewrite_applied_at",
        // Докуда эта машина проиграла чужие переносы версий на другой контроллер — тот же локальный
        // watermark, что hw_rewrite_applied_at слева, и по той же причине не синхронизируемый.
        "ctrl_reassign_applied_at",
        // «Делиться ли своим ручным весом» — решение КАЖДОЙ машины за свой вес, поэтому per-machine
        // (см. ConfigService.FwWeightShared). Уехав в общий конфиг, оно навязало бы всем чужой выбор.
        // Заметь: fw_usage_multiplier здесь СОЗНАТЕЛЬНО НЕТ — множитель популярности синхронизируемый
        // (общая политика поиска), см. ConfigService.FwUsageMultiplier.
        "fw_weight_shared",
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
        var localRevision = LocalWatermarkRevision(services);
        var read = ReadShared(services.Cfg.RootPath(), localRevision);
        error = read.Error;
        if (read.Snapshot is null) return null;

        try { return Analyze(services, read.Snapshot); }
        catch (Exception e) { error = e.Message; return null; }
    }

    /// <summary>Та же проверка, но чтение файла с сетевого диска (единственная её медленная часть)
    /// уходит в фоновый поток, а разбор диффа против БД остаётся на вызывающем — то есть на потоке
    /// UI. Так фоновый тик синхронизации перестал вешать окно на всё время, пока диск отвечает
    /// (см. MainWindowViewModel.CheckForConfigUpdateAsync и комментарий про одно SQLite-соединение
    /// в HierarchyService).</summary>
    public static async Task<(ConfigUpdateInfo? Info, string? Error, SharedConfigSnapshot? Snapshot)> CheckForUpdateAsync(AppServices services)
    {
        var root = services.Cfg.RootPath();
        var localRevision = LocalWatermarkRevision(services);
        var read = await Task.Run(() => ReadShared(root, localRevision));
        if (read.Snapshot is null) return (null, read.Error, null);

        try { return (Analyze(services, read.Snapshot), null, read.Snapshot); }
        catch (Exception e) { return (null, e.Message, null); }
    }

    /// <summary>Дисковая фаза: прочитать и разобрать общий конфиг. В БД не ходит (кроме передаваемого
    /// снаружи <paramref name="localWatermarkRevision"/> — это уже прочитанное вызывающим значение
    /// настройки, а не отдельный поход в БД внутри этого метода).
    ///
    /// Задача 2/3: СНАЧАЛА читаем только маркер ревизии (крошечный, незашифрованный файл) — если его
    /// revision не превышает то, что эта машина уже применяла, до чтения и расшифровки самого
    /// (потенциально тяжёлого на медленной шаре) конфига дело не доходит вовсе. Отсутствие маркера
    /// (marker is null — общий диск ещё не знает о ревизиях, более старая версия приложения на
    /// машине-экспортёре, или маркер повреждён гонкой записи) — откатываемся на прежнюю схему:
    /// читаем конфиг целиком, Analyze сравнивает exported_at как раньше.</summary>
    private static SharedConfigRead ReadShared(string root, int localWatermarkRevision)
    {
        try
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return new SharedConfigRead(null, null);

            var transport = TransportFactory(root);
            SyncRevisionMarker? marker;
            try { marker = transport.ReadRevisionAsync().GetAwaiter().GetResult(); }
            catch { marker = null; }

            if (marker is not null && marker.Revision <= localWatermarkRevision)
                return new SharedConfigRead(null, null);

            var bytes = transport.ReadConfigAsync().GetAwaiter().GetResult();
            if (bytes is null) return new SharedConfigRead(null, null);

            var (rootNode, hierarchyData) = ParseBytes(bytes);
            var path = ConfigPathFor(root);
            return new SharedConfigRead(new SharedConfigSnapshot(path, rootNode, hierarchyData, marker?.Revision ?? 0, marker?.Changes), null);
        }
        catch (Exception e)
        {
            return new SharedConfigRead(null, e.Message);
        }
    }

    /// <summary>БД-фаза: есть ли в прочитанном снимке что-то новое для этой машины.</summary>
    private static ConfigUpdateInfo? Analyze(AppServices services, SharedConfigSnapshot snap)
    {
        services.Cfg.SetConfigLastCheckedAt(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));

        // Ревизия ещё не в ходу на этом общем диске (снимок пришёл по legacy-пути — см. ReadShared,
        // marker is null) — то же самое сравнение по exported_at, что было всегда, поведение не
        // меняется ни для старых общих дисков, ни для уже существующих тестов/сценариев.
        if (snap.Revision <= 0)
        {
            if (string.IsNullOrEmpty(snap.ExportedAt) || string.CompareOrdinal(snap.ExportedAt, services.Cfg.ConfigLastSyncedAt()) <= 0)
                return null;
        }
        // else: ReadShared уже отфильтровал случай «ревизия не выросла» — сюда попадают только
        // снимки строго новее последнего применённого этой машиной маркера.

        var settingsChanged = CountSettingsChanges(services, snap.RootNode);
        var diff = services.Db.PreviewImportHierarchyData(snap.Hierarchy, snap.Authoritative);

        var incomingSchema = ParseSchemaVersion(snap.RootNode);
        var criticalSchemaMismatch = incomingSchema > 0 && incomingSchema != CurrentSchemaVersion;

        if (settingsChanged == 0 && diff.TotalChanges == 0 && !criticalSchemaMismatch)
        {
            // Статистика выборов прошивки — единственное, что применяется даже когда «применять
            // нечего»: она нарочно не считается изменением (иначе плашка «Поступили изменения»
            // срабатывала бы от каждого чужого поиска), а значит без этой строки чужие выборы
            // доезжали бы только за компанию с какой-нибудь правкой справочника.
            ApplySharedUsage(services, snap);

            // Нечего применять, но эта ревизия уже «найдена» — запоминаем её как достигнутую, иначе
            // следующий тик снова читал и разбирал бы тот же самый неизменившийся конфиг целиком
            // вместо дешёвой проверки одного маркера (см. ReadShared).
            if (snap.Revision > 0) SetLocalWatermarkRevision(services, snap.Revision);
            return null;
        }

        return new ConfigUpdateInfo(snap.Path, snap.ExportedAt, snap.ExportedBy, settingsChanged, diff,
            snap.Revision, NewChangesOnly(snap.Changes, LocalWatermarkRevision(services)), criticalSchemaMismatch);
    }

    /// <summary>Из журнала маркера — только те записи, которых эта машина ещё не видела. Журнал хранит
    /// последние MaxChangelogEntries записей ЦЕЛИКОМ, поэтому без этого отсева плашка перечисляла бы
    /// весь журнал заново при каждом изменении конфига, сколько бы записей в нём ни лежало.
    ///
    /// Записи без ревизии (Revision = 0) пишет старая версия приложения — их возраст неизвестен, и
    /// показываются они только машине, которая ещё ни разу ничего отсюда не применяла (watermark = 0):
    /// иначе после обновления приложения оператор один раз получил бы весь накопленный старый журнал.</summary>
    private static List<SyncChangeEntry>? NewChangesOnly(List<SyncChangeEntry>? changes, int localRevision)
    {
        if (changes is null) return null;
        return changes.Where(c => c.Revision > localRevision || (c.Revision == 0 && localRevision == 0)).ToList();
    }

    private static int ParseSchemaVersion(JsonObject rootNode) =>
        int.TryParse(rootNode["schema_version"]?.GetValue<string>(), out var v) ? v : 0;

    /// <summary>Watermark ревизии — «последняя ревизия маркера, которую эта машина уже применила
    /// (или подтвердила, что применять нечего)», хранится как обычная настройка через
    /// ConfigService.Get/Set напрямую (config_last_synced_revision в SkipSettingsKeys выше — никогда
    /// не уезжает в общий конфиг, per-machine ровно как config_last_synced_at/config_last_checked_at
    /// рядом с ним).</summary>
    private static int LocalWatermarkRevision(AppServices services) =>
        int.TryParse(services.Cfg.Get("config_last_synced_revision"), out var v) ? v : 0;

    private static void SetLocalWatermarkRevision(AppServices services, int revision) =>
        services.Cfg.Set("config_last_synced_revision", revision.ToString());

    /// <summary>Applies the shared config for real — used by both the manual Import button and the
    /// banner's "Обновить сейчас". Records exported_at as this machine's new sync watermark so the
    /// banner doesn't nag again for the same export.</summary>
    public static ConfigApplyResult Apply(AppServices services, string configPath, string currentRoot)
    {
        var (rootNode, hierarchyData) = Parse(configPath);
        var snap = new SharedConfigSnapshot(configPath, rootNode, hierarchyData);

        var (settingsApplied, counts) = ApplyToDatabase(services, snap, currentRoot);
        services.Hierarchy.SyncFwFromDisk(currentRoot);

        var exportedAt = Watermark(snap);
        services.Cfg.SetConfigLastSyncedAt(exportedAt);
        // Этот overload принимает голый путь, а не уже прочитанный SharedConfigSnapshot — маркер
        // ревизии перечитываем отдельно (тот же общий диск, currentRoot), чтобы watermark ревизии
        // (см. LocalWatermarkRevision) продвинулся вместе с exported_at. Никогда не двигаем его
        // назад — маркер мог оказаться временно недоступен/повреждён (см. FileShareTransport.
        // ReadRevisionAsync), это best-effort довесок к основному применению, а не источник истины.
        var newRevision = ReadCurrentRevision(currentRoot);
        if (newRevision > LocalWatermarkRevision(services)) SetLocalWatermarkRevision(services, newRevision);

        // Локальное содержимое теперь равно тому, что на диске — переустанавливаем базовую сигнатуру,
        // чтобы плашка «готово к отправке» не считала только что применённое своими исходящими правками.
        RebaselineContentSignature(services);
        return new ConfigApplyResult(settingsApplied, counts, exportedAt, snap.ExportedBy);
    }

    /// <summary>Задача 1 (эталонная синхронизация) — дисковая фаза предпросмотра разницы ПЕРЕД
    /// отправкой: читает ТЕКУЩИЙ общий конфиг с диска целиком, специально НЕ через revision-гейт
    /// ReadShared (тому нужен только «выросла ли ревизия относительно того, что применила ЭТА
    /// машина» — здесь нужно ровно то, что на диске СЕЙЧАС, независимо от того, что эта машина уже
    /// видела). Пустой HierarchyExportData(), если общего конфига на диске ещё нет вовсе (самая первая
    /// синхронизация вообще) — тогда предпросмотр справедливо покажет «добавится всё, что есть
    /// локально, удалится ничего».</summary>
    public static async Task<(HierarchyExportData? OnDisk, string? Error)> ReadCurrentDiskHierarchyAsync(string root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return (null, "Сетевой диск недоступен.");

        try
        {
            return await Task.Run(() =>
            {
                var transport = TransportFactory(root);
                var bytes = transport.ReadConfigAsync().GetAwaiter().GetResult();
                if (bytes is null) return (new HierarchyExportData(), (string?)null);

                var (_, hierarchy) = ParseBytes(bytes);
                return (hierarchy, (string?)null);
            });
        }
        catch (Exception e)
        {
            return (null, e.Message);
        }
    }

    /// <summary>Задача 1 — полный предпросмотр эталонной синхронизации: свой экспорт (то, что уйдёт
    /// в эталон) против того, что СЕЙЧАС лежит в общем конфиге на диске (см.
    /// ReadCurrentDiskHierarchyAsync выше и Database.PreviewAuthoritativeDiff за тем, почему это
    /// сравнение — по именам и лучшее доступное приближение, а не точный список того, что удалится
    /// на КАЖДОЙ чужой машине). Показывается в AuthoritativeDiffDialog перед подтверждением в
    /// NetworkSyncView.PushAuthoritative_Click.</summary>
    public static async Task<(AuthoritativeSyncDiff? Diff, string? Error)> PreviewAuthoritativeSyncAsync(AppServices services, string root)
    {
        var (onDisk, error) = await ReadCurrentDiskHierarchyAsync(root);
        if (error is not null) return (null, error);

        var local = services.Db.ExportHierarchyData();
        return (Database.PreviewAuthoritativeDiff(local, onDisk!), null);
    }

    private static int ReadCurrentRevision(string root)
    {
        try { return TransportFactory(root).ReadRevisionAsync().GetAwaiter().GetResult()?.Revision ?? 0; }
        catch { return 0; }
    }

    /// <summary>То же применение, но досмотр диска (SyncFwFromDisk — обход всех папок версий на
    /// сетевом диске, самая долгая часть всей синхронизации) выполняется в фоновом потоке. Обе
    /// БД-части остаются на вызывающем потоке: одно SQLite-соединение на приложение, гонки с UI не
    /// нужны (см. HierarchyService, блок про двухфазные операции).</summary>
    public static async Task<ConfigApplyResult> ApplyAsync(AppServices services, SharedConfigSnapshot snap, string currentRoot)
    {
        var (settingsApplied, counts) = ApplyToDatabase(services, snap, currentRoot);

        var plan = services.Hierarchy.PlanFwSync(currentRoot);
        var scan = await Task.Run(() => HierarchyService.ScanFwDisk(plan));
        services.Hierarchy.ImportFwCandidates(scan);

        var exportedAt = Watermark(snap);
        services.Cfg.SetConfigLastSyncedAt(exportedAt);
        // snap уже несёт ревизию, вычитанную ReadShared вместе с самим конфигом — повторно маркер не
        // перечитываем (в отличие от Apply(configPath) выше, у которого снимка нет). Тот же
        // «никогда не назад» guard — снимок мог быть построен ДО более свежего экспорта, который
        // случился, пока applyAsync выполнялся.
        if (snap.Revision > LocalWatermarkRevision(services)) SetLocalWatermarkRevision(services, snap.Revision);

        // См. Apply(): после приёма локальное содержимое совпадает с диском — сбрасываем базовую сигнатуру.
        RebaselineContentSignature(services);
        return new ConfigApplyResult(settingsApplied, counts, exportedAt, snap.ExportedBy);
    }

    /// <summary>Отметка «до какого экспорта эта машина уже дотянулась». У файла без exported_at её
    /// нет — пишем "?", как и до разбиения на фазы, чтобы watermark не оказался пустой строкой,
    /// которая сравнивается «меньше всего на свете» и заставляла бы пересинхронизироваться вечно.</summary>
    private static string Watermark(SharedConfigSnapshot snap) =>
        string.IsNullOrEmpty(snap.ExportedAt) ? "?" : snap.ExportedAt;

    /// <summary>Читает уже разобранный снимок в локальную БД: настройки, справочники, переезд путей.
    /// Быстро (локальный SQLite) — на диск здесь не ходят вообще.</summary>
    private static (int SettingsApplied, ImportCounts Counts) ApplyToDatabase(AppServices services, SharedConfigSnapshot snap, string currentRoot)
    {
        var oldRoot = snap.RootNode["source_root_path"]?.GetValue<string>() ?? "";

        int settingsApplied = 0;
        foreach (var kv in snap.RootNode)
        {
            if (!IsSetting(kv)) continue;
            services.Cfg.Set(kv.Key, kv.Value?.GetValue<string>() ?? "");
            settingsApplied++;
        }

        // Сброс статистики выборов — до импорта, который несёт чужие вклады: сначала обнуляем всё,
        // что было до сброса, потом принимаем то, что накопилось после него.
        ApplyUsageResetMark(services);

        // Проигрываем чужие переписывания hw модификаций ДО импорта fw_versions — это ключевой момент.
        // Автор правки уже переименовал строку 044 → 1321 у себя и на общей шаре; здесь мы так же
        // переименовываем СВОЮ строку 044 → 1321 на месте, чтобы натуральный ключ совпал с тем, что
        // несёт снимок. Сделай это ПОСЛЕ ImportHierarchyData — снимок вставил бы новую строку 1321
        // (наша 044 ещё цела), и получился бы дубль + фантом «нет папки на диске» (ровно та жалоба).
        ReplayHwRewrites(services, snap.Hierarchy, currentRoot);

        // Ровно по той же причине и ровно так же ДО импорта: контроллер входит в натуральный ключ
        // синхронизации (подтип+контроллер+version_raw), поэтому перенос версии на другой контроллер,
        // не проигранный заранее, выглядел бы для импорта как «под старым контроллером строка
        // пропала, под новым появилась новая» — фантом плюс дубль.
        ReplayCtrlReassigns(services, snap.Hierarchy, currentRoot);

        var counts = services.Db.ImportHierarchyData(snap.Hierarchy, snap.Authoritative);

        // Must run AFTER ImportHierarchyData, not before: RemapFwPaths rewrites the oldRoot prefix on
        // EXISTING fw_versions/param_files rows via a plain UPDATE. Running it first (as an earlier
        // version of this fix did) is a no-op for any row this import is about to INSERT for the
        // first time — so on a machine's very first sync, freshly-imported rows kept the exporting
        // machine's raw disk path untouched, exactly the bug this whole mechanism exists to fix.
        // Safe to always run after: rows already remapped in a previous Apply() no longer have the
        // oldRoot prefix, so re-running this is a harmless no-op for them.
        if (!string.IsNullOrEmpty(oldRoot) && oldRoot != currentRoot)
            services.Db.RemapFwPaths(oldRoot, currentRoot);

        return (settingsApplied, counts);
    }

    /// <summary>Тихий приём одной только статистики выборов прошивки — для случая, когда применять
    /// больше нечего. Идемпотентен: вклад каждой машины приходит снимком и заменяется целиком (см.
    /// Database.ImportFwUsage), повторное применение того же конфига ничего не удваивает.</summary>
    private static void ApplySharedUsage(AppServices services, SharedConfigSnapshot snap)
    {
        try
        {
            ApplyUsageResetMark(services);
            if (snap.Hierarchy.FwUsage is null) return;
            services.Db.ImportFwUsage(snap.Hierarchy.FwUsage.Select(u => new SharedFwUsageRow(u.Origin, u.QueryKey,
                u.SubtypeSyncId, u.ControllerSyncId, u.VersionRaw, u.Uses, u.LastUsedAt, u.Weight)), services.Db.UsageOriginId());
        }
        catch { /* статистика — вспомогательная вещь, срывать из-за неё синхронизацию нельзя */ }
    }

    /// <summary>Проигрывает приехавшие в снимке переписывания hw модификаций (см. ExportedHwRewrite,
    /// HierarchyService.ReplayControllerHwRewrite) — по одному разу на машину. Применяет только события
    /// строго новее локального watermark (ConfigService.HwRewriteAppliedAt): у событий точная ISO-
    /// отметка времени, сравнение строковое (лексикографически = хронологически при фиксированной
    /// ширине), тот же приём, что у fw_usage_reset_at. Пустой watermark (первый запуск / только что
    /// обновлённое приложение — как раз момент выката этой фичи) означает «применить все недавние»:
    /// переигрывание идемпотентно и семантически корректно — hw есть свойство модификации контроллера,
    /// значит ВСЕ строки этого контроллера со старым hw и должны переехать на новый; строк со старым hw
    /// нет — пустая операция.
    ///
    /// Контроллер резолвится по sync_id; если он на этой машине ещё не заведён/не соотнесён (самый
    /// первый контакт — sync_id проставит ImportHierarchyData ниже) — событие пропускаем: fw_versions
    /// приедут корректными прямо из снимка, переигрывать нечего. Watermark всё равно продвигаем до
    /// самого свежего события в снимке (включая пропущенные) — их результат уже отражён в снимке.</summary>
    private static void ReplayHwRewrites(AppServices services, HierarchyExportData hierarchy, string currentRoot)
    {
        var incoming = hierarchy.HwRewrites;
        if (incoming is null || incoming.Count == 0) return;

        var watermark = services.Cfg.HwRewriteAppliedAt();

        var withTs = incoming.Where(e => !string.IsNullOrEmpty(e.Ts)).ToList();
        if (withTs.Count == 0) return;
        var maxTs = withTs.Select(e => e.Ts).Max(StringComparer.Ordinal)!;

        foreach (var e in withTs
                     .Where(e => string.CompareOrdinal(e.Ts, watermark) > 0)
                     .OrderBy(e => e.Ts, StringComparer.Ordinal))
        {
            // sync_id — основной ключ, имя — запасной: sync_id контроллера приёмник перенимает у
            // отправителя только ВНУТРИ ImportHierarchyData, а это проигрывание идёт РАНЬШЕ него (см.
            // ApplyToDatabase). На первой же синхронизации, где переименование и важно, по sync_id
            // контроллер ещё не находится — тогда матчим по имени (у всех машин оно одно и то же),
            // иначе строка навсегда осталась бы фантомом. См. Database.GetControllerIdByName.
            var ctrlId = services.Db.GetControllerIdBySyncId(e.ControllerSyncId)
                         ?? services.Db.GetControllerIdByName(e.ControllerName);
            if (ctrlId is null) continue;
            try { services.Hierarchy.ReplayControllerHwRewrite(currentRoot, ctrlId.Value, e.OldHw, e.NewHw); }
            catch { /* одно переигрывание не должно валить всю синхронизацию */ }
        }

        if (string.CompareOrdinal(maxTs, watermark) > 0)
            services.Cfg.SetHwRewriteAppliedAt(maxTs);
    }

    /// <summary>Проигрывает приехавшие в снимке переносы версий на другую модель контроллера (см.
    /// ExportedCtrlReassign, HierarchyService.ReassignFwVersionToController) — по одному разу на
    /// машину, по тому же watermark-принципу, что ReplayHwRewrites выше
    /// (ConfigService.CtrlReassignAppliedAt, строгое «строго новее», сравнение строковое по точной
    /// ISO-отметке). Пустой watermark (первый запуск / только что обновлённое приложение — момент
    /// выката этой функции) означает «применить все недавние»: проигрывание идемпотентно, а версии,
    /// которую уже перенесли, под старым контроллером просто нет — тогда это пустая операция.
    ///
    /// Подтип и КАЖДЫЙ из двух контроллеров резолвятся по sync_id с запасным резолвом по имени: sync_id
    /// приёмник перенимает у отправителя лишь ВНУТРИ ImportHierarchyData, который идёт ПОСЛЕ этого
    /// проигрывания (см. ReplayHwRewrites — там та же ловушка стоила фантомной строки навсегда).
    /// Не нашли строку под старым контроллером — событие уже применено или версия сюда ещё не
    /// доехала: в обоих случаях делать нечего, снимок принесёт корректную строку сам.</summary>
    private static void ReplayCtrlReassigns(AppServices services, HierarchyExportData hierarchy, string currentRoot)
    {
        var incoming = hierarchy.CtrlReassignments;
        if (incoming is null || incoming.Count == 0) return;

        var watermark = services.Cfg.CtrlReassignAppliedAt();
        var withTs = incoming.Where(e => !string.IsNullOrEmpty(e.Ts)).ToList();
        if (withTs.Count == 0) return;
        var maxTs = withTs.Select(e => e.Ts).Max(StringComparer.Ordinal)!;

        foreach (var e in withTs
                     .Where(e => string.CompareOrdinal(e.Ts, watermark) > 0)
                     .OrderBy(e => e.Ts, StringComparer.Ordinal))
        {
            var subId = services.Db.GetSubtypeIdBySyncIdOrName(e.SubtypeSyncId, e.GroupName, e.SubtypeName);
            var oldCtrlId = services.Db.GetControllerIdBySyncId(e.OldControllerSyncId)
                            ?? services.Db.GetControllerIdByName(e.OldControllerName);
            var newCtrlId = services.Db.GetControllerIdBySyncId(e.NewControllerSyncId)
                            ?? services.Db.GetControllerIdByName(e.NewControllerName);
            if (subId is null || oldCtrlId is null || newCtrlId is null) continue;

            var fwId = services.Db.FindFwVersionIdByNaturalKey(subId.Value, oldCtrlId.Value, e.VersionRaw, excludeId: -1);
            if (fwId is null) continue;

            try { services.Hierarchy.ReassignFwVersionToController(currentRoot, fwId.Value, newCtrlId.Value, replay: true); }
            catch { /* одно переигрывание не должно валить всю синхронизацию */ }
        }

        if (string.CompareOrdinal(maxTs, watermark) > 0)
            services.Cfg.SetCtrlReassignAppliedAt(maxTs);
    }

    /// <summary>Сброс статистики, сделанный на другой машине. Отметка времени сброса едет в общем
    /// конфиге как обычная настройка, а «какой сброс эта машина уже выполнила» — локальный ключ:
    /// без него чужой снимок с уже пустой статистикой не смог бы обнулить местные числа (пустой
    /// список ничего не удаляет — он означает «мне нечего добавить», а не «удалите всё»).</summary>
    private static void ApplyUsageResetMark(AppServices services)
    {
        var incoming = services.Cfg.FwUsageResetAt();
        if (string.IsNullOrEmpty(incoming)) return;
        if (string.CompareOrdinal(incoming, services.Cfg.FwUsageResetAppliedAt()) <= 0) return;

        services.Db.ResetAllFwUsage();
        services.Cfg.SetFwUsageResetAppliedAt(incoming);
    }

    /// <summary>Writes the shared config file — used by the manual "Отправить сейчас"/"Экспорт на
    /// диск" buttons and the administrator's periodic auto-push timer. Throws if the shared drive
    /// isn't reachable; callers decide how to surface that (message box vs. a swallowed background
    /// tick). <paramref name="changeDescriptions"/> — человекочитаемые описания того, что именно
    /// отправляется (см. Database.SyncPendingChange), которые лягут в журнал маркера ревизии (см.
    /// BumpRevisionMarkerCas) и покажутся в плашке «Поступили изменения» на принимающих машинах.
    /// Null (по умолчанию — все существующие вызовы) означает «обычный полный экспорт без списка
    /// конкретных изменений», журнал получает одну общую запись.
    ///
    /// <paramref name="authoritative"/> — «Эталонная синхронизация» (см. NetworkSyncView, кнопка
    /// «Сделать это состояние эталонным для всех»): false для всех существующих вызовов (обычная
    /// отправка «Отправить сейчас»/автоотправка), true только когда администратор явно подтвердил
    /// полную замену справочника у всех получателей — см. SharedConfigSnapshot.Authoritative и
    /// Database.ImportHierarchyData(authoritative) о том, что именно это меняет на приёме.</summary>
    public static ConfigExportResult Export(AppServices services, string root, string exportedBy, IEnumerable<string>? changeDescriptions = null, bool authoritative = false)
    {
        var prepared = PrepareExport(services, root, exportedBy, authoritative);
        WriteExport(prepared);
        BumpRevisionMarkerCas(prepared.Root, exportedBy, changeDescriptions, authoritative);
        FinishExport(services, prepared);
        return prepared.Result;
    }

    /// <summary>То же самое, но запись файла на сетевой диск (и обновление маркера ревизии) идёт в
    /// фоновом потоке — сбор данных из БД и отметка «отправлено» остаются на вызывающем потоке.</summary>
    public static async Task<ConfigExportResult> ExportAsync(AppServices services, string root, string exportedBy, IEnumerable<string>? changeDescriptions = null, bool authoritative = false)
    {
        var prepared = PrepareExport(services, root, exportedBy, authoritative);
        await Task.Run(() =>
        {
            WriteExport(prepared);
            BumpRevisionMarkerCas(prepared.Root, exportedBy, changeDescriptions, authoritative);
        });
        FinishExport(services, prepared);
        return prepared.Result;
    }

    /// <summary>Best-effort compare-and-swap маркера ревизии (Задача 6). Сам конфиг транспорт уже
    /// перезаписал (WriteExport выше) — эта функция только поднимает revision и склеивает журнал
    /// изменений. Читаем текущий маркер, пишем revision+1, перечитываем — если после записи маркер
    /// оказался не тем, что мы только что написали (кто-то вклинился между чтением и записью — на
    /// голой сетевой шаре это реальный сценарий при экспорте с двух машин почти одновременно),
    /// повторяем с уже новым базовым значением. НЕ претендует на строгую транзакцию (её на файловой
    /// шаре нет в принципе) — цель сузить окно потери чужой ревизии/записи журнала, а не закрыть его
    /// полностью. Если три попытки подряд расходятся с чужой записью — сдаёмся молча: сам конфиг уже
    /// корректно записан, revision может остаться на одну «отправку» позади реальности до следующего
    /// экспорта с этой же машины (следующий Export снова попытается поднять её).</summary>
    private static void BumpRevisionMarkerCas(string root, string exportedBy, IEnumerable<string>? changeDescriptions, bool authoritative = false)
    {
        var transport = TransportFactory(root);
        var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        // Эталонная синхронизация получает свою узнаваемую запись в журнале маркера — оператор на
        // принимающей машине видит в плашке «Поступили изменения» явное предупреждение, что произошла
        // именно полная замена справочника.
        //
        // А вот обычный экспорт БЕЗ конкретных описаний не пишет в журнал ничего. Раньше на его месте
        // появлялась заглушка «Полная синхронизация справочника», и поскольку экспорт идёт ещё и по
        // таймеру автоотправки, журнал забивался десятками одинаковых строк ни о чём — ровно то, на
        // что жаловался оператор: «пишет просто полная синхронизация справочника, и там 5 раз, и потом
        // ещё 45; это ж явно не то, что было изменено, а если изменений нет — зачем мне уведомление».
        // Сам конфиг при этом, конечно, отправляется как и прежде: пустой журнал означает лишь «нечего
        // рассказать», а не «нечего отправлять».
        var descriptions = (changeDescriptions ?? Enumerable.Empty<string>()).ToList();
        if (authoritative) descriptions.Insert(0, $"Эталонная синхронизация от {exportedBy}");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            SyncRevisionMarker? current;
            try { current = transport.ReadRevisionAsync().GetAwaiter().GetResult(); }
            catch { current = null; }

            var nextRevision = (current?.Revision ?? 0) + 1;
            var newEntries = descriptions.Select(d => new SyncChangeEntry
            {
                Ts = now, Author = exportedBy, Type = "catalog", Description = d, Revision = nextRevision,
            });
            var changes = newEntries.Concat(current?.Changes ?? new List<SyncChangeEntry>()).Take(MaxChangelogEntries).ToList();
            var marker = new SyncRevisionMarker { Revision = nextRevision, ExportedAt = now, ExportedBy = exportedBy, Changes = changes };

            try
            {
                transport.WriteRevisionAsync(marker).GetAwaiter().GetResult();
                var reread = transport.ReadRevisionAsync().GetAwaiter().GetResult();
                // Сравниваем не только revision, но и то, ЧТО именно там лежит (ExportedAt/ExportedBy) —
                // если бы сверяли только число, гонка «другая машина посчитала ТУ ЖЕ следующую
                // ревизию и переписала маркер сразу после нас» осталась бы незамеченной: числа
                // совпали бы, а наш журнал изменений оказался бы молча потерян под чужим. Полное
                // совпадение содержимого — единственный надёжный признак «это точно то, что мы сами
                // только что написали, никто не вклинился».
                if (reread is not null && reread.Revision == nextRevision && reread.ExportedAt == now && reread.ExportedBy == exportedBy)
                    return; // успех
            }
            catch
            {
                // Сетевой сбой посреди записи маркера — конфиг уже записан верно (WriteExport выше),
                // это best-effort довесок к нему, следующий Export с этой же машины поднимет ревизию
                // снова. Не бросаем — экспорт как целое не должен падать из-за маркера.
                return;
            }
        }
    }

    private record PreparedExport(string Root, string ExportPath, byte[] Bytes, ConfigExportResult Result);

    /// <summary>БД-фаза экспорта: собрать снимок и зашифровать его в память.</summary>
    private static PreparedExport PrepareExport(AppServices services, string root, string exportedBy, bool authoritative = false)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            throw new DirectoryNotFoundException("Сетевой диск недоступен.");

        var exportedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        var payload = new JsonObject
        {
            ["exported_at"] = exportedAt,
            ["exported_by"] = exportedBy,
            // Not a setting to apply on the importing machine (root_path stays per-machine, see
            // SkipSettingsKeys) — this is metadata about what root THIS machine's fw_versions/
            // param_files paths were written under, so Apply() can rewrite those absolute paths
            // onto the importing machine's own root (RemapFwPaths). Different machines can reach
            // the same physical share under different local paths (UNC vs. a mapped WebDAV drive
            // letter) — without this, imported paths point at a root that only resolves on the
            // exporting machine, and files "vanish" for everyone else.
            ["source_root_path"] = root,
            // Формат общего конфига (см. CurrentSchemaVersion) — сверяется на приёме (Analyze), не
            // блокирует применение, только даёт основание для «критического» уведомления (п.5).
            ["schema_version"] = CurrentSchemaVersion.ToString(),
            // Эталонная синхронизация (см. SharedConfigSnapshot.Authoritative выше) — строковое
            // "true"/"false" по тому же формату, что schema_version, а не JSON-булево значение: та же
            // экономия отдельного JsonValue-типа поля, читается через тот же GetValue<string>().
            ["authoritative"] = authoritative ? "true" : "false",
        };
        // SkipSettingsKeys is per-machine-only data (passwords, this machine's own disk paths,
        // AD-login-gate timers, inspection auto-cleanup schedule, etc. — see that field's doc).
        // Apply()/CheckForUpdate() already refuse to READ these back from a shared config, but
        // until now nothing stopped them from being WRITTEN into it in the first place — every
        // export silently leaked this machine's local settings (including admin/programmer
        // passwords in plaintext) onto the shared drive for every other machine to see.
        foreach (var kv in services.Db.GetAllSettings())
        {
            if (SkipSettingsKeys.Contains(kv.Key)) continue;
            payload[kv.Key] = kv.Value;
        }

        var hierarchy = services.Db.ExportHierarchyData();
        var hierarchyNode = JsonSerializer.SerializeToNode(hierarchy)!.AsObject();
        foreach (var kv in hierarchyNode.ToList())
        {
            hierarchyNode.Remove(kv.Key);
            payload[kv.Key] = kv.Value;
        }

        var exportPath = ConfigPathFor(root);
        var bytes = ConfigFileCrypto.Encrypt(payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return new PreparedExport(root, exportPath, bytes, new ConfigExportResult(exportedAt, exportedBy, hierarchy));
    }

    /// <summary>Дисковая фаза экспорта: записать готовые байты через транспорт (Задача 1 — раньше
    /// была прямая File.WriteAllBytes здесь). В БД не ходит.</summary>
    private static void WriteExport(PreparedExport prepared) =>
        TransportFactory(prepared.Root).WriteConfigAsync(prepared.Bytes).GetAwaiter().GetResult();

    /// <summary>We're by definition current with what we just wrote — otherwise this same machine's
    /// own pull check would immediately offer to "update" from the file it just exported.</summary>
    private static void FinishExport(AppServices services, PreparedExport prepared)
    {
        services.Cfg.SetConfigLastSyncedAt(prepared.Result.ExportedAt);
        services.Cfg.SetConfigLastPushedAt(prepared.Result.ExportedAt);
        // Полный экспорт по определению уносит на диск ВСЁ текущее состояние этой машины (Задача 4) —
        // значит и всё, что накопилось в sync_pending_changes, теперь отправлено.
        services.Db.ClearSyncPendingChanges();
        RebaselineContentSignature(services);
    }

    /// <summary>Настроечный ключ, куда пишется хеш синхронизируемого содержимого на момент, когда
    /// это содержимое заведомо совпадает с тем, что на общем диске (после отправки или приёма).</summary>
    public const string ContentSignatureKey = "config_last_pushed_sig";

    /// <summary>Хеш ВСЕГО, что реально синхронизируется между машинами: справочник
    /// (ExportHierarchyData — типы/подтипы/контроллеры/теги/прошивки/параметры) плюс не-локальные
    /// настройки (всё, кроме SkipSettingsKeys). Именно эта пара и уходит в общий конфиг (см.
    /// BuildExport), поэтому равенство сигнатур = «на диске уже ровно это, отправлять нечего».
    /// Волатильные поля (exported_at/exported_by/revision) в сигнатуру НЕ входят — они меняются при
    /// каждой отправке и к содержимому отношения не имеют.</summary>
    public static string ComputeContentSignature(AppServices services)
    {
        var sb = new StringBuilder();
        foreach (var kv in services.Db.GetAllSettings()
                     .Where(k => !SkipSettingsKeys.Contains(k.Key))
                     .OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
        // ExportHierarchyData читает БД детерминированным порядком (ORDER BY), поэтому сериализация
        // одного и того же состояния байт в байт совпадает от вызова к вызову.
        sb.Append(JsonSerializer.Serialize(services.Db.ExportHierarchyData()));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    /// <summary>Запомнить текущее синхронизируемое содержимое как «совпадающее с диском» — вызывается
    /// после успешной отправки И после приёма чужого конфига, т.е. в обеих точках, где локальное
    /// состояние заведомо равно дисковому.</summary>
    public static void RebaselineContentSignature(AppServices services) =>
        services.Cfg.Set(ContentSignatureKey, ComputeContentSignature(services));

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

        FileSystemHelpers.UnprotectForOwnWrite(path);
        File.WriteAllBytes(path, ConfigFileCrypto.Encrypt(rootNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        FileSystemHelpers.ProtectFromExternalEdits(path);
    }

    /// <summary>Сколько последних решений модерации живёт в общем конфиге. Ограничение того же рода,
    /// что MaxChangelogEntries: секция не должна расти в файле бесконечно, а машина, отставшая больше
    /// чем на столько решений, получит верное состояние из самих строк fw_versions снимка.</summary>
    private const int MaxModerationDecisions = 500;

    /// <summary>Узкий канал доставки РЕШЕНИЙ МОДЕРАЦИИ с ЛЮБОЙ машины — сделан по образцу
    /// PushAppUsersOnly выше и существует ровно по той же причине, только для другой беды.
    ///
    /// Полный экспорт (Export) — привилегия администратора: он перезаписывает ВЕСЬ общий снимок, и
    /// случайная машина наладчика/программиста, отправив свою возможно устаревшую иерархию, стёрла бы
    /// теги/производителей, которые уже есть у остальных. Побочный эффект этого ограничения:
    /// fw_versions в общем снимке — это состояние базы машины-ЭКСПОРТЁРА, поэтому решение модерации
    /// (вывод из модерации, архивирование, откат, удаление), принятое на машине наладчика, физически
    /// не имело пути к коллегам: своего экспорта у неё нет, а в чужом её решения нет.
    ///
    /// Здесь читается существующий общий файл (если он есть), дописывается ТОЛЬКО секция
    /// moderation_decisions (объединение того, что уже лежит на диске, и журнала этой машины,
    /// дедупликация по ExportedModerationDecision.DedupKey, хвост обрезается до
    /// MaxModerationDecisions), и файл пишется обратно — все остальные ключи остаются ровно такими,
    /// какими были прочитаны. Ни иерархия, ни настройки этой машины наружу не уходят.
    ///
    /// Прав канал не выдаёт: он доставляет уже принятое решение, а кто вправе его принимать, решает
    /// роль (страница «Модерация прошивок» — RolesConfig.RoleAccess) на стороне UI.
    ///
    /// В отличие от PushAppUsersOnly здесь ОБЯЗАТЕЛЬНО поднимается маркер ревизии: получатели с
    /// версии, где появился маркер, до самого конфига вообще не доходят, пока revision не вырос (см.
    /// ReadShared) — без этого решение лежало бы в файле мёртвым грузом до ближайшего экспорта
    /// администратора, то есть задача «работает у всех» не решалась бы вовсе.
    ///
    /// Возвращает false и НИЧЕГО не пишет, если общего конфига на диске ещё нет вовсе: дописывать
    /// решение некуда, а создавать файл с одной только своей секцией нельзя категорически — снимок с
    /// пустой иерархией для получателя означает «на источнике этих типов/подтипов/контроллеров нет»,
    /// и он честно зеркалит их удаление (см. блоки *Removed в ImportHierarchyDataCore — там оно
    /// безусловное). Решение при этом уже лежит в местном журнале и уедет первым же полным
    /// экспортом.</summary>
    public static bool PushModerationOnly(AppServices services, string root, string exportedBy)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            throw new DirectoryNotFoundException("Сетевой диск недоступен.");

        var path = Path.Combine(root, "Конфиг", "po_finder_config.json");
        if (!File.Exists(path)) return false;

        var (rootNode, existingHierarchy) = Parse(path);
        var onDisk = existingHierarchy.ModerationDecisions ?? new List<ExportedModerationDecision>();

        // Чужое сохраняем как есть, своё дописываем; хвост режем с НАЧАЛА (там самые старые записи —
        // журналы отдаются по возрастанию ts), чтобы в файле оставались самые свежие решения.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<ExportedModerationDecision>();
        foreach (var d in onDisk.Concat(services.Db.GetRecentModerationDecisions(MaxModerationDecisions)))
            if (!string.IsNullOrEmpty(d.VersionRaw) && seen.Add(d.DedupKey()))
                merged.Add(d);
        merged = merged
            .OrderBy(d => d.Ts, StringComparer.Ordinal)
            .Skip(Math.Max(0, merged.Count - MaxModerationDecisions))
            .ToList();

        rootNode["moderation_decisions"] = JsonSerializer.SerializeToNode(merged);
        rootNode["exported_at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        rootNode["exported_by"] = exportedBy;

        FileSystemHelpers.UnprotectForOwnWrite(path);
        File.WriteAllBytes(path, ConfigFileCrypto.Encrypt(rootNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        FileSystemHelpers.ProtectFromExternalEdits(path);

        // Без этого получатели даже не заглянут в файл (revision-гейт в ReadShared) — см. class doc.
        BumpRevisionMarkerCas(root, exportedBy, new[] { $"Модерация: решение от {exportedBy}" });
        return true;
    }

    /// <summary>То, что зовёт UI сразу после принятого решения модерации: записать решение в местный
    /// журнал (это делается ВСЕГДА — тогда его унесёт и обычный полный экспорт) и best-effort
    /// дописать его в общий конфиг узким каналом выше, чтобы оно доехало до коллег с ЛЮБОЙ машины, а
    /// не только с администраторской. Возвращает true, если решение реально ушло на диск; false —
    /// решение сохранено локально и ждёт ближайшего обмена (диск недоступен, файл занят и т.п.).
    /// Никогда не бросает: сорвать саму модерацию из-за недоступной шары нельзя.</summary>
    public static bool RecordAndPushModeration(AppServices services, int fwVersionId, string author) =>
        RecordAndPushModeration(services, new[] { fwVersionId }, author);

    /// <summary>То же самое для нескольких записей сразу — одно решение модерации почти всегда
    /// касается ещё и копий-ссылок этой же прошивки под другими подтипами шкафа (см.
    /// Database.GetFwVersionIdsSharingFiles): в журнал они попадают каждая своей строкой, а на диск
    /// уходят одной записью в общий конфиг.</summary>
    public static bool RecordAndPushModeration(AppServices services, IEnumerable<int> fwVersionIds, string author)
    {
        try
        {
            foreach (var id in fwVersionIds)
                services.Db.RecordModerationDecision(id, author);
        }
        catch { return false; }

        try
        {
            var root = services.Cfg.RootPath();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;
            return PushModerationOnly(services, root, author);
        }
        catch { return false; }
    }

    private static int CountSettingsChanges(AppServices services, JsonObject rootNode)
    {
        var changed = 0;
        foreach (var kv in rootNode)
        {
            if (!IsSetting(kv)) continue;
            var incoming = kv.Value?.GetValue<string>() ?? "";
            if (services.Cfg.Get(kv.Key) != incoming) changed++;
        }
        return changed;
    }

    /// <summary>ConfigFileCrypto.TryDecrypt returns null for a file that isn't in our encrypted
    /// format — that's what a shared config exported by a pre-encryption app version (or, before
    /// this feature existed at all, any config file on the share) looks like, so falling back to
    /// reading the bytes as plain UTF-8 JSON keeps those working during the rollout instead of
    /// erroring out on every machine that hasn't upgraded yet.</summary>
    private static (JsonObject RootNode, HierarchyExportData Hierarchy) Parse(string configPath) =>
        ParseBytes(File.ReadAllBytes(configPath));

    /// <summary>Тот же разбор, что раньше делал Parse(path) целиком — вынесен отдельно, потому что
    /// ReadShared теперь получает байты уже прочитанными через ISyncTransport (Задача 1), а не сам
    /// читает файл с диска.</summary>
    private static (JsonObject RootNode, HierarchyExportData Hierarchy) ParseBytes(byte[] bytes)
    {
        var text = ConfigFileCrypto.TryDecrypt(bytes) ?? Encoding.UTF8.GetString(bytes);
        var rootNode = JsonNode.Parse(text)!.AsObject();
        var hierarchyData = JsonSerializer.Deserialize<HierarchyExportData>(text) ?? new HierarchyExportData();
        return (rootNode, hierarchyData);
    }
}
