using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Отчёт о сбое из очереди тикетов: «SQLite Error 19: UNIQUE constraint failed: tags.name»
/// в Database.RenameTag. Тикет был помечен закрытым, но защиты в коде не появилось — оператор
/// по-прежнему получал окно с отчётом о сбое, просто переименовав тег в уже занятое имя.
///
/// Второй сюжет здесь — кириллица. SQLite COLLATE NOCASE сворачивает только ASCII, поэтому «ПИ» и
/// «пи» для базы разные строки и обе живут в таблице; .NET StringComparer.OrdinalIgnoreCase их
/// сворачивает. На этом расхождении прежний код молча выходил, ничего не переименовав, а интерфейс
/// всё равно рапортовал об успехе.</summary>
public class TagRenameCollisionTests
{
    private static int AddVersion(Database db, int sw, string tags)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First();
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
            Tags = tags,
            Status = "active",
        });
    }

    [Fact]
    public void RenamingIntoTakenName_MergesInsteadOfCrashing()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var onlyOld = AddVersion(db, 1, "насос");
        var both = AddVersion(db, 2, TagString.Join(new[] { "насос", "жокей" }));
        db.AddTag("насос");
        db.AddTag("жокей");

        // Именно этот вызов раньше улетал в UNIQUE constraint failed.
        var result = db.RenameTag("насос", "жокей");

        Assert.Equal(Database.TagRenameOutcome.Merged, result.Outcome);
        Assert.Equal("жокей", result.Name);

        // Справочник: слитый тег исчез, целевой остался ровно один.
        var tags = db.GetAllTags();
        Assert.DoesNotContain("насос", tags);
        Assert.Single(tags.Where(t => t == "жокей"));

        // Записи переехали на целевой тег.
        Assert.Equal(new[] { "жокей" }, TagString.Parse(db.GetFwVersionById(onlyOld)!.Tags));

        // У записи, где были ОБА тега, «жокей» не задвоился.
        Assert.Equal(new[] { "жокей" }, TagString.Parse(db.GetFwVersionById(both)!.Tags));
    }

    [Fact]
    public void RenamingCyrillicTagToOtherCase_ActuallyRenames()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var id = AddVersion(db, 1, "пи");
        db.AddTag("пи");

        // Раньше здесь был молчаливый выход: OrdinalIgnoreCase считал имена одинаковыми.
        var result = db.RenameTag("пи", "ПИ");

        Assert.Equal(Database.TagRenameOutcome.Renamed, result.Outcome);
        Assert.Equal("ПИ", result.Name);
        Assert.Contains("ПИ", db.GetAllTags());
        Assert.DoesNotContain("пи", db.GetAllTags());
        Assert.Equal(new[] { "ПИ" }, TagString.Parse(db.GetFwVersionById(id)!.Tags));
    }

    [Fact]
    public void RenamingCyrillicTagOntoItsOtherCaseTwin_Merges()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        // Обе формы законно живут в базе: COLLATE NOCASE кириллицу не сворачивает.
        var id = AddVersion(db, 1, "пи");
        db.AddTag("пи");
        db.AddTag("ПИ");
        Assert.Equal(2, db.GetAllTags().Count(t => t is "пи" or "ПИ"));

        var result = db.RenameTag("пи", "ПИ");

        Assert.Equal(Database.TagRenameOutcome.Merged, result.Outcome);
        Assert.Single(db.GetAllTags().Where(t => t is "пи" or "ПИ"));
        Assert.Equal(new[] { "ПИ" }, TagString.Parse(db.GetFwVersionById(id)!.Tags));
    }

    [Fact]
    public void RenamingToSameNameChangesNothing()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        db.AddTag("насос");

        Assert.Equal(Database.TagRenameOutcome.Unchanged, db.RenameTag("насос", "насос").Outcome);
        Assert.Equal(Database.TagRenameOutcome.Unchanged, db.RenameTag("насос", "   ").Outcome);
        // Тега нет вовсе — тоже не повод что-то делать.
        Assert.Equal(Database.TagRenameOutcome.Unchanged, db.RenameTag("нет такого", "жокей").Outcome);
        Assert.Contains("насос", db.GetAllTags());
    }
}
