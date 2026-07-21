using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using Xunit;

namespace AntarusPoFinder.Tests;

public class DatabaseSmokeTests
{
    private const string CopyPath =
        @"C:\Users\Ilia\AppData\Local\Temp\claude\C--Users-Ilia\1935bdd0-ec51-45f9-b154-5b1c46d751cd\scratchpad\db-test\copy.db";

    [Fact]
    public void OpensRealDbCopy_AndSeedsPixelController()
    {
        Assert.True(File.Exists(CopyPath), "Copy of the real DB must exist before running this test.");

        using var db = new Database(CopyPath);

        var controllers = db.GetAllControllerModels();
        Assert.Contains(controllers, c => c.Name == "PIXEL");
        Assert.Contains(controllers, c => c.Name == "SMH4");

        var allMods = db.GetAllModifications();
        var pixelMods = allMods.FindAll(m => m.ControllerName == "PIXEL");
        Assert.Equal(4, pixelMods.Count);
        Assert.Contains(pixelMods, m => m.DisplayName == "PIXEL-2511");

        var exts = db.GetAllowedExtensions();
        Assert.Contains("psl", exts);

        // Real historical data must still be there (uploaded via the Python app).
        var allVersions = db.GetAllFwVersionsWithNames(includeArchived: true);
        Assert.True(allVersions.Count >= 2, "Expected the real upload history to be preserved.");
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
        var tmpDb = Path.Combine(Path.GetTempPath(), $"antarus_resv_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new Database(tmpDb);
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
        finally
        {
            // Microsoft.Data.Sqlite pools native connections even after Dispose(), so the file
            // handle can outlive the `using` block — clear pools first so cleanup doesn't throw.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { tmpDb, tmpDb + "-wal", tmpDb + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public void ExpireStaleReservations_CancelsOnlyPastDueOnes()
    {
        // Regression test for a real formatting bug: expires_at must be stamped in the exact same
        // "yyyy-MM-dd HH:mm:ss" shape as NowIso(), because the expiry check is a plain string
        // comparison (expires_at < @now) — a mismatched separator (e.g. "T" vs " ") would make every
        // stored expires_at sort after any real "now" value, silently disabling expiry forever.
        var tmpDb = Path.Combine(Path.GetTempPath(), $"antarus_resv_expiry_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new Database(tmpDb);
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
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { tmpDb, tmpDb + "-wal", tmpDb + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public void DeleteEquipmentGroup_FreshGroupWithSingleSubtype_DoesNotThrow()
    {
        // Regression test: DeleteEquipmentGroup used to bind IN(...) params via unnamed
        // SqliteParameter objects against literal '?' placeholders — Microsoft.Data.Sqlite has no
        // positional binding and throws "ParameterName must be set" the moment the command runs.
        // This fired on every real group deletion, since Database.EnsureEveryGroupHasSubtype
        // guarantees every group has at least one subtype (subtypeIds.Count is never 0).
        var tmpDb = Path.Combine(Path.GetTempPath(), $"antarus_delgroup_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new Database(tmpDb);
            var groupId = db.UpsertEquipmentGroup(new EquipmentGroup { Name = "ТестШкаф", Prefix = 999, SortOrder = 1 });
            db.UpsertEquipmentSubtype(new EquipmentSubType { GroupId = groupId, Name = "—", Prefix = 0, FolderName = "ТестШкаф", SortOrder = 1 });

            db.DeleteEquipmentGroup(groupId);

            Assert.DoesNotContain(db.GetAllEquipmentGroups(), g => g.Id == groupId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { tmpDb, tmpDb + "-wal", tmpDb + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public void FulfillReservation_MarksFulfilled_RemovesFromOpenList()
    {
        var tmpDb = Path.Combine(Path.GetTempPath(), $"antarus_resv_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new Database(tmpDb);
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
        finally
        {
            // Microsoft.Data.Sqlite pools native connections even after Dispose(), so the file
            // handle can outlive the `using` block — clear pools first so cleanup doesn't throw.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { tmpDb, tmpDb + "-wal", tmpDb + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
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
        var tmpDb = Path.Combine(Path.GetTempPath(), $"antarus_hmi_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new Database(tmpDb);
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
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { tmpDb, tmpDb + "-wal", tmpDb + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }
}

public class ServiceSmokeTests
{
    private const string CopyPath =
        @"C:\Users\Ilia\AppData\Local\Temp\claude\C--Users-Ilia\1935bdd0-ec51-45f9-b154-5b1c46d751cd\scratchpad\db-test\copy.db";

    [Fact]
    public void SearchService_FindsRealHistoricalUpload()
    {
        using var db = new AntarusPoFinder.Core.Data.Database(CopyPath);
        // First real upload in the copied DB was for a SMH5 controller (НГР).
        var results = AntarusPoFinder.Core.Services.SearchService.Search(db, "SMH5");
        Assert.NotEmpty(results);
    }

    [Fact]
    public void HierarchyService_EnsureStructure_CreatesExpectedFolders()
    {
        var tmpRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "antarus_wpf_test_" + System.Guid.NewGuid());
        System.IO.Directory.CreateDirectory(tmpRoot);
        try
        {
            using var db = new AntarusPoFinder.Core.Data.Database(CopyPath);
            var svc = new AntarusPoFinder.Core.Services.HierarchyService(db);
            var result = svc.EnsureStructure(tmpRoot);

            Assert.True(result.Ok, string.Join("; ", result.Errors));
            Assert.True(result.CreatedCount > 0);
            Assert.True(System.IO.Directory.Exists(System.IO.Path.Combine(tmpRoot, "ПО")));
            Assert.True(System.IO.Directory.Exists(System.IO.Path.Combine(tmpRoot, "Параметры")));
            Assert.True(System.IO.Directory.Exists(System.IO.Path.Combine(tmpRoot, "Конфиг")));
        }
        finally
        {
            System.IO.Directory.Delete(tmpRoot, recursive: true);
        }
    }

    [Fact]
    public void EnsureEveryGroupHasSubtype_BackfillsPlaceholder_AndSyncFwFromDisk_FindsIt()
    {
        // Regression test for a bug where a group with zero subtypes made SyncFwFromDisk silently
        // skip it forever (it built a null-subtype fallback entry, then immediately discarded it).
        // The real fix is structural — a group can never have zero subtypes after Database.Migrate
        // runs — so this proves both halves: the backfill happens, and sync then finds firmware for it.
        var tmpDb = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"antarus_subtype_test_{Guid.NewGuid():N}.db");
        var tmpRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"antarus_subtype_disk_{Guid.NewGuid():N}");
        try
        {
            int groupId;
            using (var db1 = new Database(tmpDb))
            {
                groupId = db1.UpsertEquipmentGroup(new EquipmentGroup { Name = "ТЕСТГРУППА", Prefix = 77, SortOrder = 99 });
                Assert.Empty(db1.GetSubtypesForGroup(groupId));
            }

            using var db = new Database(tmpDb);
            var subtypes = db.GetSubtypesForGroup(groupId);
            Assert.Single(subtypes);
            Assert.Equal("—", subtypes[0].Name);
            Assert.Equal("ТЕСТГРУППА", subtypes[0].FolderName);

            var versionName = FwVersionNumber.Build(77, 0, 1, 1, includeDate: false).ToString();
            var versionDir = System.IO.Path.Combine(tmpRoot, "ПО", "ТЕСТГРУППА", "SMH4", versionName);
            System.IO.Directory.CreateDirectory(versionDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(versionDir, "firmware.psl"), "test");

            var svc = new AntarusPoFinder.Core.Services.HierarchyService(db);
            var result = svc.SyncFwFromDisk(tmpRoot);

            Assert.True(result.Ok, string.Join("; ", result.Errors));
            Assert.Equal(1, result.Added);
            Assert.Contains(result.AddedItems, item => item.Contains("ТЕСТГРУППА") && item.Contains("SMH4"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { tmpDb, tmpDb + "-wal", tmpDb + "-shm" })
                if (File.Exists(f)) File.Delete(f);
            if (System.IO.Directory.Exists(tmpRoot)) System.IO.Directory.Delete(tmpRoot, recursive: true);
        }
    }

    [Fact]
    public void PrefixUniqueness_DetectsCollisions_ForGroupsAndSubtypesWithinGroup()
    {
        var tmpDb = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"antarus_prefix_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new Database(tmpDb);
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
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { tmpDb, tmpDb + "-wal", tmpDb + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public void RenameGroupFolder_MovesDiskFolder_AndRemapsStoredFwVersionPath()
    {
        // Regression test for a real crash: renaming a group used to be DB-only, which orphaned the
        // physical folder (next EnsureStructure/scan would sweep it into Неизвестное) and left
        // already-uploaded firmware's stored disk_path pointing at the now-nonexistent old folder.
        var tmpDb = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"antarus_grouprename_test_{Guid.NewGuid():N}.db");
        var tmpRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"antarus_grouprename_disk_{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(tmpRoot);
        try
        {
            using var db = new Database(tmpDb);
            var svc = new AntarusPoFinder.Core.Services.HierarchyService(db);

            var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
            var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
            var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");

            var oldDiskPath = System.IO.Path.Combine(tmpRoot, "ПО", "НГР", "КНС", "SMH4", "2.1.001.0001");
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

            var result = svc.RenameGroupFolder(tmpRoot, "НГР", "НГР2");
            Assert.True(result.Ok, result.Error);
            Assert.Equal(1, result.RemappedRows);

            var newDiskPath = System.IO.Path.Combine(tmpRoot, "ПО", "НГР2", "КНС", "SMH4", "2.1.001.0001");
            Assert.False(System.IO.Directory.Exists(System.IO.Path.Combine(tmpRoot, "ПО", "НГР")));
            Assert.True(System.IO.Directory.Exists(newDiskPath));
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(newDiskPath, "fw.psl")));

            db.RenameEquipmentGroup(group.Id!.Value, "НГР2");
            var updated = db.GetFwVersionById(fwId);
            Assert.NotNull(updated);
            Assert.Equal(newDiskPath, updated!.DiskPath);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { tmpDb, tmpDb + "-wal", tmpDb + "-shm" })
                if (File.Exists(f)) File.Delete(f);
            if (System.IO.Directory.Exists(tmpRoot)) System.IO.Directory.Delete(tmpRoot, recursive: true);
        }
    }

    [Fact]
    public void PslInspector_DetectsSmh4FromRealSampleFile()
    {
        const string smh4File =
            @"D:\MyFolder\Новая папка\2. КПЧ\1.1. Антарус 2.0\КПЧ_SMH4_(SML_3.35.214)_v3.41_Pass.psl";
        if (!System.IO.File.Exists(smh4File)) return; // sample not present in this environment, skip

        var info = AntarusPoFinder.Core.Services.PslInspector.Inspect(smh4File);
        Assert.Equal("SMH4", info.Plc.Model);
    }
}
