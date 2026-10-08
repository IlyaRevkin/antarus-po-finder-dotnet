using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Место ОПЦ в поисковой выдаче.
///
/// Жалоба Ильи дословно: «в поисковой выдаче мне не отличить обычную от опц, плюс опц должны иметь
/// ниже поисковой вес, так как они крайне редко нужны и чаще их ищут по номеру заявки или SN».
///
/// Беда в том, что ОПЦ совпадает с обычным запросом ровно теми же словами, что и обычная прошивка:
/// это её родственник по линейке, те же тип, подтип и контроллер. Поэтому она садилась рядом — а то
/// и выше, если её пару раз открыли, — хотя подходит ровно одному шкафу на свете.
///
/// Проверяется именно ПОРЯДОК, а не наличие: отсечь ОПЦ нельзя (тогда её не найти вовсе), и ровно
/// это здесь тоже закреплено.</summary>
public class OpcSearchRankTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly Database _db;

    public OpcSearchRankTests()
    {
        _db = new Database(_dbFile.Path);

        var group = _db.GetAllEquipmentGroups().First(g => g.Name == "ТГР");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).First();
        var mod = _db.GetAllModifications().First(m => m.ControllerName == "SMH5");

        _db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = mod.ControllerId,
            HwVersion = 5, SwVersion = 3, VersionRaw = "3.0.005.0003",
            DtStr = "20260101_0000", Tags = "насосная",
        });

        _db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = mod.ControllerId,
            HwVersion = 5, SwVersion = 4, VersionRaw = "3.0.005.0004",
            DtStr = "20260201_0000", IsOpc = true, Tags = "насосная",
            RequestNum = "01312", CabinetSn = "SN-778899",
        });

        // Обычная (не ОПЦ) прошивка со своим номером заявки — мерка для проверки ниже: она нужна,
        // чтобы было с чем сравнить очки ОПЦ, найденной по номеру.
        _db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = mod.ControllerId,
            HwVersion = 6, SwVersion = 1, VersionRaw = "3.0.006.0001",
            DtStr = "20260301_0000", RequestNum = "77777",
        });
    }

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
    }

    private List<ScoredFwVersion> Search(string query, bool exactWord = false) =>
        _db.SearchFwVersions(query.Split(' '), exactWord, phrase: query);

    /// <summary>Главное. На обычный запрос ОПЦ стоит НИЖЕ обычной прошивки, хотя совпала с запросом
    /// теми же словами.</summary>
    [Fact]
    public void На_обычный_запрос_опц_стоит_ниже_обычной_прошивки()
    {
        var found = Search("насосная");

        var normal = found.FindIndex(e => !e.Row.IsOpc);
        var opc = found.FindIndex(e => e.Row.IsOpc);

        Assert.True(normal >= 0 && opc >= 0, "обе прошивки должны быть в выдаче");
        Assert.True(normal < opc, "обычная прошивка должна стоять выше разовой (ОПЦ)");
    }

    /// <summary>Понижение — это понижение, а не отсев: ОПЦ остаётся в выдаче. Спрятать её значило бы
    /// вернуть прежнюю жалобу «ОПЦ в поиске не находится».</summary>
    [Fact]
    public void Понижение_не_выбрасывает_опц_из_выдачи()
    {
        Assert.Contains(Search("насосная"), e => e.Row.IsOpc);
    }

    /// <summary>Спросили по номеру заявки — значит просят ровно эту сборку: штраф снимается, и ОПЦ
    /// идёт первой.</summary>
    [Fact]
    public void По_номеру_заявки_опц_идёт_первой()
    {
        var found = Search("01312");

        Assert.NotEmpty(found);
        Assert.True(found[0].Row.IsOpc);
    }

    /// <summary>То же и по заводскому номеру шкафа — вторая метка, по которой ОПЦ опознают на
    /// объекте.</summary>
    [Fact]
    public void По_заводскому_номеру_шкафа_опц_идёт_первой()
    {
        var found = Search("SN-778899");

        Assert.NotEmpty(found);
        Assert.True(found[0].Row.IsOpc);
    }

    /// <summary>Поиск «в кавычках» (точный) живёт по тем же правилам: по номеру заявки ОПЦ сверху,
    /// по общему запросу — ниже обычной. Два режима поиска уже разъезжались, поэтому проверяются
    /// оба.</summary>
    [Fact]
    public void В_точном_поиске_правила_те_же()
    {
        var byNumber = Search("01312", exactWord: true);
        Assert.NotEmpty(byNumber);
        Assert.True(byNumber[0].Row.IsOpc);

        var byWord = Search("насосная", exactWord: true);
        var normal = byWord.FindIndex(e => !e.Row.IsOpc);
        var opc = byWord.FindIndex(e => e.Row.IsOpc);
        Assert.True(normal >= 0 && opc >= 0, "обе прошивки должны быть в выдаче");
        Assert.True(normal < opc, "обычная прошивка должна стоять выше разовой (ОПЦ)");
    }

    /// <summary>Снятие штрафа — именно снятие, а не «немного поменьше»: ОПЦ, спрошенная по своему
    /// номеру, получает ровно столько же очков, сколько получила бы обычная прошивка, найденная по
    /// своему номеру тем же способом. Иначе «чаще их ищут по номеру заявки» работало бы наполовину:
    /// по номеру нашлась, но под чужими строками.</summary>
    [Fact]
    public void По_номеру_заявки_штраф_снимается_полностью()
    {
        var opc = Search("01312")[0];
        var normal = Search("77777")[0];

        Assert.True(opc.Row.IsOpc);
        Assert.False(normal.Row.IsOpc);
        Assert.Equal(normal.Score, opc.Score);
    }

    /// <summary>Выдача отдаёт карточке признак ОПЦ и оба номера. Без этого карточке попросту нечего
    /// показать: «я загрузил опц, нигде не написано что это опц, в итоге номер заявки или сн не
    /// отображается в карточке».</summary>
    [Fact]
    public void Выдача_доносит_до_карточки_признак_опц_и_номера()
    {
        var row = Search("01312")[0].Row;

        var card = SearchService.ToHierarchyResult(row);

        Assert.True(card.IsOpc);
        Assert.Equal("01312", card.RequestNum);
        Assert.Equal("SN-778899", card.CabinetSn);
    }

    /// <summary>Сквозная проверка того, на что жаловался Илья: «номер заявки всё ещё не отображается
    /// в копируемом тексте — условно 2.1.0004.0002 отображается, а должно быть
    /// 2.1.0004.0002_(47137)». Путь целиком: строка из базы → выдача поиска → строка, которую
    /// карточка кладёт в буфер. Отдельные куски этого пути уже проверены порознь, но жалоба была
    /// именно про путь целиком — потеряться номер мог на любом стыке.</summary>
    [Fact]
    public void Из_выдачи_в_буфер_уезжает_номер_с_меткой_заявки()
    {
        var card = SearchService.ToHierarchyResult(Search("01312")[0].Row);

        var copied = FirmwareNaming.CopyableVersionName(
            card.VersionRaw, card.IsOpc, card.RequestNum, card.CabinetSn);

        Assert.Contains("_(01312)", copied);
        Assert.Contains("_SN" + card.CabinetSn, copied);
        Assert.StartsWith(card.VersionRaw, copied);
    }
}
