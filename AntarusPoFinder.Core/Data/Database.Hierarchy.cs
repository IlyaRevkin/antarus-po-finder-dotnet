using System;
using System.Collections.Generic;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    // ── Equipment Groups ─────────────────────────────────────────────────────

    public List<EquipmentGroup> GetAllEquipmentGroups()
    {
        var result = new List<EquipmentGroup>();
        using var reader = ExecuteReader("SELECT * FROM equipment_groups ORDER BY sort_order, name");
        while (reader.Read())
        {
            result.Add(new EquipmentGroup
            {
                Id = GetInt(reader, "id"),
                Name = GetString(reader, "name"),
                Prefix = GetInt(reader, "prefix"),
                SortOrder = GetInt(reader, "sort_order"),
                SyncId = GetString(reader, "sync_id"),
                UpdatedAt = GetString(reader, "updated_at"),
            });
        }
        return result;
    }

    public int UpsertEquipmentGroup(EquipmentGroup g)
    {
        ExecuteNonQuery(
            "INSERT INTO equipment_groups(name,prefix,sort_order,sync_id,updated_at) VALUES(@n,@p,@s,@sy,@u) " +
            "ON CONFLICT(name) DO UPDATE SET prefix=excluded.prefix, sort_order=excluded.sort_order, updated_at=excluded.updated_at",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@n", g.Name);
                cmd.Parameters.AddWithValue("@p", g.Prefix);
                cmd.Parameters.AddWithValue("@s", g.SortOrder);
                cmd.Parameters.AddWithValue("@sy", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@u", NowIso());
            });
        var id = ExecuteScalar("SELECT id FROM equipment_groups WHERE name=@n",
            cmd => cmd.Parameters.AddWithValue("@n", g.Name));
        return id is long l ? (int)l : -1;
    }

    /// <summary>Prefix feeds directly into the firmware version number (eq_prefix — see
    /// FwVersionNumber), so two groups sharing one would produce colliding/ambiguous version
    /// numbers. excludeGroupId lets an edit check against everyone else without tripping on itself.</summary>
    public bool GroupPrefixTaken(int prefix, int? excludeGroupId = null)
    {
        var sql = "SELECT COUNT(*) FROM equipment_groups WHERE prefix=@p" + (excludeGroupId.HasValue ? " AND id<>@id" : "");
        var count = ExecuteScalar(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("@p", prefix);
            if (excludeGroupId.HasValue) cmd.Parameters.AddWithValue("@id", excludeGroupId.Value);
        });
        return count is long l && l > 0;
    }

    /// <summary>Direct UPDATE by id, unlike UpsertEquipmentGroup which conflicts on the (unique)
    /// name column — calling Upsert with a changed name would INSERT a second row instead of
    /// renaming the existing one.</summary>
    public void RenameEquipmentGroup(int id, string newName) =>
        ExecuteNonQuery("UPDATE equipment_groups SET name=@n, updated_at=@u WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@n", newName);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@u", NowIso());
        });

    public bool GroupNameTaken(string name, int? excludeGroupId = null)
    {
        var sql = "SELECT COUNT(*) FROM equipment_groups WHERE name=@n COLLATE NOCASE" + (excludeGroupId.HasValue ? " AND id<>@id" : "");
        var count = ExecuteScalar(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("@n", name);
            if (excludeGroupId.HasValue) cmd.Parameters.AddWithValue("@id", excludeGroupId.Value);
        });
        return count is long l && l > 0;
    }

    public void DeleteEquipmentGroup(int groupId)
    {
        var subtypeIds = QueryIntsParam("SELECT id FROM equipment_subtypes WHERE group_id=@g", "@g", groupId);
        if (subtypeIds.Count > 0)
        {
            var ph = IntParamPlaceholders(subtypeIds);
            ExecWithIntParams($"DELETE FROM fw_versions WHERE subtype_id IN ({ph})", subtypeIds);
            ExecWithIntParams($"DELETE FROM param_files WHERE subtype_id IN ({ph})", subtypeIds);
        }
        ExecuteNonQuery("DELETE FROM equipment_groups WHERE id=@g", cmd => cmd.Parameters.AddWithValue("@g", groupId));
    }

    // ── Equipment SubTypes ────────────────────────────────────────────────────

    public List<EquipmentSubType> GetAllEquipmentSubtypes()
    {
        var result = new List<EquipmentSubType>();
        using var reader = ExecuteReader("SELECT * FROM equipment_subtypes ORDER BY group_id, sort_order, name");
        while (reader.Read())
            result.Add(ReadSubType(reader));
        return result;
    }

    public List<EquipmentSubType> GetSubtypesForGroup(int groupId)
    {
        var result = new List<EquipmentSubType>();
        using var reader = ExecuteReader("SELECT * FROM equipment_subtypes WHERE group_id=@g ORDER BY sort_order, name",
            cmd => cmd.Parameters.AddWithValue("@g", groupId));
        while (reader.Read())
            result.Add(ReadSubType(reader));
        return result;
    }

    private static EquipmentSubType ReadSubType(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = GetInt(r, "id"),
        GroupId = GetInt(r, "group_id"),
        Name = GetString(r, "name"),
        Prefix = GetInt(r, "prefix"),
        FolderName = GetString(r, "folder_name"),
        SortOrder = GetInt(r, "sort_order"),
        SyncId = GetString(r, "sync_id"),
        UpdatedAt = GetString(r, "updated_at"),
    };

    public int UpsertEquipmentSubtype(EquipmentSubType s)
    {
        ExecuteNonQuery(
            "INSERT INTO equipment_subtypes(group_id,name,prefix,folder_name,sort_order,sync_id,updated_at) VALUES(@g,@n,@p,@f,@s,@sy,@u) " +
            "ON CONFLICT(group_id,name) DO UPDATE SET prefix=excluded.prefix, folder_name=excluded.folder_name, sort_order=excluded.sort_order, updated_at=excluded.updated_at",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@g", s.GroupId);
                cmd.Parameters.AddWithValue("@n", s.Name);
                cmd.Parameters.AddWithValue("@p", s.Prefix);
                cmd.Parameters.AddWithValue("@f", s.FolderName);
                cmd.Parameters.AddWithValue("@s", s.SortOrder);
                cmd.Parameters.AddWithValue("@sy", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@u", NowIso());
            });
        var id = ExecuteScalar("SELECT id FROM equipment_subtypes WHERE group_id=@g AND name=@n", cmd =>
        {
            cmd.Parameters.AddWithValue("@g", s.GroupId);
            cmd.Parameters.AddWithValue("@n", s.Name);
        });
        return id is long l ? (int)l : -1;
    }

    /// <summary>Same collision reasoning as GroupPrefixTaken, but scoped to one group: sub_prefix
    /// only needs to be unique among the subtypes of the same group, since eq_prefix already
    /// separates the version numbering of different groups.</summary>
    public bool SubtypePrefixTakenInGroup(int groupId, int prefix, int? excludeSubtypeId = null)
    {
        var sql = "SELECT COUNT(*) FROM equipment_subtypes WHERE group_id=@g AND prefix=@p" + (excludeSubtypeId.HasValue ? " AND id<>@id" : "");
        var count = ExecuteScalar(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("@g", groupId);
            cmd.Parameters.AddWithValue("@p", prefix);
            if (excludeSubtypeId.HasValue) cmd.Parameters.AddWithValue("@id", excludeSubtypeId.Value);
        });
        return count is long l && l > 0;
    }

    /// <summary>Direct UPDATE by id, unlike UpsertEquipmentSubtype which conflicts on the (unique)
    /// (group_id,name) pair — calling Upsert with a changed name would INSERT a second row instead
    /// of renaming the existing one. FolderName is a legacy display field mirroring Name (see
    /// AddSubtype_Click) — kept in sync here purely for cosmetic consistency; nothing reads it to
    /// build the actual on-disk path, that's always live off Name (see HierarchyService).</summary>
    public void RenameEquipmentSubtype(int id, string newName, string newFolderName) =>
        ExecuteNonQuery("UPDATE equipment_subtypes SET name=@n, folder_name=@f, updated_at=@u WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@n", newName);
            cmd.Parameters.AddWithValue("@f", newFolderName);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@u", NowIso());
        });

    public bool SubtypeNameTakenInGroup(int groupId, string name, int? excludeSubtypeId = null)
    {
        var sql = "SELECT COUNT(*) FROM equipment_subtypes WHERE group_id=@g AND name=@n COLLATE NOCASE" + (excludeSubtypeId.HasValue ? " AND id<>@id" : "");
        var count = ExecuteScalar(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("@g", groupId);
            cmd.Parameters.AddWithValue("@n", name);
            if (excludeSubtypeId.HasValue) cmd.Parameters.AddWithValue("@id", excludeSubtypeId.Value);
        });
        return count is long l && l > 0;
    }

    /// <summary>Number of subtype rows a group currently has — used to block deleting the last one
    /// (a group must never end up with zero subtypes; see Database.EnsureEveryGroupHasSubtype).</summary>
    public int CountSubtypesForGroup(int groupId) =>
        ExecuteScalar("SELECT COUNT(*) FROM equipment_subtypes WHERE group_id=@g", cmd => cmd.Parameters.AddWithValue("@g", groupId)) is long l ? (int)l : 0;

    public void DeleteEquipmentSubtype(int subtypeId)
    {
        ExecuteNonQuery("DELETE FROM fw_versions WHERE subtype_id=@s", cmd => cmd.Parameters.AddWithValue("@s", subtypeId));
        ExecuteNonQuery("DELETE FROM param_files WHERE subtype_id=@s", cmd => cmd.Parameters.AddWithValue("@s", subtypeId));
        ExecuteNonQuery("DELETE FROM equipment_subtypes WHERE id=@s", cmd => cmd.Parameters.AddWithValue("@s", subtypeId));
    }

    // ── Controller Models ─────────────────────────────────────────────────────

    public List<ControllerModel> GetAllControllerModels()
    {
        var result = new List<ControllerModel>();
        using var reader = ExecuteReader("SELECT * FROM controller_models ORDER BY sort_order, name");
        while (reader.Read())
        {
            result.Add(new ControllerModel
            {
                Id = GetInt(reader, "id"),
                Name = GetString(reader, "name"),
                Prefix = GetInt(reader, "prefix"),
                SortOrder = GetInt(reader, "sort_order"),
                SyncId = GetString(reader, "sync_id"),
                UpdatedAt = GetString(reader, "updated_at"),
            });
        }
        return result;
    }

    public int UpsertControllerModel(ControllerModel c)
    {
        ExecuteNonQuery(
            "INSERT INTO controller_models(name,prefix,sort_order,sync_id,updated_at) VALUES(@n,@p,@s,@sy,@u) " +
            "ON CONFLICT(name) DO UPDATE SET prefix=excluded.prefix, sort_order=excluded.sort_order, updated_at=excluded.updated_at",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@n", c.Name);
                cmd.Parameters.AddWithValue("@p", c.Prefix);
                cmd.Parameters.AddWithValue("@s", c.SortOrder);
                cmd.Parameters.AddWithValue("@sy", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@u", NowIso());
            });
        var id = ExecuteScalar("SELECT id FROM controller_models WHERE name=@n", cmd => cmd.Parameters.AddWithValue("@n", c.Name));
        return id is long l ? (int)l : -1;
    }

    public void DeleteControllerModel(int ctrlId)
    {
        ExecuteNonQuery("DELETE FROM fw_versions WHERE controller_id=@c", cmd => cmd.Parameters.AddWithValue("@c", ctrlId));
        ExecuteNonQuery("DELETE FROM controller_models WHERE id=@c", cmd => cmd.Parameters.AddWithValue("@c", ctrlId));
    }

    // ── Controller Modifications ─────────────────────────────────────────────

    public List<ControllerModification> GetAllModifications()
    {
        var result = new List<ControllerModification>();
        using var reader = ExecuteReader("""
            SELECT cm_mod.*, cm.name AS controller_name
            FROM controller_modifications cm_mod
            JOIN controller_models cm ON cm_mod.controller_id = cm.id
            ORDER BY cm.sort_order, cm_mod.sort_order, cm_mod.display_name
            """);
        while (reader.Read())
            result.Add(ReadModification(reader, includeControllerName: true));
        return result;
    }

    public List<ControllerModification> GetModificationsForController(int controllerId)
    {
        var result = new List<ControllerModification>();
        using var reader = ExecuteReader(
            "SELECT * FROM controller_modifications WHERE controller_id=@c ORDER BY sort_order, display_name",
            cmd => cmd.Parameters.AddWithValue("@c", controllerId));
        while (reader.Read())
            result.Add(ReadModification(reader, includeControllerName: false));
        return result;
    }

    private static ControllerModification ReadModification(Microsoft.Data.Sqlite.SqliteDataReader r, bool includeControllerName) => new()
    {
        Id = GetInt(r, "id"),
        ControllerId = GetInt(r, "controller_id"),
        DisplayName = GetString(r, "display_name"),
        HwVersion = GetInt(r, "hw_version"),
        SortOrder = GetInt(r, "sort_order"),
        Description = GetString(r, "description"),
        SyncId = GetString(r, "sync_id"),
        UpdatedAt = GetString(r, "updated_at"),
        ControllerName = includeControllerName ? GetString(r, "controller_name") : "",
    };

    public int AddControllerModification(int controllerId, string displayName, int hwVersion, string description = "")
    {
        ExecuteNonQuery(
            "INSERT INTO controller_modifications (controller_id, display_name, hw_version, description, sync_id, updated_at) VALUES (@c,@d,@h,@desc,@sy,@u)",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@c", controllerId);
                cmd.Parameters.AddWithValue("@d", displayName);
                cmd.Parameters.AddWithValue("@h", hwVersion);
                cmd.Parameters.AddWithValue("@desc", description);
                cmd.Parameters.AddWithValue("@sy", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@u", NowIso());
            });
        var id = ExecuteScalar("SELECT last_insert_rowid()");
        return id is long l ? (int)l : -1;
    }

    public void DeleteControllerModification(int modId) =>
        ExecuteNonQuery("DELETE FROM controller_modifications WHERE id=@m", cmd => cmd.Parameters.AddWithValue("@m", modId));

    private List<int> QueryIntsParam(string sql, string paramName, int value)
    {
        var result = new List<int>();
        using var reader = ExecuteReader(sql, cmd => cmd.Parameters.AddWithValue(paramName, value));
        while (reader.Read())
            result.Add(reader.GetInt32(0));
        return result;
    }
}
