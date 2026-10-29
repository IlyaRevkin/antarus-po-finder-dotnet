using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>КОРОТКИЙ РЕДАКТОР ТЕГОВ НЕ ИМЕЕТ ПРАВА ТРОГАТЬ ЧУЖИЕ ПОЛЯ.
///
/// У строки прошивки теперь ДВЕ точки входа на запись: полное окно модерации (EditFirmwareDialog,
/// пишет описание, теги, типы пуска и подсказки разом) и короткий редактор тегов (EditFwTagsDialog).
/// Ровно здесь и живёт опасность: если короткий редактор записал бы ещё и описание — своей копией,
/// прочитанной при открытии карточки, — то правка описания, сделанная в модерации между открытием и
/// сохранением, молча откатилась бы к устаревшему снимку. Такое не замечают неделями.
///
/// Защита — в <c>Database.UpdateFwVersion</c>: параметр null означает «не трогать это поле», SQL
/// собирается только из переданных. Короткий редактор зовёт его одним именованным аргументом
/// <c>tags:</c>. Проверяем здесь, а не глазами в окне: правило держится соглашением о вызове, и
/// первая же «безобидная» правка сигнатуры его сломает.</summary>
public class FwTagsOnlyEditTests
{
    private static int SeedFirmware(Database db, string tags, string description, List<string> launchTypes)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");

        var record = new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion,
            SwVersion = 1,
            DtStr = "20260801_1200",
            VersionRaw = "1.99.7.1.20260801_1200",
            Filename = "fw.psl",
            DiskPath = "",
            Description = description,
            Changelog = description,
            LaunchTypes = launchTypes,
            Status = "active",
            Tags = tags,
        };
        var id = db.AddFwVersion(record);
        Assert.True(id > 0);
        return id;
    }

    /// <summary>Сохранение тегов коротким редактором: теги новые, описание и типы пуска — ровно те,
    /// что лежали в базе, даже если их поменяли уже после того, как редактор открыли.</summary>
    [Fact]
    public void SavingTagsOnly_LeavesDescriptionAndLaunchTypesAlone()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var id = SeedFirmware(db, TagString.Join(new[] { "ЩУН-3" }),
            "описание как было", new List<string> { "прямой" });

        // Пока короткий редактор тегов открыт, описание и типы пуска поменяли из окна модерации.
        db.UpdateFwVersion(id, description: "описание, поправленное в модерации",
            launchTypes: new List<string> { "частотный" });

        // Короткий редактор сохраняется — ОДНИМ параметром tags, как это делает EditFwTagsDialog.Apply.
        db.UpdateFwVersion(id, tags: TagString.Join(new[] { "ЩУН-3", "жокей" }));

        var after = db.GetFwVersionById(id)!;
        Assert.Equal(new[] { "ЩУН-3", "жокей" }, TagString.Parse(after.Tags).ToArray());
        Assert.Equal("описание, поправленное в модерации", after.Description);
        Assert.Equal(new[] { "частотный" }, after.LaunchTypes.ToArray());
    }

    /// <summary>Обратная половина: модерация, сохранённая после правки тегов, теги не теряет — она
    /// передаёт свой набор явно, и он к этому моменту уже прочитан из базы вызывающим кодом.
    /// Проверяем, что отметка о снятии тега, которую ставит UpdateFwVersion, не мешает обычной записи
    /// остальных полей.</summary>
    [Fact]
    public void ModerationSave_AfterTagsOnlyEdit_KeepsTheNewTags()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var id = SeedFirmware(db, TagString.Join(new[] { "ЩУН-3" }),
            "описание как было", new List<string> { "прямой" });

        db.UpdateFwVersion(id, tags: TagString.Join(new[] { "ЩУН-3", "жокей" }));

        var reread = db.GetFwVersionById(id)!;
        db.UpdateFwVersion(id, description: "новое описание", tags: reread.Tags,
            launchTypes: new List<string> { "частотный" });

        var after = db.GetFwVersionById(id)!;
        Assert.Equal(new[] { "ЩУН-3", "жокей" }, TagString.Parse(after.Tags).ToArray());
        Assert.Equal("новое описание", after.Description);
    }
}
