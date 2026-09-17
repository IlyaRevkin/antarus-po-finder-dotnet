using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Прошивка, которую показывают только если про неё спросили.
///
/// Просьба Ильи 17.09.2026: «когда я ищу шкаф НГР-ПП-2-(2.5-4А)-Рх, я ищу Рх и всё ок, а когда
/// НГР-ПП-2-(2.5-4А), мне выдаёт Рх тоже, а он мне не нужен».
///
/// Совпадение тут честное — короткий запрос является префиксом длинного названия, — и прятать такие
/// строки догадкой нельзя: иногда нужен как раз длинный вариант. Решает тот, кто заводил прошивку:
/// у неё указываются слова, без которых её не показывают.</summary>
public class FwOnDemandTermsTests
{
    private static int AddVersion(Database db, int sw, string tags, string onDemand = "")
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
            OnDemandTerms = onDemand,
            Execution = onDemand.Length > 0 ? "резерв" : "",
        });
    }

    private static List<int?> Find(Database db, string phrase) =>
        db.SearchFwVersions(phrase.Split(' ', '-'), phrase: phrase).Select(r => r.Row.Id).ToList();

    [Fact]
    public void FirmwareWithOnDemandTerm_HidesUntilTheTermIsAsked()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);

        var plain = AddVersion(db, 1, "НГР-ПП-2-(2.5-4А)");
        var reserve = AddVersion(db, 2, "НГР-ПП-2-(2.5-4А)-Рх", onDemand: "Рх");

        var without = Find(db, "НГР-ПП-2-(2.5-4А)");
        Assert.Contains(plain, without);
        Assert.DoesNotContain(reserve, without);

        var with = Find(db, "НГР-ПП-2-(2.5-4А)-Рх");
        Assert.Contains(reserve, with);
    }

    /// <summary>Регистр не должен иметь значения: «рх» и «РХ» — одно и то же слово. Проверяем именно
    /// кириллицу — в SQLite её регистр не сворачивается, и соблазн сделать это запросом к базе
    /// привёл бы к тому, что днём работает, а ночью нет.</summary>
    [Fact]
    public void TheTerm_IsMatchedRegardlessOfCase()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        var reserve = AddVersion(db, 2, "НГР-ПП-2-(2.5-4А)-Рх", onDemand: "Рх");

        Assert.Contains(reserve, Find(db, "НГР-ПП-2-(2.5-4А)-рх"));
        Assert.Contains(reserve, Find(db, "НГР-ПП-2-(2.5-4А)-РХ"));
    }

    /// <summary>Слов может быть несколько, и это синонимы: хватает любого. Требовать все значило бы
    /// требовать от человека помнить, каким именно словом он прошивку когда-то пометил.</summary>
    [Fact]
    public void AnyOneOfTheTerms_IsEnough()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        var reserve = AddVersion(db, 2, "НГР-ПП-2 резервный", onDemand: "Рх, резерв");

        Assert.Contains(reserve, Find(db, "НГР-ПП-2 резерв"));
        Assert.Contains(reserve, Find(db, "НГР-ПП-2 Рх"));
        Assert.DoesNotContain(reserve, Find(db, "НГР-ПП-2"));
    }

    /// <summary>Галка «показывать все версии» на то и галка: она показывает всё, включая спрятанное.
    /// Прятать что-то от неё значило бы соврать её названием.</summary>
    [Fact]
    public void ShowAllVersions_ShowsTheHiddenOnesToo()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        AddVersion(db, 1, "НГР-ПП-2-(2.5-4А)");
        var reserve = AddVersion(db, 2, "НГР-ПП-2-(2.5-4А)-Рх", onDemand: "Рх");

        var all = db.SearchFwVersions(new[] { "НГР" }, phrase: "НГР", showAllVersions: true)
            .Select(r => r.Row.Id).ToList();
        Assert.Contains(reserve, all);
    }

    /// <summary>Обратная половина: у подавляющего большинства прошивок поле пустое, и они обязаны
    /// вести себя ровно как раньше — иначе правка спрятала бы половину справочника.</summary>
    [Fact]
    public void FirmwareWithoutTerms_IsUnaffected()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        var plain = AddVersion(db, 1, "НГР-ПП-2-(2.5-4А)");
        Assert.Contains(plain, Find(db, "НГР"));
        Assert.Contains(plain, Find(db, "НГР-ПП-2-(2.5-4А)"));
    }

    [Fact]
    public void Terms_AreStoredAndReadBackAsWritten()
    {
        Assert.Equal("Рх, резерв", FwOnDemandTerms.Normalize("  Рх ,, резерв ,  "));
        Assert.Equal("Рх", FwOnDemandTerms.Normalize("Рх, рх"));
        Assert.Empty(FwOnDemandTerms.Parse("   "));
        Assert.True(FwOnDemandTerms.AskedFor("", "что угодно"));
        Assert.False(FwOnDemandTerms.AskedFor("Рх", ""));
    }
}
