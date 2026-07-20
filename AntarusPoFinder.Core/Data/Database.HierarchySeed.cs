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
