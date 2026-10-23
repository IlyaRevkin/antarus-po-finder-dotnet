using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Выключение версии — третье состояние прошивки рядом с «активна» и «откатана»
/// (см. Core/Services/FwStatus.cs). Заказ дословно: «помимо откатной логики сделать выключение…
/// во-вторых отключение, опять же залили не то случайно раздвоили прошивку… в-третьих логика
/// включения намеренно сделали 3 копии прошивки под разные исполнения и их можно включать и
/// выключать. Потому что если откатывать то пока все версии не откатить начиная от последней до
/// первой… включить допустим новую версию с каким-то исполнением и оставить только её не получится».
///
/// Отсюда и проверки ниже: выключить можно ЛЮБУЮ версию по отдельности, номер при этом не
/// освобождается (в отличие от отката), выключенная не выдаётся и не считается текущей, а «оставить
/// только эту» гасит соседей, не трогая откатанных.</summary>
public class FwDisableTests
{
    private static (Database Db, TempDb File, EquipmentGroup Group, EquipmentSubType Subtype, ControllerModification Mod) New()
    {
        var file = new TempDb();
        var db = new Database(file.Path);
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");
        return (db, file, group, subtype, mod);
    }

    private static int Seed(Database db, EquipmentGroup group, EquipmentSubType subtype, ControllerModification mod,
        int sw, string status = FwStatus.Active, string execution = "") =>
        db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion,
            SwVersion = sw,
            DtStr = $"2026010{sw}_0000",
            VersionRaw = $"2.1.001.000{sw}.2026010{sw}_0000",
            Filename = "fw.psl",
            DiskPath = "",
            Description = "test",
            Status = status,
            Execution = execution,
        });

    [Fact]
    public void Выключенная_версия_не_выдаётся_и_включается_обратно()
    {
        var (db, file, group, subtype, mod) = New();
        using var _ = file;
        using (db)
        {
            var id = Seed(db, group, subtype, mod, 1);

            Assert.True(db.DisableFwVersion(id));
            Assert.Equal(FwStatus.Disabled, db.GetFwVersionById(id)!.Status);
            // Та же выборка, по которой идёт обычная выдача: выключенной в ней быть не должно.
            Assert.DoesNotContain(db.GetFwVersions(subtype.Id!.Value, mod.ControllerId), v => v.Id == id);

            Assert.True(db.EnableFwVersion(id));
            Assert.Equal(FwStatus.Active, db.GetFwVersionById(id)!.Status);
            Assert.Contains(db.GetFwVersions(subtype.Id!.Value, mod.ControllerId), v => v.Id == id);
        }
    }

    [Fact]
    public void Выключить_можно_любую_версию_по_отдельности_не_трогая_соседние()
    {
        // Ровно то, чего не умел откат: там пришлось бы идти «от последней до первой».
        var (db, file, group, subtype, mod) = New();
        using var _ = file;
        using (db)
        {
            var старая = Seed(db, group, subtype, mod, 1);
            var средняя = Seed(db, group, subtype, mod, 2);
            var новая = Seed(db, group, subtype, mod, 3);

            Assert.True(db.DisableFwVersion(средняя));

            Assert.Equal(FwStatus.Active, db.GetFwVersionById(старая)!.Status);
            Assert.Equal(FwStatus.Active, db.GetFwVersionById(новая)!.Status);
        }
    }

    [Fact]
    public void Выключение_не_освобождает_номер_и_не_путается_с_откатом()
    {
        var (db, file, group, subtype, mod) = New();
        using var _ = file;
        using (db)
        {
            var id = Seed(db, group, subtype, mod, 1);
            db.DisableFwVersion(id);

            // Номер остаётся занятым: следующая загрузка не должна получить тот же SW заново —
            // этим выключение и отличается от отката (там номер освобождается намеренно).
            Assert.Equal(2, db.GetNextSwVersion(subtype.Id!.Value, mod.ControllerId, mod.HwVersion));

            // «Включить» работает только для выключенной: возврат откатанной — отдельное действие
            // со своим предупреждением про папку «_ОТКАТАНО».
            var другая = Seed(db, group, subtype, mod, 2, status: FwStatus.RolledBack);
            Assert.False(db.EnableFwVersion(другая));
            Assert.False(db.DisableFwVersion(другая));
            Assert.Equal(FwStatus.RolledBack, db.GetFwVersionById(другая)!.Status);
        }
    }

    [Fact]
    public void Оставить_только_эту_гасит_соседей_включая_другие_исполнения()
    {
        var (db, file, group, subtype, mod) = New();
        using var _ = file;
        using (db)
        {
            var трёхнасосная = Seed(db, group, subtype, mod, 1, execution: "3 насоса");
            var данфосс = Seed(db, group, subtype, mod, 2, execution: "ПЧ Danfoss");
            var новая = Seed(db, group, subtype, mod, 3, execution: "2 насоса");
            var откатанная = Seed(db, group, subtype, mod, 4, status: FwStatus.RolledBack);

            var выключено = db.KeepOnlyFwVersionActive(новая);

            Assert.Equal(2, выключено);
            Assert.Equal(FwStatus.Disabled, db.GetFwVersionById(трёхнасосная)!.Status);
            Assert.Equal(FwStatus.Disabled, db.GetFwVersionById(данфосс)!.Status);
            Assert.Equal(FwStatus.Active, db.GetFwVersionById(новая)!.Status);
            // Откат — решение другого порядка, снимать его мимо кнопки «Вернуть в активные» нельзя.
            Assert.Equal(FwStatus.RolledBack, db.GetFwVersionById(откатанная)!.Status);
            // И выбранная становится текущей на вид, а не остаётся «Заменена» рядом с выключенными.
            Assert.True(db.GetFwVersionById(новая)!.ManualCurrent);
        }
    }

    [Fact]
    public void Выключенная_не_считается_текущей_в_истории()
    {
        var versions = new[]
        {
            new FwVersionRecord { HwVersion = 1, SwVersion = 3, Status = FwStatus.Disabled },
            new FwVersionRecord { HwVersion = 1, SwVersion = 2, Status = FwStatus.Active },
            new FwVersionRecord { HwVersion = 1, SwVersion = 1, Status = FwStatus.Active },
        };

        var labels = FwHistoryStatus.Labels(versions);

        Assert.Equal(new[] { FwHistoryStatus.Disabled, FwHistoryStatus.Current, FwHistoryStatus.Superseded }, labels);
    }

    [Fact]
    public void Ручная_отметка_текущей_на_выключенной_версии_не_ставится()
    {
        var (db, file, group, subtype, mod) = New();
        using var _ = file;
        using (db)
        {
            var id = Seed(db, group, subtype, mod, 1);
            db.SetFwVersionManualCurrent(id);
            db.DisableFwVersion(id);

            // Отметка снята выключением — иначе она молча ожила бы при включении обратно.
            Assert.False(db.GetFwVersionById(id)!.ManualCurrent);
            Assert.False(db.SetFwVersionManualCurrent(id));
        }
    }
}
