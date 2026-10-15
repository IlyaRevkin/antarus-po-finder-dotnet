using System;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>СПРАВОЧНИК ИСПОЛНЕНИЙ: правка, включение-выключение, дата заведения.
///
/// Просьба Ильи 15.10.2026: «добавь возможность редактировать исполнения, также делать из активными
/// или нет дату создания исполнения и та и отображение таблицей уже удобней будет».
///
/// Главное, что здесь проверяется, — НЕ удобство, а целостность линеек. Исполнения сравниваются
/// ТОЧНО, строка в строку (см. FwExecution): «3 насоса» и «3 Насоса» — две разные линейки прошивок,
/// актуальные одновременно. Поэтому переименование обязано двигать и прошивки тоже, а склеивать два
/// исполнения в одно нельзя вовсе.</summary>
public class ExecutionCatalogTests
{
    /// <summary>Переименование тянет за собой ПРОШИВКИ. Иначе линейка рвётся надвое: версии со
    /// старым написанием перестают заменять новые и повисают в модерации все разом.</summary>
    [Fact]
    public void Переименование_переносит_и_прошивки()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);
        db.AddExecutionToCatalog("3 насоса");

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First();
        var mod = db.GetAllModifications().First();
        var fwId = db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion, SwVersion = 1, DtStr = "20261015_1000",
            VersionRaw = "9.9.9.1", Execution = "3 насоса", Status = "active",
        });

        Assert.True(db.RenameExecution("3 насоса", "Три насоса"));

        Assert.Contains("Три насоса", db.GetExecutionCatalog());
        Assert.DoesNotContain("3 насоса", db.GetExecutionCatalog());
        Assert.Equal("Три насоса", db.GetFwVersionById(fwId)!.Execution);
    }

    /// <summary>Переименовать в уже существующее имя нельзя: это склеило бы две разные линейки
    /// прошивок в одну молча, и обратно их уже не разделить.</summary>
    [Fact]
    public void Нельзя_переименовать_в_занятое_имя()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);
        db.AddExecutionToCatalog("3 насоса");
        db.AddExecutionToCatalog("ПЧ Danfoss");

        Assert.False(db.RenameExecution("3 насоса", "ПЧ Danfoss"));
        Assert.Contains("3 насоса", db.GetExecutionCatalog());
    }

    /// <summary>Выключенное исполнение больше не предлагают при загрузке — ради этого выключатель и
    /// нужен.</summary>
    [Fact]
    public void Выключенное_исполнение_не_предлагается()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);
        db.AddExecutionToCatalog("3 насоса");
        db.AddExecutionToCatalog("Старое исполнение");
        var subtype = db.GetSubtypesForGroup(db.GetAllEquipmentGroups().First().Id!.Value).First();
        var mod = db.GetAllModifications().First();

        db.SetExecutionActive("Старое исполнение", false);

        var choices = db.GetExecutionChoices(subtype.Id!.Value, mod.ControllerId);
        Assert.Contains("3 насоса", choices);
        Assert.DoesNotContain("Старое исполнение", choices);
    }

    /// <summary>Выключение — это НЕ удаление: пометка у загруженных прошивок остаётся, иначе две
    /// линейки слились бы в одну задним числом. Именно поэтому выключатель нужен отдельно от
    /// крестика.</summary>
    [Fact]
    public void Выключение_не_стирает_пометку_у_прошивок()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);
        db.AddExecutionToCatalog("Старое исполнение");
        var group = db.GetAllEquipmentGroups().First();
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First();
        var mod = db.GetAllModifications().First();
        var fwId = db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion, SwVersion = 1, DtStr = "20261015_1000",
            VersionRaw = "9.9.9.2", Execution = "Старое исполнение", Status = "active",
        });

        db.SetExecutionActive("Старое исполнение", false);

        Assert.Equal("Старое исполнение", db.GetFwVersionById(fwId)!.Execution);
        Assert.Contains(db.GetExecutionRows(), r => r.Name == "Старое исполнение" && !r.Active);
    }

    /// <summary>Дата заведения записывается при создании и больше не меняется — это «когда завели»,
    /// а не «когда трогали».</summary>
    [Fact]
    public void Дата_заведения_записывается_и_не_меняется()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);
        db.AddExecutionToCatalog("3 насоса");
        var created = db.GetExecutionRows().Single(r => r.Name == "3 насоса").CreatedAt;
        Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), created);

        db.SetExecutionActive("3 насоса", false);
        db.RenameExecution("3 насоса", "Три насоса");

        Assert.Equal(created, db.GetExecutionRows().Single(r => r.Name == "Три насоса").CreatedAt);
    }

    /// <summary>Новое исполнение заводится включённым: заводят его, чтобы тут же использовать.</summary>
    [Fact]
    public void Новое_исполнение_включено()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);
        db.AddExecutionToCatalog("3 насоса");

        Assert.True(db.GetExecutionRows().Single(r => r.Name == "3 насоса").Active);
    }
}
