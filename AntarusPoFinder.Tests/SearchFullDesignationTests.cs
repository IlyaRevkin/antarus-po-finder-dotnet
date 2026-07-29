using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Жалоба наладчика: вставил в поиск целое обозначение шкафа
/// «Шкаф управление АМПЕРУС НГР-КПЧ-3-1,5(3,8А)-РВР-RS-485-L-Ст», галочка точного поиска снята, а
/// прошивка «НГР-2.0 SMH5» с тегами «RS485 НГР 2.0 SMH5» и типом пуска КПЧ не нашлась — хотя совпадают
/// и НГР, и КПЧ, и RS485. Проверяем, что обычный поиск по словам её находит (совпадений хватает), и
/// что скобки в обозначении не склеивают куски в мусорные токены.</summary>
public class SearchFullDesignationTests
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
            LaunchTypes = launchTypes ?? new List<string> { "КПЧ" },
            Tags = tags,
            Status = "active",
        });
    }

    private const string FullDesignation = "Шкаф управление АМПЕРУС НГР-КПЧ-3-1,5(3,8А)-РВР-RS-485-L-Ст";

    [Fact]
    public void FullCabinetDesignation_FindsTargetFirmware()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var target = AddVersion(db, "2.0", 1, TagString.Join(new[] { "RS485", "НГР", "2.0", "SMH5" }),
            controller: "SMH5", launchTypes: new List<string> { "КПЧ" });
        // Шум: совпадает только по группе «НГР», без RS485/КПЧ — не должен закрыть собой цель.
        AddVersion(db, "УПД", 2, TagString.Join(new[] { "прочее" }), controller: "SMH4",
            launchTypes: new List<string> { "ПЧ" });

        var hits = SearchService.Search(db, FullDesignation, exactWord: false);

        Assert.Contains(hits, h => h.FwVersionId == target);
        // Совпало больше слов (НГР, КПЧ, RS, 485) → цель стоит выше шумовой версии.
        Assert.Equal(target, hits[0].FwVersionId);
    }

    /// <summary>Скобки в «1,5(3,8А)» — разделители слова, как запятая и дефис: должны получиться
    /// цельные токены «5», «3», «8А», а не склейки «5(3» / «8А)», которые ни с чем не совпадают.</summary>
    [Fact]
    public void Normalize_TreatsBracketsAsSeparators()
    {
        var tokens = SearchService.Normalize(FullDesignation).Split(' ');

        Assert.DoesNotContain("5(3", tokens);
        Assert.DoesNotContain("8А)", tokens);
        Assert.Contains("8А", tokens);
        // Точка не разделитель — «2.0» осталось бы одним словом (здесь его нет, но проверяем принцип).
        Assert.Equal("НГР 2.0", SearchService.Normalize("НГР-2.0"));
    }
}
