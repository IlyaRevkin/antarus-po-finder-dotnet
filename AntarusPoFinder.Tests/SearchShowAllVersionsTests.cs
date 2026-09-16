using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Галочка «показывать все версии» — снимает схлопывание выдачи.
///
/// Тикет: «сделать чекбокс настройки отображения всего имеющегося в поиске — кому надо, тот включит и
/// будет видеть всё; удобно для модерации видеть, что есть и что надо подгрузить».
///
/// Схлопывание к одной строке на шкаф остаётся умолчанием и снимать его насовсем нельзя: наладчику в
/// цеху нужна одна актуальная прошивка, а не вся история. Поэтому проверяется ОБА состояния — и что
/// галочка показывает спрятанное, и что без неё всё по-прежнему схлопнуто.</summary>
public class SearchShowAllVersionsTests
{
    private static (EquipmentSubType, ControllerModification) Seed(Database db)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");
        return (subtype, mod);
    }

    private static int AddVersion(Database db, int sw)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var (subtype, mod) = Seed(db);
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
            DiskPath = $@"X:\ПО\НГР\КНС\SMH4\{sw}",
            LaunchTypes = new List<string> { "ПЧ" },
            Tags = "КНС",
            Status = "active",
        });
    }

    /// <summary>С галочкой видно каждую версию — именно это и просили: «видеть, что есть и что надо
    /// подгрузить». Без неё та же выдача схлопнута к одной строке.</summary>
    [Fact]
    public void ShowAllVersions_ListsEveryVersion_WhileTheDefaultStillCollapses()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var older = AddVersion(db, sw: 1);
        var newer = AddVersion(db, sw: 2);

        var collapsed = db.SearchFwVersions(new[] { "КНС" }).Select(r => r.Row.Id).ToList();
        var all = db.SearchFwVersions(new[] { "КНС" }, showAllVersions: true).Select(r => r.Row.Id).ToList();

        Assert.Equal(new[] { (int?)newer }, collapsed);
        Assert.Contains(older, all);
        Assert.Contains(newer, all);
    }

    /// <summary>Порядок выдачи галочка не переставляет: наверху та же версия, что и без неё.
    /// Иначе «включил, чтобы посмотреть, что есть» превращалось бы в другой поиск, и сравнить два
    /// состояния глазами было бы нельзя.</summary>
    [Fact]
    public void ShowAllVersions_KeepsTheSameFirstRow()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        AddVersion(db, sw: 1);
        AddVersion(db, sw: 2);

        var collapsedTop = db.SearchFwVersions(new[] { "КНС" }).First().Row.Id;
        var allTop = db.SearchFwVersions(new[] { "КНС" }, showAllVersions: true).First().Row.Id;

        Assert.Equal(collapsedTop, allTop);
    }

    /// <summary>Отбор галочка не расширяет: она снимает только последний шаг «оставить по одной
    /// строке на шкаф». Версия, которая запросу не подходит, не появляется и с ней — иначе поиск
    /// превратился бы в перечисление базы.</summary>
    [Fact]
    public void ShowAllVersions_DoesNotBringInRowsThatDoNotMatchTheQuery()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        AddVersion(db, sw: 1);

        var all = db.SearchFwVersions(new[] { "ПЖ-ПП-НЕТУ-ТАКОГО" }, showAllVersions: true);

        Assert.Empty(all);
    }
}
