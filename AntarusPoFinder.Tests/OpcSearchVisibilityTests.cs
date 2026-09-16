using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ОПЦ-версия и обычная живут в выдаче рядом.
///
/// Жалобы Ильи 16.09.2026: «ОПЦ почему-то перезаписали стандартную прошивку и индексирует её как
/// текущую, хотя она ОПЦ» и «плюс она в поиске не находится, только во вкладке прошивок видна».
///
/// Причина у обеих одна: выдача схлопывалась к одной строке на «подтип + контроллер». Придумано это
/// для обычной линейки — свежая версия заменяет прежнюю, и показывать обе незачем. У ОПЦ смысл
/// обратный: это разовая сборка под конкретный шкаф, она ничего не заменяет. Из двух строк
/// оставалась одна, а какая именно — зависело от очков и порядка обхода; отсюда и «перезаписала»,
/// и «не находится» — в разных случаях выживала разная.</summary>
public class OpcSearchVisibilityTests
{
    private static int AddVersion(Database db, int sw, bool opc, string tags = "")
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");
        return db.AddFwVersion(new FwVersionRecord
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
            LaunchTypes = new List<string> { "ПЧ" },
            Tags = tags,
            Status = "active",
            IsOpc = opc,
            RequestNum = opc ? "З-1234" : "",
            CabinetSn = opc ? "SN-77" : "",
        });
    }

    [Fact]
    public void OpcVersion_AndTheStandardOne_BothStayInResults()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var standard = AddVersion(db, sw: 1, opc: false);
        var opc = AddVersion(db, sw: 2, opc: true);

        var found = db.SearchFwVersions(new[] { "КНС" });
        var ids = found.Select(f => f.Row.Id).ToList();

        Assert.Contains(standard, ids);
        Assert.Contains(opc, ids);
    }

    /// <summary>Двух ОПЦ под разные шкафы тоже должно быть видно две: у каждой свой серийный номер,
    /// и наладчику нужна именно «его».</summary>
    [Fact]
    public void TwoOpcVersions_ForDifferentCabinets_AreBothListed()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var first = AddVersion(db, sw: 2, opc: true);
        var second = AddVersion(db, sw: 3, opc: true);

        var ids = db.SearchFwVersions(new[] { "КНС" }).Select(f => f.Row.Id).ToList();
        Assert.Contains(first, ids);
        Assert.Contains(second, ids);
    }

    /// <summary>ОПЦ не становится «текущей» версией шкафа. Она собирается под один конкретный шкаф
    /// и линейку не продолжает — а пока считалась продолжением, свежая ОПЦ с бо́льшим номером
    /// подменяла собой обычную прошивку везде, где спрашивается «что сейчас актуально».</summary>
    [Fact]
    public void OpcVersion_DoesNotBecomeTheCurrentOneForTheCabinet()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var standard = AddVersion(db, sw: 1, opc: false);
        AddVersion(db, sw: 9, opc: true);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");

        var current = db.GetLastActiveFwVersion(subtype.Id!.Value, mod.ControllerId, mod.HwVersion);
        Assert.Equal(standard, current?.Id);
    }

    /// <summary>А обычная линейка как схлопывалась, так и схлопывается: две обычных версии одного
    /// шкафа — это старая и новая, и показывать обе по-прежнему незачем.</summary>
    [Fact]
    public void PlainVersions_StillCollapseToTheLatest()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        AddVersion(db, sw: 1, opc: false);
        var newer = AddVersion(db, sw: 2, opc: false);

        var found = db.SearchFwVersions(new[] { "КНС" });
        Assert.Equal(newer, Assert.Single(found).Row.Id);
    }
}
