using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Правки в «Истории версий» (сменить контроллер, задать счётчик обращений, откат конкретной
/// версии) и уборка осиротевших ярлыков прошивок, когда файлы удалили прямо на диске мимо программы
/// (жалоба «корневой файл исчез, а ярлык не исчезает»).</summary>
public class HistoryEditAndShortcutPruneTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public HistoryEditAndShortcutPruneTests()
    {
        _db = new Database(_dbFile.Path);
        _hierarchy = new HierarchyService(_db);
        _hierarchy.EnsureStructure(Root);
    }

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
        _tempRoot.Dispose();
    }

    private int AddVersion(int subtypeId, int controllerId, string diskPath, int sw = 1, string status = "active")
    {
        var group = _db.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Id == subtypeId);
        var mod = _db.GetAllModifications().First(m => m.ControllerId == controllerId);
        return _db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtypeId, ControllerId = controllerId,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix, HwVersion = mod.HwVersion, SwVersion = sw,
            DtStr = $"2026010{sw}_0000", VersionRaw = $"2.1.001.000{sw}.2026010{sw}_0000",
            Filename = "fw.psl", DiskPath = diskPath, Description = "test", Status = status,
        });
    }

    // ── Счётчик обращений (кол-во тыков) ──────────────────────────────────────

    [Fact]
    public void SetLocalFwUsageVersionTotal_AboveRealUsage_KeepsPerQueryStatsAndTopsUpWithManual()
    {
        var group = _db.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).First();
        var mod = _db.GetAllModifications().First(m => m.ControllerName == "SMH5");
        var id = AddVersion(subtype.Id!.Value, mod.ControllerId, diskPath: "");

        var key = SearchService.UsageKey("ПЖ");
        for (var i = 0; i < 3; i++) _db.RecordFwUsage(key, id);
        Assert.Equal(3, _db.GetLocalFwUsageTotal(id));

        _db.SetLocalFwUsageVersionTotal(id, 10);

        Assert.Equal(10, _db.GetLocalFwUsageTotal(id));
        Assert.Equal(10, _db.GetFwUsageTotal(id));
        // Настоящая по-запросная строка уцелела (разницу добрала служебная строка ручной правки).
        Assert.Equal(3, _db.GetFwUsageForQuery(key)[id]);
        // Служебная строка не выдаётся как «запрос» в редакторе веса.
        Assert.DoesNotContain(_db.GetFwUsageQueriesForVersion(id), q => q.QueryKey == Database.ManualUsageKey);
    }

    [Fact]
    public void SetLocalFwUsageVersionTotal_BelowRealUsage_DropsPerQueryRowsAndPinsToNewTotal()
    {
        var group = _db.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).First();
        var mod = _db.GetAllModifications().First(m => m.ControllerName == "SMH5");
        var id = AddVersion(subtype.Id!.Value, mod.ControllerId, diskPath: "");

        var key = SearchService.UsageKey("ПЖ");
        for (var i = 0; i < 5; i++) _db.RecordFwUsage(key, id);

        _db.SetLocalFwUsageVersionTotal(id, 1);
        Assert.Equal(1, _db.GetLocalFwUsageTotal(id));
        Assert.False(_db.GetFwUsageForQuery(key).ContainsKey(id));

        // Обнуление убирает и служебную строку — счётчик ровно ноль.
        _db.SetLocalFwUsageVersionTotal(id, 0);
        Assert.Equal(0, _db.GetLocalFwUsageTotal(id));
        Assert.Equal(0, _db.GetFwUsageTotal(id));
    }

    // ── Смена контроллера ─────────────────────────────────────────────────────

    [Fact]
    public void ReassignFwVersionController_MovesRecordAndClearsManualCurrent()
    {
        var group = _db.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).First();
        var from = _db.GetAllModifications().First(m => m.ControllerName == "SMH4");
        var toCtrl = _db.GetAllControllerModels().First(c => c.Name == "SMH5");
        var id = AddVersion(subtype.Id!.Value, from.ControllerId, diskPath: "");
        _db.SetFwVersionManualCurrent(id);
        Assert.True(_db.GetFwVersionById(id)!.ManualCurrent);

        Assert.True(_db.ReassignFwVersionController(id, toCtrl.Id!.Value));

        var row = _db.GetFwVersionById(id)!;
        Assert.Equal(toCtrl.Id!.Value, row.ControllerId);
        Assert.False(row.ManualCurrent);

        // Тот же контроллер / несуществующая версия — ничего не делаем.
        Assert.False(_db.ReassignFwVersionController(id, toCtrl.Id!.Value));
        Assert.False(_db.ReassignFwVersionController(999999, toCtrl.Id!.Value));
    }

    // ── Уборка осиротевших ярлыков ────────────────────────────────────────────

    [Fact]
    public void PruneOrphanedFirmwareShortcuts_RemovesLinkWhenFilesGone_ButKeepsItWhileFilesExist()
    {
        var group = _db.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        var subtypes = _db.GetSubtypesForGroup(group.Id!.Value);
        var primary = subtypes[0];
        var extra = subtypes[1];
        var mod = _db.GetAllModifications().First(m => m.ControllerName == "SMH5");

        // Настоящие файлы версии лежат в папке контроллера ОСНОВНОГО подтипа.
        var versionRaw = "2.1.001.0001.20260101_0000";
        var diskPath = _hierarchy.FwPath(Root, group.Name, primary.Name, mod.ControllerName, versionRaw);
        Directory.CreateDirectory(diskPath);
        File.WriteAllText(Path.Combine(diskPath, "fw.psl"), "firmware");

        // Запись основного подтипа (в его папке — настоящая версия, не ярлык) и запись-ярлык доп.подтипа.
        AddVersion(primary.Id!.Value, mod.ControllerId, diskPath);
        AddVersion(extra.Id!.Value, mod.ControllerId, diskPath, sw: 1);

        // Кладём на диск сам файл ярлыка доп.подтипа — именно его должен убрать обход.
        var extraCtrlFolder = _hierarchy.ControllerFolder(Root, group.Name, extra.Name, mod.ControllerName);
        Directory.CreateDirectory(extraCtrlFolder);
        var linkPath = Path.Combine(extraCtrlFolder, $"{versionRaw}.lnk");
        File.WriteAllText(linkPath, "shortcut");

        // Файлы на месте — ярлык живой, обход его не трогает.
        var keep = _hierarchy.PruneOrphanedFirmwareShortcuts(Root);
        Assert.Equal(0, keep.Removed);
        Assert.True(File.Exists(linkPath));

        // «Корневой файл исчез» — удаляем папку версии прямо на диске мимо программы.
        Directory.Delete(diskPath, recursive: true);

        var pruned = _hierarchy.PruneOrphanedFirmwareShortcuts(Root);
        Assert.Equal(1, pruned.Removed);
        Assert.Empty(pruned.Errors);
        Assert.False(File.Exists(linkPath));
    }

    [Fact]
    public void PruneOrphanedFirmwareShortcuts_UnavailableRoot_DoesNothing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "antarus_no_such_root_" + Guid.NewGuid().ToString("N"));
        var result = _hierarchy.PruneOrphanedFirmwareShortcuts(missing);
        Assert.Equal(0, result.Removed);
        Assert.Empty(result.Errors);
    }
}
