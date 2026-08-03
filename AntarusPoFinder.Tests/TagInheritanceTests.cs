using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Жалоба: «при обновлении прошивки теги не переносятся». Теги описывают ШКАФ («Шкаф
/// управления пожарными насосами АМПЕРУС ПЖ-ПП-2-…»), а не конкретную сборку программы: новая версия
/// ПЛК ставится ровно в те же шкафы. До этого каждая загрузка начинала с чистого листа — программист
/// либо заново набивал десяток названий шкафов, либо (что и происходило) не набивал, и свежая версия
/// переставала находиться по тем запросам, по которым находилась предыдущая.
///
/// Сделано по образцу уже существующего наследования HMI-проекта (HmiInheritanceTests,
/// Database.GetLatestHmiForFirmware): та же пара подтип+контроллер, тот же порядок «последняя живая
/// версия», тот же принцип «унаследованное видно оператору, а не подставляется молча» — здесь это
/// FirmwareUploadResult.InheritedTags/InheritedTagsFromVersion.</summary>
public class TagInheritanceTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public TagInheritanceTests()
    {
        _db = new Database(_dbFile.Path);
        _hierarchy = new HierarchyService(_db);
        _hierarchy.EnsureStructure(Root);
    }

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
        _tempRoot.Dispose();
    }

    private (EquipmentGroup group, EquipmentSubType subtype, ControllerModification mod) Cabinet(
        string groupName = "ТГР", string controller = "SMH5")
    {
        var group = _db.GetAllEquipmentGroups().Single(g => g.Name == groupName);
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).First();
        var mod = _db.GetAllModifications().Single(m => m.ControllerName == controller && m.DisplayName == controller);
        return (group, subtype, mod);
    }

    private string TempPsl()
    {
        var path = Path.Combine(_tempRoot.Path, $"src_{Guid.NewGuid():N}.psl");
        File.WriteAllText(path, "dummy firmware bytes");
        return path;
    }

    private FirmwareUploadResult Upload(EquipmentGroup group, EquipmentSubType subtype, ControllerModification mod,
        params string[] tags)
    {
        var result = FirmwareUploadService.Upload(_db, _hierarchy, new FirmwareUploadRequest
        {
            SourcePath = TempPsl(),
            Group = group,
            Subtype = subtype,
            Modification = mod,
            LaunchTypes = new() { "УПП" },
            Description = "тестовая загрузка",
            IncludeDateInVersion = false,
            RootPath = Root,
            AuthorUserName = "tester",
            Tags = tags.ToList(),
        });
        Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
        return result;
    }

    private List<string> StoredTags(int fwVersionId) => TagString.Parse(_db.GetFwVersionById(fwVersionId)!.Tags);

    private const string Cabinet914 = "Шкаф управления пожарными насосами АМПЕРУС ПЖ-ПП-2-(9-14А)-АВР-FD-Ст";

    /// <summary>Главный случай: новая версия того же шкафа получает теги предыдущей.</summary>
    [Fact]
    public void NewVersionOfSameCabinet_InheritsTagsOfPreviousVersion()
    {
        var (group, subtype, mod) = Cabinet();

        var first = Upload(group, subtype, mod, Cabinet914, "пожарные насосы");
        var second = Upload(group, subtype, mod);

        Assert.Contains(Cabinet914, StoredTags(second.FwVersionId));
        Assert.Contains("пожарные насосы", StoredTags(second.FwVersionId));
        Assert.Equal(first.Record!.VersionRaw, second.InheritedTagsFromVersion);
        Assert.Contains(Cabinet914, second.InheritedTags);
    }

    /// <summary>Наследование не молчаливое: и сами теги, и версия-источник возвращаются вызывающему —
    /// UploadView показывает их и в подсказке ДО загрузки, и в сообщении об успехе.</summary>
    [Fact]
    public void InheritedTags_AreReportedToCaller_NotAppliedSilently()
    {
        var (group, subtype, mod) = Cabinet();
        var first = Upload(group, subtype, mod, Cabinet914);

        // Та же справка, которой пользуется подсказка в форме загрузки.
        var preview = FirmwareUploadService.PreviousVersionTags(_db, subtype.Id!.Value, mod.ControllerId);
        Assert.NotNull(preview);
        Assert.Equal(first.Record!.VersionRaw, preview!.Value.VersionRaw);
        Assert.Contains(Cabinet914, TagString.Parse(preview.Value.Tags));

        var second = Upload(group, subtype, mod);
        Assert.Equal(preview.Value.VersionRaw, second.InheritedTagsFromVersion);
        Assert.Contains(Cabinet914, second.InheritedTags);
    }

    /// <summary>Набранное вручную не перетирается и остаётся первым, унаследованное дописывается в
    /// конец, повторы (без учёта регистра) не задваиваются.</summary>
    [Fact]
    public void ManualTags_WinAndAreNotDuplicated_InheritedGoLast()
    {
        var (group, subtype, mod) = Cabinet();
        Upload(group, subtype, mod, Cabinet914, "пожарные насосы");

        // Тот же тег другим регистром + свой новый.
        var second = Upload(group, subtype, mod, Cabinet914.ToUpperInvariant(), "новый тег");

        var tags = StoredTags(second.FwVersionId);
        Assert.Equal(Cabinet914.ToUpperInvariant(), tags[0]);
        Assert.Equal("новый тег", tags[1]);
        Assert.Single(tags.Where(t => string.Equals(t, Cabinet914, StringComparison.OrdinalIgnoreCase)));
        // Уже введённый вручную тег унаследованным не считается — иначе оператору сообщали бы о
        // «переносе» того, что он только что набрал сам.
        Assert.DoesNotContain(second.InheritedTags, t => string.Equals(t, Cabinet914, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("пожарные насосы", second.InheritedTags);
    }

    /// <summary>Первая версия шкафа наследовать не от чего — список пуст, а автотеги (тип шкафа,
    /// подтип, контроллер) в «унаследованные» не попадают: их добавляет сама загрузка.</summary>
    [Fact]
    public void FirstVersionOfCabinet_InheritsNothing()
    {
        var (group, subtype, mod) = Cabinet();

        var first = Upload(group, subtype, mod, "свой тег");

        Assert.Empty(first.InheritedTags);
        Assert.Equal("", first.InheritedTagsFromVersion);
        Assert.Contains(group.Name, StoredTags(first.FwVersionId));
        Assert.Contains(mod.ControllerName, StoredTags(first.FwVersionId));
    }

    /// <summary>Другой шкаф — другие теги: наследование идёт по паре подтип+контроллер, а не «по всей
    /// базе». Иначе шкаф пожаротушения получил бы теги вентиляции.</summary>
    [Fact]
    public void DifferentCabinet_DoesNotInheritForeignTags()
    {
        var (group, subtype, mod) = Cabinet();
        Upload(group, subtype, mod, Cabinet914);

        var (otherGroup, otherSubtype, otherMod) = Cabinet("НГР", "SMH4");
        var other = Upload(otherGroup, otherSubtype, otherMod);

        Assert.Empty(other.InheritedTags);
        Assert.DoesNotContain(Cabinet914, StoredTags(other.FwVersionId));
    }

    /// <summary>Откатанная версия источником тегов не считается: её теги — как раз то, от чего
    /// отказались. Наследуем от последней ЖИВОЙ версии.</summary>
    [Fact]
    public void RolledBackVersion_IsNotUsedAsTagSource()
    {
        var (group, subtype, mod) = Cabinet();
        var good = Upload(group, subtype, mod, "тег хорошей версии");
        var bad = Upload(group, subtype, mod, "тег забракованной версии");
        Assert.True(_db.RollbackFwVersion(bad.FwVersionId));

        var third = Upload(group, subtype, mod);

        Assert.Contains("тег хорошей версии", StoredTags(third.FwVersionId));
        Assert.DoesNotContain("тег забракованной версии", StoredTags(third.FwVersionId));
        Assert.Equal(good.Record!.VersionRaw, third.InheritedTagsFromVersion);
    }

    /// <summary>Удалённая версия — тем более не источник: она вычеркнута везде (см.
    /// Database.TombstoneFwVersion).</summary>
    [Fact]
    public void DeletedVersion_IsNotUsedAsTagSource()
    {
        var (group, subtype, mod) = Cabinet();
        var first = Upload(group, subtype, mod, "тег удалённой версии");
        // Полное удаление, как его делает Настройки → Прошивки: надгробие в БД плюс файлы с диска
        // (иначе освободившийся номер версии упрётся в уже существующую папку и загрузка спросит
        // подтверждение перезаписи — к наследованию тегов это отношения не имеет).
        _db.TombstoneFwVersion(first.FwVersionId);
        Directory.Delete(first.DestinationFolder!, recursive: true);

        var second = Upload(group, subtype, mod);

        Assert.Empty(second.InheritedTags);
        Assert.DoesNotContain("тег удалённой версии", StoredTags(second.FwVersionId));
    }

    /// <summary>Унаследованные теги считаются в ПЕРВОЙ фазе загрузки (FirmwareUploadService.Prepare),
    /// поэтому попадают и в CHANGELOG.md рядом с прошивкой — а по нему теги доезжают до машин, куда
    /// общий конфиг не отправляется вовсе (см. ChangelogFile и HierarchyService.ImportFwCandidates).
    /// Разъедься эти два места — на соседней машине версия нашлась бы по одному набору тегов, а в
    /// карточке показывала бы другой.</summary>
    [Fact]
    public void InheritedTags_AlsoLandInChangelogOnDisk()
    {
        var (group, subtype, mod) = Cabinet();
        Upload(group, subtype, mod, Cabinet914);

        var second = Upload(group, subtype, mod);

        var changelog = ChangelogFile.TryRead(second.DestinationFolder!);
        Assert.NotNull(changelog);
        Assert.Contains(Cabinet914, changelog!.Tags);
        Assert.Equal(StoredTags(second.FwVersionId).OrderBy(t => t), changelog.Tags.OrderBy(t => t));
    }
}
