using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>FirmwareUpdateService had zero test coverage before this — it drives the startup
/// "firmware update available" banner/auto-update (see MainWindowViewModel.CheckForFirmwareUpdates),
/// so a regression here means a naladchik silently keeps running outdated cached firmware with no
/// prompt to update. Covers the core detection rule: a locally cached firmware whose newest cached
/// version is older than the server's latest active version counts as "available update"; anything
/// never downloaded, already current, or with an empty cache folder must not.
///
/// Uses ConfigService.LocalFw (via LocalFirmwareCache) — redirected to an isolated temp folder for
/// the whole test process by TestHelpers.TestAppDataInit (ANTARUS_TEST_APPDATA), never the real
/// %LocalAppData%\AntarusPOFinder\.</summary>
public class FirmwareUpdateServiceTests
{
    private static (Database Db, TempDb DbFile, ConfigService Cfg) NewDb()
    {
        var dbFile = new TempDb();
        var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db); // constructor creates ConfigService.LocalFw on disk
        return (db, dbFile, cfg);
    }

    /// <summary>Seeds a brand-new, uniquely-named subtype (a GUID-suffixed name, never reused) so the
    /// resulting LocalFirmwareCache directory name is guaranteed unique too — ConfigService.LocalFw is
    /// a real (if redirected, see TestAppDataInit) directory that OUTLIVES any single test method/
    /// TempDb, so two tests sharing the same subtype+controller name would otherwise leave each
    /// other's cached version folders behind and contaminate "already current"/"outdated" detection
    /// across unrelated tests (and across repeated runs of the whole suite).</summary>
    private static FwVersionRecord SeedActiveFwVersion(Database db, string groupName, string versionRaw, string dtStr)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == groupName);
        var subtypeName = $"ТЕСТ-{Guid.NewGuid():N}"[..12];
        var subtypeId = db.UpsertEquipmentSubtype(new EquipmentSubType
        {
            GroupId = group.Id!.Value, Name = subtypeName, Prefix = 90, FolderName = subtypeName, SortOrder = 999,
        });
        var mod = db.GetAllModifications().First(m => m.ControllerName == "PIXEL" && m.DisplayName == "PIXEL-2511");
        var user = db.GetOrCreateUser("ivanov", "ivanov");

        db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtypeId, ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix, SubPrefix = 90, HwVersion = mod.HwVersion, SwVersion = 1,
            DtStr = dtStr, VersionRaw = versionRaw, Filename = "firmware.psl", DiskPath = "",
            Description = "test upload", Status = "active", AuthorId = user.Id,
        });

        return db.GetLatestActiveFwVersions().First(f => f.VersionRaw == versionRaw);
    }

    private static string LocalCacheName(FwVersionRecord latest) =>
        ($"{(!string.IsNullOrEmpty(latest.SubtypeFolder) ? latest.SubtypeFolder : latest.SubtypeName)} {latest.CtrlName}").Trim();

    [Fact]
    public void GetAvailableUpdates_LocalCacheOlderThanServer_ReportsUpdateWithCorrectVersions()
    {
        var (db, dbFile, cfg) = NewDb();
        using var _f = dbFile; using var _db = db;

        var latest = SeedActiveFwVersion(db, "НГР", versionRaw: "1.1.1.1.20260201_0000", dtStr: "20260201_0000");
        var localDir = Path.Combine(LocalFirmwareCache.DirFor(LocalCacheName(latest)), "1.1.1.1.20260101_0000");
        Directory.CreateDirectory(localDir);
        File.WriteAllText(Path.Combine(localDir, "firmware.psl"), "old cached firmware");

        var updates = FirmwareUpdateService.GetAvailableUpdates(db);

        var update = Assert.Single(updates);
        Assert.Equal("1.1.1.1.20260101_0000", update.CurrentLocalVersion);
        Assert.Equal("1.1.1.1.20260201_0000", update.Latest.VersionRaw);
    }

    [Fact]
    public void GetAvailableUpdates_NeverDownloaded_NotReportedAsUpdate()
    {
        // No local cache directory at all — this is "available to download for the first time", not
        // an "update" (see the method's own comment: "never downloaded — not an update, just an
        // available sync").
        var (db, dbFile, cfg) = NewDb();
        using var _f = dbFile; using var _db = db;

        SeedActiveFwVersion(db, "НГР", versionRaw: "1.1.1.1.20260201_0000", dtStr: "20260201_0000");

        var updates = FirmwareUpdateService.GetAvailableUpdates(db);

        Assert.Empty(updates);
    }

    [Fact]
    public void GetAvailableUpdates_LocalCacheAlreadyCurrent_NotReportedAsUpdate()
    {
        var (db, dbFile, cfg) = NewDb();
        using var _f = dbFile; using var _db = db;

        var latest = SeedActiveFwVersion(db, "НГР", versionRaw: "1.1.1.1.20260201_0000", dtStr: "20260201_0000");
        var localDir = Path.Combine(LocalFirmwareCache.DirFor(LocalCacheName(latest)), latest.VersionRaw);
        Directory.CreateDirectory(localDir);
        File.WriteAllText(Path.Combine(localDir, "firmware.psl"), "already current firmware");

        var updates = FirmwareUpdateService.GetAvailableUpdates(db);

        Assert.Empty(updates);
    }

    [Fact]
    public void GetAvailableUpdates_CacheDirExistsButEmpty_NotReportedAsUpdate()
    {
        var (db, dbFile, cfg) = NewDb();
        using var _f = dbFile; using var _db = db;

        var latest = SeedActiveFwVersion(db, "НГР", versionRaw: "1.1.1.1.20260201_0000", dtStr: "20260201_0000");
        Directory.CreateDirectory(LocalFirmwareCache.DirFor(LocalCacheName(latest))); // base dir exists, no version subfolders

        var updates = FirmwareUpdateService.GetAvailableUpdates(db);

        Assert.Empty(updates);
    }

    [Fact]
    public void GetAvailableUpdates_MultipleCachedFirmwares_OnlyOutdatedOnesReported()
    {
        var (db, dbFile, cfg) = NewDb();
        using var _f = dbFile; using var _db = db;

        // Firmware A: outdated cache -> should be reported.
        var latestA = SeedActiveFwVersion(db, "НГР", versionRaw: "1.1.1.1.20260201_0000", dtStr: "20260201_0000");
        var localDirA = Path.Combine(LocalFirmwareCache.DirFor(LocalCacheName(latestA)), "1.1.1.1.20260101_0000");
        Directory.CreateDirectory(localDirA);
        File.WriteAllText(Path.Combine(localDirA, "firmware.psl"), "old A");

        // Firmware B: cache already current -> should NOT be reported.
        var latestB = SeedActiveFwVersion(db, "НГР", versionRaw: "1.1.2.1.20260201_0000", dtStr: "20260201_0000");
        var localDirB = Path.Combine(LocalFirmwareCache.DirFor(LocalCacheName(latestB)), latestB.VersionRaw);
        Directory.CreateDirectory(localDirB);
        File.WriteAllText(Path.Combine(localDirB, "firmware.psl"), "current B");

        // Firmware C: never downloaded -> should NOT be reported.
        SeedActiveFwVersion(db, "НГР", versionRaw: "1.1.3.1.20260201_0000", dtStr: "20260201_0000");

        var updates = FirmwareUpdateService.GetAvailableUpdates(db);

        var update = Assert.Single(updates);
        Assert.Equal("1.1.1.1.20260201_0000", update.Latest.VersionRaw);
    }

    [Fact]
    public void GetAvailableUpdates_NoFwVersionsAtAll_ReturnsEmptyList()
    {
        var (db, dbFile, cfg) = NewDb();
        using var _f = dbFile; using var _db = db;

        var updates = FirmwareUpdateService.GetAvailableUpdates(db);

        Assert.Empty(updates);
    }
}
