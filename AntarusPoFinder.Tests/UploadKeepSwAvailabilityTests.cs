using System.Linq;
using AntarusPoFinder.App.Views;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>«Не увеличивать версию ПО (sw)» (UploadView) должна быть доступна только когда для
/// выбранного подтип/контроллер/HW уже есть хотя бы одна активная версия — иначе увеличивать попросту
/// нечего, а галочка раньше была доступна всегда. Сама проверка вынесена в
/// UploadView.KeepSwVersionAvailable — internal static, чистая функция от Database + выбранных
/// подтипа/контроллера, проверяется здесь напрямую без поднятия самого WPF-контрола.</summary>
public class UploadKeepSwAvailabilityTests
{
    private static (EquipmentSubType subtype, ControllerModification mod) SeedNgrKns(Database db)
    {
        var group = db.GetAllEquipmentGroups().Single(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).Single(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");
        return (subtype, mod);
    }

    [Fact]
    public void Unavailable_WhenSubtypeOrControllerNotSelected()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var (subtype, mod) = SeedNgrKns(db);

        Assert.False(UploadView.KeepSwVersionAvailable(db, null, mod));
        Assert.False(UploadView.KeepSwVersionAvailable(db, subtype, null));
        Assert.False(UploadView.KeepSwVersionAvailable(db, null, null));
    }

    [Fact]
    public void Unavailable_WhenCombinationHasNoUploadedVersionsYet()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var (subtype, mod) = SeedNgrKns(db);

        Assert.False(UploadView.KeepSwVersionAvailable(db, subtype, mod));
    }

    [Fact]
    public void Available_AfterAtLeastOneVersionExistsForTheSameCombination()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var group = db.GetAllEquipmentGroups().Single(g => g.Name == "НГР");
        var (subtype, mod) = SeedNgrKns(db);

        db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion,
            SwVersion = 1,
            DtStr = "20260101_0000",
            VersionRaw = "2.1.001.0001.20260101_0000",
            Filename = "fw.psl",
            LaunchTypes = new() { "ПЧ" },
            Status = "active",
        });

        Assert.True(UploadView.KeepSwVersionAvailable(db, subtype, mod));
    }

    /// <summary>Версия есть, но для ДРУГОГО HW того же подтипа/контроллера — доступность не должна
    /// «протекать» между HW-версиями одного и того же контроллера: «не увеличивать» относится ровно к
    /// той тройке, которую видит GetNextSwVersion/GetLastActiveFwVersion при самой загрузке.</summary>
    [Fact]
    public void Unavailable_WhenExistingVersionIsForADifferentHwVersion()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var group = db.GetAllEquipmentGroups().Single(g => g.Name == "НГР");
        var (subtype, mod) = SeedNgrKns(db);

        db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion + 1000, // заведомо другой HW, отдельно от реальных модификаций
            SwVersion = 1,
            DtStr = "20260101_0000",
            VersionRaw = "2.1.001.0001.20260101_0000",
            Filename = "fw.psl",
            LaunchTypes = new() { "ПЧ" },
            Status = "active",
        });

        Assert.False(UploadView.KeepSwVersionAvailable(db, subtype, mod));
    }

    /// <summary>Откатанная (rolled_back) версия не в счёт — GetLastActiveFwVersion её и так не видит
    /// (см. её собственный WHERE), «не увеличивать» относительно откатанной версии не имеет смысла.</summary>
    [Fact]
    public void Unavailable_WhenTheOnlyExistingVersionWasRolledBack()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var group = db.GetAllEquipmentGroups().Single(g => g.Name == "НГР");
        var (subtype, mod) = SeedNgrKns(db);

        var id = db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion,
            SwVersion = 1,
            DtStr = "20260101_0000",
            VersionRaw = "2.1.001.0001.20260101_0000",
            Filename = "fw.psl",
            LaunchTypes = new() { "ПЧ" },
            Status = "active",
        });
        db.RollbackFwVersion(id);

        Assert.False(UploadView.KeepSwVersionAvailable(db, subtype, mod));
    }
}
