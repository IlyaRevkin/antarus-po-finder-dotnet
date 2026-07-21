using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Simulates two independently-built installs sharing a config file, the scenario behind
/// Настройки → Общие → Экспорт/Импорт. Verifies the sync_id-based upsert actually propagates a
/// rename/edit (not just brand-new additions) WITHOUT touching or orphaning fw_versions that only
/// exist on the receiving machine — the exact risk plain name-matching + delete/reinsert would have
/// hit, since fw_versions references equipment_subtypes.id with no ON DELETE CASCADE.
///
/// IMPORTANT documented limitation this test also demonstrates: sync_id is assigned independently
/// and randomly by each fresh install's own seeding (BackfillSyncIds), so two databases that have
/// NEVER exchanged a config yet have NO shared invariant to match a renamed row against (name no
/// longer matches, sync_id never matched to begin with) — a rename that happens before the very
/// first sync is indistinguishable from "a new entity was added" and lands as a duplicate. The fix
/// only works from the SECOND sync onward, once a first successful name-matched sync has adopted a
/// shared sync_id (see the name-match branches in Database.ConfigExchange.cs). Operationally: a new
/// PC must do one config import while its hierarchy still matches the source machine (e.g. right
/// after first install, before anyone touches Настройки → Иерархия) for later renames to track
/// correctly — this is exactly what both tests below set up before diverging.</summary>
public class ConfigSyncTests
{
    private static string NewTempDb() => Path.Combine(Path.GetTempPath(), $"antarus_sync_test_{Guid.NewGuid():N}.db");

    private static void Cleanup(params string[] dbPaths)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var db in dbPaths)
            foreach (var f in new[] { db, db + "-wal", db + "-shm" })
                if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void Import_RenamesSubtypeInPlace_KeepsLocalFwVersionAttached_NeverDuplicates()
    {
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);

            // Handshake sync: while A and B still match on name (nothing diverged yet), sync once
            // so their sync_ids for shared seed data get correlated — see class doc comment above
            // for why this step is required before a later rename can track correctly.
            var handshakeCounts = dbB.ImportHierarchyData(dbA.ExportHierarchyData());
            var gA0 = dbA.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
            var sA0 = dbA.GetSubtypesForGroup(gA0.Id!.Value).First(s => s.Name == "ХП");
            var gB0 = dbB.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
            var sB0 = dbB.GetSubtypesForGroup(gB0.Id!.Value).First(s => s.Name == "ХП");
            Assert.True(handshakeCounts.SubtypesAdded == 0, $"handshake should add 0 subtypes, added {handshakeCounts.SubtypesAdded}");
            Assert.Equal(sA0.SyncId, sB0.SyncId); // handshake must have correlated sync_ids while names still matched
            Assert.False(string.IsNullOrEmpty(sA0.SyncId));

            // Machine A: rename "ХП" -> "ХП-ТЕСТ" (simulates a future rename feature / manual edit —
            // there's no in-app rename UI yet, so this goes straight at the row like that UI would).
            var groupA = dbA.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
            var subtypeA = dbA.GetSubtypesForGroup(groupA.Id!.Value).First(s => s.Name == "ХП");
            RenameSubtype(pathA, subtypeA.Id!.Value, "ХП-ТЕСТ");

            // Machine A: add a brand-new modification + tag + allowed extension.
            var pixel2 = dbA.GetAllControllerModels().First(c => c.Name == "PIXEL2");
            dbA.AddControllerModification(pixel2.Id!.Value, "PIXEL2-9999", 99, "синтетическая тестовая модификация");
            dbA.AddTag("synctest");
            dbA.AddAllowedExtension("synctest");

            var exported = dbA.ExportHierarchyData();
            Assert.Contains(exported.EquipmentSubtypes, s => s.Name == "ХП-ТЕСТ");
            Assert.Contains(exported.ControllerModifications, m => m.DisplayName == "PIXEL2-9999");
            Assert.Contains(exported.Tags!, t => t == "synctest");
            Assert.Contains(exported.AllowedExtensions!, e => e == "synctest");

            // Machine B, BEFORE import: still has the original name, plus its OWN local upload
            // under that exact subtype — this must survive the rename untouched.
            var groupB = dbB.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
            var subtypeB = dbB.GetSubtypesForGroup(groupB.Id!.Value).First(s => s.Name == "ХП");
            var smh4 = dbB.GetAllModifications().First(m => m.ControllerName == "SMH4");
            var localFwId = dbB.AddFwVersion(new FwVersionRecord
            {
                SubtypeId = subtypeB.Id!.Value,
                ControllerId = smh4.ControllerId,
                EqPrefix = groupB.Prefix,
                SubPrefix = subtypeB.Prefix,
                HwVersion = smh4.HwVersion,
                SwVersion = 1,
                DtStr = "20260101_0000",
                VersionRaw = "9.9.9.1.20260101_0000",
                Filename = "local_only.psl",
                DiskPath = "",
                Description = "локальная загрузка только на этой машине",
                Status = "active",
            });
            Assert.DoesNotContain(dbB.GetAllModifications(), m => m.DisplayName == "PIXEL2-9999");
            Assert.DoesNotContain(dbB.GetAllTags(), t => t == "synctest");
            Assert.DoesNotContain(dbB.GetAllowedExtensions(), e => e == "synctest");

            // Dry-run preview must report the same counts as the real apply, and change nothing.
            var preview = dbB.PreviewImportHierarchyData(exported);
            Assert.Equal(1, preview.SubtypesUpdated);
            Assert.Equal(1, preview.ModificationsAdded);
            Assert.Equal(1, preview.TagsAdded);
            Assert.Equal(1, preview.ExtensionsAdded);
            var subtypeBStillOldName = dbB.GetSubtypesForGroup(groupB.Id!.Value).First(s => s.Id == subtypeB.Id);
            Assert.Equal("ХП", subtypeBStillOldName.Name); // preview must not have written anything

            // Real apply.
            var counts = dbB.ImportHierarchyData(exported);
            Assert.Equal(1, counts.SubtypesUpdated);
            Assert.Equal(0, counts.SubtypesAdded); // must UPDATE the existing row, never add a duplicate
            Assert.Equal(1, counts.ModificationsAdded);
            Assert.Equal(1, counts.TagsAdded);
            Assert.Equal(1, counts.ExtensionsAdded);

            // The rename propagated onto the SAME row (same id) — not a delete+reinsert.
            var allSubtypesB = dbB.GetSubtypesForGroup(groupB.Id!.Value);
            Assert.Single(allSubtypesB, s => s.Id == subtypeB.Id);
            Assert.Equal("ХП-ТЕСТ", allSubtypesB.First(s => s.Id == subtypeB.Id).Name);
            Assert.DoesNotContain(allSubtypesB, s => s.Name == "ХП"); // old name gone, no leftover duplicate

            // The local-only fw_version survived, untouched, still linked to the (renamed) subtype.
            var fwAfter = dbB.GetAllFwVersionsWithNames(includeArchived: true).First(f => f.Id == localFwId);
            Assert.Equal("local_only.psl", fwAfter.Filename);
            Assert.Equal("ХП-ТЕСТ", fwAfter.SubtypeName);

            Assert.Contains(dbB.GetAllModifications(), m => m.DisplayName == "PIXEL2-9999");
            Assert.Contains(dbB.GetAllTags(), t => t == "synctest");
            Assert.Contains(dbB.GetAllowedExtensions(), e => e == "synctest");

            // Re-importing the same export must be a no-op (idempotent) — proves sync_id matching
            // works on the second pass too, not just "first contact".
            var secondPass = dbB.ImportHierarchyData(exported);
            Assert.Equal(0, secondPass.TotalChanges);
        }
        finally
        {
            Cleanup(pathA, pathB);
        }
    }

    [Fact]
    public void Import_RemovingATagLocally_StripsItFromFwVersionsText_ButNeverDeletesFwVersionRow()
    {
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);

            // B has a tag that A does not (A's export represents "the current truth" — tags/
            // extensions are the two categories safe to fully mirror, see Database.ConfigExchange).
            dbB.AddTag("only_on_b");
            var group = dbB.GetAllEquipmentGroups().First(g => g.Name == "НГР");
            var subtype = dbB.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
            var mod = dbB.GetAllModifications().First(m => m.ControllerName == "SMH4");
            var fwId = dbB.AddFwVersion(new FwVersionRecord
            {
                SubtypeId = subtype.Id!.Value,
                ControllerId = mod.ControllerId,
                EqPrefix = group.Prefix,
                SubPrefix = subtype.Prefix,
                HwVersion = mod.HwVersion,
                SwVersion = 1,
                DtStr = "20260101_0000",
                VersionRaw = "1.1.1.1.20260101_0000",
                Filename = "tagged.psl",
                DiskPath = "",
                Description = "test",
                Tags = "only_on_b",
                Status = "active",
            });

            var exported = dbA.ExportHierarchyData(); // A never had "only_on_b"
            var counts = dbB.ImportHierarchyData(exported);
            Assert.Equal(1, counts.TagsRemoved);

            Assert.DoesNotContain(dbB.GetAllTags(), t => t == "only_on_b");
            // The fw_version row itself must survive — only the tag TEXT is stripped from it.
            var fw = dbB.GetAllFwVersionsWithNames(includeArchived: true).First(f => f.Id == fwId);
            Assert.Equal("tagged.psl", fw.Filename);
            Assert.Equal("", fw.Tags);
        }
        finally
        {
            Cleanup(pathA, pathB);
        }
    }

    [Fact]
    public void Import_ManufacturerAddedAndRemovedOnA_FullyMirrorsOnB()
    {
        // Regression test: a colleague added a new manufacturer and deleted an old one on their
        // machine; after exchanging configs, the new one showed up locally but the deleted one
        // never disappeared. ImportHierarchyDataCore used to only ever INSERT param_manufacturers
        // rows found in the incoming export, with no removal half — unlike tags/extensions, which
        // are fully mirrored (added-and-removed). Manufacturers are name-keyed with no FK-by-id
        // reference from param_files, so mirroring them the same way is safe.
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);

            // Both machines start with a manufacturer in common.
            dbA.AddParamManufacturer("Yaskawa");
            dbB.AddParamManufacturer("Yaskawa");

            // Colleague (A): adds a new manufacturer, removes the shared old one.
            dbA.AddParamManufacturer("Mitsubishi");
            dbA.DeleteParamManufacturer("Yaskawa");

            var exported = dbA.ExportHierarchyData();
            var counts = dbB.ImportHierarchyData(exported);
            Assert.Equal(1, counts.ManufacturersAdded);
            Assert.Equal(1, counts.ManufacturersRemoved);

            var manufacturersB = dbB.GetParamManufacturers();
            Assert.Contains("Mitsubishi", manufacturersB);
            Assert.DoesNotContain("Yaskawa", manufacturersB); // deleted on A must disappear on B too

            // Re-importing the same export must be a no-op (idempotent).
            var secondPass = dbB.ImportHierarchyData(exported);
            Assert.Equal(0, secondPass.ManufacturersAdded);
            Assert.Equal(0, secondPass.ManufacturersRemoved);
        }
        finally
        {
            Cleanup(pathA, pathB);
        }
    }

    [Fact]
    public void Import_SubtypeDeletedOnA_RemovesUnreferencedCopyOnB_ButKeepsOneStillInUse()
    {
        // Regression test: a colleague deleted a subtype on their machine, but it kept "resurrecting"
        // on other machines after every sync — equipment_subtypes was upsert-only, never mirrored a
        // deletion (unlike tags/extensions/manufacturers, see class doc). Two subtypes deleted on A:
        // one nobody has uploaded firmware under (must disappear on B), one B itself has a local
        // upload under (must be kept — deleting it would orphan that fw_version's subtype_id).
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);

            // Handshake so sync_ids correlate before anything diverges (see class doc).
            dbB.ImportHierarchyData(dbA.ExportHierarchyData());

            var groupA = dbA.GetAllEquipmentGroups().First(g => g.Name == "НГР");
            var unreferencedA = dbA.GetSubtypesForGroup(groupA.Id!.Value).First(s => s.Name == "КР");
            var stillUsedA = dbA.GetSubtypesForGroup(groupA.Id!.Value).First(s => s.Name == "УПД");

            var groupB = dbB.GetAllEquipmentGroups().First(g => g.Name == "НГР");
            var stillUsedB = dbB.GetSubtypesForGroup(groupB.Id!.Value).First(s => s.Name == "УПД");
            var mod = dbB.GetAllModifications().First(m => m.ControllerName == "SMH4");
            var localFwId = dbB.AddFwVersion(new FwVersionRecord
            {
                SubtypeId = stillUsedB.Id!.Value,
                ControllerId = mod.ControllerId,
                EqPrefix = groupB.Prefix,
                SubPrefix = stillUsedB.Prefix,
                HwVersion = mod.HwVersion,
                SwVersion = 1,
                DtStr = "20260101_0000",
                VersionRaw = "9.9.9.1.20260101_0000",
                Filename = "local_only.psl",
                DiskPath = "",
                Description = "локальная загрузка только на этой машине",
                Status = "active",
            });

            // A deletes both subtypes locally (no in-app rename/delete UI yet — same raw-SQL
            // approach RenameSubtype below uses to simulate one).
            DeleteSubtype(pathA, unreferencedA.Id!.Value);
            DeleteSubtype(pathA, stillUsedA.Id!.Value);

            var exported = dbA.ExportHierarchyData();
            Assert.DoesNotContain(exported.EquipmentSubtypes, s => s.Name == "КР");
            Assert.DoesNotContain(exported.EquipmentSubtypes, s => s.Name == "УПД");

            var preview = dbB.PreviewImportHierarchyData(exported);
            Assert.Equal(1, preview.SubtypesRemoved);
            Assert.Equal(1, preview.SubtypesSkippedDelete);
            // Preview must not have written anything.
            Assert.Contains(dbB.GetSubtypesForGroup(groupB.Id!.Value), s => s.Name == "КР");

            var counts = dbB.ImportHierarchyData(exported);
            Assert.Equal(1, counts.SubtypesRemoved);
            Assert.Equal(1, counts.SubtypesSkippedDelete);

            var subtypesB = dbB.GetSubtypesForGroup(groupB.Id!.Value);
            Assert.DoesNotContain(subtypesB, s => s.Name == "КР"); // unreferenced -> gone
            Assert.Contains(subtypesB, s => s.Name == "УПД"); // still referenced locally -> kept

            // The local fw_version under the kept subtype must survive untouched.
            var fwAfter = dbB.GetAllFwVersionsWithNames(includeArchived: true).First(f => f.Id == localFwId);
            Assert.Equal("local_only.psl", fwAfter.Filename);
            Assert.Equal("УПД", fwAfter.SubtypeName);

            // Idempotent on a second pass.
            var secondPass = dbB.ImportHierarchyData(exported);
            Assert.Equal(0, secondPass.SubtypesRemoved);
            Assert.Equal(1, secondPass.SubtypesSkippedDelete);
        }
        finally
        {
            Cleanup(pathA, pathB);
        }
    }

    private static void DeleteSubtype(string dbPath, int subtypeId)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM equipment_subtypes WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", subtypeId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>There's no in-app rename UI for subtypes yet — this opens a second raw connection
    /// to the same SQLite file to simulate one (WAL mode allows concurrent readers/writers), so the
    /// test exercises the sync layer's rename handling without needing that feature to exist yet.</summary>
    /// <summary>Repro for the "178 записей вместо 2 файлов" bug: param_files used to be matched on
    /// import by (subtype, manufacturer, filename, disk_path), but disk_path is an absolute path
    /// baked in on the exporting machine (root_path\Параметры\...) which essentially never matches
    /// the importing machine's own root — so every sync round re-inserted the same file as "new".
    /// Simulates exactly that: A and B have different local roots (hence different disk_path for
    /// the very same file), sync repeatedly, must converge to ONE row, not grow every round.</summary>
    [Fact]
    public void Import_SameParamFile_RepeatedSyncRounds_NeverDuplicates()
    {
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);

            // Handshake first, as required (see class doc) so the shared subtype correlates by sync_id.
            dbB.ImportHierarchyData(dbA.ExportHierarchyData());

            var groupA = dbA.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
            var subtypeA = dbA.GetSubtypesForGroup(groupA.Id!.Value).First(s => s.Name == "ХП");
            dbA.AddParamFile(new ParamFile
            {
                SubtypeId = subtypeA.Id!.Value,
                Manufacturer = "Danfoss",
                Filename = "params_v1.dcfx",
                DiskPath = @"Z:\Software\Antarus Finder\Параметры\ПЖ\ХП\Danfoss", // machine A's own root
                Description = "тестовый файл параметров",
                UploadDate = "2026-07-21 10:00:00",
            });

            var exported = dbA.ExportHierarchyData();

            // Machine B imports the same export three times in a row (mirrors the reported "every
            // minute" auto-sync) — must land exactly one row every time, even though B's local
            // disk_path for this same logical file (were it uploaded locally) would be totally
            // different from A's (different root/drive letter).
            dbB.ImportHierarchyData(exported);
            dbB.ImportHierarchyData(exported);
            var counts3 = dbB.ImportHierarchyData(exported);

            Assert.Equal(0, counts3.ParamFiles); // third+ round must add nothing at all
            var filesB = dbB.GetParamFiles().Where(f => f.Filename == "params_v1.dcfx").ToList();
            Assert.Single(filesB);
        }
        finally
        {
            Cleanup(pathA, pathB);
        }
    }

    private static void RenameSubtype(string dbPath, int subtypeId, string newName)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE equipment_subtypes SET name=@n WHERE id=@id";
        cmd.Parameters.AddWithValue("@n", newName);
        cmd.Parameters.AddWithValue("@id", subtypeId);
        cmd.ExecuteNonQuery();
    }
}
