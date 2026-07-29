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

    /// <summary>Выдача помечает КАЖДЫЙ результат числом совпавших слов запроса (MatchedTokens), чтобы
    /// экран мог показать сразу только полные совпадения, а частичные («совпало лишь одно общее
    /// слово») спрятать под «Показать ещё». По запросу «smh hertz» карточка НГР с обоими словами
    /// помечена 2, чужая ПЖ SMH без тега hertz — 1.</summary>
    [Fact]
    public void MatchedTokens_CountsDistinctQueryWordsHit()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var partial = AddVersion(db, "УПД", 2, TagString.Join(new[] { "пж" }), controller: "SMH5");
        var full = AddVersion(db, "КНС", 1, TagString.Join(new[] { "hertz" }), controller: "SMH5");

        var byId = SearchService.Search(db, "smh hertz").ToDictionary(h => h.FwVersionId);

        Assert.Equal(2, byId[full].MatchedTokens);   // совпали и «smh» (контроллер), и «hertz» (тег)
        Assert.Equal(1, byId[partial].MatchedTokens); // совпал только «smh», тега hertz нет
    }

    /// <summary>Смешанная раскладка: оператор пишет «hertz» на ЙЦУКЕН, не переключившись (выходит
    /// «рукея»), «кпч» русскими, а «smh» латиницей. Сплошная замена всего запроса сломала бы «кпч» и
    /// «smh»; послованная чинит только «рукея»→«hertz», и НГР со всеми тремя признаками (тег hertz,
    /// тип пуска КПЧ, контроллер SMH5) находится и совпадает по всем трём словам.</summary>
    [Fact]
    public void MixedKeyboardLayout_RepairsOnlyWrongLayoutTokens()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var target = AddVersion(db, "КНС", 1, TagString.Join(new[] { "hertz" }), controller: "SMH5",
            launchTypes: new List<string> { "КПЧ" });

        // «рукея» = h-e-r-t-z, набранные на ЙЦУКЕН; «кпч» русскими; «smh» латиницей.
        var byId = SearchService.Search(db, "рукея кпч smh").ToDictionary(h => h.FwVersionId);

        Assert.Contains(target, byId.Keys);
        Assert.Equal(3, byId[target].MatchedTokens);
    }

    /// <summary>Обратная сторона той же починки: слово чинится по раскладке, только если как есть оно
    /// не встречается в индексе. «kinco» (латиницей, правильно) не должно превращаться в «лштсщ» —
    /// раз уж такой контроллер в базе есть, слово оставляют как есть.</summary>
    [Fact]
    public void MixedKeyboardLayout_LeavesTokensThatAlreadyMatch()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var target = AddVersion(db, "КНС", 1, TagString.Join(new[] { "нгр" }), controller: "SMH5");

        // «нгр» совпадает как есть (тег), «smh» — как есть (контроллер): ни одно не должно конвертироваться.
        var byId = SearchService.Search(db, "нгр smh").ToDictionary(h => h.FwVersionId);

        Assert.Contains(target, byId.Keys);
        Assert.Equal(2, byId[target].MatchedTokens);
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
