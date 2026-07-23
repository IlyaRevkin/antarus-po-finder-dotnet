using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Одна прошивка / один файл параметров, подходящие сразу нескольким подтипам шкафа:
/// запись заводится каждому подтипу, а файлы на диск кладутся ОДИН раз — остальным подтипам ярлык
/// (FirmwareUploadRequest.ExtraSubtypes и ParamFileLinkService). Проверяется именно то, ради чего
/// это делалось: отсутствие второй копии файлов на диске.</summary>
public class ExtraSubtypesLinkTests : IDisposable
{
    /// <summary>Реальный .lnk через COM здесь не нужен (и на CI-агенте без WScript.Shell мог бы
    /// упасть) — важно, что вызов происходит с правильными путями.</summary>
    private sealed class FakeShortcuts : IShortcutCreator
    {
        public List<(string Shortcut, string Target)> Created { get; } = new();
        public Exception? Throw { get; set; }

        public void Create(string shortcutPath, string targetPath, string description)
        {
            if (Throw is not null) throw Throw;
            Created.Add((shortcutPath, targetPath));
        }
    }

    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public ExtraSubtypesLinkTests()
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

    /// <summary>ПЖ — единственная группа сида с несколькими реальными подтипами (2.0/FD/КПЧ/…),
    /// то есть единственная, где эта функция вообще имеет смысл.</summary>
    private (EquipmentGroup group, List<EquipmentSubType> subtypes) SeedPj()
    {
        var group = _db.GetAllEquipmentGroups().Single(g => g.Name == "ПЖ");
        var subtypes = _db.GetSubtypesForGroup(group.Id!.Value);
        Assert.True(subtypes.Count >= 3);
        return (group, subtypes);
    }

    private static string WriteTempFile(string extension, string content = "dummy bytes")
    {
        var path = Path.Combine(Path.GetTempPath(), $"antarus_extra_test_{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, content);
        return path;
    }

    // ── прошивки ─────────────────────────────────────────────────────────────

    [Fact]
    public void Firmware_ExtraSubtypes_CreateRecordsSharingOneCopyOnDisk()
    {
        var (group, subtypes) = SeedPj();
        var mod = _db.GetAllModifications().First(m => m.ControllerName == "SMH5");
        var src = WriteTempFile(".psl");
        var shortcuts = new FakeShortcuts();
        try
        {
            var result = FirmwareUploadService.Upload(_db, _hierarchy, new FirmwareUploadRequest
            {
                SourcePath = src,
                Group = group,
                Subtype = subtypes[0],
                ExtraSubtypes = new List<EquipmentSubType> { subtypes[1], subtypes[2] },
                Modification = mod,
                LaunchTypes = new() { "УПП" },
                Description = "общая для трёх подтипов",
                IncludeDateInVersion = false,
                RootPath = Root,
                AuthorUserName = "tester",
            }, shortcuts);

            Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
            Assert.Equal(2, result.ExtraFwVersionIds.Count);

            // Записи есть у каждого подтипа, номер версии и путь у всех один и тот же.
            foreach (var id in result.ExtraFwVersionIds)
            {
                var extra = _db.GetFwVersionById(id);
                Assert.NotNull(extra);
                Assert.Equal(result.Record!.VersionRaw, extra!.VersionRaw);
                Assert.Equal(result.Record.DiskPath, extra.DiskPath);
                Assert.Equal(result.Record.Filename, extra.Filename);
            }
            Assert.Equal(new[] { subtypes[1].Id, subtypes[2].Id }.OrderBy(x => x),
                         result.ExtraFwVersionIds.Select(id => _db.GetFwVersionById(id)!.SubtypeId).OrderBy(x => x).Cast<int?>());

            // Главное: файл прошивки лежит на диске ровно один раз.
            var copies = Directory.GetFiles(Root, result.DestinationFilename!, SearchOption.AllDirectories);
            Assert.Single(copies);

            // Ярлык — в папке контроллера каждого дополнительного подтипа, на папку основной версии.
            Assert.Equal(2, shortcuts.Created.Count);
            Assert.All(shortcuts.Created, c => Assert.Equal(result.DestinationFolder, c.Target));
            foreach (var extra in new[] { subtypes[1], subtypes[2] })
            {
                var expected = Path.Combine(
                    _hierarchy.ControllerFolder(Root, group.Name, extra.Name, mod.ControllerName),
                    $"{result.Record!.VersionRaw}.lnk");
                Assert.Contains(shortcuts.Created, c => c.Shortcut == expected);
            }
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Firmware_ShortcutFailure_DowngradesToWarningAndKeepsRecords()
    {
        var (group, subtypes) = SeedPj();
        var mod = _db.GetAllModifications().First(m => m.ControllerName == "SMH5");
        var src = WriteTempFile(".psl");
        var shortcuts = new FakeShortcuts { Throw = new InvalidOperationException("WScript.Shell недоступен") };
        try
        {
            var result = FirmwareUploadService.Upload(_db, _hierarchy, new FirmwareUploadRequest
            {
                SourcePath = src,
                Group = group,
                Subtype = subtypes[0],
                ExtraSubtypes = new List<EquipmentSubType> { subtypes[1] },
                Modification = mod,
                LaunchTypes = new() { "УПП" },
                Description = "ярлык не создался",
                IncludeDateInVersion = false,
                RootPath = Root,
                AuthorUserName = "tester",
            }, shortcuts);

            Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
            Assert.Single(result.ExtraFwVersionIds);
            Assert.Contains(result.Warnings, w => w.Contains(subtypes[1].Name) && w.Contains("Ярлык"));
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Firmware_DuplicateAndSelfReferencingExtras_AreIgnored()
    {
        var (group, subtypes) = SeedPj();
        var mod = _db.GetAllModifications().First(m => m.ControllerName == "SMH5");
        var src = WriteTempFile(".psl");
        var shortcuts = new FakeShortcuts();
        try
        {
            var result = FirmwareUploadService.Upload(_db, _hierarchy, new FirmwareUploadRequest
            {
                SourcePath = src,
                Group = group,
                Subtype = subtypes[0],
                // Основной подтип и дубль того же дополнительного — не должны давать лишних записей.
                ExtraSubtypes = new List<EquipmentSubType> { subtypes[0], subtypes[1], subtypes[1] },
                Modification = mod,
                LaunchTypes = new() { "УПП" },
                Description = "дубли в списке",
                IncludeDateInVersion = false,
                RootPath = Root,
                AuthorUserName = "tester",
            }, shortcuts);

            Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
            Assert.Single(result.ExtraFwVersionIds);
            Assert.Single(shortcuts.Created);
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Firmware_NoExtras_BehavesExactlyAsBefore()
    {
        var (group, subtypes) = SeedPj();
        var mod = _db.GetAllModifications().First(m => m.ControllerName == "SMH5");
        var src = WriteTempFile(".psl");
        var shortcuts = new FakeShortcuts();
        try
        {
            var result = FirmwareUploadService.Upload(_db, _hierarchy, new FirmwareUploadRequest
            {
                SourcePath = src,
                Group = group,
                Subtype = subtypes[0],
                Modification = mod,
                LaunchTypes = new() { "УПП" },
                Description = "без дополнительных подтипов",
                IncludeDateInVersion = false,
                RootPath = Root,
                AuthorUserName = "tester",
            }, shortcuts);

            Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
            Assert.Empty(result.ExtraFwVersionIds);
            Assert.Empty(shortcuts.Created);
        }
        finally { File.Delete(src); }
    }

    // ── параметры ПЧ/УПП ─────────────────────────────────────────────────────

    [Fact]
    public void Params_ExtraSubtypes_CreateRecordsSharingOneCopyOnDisk()
    {
        var (group, subtypes) = SeedPj();
        var manuf = _db.GetParamManufacturers().First();
        var dstFolder = _hierarchy.ParamsPath(Root, group.Name, subtypes[0].Name, manuf);
        Directory.CreateDirectory(dstFolder);
        File.WriteAllText(Path.Combine(dstFolder, "params.par"), "parameters");

        var primary = new ParamFile
        {
            SubtypeId = subtypes[0].Id,
            Manufacturer = manuf,
            Filename = "params.par",
            DiskPath = dstFolder,
            Description = "общие параметры",
            UploadDate = "2026-07-23 10:00:00",
        };
        _db.AddParamFile(primary);
        var shortcuts = new FakeShortcuts();

        var link = ParamFileLinkService.LinkToExtraSubtypes(_db, _hierarchy, Root, group, subtypes[0], primary,
            new[] { subtypes[1], subtypes[2], subtypes[0] }, shortcuts);

        Assert.Equal(2, link.CreatedIds.Count);
        Assert.Empty(link.Warnings);

        // Записи у дополнительных подтипов ссылаются на ту же (единственную) копию файла.
        foreach (var extra in new[] { subtypes[1], subtypes[2] })
        {
            var rows = _db.GetParamFiles(extra.Id);
            var row = Assert.Single(rows);
            Assert.Equal(dstFolder, row.DiskPath);
            Assert.Equal("params.par", row.Filename);
        }
        Assert.Single(Directory.GetFiles(Root, "params.par", SearchOption.AllDirectories));

        Assert.Equal(2, shortcuts.Created.Count);
        Assert.All(shortcuts.Created, c => Assert.Equal(Path.Combine(dstFolder, "params.par"), c.Target));
    }

    [Fact]
    public void Params_ShortcutFailure_DowngradesToWarningAndKeepsRecords()
    {
        var (group, subtypes) = SeedPj();
        var manuf = _db.GetParamManufacturers().First();
        var dstFolder = _hierarchy.ParamsPath(Root, group.Name, subtypes[0].Name, manuf);
        Directory.CreateDirectory(dstFolder);

        var primary = new ParamFile
        {
            SubtypeId = subtypes[0].Id,
            Manufacturer = manuf,
            Filename = "params.par",
            DiskPath = dstFolder,
            UploadDate = "2026-07-23 10:00:00",
        };
        _db.AddParamFile(primary);

        var link = ParamFileLinkService.LinkToExtraSubtypes(_db, _hierarchy, Root, group, subtypes[0], primary,
            new[] { subtypes[1] }, new FakeShortcuts { Throw = new IOException("сеть недоступна") });

        Assert.Single(link.CreatedIds);
        Assert.Contains(link.Warnings, w => w.Contains(subtypes[1].Name));
    }
}
