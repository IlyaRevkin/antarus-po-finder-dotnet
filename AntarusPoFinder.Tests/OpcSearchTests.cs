using System;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ОПЦ в поисковой выдаче.
///
/// Жалоба Ильи, повторенная дважды: «ОПЦ так и не работает — я загрузил, и ОПЦ заменил стандарт в
/// поисковой выдаче, а по номеру заявки так и не нашлось».
///
/// Здесь сценарий целиком, от записей в базе до выдачи: обычная прошивка и ОПЦ того же шкафа рядом.
/// Проверять это глазами дорого (нужна загрузка на сетевой диск), а сломать — дёшево: обе беды
/// проявляются только тогда, когда ОПЦ уже выложена на объект.</summary>
public class OpcSearchTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly Database _db;

    private readonly int _subtypeId;
    private readonly int _controllerId;

    public OpcSearchTests()
    {
        _db = new Database(_dbFile.Path);

        var group = _db.GetAllEquipmentGroups().First(g => g.Name == "ТГР");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).First();
        var mod = _db.GetAllModifications().First(m => m.ControllerName == "SMH5");
        _subtypeId = subtype.Id!.Value;
        _controllerId = mod.ControllerId;

        // Обычная прошивка линейки.
        _db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = _subtypeId, ControllerId = _controllerId,
            HwVersion = 5, SwVersion = 3, VersionRaw = "3.0.005.0003",
            DtStr = "20260101_0000", Tags = "насосная",
        });

        // ОПЦ того же шкафа: разовая сборка под конкретную заявку.
        _db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = _subtypeId, ControllerId = _controllerId,
            HwVersion = 5, SwVersion = 4, VersionRaw = "3.0.005.0004",
            DtStr = "20260201_0000", IsOpc = true,
            RequestNum = "01312", CabinetSn = "SN-778899",
        });
    }

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
    }

    private System.Collections.Generic.List<FwVersionRecord> Find(params string[] words) =>
        _db.SearchFwVersionsByTokens(words);

    /// <summary>ОПЦ НЕ вытесняет обычную прошивку: это разовая сборка под конкретный шкаф, она
    /// ничего не заменяет и жить должна рядом.</summary>
    [Fact]
    public void Опц_не_вытесняет_обычную_прошивку_из_выдачи()
    {
        var found = Find("ТГР");

        Assert.Contains(found, r => !r.IsOpc);
        Assert.Contains(found, r => r.IsOpc);
    }

    /// <summary>Главное. ОПЦ заводится ради конкретной заявки, и искать её будут именно по её
    /// номеру — другого имени у неё нет.</summary>
    [Fact]
    public void Опц_находится_по_номеру_заявки()
    {
        var found = Find("01312");

        Assert.Contains(found, r => r.IsOpc && r.RequestNum == "01312");
    }

    /// <summary>И по заводскому номеру шкафа — вторая метка, по которой ОПЦ опознают на объекте.</summary>
    [Fact]
    public void Опц_находится_по_серийному_номеру_шкафа()
    {
        var found = Find("SN-778899");

        Assert.Contains(found, r => r.IsOpc && r.CabinetSn == "SN-778899");
    }

    /// <summary>Номер заявки не должен «цеплять» чужие прошивки: он опознаёт ровно одну.</summary>
    [Fact]
    public void Номер_заявки_находит_только_свою_прошивку()
    {
        Assert.Single(Find("01312"));
    }
}
