using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    public HierarchyExportData ExportHierarchyData()
    {
        var data = new HierarchyExportData();

        using (var r = ExecuteReader("SELECT name, prefix, sort_order, sync_id FROM equipment_groups ORDER BY sort_order"))
            while (r.Read())
                data.EquipmentGroups.Add(new ExportedGroup { Name = r.GetString(0), Prefix = r.GetInt32(1), SortOrder = r.GetInt32(2), SyncId = GetString(r, "sync_id") });

        using (var r = ExecuteReader("""
            SELECT es.name, es.prefix, es.folder_name, es.sort_order, es.sync_id, eg.name AS group_name, eg.sync_id AS group_sync_id
            FROM equipment_subtypes es JOIN equipment_groups eg ON es.group_id = eg.id
            ORDER BY es.sort_order
            """))
            while (r.Read())
                data.EquipmentSubtypes.Add(new ExportedSubType
                {
                    Name = r.GetString(0), Prefix = r.GetInt32(1), FolderName = r.GetString(2),
                    SortOrder = r.GetInt32(3), SyncId = GetString(r, "sync_id"),
                    GroupName = GetString(r, "group_name"), GroupSyncId = GetString(r, "group_sync_id"),
                });

        using (var r = ExecuteReader("SELECT name, prefix, sort_order, sync_id FROM controller_models ORDER BY sort_order"))
            while (r.Read())
                data.ControllerModels.Add(new ExportedController { Name = r.GetString(0), Prefix = r.GetInt32(1), SortOrder = r.GetInt32(2), SyncId = GetString(r, "sync_id") });

        using (var r = ExecuteReader("""
            SELECT cm.display_name, cm.hw_version, cm.sort_order, cm.description, cm.sync_id,
                   c.name AS controller_name, c.sync_id AS controller_sync_id
            FROM controller_modifications cm JOIN controller_models c ON cm.controller_id = c.id
            ORDER BY c.sort_order, cm.sort_order
            """))
            while (r.Read())
                data.ControllerModifications.Add(new ExportedModification
                {
                    DisplayName = GetString(r, "display_name"), HwVersion = GetInt(r, "hw_version"),
                    SortOrder = GetInt(r, "sort_order"), Description = GetString(r, "description"),
                    SyncId = GetString(r, "sync_id"), ControllerName = GetString(r, "controller_name"),
                    ControllerSyncId = GetString(r, "controller_sync_id"),
                });

        data.ParamManufacturers = new();
        using (var r = ExecuteReader("SELECT name, sort_order FROM param_manufacturers ORDER BY sort_order, name"))
            while (r.Read())
                data.ParamManufacturers.Add(new ExportedManufacturer { Name = r.GetString(0), SortOrder = r.GetInt32(1) });

        data.Tags = GetAllTags();
        data.AllowedExtensions = GetAllowedExtensions();

        using (var r = ExecuteReader("""
            SELECT res.hw_version, res.version_raw, res.status, res.reserved_by, res.reserved_at,
                   es.name AS subtype_name, es.sync_id AS subtype_sync_id,
                   cm.name AS ctrl_name, cm.sync_id AS controller_sync_id
            FROM fw_version_reservations res
            JOIN equipment_subtypes es ON res.subtype_id   = es.id
            JOIN controller_models  cm ON res.controller_id = cm.id
            WHERE res.status = 'reserved'
            ORDER BY res.reserved_at
            """))
            while (r.Read())
                data.Reservations.Add(new ExportedReservation
                {
                    HwVersion = GetInt(r, "hw_version"), VersionRaw = GetString(r, "version_raw"),
                    Status = GetString(r, "status"), ReservedBy = GetString(r, "reserved_by"), ReservedAt = GetString(r, "reserved_at"),
                    SubtypeName = GetString(r, "subtype_name"), SubtypeSyncId = GetString(r, "subtype_sync_id"),
                    ControllerName = GetString(r, "ctrl_name"), ControllerSyncId = GetString(r, "controller_sync_id"),
                });

        using (var r = ExecuteReader("""
            SELECT fv.version_raw, fv.hw_version, fv.sw_version, fv.eq_prefix, fv.sub_prefix,
                   fv.dt_str, fv.filename, fv.disk_path, fv.local_path, fv.description,
                   fv.changelog, fv.launch_types, fv.io_map_path, fv.instructions_path,
                   fv.is_opc, fv.request_num, fv.upload_date, fv.archived, fv.tags,
                   fv.status, fv.released,
                   eg.name AS group_name, es.name AS subtype_name, es.sync_id AS subtype_sync_id,
                   cm.name AS ctrl_name, cm.sync_id AS controller_sync_id
            FROM fw_versions fv
            JOIN equipment_subtypes es ON fv.subtype_id  = es.id
            JOIN equipment_groups   eg ON es.group_id    = eg.id
            JOIN controller_models  cm ON fv.controller_id = cm.id
            ORDER BY fv.id
            """))
            while (r.Read())
                data.FwVersions.Add(new ExportedFwVersion
                {
                    VersionRaw = r.GetString(0), HwVersion = r.GetInt32(1), SwVersion = r.GetInt32(2),
                    EqPrefix = r.GetInt32(3), SubPrefix = r.GetInt32(4), DtStr = r.GetString(5),
                    Filename = r.GetString(6), DiskPath = r.GetString(7), LocalPath = r.GetString(8),
                    Description = r.GetString(9), Changelog = r.GetString(10), LaunchTypes = r.GetString(11),
                    IoMapPath = r.GetString(12), InstructionsPath = r.GetString(13), IsOpc = r.GetInt32(14),
                    RequestNum = r.GetString(15), UploadDate = r.GetString(16), Archived = r.GetInt32(17),
                    Tags = r.GetString(18), Status = GetString(r, "status"), Released = GetInt(r, "released"),
                    GroupName = r.GetString(21),
                    SubtypeName = GetString(r, "subtype_name"), SubtypeSyncId = GetString(r, "subtype_sync_id"),
                    CtrlName = GetString(r, "ctrl_name"), ControllerSyncId = GetString(r, "controller_sync_id"),
                });

        using (var r = ExecuteReader("""
            SELECT pf.filename, pf.disk_path, pf.description, pf.upload_date, pf.archived, pf.manufacturer,
                   es.name AS subtype_name, es.sync_id AS subtype_sync_id, eg.name AS group_name
            FROM param_files pf
            JOIN equipment_subtypes es ON pf.subtype_id = es.id
            JOIN equipment_groups   eg ON es.group_id   = eg.id
            WHERE pf.archived = 0
            ORDER BY pf.id
            """))
            while (r.Read())
                data.ParamFiles.Add(new ExportedParamFile
                {
                    Filename = r.GetString(0), DiskPath = r.GetString(1), Description = r.GetString(2),
                    UploadDate = r.GetString(3), Archived = r.GetInt32(4), Manufacturer = r.GetString(5),
                    SubtypeName = GetString(r, "subtype_name"), SubtypeSyncId = GetString(r, "subtype_sync_id"),
                    GroupName = GetString(r, "group_name"),
                });

        using (var r = ExecuteReader("SELECT sync_id, ad_login, role, first_login_at, last_login_at, role_updated_at FROM app_users ORDER BY ad_login"))
            while (r.Read())
                data.AppUsers.Add(new ExportedAppUser
                {
                    SyncId = GetString(r, "sync_id"), AdLogin = r.GetString(1), Role = GetString(r, "role", "naladchik"),
                    FirstLoginAt = GetString(r, "first_login_at"), LastLoginAt = GetString(r, "last_login_at"),
                    RoleUpdatedAt = GetString(r, "role_updated_at"),
                });

        return data;
    }

    /// <summary>Computes what an import WOULD do without writing anything — powers the config-update
    /// banner's "Подробно" view so the operator can see who changed what before committing.</summary>
    public ImportCounts PreviewImportHierarchyData(HierarchyExportData data) => ImportHierarchyDataCore(data, apply: false);

    /// <summary>Applies the import for real. Catalog tables (groups/subtypes/controllers/
    /// modifications) are matched by SyncId first (falls back to name for older exports/first
    /// contact) and updated IN PLACE — never deleted — because fw_versions/param_files reference
    /// them by integer id with no ON DELETE CASCADE from this direction; deleting and re-inserting
    /// a "renamed" row would silently orphan or (with foreign_keys=ON) outright fail against any
    /// locally-uploaded firmware under it. Tags/allowed_extensions have no such reference (they're
    /// copied into fw_versions.tags/param_files.tags as plain text, not FKs) so those two are safe
    /// to fully mirror — anything missing locally is added, anything absent from the incoming set
    /// is removed (via the existing tag-stripping DeleteTag, so removing a tag here also strips it
    /// from every record's tags text). fw_versions/param_files themselves stay additive-only, as
    /// before — each install may have uploads the exporting machine never saw.</summary>
    public ImportCounts ImportHierarchyData(HierarchyExportData data) => ImportHierarchyDataCore(data, apply: true);

    private ImportCounts ImportHierarchyDataCore(HierarchyExportData data, bool apply)
    {
        var counts = new ImportCounts();

        // ── Groups (upsert by sync_id, fallback to name) ────────────────────────
        var groupSyncToId = new Dictionary<string, int>();
        foreach (var g in data.EquipmentGroups)
        {
            var existing = FindBySyncOrName("equipment_groups", g.SyncId, "name", g.Name);
            if (existing is null)
            {
                counts.GroupsAdded++;
                if (apply)
                {
                    var sync = string.IsNullOrEmpty(g.SyncId) ? Guid.NewGuid().ToString() : g.SyncId;
                    ExecuteNonQuery("INSERT INTO equipment_groups(name,prefix,sort_order,sync_id) VALUES(@n,@p,@s,@sy)", cmd =>
                    {
                        cmd.Parameters.AddWithValue("@n", g.Name); cmd.Parameters.AddWithValue("@p", g.Prefix);
                        cmd.Parameters.AddWithValue("@s", g.SortOrder); cmd.Parameters.AddWithValue("@sy", sync);
                    });
                    if (!string.IsNullOrEmpty(g.SyncId)) groupSyncToId[g.SyncId] = Convert.ToInt32(ExecuteScalar("SELECT last_insert_rowid()"));
                }
                continue;
            }
            var (id, name, prefix, sort, localSyncId) = existing.Value;
            if (!string.IsNullOrEmpty(g.SyncId)) groupSyncToId[g.SyncId] = id;
            // First contact between two independently-seeded databases: this row matched by NAME,
            // not sync_id (their sync_ids started out different, generated independently). Adopt
            // the incoming sync_id now so a FUTURE rename can correlate via sync_id instead of name.
            var adoptSyncId = !string.IsNullOrEmpty(g.SyncId) && g.SyncId != localSyncId;
            if (name != g.Name || prefix != g.Prefix || sort != g.SortOrder)
            {
                counts.GroupsUpdated++;
                if (apply)
                    ExecuteNonQuery("UPDATE equipment_groups SET name=@n, prefix=@p, sort_order=@s, sync_id=@sy WHERE id=@id", cmd =>
                    {
                        cmd.Parameters.AddWithValue("@n", g.Name); cmd.Parameters.AddWithValue("@p", g.Prefix);
                        cmd.Parameters.AddWithValue("@s", g.SortOrder); cmd.Parameters.AddWithValue("@id", id);
                        cmd.Parameters.AddWithValue("@sy", adoptSyncId ? g.SyncId : localSyncId);
                    });
            }
            else if (adoptSyncId && apply)
                ExecuteNonQuery("UPDATE equipment_groups SET sync_id=@sy WHERE id=@id", cmd =>
                { cmd.Parameters.AddWithValue("@sy", g.SyncId); cmd.Parameters.AddWithValue("@id", id); });
        }

        // ── Subtypes (upsert by sync_id, fallback to (group,name)) ──────────────
        var subtypeSyncToId = new Dictionary<string, int>();
        foreach (var s in data.EquipmentSubtypes)
        {
            var groupId = ResolveId("equipment_groups", s.GroupSyncId, groupSyncToId, "name", s.GroupName);
            if (groupId is null) continue;

            var existing = FindSubtype(s.SyncId, groupId.Value, s.Name);
            if (existing is null)
            {
                counts.SubtypesAdded++;
                if (apply)
                {
                    var sync = string.IsNullOrEmpty(s.SyncId) ? Guid.NewGuid().ToString() : s.SyncId;
                    ExecuteNonQuery("INSERT INTO equipment_subtypes(group_id,name,prefix,folder_name,sort_order,sync_id) VALUES(@g,@n,@p,@f,@s,@sy)", cmd =>
                    {
                        cmd.Parameters.AddWithValue("@g", groupId.Value); cmd.Parameters.AddWithValue("@n", s.Name);
                        cmd.Parameters.AddWithValue("@p", s.Prefix); cmd.Parameters.AddWithValue("@f", string.IsNullOrEmpty(s.FolderName) ? s.Name : s.FolderName);
                        cmd.Parameters.AddWithValue("@s", s.SortOrder); cmd.Parameters.AddWithValue("@sy", sync);
                    });
                    if (!string.IsNullOrEmpty(s.SyncId)) subtypeSyncToId[s.SyncId] = Convert.ToInt32(ExecuteScalar("SELECT last_insert_rowid()"));
                }
                continue;
            }
            var (id, name, prefix, folder, sort, localSyncId) = existing.Value;
            if (!string.IsNullOrEmpty(s.SyncId)) subtypeSyncToId[s.SyncId] = id;
            var wantFolder = string.IsNullOrEmpty(s.FolderName) ? s.Name : s.FolderName;
            var adoptSyncId = !string.IsNullOrEmpty(s.SyncId) && s.SyncId != localSyncId;
            if (name != s.Name || prefix != s.Prefix || folder != wantFolder || sort != s.SortOrder)
            {
                counts.SubtypesUpdated++;
                if (apply)
                    ExecuteNonQuery("UPDATE equipment_subtypes SET name=@n, prefix=@p, folder_name=@f, sort_order=@s, sync_id=@sy WHERE id=@id", cmd =>
                    {
                        cmd.Parameters.AddWithValue("@n", s.Name); cmd.Parameters.AddWithValue("@p", s.Prefix);
                        cmd.Parameters.AddWithValue("@f", wantFolder); cmd.Parameters.AddWithValue("@s", s.SortOrder);
                        cmd.Parameters.AddWithValue("@id", id);
                        cmd.Parameters.AddWithValue("@sy", adoptSyncId ? s.SyncId : localSyncId);
                    });
            }
            else if (adoptSyncId && apply)
                ExecuteNonQuery("UPDATE equipment_subtypes SET sync_id=@sy WHERE id=@id", cmd =>
                { cmd.Parameters.AddWithValue("@sy", s.SyncId); cmd.Parameters.AddWithValue("@id", id); });
        }

        // ── Subtypes removed on the exporting machine — unlike groups/controllers/modifications
        //    above (upsert-only, see class doc: never deleted because fw_versions/param_files hold
        //    them by plain integer FK with no cascade), a subtype the operator explicitly deleted
        //    used to never disappear on any OTHER machine ("resurrected" subtype, reported live).
        //    Safe to mirror the deletion here because it's a full, unfiltered snapshot (same
        //    reasoning as tags/allowed_extensions) — but only for a subtype nothing local still
        //    references; one that still has uploads/params/reservations under it is left alone
        //    (SubtypesSkippedDelete) rather than orphaning that data or silently losing it.
        //    Rows without a sync_id predate this feature and are never auto-deleted — no reliable
        //    way to correlate them against the incoming set.
        var incomingSubtypeSyncIds = new HashSet<string>(
            data.EquipmentSubtypes.Where(s => !string.IsNullOrEmpty(s.SyncId)).Select(s => s.SyncId));
        var localSubtypes = new List<(int Id, string SyncId)>();
        using (var r = ExecuteReader("SELECT id, sync_id FROM equipment_subtypes WHERE sync_id IS NOT NULL AND sync_id != ''"))
            while (r.Read())
                localSubtypes.Add((r.GetInt32(0), r.GetString(1)));

        foreach (var (id, syncId) in localSubtypes)
        {
            if (incomingSubtypeSyncIds.Contains(syncId)) continue;

            var referenced = ExecuteScalar("""
                SELECT 1 WHERE EXISTS(SELECT 1 FROM fw_versions WHERE subtype_id=@id)
                   OR EXISTS(SELECT 1 FROM param_files WHERE subtype_id=@id)
                   OR EXISTS(SELECT 1 FROM fw_version_reservations WHERE subtype_id=@id)
                """, cmd => cmd.Parameters.AddWithValue("@id", id)) is not null;
            if (referenced)
            {
                counts.SubtypesSkippedDelete++;
                continue;
            }

            counts.SubtypesRemoved++;
            if (apply) ExecuteNonQuery("DELETE FROM equipment_subtypes WHERE id=@id", cmd => cmd.Parameters.AddWithValue("@id", id));
        }

        // ── Controller models (upsert by sync_id, fallback to name) ─────────────
        var controllerSyncToId = new Dictionary<string, int>();
        foreach (var c in data.ControllerModels)
        {
            var existing = FindBySyncOrName("controller_models", c.SyncId, "name", c.Name);
            if (existing is null)
            {
                counts.ControllersAdded++;
                if (apply)
                {
                    var sync = string.IsNullOrEmpty(c.SyncId) ? Guid.NewGuid().ToString() : c.SyncId;
                    ExecuteNonQuery("INSERT INTO controller_models(name,prefix,sort_order,sync_id) VALUES(@n,@p,@s,@sy)", cmd =>
                    {
                        cmd.Parameters.AddWithValue("@n", c.Name); cmd.Parameters.AddWithValue("@p", c.Prefix);
                        cmd.Parameters.AddWithValue("@s", c.SortOrder); cmd.Parameters.AddWithValue("@sy", sync);
                    });
                    if (!string.IsNullOrEmpty(c.SyncId)) controllerSyncToId[c.SyncId] = Convert.ToInt32(ExecuteScalar("SELECT last_insert_rowid()"));
                }
                continue;
            }
            var (id, name, prefix, sort, localSyncId) = existing.Value;
            if (!string.IsNullOrEmpty(c.SyncId)) controllerSyncToId[c.SyncId] = id;
            var adoptSyncId = !string.IsNullOrEmpty(c.SyncId) && c.SyncId != localSyncId;
            if (name != c.Name || prefix != c.Prefix || sort != c.SortOrder)
            {
                counts.ControllersUpdated++;
                if (apply)
                    ExecuteNonQuery("UPDATE controller_models SET name=@n, prefix=@p, sort_order=@s, sync_id=@sy WHERE id=@id", cmd =>
                    {
                        cmd.Parameters.AddWithValue("@n", c.Name); cmd.Parameters.AddWithValue("@p", c.Prefix);
                        cmd.Parameters.AddWithValue("@s", c.SortOrder); cmd.Parameters.AddWithValue("@id", id);
                        cmd.Parameters.AddWithValue("@sy", adoptSyncId ? c.SyncId : localSyncId);
                    });
            }
            else if (adoptSyncId && apply)
                ExecuteNonQuery("UPDATE controller_models SET sync_id=@sy WHERE id=@id", cmd =>
                { cmd.Parameters.AddWithValue("@sy", c.SyncId); cmd.Parameters.AddWithValue("@id", id); });
        }

        // ── Controller modifications (upsert by sync_id, fallback to (controller,display_name)) ──
        foreach (var m in data.ControllerModifications)
        {
            var ctrlId = ResolveId("controller_models", m.ControllerSyncId, controllerSyncToId, "name", m.ControllerName);
            if (ctrlId is null) continue;

            var existing = FindModification(m.SyncId, ctrlId.Value, m.DisplayName);
            if (existing is null)
            {
                counts.ModificationsAdded++;
                if (apply)
                {
                    var sync = string.IsNullOrEmpty(m.SyncId) ? Guid.NewGuid().ToString() : m.SyncId;
                    ExecuteNonQuery(
                        "INSERT INTO controller_modifications(controller_id,display_name,hw_version,sort_order,description,sync_id) VALUES(@c,@n,@h,@s,@d,@sy)",
                        cmd =>
                        {
                            cmd.Parameters.AddWithValue("@c", ctrlId.Value); cmd.Parameters.AddWithValue("@n", m.DisplayName);
                            cmd.Parameters.AddWithValue("@h", m.HwVersion); cmd.Parameters.AddWithValue("@s", m.SortOrder);
                            cmd.Parameters.AddWithValue("@d", m.Description); cmd.Parameters.AddWithValue("@sy", sync);
                        });
                }
                continue;
            }
            var (id, name, hw, sort, desc, localSyncId2) = existing.Value;
            var adoptSyncId2 = !string.IsNullOrEmpty(m.SyncId) && m.SyncId != localSyncId2;
            if (name != m.DisplayName || hw != m.HwVersion || sort != m.SortOrder || desc != m.Description)
            {
                counts.ModificationsUpdated++;
                if (apply)
                    ExecuteNonQuery("UPDATE controller_modifications SET display_name=@n, hw_version=@h, sort_order=@s, description=@d, sync_id=@sy WHERE id=@id", cmd =>
                    {
                        cmd.Parameters.AddWithValue("@n", m.DisplayName); cmd.Parameters.AddWithValue("@h", m.HwVersion);
                        cmd.Parameters.AddWithValue("@s", m.SortOrder); cmd.Parameters.AddWithValue("@d", m.Description);
                        cmd.Parameters.AddWithValue("@id", id);
                        cmd.Parameters.AddWithValue("@sy", adoptSyncId2 ? m.SyncId : localSyncId2);
                    });
            }
            else if (adoptSyncId2 && apply)
                ExecuteNonQuery("UPDATE controller_modifications SET sync_id=@sy WHERE id=@id", cmd =>
                { cmd.Parameters.AddWithValue("@sy", m.SyncId); cmd.Parameters.AddWithValue("@id", id); });
        }

        // ── Manufacturers (name-keyed, no FK-by-id references it — safe full mirror, same
        //    add-and-remove reasoning as tags/extensions below: a manufacturer deleted on the
        //    exporting machine used to never disappear locally, since this only ever inserted). ─
        var incomingManufacturers = data.ParamManufacturers ?? new();
        foreach (var m in incomingManufacturers)
        {
            var exists = ExecuteScalar("SELECT sort_order FROM param_manufacturers WHERE name=@n", cmd => cmd.Parameters.AddWithValue("@n", m.Name));
            if (exists is null)
            {
                counts.ManufacturersAdded++;
                if (apply)
                    ExecuteNonQuery("INSERT INTO param_manufacturers(name,sort_order) VALUES(@n,@s)", cmd =>
                    {
                        cmd.Parameters.AddWithValue("@n", m.Name); cmd.Parameters.AddWithValue("@s", m.SortOrder);
                    });
            }
        }
        if (data.ParamManufacturers is not null)
        {
            var incomingNames = new HashSet<string>(incomingManufacturers.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var localName in GetParamManufacturers())
                if (!incomingNames.Contains(localName))
                {
                    counts.ManufacturersRemoved++;
                    if (apply) DeleteParamManufacturer(localName);
                }
        }

        // ── Tags (name-keyed, full mirror — safe: only referenced as free text, never by id).
        //    The "remove locally-extra" half only runs when the source JSON actually HAD this
        //    field — an export written by an older app version simply omits the key, which
        //    deserializes as null (see HierarchyExportData), vs. a present-but-empty array meaning
        //    the source genuinely has zero tags. Mirroring a null field would wipe every local tag
        //    just because the exporting machine hadn't been upgraded yet. ────────────────────────
        var localTags = new HashSet<string>(GetAllTags(), StringComparer.OrdinalIgnoreCase);
        var incomingTags = new HashSet<string>(data.Tags ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var t in incomingTags.Except(localTags, StringComparer.OrdinalIgnoreCase))
        {
            counts.TagsAdded++;
            if (apply) AddTag(t);
        }
        if (data.Tags is not null)
            foreach (var t in localTags.Except(incomingTags, StringComparer.OrdinalIgnoreCase))
            {
                counts.TagsRemoved++;
                if (apply) DeleteTag(t);
            }

        // ── Allowed extensions (name-keyed, full mirror — same reasoning as tags) ────
        var localExt = new HashSet<string>(GetAllowedExtensions(), StringComparer.OrdinalIgnoreCase);
        var incomingExt = new HashSet<string>(data.AllowedExtensions ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var e in incomingExt.Except(localExt, StringComparer.OrdinalIgnoreCase))
        {
            counts.ExtensionsAdded++;
            if (apply) ExecuteNonQuery("INSERT OR IGNORE INTO allowed_extensions(ext) VALUES(@e)", cmd => cmd.Parameters.AddWithValue("@e", e));
        }
        if (data.AllowedExtensions is not null)
            foreach (var e in localExt.Except(incomingExt, StringComparer.OrdinalIgnoreCase))
            {
                counts.ExtensionsRemoved++;
                if (apply) ExecuteNonQuery("DELETE FROM allowed_extensions WHERE ext=@e COLLATE NOCASE", cmd => cmd.Parameters.AddWithValue("@e", e));
            }

        // ── Reservations (natural key = subtype+controller+hw_version+version_raw; status only
        //    ever advances reserved → fulfilled/cancelled, never the other way, so a local reservation
        //    that's already closed out is left alone even if the incoming copy still says "reserved") ─
        foreach (var res in data.Reservations)
        {
            var subId = ResolveId("equipment_subtypes", res.SubtypeSyncId, subtypeSyncToId, "name", res.SubtypeName);
            var ctrlId = ResolveId("controller_models", res.ControllerSyncId, controllerSyncToId, "name", res.ControllerName);
            if (subId is null || ctrlId is null) continue;

            var localStatus = ExecuteScalar(
                "SELECT status FROM fw_version_reservations WHERE subtype_id=@s AND controller_id=@c AND hw_version=@h AND version_raw=@v",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@s", subId.Value); cmd.Parameters.AddWithValue("@c", ctrlId.Value);
                    cmd.Parameters.AddWithValue("@h", res.HwVersion); cmd.Parameters.AddWithValue("@v", res.VersionRaw);
                }) as string;

            if (localStatus is null)
            {
                counts.ReservationsAdded++;
                if (apply)
                    ExecuteNonQuery("""
                        INSERT INTO fw_version_reservations(subtype_id,controller_id,hw_version,version_raw,status,reserved_by,reserved_at)
                        VALUES(@s,@c,@h,@v,@st,@by,@at)
                        """, cmd =>
                    {
                        cmd.Parameters.AddWithValue("@s", subId.Value); cmd.Parameters.AddWithValue("@c", ctrlId.Value);
                        cmd.Parameters.AddWithValue("@h", res.HwVersion); cmd.Parameters.AddWithValue("@v", res.VersionRaw);
                        cmd.Parameters.AddWithValue("@st", res.Status); cmd.Parameters.AddWithValue("@by", res.ReservedBy);
                        cmd.Parameters.AddWithValue("@at", res.ReservedAt);
                    });
            }
            else if (localStatus == "reserved" && res.Status != "reserved")
            {
                counts.ReservationsUpdated++;
                if (apply)
                    ExecuteNonQuery("""
                        UPDATE fw_version_reservations SET status=@st
                        WHERE subtype_id=@s AND controller_id=@c AND hw_version=@h AND version_raw=@v
                        """, cmd =>
                    {
                        cmd.Parameters.AddWithValue("@st", res.Status);
                        cmd.Parameters.AddWithValue("@s", subId.Value); cmd.Parameters.AddWithValue("@c", ctrlId.Value);
                        cmd.Parameters.AddWithValue("@h", res.HwVersion); cmd.Parameters.AddWithValue("@v", res.VersionRaw);
                    });
            }
        }

        // ── App users roster — see MergeAppUsersInto (Database.AppUsers.cs) for the merge rule.
        MergeAppUsersInto(data.AppUsers, counts, apply);

        // ── fw_versions / param_files: additive-only, as before — each machine may have uploads
        //    the exporting one never saw, so absence locally never means "delete it". The one
        //    exception is status/released on an ALREADY-matched row: moderation only ever advances
        //    (active→rolled_back, unreleased→released — see the reservations block above for the
        //    same reasoning), so if the incoming copy is further along than the local row we adopt
        //    it; we never move a local row backwards to active/unreleased. Without this, a version
        //    moderated (tags added + released) on one machine before its FIRST export would insert
        //    fine there, but land back in every OTHER machine's moderation queue forever, since the
        //    natural-key match would just skip it as "already exists" without ever updating it.
        foreach (var fv in data.FwVersions)
        {
            var subId = ResolveId("equipment_subtypes", fv.SubtypeSyncId, subtypeSyncToId, "name", fv.SubtypeName, fv.GroupName);
            var ctrlId = ResolveId("controller_models", fv.ControllerSyncId, controllerSyncToId, "name", fv.CtrlName);
            if (subId is null || ctrlId is null) continue;

            var existing = ExecuteReader(
                "SELECT id, status, released FROM fw_versions WHERE subtype_id=@s AND controller_id=@c AND version_raw=@v", cmd =>
                {
                    cmd.Parameters.AddWithValue("@s", subId.Value);
                    cmd.Parameters.AddWithValue("@c", ctrlId.Value);
                    cmd.Parameters.AddWithValue("@v", fv.VersionRaw);
                });
            (int Id, string Status, int Released)? existingRow = null;
            using (existing)
                if (existing.Read())
                    existingRow = (existing.GetInt32(0), GetString(existing, "status"), GetInt(existing, "released"));

            if (existingRow is not null)
            {
                var (id, localStatus, localReleased) = existingRow.Value;
                var incomingStatus = string.IsNullOrEmpty(fv.Status) ? "active" : fv.Status;
                var advances = (localStatus == "active" && incomingStatus != "active") ||
                               (localReleased == 0 && fv.Released != 0);
                if (!advances) continue;

                counts.FwVersions++;
                if (!apply) continue;
                ExecuteNonQuery("UPDATE fw_versions SET status=@st, released=@rel WHERE id=@id", cmd =>
                {
                    cmd.Parameters.AddWithValue("@st", localStatus == "active" ? incomingStatus : localStatus);
                    cmd.Parameters.AddWithValue("@rel", localReleased != 0 ? 1 : fv.Released);
                    cmd.Parameters.AddWithValue("@id", id);
                });
                continue;
            }

            counts.FwVersions++;
            if (!apply) continue;

            ExecuteNonQuery("""
                INSERT INTO fw_versions
                   (subtype_id, controller_id, eq_prefix, sub_prefix, hw_version, sw_version,
                    dt_str, version_raw, filename, disk_path, local_path, description, changelog,
                    launch_types, io_map_path, instructions_path, is_opc, request_num,
                    upload_date, archived, tags, status, released)
                VALUES(@subtype_id,@controller_id,@eq_prefix,@sub_prefix,@hw_version,@sw_version,
                    @dt_str,@version_raw,@filename,@disk_path,@local_path,@description,@changelog,
                    @launch_types,@io_map_path,@instructions_path,@is_opc,@request_num,
                    @upload_date,@archived,@tags,@status,@released)
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("@subtype_id", subId.Value);
                cmd.Parameters.AddWithValue("@controller_id", ctrlId.Value);
                cmd.Parameters.AddWithValue("@eq_prefix", fv.EqPrefix);
                cmd.Parameters.AddWithValue("@sub_prefix", fv.SubPrefix);
                cmd.Parameters.AddWithValue("@hw_version", fv.HwVersion);
                cmd.Parameters.AddWithValue("@sw_version", fv.SwVersion);
                cmd.Parameters.AddWithValue("@dt_str", fv.DtStr);
                cmd.Parameters.AddWithValue("@version_raw", fv.VersionRaw);
                cmd.Parameters.AddWithValue("@filename", fv.Filename);
                cmd.Parameters.AddWithValue("@disk_path", fv.DiskPath);
                cmd.Parameters.AddWithValue("@local_path", fv.LocalPath);
                cmd.Parameters.AddWithValue("@description", fv.Description);
                cmd.Parameters.AddWithValue("@changelog", fv.Changelog);
                cmd.Parameters.AddWithValue("@launch_types", fv.LaunchTypes);
                cmd.Parameters.AddWithValue("@io_map_path", fv.IoMapPath);
                cmd.Parameters.AddWithValue("@instructions_path", fv.InstructionsPath);
                cmd.Parameters.AddWithValue("@is_opc", fv.IsOpc);
                cmd.Parameters.AddWithValue("@request_num", fv.RequestNum);
                cmd.Parameters.AddWithValue("@upload_date", string.IsNullOrEmpty(fv.UploadDate) ? NowIso() : fv.UploadDate);
                cmd.Parameters.AddWithValue("@archived", fv.Archived);
                cmd.Parameters.AddWithValue("@tags", fv.Tags);
                cmd.Parameters.AddWithValue("@status", string.IsNullOrEmpty(fv.Status) ? "active" : fv.Status);
                cmd.Parameters.AddWithValue("@released", fv.Released);
            });
        }

        foreach (var pf in data.ParamFiles)
        {
            var subId = ResolveId("equipment_subtypes", pf.SubtypeSyncId, subtypeSyncToId, "name", pf.SubtypeName, pf.GroupName);
            if (subId is null) continue;

            var exists = ExecuteScalar(
                "SELECT 1 FROM param_files WHERE subtype_id=@s AND manufacturer=@m AND filename=@f AND disk_path=@d", cmd =>
                {
                    cmd.Parameters.AddWithValue("@s", subId.Value);
                    cmd.Parameters.AddWithValue("@m", pf.Manufacturer);
                    cmd.Parameters.AddWithValue("@f", pf.Filename);
                    cmd.Parameters.AddWithValue("@d", pf.DiskPath);
                });
            if (exists is not null) continue;

            counts.ParamFiles++;
            if (!apply) continue;

            ExecuteNonQuery("""
                INSERT INTO param_files(subtype_id, manufacturer, filename, disk_path, description, upload_date, archived)
                VALUES(@s,@m,@f,@d,@desc,@u,@a)
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("@s", subId.Value);
                cmd.Parameters.AddWithValue("@m", pf.Manufacturer);
                cmd.Parameters.AddWithValue("@f", pf.Filename);
                cmd.Parameters.AddWithValue("@d", pf.DiskPath);
                cmd.Parameters.AddWithValue("@desc", pf.Description);
                cmd.Parameters.AddWithValue("@u", string.IsNullOrEmpty(pf.UploadDate) ? NowIso() : pf.UploadDate);
                cmd.Parameters.AddWithValue("@a", pf.Archived);
            });
        }

        return counts;
    }

    // ── Sync-id-aware lookup helpers ─────────────────────────────────────────

    private (int Id, string Name, int Prefix, int SortOrder, string SyncId)? FindBySyncOrName(string table, string syncId, string nameCol, string name)
    {
        if (!string.IsNullOrEmpty(syncId))
        {
            using var r1 = ExecuteReader($"SELECT id, {nameCol}, prefix, sort_order, sync_id FROM {table} WHERE sync_id=@sy", cmd => cmd.Parameters.AddWithValue("@sy", syncId));
            if (r1.Read()) return (r1.GetInt32(0), r1.GetString(1), r1.GetInt32(2), r1.GetInt32(3), GetString(r1, "sync_id"));
        }
        using var r2 = ExecuteReader($"SELECT id, {nameCol}, prefix, sort_order, sync_id FROM {table} WHERE {nameCol}=@n", cmd => cmd.Parameters.AddWithValue("@n", name));
        return r2.Read() ? (r2.GetInt32(0), r2.GetString(1), r2.GetInt32(2), r2.GetInt32(3), GetString(r2, "sync_id")) : null;
    }

    private (int Id, string Name, int Prefix, string Folder, int SortOrder, string SyncId)? FindSubtype(string syncId, int groupId, string name)
    {
        if (!string.IsNullOrEmpty(syncId))
        {
            using var r1 = ExecuteReader("SELECT id, name, prefix, folder_name, sort_order, sync_id FROM equipment_subtypes WHERE sync_id=@sy AND group_id=@g",
                cmd => { cmd.Parameters.AddWithValue("@sy", syncId); cmd.Parameters.AddWithValue("@g", groupId); });
            if (r1.Read()) return (r1.GetInt32(0), r1.GetString(1), r1.GetInt32(2), r1.GetString(3), r1.GetInt32(4), GetString(r1, "sync_id"));
        }
        using var r2 = ExecuteReader("SELECT id, name, prefix, folder_name, sort_order, sync_id FROM equipment_subtypes WHERE group_id=@g AND name=@n",
            cmd => { cmd.Parameters.AddWithValue("@g", groupId); cmd.Parameters.AddWithValue("@n", name); });
        return r2.Read() ? (r2.GetInt32(0), r2.GetString(1), r2.GetInt32(2), r2.GetString(3), r2.GetInt32(4), GetString(r2, "sync_id")) : null;
    }

    private (int Id, string Name, int HwVersion, int SortOrder, string Description, string SyncId)? FindModification(string syncId, int controllerId, string displayName)
    {
        if (!string.IsNullOrEmpty(syncId))
        {
            using var r1 = ExecuteReader("SELECT id, display_name, hw_version, sort_order, description, sync_id FROM controller_modifications WHERE sync_id=@sy AND controller_id=@c",
                cmd => { cmd.Parameters.AddWithValue("@sy", syncId); cmd.Parameters.AddWithValue("@c", controllerId); });
            if (r1.Read()) return (r1.GetInt32(0), r1.GetString(1), r1.GetInt32(2), r1.GetInt32(3), r1.GetString(4), GetString(r1, "sync_id"));
        }
        using var r2 = ExecuteReader("SELECT id, display_name, hw_version, sort_order, description, sync_id FROM controller_modifications WHERE controller_id=@c AND display_name=@n",
            cmd => { cmd.Parameters.AddWithValue("@c", controllerId); cmd.Parameters.AddWithValue("@n", displayName); });
        return r2.Read() ? (r2.GetInt32(0), r2.GetString(1), r2.GetInt32(2), r2.GetInt32(3), r2.GetString(4), GetString(r2, "sync_id")) : null;
    }

    /// <summary>Resolves a hierarchy row's local id: prefer the sync_id map built earlier in THIS
    /// import pass (covers rows renamed earlier in the same batch), else look the row up fresh by
    /// sync_id, else fall back to name (older exports, or first contact between two independently-
    /// built databases that happen to use the same names already).</summary>
    private int? ResolveId(string table, string syncId, Dictionary<string, int> syncMap, string nameCol, string name, string? groupName = null)
    {
        if (!string.IsNullOrEmpty(syncId) && syncMap.TryGetValue(syncId, out var mapped)) return mapped;
        if (!string.IsNullOrEmpty(syncId))
        {
            var byId = ExecuteScalar($"SELECT id FROM {table} WHERE sync_id=@sy", cmd => cmd.Parameters.AddWithValue("@sy", syncId));
            if (byId is long l) return (int)l;
        }
        if (table == "equipment_subtypes" && groupName is not null)
        {
            var byName = ExecuteScalar("""
                SELECT es.id FROM equipment_subtypes es JOIN equipment_groups eg ON es.group_id = eg.id
                WHERE eg.name=@g AND es.name=@n
                """, cmd => { cmd.Parameters.AddWithValue("@g", groupName); cmd.Parameters.AddWithValue("@n", name); });
            return byName is long l2 ? (int)l2 : null;
        }
        var byName2 = ExecuteScalar($"SELECT id FROM {table} WHERE {nameCol}=@n", cmd => cmd.Parameters.AddWithValue("@n", name));
        return byName2 is long l3 ? (int)l3 : null;
    }

    /// <summary>Replace old_root prefix with new_root in fw_versions/param_files path columns
    /// (normalizes separators so mixed forward/backward slashes still match).</summary>
    public void RemapFwPaths(string oldRoot, string newRoot) => RemapPathPrefix(oldRoot, newRoot);

    /// <summary>Same prefix-replace as RemapFwPaths but for an arbitrary path segment — used after
    /// renaming an equipment group/subtype's disk folder (see SettingsView RenameGroup/RenameSubtype),
    /// where only the group/subtype segment of the path changes, not the whole root. Returns how many
    /// rows were touched, so the caller can report it.</summary>
    public int RemapPathPrefix(string oldPrefix, string newPrefix)
    {
        var oldNorm = Path.TrimEndingDirectorySeparator(oldPrefix);
        var newNorm = Path.TrimEndingDirectorySeparator(newPrefix);
        if (string.IsNullOrEmpty(oldNorm) || oldNorm == newNorm) return 0;

        (string Value, bool Changed) Remap(string val)
        {
            if (string.IsNullOrEmpty(val)) return (val, false);
            var norm = Path.GetFullPath(val);
            if (string.Equals(norm, oldNorm, StringComparison.OrdinalIgnoreCase))
                return (newNorm, true);
            if (norm.StartsWith(oldNorm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return (newNorm + norm[oldNorm.Length..], true);
            return (val, false);
        }

        var changedCount = 0;

        var fwRows = new List<(int Id, string DiskPath, string IoMapPath, string InstructionsPath)>();
        using (var r = ExecuteReader("SELECT id, disk_path, io_map_path, instructions_path FROM fw_versions"))
            while (r.Read())
                fwRows.Add((r.GetInt32(0), GetString(r, "disk_path"), GetString(r, "io_map_path"), GetString(r, "instructions_path")));

        foreach (var row in fwRows)
        {
            var (disk, c1) = Remap(row.DiskPath);
            var (io, c2) = Remap(row.IoMapPath);
            var (instr, c3) = Remap(row.InstructionsPath);
            if (!c1 && !c2 && !c3) continue;
            ExecuteNonQuery("UPDATE fw_versions SET disk_path=@d, io_map_path=@io, instructions_path=@instr WHERE id=@id", cmd =>
            {
                cmd.Parameters.AddWithValue("@d", disk);
                cmd.Parameters.AddWithValue("@io", io);
                cmd.Parameters.AddWithValue("@instr", instr);
                cmd.Parameters.AddWithValue("@id", row.Id);
            });
            changedCount++;
        }

        var pfRows = new List<(int Id, string DiskPath)>();
        using (var r = ExecuteReader("SELECT id, disk_path FROM param_files"))
            while (r.Read())
                pfRows.Add((r.GetInt32(0), GetString(r, "disk_path")));

        foreach (var row in pfRows)
        {
            var (disk, changed) = Remap(row.DiskPath);
            if (!changed) continue;
            ExecuteNonQuery("UPDATE param_files SET disk_path=@d WHERE id=@id", cmd =>
            {
                cmd.Parameters.AddWithValue("@d", disk);
                cmd.Parameters.AddWithValue("@id", row.Id);
            });
            changedCount++;
        }

        return changedCount;
    }
}
