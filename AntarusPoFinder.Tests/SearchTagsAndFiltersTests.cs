using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Жалоба, с которой начался этот заход: наладчик поставил прошивке тег с ТОЧНЫМ названием
/// шкафа («шкаф управления пожарными насосами Антарус ПЖ-ПП-2-…»), включил «точное совпадение» и
/// ждал ровно одну прошивку — а получил её первой и следом всё остальное, что случайно совпало
/// словом «шкаф». Причин было две, и обе здесь: многословный тег не существовал как тег (строка
/// тегов разделялась пробелом, см. TagString), а «точное совпадение» означало «любое слово целиком»
/// вместо «все слова». Плюс фильтры выдачи и счётчик выбора — см. Database.Search.cs.</summary>
public class SearchTagsAndFiltersTests
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

    // ── Многословный тег ──────────────────────────────────────────────────

    [Fact]
    public void MultiWordTag_SurvivesStorageAsOneTag()
    {
        var stored = TagString.Join(new[] { "шкаф управления пожарными насосами", "SMH4" });

        Assert.Equal(new[] { "шкаф управления пожарными насосами", "SMH4" }, TagString.Parse(stored));
        // В строке хранения разделителем остаётся обычный пробел — формат, с которым работает
        // синхронизация конфига, не поменялся.
        Assert.Equal(2, stored.Split(' ').Length);
    }

    [Fact]
    public void OldSingleWordTags_ReadBackUnchanged()
    {
        Assert.Equal(new[] { "НГР", "КНС", "SMH4" }, TagString.Parse("НГР КНС SMH4"));
    }

    [Fact]
    public void ExactSearchByFullCabinetNameTag_FindsOnlyThatFirmware()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        const string cabinet = "шкаф управления пожарными насосами Антарус ПЖ-ПП-2";
        var tagged = AddVersion(db, "КНС", 1, TagString.Join(new[] { cabinet, "НГР" }));
        // Тот же «шкаф» в тегах, но другой — раньше он приезжал следом за нужным.
        AddVersion(db, "УПД", 2, TagString.Join(new[] { "шкаф управления задвижкой", "НГР" }));

        var hits = SearchService.Search(db, cabinet, exactWord: true);

        var hit = Assert.Single(hits);
        Assert.Equal(tagged, hit.FwVersionId);
    }

    [Fact]
    public void ExactSearch_RequiresEveryWord_NotJustOne()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var both = AddVersion(db, "КНС", 1, TagString.Join(new[] { "пожарный", "резервный" }));
        AddVersion(db, "УПД", 2, TagString.Join(new[] { "пожарный" }));

        var hit = Assert.Single(SearchService.Search(db, "пожарный резервный", exactWord: true));

        Assert.Equal(both, hit.FwVersionId);
    }

    /// <summary>Без галочки поведение прежнее — совпадение по любому слову; тег-фраза просто весит
    /// больше и идёт первой. Иначе обычный (и самый частый) поиск стал бы строже, чем был.</summary>
    [Fact]
    public void NonExactSearch_StillFindsByAnyWord_ButPhraseTagRanksFirst()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        const string cabinet = "шкаф управления пожарными насосами";
        AddVersion(db, "УПД", 2, TagString.Join(new[] { "шкаф управления задвижкой" }));
        var tagged = AddVersion(db, "КНС", 1, TagString.Join(new[] { cabinet }));

        var hits = SearchService.Search(db, cabinet);

        Assert.True(hits.Count > 1);
        Assert.Equal(tagged, hits[0].FwVersionId);
    }

    [Fact]
    public void RenamingMultiWordTag_KeepsItWhole()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var id = AddVersion(db, "КНС", 1, TagString.Join(new[] { "шкаф пожарный старый", "НГР" }));
        db.AddTag("шкаф пожарный старый");

        db.RenameTag("шкаф пожарный старый", "шкаф пожарный новый");

        var tags = TagString.Parse(db.GetFwVersionById(id)!.Tags);
        Assert.Contains("шкаф пожарный новый", tags);
        Assert.DoesNotContain("шкаф пожарный старый", tags);
        Assert.Contains("НГР", tags);
    }

    // ── Фильтры ───────────────────────────────────────────────────────────

    [Fact]
    public void Filters_NarrowResultsWithoutChangingTheQuery()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var smh4 = AddVersion(db, "КНС", 1, "НГР", controller: "SMH4");
        AddVersion(db, "КНС", 2, "НГР", controller: "SMH5");

        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");
        var hits = SearchService.Search(db, "НГР", exactWord: false,
            filters: new FirmwareSearchFilters { ControllerId = mod.ControllerId });

        var hit = Assert.Single(hits);
        Assert.Equal(smh4, hit.FwVersionId);
    }

    [Fact]
    public void FiltersWithEmptyQuery_ShowEverythingThatMatches()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        AddVersion(db, "КНС", 1, "НГР", launchTypes: new List<string> { "ПЧ" });
        AddVersion(db, "УПД", 2, "НГР", launchTypes: new List<string> { "УПП" });

        var hits = SearchService.Search(db, "", exactWord: false,
            filters: new FirmwareSearchFilters { LaunchType = "ПЧ" });

        Assert.Single(hits);
    }

    [Fact]
    public void EmptyQueryWithoutFilters_FindsNothing()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        AddVersion(db, "КНС", 1, "НГР");

        Assert.Empty(SearchService.Search(db, ""));
    }

    [Fact]
    public void TagFilter_MatchesWholeTag_NotAWordInsideIt()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var exact = AddVersion(db, "КНС", 1, TagString.Join(new[] { "шкаф пожарный" }));
        AddVersion(db, "УПД", 2, TagString.Join(new[] { "шкаф" }));

        var hits = SearchService.Search(db, "", exactWord: false,
            filters: new FirmwareSearchFilters { Tag = "шкаф пожарный" });

        var hit = Assert.Single(hits);
        Assert.Equal(exact, hit.FwVersionId);
    }

    [Fact]
    public void TagsInUse_ListsOnlyTagsActuallyPutOnVersions()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        AddVersion(db, "КНС", 1, TagString.Join(new[] { "шкаф пожарный" }));
        db.AddTag("никому не проставленный тег");

        var inUse = db.GetTagsInUse();

        Assert.Contains("шкаф пожарный", inUse);
        Assert.DoesNotContain("никому не проставленный тег", inUse);
    }

    // ── Счётчик выбора ────────────────────────────────────────────────────

    [Fact]
    public void ChosenFirmware_RisesAmongEquallyRelevantOnes()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var rarely = AddVersion(db, "КНС", 1, "НГР");
        var often = AddVersion(db, "УПД", 2, "НГР");

        var key = SearchService.UsageKey("НГР");
        for (int i = 0; i < 10; i++) db.RecordFwUsage(key, often);
        db.RecordFwUsage(key, rarely);

        var hits = SearchService.Search(db, "НГР");

        Assert.Equal(often, hits[0].FwVersionId);
        Assert.Equal(10, hits[0].UsageCount);
    }

    /// <summary>Частота — это подсказка среди подходящего, а не замена релевантности: сто открытий
    /// прошивки другого шкафа не должны вытаскивать её в выдачу по чужому запросу.</summary>
    [Fact]
    public void UsageNeverOutweighsRelevance()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var offTopic = AddVersion(db, "УПД", 2, TagString.Join(new[] { "совсем другое" }));
        var onTopic = AddVersion(db, "КНС", 1, TagString.Join(new[] { "пожарный", "насос", "резерв" }));

        var key = SearchService.UsageKey("пожарный насос резерв");
        for (int i = 0; i < 100; i++) db.RecordFwUsage(key, offTopic);

        var hits = SearchService.Search(db, "пожарный насос резерв");

        Assert.Equal(onTopic, hits[0].FwVersionId);
    }

    [Fact]
    public void UsageIsPerQuery_NotGlobal()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var id = AddVersion(db, "КНС", 1, "НГР");
        db.RecordFwUsage(SearchService.UsageKey("другой запрос"), id);

        var hit = Assert.Single(SearchService.Search(db, "НГР"));
        Assert.Equal(0, hit.UsageCount);
        // Сам факт выбора при этом не потерян — он записан под своим запросом.
        Assert.Equal(1, db.GetFwUsageTotal(id));
    }

    [Fact]
    public void DeletingFirmware_TakesItsUsageCounterWithIt()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var id = AddVersion(db, "КНС", 1, "НГР");
        db.RecordFwUsage(SearchService.UsageKey("НГР"), id);

        db.DeleteFwVersion(id);

        Assert.Equal(0, db.GetFwUsageTotal(id));
    }

    // ── Индекс поиска ─────────────────────────────────────────────────────

    /// <summary>Снимок для поиска переиспользуется между запросами, поэтому важно, что любая запись
    /// его сбрасывает: иначе только что загруженная прошивка не находилась бы до перезапуска.</summary>
    [Fact]
    public void SearchIndex_SeesRowsAddedAfterTheFirstSearch()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        AddVersion(db, "КНС", 1, "НГР");
        Assert.Single(SearchService.Search(db, "НГР"));

        AddVersion(db, "УПД", 2, "НГР");

        Assert.Equal(2, SearchService.Search(db, "НГР").Count);
    }

    [Fact]
    public void SearchIndex_SeesTagEditsOnAnAlreadySearchedRow()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var id = AddVersion(db, "КНС", 1, "НГР");
        Assert.Empty(SearchService.Search(db, "пожарный"));

        db.UpdateFwVersion(id, tags: TagString.Join(new[] { "НГР", "пожарный" }));

        Assert.Single(SearchService.Search(db, "пожарный"));
    }
}
