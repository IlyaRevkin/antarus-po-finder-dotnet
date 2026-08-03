using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Шаблонные теги в самом поиске (см. TagPatternTests — там проверен матчер сам по себе).
/// Сценарий владельца: программист помечает прошивку ОДНИМ тегом со звёздочкой вместо десятка почти
/// одинаковых названий шкафов, а наладчик вводит КОНКРЕТНОЕ название своего шкафа — и находит её.
///
/// Проверяется в обоих режимах поиска (обычный по словам и «точное совпадение» — позиционный) и в обе
/// стороны (звёздочка в теге / звёздочка в запросе), а также то, что теги БЕЗ звёздочки ищутся ровно
/// как раньше.</summary>
public class SearchTagWildcardTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly Database _db;

    private const string Template = "Шкаф управления пожарными насосами АМПЕРУС ПЖ-ПП-2-(*-*А)-АВР-FD-Ст";
    private static string Cabinet(string amps) => $"Шкаф управления пожарными насосами АМПЕРУС ПЖ-ПП-2-({amps}А)-АВР-FD-Ст";

    public SearchTagWildcardTests()
    {
        _db = new Database(_dbFile.Path);
    }

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
    }

    /// <summary>Одна прошивка с ОДНИМ тегом — именно одним, целой фразой: «шкаф управления пожарными
    /// насосами …» это один тег, а не пять слов (см. TagString — пробелы внутри тега кодируются).
    /// Контроллер выбирается вызывающим, чтобы в одной базе можно было держать две различимые в выдаче
    /// прошивки (поиск отдаёт по одной строке на пару подтип+контроллер, см. Database.Deduplicate).</summary>
    private int Seed(string tag, string controller = "SMH4", string versionRaw = "1.99.7.1")
    {
        var group = _db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = _db.GetAllModifications().First(m => m.ControllerName == controller);

        return _db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion, SwVersion = 1,
            VersionRaw = versionRaw, Filename = "fw.psl",
            Description = "тест шаблонных тегов", Status = "active",
            LaunchTypes = new List<string> { "УПП" },
            Tags = TagString.Join(new[] { tag }),
        });
    }

    private List<HierarchyResult> Search(string query, bool exactWord = false) =>
        SearchService.Search(_db, query, exactWord);

    /// <summary>Главный случай, обычный поиск: наладчик вводит своё название шкафа целиком — находится
    /// прошивка, помеченная шаблоном.</summary>
    [Theory]
    [InlineData("9-14")]
    [InlineData("20-25")]
    [InlineData("100-125")]
    public void ConcreteCabinetName_FindsFirmwareTaggedWithTemplate(string amps)
    {
        var id = Seed(Template);

        var hits = Search(Cabinet(amps));

        Assert.Contains(hits, h => h.FwVersionId == id);
    }

    /// <summary>Тот же случай в режиме «точное совпадение» (в кавычках) — именно им пользуются, когда
    /// нужна ровно одна прошивка под конкретный шкаф.</summary>
    [Fact]
    public void ConcreteCabinetName_FindsTemplateTag_InExactMode()
    {
        var id = Seed(Template);

        var hits = Search(Cabinet("20-25"), exactWord: true);

        Assert.Contains(hits, h => h.FwVersionId == id);
    }

    /// <summary>Обратное направление: звёздочка в ЗАПРОСЕ находит прошивки с конкретными тегами —
    /// «поиск по такому тегу работает в обе стороны».</summary>
    [Fact]
    public void TemplateQuery_FindsFirmwaresTaggedWithConcreteNames()
    {
        var first = Seed(Cabinet("9-14"), "SMH4", "1.99.7.1");
        var second = Seed(Cabinet("20-25"), "SMH5", "1.99.8.1");

        var hits = Search(Template);

        Assert.Contains(hits, h => h.FwVersionId == first);
        Assert.Contains(hits, h => h.FwVersionId == second);
    }

    /// <summary>Шаблон не превращается в «совпадает со всем»: шкаф другой серии не находится ни по
    /// какому направлению.</summary>
    [Fact]
    public void TemplateTag_DoesNotMatchDifferentCabinetSeries()
    {
        var id = Seed(Template);

        var hits = Search("Шкаф управления пожарными насосами АМПЕРУС ПЖ-ПП-3-(9-14А)-АВР-VD-Ст", exactWord: true);

        Assert.DoesNotContain(hits, h => h.FwVersionId == id);
    }

    /// <summary>Совпадение целиком с шаблонным тегом весит столько же, сколько совпадение целиком с
    /// обычным: прошивка «своего» шкафа обязана быть первой, а не тонуть под теми, у кого случайно
    /// совпало больше общих слов.</summary>
    [Fact]
    public void TemplateTagMatch_RanksFirst()
    {
        var template = Seed(Template, "SMH4", "1.99.7.1");
        // Соседняя прошивка, у которой совпадут только общие слова («шкаф», «управления», «насосами»).
        Seed("Шкаф управления насосами водоснабжения", "SMH5", "1.99.8.1");

        var hits = Search(Cabinet("9-14"));

        Assert.NotEmpty(hits);
        Assert.Equal(template, hits[0].FwVersionId);
    }

    /// <summary>Регистр и кириллица: тег и запрос набраны по-разному — совпадение всё равно есть.</summary>
    [Fact]
    public void TemplateTagMatch_IsCaseInsensitive()
    {
        var id = Seed(Template.ToUpperInvariant());

        var hits = Search(Cabinet("9-14").ToLowerInvariant(), exactWord: true);

        Assert.Contains(hits, h => h.FwVersionId == id);
    }

    /// <summary>Теги без звёздочки ищутся ровно как раньше — точное совпадение находит свой шкаф и не
    /// находит чужой. Это страховка от того, что подстановка «размыла» обычный поиск.</summary>
    [Fact]
    public void PlainTags_KeepWorkingExactlyAsBefore()
    {
        var id = Seed(Cabinet("9-14"));

        Assert.Contains(Search(Cabinet("9-14"), exactWord: true), h => h.FwVersionId == id);
        Assert.DoesNotContain(Search(Cabinet("20-25"), exactWord: true), h => h.FwVersionId == id);
    }

    /// <summary>Звёздочка в НАЧАЛЕ тега — ходовой случай («любой шкаф этой серии, чем бы он ни
    /// начинался»).</summary>
    [Fact]
    public void LeadingWildcardTag_MatchesFullCabinetName()
    {
        var id = Seed("*-АВР-FD-Ст");

        Assert.Contains(Search(Cabinet("9-14"), exactWord: true), h => h.FwVersionId == id);
        Assert.DoesNotContain(
            Search("Шкаф управления пожарными насосами АМПЕРУС ПЖ-ПП-2-(9-14А)-АВР-VD-Ст", exactWord: true),
            h => h.FwVersionId == id);
    }
}
