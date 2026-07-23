using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Закрывает жалобу «прошивки, добавленные на другом компе, появляются с описанием
/// "(синхронизировано с диска)" вместо того, что я написал». Два независимых пути, которыми строка
/// fw_versions попадает на вторую машину, и оба раньше теряли описание:
///   1. сканирование диска (HierarchyService.SyncFwFromDisk) — видело только имена папок;
///   2. config-обмен — additive-only, уже существующую локальную строку с заглушкой не трогал.
/// Теперь (1) читает CHANGELOG.md, который записала загрузившая машина, а (2) считает заглушку
/// «пустым» описанием и разрешает входящему настоящему её перезаписать.</summary>
public class ChangelogFileTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public ChangelogFileTests()
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

    // ── чистый разбор файла ──────────────────────────────────────────────────

    [Fact]
    public void WriteThenRead_RoundTripsDescriptionAndLaunchTypes()
    {
        var folder = Path.Combine(Root, "changelog_roundtrip");
        Directory.CreateDirectory(folder);
        var fwv = FwVersionNumber.Build(1, 2, 3, 4, includeDate: false);

        ChangelogFile.Write(folder, fwv, new[] { "УПП", "ПЧ" }, "первая строка\nвторая строка");
        var read = ChangelogFile.TryRead(folder);

        Assert.NotNull(read);
        Assert.Equal("первая строка\nвторая строка", read!.Description);
        Assert.Equal(new[] { "УПП", "ПЧ" }, read.LaunchTypes);
    }

    [Fact]
    public void TryRead_NoFile_ReturnsNull()
    {
        var folder = Path.Combine(Root, "changelog_missing");
        Directory.CreateDirectory(folder);

        Assert.Null(ChangelogFile.TryRead(folder));
    }

    [Fact]
    public void TryRead_HeaderOnlyNoDescription_ReturnsEmptyDescriptionNotHeaderText()
    {
        var folder = Path.Combine(Root, "changelog_headeronly");
        Directory.CreateDirectory(folder);
        var fwv = FwVersionNumber.Build(1, 2, 3, 4, includeDate: false);

        ChangelogFile.Write(folder, fwv, new[] { "УПП" }, "");
        var read = ChangelogFile.TryRead(folder);

        Assert.NotNull(read);
        Assert.Equal("", read!.Description);
        Assert.Equal(new[] { "УПП" }, read.LaunchTypes);
    }

    // ── путь 1: сканирование диска ───────────────────────────────────────────

    /// <summary>Полный сценарий «загрузил на компе A — увидел на компе B»: машина A делает настоящую
    /// загрузку в общий корень, у машины B своя пустая база, и она поднимает ту же версию с диска.</summary>
    [Fact]
    public void SyncFwFromDisk_UsesDescriptionFromChangelogWrittenByUpload()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var src = WriteTempFile(".psl");
        try
        {
            var upload = FirmwareUploadService.Upload(_db, _hierarchy, new FirmwareUploadRequest
            {
                SourcePath = src,
                Group = group,
                Subtype = subtype,
                Modification = mod,
                LaunchTypes = new() { "УПП", "ПЧ" },
                Description = "поправлена уставка давления",
                IncludeDateInVersion = false,
                RootPath = Root,
                AuthorUserName = "machineA",
            });
            Assert.True(upload.IsSuccess);

            using var otherDbFile = new TempDb();
            using var otherDb = new Database(otherDbFile.Path);
            var otherHierarchy = new HierarchyService(otherDb);

            var result = otherHierarchy.SyncFwFromDisk(Root);

            Assert.True(result.Ok);
            var synced = otherDb.GetFwVersions(includeArchived: true, includeRolledBack: true).Single(v => v.VersionRaw == upload.Record!.VersionRaw);
            Assert.Equal("поправлена уставка давления", synced.Description);
            Assert.Equal(new[] { "УПП", "ПЧ" }, synced.LaunchTypes);
        }
        finally { File.Delete(src); }
    }

    /// <summary>Папка версии без CHANGELOG.md (например, положенная на диск руками) — заглушка
    /// остаётся, иначе описание было бы просто пустым и в списке версий выглядело бы как потерянное.</summary>
    [Fact]
    public void SyncFwFromDisk_NoChangelog_KeepsPlaceholderDescription()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var fwv = FwVersionNumber.Build(group.Prefix, subtype.Prefix, mod.HwVersion, 77, includeDate: false);
        var versionDir = _hierarchy.FwPath(Root, group.Name, subtype.Name, mod.ControllerName, fwv.Raw, isOpc: false);
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "handmade.psl"), "x");

        var result = _hierarchy.SyncFwFromDisk(Root);

        Assert.True(result.Ok);
        var synced = _db.GetFwVersions(includeArchived: true, includeRolledBack: true).Single(v => v.VersionRaw == fwv.Raw);
        Assert.Equal(ChangelogFile.DiskSyncPlaceholder, synced.Description);
        Assert.Equal("handmade.psl", synced.Filename);
    }

    /// <summary>CHANGELOG.md лежит в папке первым по алфавиту — filename должен указывать на саму
    /// прошивку, а не на служебный файл.</summary>
    [Fact]
    public void SyncFwFromDisk_SkipsChangelogWhenPickingFilename()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var fwv = FwVersionNumber.Build(group.Prefix, subtype.Prefix, mod.HwVersion, 78, includeDate: false);
        var versionDir = _hierarchy.FwPath(Root, group.Name, subtype.Name, mod.ControllerName, fwv.Raw, isOpc: false);
        Directory.CreateDirectory(versionDir);
        ChangelogFile.Write(versionDir, fwv, new[] { "УПП" }, "описание из файла");
        File.WriteAllText(Path.Combine(versionDir, "zzz.psl"), "x");

        _hierarchy.SyncFwFromDisk(Root);

        var synced = _db.GetFwVersions(includeArchived: true, includeRolledBack: true).Single(v => v.VersionRaw == fwv.Raw);
        Assert.Equal("zzz.psl", synced.Filename);
        Assert.Equal("описание из файла", synced.Description);
    }

    // ── путь 2: config-обмен ─────────────────────────────────────────────────

    [Fact]
    public void ImportHierarchyData_OverwritesDiskSyncPlaceholderWithRealDescription()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        m.HierA.EnsureStructure(m.Root.Path);

        var upload = UploadOn(m.DbA, m.HierA, m.Root.Path, "настоящее описание", new() { "УПП" });

        // Машина B сначала подняла ту же папку с диска ДО того, как до неё дошёл конфиг — но без
        // CHANGELOG.md (удаляем его, чтобы получить именно заглушку и проверить её перезапись).
        File.Delete(Path.Combine(upload.DestinationFolder!, ChangelogFile.FileName));
        m.HierB.SyncFwFromDisk(m.Root.Path);
        var before = m.DbB.GetFwVersions(includeArchived: true, includeRolledBack: true).Single(v => v.VersionRaw == upload.Record!.VersionRaw);
        Assert.Equal(ChangelogFile.DiskSyncPlaceholder, before.Description);

        m.DbB.ImportHierarchyData(m.DbA.ExportHierarchyData());

        var after = m.DbB.GetFwVersions(includeArchived: true, includeRolledBack: true).Single(v => v.VersionRaw == upload.Record!.VersionRaw);
        Assert.Equal("настоящее описание", after.Description);
        Assert.Equal(new[] { "УПП" }, after.LaunchTypes);
    }

    [Fact]
    public void ImportHierarchyData_DoesNotOverwriteRealLocalDescription()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        m.HierA.EnsureStructure(m.Root.Path);

        var upload = UploadOn(m.DbA, m.HierA, m.Root.Path, "описание с машины A", new() { "УПП" });
        m.HierB.SyncFwFromDisk(m.Root.Path);

        var local = m.DbB.GetFwVersions(includeArchived: true, includeRolledBack: true).Single(v => v.VersionRaw == upload.Record!.VersionRaw);
        m.DbB.UpdateFwVersion(local.Id!.Value, description: "правка руками на машине B");

        m.DbB.ImportHierarchyData(m.DbA.ExportHierarchyData());

        var after = m.DbB.GetFwVersions(includeArchived: true, includeRolledBack: true).Single(v => v.VersionRaw == upload.Record!.VersionRaw);
        Assert.Equal("правка руками на машине B", after.Description);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private (EquipmentGroup group, EquipmentSubType subtype, ControllerModification mod) SeedTgrSmh5() => SeedTgrSmh5(_db);

    private static (EquipmentGroup group, EquipmentSubType subtype, ControllerModification mod) SeedTgrSmh5(Database db)
    {
        var group = db.GetAllEquipmentGroups().Single(g => g.Name == "ТГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).Single();
        var mod = db.GetAllModifications().Single(m => m.ControllerName == "SMH5" && m.DisplayName == "SMH5");
        return (group, subtype, mod);
    }

    private static FirmwareUploadResult UploadOn(Database db, HierarchyService hierarchy, string root,
        string description, System.Collections.Generic.List<string> launchTypes)
    {
        var (group, subtype, mod) = SeedTgrSmh5(db);
        var src = WriteTempFile(".psl");
        try
        {
            var result = FirmwareUploadService.Upload(db, hierarchy, new FirmwareUploadRequest
            {
                SourcePath = src,
                Group = group,
                Subtype = subtype,
                Modification = mod,
                LaunchTypes = launchTypes,
                Description = description,
                IncludeDateInVersion = false,
                RootPath = root,
                AuthorUserName = "tester",
            });
            Assert.True(result.IsSuccess);
            return result;
        }
        finally { File.Delete(src); }
    }

    private static string WriteTempFile(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"antarus_changelog_test_{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, "dummy firmware bytes");
        return path;
    }
}
