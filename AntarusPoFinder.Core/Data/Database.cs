using System;
using System.Collections.Generic;
using System.IO;
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

             CREATE TABLE IF NOT EXISTS settings (
                 key   TEXT PRIMARY KEY,
                 value TEXT NOT NULL DEFAULT ''
             );

             CREATE TABLE IF NOT EXISTS tags (
                 id   INTEGER PRIMARY KEY AUTOINCREMENT,
                 name TEXT UNIQUE NOT NULL COLLATE NOCASE
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
             """);

        EnsureColumnsExist();
        SeedHierarchyDefaults();
        SeedAllowedExtensionsDefaults();
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
    }

    /// <summary>Backfills the tags table from words already used in fw_versions.tags, so tags typed
    /// before this table existed still show up for autocomplete/CRUD instead of disappearing.</summary>
    private void SeedTagsFromExistingFwVersions()
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = ExecuteReader("SELECT tags FROM fw_versions WHERE tags IS NOT NULL AND tags != ''"))
        {
            while (reader.Read())
                foreach (var w in reader.GetString(0).Split(' ', StringSplitOptions.RemoveEmptyEntries))
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
        AddColumnsIfMissing("equipment_groups", ("prefix", "INTEGER NOT NULL DEFAULT 0"), ("sort_order", "INTEGER NOT NULL DEFAULT 0"), ("sync_id", "TEXT NOT NULL DEFAULT ''"));
        AddColumnsIfMissing("equipment_subtypes", ("prefix", "INTEGER NOT NULL DEFAULT 0"), ("folder_name", "TEXT NOT NULL DEFAULT ''"), ("sort_order", "INTEGER NOT NULL DEFAULT 0"), ("sync_id", "TEXT NOT NULL DEFAULT ''"));
        AddColumnsIfMissing("controller_models", ("prefix", "INTEGER NOT NULL DEFAULT 0"), ("sort_order", "INTEGER NOT NULL DEFAULT 0"), ("sync_id", "TEXT NOT NULL DEFAULT ''"));
        AddColumnsIfMissing("controller_modifications", ("hw_version", "INTEGER NOT NULL DEFAULT 0"), ("sort_order", "INTEGER NOT NULL DEFAULT 0"), ("description", "TEXT NOT NULL DEFAULT ''"), ("sync_id", "TEXT NOT NULL DEFAULT ''"));

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
