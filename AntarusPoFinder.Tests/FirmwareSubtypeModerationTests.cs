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

/// <summary>Набор подтипов шкафов у УЖЕ загруженной прошивки — то, что раньше задавалось только в
/// момент загрузки (FirmwareUploadRequest.ExtraSubtypes) и переделывалось только повторной заливкой.
/// Модерация правит его через FirmwareSubtypeLinkService.Apply: отметил подтип — прошивка в него
/// СКОПИРОВАЛАСЬ (своя папка, свои файлы, свой номер с его префиксом).
///
/// Прежние записи-ссылки (общий disk_path плюс ярлык) программа больше не создаёт, но на дисках их
/// накоплено много, и всё, что с ними работает, обязано работать как раньше: снять такую галочку
/// можно, а файлы при этом не трогаются — ни на этой машине, ни на соседней, куда tombstone
/// приезжает синхронизацией. Такие связки тесты заводят руками (TestHelpers.LegacySubtypeLink).</summary>
public class FirmwareSubtypeModerationTests : IDisposable
{
    private sealed class FakeShortcuts : IShortcutCreator
    {
        public List<(string Shortcut, string Target)> Created { get; } = new();

        /// <summary>В отличие от ExtraSubtypesLinkTests здесь ярлык кладётся на диск по-настоящему
        /// (пустым файлом): проверяется именно то, что отвязка подтипа его убирает.</summary>
        public void Create(string shortcutPath, string targetPath, string description)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
            File.WriteAllText(shortcutPath, targetPath);
            Created.Add((shortcutPath, targetPath));
        }
    }

    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private readonly FakeShortcuts _shortcuts = new();
    private string Root => _tempRoot.Path;

    public FirmwareSubtypeModerationTests()
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

    private (EquipmentGroup Group, List<EquipmentSubType> Subtypes, ControllerModification Mod) SeedPj()
    {
        var group = _db.GetAllEquipmentGroups().Single(g => g.Name == "ПЖ");
        var subtypes = _db.GetSubtypesForGroup(group.Id!.Value);
        Assert.True(subtypes.Count >= 3);
        return (group, subtypes, _db.GetAllModifications().First(m => m.ControllerName == "SMH5"));
    }

    /// <summary>Загружает прошивку основному подтипу (без доп. подтипов) — исходное состояние, из
    /// которого модерация и правит набор.</summary>
    private FirmwareUploadResult UploadPrimary(EquipmentGroup group, EquipmentSubType subtype,
        ControllerModification mod, IEnumerable<EquipmentSubType>? extras = null)
    {
        var src = Path.Combine(Path.GetTempPath(), $"antarus_moder_test_{Guid.NewGuid():N}.psl");
        File.WriteAllText(src, "dummy bytes");
        try
        {
            var result = FirmwareUploadService.Upload(_db, _hierarchy, new FirmwareUploadRequest
            {
                SourcePath = src,
                Group = group,
                Subtype = subtype,
                ExtraSubtypes = extras?.ToList() ?? new List<EquipmentSubType>(),
                Modification = mod,
                LaunchTypes = new() { "УПП" },
                Description = "прошивка для правки подтипов",
                IncludeDateInVersion = false,
                RootPath = Root,
                AuthorUserName = "tester",
            }, _shortcuts);
            Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
            return result;
        }
        finally { File.Delete(src); }
    }

    private FirmwareSubtypeLinkService.ApplyResult Apply(EquipmentGroup group, ControllerModification mod,
        FwVersionRecord primary, List<EquipmentSubType> groupSubtypes, IEnumerable<EquipmentSubType> desired) =>
        FirmwareSubtypeLinkService.Apply(_db, _hierarchy, Root, primary, group.Name, mod.ControllerName,
            groupSubtypes, desired.Select(s => s.Id!.Value).ToList(), _shortcuts);

    [Fact]
    public void AddSubtype_CopiesFirmwareIntoItsOwnFolderWithItsOwnNumber()
    {
        var (group, subtypes, mod) = SeedPj();
        var uploaded = UploadPrimary(group, subtypes[0], mod);
        var primary = uploaded.Record!;
        _shortcuts.Created.Clear();

        var result = Apply(group, mod, primary, subtypes, new[] { subtypes[1] });

        Assert.True(result.Changed);
        Assert.Empty(result.Removed);
        Assert.Empty(result.Warnings);
        Assert.Single(result.Added);

        // Своя папка, свой номер, своя настоящая копия файла.
        var copy = _db.GetFwVersions(subtypes[1].Id).Single();
        Assert.NotEqual(primary.VersionRaw, copy.VersionRaw);
        Assert.NotEqual(primary.DiskPath, copy.DiskPath);
        Assert.Equal(subtypes[1].Prefix, copy.SubPrefix);
        Assert.Equal(_hierarchy.FwPath(Root, group.Name, subtypes[1].Name, mod.ControllerName, copy.VersionRaw),
            copy.DiskPath);
        // Раскладка (файл в корне версии или в «Прошивка») тут не проверяется — она наследуется от
        // исходной папки, а её задаёт загрузка; важно, что файл в копии есть и назван по её версии.
        Assert.Single(Directory.GetFiles(copy.DiskPath, copy.Filename, SearchOption.AllDirectories));

        // Ни ярлыков, ни файла с чужим именем.
        Assert.Empty(_shortcuts.Created);
        Assert.Empty(Directory.GetFiles(Root, "*.lnk", SearchOption.AllDirectories));
        Assert.Single(Directory.GetFiles(Root, uploaded.DestinationFilename!, SearchOption.AllDirectories));
    }

    /// <summary>Повторно предлагать подтип, у которого копия уже есть, нельзя: получилась бы вторая
    /// копия той же сборки под тем же шкафом. Родство хранится в fw_versions.copy_of.</summary>
    [Fact]
    public void Coverage_ShowsTheCopyAsAnOwnVersion_AndApplyDoesNotDuplicateIt()
    {
        var (group, subtypes, mod) = SeedPj();
        var primary = UploadPrimary(group, subtypes[0], mod).Record!;
        Apply(group, mod, primary, subtypes, new[] { subtypes[1] });

        var coverage = FirmwareSubtypeLinkService.Coverage(_db, primary);
        var copy = Assert.Single(coverage, c => c.SubtypeId == subtypes[1].Id);
        Assert.True(copy.IsOwnVersion);
        Assert.Equal(_db.GetFwVersions(subtypes[1].Id).Single().VersionRaw, copy.VersionRaw);

        var again = Apply(group, mod, primary, subtypes, new[] { subtypes[1] });
        Assert.False(again.Changed);
        Assert.Single(_db.GetFwVersions(subtypes[1].Id));
    }

    /// <summary>Копия — самостоятельная версия, и снять её этой галочкой нельзя: файлы у неё свои,
    /// «отвязка» унесла бы запись, оставив папку сиротой.</summary>
    [Fact]
    public void RemovingASubtypeThatHasItsOwnCopy_DoesNothing()
    {
        var (group, subtypes, mod) = SeedPj();
        var primary = UploadPrimary(group, subtypes[0], mod).Record!;
        Apply(group, mod, primary, subtypes, new[] { subtypes[1] });

        var result = Apply(group, mod, primary, subtypes, Array.Empty<EquipmentSubType>());

        Assert.False(result.Changed);
        Assert.Single(_db.GetFwVersions(subtypes[1].Id));
    }

    [Fact]
    public void RemoveSubtype_TombstonesRecordAndShortcut_ButKeepsFirmwareFilesOnDisk()
    {
        var (group, subtypes, mod) = SeedPj();
        var uploaded = UploadPrimary(group, subtypes[0], mod);
        var primary = uploaded.Record!;
        LegacySubtypeLink.Create(_db, _hierarchy, Root, primary, subtypes[1], group.Name, mod.ControllerName, _shortcuts);
        var shortcut = Path.Combine(
            _hierarchy.ControllerFolder(Root, group.Name, subtypes[1].Name, mod.ControllerName),
            $"{primary.VersionRaw}.lnk");
        Assert.True(File.Exists(shortcut));

        // Оставляем только основной подтип.
        var result = Apply(group, mod, primary, subtypes, Array.Empty<EquipmentSubType>());

        Assert.True(result.Changed);
        Assert.Empty(result.Added);
        Assert.Single(result.Removed);
        Assert.Empty(_db.GetFwVersions(subtypes[1].Id));
        Assert.False(File.Exists(shortcut));

        // Сама прошивка цела: и запись основного подтипа, и файлы.
        Assert.NotNull(_db.GetFwVersionById(primary.Id!.Value));
        Assert.True(Directory.Exists(primary.DiskPath));
        Assert.Single(Directory.GetFiles(Root, uploaded.DestinationFilename!, SearchOption.AllDirectories));
    }

    [Fact]
    public void PrimarySubtype_IsNeverUnlinked_EvenWhenAbsentFromDesiredSet()
    {
        var (group, subtypes, mod) = SeedPj();
        var primary = UploadPrimary(group, subtypes[0], mod).Record!;

        // Пустой список желаемых подтипов — основной обязан выжить: это сама прошивка, а не ссылка.
        var result = Apply(group, mod, primary, subtypes, Array.Empty<EquipmentSubType>());

        Assert.False(result.Changed);
        Assert.NotNull(_db.GetFwVersionById(primary.Id!.Value));
        Assert.Single(_db.GetFwVersions(subtypes[0].Id));
    }

    [Fact]
    public void AddSubtype_ToReleasedFirmware_DoesNotPutCopyBackIntoModeration()
    {
        var (group, subtypes, mod) = SeedPj();
        var primary = UploadPrimary(group, subtypes[0], mod).Record!;
        _db.MarkFwVersionReleased(primary.Id!.Value);
        var released = _db.GetFwVersionById(primary.Id.Value)!;
        var beforeQueue = _db.GetUnreleasedFwVersionsCount();

        Apply(group, mod, released, subtypes, new[] { subtypes[1] });

        // Это та же самая, давно выпущенная прошивка — проверять в «Модерации» нечего.
        Assert.Equal(beforeQueue, _db.GetUnreleasedFwVersionsCount());
    }

    [Fact]
    public void CurrentLinks_ListsEverySubtypeTheFirmwareIsVisibleUnder_MarkingThePrimary()
    {
        var (group, subtypes, mod) = SeedPj();
        var primary = UploadPrimary(group, subtypes[0], mod).Record!;
        LegacySubtypeLink.Create(_db, _hierarchy, Root, primary, subtypes[1], group.Name, mod.ControllerName);

        var links = FirmwareSubtypeLinkService.CurrentLinks(_db, primary);

        Assert.Equal(2, links.Count);
        Assert.Single(links, l => l.IsPrimary && l.SubtypeId == subtypes[0].Id);
        Assert.Single(links, l => !l.IsPrimary && l.SubtypeId == subtypes[1].Id);
    }

    // ── защита общих файлов ──────────────────────────────────────────────────

    /// <summary>Прямой тест того, на что опираются оба места, где файлы реально удаляются
    /// (SettingsView.DeleteFirmware_Click и зеркалирование tombstone в ImportHierarchyDataCore).</summary>
    [Fact]
    public void IsDiskPathSharedByOtherVersions_TrueWhileLinkExists_FalseAfterItIsRemoved()
    {
        var (group, subtypes, mod) = SeedPj();
        var primary = UploadPrimary(group, subtypes[0], mod).Record!;
        LegacySubtypeLink.Create(_db, _hierarchy, Root, primary, subtypes[1], group.Name, mod.ControllerName, _shortcuts);
        var link = _db.GetFwVersions(subtypes[1].Id).Single();

        Assert.True(_db.IsDiskPathSharedByOtherVersions(primary.DiskPath, primary.Id!.Value));
        Assert.True(_db.IsDiskPathSharedByOtherVersions(link.DiskPath, link.Id!.Value));

        Apply(group, mod, primary, subtypes, Array.Empty<EquipmentSubType>());

        // Осталась одна запись — файлы больше ни с кем не общие, обычное удаление их унесёт.
        Assert.False(_db.IsDiskPathSharedByOtherVersions(primary.DiskPath, primary.Id.Value));
    }

    [Fact]
    public void IsDiskPathSharedByOtherVersions_EmptyDiskPath_NeverCountsAsShared()
    {
        // Записи без файлов на диске не связаны между собой — иначе «общими» стали бы все разом.
        Assert.False(_db.IsDiskPathSharedByOtherVersions("", 1));
        Assert.False(_db.IsDiskPathSharedByOtherVersions("   ", 1));
    }

    /// <summary>Регрессия, ради которой всё это и написано: отвязка лишнего подтипа приезжает на
    /// соседнюю машину обычным tombstone'ом fw_versions, а он ТАМ удаляет ещё и файлы с диска. Диск
    /// сетевой и общий — без проверки «а не ссылается ли на эти файлы кто-то ещё» безобидная правка
    /// набора подтипов уносила бы саму прошивку у всех.</summary>
    [Fact]
    public void UnlinkingSubtype_SyncedToAnotherMachine_DoesNotDeleteTheSharedFirmwareFolder()
    {
        var (group, subtypes, mod) = SeedPj();
        var uploaded = UploadPrimary(group, subtypes[0], mod);
        var primary = uploaded.Record!;
        LegacySubtypeLink.Create(_db, _hierarchy, Root, primary, subtypes[1], group.Name, mod.ControllerName, _shortcuts);

        using var otherDbFile = new TempDb();
        using var other = new Database(otherDbFile.Path);
        other.ImportHierarchyData(_db.ExportHierarchyData());
        Assert.Single(other.GetFwVersions(other.GetSubtypesForGroup(
            other.GetAllEquipmentGroups().Single(g => g.Name == "ПЖ").Id!.Value)
            .Single(s => s.Name == subtypes[1].Name).Id));

        // Машина A: убрали лишний подтип; файлы на общем диске трогать нельзя.
        Apply(group, mod, primary, subtypes, Array.Empty<EquipmentSubType>());
        other.ImportHierarchyData(_db.ExportHierarchyData());

        var otherSubtypes = other.GetSubtypesForGroup(
            other.GetAllEquipmentGroups().Single(g => g.Name == "ПЖ").Id!.Value);
        // Ссылка уехала...
        Assert.Empty(other.GetFwVersions(otherSubtypes.Single(s => s.Name == subtypes[1].Name).Id));
        // ...а сама прошивка и её файлы — на месте.
        Assert.Single(other.GetFwVersions(otherSubtypes.Single(s => s.Name == subtypes[0].Name).Id));
        Assert.True(Directory.Exists(primary.DiskPath));
        Assert.Single(Directory.GetFiles(Root, uploaded.DestinationFilename!, SearchOption.AllDirectories));
    }

    /// <summary>Обратная сторона той же проверки: когда запись на эти файлы осталась последней,
    /// зеркалирование удаления обязано унести и файлы — иначе «удалил прошивку» перестало бы работать.</summary>
    [Fact]
    public void DeletingTheLastVersionForFolder_StillRemovesFilesOnTheOtherMachine()
    {
        var (group, subtypes, mod) = SeedPj();
        var uploaded = UploadPrimary(group, subtypes[0], mod);
        var primary = uploaded.Record!;

        using var otherDbFile = new TempDb();
        using var other = new Database(otherDbFile.Path);
        other.ImportHierarchyData(_db.ExportHierarchyData());

        _db.TombstoneFwVersion(primary.Id!.Value);
        other.ImportHierarchyData(_db.ExportHierarchyData());

        Assert.False(Directory.Exists(primary.DiskPath));
    }

    /// <summary>Жалоба оператора: «когда я в модерации добавляю ещё один подтип, мне эта прошивка
    /// опять на модерацию прилетает». Причина — копия заводилась в том же диалоге, ДО ответа
    /// «вывести из модерации», то есть у ещё не выпущенной версии, и оставалась released = 0.</summary>
    [Fact]
    public void ReleasingFromModeration_AlsoReleasesTheSubtypeCopiesOfTheSameFirmware()
    {
        var (group, subtypes, mod) = SeedPj();
        var primary = UploadPrimary(group, subtypes[0], mod).Record!;

        // Ровно тот порядок, что в окне модерации: сначала отметили подтип, потом подтвердили релиз.
        Apply(group, mod, primary, subtypes, new[] { subtypes[1] });
        Assert.Equal(2, _db.GetUnreleasedFwVersionsWithNames().Count);

        _db.MarkFwVersionReleasedWithLinked(primary.Id!.Value);

        Assert.Empty(_db.GetUnreleasedFwVersionsWithNames());
        Assert.Equal(0, _db.GetUnreleasedFwVersionsCount());
    }

    /// <summary>«Замененные и откаченные в модерации смысла отображать нет»: как только под тем же
    /// шкафом/контроллером/hw появилась версия свежее, старая из очереди модерации уходит — размечать
    /// теги у версии, которую уже никто не поставит, незачем. Счётчик бейджа обязан совпадать со
    /// списком, иначе «Модерация (2)» открывается и показывает одну строку.</summary>
    [Fact]
    public void SupersededVersion_DropsOutOfModerationQueueAndItsCount()
    {
        var (group, subtypes, mod) = SeedPj();
        var older = UploadPrimary(group, subtypes[0], mod).Record!;
        Assert.Single(_db.GetUnreleasedFwVersionsWithNames());

        var newer = UploadPrimary(group, subtypes[0], mod).Record!;
        Assert.True(newer.SwVersion > older.SwVersion);

        var queue = _db.GetUnreleasedFwVersionsWithNames();
        Assert.Equal(newer.Id, Assert.Single(queue).Id);
        Assert.Equal(1, _db.GetUnreleasedFwVersionsCount());
    }
}
