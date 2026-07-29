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

    // Баг pixel2: правка hw 044→1321 переименовывала папку версии и version_raw, но папку панели
    // «{версия}_hmi» (лежит в отдельной папке HMI контроллера, не внутри версии) не трогала. Из-за
    // этого карточка показывала «HMI от версии 2.4.044.0005», хотя панель принадлежит этой же версии.
    [Fact]
    public void RewriteControllerHwVersion_RenamesOwnHmiFolder_AndRepointsHmiPath()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var tmpRoot = new TempRoot();
        var svc = new HierarchyService(db);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var ctrl = db.GetAllControllerModels().First(c => c.Name == "SMH4");

        var oldRaw = "2.1.0044.0001.20260101_1200";
        var ctrlDir = Path.Combine(tmpRoot.Path, "ПО", "НГР", "КНС", "SMH4");
        var oldDir = Path.Combine(ctrlDir, oldRaw);
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "fw.psl"), "test");

        // Папка панели — сосед папки версии, в HMI-папке контроллера, названа по старой версии.
        var oldHmiDir = Path.Combine(ctrlDir, "HMI", $"{oldRaw}_hmi");
        Directory.CreateDirectory(oldHmiDir);
        File.WriteAllText(Path.Combine(oldHmiDir, "panel.dpj"), "hmi");

        var fwId = db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = ctrl.Id!.Value,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = 44, SwVersion = 1, DtStr = "20260101_1200",
            VersionRaw = oldRaw, Filename = "fw.psl", DiskPath = oldDir, HmiPath = oldHmiDir,
            Description = "test", Status = "active",
        });

        var res = svc.RewriteControllerHwVersion(tmpRoot.Path, ctrl.Id!.Value, 44, 1321);
        Assert.True(res.Ok, string.Join("; ", res.Errors));

        var newRaw = "2.1.1321.0001.20260101_1200";
        var newHmiDir = Path.Combine(ctrlDir, "HMI", $"{newRaw}_hmi");
        Assert.False(Directory.Exists(oldHmiDir));
        Assert.True(Directory.Exists(newHmiDir));
        Assert.True(File.Exists(Path.Combine(newHmiDir, "panel.dpj")));
        Assert.Equal(newHmiDir, db.GetFwVersionById(fwId)!.HmiPath);
    }

    // Панель, унаследованную от ДРУГОЙ версии (её имя — про старую версию, не про правимую), трогать
    // нельзя: пометка «HMI от версии X» на карточке верна, и переименование сломало бы её у той версии.
    [Fact]
    public void RewriteControllerHwVersion_LeavesInheritedHmiFolderUntouched()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var tmpRoot = new TempRoot();
        var svc = new HierarchyService(db);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var ctrl = db.GetAllControllerModels().First(c => c.Name == "SMH4");

        var ownRaw = "2.1.0044.0002";
        var ctrlDir = Path.Combine(tmpRoot.Path, "ПО", "НГР", "КНС", "SMH4");
        var ownDir = Path.Combine(ctrlDir, ownRaw);
        Directory.CreateDirectory(ownDir);
        File.WriteAllText(Path.Combine(ownDir, "fw.psl"), "test");

        // Панель осталась от более ранней версии 0001 — её имя не про правимую версию 0002.
        var inheritedHmiDir = Path.Combine(ctrlDir, "HMI", "2.1.0044.0001_hmi");
        Directory.CreateDirectory(inheritedHmiDir);
        File.WriteAllText(Path.Combine(inheritedHmiDir, "panel.dpj"), "hmi");

        var fwId = db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = ctrl.Id!.Value,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = 44, SwVersion = 2, DtStr = "",
            VersionRaw = ownRaw, Filename = "fw.psl", DiskPath = ownDir, HmiPath = inheritedHmiDir,
            Description = "test", Status = "active",
        });

        var res = svc.RewriteControllerHwVersion(tmpRoot.Path, ctrl.Id!.Value, 44, 1321);
        Assert.True(res.Ok, string.Join("; ", res.Errors));

        // Папка панели и hmi_path не изменились — она принадлежит версии 0001, а не правимой 0002.
        Assert.True(Directory.Exists(inheritedHmiDir));
        Assert.Equal(inheritedHmiDir, db.GetFwVersionById(fwId)!.HmiPath);
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

    // Прошивка коллеги: disk_path в БД записан с ЕГО формой шары (напр. UNC \\ant_srv\...), а у нас
    // та же папка лежит под ЛОКАЛЬНЫМ корнем. Правка hw обязана локализовать путь перед проверкой —
    // иначе Directory.Exists на чужом пути = ложь и версия падает «папка на диске недоступна»
    // (симптом «пиксель ищет, но при синхроне не находит папку с старым hw»).
    [Fact]
    public void RewriteControllerHwVersion_LocalizesForeignDiskPath_BeforeRenaming()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var tmpRoot = new TempRoot();
        var svc = new HierarchyService(db);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var ctrl = db.GetAllControllerModels().First(c => c.Name == "SMH4");

        var oldRaw = "2.1.0044.0005";
        // Папка реально лежит под нашим ЛОКАЛЬНЫМ корнем...
        var localDir = Path.Combine(tmpRoot.Path, "ПО", "НГР", "КНС", "SMH4", oldRaw);
        Directory.CreateDirectory(localDir);
        File.WriteAllText(Path.Combine(localDir, "fw.psl"), "test");
        // ...а в БД disk_path сохранён с ЧУЖИМ корнем коллеги (иначе смонтированная шара).
        var foreignDir = Path.Combine(@"\\ant_srv\Software\Antarus", "ПО", "НГР", "КНС", "SMH4", oldRaw);

        var fwId = db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = ctrl.Id!.Value,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = 44, SwVersion = 5, DtStr = "",
            VersionRaw = oldRaw, Filename = "fw.psl", DiskPath = foreignDir,
            Description = "test", Status = "active",
        });

        var res = svc.RewriteControllerHwVersion(tmpRoot.Path, ctrl.Id!.Value, 44, 1321);

        Assert.True(res.Ok, string.Join("; ", res.Errors));
        Assert.Equal(1, res.UpdatedRows);

        var newRaw = "2.1.1321.0005";
        var newDir = Path.Combine(tmpRoot.Path, "ПО", "НГР", "КНС", "SMH4", newRaw);
        Assert.False(Directory.Exists(localDir));
        Assert.True(Directory.Exists(newDir));
        Assert.True(File.Exists(Path.Combine(newDir, "fw.psl")));

        var row = db.GetFwVersionById(fwId)!;
        Assert.Equal(1321, row.HwVersion);
        Assert.Equal(newRaw, row.VersionRaw);
        Assert.Equal(newDir, row.DiskPath); // перезаписан в локальной форме
    }

    // То же для панели HMI, пришедшей с чужой формой пути: локализуем и переименовываем «свою» папку.
    [Fact]
    public void RewriteControllerHwVersion_LocalizesForeignHmiPath_BeforeRenaming()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var tmpRoot = new TempRoot();
        var svc = new HierarchyService(db);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var ctrl = db.GetAllControllerModels().First(c => c.Name == "SMH4");

        var oldRaw = "2.1.0044.0005";
        var ctrlDir = Path.Combine(tmpRoot.Path, "ПО", "НГР", "КНС", "SMH4");
        var localDir = Path.Combine(ctrlDir, oldRaw);
        Directory.CreateDirectory(localDir);
        File.WriteAllText(Path.Combine(localDir, "fw.psl"), "test");
        var localHmiDir = Path.Combine(ctrlDir, "HMI", $"{oldRaw}_hmi");
        Directory.CreateDirectory(localHmiDir);
        File.WriteAllText(Path.Combine(localHmiDir, "panel.dpj"), "hmi");

        // В БД оба пути — с чужим корнем.
        var foreignRoot = @"\\ant_srv\Software\Antarus";
        var foreignDir = Path.Combine(foreignRoot, "ПО", "НГР", "КНС", "SMH4", oldRaw);
        var foreignHmiDir = Path.Combine(foreignRoot, "ПО", "НГР", "КНС", "SMH4", "HMI", $"{oldRaw}_hmi");

        var fwId = db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = ctrl.Id!.Value,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = 44, SwVersion = 5, DtStr = "",
            VersionRaw = oldRaw, Filename = "fw.psl", DiskPath = foreignDir, HmiPath = foreignHmiDir,
            Description = "test", Status = "active",
        });

        var res = svc.RewriteControllerHwVersion(tmpRoot.Path, ctrl.Id!.Value, 44, 1321);
        Assert.True(res.Ok, string.Join("; ", res.Errors));

        var newRaw = "2.1.1321.0005";
        var newHmiDir = Path.Combine(ctrlDir, "HMI", $"{newRaw}_hmi");
        Assert.False(Directory.Exists(localHmiDir));
        Assert.True(Directory.Exists(newHmiDir));
        Assert.True(File.Exists(Path.Combine(newHmiDir, "panel.dpj")));
        Assert.Equal(newHmiDir, db.GetFwVersionById(fwId)!.HmiPath); // локальная форма новой папки
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
