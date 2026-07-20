using System;
using System.Collections.Generic;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    /// <summary>Insert default hierarchy data if the equipment_groups table is empty (fresh install only).</summary>
    private void SeedHierarchyDefaults()
    {
        var count = (long)(ExecuteScalar("SELECT COUNT(*) FROM equipment_groups") ?? 0L);
        if (count > 0) return;

        foreach (var g in HierarchyDefaultsData.EquipmentGroups)
        {
            ExecuteNonQuery("INSERT OR IGNORE INTO equipment_groups(name,prefix,sort_order) VALUES(@n,@p,@s)", cmd =>
            {
                cmd.Parameters.AddWithValue("@n", g.Name);
                cmd.Parameters.AddWithValue("@p", g.Prefix);
                cmd.Parameters.AddWithValue("@s", g.SortOrder);
            });
        }

        var groupsMap = new Dictionary<string, int>();
        using (var reader = ExecuteReader("SELECT id, name FROM equipment_groups"))
        {
            while (reader.Read())
                groupsMap[reader.GetString(1)] = reader.GetInt32(0);
        }

        foreach (var s in HierarchyDefaultsData.SubTypes)
        {
            if (!groupsMap.TryGetValue(s.GroupName, out var gid)) continue;
            ExecuteNonQuery(
                "INSERT OR IGNORE INTO equipment_subtypes(group_id,name,prefix,folder_name,sort_order) VALUES(@g,@n,@p,@f,@s)",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@g", gid);
                    cmd.Parameters.AddWithValue("@n", s.Name);
                    cmd.Parameters.AddWithValue("@p", s.Prefix);
                    cmd.Parameters.AddWithValue("@f", s.FolderName);
                    cmd.Parameters.AddWithValue("@s", s.SortOrder);
                });
        }

        foreach (var c in HierarchyDefaultsData.Controllers)
        {
            ExecuteNonQuery("INSERT OR IGNORE INTO controller_models(name,sort_order) VALUES(@n,@s)", cmd =>
            {
                cmd.Parameters.AddWithValue("@n", c.Name);
                cmd.Parameters.AddWithValue("@s", c.SortOrder);
            });
        }

        var manufacturers = new[] { "VEDS", "HERTZ", "DANFOSS", "ABB", "SIEMENS", "АЭП" };
        for (int i = 0; i < manufacturers.Length; i++)
        {
            var m = manufacturers[i];
            var sortOrder = i + 1;
            ExecuteNonQuery("INSERT OR IGNORE INTO param_manufacturers(name,sort_order) VALUES(@n,@s)", cmd =>
            {
                cmd.Parameters.AddWithValue("@n", m);
                cmd.Parameters.AddWithValue("@s", sortOrder);
            });
        }

        SeedModifications(HierarchyDefaultsData.Modifications);
    }

    private void SeedAllowedExtensionsDefaults()
    {
        var count = (long)(ExecuteScalar("SELECT COUNT(*) FROM allowed_extensions") ?? 0L);
        if (count > 0) return;
        foreach (var ext in new[] { "psl", "lfs", "kpr", "kpj", "dpj" })
        {
            var e = ext;
            ExecuteNonQuery("INSERT OR IGNORE INTO allowed_extensions(ext) VALUES(@e)",
                cmd => cmd.Parameters.AddWithValue("@e", e));
        }
    }

    /// <summary>Idempotent: add any DefaultEquipmentGroup missing (by name) from equipment_groups.
    /// Runs on every startup (unlike SeedHierarchyDefaults, which only seeds a fresh/empty DB) so a
    /// group added to the reference catalogue later (e.g. ШУЗ) reaches already-existing installs
    /// too — additive only: never overwrites the prefix/sort_order of a group that already exists
    /// under that name (an admin may have retargeted it via Настройки → Иерархия since).</summary>
    private void EnsureDefaultEquipmentGroups()
    {
        var existingNames = new HashSet<string>();
        var existingPrefixes = new HashSet<int>();
        using (var reader = ExecuteReader("SELECT name, prefix FROM equipment_groups"))
        {
            while (reader.Read())
            {
                existingNames.Add(reader.GetString(0));
                existingPrefixes.Add(reader.GetInt32(1));
            }
        }

        foreach (var g in HierarchyDefaultsData.EquipmentGroups)
        {
            if (existingNames.Contains(g.Name)) continue;

            // The default prefix may already be in use by an unrelated group an admin created
            // manually — prefixes feed directly into the version number (eq_prefix) and must stay
            // unique, so fall back to the first free one above the existing set instead of colliding.
            var prefix = g.Prefix;
            while (existingPrefixes.Contains(prefix)) prefix++;

            ExecuteNonQuery("INSERT INTO equipment_groups(name,prefix,sort_order,sync_id) VALUES(@n,@p,@s,@sy)", cmd =>
            {
                cmd.Parameters.AddWithValue("@n", g.Name);
                cmd.Parameters.AddWithValue("@p", prefix);
                cmd.Parameters.AddWithValue("@s", g.SortOrder);
                cmd.Parameters.AddWithValue("@sy", Guid.NewGuid().ToString());
            });
            existingNames.Add(g.Name);
            existingPrefixes.Add(prefix);
        }
    }

    /// <summary>Idempotent: add any DefaultSubType missing (by group name + subtype name) from
    /// equipment_subtypes — same additive-only reasoning as EnsureDefaultEquipmentGroups (e.g. adds
    /// ПЖ-ПКР/ПЖ-ПКР ПИ to an install that predates them, without touching the subtypes it already
    /// has). Only applies to groups that exist by the time this runs — including ones this same
    /// startup's EnsureDefaultEquipmentGroups call just added, since it always runs first.</summary>
    private void EnsureDefaultEquipmentSubtypes()
    {
        var groupIdsByName = new Dictionary<string, int>();
        using (var reader = ExecuteReader("SELECT id, name FROM equipment_groups"))
        {
            while (reader.Read())
                groupIdsByName[reader.GetString(1)] = reader.GetInt32(0);
        }

        var existingByGroup = new Dictionary<int, (HashSet<string> Names, HashSet<int> Prefixes)>();
        using (var reader = ExecuteReader("SELECT group_id, name, prefix FROM equipment_subtypes"))
        {
            while (reader.Read())
            {
                var gid = reader.GetInt32(0);
                if (!existingByGroup.TryGetValue(gid, out var sets))
                    existingByGroup[gid] = sets = (new HashSet<string>(), new HashSet<int>());
                sets.Names.Add(reader.GetString(1));
                sets.Prefixes.Add(reader.GetInt32(2));
            }
        }

        foreach (var s in HierarchyDefaultsData.SubTypes)
        {
            if (!groupIdsByName.TryGetValue(s.GroupName, out var gid)) continue;
            if (!existingByGroup.TryGetValue(gid, out var sets))
                existingByGroup[gid] = sets = (new HashSet<string>(), new HashSet<int>());
            if (sets.Names.Contains(s.Name)) continue;

            var prefix = s.Prefix;
            while (sets.Prefixes.Contains(prefix)) prefix++;

            ExecuteNonQuery(
                "INSERT INTO equipment_subtypes(group_id,name,prefix,folder_name,sort_order,sync_id) VALUES(@g,@n,@p,@f,@s,@sy)",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@g", gid);
                    cmd.Parameters.AddWithValue("@n", s.Name);
                    cmd.Parameters.AddWithValue("@p", prefix);
                    cmd.Parameters.AddWithValue("@f", s.FolderName);
                    cmd.Parameters.AddWithValue("@s", s.SortOrder);
                    cmd.Parameters.AddWithValue("@sy", Guid.NewGuid().ToString());
                });
            sets.Names.Add(s.Name);
            sets.Prefixes.Add(prefix);
        }
    }

    /// <summary>Idempotent: add any DefaultController missing from controller_models.
    /// Runs on every startup (unlike SeedHierarchyDefaults, which only seeds an empty DB)
    /// so new default controllers (e.g. PIXEL) reach already-existing databases.</summary>
    private void EnsureDefaultControllers()
    {
        var existing = new HashSet<string>();
        using (var reader = ExecuteReader("SELECT name FROM controller_models"))
        {
            while (reader.Read())
                existing.Add(reader.GetString(0));
        }

        foreach (var c in HierarchyDefaultsData.Controllers)
        {
            if (existing.Contains(c.Name)) continue;
            ExecuteNonQuery("INSERT OR IGNORE INTO controller_models(name,sort_order) VALUES(@n,@s)", cmd =>
            {
                cmd.Parameters.AddWithValue("@n", c.Name);
                cmd.Parameters.AddWithValue("@s", c.SortOrder);
            });
        }
    }

    /// <summary>Idempotent: add missing DefaultModification rows and backfill empty descriptions.</summary>
    private void EnsureDefaultModifications() => SeedModifications(HierarchyDefaultsData.Modifications);

    private void SeedModifications(DefaultModification[] modifications)
    {
        var ctrlMap = new Dictionary<string, int>();
        using (var reader = ExecuteReader("SELECT id, name FROM controller_models"))
        {
            while (reader.Read())
                ctrlMap[reader.GetString(1)] = reader.GetInt32(0);
        }

        foreach (var mod in modifications)
        {
            if (!ctrlMap.TryGetValue(mod.ControllerName, out var cid)) continue;

            ExecuteNonQuery(
                "INSERT OR IGNORE INTO controller_modifications (controller_id, display_name, hw_version, sort_order, description) VALUES (@c,@d,@h,@s,@desc)",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@c", cid);
                    cmd.Parameters.AddWithValue("@d", mod.DisplayName);
                    cmd.Parameters.AddWithValue("@h", mod.HwVersion);
                    cmd.Parameters.AddWithValue("@s", mod.SortOrder);
                    cmd.Parameters.AddWithValue("@desc", mod.Description);
                });

            if (!string.IsNullOrEmpty(mod.Description))
            {
                ExecuteNonQuery(
                    "UPDATE controller_modifications SET description=@desc WHERE controller_id=@c AND display_name=@d AND description=''",
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@desc", mod.Description);
                        cmd.Parameters.AddWithValue("@c", cid);
                        cmd.Parameters.AddWithValue("@d", mod.DisplayName);
                    });
            }
        }
    }
}
