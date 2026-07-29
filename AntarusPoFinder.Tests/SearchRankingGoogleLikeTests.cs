using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Модель поиска «как в Гугле» (просьба наладчика): без галочки — обычный поиск по словам,
/// но порядок выдачи подчиняется двум принципам: «чем больше слов запроса совпало и чем ближе они
/// к тому, как написано в запросе, тем выше». Галочка «в кавычках» — уже отдельный, точный
/// (позиционный) режим, он покрыт SearchTagsAndFiltersTests.</summary>
public class SearchRankingGoogleLikeTests
{
    private static int AddVersion(Database db, string subtypeName, int sw, string tags,
        string controller = "SMH4", List<string>? launchTypes = null)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == subtypeName);
        var mod = db.GetAllModifications().First(m => m.ControllerName == controller);
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
            LaunchTypes = launchTypes ?? new List<string> { "ПЧ" },
            Tags = tags,
            Status = "active",
        });
    }

    /// <summary>«Больше совпадений → выше»: версия, у которой совпали все три слова запроса, обязана
    /// стоять выше той, у которой совпало только одно, — даже если у второй это слово в теге.</summary>
    [Fact]
    public void MoreMatchedWords_RankHigher()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var partial = AddVersion(db, "УПД", 2, TagString.Join(new[] { "нгр" }), controller: "SMH4");
        var full = AddVersion(db, "КНС", 1, TagString.Join(new[] { "нгр 2.0 smh5" }), controller: "SMH5");

        var hits = SearchService.Search(db, "нгр 2.0 smh5");

        Assert.Equal(full, hits[0].FwVersionId);
        Assert.True(hits.Count > 1, "обычный поиск остаётся широким — версия с одним совпавшим словом тоже в выдаче");
    }

    /// <summary>«Точнее запрос → выше»: два одинаковых по числу совпавших слов кандидата, но у одного
    /// слова стоят рядом и в том же порядке, что в запросе, — он выше.</summary>
    [Fact]
    public void WordsInQueryOrder_RankAboveScattered()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var scattered = AddVersion(db, "УПД", 2, TagString.Join(new[] { "резерв", "прочее", "пожарный" }));
        var adjacent = AddVersion(db, "КНС", 1, TagString.Join(new[] { "пожарный", "резерв" }));

        var hits = SearchService.Search(db, "пожарный резерв");

        Assert.Equal(adjacent, hits[0].FwVersionId);
        Assert.Contains(hits, h => h.FwVersionId == scattered);
    }

    /// <summary>Жалоба наладчика ровно в обычном (дефолтном теперь) режиме: залил НГР-2.0 SMH5, тег
    /// «нгр 2.0 smh5» — «нгр 2.0» без галочки обязано находить и ставить эту версию первой.</summary>
    [Fact]
    public void KeywordSearch_FindsFirmwareByWords_WithoutExactCheckbox()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var id = AddVersion(db, "КНС", 1, TagString.Join(new[] { "нгр 2.0 smh5" }), controller: "SMH5");

        var hits = SearchService.Search(db, "нгр 2.0", exactWord: false);

        Assert.NotEmpty(hits);
        Assert.Equal(id, hits[0].FwVersionId);
    }

    /// <summary>Точное равенство одному тегу перевешивает просто «много совпавших слов»: точно
    /// поименованная прошивка стоит выше набравшей столько же слов вразнобой.</summary>
    [Fact]
    public void ExactTagEquality_OutranksSameWordsScattered()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var scattered = AddVersion(db, "УПД", 2,
            TagString.Join(new[] { "пожарный", "прочее", "резерв", "насос" }));
        var namedTag = AddVersion(db, "КНС", 1, TagString.Join(new[] { "пожарный резерв насос" }));

        var hits = SearchService.Search(db, "пожарный резерв насос");

        Assert.Equal(namedTag, hits[0].FwVersionId);
        Assert.Contains(hits, h => h.FwVersionId == scattered);
    }
}
