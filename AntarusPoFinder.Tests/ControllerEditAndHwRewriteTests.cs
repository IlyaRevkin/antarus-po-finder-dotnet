using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

public class ControllerEditAndHwRewriteTests
{
    // Сид Pixel/Pixel2 нумеруется настоящей 4-значной ревизией платы, а не выдуманными 40-51 —
    // прошивка под ревизию должна совпадать с ней по hw.
    [Fact]
    public void Seed_PixelModifications_UseRealFourDigitRevisionAsHwVersion()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var mods = db.GetAllModifications();
        Assert.Equal(1321, mods.First(m => m.DisplayName == "PIXEL2-1321").HwVersion);
        Assert.Equal(3422, mods.First(m => m.DisplayName == "PIXEL2-3422").HwVersion);
        Assert.Equal(2511, mods.First(m => m.DisplayName == "PIXEL-2511").HwVersion);
    }

    [Fact]
    public void UpdateControllerModification_ChangesTypeNameHwAndDescription()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var smh4 = db.GetAllControllerModels().First(c => c.Name == "SMH4");
        var smh5 = db.GetAllControllerModels().First(c => c.Name == "SMH5");
        var modId = db.AddControllerModification(smh4.Id!.Value, "SMH4-TEST", 4, "старое");

        db.UpdateControllerModification(modId, smh5.Id!.Value, "SMH5-TEST", 5, "новое");

        var updated = db.GetAllModifications().First(m => m.Id == modId);
        Assert.Equal("SMH5-TEST", updated.DisplayName);
        Assert.Equal(5, updated.HwVersion);
        Assert.Equal("новое", updated.Description);
        Assert.Equal(smh5.Id!.Value, updated.ControllerId);
    }

    [Fact]
    public void RewriteControllerHwVersion_RenamesVersionFolderOnDisk_AndUpdatesDbRow_PreservingDate()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var tmpRoot = new TempRoot();
        var svc = new HierarchyService(db);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var ctrl = db.GetAllControllerModels().First(c => c.Name == "SMH4");

        // Старый hw = 44 (как было в старом сиде), строка версии с датой.
        var oldRaw = "2.1.0044.0001.20260101_1200";
        var oldDir = Path.Combine(tmpRoot.Path, "ПО", "НГР", "КНС", "SMH4", oldRaw);
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "fw.psl"), "test");

        var fwId = db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = ctrl.Id!.Value,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = 44, SwVersion = 1, DtStr = "20260101_1200",
            VersionRaw = oldRaw, Filename = "fw.psl", DiskPath = oldDir,
            Description = "test", Status = "active",
        });

        var res = svc.RewriteControllerHwVersion(tmpRoot.Path, ctrl.Id!.Value, 44, 1321);

        Assert.True(res.Ok, string.Join("; ", res.Errors));
        Assert.Equal(1, res.UpdatedRows);

        var newRaw = "2.1.1321.0001.20260101_1200";
        var newDir = Path.Combine(tmpRoot.Path, "ПО", "НГР", "КНС", "SMH4", newRaw);
        Assert.False(Directory.Exists(oldDir));
        Assert.True(Directory.Exists(newDir));
        Assert.True(File.Exists(Path.Combine(newDir, "fw.psl")));

        var row = db.GetFwVersionById(fwId)!;
        Assert.Equal(1321, row.HwVersion);
        Assert.Equal(newRaw, row.VersionRaw);
        Assert.Equal(newDir, row.DiskPath);
    }

    [Fact]
    public void RewriteControllerHwVersion_RecordWithoutDiskFiles_UpdatesDbOnly()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var tmpRoot = new TempRoot();
        var svc = new HierarchyService(db);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var ctrl = db.GetAllControllerModels().First(c => c.Name == "SMH5");

        var fwId = db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = ctrl.Id!.Value,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = 5, SwVersion = 2, DtStr = "",
            VersionRaw = "2.1.0005.0002", Filename = "fw.psl", DiskPath = "",
            Description = "no disk", Status = "active",
        });

        var res = svc.RewriteControllerHwVersion(tmpRoot.Path, ctrl.Id!.Value, 5, 9);

        Assert.True(res.Ok);
        Assert.Equal(1, res.UpdatedRows);
        var row = db.GetFwVersionById(fwId)!;
        Assert.Equal(9, row.HwVersion);
        Assert.Equal("2.1.0009.0002", row.VersionRaw);
    }

    [Fact]
    public void RewriteControllerHwVersion_MissingDiskFolder_SkipsRowAndReportsError()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var tmpRoot = new TempRoot();
        var svc = new HierarchyService(db);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var ctrl = db.GetAllControllerModels().First(c => c.Name == "SMH4");

        // disk_path задан, но папки на диске нет (шара недоступна) — БД трогать нельзя.
        var ghost = Path.Combine(tmpRoot.Path, "ПО", "НГР", "КНС", "SMH4", "2.1.0044.0003");
        var fwId = db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = ctrl.Id!.Value,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = 44, SwVersion = 3, DtStr = "",
            VersionRaw = "2.1.0044.0003", Filename = "fw.psl", DiskPath = ghost,
            Description = "ghost", Status = "active",
        });

        var res = svc.RewriteControllerHwVersion(tmpRoot.Path, ctrl.Id!.Value, 44, 1321);

        Assert.False(res.Ok);
        Assert.Equal(0, res.UpdatedRows);
        Assert.NotEmpty(res.Errors);
        var row = db.GetFwVersionById(fwId)!;
        Assert.Equal(44, row.HwVersion);
        Assert.Equal("2.1.0044.0003", row.VersionRaw);
    }

    [Fact]
    public void RenameControllerFolders_MovesFoldersAcrossTree_AndRemapsStoredPaths()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var tmpRoot = new TempRoot();
        var svc = new HierarchyService(db);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var ctrl = db.GetAllControllerModels().First(c => c.Name == "SMH4");

        var oldDir = Path.Combine(tmpRoot.Path, "ПО", "НГР", "КНС", "SMH4", "2.1.0004.0001");
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "fw.psl"), "test");

        var fwId = db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = ctrl.Id!.Value,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = 4, SwVersion = 1, DtStr = "",
            VersionRaw = "2.1.0004.0001", Filename = "fw.psl", DiskPath = oldDir,
            Description = "test", Status = "active",
        });

        var result = svc.RenameControllerFolders(tmpRoot.Path, "SMH4", "SMH4NEW");
        Assert.True(result.Ok, result.Error);
        Assert.True(result.RemappedRows >= 1);

        db.UpdateControllerModelName(ctrl.Id!.Value, "SMH4NEW");

        var newDir = Path.Combine(tmpRoot.Path, "ПО", "НГР", "КНС", "SMH4NEW", "2.1.0004.0001");
        Assert.False(Directory.Exists(Path.Combine(tmpRoot.Path, "ПО", "НГР", "КНС", "SMH4")));
        Assert.True(File.Exists(Path.Combine(newDir, "fw.psl")));
        Assert.Equal(newDir, db.GetFwVersionById(fwId)!.DiskPath);
    }
}
