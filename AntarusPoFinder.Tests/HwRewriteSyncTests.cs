using System.IO;
using System.Linq;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Синхронизация «переписывания hw модификации» (напр. PIXEL2/SMH4 044 → 1321) как ЯВНОЙ
/// операции-переименования, а не обычного диффа строк fw_versions. Жалоба: оператор правит hw у себя и
/// отправляет на сервер — hw зашит в version_raw (натуральный ключ синхронизации), поэтому у коллег
/// старая строка (044) остаётся фантомом «нет папки на диске», а новая (1321) вставляется дублем.
/// Механизм: hw_rewrite_log + ExportedHwRewrite едут в общем конфиге, ConfigSyncService.ReplayHwRewrites
/// проигрывает их ДО импорта fw_versions, переименовывая свою строку 044 → 1321 на месте.</summary>
public class HwRewriteSyncTests
{
    private static string OldRaw => "2.1.0044.0001.20260101_1200";
    private static string NewRaw => "2.1.1321.0001.20260101_1200";

    private static int SeedFwAtHw44(Database db, HierarchyService hier, string root, out int controllerId, out string oldDir)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var ctrl = db.GetAllControllerModels().First(c => c.Name == "SMH4");
        controllerId = ctrl.Id!.Value;

        oldDir = Path.Combine(root, "ПО", "НГР", "КНС", "SMH4", OldRaw);
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "fw.psl"), "test");

        return db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = ctrl.Id!.Value,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = 44, SwVersion = 1, DtStr = "20260101_1200",
            VersionRaw = OldRaw, Filename = "fw.psl", DiskPath = oldDir,
            Description = "test", Status = "active",
        });
    }

    /// <summary>Главный сценарий: A и B оба знают прошивку с hw44, A переписывает hw44 → hw1321 и
    /// отправляет — у B строка ПЕРЕИМЕНОВЫВАЕТСЯ (одна строка hw1321), а не появляется дубль + фантом.</summary>
    [Fact]
    public void HwRewrite_PropagatesAsRename_NoDuplicateOrPhantomOnColleague()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        // A заводит прошивку hw44 и раздаёт её B через обычную синхронизацию.
        SeedFwAtHw44(m.DbA, m.HierA, root, out var ctrlA, out var oldDir);
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        var first = ConfigSyncService.CheckForUpdate(m.SvcB, out var e1);
        Assert.True(e1 is null, e1);
        Assert.NotNull(first);
        ConfigSyncService.Apply(m.SvcB, first!.ConfigPath, root);

        var ctrlB = m.DbB.GetAllControllerModels().First(c => c.Name == "SMH4").Id!.Value;
        Assert.Single(m.DbB.GetFwVersionsByControllerAndHw(ctrlB, 44));

        // Оператор на A переписывает hw44 → hw1321 (переименовывает папку на общей шаре) и фиксирует
        // это событие ровно так, как делает call-site в SettingsView.
        var res = m.HierA.RewriteControllerHwVersion(root, ctrlA, 44, 1321);
        Assert.True(res.Ok, string.Join("; ", res.Errors));
        Assert.Equal(1, res.UpdatedRows);
        Assert.False(Directory.Exists(oldDir));

        var ts = Database.NowIsoPreciseTs();
        m.DbA.RecordHwRewrite(m.DbA.GetControllerSyncId(ctrlA), "SMH4", 44, 1321, ts, "profileA");
        m.CfgA.SetHwRewriteAppliedAt(ts);

        // A отправляет снова, B забирает.
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        var second = ConfigSyncService.CheckForUpdate(m.SvcB, out var e2);
        Assert.True(e2 is null, e2);
        Assert.NotNull(second);
        ConfigSyncService.Apply(m.SvcB, second!.ConfigPath, root);

        // Ключевые проверки: у B НЕТ фантома hw44 и НЕТ дубля — ровно одна строка hw1321.
        Assert.Empty(m.DbB.GetFwVersionsByControllerAndHw(ctrlB, 44));
        var migrated = m.DbB.GetFwVersionsByControllerAndHw(ctrlB, 1321);
        Assert.Single(migrated);
        Assert.Equal(NewRaw, migrated[0].VersionRaw);
        Assert.True(Directory.Exists(migrated[0].DiskPath), $"папка новой версии должна существовать: {migrated[0].DiskPath}");

        // Watermark у B продвинулся — повторная синхронизация того же события ничего не проиграет.
        Assert.Equal(ts, m.CfgB.HwRewriteAppliedAt());
    }

    /// <summary>Толерантность проигрывания к уже переименованной папке: у коллеги папка версии на общей
    /// шаре уже 1321 (её переименовал автор правки), локальная строка ещё hw44 — проигрывание должно
    /// перецелить строку БД на новую папку, не считая «старой папки нет» ошибкой и не плодя дубля.</summary>
    [Fact]
    public void ReplayControllerHwRewrite_OldFolderAlreadyRenamedOnShare_RepointsRowOnly()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var tmpRoot = new TempRoot();
        var svc = new HierarchyService(db);

        SeedFwAtHw44(db, svc, tmpRoot.Path, out var ctrl, out var oldDir);

        // Имитируем: автор правки уже переименовал папку на общей шаре в 1321, старой больше нет.
        var newDir = Path.Combine(tmpRoot.Path, "ПО", "НГР", "КНС", "SMH4", NewRaw);
        Directory.Move(oldDir, newDir);

        var res = svc.ReplayControllerHwRewrite(tmpRoot.Path, ctrl, 44, 1321);
        Assert.True(res.Ok, string.Join("; ", res.Errors));
        Assert.Equal(1, res.UpdatedRows);

        Assert.Empty(db.GetFwVersionsByControllerAndHw(ctrl, 44));
        var row = Assert.Single(db.GetFwVersionsByControllerAndHw(ctrl, 1321));
        Assert.Equal(NewRaw, row.VersionRaw);
        Assert.Equal(newDir, row.DiskPath);
        Assert.True(Directory.Exists(newDir));

        // Идемпотентность: повторное проигрывание — пустая операция, строка не задваивается.
        var again = svc.ReplayControllerHwRewrite(tmpRoot.Path, ctrl, 44, 1321);
        Assert.Equal(0, again.UpdatedRows);
        Assert.Single(db.GetFwVersionsByControllerAndHw(ctrl, 1321));
    }

    /// <summary>Если строка с целевым ключом hw1321 уже есть (напр. дубль от старой версии приложения),
    /// проигрывание НЕ переименовывает старую hw44 в него — иначе два ряда с одним version_raw.</summary>
    [Fact]
    public void ReplayControllerHwRewrite_TargetKeyAlreadyExists_LeavesOldRowUntouched()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var tmpRoot = new TempRoot();
        var svc = new HierarchyService(db);

        SeedFwAtHw44(db, svc, tmpRoot.Path, out var ctrl, out var oldDir);

        // Уже существующая строка hw1321 с тем же натуральным ключом (подтип+контроллер+version_raw).
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var newDir = Path.Combine(tmpRoot.Path, "ПО", "НГР", "КНС", "SMH4", NewRaw);
        Directory.CreateDirectory(newDir);
        db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = ctrl,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = 1321, SwVersion = 1, DtStr = "20260101_1200",
            VersionRaw = NewRaw, Filename = "fw.psl", DiskPath = newDir,
            Description = "already here", Status = "active",
        });

        var res = svc.ReplayControllerHwRewrite(tmpRoot.Path, ctrl, 44, 1321);

        // Старая строка hw44 не тронута (коллизии ключа не создали), целевая по-прежнему одна.
        Assert.Equal(0, res.UpdatedRows);
        Assert.Single(db.GetFwVersionsByControllerAndHw(ctrl, 44));
        Assert.Single(db.GetFwVersionsByControllerAndHw(ctrl, 1321));
    }
}
