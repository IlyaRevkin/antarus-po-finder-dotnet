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
            dbA.AddAllowedExtensionHmi("synctest_hmi");

            var exported = dbA.ExportHierarchyData();
            Assert.Contains(exported.EquipmentSubtypes, s => s.Name == "ХП-ТЕСТ");
            Assert.Contains(exported.ControllerModifications, m => m.DisplayName == "PIXEL2-9999");
            Assert.Contains(exported.Tags!, t => t == "synctest");
            Assert.Contains(exported.AllowedExtensions!, e => e == "synctest");
            Assert.Contains(exported.AllowedExtensionsHmi!, e => e == "synctest_hmi");

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
            Assert.DoesNotContain(dbB.GetAllowedExtensionsHmi(), e => e == "synctest_hmi");

            // Dry-run preview must report the same counts as the real apply, and change nothing.
            var preview = dbB.PreviewImportHierarchyData(exported);
            Assert.Equal(1, preview.SubtypesUpdated);
            Assert.Equal(1, preview.ModificationsAdded);
            Assert.Equal(1, preview.TagsAdded);
            Assert.Equal(1, preview.ExtensionsAdded);
            Assert.Equal(1, preview.ExtensionsHmiAdded);
            var subtypeBStillOldName = dbB.GetSubtypesForGroup(groupB.Id!.Value).First(s => s.Id == subtypeB.Id);
            Assert.Equal("ХП", subtypeBStillOldName.Name); // preview must not have written anything

            // Real apply.
            var counts = dbB.ImportHierarchyData(exported);
            Assert.Equal(1, counts.SubtypesUpdated);
            Assert.Equal(0, counts.SubtypesAdded); // must UPDATE the existing row, never add a duplicate
            Assert.Equal(1, counts.ModificationsAdded);
            Assert.Equal(1, counts.TagsAdded);
            Assert.Equal(1, counts.ExtensionsAdded);
            Assert.Equal(1, counts.ExtensionsHmiAdded);

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
            Assert.Contains(dbB.GetAllowedExtensionsHmi(), e => e == "synctest_hmi");

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

            // Обе машины знают тег, и он проставлен на прошивке у B; затем A его осознанно удаляет —
            // именно удаление (событие с отметкой времени, см. Database.FlatLists.cs), а не просто
            // отсутствие имени в чужой выгрузке, и должно доехать до B.
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

            dbA.AddTag("only_on_b");
            dbA.DeleteTag("only_on_b");

            var exported = dbA.ExportHierarchyData();
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
    public void Import_ConfigFromMachineThatNeverSawIt_KeepsLocallyAddedManufacturer_AndKeepsDeletedOneDeleted()
    {
        // Обе половины жалобы пользователя разом — до отметок времени в flat_list_state списки
        // синхронизировались «зеркалом» (чего нет во входящем наборе — удалить локально), и потому:
        //   • «добавил производителей ПЧ/УПП, а они не синхронизировались» — их стирал импорт с
        //     машины, которая о новых именах ещё не знала;
        //   • «залил новые настройки, а с какого-то компа опять мусорное название» — удалённое имя
        //     возвращала любая машина, ещё не забравшая конфиг с удалением.
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);

            // Стартовое общее состояние: обе машины знают «мусорное» имя.
            dbA.AddParamManufacturer("МУСОРНОЕ");
            dbB.AddParamManufacturer("МУСОРНОЕ");

            // B наводит порядок: добавляет нужного производителя и убирает мусорный.
            dbB.AddParamManufacturer("TESTVENDOR");
            dbB.DeleteParamManufacturer("МУСОРНОЕ");

            // A выгружает свой конфиг, ничего не зная про правки B (снимок сделан раньше них).
            var staleFromA = dbA.ExportHierarchyData();
            var counts = dbB.ImportHierarchyData(staleFromA);

            var manufacturersB = dbB.GetParamManufacturers();
            Assert.Contains("TESTVENDOR", manufacturersB);        // не должен быть стёрт чужим снимком
            Assert.DoesNotContain("МУСОРНОЕ", manufacturersB); // и не должен воскреснуть
            Assert.Equal(0, counts.ManufacturersAdded);
            Assert.Equal(0, counts.ManufacturersRemoved);

            // Встречное направление: теперь A забирает конфиг B и приходит к тому же состоянию.
            dbA.ImportHierarchyData(dbB.ExportHierarchyData());
            var manufacturersA = dbA.GetParamManufacturers();
            Assert.Contains("TESTVENDOR", manufacturersA);
            Assert.DoesNotContain("МУСОРНОЕ", manufacturersA);
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

    /// <summary>Regression test for the repeatedly-reported "FORTUS controller resurrects" bug:
    /// controller_models was upsert-only in ImportHierarchyDataCore, same gap equipment_subtypes had
    /// before the fix above — a controller deleted on one machine kept coming back the moment any
    /// sync partner (or a stale JSON export) still listed it, because nothing ever mirrored the
    /// deletion. Uses two brand-new custom controllers rather than the seeded SMH4/KINCO/PIXEL family
    /// — every default controller ships with its own default modification (see HierarchyDefaults),
    /// which would make it look "referenced" on both machines regardless of this fix, masking the
    /// bug. Mirrors the subtype test above: one deleted controller nobody locally references (must
    /// disappear on B), one B still has a local upload under (must be kept) — same shape "FORTUS"
    /// actually had: a controller someone added, then deleted, that another machine still had
    /// firmware filed under.</summary>
    [Fact]
    public void Import_DeletedController_PropagatesButKeepsLocallyReferencedOnes()
    {
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);

            var unusedId = dbA.UpsertControllerModel(new ControllerModel { Name = "FORTUS-TEST-UNUSED", SortOrder = 900 });
            var usedId = dbA.UpsertControllerModel(new ControllerModel { Name = "FORTUS-TEST-USED", SortOrder = 901 });

            // Handshake so sync_ids correlate before anything diverges (see class doc) — B now has
            // both custom controllers too, with matching sync_ids, but none of A's default modifications.
            dbB.ImportHierarchyData(dbA.ExportHierarchyData());

            var groupB = dbB.GetAllEquipmentGroups().First(g => g.Name == "НГР");
            var subtypeB = dbB.GetSubtypesForGroup(groupB.Id!.Value).First(s => s.Name == "УПД");
            var usedB = dbB.GetAllControllerModels().First(c => c.Name == "FORTUS-TEST-USED");
            var localFwId = dbB.AddFwVersion(new FwVersionRecord
            {
                SubtypeId = subtypeB.Id!.Value,
                ControllerId = usedB.Id!.Value,
                EqPrefix = groupB.Prefix,
                SubPrefix = subtypeB.Prefix,
                HwVersion = 1,
                SwVersion = 1,
                DtStr = "20260101_0000",
                VersionRaw = "9.9.1.1.20260101_0000",
                Filename = "local_only_fortus_test.psl",
                DiskPath = "",
                Description = "локальная загрузка только на этой машине",
                Status = "active",
            });

            // A deletes both custom controllers locally.
            DeleteControllerRaw(pathA, unusedId);
            DeleteControllerRaw(pathA, usedId);

            var exported = dbA.ExportHierarchyData();
            Assert.DoesNotContain(exported.ControllerModels, c => c.Name == "FORTUS-TEST-UNUSED");
            Assert.DoesNotContain(exported.ControllerModels, c => c.Name == "FORTUS-TEST-USED");

            var preview = dbB.PreviewImportHierarchyData(exported);
            Assert.Equal(1, preview.ControllersRemoved);
            Assert.Equal(1, preview.ControllersSkippedDelete);
            // Preview must not have written anything.
            Assert.Contains(dbB.GetAllControllerModels(), c => c.Name == "FORTUS-TEST-UNUSED");

            var counts = dbB.ImportHierarchyData(exported);
            Assert.Equal(1, counts.ControllersRemoved);
            Assert.Equal(1, counts.ControllersSkippedDelete);

            var controllersB = dbB.GetAllControllerModels();
            Assert.DoesNotContain(controllersB, c => c.Name == "FORTUS-TEST-UNUSED"); // unreferenced -> gone
            Assert.Contains(controllersB, c => c.Name == "FORTUS-TEST-USED"); // still referenced locally -> kept

            // The local fw_version under the kept controller must survive untouched.
            var fwAfter = dbB.GetAllFwVersionsWithNames(includeArchived: true).First(f => f.Id == localFwId);
            Assert.Equal("local_only_fortus_test.psl", fwAfter.Filename);
            Assert.Equal("FORTUS-TEST-USED", fwAfter.CtrlName);

            // Idempotent on a second pass.
            var secondPass = dbB.ImportHierarchyData(exported);
            Assert.Equal(0, secondPass.ControllersRemoved);
            Assert.Equal(1, secondPass.ControllersSkippedDelete);
        }
        finally
        {
            Cleanup(pathA, pathB);
        }
    }

    private static void DeleteControllerRaw(string dbPath, int controllerId)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM controller_models WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", controllerId);
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
                Manufacturer = "TESTVENDOR",
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

    // ── Эталонная синхронизация (authoritative) — ImportHierarchyData(authoritative: true) ──────
    //
    // Сценарий из задачи: мусорная запись справочника завелась на машине B, а машина A (в этих
    // тестах — «эталон») её никогда не видела вовсе — не «удалила», а именно никогда не имела.
    // Обычная синхронизация не может её убрать: надгробить-то нечего (запись никогда не существовала
    // там, откуда мог бы прийти tombstone). Ниже — ровно тот случай, через тег (у тегов/производителей/
    // расширений нет sync_id и обычно они не удаляются зеркалом вообще — см. ImportFlatList).

    /// <summary>Эталонный снимок удаляет у получателя справочную запись, которой в снимке нет —
    /// даже если она никогда не существовала у отправителя (не «была и удалена», а «никогда не
    /// была»), именно тот пробел, который обычная синхронизация закрыть не может.</summary>
    [Fact]
    public void ImportHierarchyData_Authoritative_RemovesLocalTagAbsentFromSnapshot()
    {
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);

            dbB.AddTag("Е2Е-ЭТАЛОН-МУСОРНЫЙ-ТЕГ"); // A никогда о нём не знала — не «удалён», а «не было»
            Assert.Contains("Е2Е-ЭТАЛОН-МУСОРНЫЙ-ТЕГ", dbB.GetAllTags());

            var exported = dbA.ExportHierarchyData();
            Assert.DoesNotContain("Е2Е-ЭТАЛОН-МУСОРНЫЙ-ТЕГ", exported.Tags!);

            var preview = dbB.PreviewImportHierarchyData(exported, authoritative: true);
            Assert.Equal(1, preview.TagsRemoved);
            // Preview must not have written anything.
            Assert.Contains("Е2Е-ЭТАЛОН-МУСОРНЫЙ-ТЕГ", dbB.GetAllTags());

            var counts = dbB.ImportHierarchyData(exported, authoritative: true);
            Assert.Equal(1, counts.TagsRemoved);
            Assert.DoesNotContain("Е2Е-ЭТАЛОН-МУСОРНЫЙ-ТЕГ", dbB.GetAllTags());
        }
        finally
        {
            Cleanup(pathA, pathB);
        }
    }

    /// <summary>Регресс-защита: тот же самый ровно снимок, но БЕЗ authoritative — текущее аддитивное
    /// поведение (ImportFlatList) не должно измениться ни на йоту. Лишний тег, о котором отправитель
    /// просто ничего не знает, обязан остаться — это и есть разница между «обычным» и «эталонным»
    /// снимком, которую весь этот механизм и вводит.</summary>
    [Fact]
    public void ImportHierarchyData_NonAuthoritative_KeepsLocalTagAbsentFromSnapshot()
    {
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);

            dbB.AddTag("Е2Е-ОБЫЧНЫЙ-МУСОРНЫЙ-ТЕГ");
            var exported = dbA.ExportHierarchyData();
            Assert.DoesNotContain("Е2Е-ОБЫЧНЫЙ-МУСОРНЫЙ-ТЕГ", exported.Tags!);

            // authoritative по умолчанию false — все существующие вызовы (без параметра) идут этим путём.
            var counts = dbB.ImportHierarchyData(exported);
            Assert.Equal(0, counts.TagsRemoved);
            Assert.Contains("Е2Е-ОБЫЧНЫЙ-МУСОРНЫЙ-ТЕГ", dbB.GetAllTags());
        }
        finally
        {
            Cleanup(pathA, pathB);
        }
    }

    /// <summary>Критическое ограничение задачи: эталон касается ТОЛЬКО справочника. Прошивки/файлы
    /// параметров получателя, отсутствующие у «эталона» (потому что тот их никогда не загружал),
    /// обязаны остаться нетронутыми и полностью незамеченными — не только не удалиться, но даже не
    /// попасть ни в один из счётчиков FwVersions*/ParamFiles.</summary>
    [Fact]
    public void ImportHierarchyData_Authoritative_DoesNotTouchLocalFwVersionsOrParamFiles()
    {
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);

            var groupB = dbB.GetAllEquipmentGroups().First(g => g.Name == "НГР");
            var subtypeB = dbB.GetSubtypesForGroup(groupB.Id!.Value).First(s => s.Name == "УПД");
            var mod = dbB.GetAllModifications().First(m => m.ControllerName == "SMH4");
            dbB.AddFwVersion(new FwVersionRecord
            {
                SubtypeId = subtypeB.Id!.Value,
                ControllerId = mod.ControllerId,
                EqPrefix = groupB.Prefix,
                SubPrefix = subtypeB.Prefix,
                HwVersion = mod.HwVersion,
                SwVersion = 1,
                DtStr = "20260722_0000",
                VersionRaw = "9.9.9.1.20260722_0000",
                Filename = "authoritative_test.psl",
                DiskPath = "",
                Description = "локальная прошивка только на этой машине",
                Status = "active",
            });

            var groupParamsB = dbB.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
            var subtypeParamsB = dbB.GetSubtypesForGroup(groupParamsB.Id!.Value).First(s => s.Name == "ХП");
            dbB.AddParamFile(new ParamFile
            {
                SubtypeId = subtypeParamsB.Id!.Value,
                Manufacturer = "Danfoss",
                Filename = "authoritative_test.dcfx",
                DiskPath = "x",
                Description = "локальный файл параметров только на этой машине",
                UploadDate = "2026-07-22 10:00:00",
            });

            var fwCountBefore = dbB.GetAllFwVersionsWithNames(includeArchived: true).Count;
            var paramCountBefore = dbB.GetParamFiles().Count;

            var exported = dbA.ExportHierarchyData(); // A: свежая база, ни одной прошивки/файла параметров
            Assert.Empty(exported.FwVersions);
            Assert.Empty(exported.ParamFiles);

            var counts = dbB.ImportHierarchyData(exported, authoritative: true);
            Assert.Equal(0, counts.FwVersions);
            Assert.Equal(0, counts.FwVersionsRemoved);
            Assert.Equal(0, counts.ParamFiles);

            Assert.Equal(fwCountBefore, dbB.GetAllFwVersionsWithNames(includeArchived: true).Count);
            Assert.Equal(paramCountBefore, dbB.GetParamFiles().Count);
            Assert.Contains(dbB.GetAllFwVersionsWithNames(includeArchived: true), f => f.VersionRaw == "9.9.9.1.20260722_0000");
            Assert.Contains(dbB.GetParamFiles(), f => f.Filename == "authoritative_test.dcfx");
        }
        finally
        {
            Cleanup(pathA, pathB);
        }
    }

    /// <summary>FK-предохранитель эталонной синхронизации: производитель, отсутствующий в снимке
    /// «эталона», НЕ удаляется, если получатель им ещё помечает локальный файл параметров — тот же
    /// паттерн, что SubtypesSkippedDelete/ControllersSkippedDelete выше, только «мягкий» (текстовое
    /// совпадение, а не физический FK — см. Database.CollectUsedManufacturers).</summary>
    [Fact]
    public void ImportHierarchyData_Authoritative_SkipsManufacturerStillUsedByLocalParamFile()
    {
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);

            dbB.AddParamManufacturer("Е2Е-ИСПОЛЬЗУЕМЫЙ-ПРОИЗВОДИТЕЛЬ");
            var groupB = dbB.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
            var subtypeB = dbB.GetSubtypesForGroup(groupB.Id!.Value).First(s => s.Name == "ХП");
            dbB.AddParamFile(new ParamFile
            {
                SubtypeId = subtypeB.Id!.Value,
                Manufacturer = "Е2Е-ИСПОЛЬЗУЕМЫЙ-ПРОИЗВОДИТЕЛЬ",
                Filename = "used_manufacturer.dcfx",
                DiskPath = "x",
                Description = "",
                UploadDate = "2026-07-22 10:00:00",
            });

            var exported = dbA.ExportHierarchyData(); // A никогда не знала об этом производителе
            Assert.DoesNotContain(exported.ParamManufacturers!, m => m.Name == "Е2Е-ИСПОЛЬЗУЕМЫЙ-ПРОИЗВОДИТЕЛЬ");

            var preview = dbB.PreviewImportHierarchyData(exported, authoritative: true);
            Assert.Equal(0, preview.ManufacturersRemoved);
            Assert.Equal(1, preview.ManufacturersSkippedDelete);
            Assert.Contains("Е2Е-ИСПОЛЬЗУЕМЫЙ-ПРОИЗВОДИТЕЛЬ", dbB.GetParamManufacturers());

            var counts = dbB.ImportHierarchyData(exported, authoritative: true);
            Assert.Equal(0, counts.ManufacturersRemoved);
            Assert.Equal(1, counts.ManufacturersSkippedDelete);
            Assert.Contains("Е2Е-ИСПОЛЬЗУЕМЫЙ-ПРОИЗВОДИТЕЛЬ", dbB.GetParamManufacturers());
        }
        finally
        {
            Cleanup(pathA, pathB);
        }
    }
}
