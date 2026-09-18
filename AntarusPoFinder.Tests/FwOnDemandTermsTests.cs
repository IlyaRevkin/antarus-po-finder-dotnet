using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.App;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Слова-исключения: прошивка не показывается, пока про неё не спросили этим словом.
///
/// Просьба Ильи 17.09.2026: «когда я ищу шкаф НГР-ПП-2-(2.5-4А)-Рх, я ищу Рх и всё ок, а когда
/// НГР-ПП-2-(2.5-4А), мне выдаёт Рх тоже, а он мне не нужен». И уточнение: «лучше слова исключения
/// добавить» — то есть один общий список слов, а не пометка у каждой прошивки. Слово «Рх» относится
/// ко всему, где встречается, включая то, что загрузят завтра.</summary>
public class FwOnDemandTermsTests
{
    private static int AddVersion(Database db, int sw, string tags)
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
            Execution = sw == 2 ? "резерв" : "",
        });
    }

    private static List<int?> Find(Database db, string phrase) =>
        db.SearchFwVersions(phrase.Split(' ', '-'), phrase: phrase).Select(r => r.Row.Id).ToList();

    [Fact]
    public void WordInTheList_HidesTheFirmwareUntilTheWordIsAsked()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);

        var plain = AddVersion(db, 1, "НГР-ПП-2-(2.5-4А)");
        var reserve = AddVersion(db, 2, "НГР-ПП-2-(2.5-4А)-Рх");

        // Пока слова в списке нет — находится и то и другое, как раньше.
        Assert.Contains(reserve, Find(db, "НГР-ПП-2-(2.5-4А)"));

        db.AddOnDemandWord("Рх");

        var without = Find(db, "НГР-ПП-2-(2.5-4А)");
        Assert.Contains(plain, without);
        Assert.DoesNotContain(reserve, without);

        Assert.Contains(reserve, Find(db, "НГР-ПП-2-(2.5-4А)-Рх"));
    }

    /// <summary>Слово заводится один раз и действует на то, что загрузят потом. Ради этого список и
    /// сделан общим, а не полем у каждой записи.</summary>
    [Fact]
    public void TheWord_AppliesToFirmwareAddedLater()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);

        db.AddOnDemandWord("Рх");
        var reserve = AddVersion(db, 2, "НГР-ПП-2-(2.5-4А)-Рх");

        Assert.DoesNotContain(reserve, Find(db, "НГР-ПП-2-(2.5-4А)"));
        Assert.Contains(reserve, Find(db, "НГР Рх"));
    }

    /// <summary>Регистр не важен, и проверяем именно кириллицу: в SQLite её регистр не сворачивается,
    /// и соблазн сравнивать запросом к базе дал бы «днём работает, ночью нет».</summary>
    [Fact]
    public void TheWord_IsMatchedRegardlessOfCase()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        db.AddOnDemandWord("рх");
        var reserve = AddVersion(db, 2, "НГР-ПП-2-(2.5-4А)-РХ");

        Assert.DoesNotContain(reserve, Find(db, "НГР-ПП-2"));
        Assert.Contains(reserve, Find(db, "НГР-ПП-2-Рх"));
    }

    /// <summary>Галка «показывать все версии» на то и галка: показывает всё, включая спрятанное.</summary>
    [Fact]
    public void ShowAllVersions_ShowsTheHiddenOnesToo()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        db.AddOnDemandWord("Рх");
        AddVersion(db, 1, "НГР-ПП-2-(2.5-4А)");
        var reserve = AddVersion(db, 2, "НГР-ПП-2-(2.5-4А)-Рх");

        var all = db.SearchFwVersions(new[] { "НГР" }, phrase: "НГР", showAllVersions: true)
            .Select(r => r.Row.Id).ToList();
        Assert.Contains(reserve, all);
    }

    /// <summary>Обратная половина: пустой список ничего не меняет. Иначе правка спрятала бы
    /// половину справочника у всех, кто этим не пользуется.</summary>
    [Fact]
    public void EmptyList_ChangesNothing()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        var plain = AddVersion(db, 1, "НГР-ПП-2-(2.5-4А)");
        var reserve = AddVersion(db, 2, "НГР-ПП-2-(2.5-4А)-Рх");

        var found = Find(db, "НГР-ПП-2-(2.5-4А)");
        Assert.Contains(plain, found);
        Assert.Contains(reserve, found);
    }

    /// <summary>Убрали слово — всё снова находится как раньше. Прошивки при этом не трогаются:
    /// слово нигде у них не записано.</summary>
    [Fact]
    public void RemovingTheWord_BringsTheFirmwareBack()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        var reserve = AddVersion(db, 2, "НГР-ПП-2-(2.5-4А)-Рх");

        db.AddOnDemandWord("Рх");
        Assert.DoesNotContain(reserve, Find(db, "НГР-ПП-2-(2.5-4А)"));

        db.DeleteOnDemandWord("Рх");
        Assert.Contains(reserve, Find(db, "НГР-ПП-2-(2.5-4А)"));
    }

    /// <summary>Одно и то же слово в другом регистре не заводится вторым — иначе словарь по именам
    /// с игнором регистра, который строит приём конфига, упал бы на дубликате ключа.</summary>
    [Fact]
    public void TheSameWord_IsNotAddedTwiceInAnotherCase()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        db.AddOnDemandWord("Рх");
        db.AddOnDemandWord("РХ");
        Assert.Single(db.GetOnDemandWords());
    }

    [Fact]
    public void Rule_IsExpressedPlainly()
    {
        var words = new List<string> { "Рх" };
        Assert.True(FwOnDemandTerms.ShouldHide(words, "НГР-ПП-2-Рх", "НГР-ПП-2"));
        Assert.False(FwOnDemandTerms.ShouldHide(words, "НГР-ПП-2-Рх", "НГР-ПП-2-Рх"));
        Assert.False(FwOnDemandTerms.ShouldHide(words, "НГР-ПП-2", "НГР-ПП-2"));
        Assert.False(FwOnDemandTerms.ShouldHide(new List<string>(), "НГР-ПП-2-Рх", "НГР"));
    }
}

/// <summary>Список слов-исключений едет между машинами: правило показа обязано быть одинаковым у
/// всех, иначе один по запросу находит, а другой по тому же запросу нет — и выяснить, у кого
/// правильно, нельзя.</summary>
public class OnDemandWordsSyncTests
{
    [Fact]
    public void TheWordList_TravelsToTheOtherMachine()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        m.DbA.AddOnDemandWord("Рх");
        App.Services.ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");
        App.Services.ConfigSyncService.Apply(m.SvcB, App.Services.ConfigSyncService.ConfigPathFor(m.Root.Path), m.Root.Path);

        Assert.Contains("Рх", m.DbB.GetOnDemandWords());
    }

    /// <summary>И удаление тоже: убранное слово не должно возвращаться с машины, которая о правке
    /// ещё не знает, — ровно как у тегов и видов доп. материалов.</summary>
    [Fact]
    public void RemovingTheWord_TravelsToo()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        m.DbA.AddOnDemandWord("Рх");
        App.Services.ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");
        App.Services.ConfigSyncService.Apply(m.SvcB, App.Services.ConfigSyncService.ConfigPathFor(m.Root.Path), m.Root.Path);
        Assert.Contains("Рх", m.DbB.GetOnDemandWords());

        m.DbA.DeleteOnDemandWord("Рх");
        App.Services.ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");
        App.Services.ConfigSyncService.Apply(m.SvcB, App.Services.ConfigSyncService.ConfigPathFor(m.Root.Path), m.Root.Path);

        Assert.DoesNotContain("Рх", m.DbB.GetOnDemandWords());
    }
}
