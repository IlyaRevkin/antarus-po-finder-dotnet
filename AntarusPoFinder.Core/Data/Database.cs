using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AntarusPoFinder.Core.Data;

/// <summary>
/// SQLite-backed store for the hierarchy/firmware data model. Reuses the same po_finder.db
/// file and table names as the original Python app — only the legacy rules/versions/templates
/// tables (unused by any live feature) are left untouched, never created or migrated here.
/// </summary>
public partial class Database : IDisposable
{
    private readonly SqliteConnection _conn;

    public Database(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();

        using (var pragma = _conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }

        Migrate();
    }

    private void Migrate()
    {
        Exec("""
             CREATE TABLE IF NOT EXISTS equipment_groups (
                 id         INTEGER PRIMARY KEY AUTOINCREMENT,
                 name       TEXT    UNIQUE NOT NULL,
                 prefix     INTEGER NOT NULL DEFAULT 0,
                 sort_order INTEGER NOT NULL DEFAULT 0
             );

             CREATE TABLE IF NOT EXISTS equipment_subtypes (
                 id          INTEGER PRIMARY KEY AUTOINCREMENT,
                 group_id    INTEGER NOT NULL REFERENCES equipment_groups(id) ON DELETE CASCADE,
                 name        TEXT    NOT NULL,
                 prefix      INTEGER NOT NULL DEFAULT 0,
                 folder_name TEXT    NOT NULL,
                 sort_order  INTEGER NOT NULL DEFAULT 0,
                 UNIQUE(group_id, name)
             );

             CREATE TABLE IF NOT EXISTS controller_models (
                 id         INTEGER PRIMARY KEY AUTOINCREMENT,
                 name       TEXT    UNIQUE NOT NULL,
                 prefix     INTEGER NOT NULL DEFAULT 0,
                 sort_order INTEGER NOT NULL DEFAULT 0
             );

             CREATE TABLE IF NOT EXISTS controller_modifications (
                 id            INTEGER PRIMARY KEY AUTOINCREMENT,
                 controller_id INTEGER NOT NULL REFERENCES controller_models(id) ON DELETE CASCADE,
                 display_name  TEXT    NOT NULL,
                 hw_version    INTEGER NOT NULL,
                 sort_order    INTEGER NOT NULL DEFAULT 0,
                 description   TEXT    NOT NULL DEFAULT '',
                 UNIQUE(controller_id, display_name)
             );

             CREATE TABLE IF NOT EXISTS fw_versions (
                 id                INTEGER PRIMARY KEY AUTOINCREMENT,
                 subtype_id        INTEGER REFERENCES equipment_subtypes(id),
                 controller_id     INTEGER REFERENCES controller_models(id),
                 eq_prefix         INTEGER NOT NULL DEFAULT 0,
                 sub_prefix        INTEGER NOT NULL DEFAULT 0,
                 hw_version        INTEGER NOT NULL DEFAULT 0,
                 sw_version        INTEGER NOT NULL DEFAULT 0,
                 dt_str            TEXT    NOT NULL DEFAULT '',
                 version_raw       TEXT    NOT NULL DEFAULT '',
                 filename          TEXT    NOT NULL DEFAULT '',
                 disk_path         TEXT    NOT NULL DEFAULT '',
                 local_path        TEXT    NOT NULL DEFAULT '',
                 description       TEXT    NOT NULL DEFAULT '',
                 changelog         TEXT    NOT NULL DEFAULT '',
                 launch_types      TEXT    NOT NULL DEFAULT '[]',
                 io_map_path       TEXT    NOT NULL DEFAULT '',
                 instructions_path TEXT    NOT NULL DEFAULT '',
                 hmi_path              TEXT NOT NULL DEFAULT '',
                 executable_hint       TEXT NOT NULL DEFAULT '',
                 hmi_executable_hint   TEXT NOT NULL DEFAULT '',
                 modbus_map_path       TEXT NOT NULL DEFAULT '',
                 is_opc            INTEGER NOT NULL DEFAULT 0,
                 request_num       TEXT    NOT NULL DEFAULT '',
                 cabinet_sn        TEXT    NOT NULL DEFAULT '',
                 archived          INTEGER NOT NULL DEFAULT 0,
                 upload_date       TEXT    NOT NULL,
                 tags              TEXT    NOT NULL DEFAULT '',
                 author_id         INTEGER REFERENCES users(id),
                 status            TEXT    NOT NULL DEFAULT 'active',
                 released          INTEGER NOT NULL DEFAULT 0
             );

             CREATE TABLE IF NOT EXISTS param_manufacturers (
                 id         INTEGER PRIMARY KEY AUTOINCREMENT,
                 name       TEXT UNIQUE NOT NULL,
                 sort_order INTEGER NOT NULL DEFAULT 0
             );

             CREATE TABLE IF NOT EXISTS param_files (
                 id           INTEGER PRIMARY KEY AUTOINCREMENT,
                 subtype_id   INTEGER REFERENCES equipment_subtypes(id),
                 manufacturer TEXT    NOT NULL DEFAULT '',
                 filename     TEXT    NOT NULL,
                 disk_path    TEXT    NOT NULL,
                 description  TEXT    NOT NULL DEFAULT '',
                 upload_date  TEXT    NOT NULL DEFAULT '',
                 archived     INTEGER NOT NULL DEFAULT 0,
                 tags         TEXT    NOT NULL DEFAULT ''
             );

             CREATE TABLE IF NOT EXISTS users (
                 id            INTEGER PRIMARY KEY AUTOINCREMENT,
                 name          TEXT    NOT NULL,
                 windows_login TEXT    UNIQUE NOT NULL,
                 created_at    TEXT    NOT NULL
             );

             CREATE TABLE IF NOT EXISTS allowed_extensions (
                 ext TEXT PRIMARY KEY
             );

             -- Отдельный, независимый список разрешённых расширений для HMI-проектов (.fsprj и т.п.) —
             -- полный аналог allowed_extensions выше, но проверяется при загрузке HMI-вложения, а не
             -- основной прошивки ПЛК (см. FirmwareUploadService.Prepare).
             CREATE TABLE IF NOT EXISTS allowed_extensions_hmi (
                 ext TEXT PRIMARY KEY
             );

             CREATE TABLE IF NOT EXISTS settings (
                 key   TEXT PRIMARY KEY,
                 value TEXT NOT NULL DEFAULT ''
             );

             CREATE TABLE IF NOT EXISTS tags (
                 id   INTEGER PRIMARY KEY AUTOINCREMENT,
                 name TEXT UNIQUE NOT NULL COLLATE NOCASE
             );

             -- Отметки времени удаления/возврата для плоских списков-справочников
             -- (производители ПЧ/УПП, теги, разрешённые расширения) — см. Database.FlatLists.cs
             -- о том, почему синхронизация «зеркалом» без них теряла добавленные записи.
             CREATE TABLE IF NOT EXISTS flat_list_state (
                 kind       TEXT NOT NULL,
                 name       TEXT NOT NULL COLLATE NOCASE,
                 deleted_at TEXT NOT NULL DEFAULT '',
                 revived_at TEXT NOT NULL DEFAULT '',
                 PRIMARY KEY (kind, name)
             );

             CREATE TABLE IF NOT EXISTS fw_version_reservations (
                 id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                 subtype_id              INTEGER NOT NULL REFERENCES equipment_subtypes(id),
                 controller_id           INTEGER NOT NULL REFERENCES controller_models(id),
                 hw_version              INTEGER NOT NULL DEFAULT 0,
                 version_raw             TEXT    NOT NULL DEFAULT '',
                 status                  TEXT    NOT NULL DEFAULT 'reserved',
                 reserved_by             TEXT    NOT NULL DEFAULT '',
                 reserved_at             TEXT    NOT NULL DEFAULT '',
                 fulfilled_fw_version_id INTEGER REFERENCES fw_versions(id),
                 expires_at              TEXT    NOT NULL DEFAULT ''
             );

             CREATE TABLE IF NOT EXISTS tickets (
                 id               TEXT    PRIMARY KEY,
                 ticket_type      TEXT    NOT NULL DEFAULT 'other',
                 text             TEXT    NOT NULL DEFAULT '',
                 status           TEXT    NOT NULL DEFAULT 'open',
                 created_by       TEXT    NOT NULL DEFAULT '',
                 created_by_role  TEXT    NOT NULL DEFAULT '',
                 created_at       TEXT    NOT NULL DEFAULT '',
                 updated_at       TEXT    NOT NULL DEFAULT ''
             );

             CREATE TABLE IF NOT EXISTS ticket_sync_applied (
                 filename TEXT PRIMARY KEY
             );

             CREATE TABLE IF NOT EXISTS ticket_outbox (
                 filename TEXT PRIMARY KEY,
                 payload  TEXT NOT NULL
             );

             CREATE TABLE IF NOT EXISTS app_users (
                 id              INTEGER PRIMARY KEY AUTOINCREMENT,
                 ad_login        TEXT    UNIQUE NOT NULL COLLATE NOCASE,
                 role            TEXT    NOT NULL DEFAULT 'naladchik',
                 first_login_at  TEXT    NOT NULL DEFAULT '',
                 last_login_at   TEXT    NOT NULL DEFAULT '',
                 role_updated_at TEXT    NOT NULL DEFAULT '',
                 sync_id         TEXT    NOT NULL DEFAULT ''
             );

             CREATE TABLE IF NOT EXISTS layout_fallback_feedback (
                 query_key  TEXT    PRIMARY KEY,
                 yes_count  INTEGER NOT NULL DEFAULT 0,
                 no_count   INTEGER NOT NULL DEFAULT 0,
                 decision   TEXT    NOT NULL DEFAULT ''
             );

             CREATE TABLE IF NOT EXISTS ad_login_sessions (
                 ad_login    TEXT    PRIMARY KEY NOT NULL COLLATE NOCASE,
                 mode        TEXT    NOT NULL DEFAULT 'default',
                 custom_days INTEGER NOT NULL DEFAULT 0,
                 valid_until TEXT    NOT NULL DEFAULT ''
             );

             -- Per-hierarchy-row "last time this specific entity round-tripped through a successful
             -- (non-conflicting) config sync on this machine" — see Database.ConflictResolution.cs.
             -- Deliberately separate from equipment_groups.updated_at/etc: updated_at tracks the row's
             -- own last edit, this tracks the last point both sides were known to agree, which is what
             -- the conflict detector actually needs (was THIS row edited on BOTH sides since they last
             -- agreed, not just "is updated_at recent").
             CREATE TABLE IF NOT EXISTS hierarchy_sync_watermarks (
                 sync_id                  TEXT PRIMARY KEY,
                 last_synced_at           TEXT NOT NULL DEFAULT '',
                 -- Set only by Database.ResolveHierarchyConflict — the incoming.updated_at value that
                 -- was actually part of the conflict the operator just ruled on. Lets a later sync of
                 -- that SAME still-unchanged incoming snapshot be recognized as "already decided"
                 -- unambiguously, without depending on how last_synced_at's own timestamp happens to
                 -- compare (NowIso() has 1-second resolution, and a resolution can legitimately land
                 -- in the same wall-clock second as an earlier, unrelated reconcile of this row).
                 last_resolved_incoming_at TEXT NOT NULL DEFAULT ''
             );

             -- A hierarchy row held back by ImportHierarchyDataCore because both the local copy and the
             -- incoming one were edited since their last agreed watermark — see Database.
             -- ConflictResolution.cs. Survives app restarts (same reasoning as ticket_outbox) so an
             -- operator who closes the app before resolving still sees it next launch. One row per
             -- sync_id — a second detection of the same still-unresolved conflict just replaces it
             -- (INSERT OR REPLACE), it doesn't pile up duplicates.
             -- «По такому запросу обычно ставят вот эту версию» — см. Database.FwUsage.cs.
             -- Локальная таблица: в общий конфиг не выгружается и с других машин не приезжает.
             CREATE TABLE IF NOT EXISTS fw_search_usage (
                 query_key     TEXT    NOT NULL,
                 fw_version_id INTEGER NOT NULL,
                 uses          INTEGER NOT NULL DEFAULT 0,
                 last_used_at  TEXT    NOT NULL DEFAULT '',
                 PRIMARY KEY (query_key, fw_version_id)
             );

             CREATE TABLE IF NOT EXISTS hierarchy_pending_conflicts (
                 sync_id       TEXT PRIMARY KEY,
                 entity_type   TEXT NOT NULL,
                 local_id      INTEGER NOT NULL,
                 display_label TEXT NOT NULL DEFAULT '',
                 local_json    TEXT NOT NULL,
                 incoming_json TEXT NOT NULL,
                 created_at    TEXT NOT NULL DEFAULT ''
             );

             -- Изменения справочника, накопленные этой машиной и ещё не отправленные на общий диск —
             -- см. Database.SyncPending.cs и плашку «Изменений готово к отправке» в MainWindowViewModel.
             -- Machine-local: никогда не входит в общий конфиг, не приезжает с других машин.
             CREATE TABLE IF NOT EXISTS sync_pending_changes (
                 id          INTEGER PRIMARY KEY AUTOINCREMENT,
                 ts          TEXT NOT NULL DEFAULT '',
                 author      TEXT NOT NULL DEFAULT '',
                 change_type TEXT NOT NULL DEFAULT '',
                 description TEXT NOT NULL DEFAULT ''
             );
             """);

        EnsureIndexes();
        EnsureColumnsExist();
        SeedHierarchyDefaults();
        SeedAllowedExtensionsDefaults();
        SeedAllowedExtensionsHmiDefaults();
        SeedTagsFromExistingFwVersions();
        RunDataMigrations();
        EnsureDefaultEquipmentGroups();
        EnsureDefaultEquipmentSubtypes();
        EnsureEveryGroupHasSubtype();
        EnsureDefaultControllers();
        EnsureDefaultModifications();
        // Must run LAST: every seeding step above inserts rows with no sync_id of their own
        // (they're plain INSERTs, not the sync_id-aware Upsert* methods), so backfilling here —
        // after all seeding — is what actually gives fresh installs a sync_id on every row.
        BackfillSyncIds();
        // Same reasoning, same "run after every seeding step" placement — backfills updated_at on
        // rows the seeding above inserted with plain INSERTs (never through the Upsert*/Rename*/Add*
        // methods, which are the only places that stamp it going forward).
        BackfillHierarchyUpdatedAt();
    }

    /// <summary>Индексы по колонкам, по которым реально ищут. Без них каждая проверка «какие версии
    /// уже есть у этой пары подтип/контроллер» (а их на одну синхронизацию с диском — по числу
    /// папок контроллеров) была полным перебором таблицы; на базе наладчика с сотнями версий это
    /// та самая пауза, во время которой окно «висит». Отдельно от CREATE TABLE выше: базы у всех
    /// давно созданы, IF NOT EXISTS в CREATE TABLE их бы не тронул.</summary>
    private void EnsureIndexes() => Exec("""
        CREATE INDEX IF NOT EXISTS idx_fw_versions_subtype_ctrl ON fw_versions(subtype_id, controller_id);
        CREATE INDEX IF NOT EXISTS idx_fw_versions_version_raw  ON fw_versions(version_raw);
        CREATE INDEX IF NOT EXISTS idx_param_files_subtype      ON param_files(subtype_id);
        CREATE INDEX IF NOT EXISTS idx_fw_reservations_lookup   ON fw_version_reservations(subtype_id, controller_id, hw_version);
        CREATE INDEX IF NOT EXISTS idx_fw_search_usage_version  ON fw_search_usage(fw_version_id);
        """);

    /// <summary>Gives every pre-existing hierarchy row (both genuinely old databases picking up the
    /// ALTER TABLE column for the first time, AND rows this very Migrate() call just seeded above) a
    /// real updated_at instead of the schema's '' default — an empty updated_at would otherwise look
    /// "infinitely old" to the config-sync conflict detector (Database.ConflictResolution.cs), making
    /// its very first real edit look like it raced an edit that never actually happened.</summary>
    private void BackfillHierarchyUpdatedAt()
    {
        var now = NowIso();
        foreach (var table in new[] { "equipment_groups", "equipment_subtypes", "controller_models", "controller_modifications" })
            ExecuteNonQuery($"UPDATE {table} SET updated_at=@n WHERE updated_at IS NULL OR updated_at = ''",
                cmd => cmd.Parameters.AddWithValue("@n", now));
    }

    /// <summary>Backfills the tags table from words already used in fw_versions.tags, so tags typed
    /// before this table existed still show up for autocomplete/CRUD instead of disappearing.</summary>
    private void SeedTagsFromExistingFwVersions()
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = ExecuteReader("SELECT tags FROM fw_versions WHERE tags IS NOT NULL AND tags != ''"))
        {
            while (reader.Read())
                foreach (var w in Services.TagString.Parse(reader.GetString(0)))
                    words.Add(w);
        }
        foreach (var w in words)
            ExecuteNonQuery("INSERT OR IGNORE INTO tags (name) VALUES (@n)", cmd => cmd.Parameters.AddWithValue("@n", w));
    }

    /// <summary>
    /// The po_finder.db file is reused across app versions (and originally came from the Python
    /// prototype), so CREATE TABLE IF NOT EXISTS above is a no-op on an existing table even when
    /// its column set has grown since — e.g. status/author_id/tags were added to fw_versions
    /// after some databases were already created. ALTER TABLE ADD COLUMN backfills those so old
    /// databases don't throw "no such column" on columns the current code expects.
    /// </summary>
    private void EnsureColumnsExist()
    {
        AddColumnsIfMissing("equipment_groups", ("prefix", "INTEGER NOT NULL DEFAULT 0"), ("sort_order", "INTEGER NOT NULL DEFAULT 0"), ("sync_id", "TEXT NOT NULL DEFAULT ''"), ("updated_at", "TEXT NOT NULL DEFAULT ''"));
        AddColumnsIfMissing("equipment_subtypes", ("prefix", "INTEGER NOT NULL DEFAULT 0"), ("folder_name", "TEXT NOT NULL DEFAULT ''"), ("sort_order", "INTEGER NOT NULL DEFAULT 0"), ("sync_id", "TEXT NOT NULL DEFAULT ''"), ("updated_at", "TEXT NOT NULL DEFAULT ''"));
        AddColumnsIfMissing("controller_models", ("prefix", "INTEGER NOT NULL DEFAULT 0"), ("sort_order", "INTEGER NOT NULL DEFAULT 0"), ("sync_id", "TEXT NOT NULL DEFAULT ''"), ("updated_at", "TEXT NOT NULL DEFAULT ''"));
        AddColumnsIfMissing("controller_modifications", ("hw_version", "INTEGER NOT NULL DEFAULT 0"), ("sort_order", "INTEGER NOT NULL DEFAULT 0"), ("description", "TEXT NOT NULL DEFAULT ''"), ("sync_id", "TEXT NOT NULL DEFAULT ''"), ("updated_at", "TEXT NOT NULL DEFAULT ''"));
        // deleted_at: fw_versions' own tombstone marker (Задача 3) — '' means "not deleted". Unlike
        // equipment_subtypes/controller_models above, fw_versions can't mirror deletion by mere
        // absence from an incoming sync snapshot (it's additive-only: each machine may have uploads
        // no other machine has ever seen, see ImportHierarchyDataCore's class doc), so deletion needs
        // an explicit, positively-propagated marker instead — set by Database.TombstoneFwVersion,
        // read by every fw_versions SELECT below (kept out of every normal listing/search) and by
        // ExportHierarchyData/ImportHierarchyDataCore (kept IN the sync payload so the deletion itself
        // reaches every other machine, not just this one).
        AddColumnsIfMissing("fw_versions", ("deleted_at", "TEXT NOT NULL DEFAULT ''"));

        // Задел (Задача 7): «сохранить у себя, не выгружать» — строка с is_local_only=1 просто
        // пропускается ExportHierarchyData (см. Database.ConfigExchange.cs), т.е. никогда не попадёт
        // в общий конфиг и не уедет к коллегам. UI-переключателя пока нет (минимум по задаче) —
        // только схема и фильтр экспорта, готовые для будущей галочки в UploadView.
        AddColumnsIfMissing("fw_versions", ("is_local_only", "INTEGER NOT NULL DEFAULT 0"));

        // Backfilling `released` from existing tags is only correct the ONE TIME this column is
        // introduced on an old database — after that, release status must come solely from the
        // explicit moderation confirmation (see Database.FwVersions.cs), never re-inferred from
        // tags again, or a "No" answer would flip back to released on the next app start.
        var hadReleasedColumn = ColumnExists("fw_versions", "released");

        AddColumnsIfMissing("fw_versions",
            ("eq_prefix", "INTEGER NOT NULL DEFAULT 0"),
            ("sub_prefix", "INTEGER NOT NULL DEFAULT 0"),
            ("dt_str", "TEXT NOT NULL DEFAULT ''"),
            ("version_raw", "TEXT NOT NULL DEFAULT ''"),
            ("description", "TEXT NOT NULL DEFAULT ''"),
            ("changelog", "TEXT NOT NULL DEFAULT ''"),
            ("launch_types", "TEXT NOT NULL DEFAULT '[]'"),
            ("io_map_path", "TEXT NOT NULL DEFAULT ''"),
            ("instructions_path", "TEXT NOT NULL DEFAULT ''"),
            ("hmi_path", "TEXT NOT NULL DEFAULT ''"),
            ("executable_hint", "TEXT NOT NULL DEFAULT ''"),
            ("hmi_executable_hint", "TEXT NOT NULL DEFAULT ''"),
            ("modbus_map_path", "TEXT NOT NULL DEFAULT ''"),
            ("is_opc", "INTEGER NOT NULL DEFAULT 0"),
            ("request_num", "TEXT NOT NULL DEFAULT ''"),
            ("cabinet_sn", "TEXT NOT NULL DEFAULT ''"),
            ("archived", "INTEGER NOT NULL DEFAULT 0"),
            ("tags", "TEXT NOT NULL DEFAULT ''"),
            ("author_id", "INTEGER"),
            ("status", "TEXT NOT NULL DEFAULT 'active'"),
            ("released", "INTEGER NOT NULL DEFAULT 0"));

        if (!hadReleasedColumn)
            Exec("UPDATE fw_versions SET released = CASE WHEN TRIM(tags) <> '' THEN 1 ELSE 0 END");

        AddColumnsIfMissing("param_files",
            ("manufacturer", "TEXT NOT NULL DEFAULT ''"),
            ("description", "TEXT NOT NULL DEFAULT ''"),
            ("upload_date", "TEXT NOT NULL DEFAULT ''"),
            ("archived", "INTEGER NOT NULL DEFAULT 0"),
            ("tags", "TEXT NOT NULL DEFAULT ''"));

        AddColumnsIfMissing("fw_version_reservations",
            ("expires_at", "TEXT NOT NULL DEFAULT ''"));

        AddColumnsIfMissing("hierarchy_sync_watermarks",
            ("last_resolved_incoming_at", "TEXT NOT NULL DEFAULT ''"));
    }

    /// <summary>Assigns a stable GUID to any row left with an empty sync_id — either a brand-new
    /// ALTER TABLE column on an existing database, or a row inserted by code that predates sync_id.
    /// Config export/import matches rows by this id instead of by Name, so renames sync correctly
    /// instead of looking like a delete+insert.</summary>
    private void BackfillSyncIds()
    {
        foreach (var table in new[] { "equipment_groups", "equipment_subtypes", "controller_models", "controller_modifications" })
        {
            var ids = QueryInts($"SELECT id FROM {table} WHERE sync_id IS NULL OR sync_id = ''");
            foreach (var id in ids)
                ExecuteNonQuery($"UPDATE {table} SET sync_id=@s WHERE id=@id", cmd =>
                {
                    cmd.Parameters.AddWithValue("@s", Guid.NewGuid().ToString());
                    cmd.Parameters.AddWithValue("@id", id);
                });
        }
    }

    private bool ColumnExists(string table, string column)
    {
        using var reader = ExecuteReader($"PRAGMA table_info({table})");
        while (reader.Read())
            if (string.Equals(GetString(reader, "name"), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void AddColumnsIfMissing(string table, params (string Name, string Ddl)[] columns)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = ExecuteReader($"PRAGMA table_info({table})"))
            while (reader.Read())
                existing.Add(GetString(reader, "name"));

        foreach (var (name, ddl) in columns)
            if (!existing.Contains(name))
                Exec($"ALTER TABLE {table} ADD COLUMN {name} {ddl}");
    }

    private void RunDataMigrations()
    {
        DedupeParamFiles();
        ResetAppStartMinimizedDefaultOnce();
        SeedDefaultAdminPasswordHash();
        MigratePlaintextPasswordsToHashesOnce();

        // Remove '—' subtypes for groups that also have real subtypes (groups like НГР always
        // have real subtypes; a leftover '—' entry would put controllers directly under the
        // group folder, mixed in with subtype folders).
        var staleIds = QueryInts("""
            SELECT id FROM equipment_subtypes
            WHERE name = '—' AND group_id IN (
                SELECT DISTINCT group_id FROM equipment_subtypes WHERE name != '—'
            )
            """);
        if (staleIds.Count == 0) return;

        var placeholders = IntParamPlaceholders(staleIds);
        ExecWithIntParams($"DELETE FROM fw_versions WHERE subtype_id IN ({placeholders})", staleIds);
        ExecWithIntParams($"DELETE FROM param_files WHERE subtype_id IN ({placeholders})", staleIds);
        ExecWithIntParams($"DELETE FROM equipment_subtypes WHERE name = '—' AND id IN ({placeholders})", staleIds);
    }

    /// <summary>One-off cleanup for the config-sync duplication bug: param_files used to be matched
    /// by (subtype, manufacturer, filename, disk_path) on import, but disk_path is an absolute path
    /// baked in on the EXPORTING machine, which almost never matches the local root — so every sync
    /// round re-inserted the same file as a "new" row (repro: 178 rows for 2 real files after
    /// repeated syncs). The match key is fixed (see ImportHierarchyDataCore), this collapses the
    /// duplicates that already accumulated: keeps the oldest row per (subtype, manufacturer,
    /// filename), unions everyone's tags onto it, deletes the rest. Idempotent — a clean DB has
    /// nothing to group, runs in O(rows) every startup after that.</summary>
    private void DedupeParamFiles()
    {
        var groups = new Dictionary<(int SubtypeId, string Manufacturer, string Filename), List<(int Id, string Tags)>>();
        using (var r = ExecuteReader("SELECT id, subtype_id, manufacturer, filename, tags FROM param_files WHERE archived = 0 ORDER BY id"))
        {
            while (r.Read())
            {
                if (r.IsDBNull(1)) continue;
                var key = (r.GetInt32(1), r.GetString(2), r.GetString(3));
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
                list.Add((r.GetInt32(0), GetString(r, "tags")));
            }
        }

        foreach (var list in groups.Values)
        {
            if (list.Count < 2) continue;

            var keepId = list[0].Id;
            var mergedTags = Services.TagString.Join(list.SelectMany(x => Services.TagString.Parse(x.Tags)));
            ExecuteNonQuery("UPDATE param_files SET tags=@t WHERE id=@id", cmd =>
            {
                cmd.Parameters.AddWithValue("@t", mergedTags);
                cmd.Parameters.AddWithValue("@id", keepId);
            });

            var dupIds = list.Skip(1).Select(x => x.Id).ToList();
            var ph = IntParamPlaceholders(dupIds);
            ExecWithIntParams($"DELETE FROM param_files WHERE id IN ({ph})", dupIds);
        }
    }

    /// <summary>One-off сброс: app_start_minimized раньше по умолчанию был "true" — приложение
    /// молча запускалось свёрнутым, и это раздражало пользователей, которые никогда сознательно
    /// эту галочку не ставили (см. ConfigService.Defaults, теперь дефолт "false"). Смена одного
    /// только дефолта не помогает базам, где значение уже физически сохранено в settings как
    /// "true" (например, любое сохранение через Настройки записывает все поля страницы, включая
    /// эту галочку, даже если пользователь её не трогал) — такие базы продолжили бы читать "true"
    /// из settings, а не новый дефолт. Здесь старый навязанный дефолт осознанно сбрасывается один
    /// раз, безусловно (не только когда значение равно "true" — важна не текущая величина, а сам
    /// факт, что раньше выбора не было), а флаг-маркер ниже гарантирует, что это разовое действие
    /// не повторится и не затрёт значение, которое пользователь позже выставит сам через Настройки.</summary>
    private void ResetAppStartMinimizedDefaultOnce()
    {
        const string doneFlag = "migration_app_start_minimized_reset_done";
        if (GetSetting(doneFlag) == "true") return;

        SetSetting("app_start_minimized", "false");
        SetSetting(doneFlag, "true");
    }

    /// <summary>Заводит хешированный пароль администратора по умолчанию ("12345" — тот же, что
    /// наладчики использовали всегда, только теперь не открытым текстом), если строки
    /// settings["admin_password"] ещё вообще нет. Раньше этот дефолт существовал только в коде
    /// (ConfigService.Defaults) и никогда не писался в БД, пока кто-то явно не сохранял пароль
    /// через Настройки — с переходом на хеши сравнение пароля (PasswordHasher.Verify) требует
    /// РЕАЛЬНОГО хеша в БД, а не текстовой подстановки "12345" из кода (хешу его сравнивать не с
    /// чем), поэтому здесь эта строка заводится один раз явно, при первом открытии/создании БД.
    /// Идемпотентно само по себе (проверка на пустоту), отдельный флаг-маркер не нужен — в отличие
    /// от MigratePlaintextPasswordsToHashesOnce ниже, здесь нечего "перезаписать поверх" того, что
    /// пользователь сам сохранил (Set только если строки ещё нет).</summary>
    private void SeedDefaultAdminPasswordHash()
    {
        if (GetSetting("admin_password") == "")
            SetSetting("admin_password", PasswordHasher.Hash("12345"));
    }

    /// <summary>Разовая миграция: если admin_password/programmer_password в settings уже хранятся
    /// открытым текстом (кто-то менял пароль через Настройки ДО этого фикса — реальный кастомный
    /// пароль, не дефолт "12345" из SeedDefaultAdminPasswordHash выше, который уже сеет хеш), они
    /// перехешируются на месте. Пустой programmer_password — осознанное «пароль не задан», не
    /// трогается (см. ConfigService.SetProgrammerPassword/VerifyProgrammerPassword). Флаг-маркер —
    /// тот же приём, что и в ResetAppStartMinimizedDefaultOnce выше: гарантирует, что миграция
    /// реально одноразовая (хотя PasswordHasher.IsHashed и без флага делает повторный проход
    /// безопасным no-op'ом — флаг просто экономит эту проверку на каждом старте).</summary>
    private void MigratePlaintextPasswordsToHashesOnce()
    {
        const string doneFlag = "migration_password_hash_done";
        if (GetSetting(doneFlag) == "true") return;

        foreach (var key in new[] { "admin_password", "programmer_password" })
        {
            var value = GetSetting(key);
            if (!string.IsNullOrEmpty(value) && !PasswordHasher.IsHashed(value))
                SetSetting(key, PasswordHasher.Hash(value));
        }
        SetSetting(doneFlag, "true");
    }

    /// <summary>A cabinet type (group) must never exist without at least one subtype — "—" is the
    /// placeholder for "no real subtype division" (its folder sits directly under the group, see
    /// HierarchyService.PoCtrlFolder). Older databases (or the pre-fix Settings UI, which allowed
    /// creating a group with no subtype at all) could leave a group with zero subtype rows, which
    /// also silently broke SyncFwFromDisk for that group (it has nothing to iterate). Backfill here
    /// so every group is guaranteed at least one subtype row going forward.</summary>
    private void EnsureEveryGroupHasSubtype()
    {
        var groupIds = QueryInts("""
            SELECT id FROM equipment_groups WHERE id NOT IN (SELECT DISTINCT group_id FROM equipment_subtypes)
            """);
        foreach (var groupId in groupIds)
        {
            var name = ExecuteScalar("SELECT name FROM equipment_groups WHERE id=@g",
                cmd => cmd.Parameters.AddWithValue("@g", groupId)) as string ?? "";
            ExecuteNonQuery(
                "INSERT INTO equipment_subtypes(group_id,name,prefix,folder_name,sort_order,sync_id) VALUES(@g,'—',0,@f,0,@sy)",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@g", groupId);
                    cmd.Parameters.AddWithValue("@f", name);
                    cmd.Parameters.AddWithValue("@sy", Guid.NewGuid().ToString());
                });
        }
    }

    public void Dispose()
    {
        _conn.Dispose();
    }
}
