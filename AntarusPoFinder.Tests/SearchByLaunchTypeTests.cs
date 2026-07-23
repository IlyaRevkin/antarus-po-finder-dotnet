using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Тип пуска (УПП/ПП/ПЧ/КПЧ) оператор отмечает галочкой при загрузке версии — и потом ищет
/// ровно по нему. До этого launch_types не участвовал в подсчёте очков вообще: находилось только то,
/// что случайно совпало с названием подтипа или тегом («указываю тип пуска, а в поиске по нему не
/// находит»). См. Database.SearchFwVersionsByTokens.</summary>
public class SearchByLaunchTypeTests
{
    private static (int SubtypeId, int ControllerId) AddVersion(Database db, string subtypeName, int sw,
        List<string> launchTypes)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == subtypeName);
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");
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
            LaunchTypes = launchTypes,
            Status = "active",
        });
        return (subtype.Id!.Value, mod.ControllerId);
    }

    [Fact]
    public void QueryByLaunchType_FindsVersionMarkedWithIt()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var withUpp = AddVersion(db, "КНС", 1, new List<string> { "УПП" });

        var hits = SearchService.Search(db, "УПП");

        Assert.Contains(hits, h => h.SubtypeId == withUpp.SubtypeId);
    }

    [Fact]
    public void QueryByLaunchType_DoesNotFindVersionWithoutIt()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        AddVersion(db, "КНС", 1, new List<string> { "ПЧ" });

        // «УПП» не встречается ни в одном поле этой версии — единственный кандидат по типу пуска
        // отмечен другим значением, так что находиться нечему.
        var hits = SearchService.Search(db, "УПП", exactWord: true);

        Assert.Empty(hits);
    }

    /// <summary>Список типов пуска закрытый, и короткие в нём — подстроки длинных («ПЧ» в «КПЧ»,
    /// «ПП» в «УПП»). Подстрочное совпадение поднимало по запросу «ПЧ» ещё и шкафы, отмеченные КПЧ —
    /// причём в обычном режиме поиска, без галочки «точное совпадение слова», то есть по умолчанию.</summary>
    [Theory]
    [InlineData("ПЧ", "КПЧ")]
    [InlineData("ПП", "УПП")]
    public void QueryByLaunchType_DoesNotMatchLongerLaunchTypeContainingIt(string query, string marked)
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        AddVersion(db, "КНС", 1, new List<string> { marked });

        Assert.Empty(SearchService.Search(db, query));
    }

    [Fact]
    public void LaunchTypeIsShownOnTheCard_SoTheHitIsExplainable()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        AddVersion(db, "КНС", 1, new List<string> { "КПЧ", "ПЧ" });

        var hit = Assert.Single(SearchService.Search(db, "КПЧ", exactWord: true));

        Assert.Equal("КПЧ, ПЧ", hit.WorkType);
    }
}
