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
/// Модерация правит его через FirmwareSubtypeLinkService.Apply: отметил подтип — завелась запись и
/// ярлык, снял — запись помечена удалённой и ярлык убран.
///
/// Главное, что здесь проверяется, — файлы прошивки на диске лежат ОДИН раз и общие для всех этих
/// записей, поэтому отвязка подтипа не имеет права их трогать: ни на этой машине, ни на соседней,
/// куда tombstone приезжает синхронизацией.</summary>
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
    public void AddSubtype_CreatesRecordAndShortcut_WithoutCopyingFilesOnDisk()
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

        // Запись у нового подтипа — та же версия и те же файлы, второй копии на диске не появилось.
        var linked = _db.GetFwVersions(subtypes[1].Id).Single();
        Assert.Equal(primary.VersionRaw, linked.VersionRaw);
        Assert.Equal(primary.DiskPath, linked.DiskPath);
        Assert.Single(Directory.GetFiles(Root, uploaded.DestinationFilename!, SearchOption.AllDirectories));

        // Ярлык — в папке контроллера этого подтипа, на папку версии основного подтипа.
        var expected = Path.Combine(
            _hierarchy.ControllerFolder(Root, group.Name, subtypes[1].Name, mod.ControllerName),
            $"{primary.VersionRaw}.lnk");
        Assert.True(File.Exists(expected));
        Assert.Contains(_shortcuts.Created, c => c.Shortcut == expected && c.Target == primary.DiskPath);
    }

    [Fact]
    public void RemoveSubtype_TombstonesRecordAndShortcut_ButKeepsFirmwareFilesOnDisk()
    {
        var (group, subtypes, mod) = SeedPj();
        var uploaded = UploadPrimary(group, subtypes[0], mod, new[] { subtypes[1] });
        var primary = uploaded.Record!;
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
    public void Apply_IsIdempotent_SecondRunWithSameSetChangesNothing()
    {
        var (group, subtypes, mod) = SeedPj();
        var primary = UploadPrimary(group, subtypes[0], mod).Record!;

        Assert.True(Apply(group, mod, primary, subtypes, new[] { subtypes[1] }).Changed);
        var again = Apply(group, mod, primary, subtypes, new[] { subtypes[1] });

        Assert.False(again.Changed);
        Assert.Single(_db.GetFwVersions(subtypes[1].Id));
    }

    [Fact]
    public void CurrentLinks_ListsEverySubtypeTheFirmwareIsVisibleUnder_MarkingThePrimary()
    {
        var (group, subtypes, mod) = SeedPj();
        var primary = UploadPrimary(group, subtypes[0], mod, new[] { subtypes[1] }).Record!;

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
        var primary = UploadPrimary(group, subtypes[0], mod, new[] { subtypes[1] }).Record!;
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
        var uploaded = UploadPrimary(group, subtypes[0], mod, new[] { subtypes[1] });
        var primary = uploaded.Record!;

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
}
