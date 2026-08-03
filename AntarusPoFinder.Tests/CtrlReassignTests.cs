using System.IO;
using System.Linq;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Перенос версии прошивки на другую модель контроллера двигает и папку на диске, а не
/// только запись. Жалоба: раньше Database.ReassignFwVersionController правил одну лишь строку, папка
/// оставалась под старым контроллером, и ближайший досмотр диска (SyncFwFromDisk) заводил её
/// ОТДЕЛЬНОЙ записью — фантом, который тут же вставал в очередь модерации. Сделано в манере
/// HierarchyService.RewriteHw: устойчиво к недоступной шаре, идемпотентно, с журналом события
/// (ctrl_reassign_log / ExportedCtrlReassign), чтобы у коллег это выглядело переносом, а не
/// «удалили + завели заново».</summary>
public class CtrlReassignTests
{
    private const string Raw = "2.1.0044.0001.20260101_1200";

    private static (int FwId, int FromCtrl, int ToCtrl, string OldDir) Seed(Database db, string root, bool withHmi = false)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var from = db.GetAllControllerModels().First(c => c.Name == "SMH4");
        var to = db.GetAllControllerModels().First(c => c.Name == "SMH5");

        var oldDir = Path.Combine(root, "ПО", "НГР", "КНС", "SMH4", Raw);
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "fw.psl"), "test");

        var hmiDir = "";
        if (withHmi)
        {
            hmiDir = Path.Combine(root, "ПО", "НГР", "КНС", "SMH4", "HMI", $"{Raw}_hmi");
            Directory.CreateDirectory(hmiDir);
            File.WriteAllText(Path.Combine(hmiDir, "panel.fsprj"), "hmi");
        }

        var id = db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = from.Id!.Value,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = 44, SwVersion = 1, DtStr = "20260101_1200",
            VersionRaw = Raw, Filename = "fw.psl", DiskPath = oldDir, HmiPath = hmiDir,
            Description = "test", Status = "active",
        });
        return (id, from.Id!.Value, to.Id!.Value, oldDir);
    }

    /// <summary>Папка переезжает под нового контроллера, запись и disk_path согласованы, «своя» папка
    /// панели едет следом.</summary>
    [Fact]
    public void Reassign_MovesFolderAndKeepsRecordConsistent()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();
        var hier = new HierarchyService(db);

        var (fwId, _, toCtrl, oldDir) = Seed(db, root.Path, withHmi: true);

        var res = hier.ReassignFwVersionToController(root.Path, fwId, toCtrl);
        Assert.True(res.Ok, string.Join("; ", res.Errors));
        Assert.True(res.Moved);

        var newDir = Path.Combine(root.Path, "ПО", "НГР", "КНС", "SMH5", Raw);
        Assert.False(Directory.Exists(oldDir), "старая папка версии должна исчезнуть");
        Assert.True(Directory.Exists(newDir), "папка версии должна оказаться под новым контроллером");
        Assert.True(File.Exists(Path.Combine(newDir, "fw.psl")), "файлы должны переехать вместе с папкой");

        var row = db.GetFwVersionById(fwId)!;
        Assert.Equal(toCtrl, row.ControllerId);
        Assert.Equal(newDir, row.DiskPath);
        Assert.False(row.ManualCurrent);

        var newHmi = Path.Combine(root.Path, "ПО", "НГР", "КНС", "SMH5", "HMI", $"{Raw}_hmi");
        Assert.True(Directory.Exists(newHmi), "своя папка панели должна переехать под нового контроллера");
        Assert.Equal(newHmi, row.HmiPath);
    }

    /// <summary>Главная причина всей правки: после переноса досмотр диска НЕ заводит фантомную запись
    /// под старым контроллером (осиротевшей папки там больше нет).</summary>
    [Fact]
    public void Reassign_NoPhantomRecordAfterDiskScan()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();
        var hier = new HierarchyService(db);

        var (fwId, _, toCtrl, _) = Seed(db, root.Path);
        Assert.True(hier.ReassignFwVersionToController(root.Path, fwId, toCtrl).Ok);

        var scan = hier.SyncFwFromDisk(root.Path);
        Assert.Equal(0, scan.Added);
        Assert.Single(db.GetAllFwVersionsWithNames(includeArchived: true).Where(v => v.VersionRaw == Raw));
        // В очереди модерации ровно одна запись — сама перенесённая версия, а не она же плюс фантом.
        Assert.Equal(1, db.GetUnreleasedFwVersionsCount());
    }

    /// <summary>Шара недоступна — операция отменяется целиком: ни диск, ни база не тронуты, а причина
    /// названа словами. Без этого запись разъехалась бы с диском, когда шара вернётся.</summary>
    [Fact]
    public void Reassign_ShareUnavailable_LeavesDatabaseIntactAndExplains()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();
        var hier = new HierarchyService(db);

        var (fwId, fromCtrl, toCtrl, oldDir) = Seed(db, root.Path);

        var offline = Path.Combine(root.Path, "нет-такой-шары");
        var res = hier.ReassignFwVersionToController(offline, fwId, toCtrl);
        Assert.False(res.Ok);
        Assert.NotEmpty(res.Errors);

        var row = db.GetFwVersionById(fwId)!;
        Assert.Equal(fromCtrl, row.ControllerId);
        Assert.Equal(oldDir, row.DiskPath);

        // Папка на месте, но исчезла из-под записи (кто-то убрал её руками) — тоже отказ, а не
        // молчаливая правка одной лишь БД.
        Directory.Delete(oldDir, recursive: true);
        var gone = hier.ReassignFwVersionToController(root.Path, fwId, toCtrl);
        Assert.False(gone.Ok);
        Assert.NotEmpty(gone.Errors);
        Assert.Equal(fromCtrl, db.GetFwVersionById(fwId)!.ControllerId);
    }

    /// <summary>Повторный запуск ничего не ломает: контроллер уже новый — пустая операция без ошибок,
    /// строка и папка не задваиваются.</summary>
    [Fact]
    public void Reassign_RepeatedRun_IsNoOp()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();
        var hier = new HierarchyService(db);

        var (fwId, _, toCtrl, _) = Seed(db, root.Path);
        Assert.True(hier.ReassignFwVersionToController(root.Path, fwId, toCtrl).Ok);

        var again = hier.ReassignFwVersionToController(root.Path, fwId, toCtrl);
        Assert.False(again.Ok);
        Assert.Empty(again.Errors);
        Assert.Single(db.GetAllFwVersionsWithNames(includeArchived: true).Where(v => v.VersionRaw == Raw));
        Assert.True(Directory.Exists(Path.Combine(root.Path, "ПО", "НГР", "КНС", "SMH5", Raw)));

        // И проигрывание того же события повторно — тоже пустая операция.
        var replay = hier.ReassignFwVersionToController(root.Path, fwId, toCtrl, replay: true);
        Assert.False(replay.Ok);
        Assert.Single(db.GetAllFwVersionsWithNames(includeArchived: true).Where(v => v.VersionRaw == Raw));
    }

    /// <summary>Проигрывание чужого переноса: папку на общей шаре уже перенёс автор правки, локальная
    /// строка ещё под старым контроллером — перецеливаем запись, не считая «старой папки нет»
    /// ошибкой.</summary>
    [Fact]
    public void Replay_FolderAlreadyMovedOnShare_RepointsRowOnly()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();
        var hier = new HierarchyService(db);

        var (fwId, _, toCtrl, oldDir) = Seed(db, root.Path);
        var newDir = Path.Combine(root.Path, "ПО", "НГР", "КНС", "SMH5", Raw);
        Directory.CreateDirectory(Path.GetDirectoryName(newDir)!);
        Directory.Move(oldDir, newDir);

        var res = hier.ReassignFwVersionToController(root.Path, fwId, toCtrl, replay: true);
        Assert.True(res.Ok, string.Join("; ", res.Errors));
        Assert.False(res.Moved);

        var row = db.GetFwVersionById(fwId)!;
        Assert.Equal(toCtrl, row.ControllerId);
        Assert.Equal(newDir, row.DiskPath);
    }

    /// <summary>Сквозной сценарий двух машин: A переносит версию и отправляет снимок — у B это
    /// ПЕРЕНОС (одна строка под новым контроллером), а не фантом под старым плюс дубль под новым.</summary>
    [Fact]
    public void Reassign_PropagatesAsMove_NoDuplicateOrPhantomOnColleague()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var (fwId, fromCtrl, toCtrl, _) = Seed(m.DbA, root);
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        var first = ConfigSyncService.CheckForUpdate(m.SvcB, out var e1);
        Assert.True(e1 is null, e1);
        Assert.NotNull(first);
        ConfigSyncService.Apply(m.SvcB, first!.ConfigPath, root);

        var fromB = m.DbB.GetAllControllerModels().First(c => c.Name == "SMH4").Id!.Value;
        var toB = m.DbB.GetAllControllerModels().First(c => c.Name == "SMH5").Id!.Value;
        Assert.Single(m.DbB.GetFwVersions(null, fromB, includeArchived: true, includeRolledBack: true));

        // Оператор на A переносит версию и фиксирует событие ровно так, как это делает
        // HistoryDialog.ChangeController_Click.
        var subtypeId = m.DbA.GetFwVersionById(fwId)!.SubtypeId;
        var names = m.DbA.GetFwVersionNames(fwId)!.Value;
        Assert.True(m.HierA.ReassignFwVersionToController(root, fwId, toCtrl).Ok);
        var ts = Database.NowIsoPreciseTs();
        m.DbA.RecordCtrlReassign(new ExportedCtrlReassign
        {
            SubtypeSyncId = m.DbA.GetSubtypeSyncId(subtypeId),
            SubtypeName = names.SubtypeName, GroupName = names.GroupName,
            OldControllerSyncId = m.DbA.GetControllerSyncId(fromCtrl), OldControllerName = "SMH4",
            NewControllerSyncId = m.DbA.GetControllerSyncId(toCtrl), NewControllerName = "SMH5",
            VersionRaw = Raw, Ts = ts, Author = "profileA",
        });
        m.CfgA.SetCtrlReassignAppliedAt(ts);

        ConfigSyncService.Export(m.SvcA, root, "profileA");
        var second = ConfigSyncService.CheckForUpdate(m.SvcB, out var e2);
        Assert.True(e2 is null, e2);
        Assert.NotNull(second);
        ConfigSyncService.Apply(m.SvcB, second!.ConfigPath, root);

        Assert.Empty(m.DbB.GetFwVersions(null, fromB, includeArchived: true, includeRolledBack: true)
            .Where(v => v.VersionRaw == Raw));
        var migrated = m.DbB.GetFwVersions(null, toB, includeArchived: true, includeRolledBack: true)
            .Where(v => v.VersionRaw == Raw).ToList();
        Assert.Single(migrated);
        Assert.Equal(ts, m.CfgB.CtrlReassignAppliedAt());
    }
}
