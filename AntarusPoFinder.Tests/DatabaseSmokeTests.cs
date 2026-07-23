using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

public class DatabaseSmokeTests
{
    /// <summary>Used to open a copy of a real user's production DB from a long-gone Claude-session
    /// temp folder (only ever valid on one developer's machine, until temp cleanup silently deleted
    /// it) and assert on its exact contents ("ровно 4 модификации PIXEL" etc). PIXEL/SMH4/the "psl"
    /// extension are actually all default seed data (see HierarchyDefaultsData), not anything specific
    /// to that user's uploads — a fresh Database already has them. The "real upload history survives"
    /// half is now a fixture the test seeds and owns itself, instead of an assumption about someone
    /// else's disk.</summary>
    [Fact]
    public void FreshDatabase_SeedsPixelControllerAndAllowedExtensions_AndPreservesSeededFwVersions()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var controllers = db.GetAllControllerModels();
        Assert.Contains(controllers, c => c.Name == "PIXEL");
        Assert.Contains(controllers, c => c.Name == "SMH4");

        var allMods = db.GetAllModifications();
        var pixelMods = allMods.FindAll(m => m.ControllerName == "PIXEL");
        Assert.Equal(4, pixelMods.Count);
        Assert.Contains(pixelMods, m => m.DisplayName == "PIXEL-2511");

        var exts = db.GetAllowedExtensions();
        Assert.Contains("psl", exts);

        var hmiExts = db.GetAllowedExtensionsHmi();
        Assert.Contains("fsprj", hmiExts);

        // Seed exactly the "already uploaded firmware" data this test controls, then read it back
        // through the normal query path — proves fw_versions survives a round trip through
        // RunDataMigrations/Migrate the same way real uploads do, without depending on any specific
        // user's real upload history.
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");
        for (var i = 1; i <= 2; i++)
        {
            db.AddFwVersion(new FwVersionRecord
            {
                SubtypeId = subtype.Id!.Value,
                ControllerId = mod.ControllerId,
                EqPrefix = group.Prefix,
                SubPrefix = subtype.Prefix,
                HwVersion = mod.HwVersion,
                SwVersion = i,
                DtStr = $"2026010{i}_0000",
                VersionRaw = $"2.1.001.000{i}.2026010{i}_0000",
                Filename = "fw.psl",
                DiskPath = "",
                Description = "seeded upload history",
                Status = "active",
            });
        }

        var allVersions = db.GetAllFwVersionsWithNames(includeArchived: true);
        Assert.Equal(2, allVersions.Count);
    }

    /// <summary>Repro for pre-existing duplicate rows left over from the sync-duplication bug
    /// (see ConfigSyncTests.Import_SameParamFile_RepeatedSyncRounds_NeverDuplicates for the sync-side
    /// fix). Creates 3 identical param_files rows directly (as AddParamFile calls, not through sync,
    /// so it doesn't depend on the import fix), reopens the DB — RunDataMigrations must collapse them
    /// to one row and union their tags, same as it does for stale '—' subtypes.</summary>
    [Fact]
    public void ReopeningDb_DedupesExistingDuplicateParamFiles_MergesTags()
    {
        using var dbFile = new TempDb();

        int subtypeId;
        using (var db = new Database(dbFile.Path))
        {
            var group = db.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
            subtypeId = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "ХП").Id!.Value;

            var id1 = db.AddParamFile(new ParamFile { SubtypeId = subtypeId, Manufacturer = "Danfoss", Filename = "dup.dcfx", DiskPath = @"Z:\A" });
            var id2 = db.AddParamFile(new ParamFile { SubtypeId = subtypeId, Manufacturer = "Danfoss", Filename = "dup.dcfx", DiskPath = @"Y:\B" });
            db.AddParamFile(new ParamFile { SubtypeId = subtypeId, Manufacturer = "Danfoss", Filename = "dup.dcfx", DiskPath = @"C:\C" });
            db.UpdateParamFileTags(id1, "жара");
            db.UpdateParamFileTags(id2, "холод");
            Assert.Equal(3, db.GetParamFiles().Count(f => f.Filename == "dup.dcfx"));
        }

        using var reopened = new Database(dbFile.Path);
        var files = reopened.GetParamFiles().Where(f => f.Filename == "dup.dcfx").ToList();
        Assert.Single(files);
        Assert.Contains("жара", files[0].Tags);
        Assert.Contains("холод", files[0].Tags);
    }

    [Fact]
    public void FwVersionNumber_BuildAndParse_RoundTrips()
    {
        var v = Core.Domain.FwVersionNumber.Build(2, 1, 42, 3, new System.DateTime(2026, 4, 22, 13, 48, 0));
        Assert.Equal("2.1.042.0003.20260422_1348", v.ToString());

        var parsed = Core.Domain.FwVersionNumber.Parse(v.ToString());
        Assert.NotNull(parsed);
        Assert.Equal(42, parsed!.HwVersion);
        Assert.Equal(3, parsed.SwVersion);
    }

    [Fact]
    public void FwVersionNumber_BuildAndParse_WithoutDate_RoundTrips()
    {
        // "Добавлять дату/время" unchecked in Upload — manager decided the timestamp isn't needed.
        var v = Core.Domain.FwVersionNumber.Build(2, 1, 42, 3, includeDate: false);
        Assert.Equal("2.1.042.0003", v.ToString());
        Assert.Equal("", v.DtStr);
        Assert.Equal("hw42.sw3", v.Display);

        var parsed = Core.Domain.FwVersionNumber.Parse(v.ToString());
        Assert.NotNull(parsed);
        Assert.Equal(42, parsed!.HwVersion);
        Assert.Equal(3, parsed.SwVersion);
        Assert.Equal("", parsed.DtStr);
    }

    [Fact]
    public void ReserveNextVersion_LocksNumber_PreventsCollision_CancelSkipsForever()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");

        int before = db.GetNextSwVersion(subtype.Id!.Value, mod.ControllerId, mod.HwVersion);

        var reservation = db.ReserveNextVersion(subtype.Id!.Value, mod.ControllerId, mod.HwVersion,
            group.Prefix, subtype.Prefix, "tester");
        var parsed = FwVersionNumber.Parse(reservation.VersionRaw);
        Assert.NotNull(parsed);
        Assert.Equal(before, parsed!.SwVersion);

        // The live preview (no reservation picked yet) must not suggest the same number again.
        int nextAfterReserve = db.GetNextSwVersion(subtype.Id!.Value, mod.ControllerId, mod.HwVersion);
        Assert.Equal(before + 1, nextAfterReserve);

        var active = db.GetActiveReservations(subtype.Id!.Value, mod.ControllerId, mod.HwVersion);
        Assert.Single(active);
        Assert.Equal(reservation.Id, active[0].Id);

        var openAcrossAll = db.GetAllOpenReservations();
        Assert.Contains(openAcrossAll, r => r.Id == reservation.Id);

        db.CancelReservation(reservation.Id!.Value);
        Assert.Empty(db.GetActiveReservations(subtype.Id!.Value, mod.ControllerId, mod.HwVersion));

        // Cancelled numbers are never reused — next free number stays past the cancelled one.
        int nextAfterCancel = db.GetNextSwVersion(subtype.Id!.Value, mod.ControllerId, mod.HwVersion);
        Assert.Equal(before + 1, nextAfterCancel);
    }

    [Fact]
    public void ExpireStaleReservations_CancelsOnlyPastDueOnes()
    {
        // Regression test for a real formatting bug: expires_at must be stamped in the exact same
        // "yyyy-MM-dd HH:mm:ss" shape as NowIso(), because the expiry check is a plain string
        // comparison (expires_at < @now) — a mismatched separator (e.g. "T" vs " ") would make every
        // stored expires_at sort after any real "now" value, silently disabling expiry forever.
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");

        var reservation = db.ReserveNextVersion(subtype.Id!.Value, mod.ControllerId, mod.HwVersion,
            group.Prefix, subtype.Prefix, "tester", ttlHours: 1);
        Assert.NotEmpty(reservation.ExpiresAt);

        var neverExpiring = db.ReserveNextVersion(subtype.Id!.Value, mod.ControllerId, mod.HwVersion,
            group.Prefix, subtype.Prefix, "tester", ttlHours: null);
        Assert.Equal("", neverExpiring.ExpiresAt);

        // Not due yet (expires in 1h, checked "now") — must survive.
        Assert.Equal(0, db.ExpireStaleReservations());
        Assert.Single(db.GetActiveReservations(subtype.Id!.Value, mod.ControllerId, mod.HwVersion)
            .Where(r => r.Id == reservation.Id));

        // Simulate 2 hours passing — now it's past due, the never-expiring one must survive it.
        var future = System.DateTime.ParseExact(reservation.ReservedAt, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
            .AddHours(2).ToString("yyyy-MM-dd HH:mm:ss");
        Assert.Equal(1, db.ExpireStaleReservations(future));

        var active = db.GetActiveReservations(subtype.Id!.Value, mod.ControllerId, mod.HwVersion);
        Assert.DoesNotContain(active, r => r.Id == reservation.Id);
        Assert.Contains(active, r => r.Id == neverExpiring.Id);
    }

    [Fact]
    public void DeleteEquipmentGroup_FreshGroupWithSingleSubtype_DoesNotThrow()
    {
        // Regression test: DeleteEquipmentGroup used to bind IN(...) params via unnamed
        // SqliteParameter objects against literal '?' placeholders — Microsoft.Data.Sqlite has no
        // positional binding and throws "ParameterName must be set" the moment the command runs.
        // This fired on every real group deletion, since Database.EnsureEveryGroupHasSubtype
        // guarantees every group has at least one subtype (subtypeIds.Count is never 0).
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var groupId = db.UpsertEquipmentGroup(new EquipmentGroup { Name = "ТестШкаф", Prefix = 999, SortOrder = 1 });
        db.UpsertEquipmentSubtype(new EquipmentSubType { GroupId = groupId, Name = "—", Prefix = 0, FolderName = "ТестШкаф", SortOrder = 1 });

        db.DeleteEquipmentGroup(groupId);

        Assert.DoesNotContain(db.GetAllEquipmentGroups(), g => g.Id == groupId);
    }

    [Fact]
    public void FulfillReservation_MarksFulfilled_RemovesFromOpenList()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");

        var reservation = db.ReserveNextVersion(subtype.Id!.Value, mod.ControllerId, mod.HwVersion,
            group.Prefix, subtype.Prefix, "tester");
        var fwv = FwVersionNumber.Parse(reservation.VersionRaw)!;

        var fwId = db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion,
            SwVersion = fwv.SwVersion,
            DtStr = fwv.DtStr,
            VersionRaw = fwv.Raw,
            Filename = "test.psl",
            DiskPath = "",
            Description = "test",
            Status = "active",
        });
        db.FulfillReservation(reservation.Id!.Value, fwId);

        Assert.Empty(db.GetActiveReservations(subtype.Id!.Value, mod.ControllerId, mod.HwVersion));
        Assert.DoesNotContain(db.GetAllOpenReservations(), r => r.Id == reservation.Id);
    }

    /// <summary>Regression coverage for the HMI-project fields (separate optional upload for
    /// controllers like Segnetics, where PLC is a single .psl/.lfs but HMI is its own multi-file
    /// project — see UploadView's "Добавить HMI-проект" checkbox) and the executable-hint fields
    /// (which file inside a folder-without-a-recognized-extension the operator flagged as the one
    /// to actually run). Both are plain columns on fw_versions — this just confirms AddFwVersion/
    /// GetFwVersionById round-trip them instead of silently dropping them.</summary>
    [Fact]
    public void AddFwVersion_RoundTripsHmiPathAndExecutableHints()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");

        var fwId = db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion,
            SwVersion = 1,
            DtStr = "20260101_0000",
            VersionRaw = "9.9.9.1.20260101_0000",
            Filename = "unrecognized_folder",
            DiskPath = @"C:\po\fw",
            Description = "test",
            Status = "active",
            HmiPath = @"C:\po\fw\HMI",
            ExecutableHint = "run.exe",
            HmiExecutableHint = "project.fsprj",
        });

        var row = db.GetFwVersionById(fwId);
        Assert.NotNull(row);
        Assert.Equal(@"C:\po\fw\HMI", row!.HmiPath);
        Assert.Equal("run.exe", row.ExecutableHint);
        Assert.Equal("project.fsprj", row.HmiExecutableHint);
    }
}

public class ServiceSmokeTests
{
    /// <summary>Used to require the real fw_version upload history from a copied production DB
    /// ("First real upload in the copied DB was for a SMH5 controller") — SearchService itself only
    /// searches whatever fw_versions rows exist, real or not, so a self-seeded SMH5 upload exercises
    /// the exact same code path (SearchFwVersionsByTokens matching on controller name).</summary>
    [Fact]
    public void SearchService_FindsSeededFirmwareUpload_ByControllerName()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH5");
        db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion,
            SwVersion = 1,
            DtStr = "20260101_0000",
            VersionRaw = "2.5.005.0001.20260101_0000",
            Filename = "fw.psl",
            DiskPath = "",
            Description = "seeded SMH5 upload",
            Status = "active",
        });

        var results = AntarusPoFinder.Core.Services.SearchService.Search(db, "SMH5");
        Assert.NotEmpty(results);
    }

    [Fact]
    public void HierarchyService_EnsureStructure_CreatesExpectedFolders()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var tmpRoot = new TempRoot();

        var svc = new AntarusPoFinder.Core.Services.HierarchyService(db);
        var result = svc.EnsureStructure(tmpRoot.Path);

        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.True(result.CreatedCount > 0);
        Assert.True(System.IO.Directory.Exists(System.IO.Path.Combine(tmpRoot.Path, "ПО")));
        Assert.True(System.IO.Directory.Exists(System.IO.Path.Combine(tmpRoot.Path, "Параметры")));
        Assert.True(System.IO.Directory.Exists(System.IO.Path.Combine(tmpRoot.Path, "Конфиг")));
    }

    [Fact]
    public void EnsureEveryGroupHasSubtype_BackfillsPlaceholder_AndSyncFwFromDisk_FindsIt()
    {
        // Regression test for a bug where a group with zero subtypes made SyncFwFromDisk silently
        // skip it forever (it built a null-subtype fallback entry, then immediately discarded it).
        // The real fix is structural — a group can never have zero subtypes after Database.Migrate
        // runs — so this proves both halves: the backfill happens, and sync then finds firmware for it.
        using var dbFile = new TempDb();
        using var tmpRoot = new TempRoot();

        int groupId;
        using (var db1 = new Database(dbFile.Path))
        {
            groupId = db1.UpsertEquipmentGroup(new EquipmentGroup { Name = "ТЕСТГРУППА", Prefix = 77, SortOrder = 99 });
            Assert.Empty(db1.GetSubtypesForGroup(groupId));
        }

        using var db = new Database(dbFile.Path);
        var subtypes = db.GetSubtypesForGroup(groupId);
        Assert.Single(subtypes);
        Assert.Equal("—", subtypes[0].Name);
        Assert.Equal("ТЕСТГРУППА", subtypes[0].FolderName);

        var versionName = FwVersionNumber.Build(77, 0, 1, 1, includeDate: false).ToString();
        var versionDir = System.IO.Path.Combine(tmpRoot.Path, "ПО", "ТЕСТГРУППА", "SMH4", versionName);
        System.IO.Directory.CreateDirectory(versionDir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(versionDir, "firmware.psl"), "test");

        var svc = new AntarusPoFinder.Core.Services.HierarchyService(db);
        var result = svc.SyncFwFromDisk(tmpRoot.Path);

        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.Equal(1, result.Added);
        Assert.Contains(result.AddedItems, item => item.Contains("ТЕСТГРУППА") && item.Contains("SMH4"));
    }

    [Fact]
    public void PrefixUniqueness_DetectsCollisions_ForGroupsAndSubtypesWithinGroup()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");

        Assert.False(db.GroupPrefixTaken(9999));
        var otherGroupId = db.UpsertEquipmentGroup(new EquipmentGroup { Name = "ДРУГОЙТИП", Prefix = 9999, SortOrder = 100 });
        Assert.True(db.GroupPrefixTaken(9999));
        // Editing the same group to keep its own prefix must not trip the check on itself.
        Assert.False(db.GroupPrefixTaken(9999, excludeGroupId: otherGroupId));

        var subtypes = db.GetSubtypesForGroup(group.Id!.Value);
        var existingPrefix = subtypes[0].Prefix;
        Assert.True(db.SubtypePrefixTakenInGroup(group.Id!.Value, existingPrefix));
        Assert.False(db.SubtypePrefixTakenInGroup(group.Id!.Value, existingPrefix, excludeSubtypeId: subtypes[0].Id));
        Assert.False(db.SubtypePrefixTakenInGroup(group.Id!.Value, 88888));

        Assert.True(db.CountSubtypesForGroup(group.Id!.Value) >= 1);
    }

    [Fact]
    public void RenameGroupFolder_MovesDiskFolder_AndRemapsStoredFwVersionPath()
    {
        // Regression test for a real crash: renaming a group used to be DB-only, which orphaned the
        // physical folder (next EnsureStructure/scan would sweep it into Неизвестное) and left
        // already-uploaded firmware's stored disk_path pointing at the now-nonexistent old folder.
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var tmpRoot = new TempRoot();

        var svc = new AntarusPoFinder.Core.Services.HierarchyService(db);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");

        var oldDiskPath = System.IO.Path.Combine(tmpRoot.Path, "ПО", "НГР", "КНС", "SMH4", "2.1.001.0001");
        System.IO.Directory.CreateDirectory(oldDiskPath);
        System.IO.File.WriteAllText(System.IO.Path.Combine(oldDiskPath, "fw.psl"), "test");

        var fwId = db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion,
            SwVersion = 1,
            DtStr = "",
            VersionRaw = "2.1.001.0001",
            Filename = "fw.psl",
            DiskPath = oldDiskPath,
            Description = "test",
            Status = "active",
        });

        var result = svc.RenameGroupFolder(tmpRoot.Path, "НГР", "НГР2");
        Assert.True(result.Ok, result.Error);
        Assert.Equal(1, result.RemappedRows);

        var newDiskPath = System.IO.Path.Combine(tmpRoot.Path, "ПО", "НГР2", "КНС", "SMH4", "2.1.001.0001");
        Assert.False(System.IO.Directory.Exists(System.IO.Path.Combine(tmpRoot.Path, "ПО", "НГР")));
        Assert.True(System.IO.Directory.Exists(newDiskPath));
        Assert.True(System.IO.File.Exists(System.IO.Path.Combine(newDiskPath, "fw.psl")));

        db.RenameEquipmentGroup(group.Id!.Value, "НГР2");
        var updated = db.GetFwVersionById(fwId);
        Assert.NotNull(updated);
        Assert.Equal(newDiskPath, updated!.DiskPath);
    }
}
