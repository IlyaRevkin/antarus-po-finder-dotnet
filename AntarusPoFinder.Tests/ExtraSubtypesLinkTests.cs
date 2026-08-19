using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Одна прошивка / один файл параметров, подходящие сразу нескольким подтипам шкафа.
///
/// У ПРОШИВОК ярлыков больше нет: каждый отмеченный подтип получает свою папку, настоящую копию
/// файлов и свой номер версии — с префиксом своего подтипа (FirmwareSubtypeLinkService). У ФАЙЛОВ
/// ПАРАМЕТРОВ прежний порядок сохранён: файл один, остальным подтипам ярлык (ParamFileLinkService) —
/// про них разговора не было, а параметры и правда один и тот же файл, а не сборка под шкаф.</summary>
public class ExtraSubtypesLinkTests : IDisposable
{
    /// <summary>Реальный .lnk через COM здесь не нужен (и на сборочной машине без WScript.Shell мог бы
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
    public void Firmware_ExtraSubtypes_GetTheirOwnFolderCopyAndNumber()
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
                Description = "одна прошивка на три шкафа",
                IncludeDateInVersion = false,
                RootPath = Root,
                NewDiskLayout = true,
                AuthorUserName = "tester",
            }, shortcuts);

            Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
            Assert.Equal(2, result.ExtraFwVersionIds.Count);

            foreach (var (id, extraSubtype) in result.ExtraFwVersionIds.Zip(new[] { subtypes[1], subtypes[2] }))
            {
                var extra = _db.GetFwVersionById(id);
                Assert.NotNull(extra);

                // Своя папка — в папке контроллера СВОЕГО подтипа, а не соседнего.
                var expectedFolder = _hierarchy.FwPath(Root, group.Name, extraSubtype.Name,
                    mod.ControllerName, extra!.VersionRaw);
                Assert.Equal(expectedFolder, extra.DiskPath);
                Assert.NotEqual(result.Record!.DiskPath, extra.DiskPath);

                // Свой номер: префикс подтипа — его собственный.
                var number = FwVersionNumber.Parse(extra.VersionRaw)!;
                Assert.Equal(extraSubtype.Prefix, number.SubPrefix);
                Assert.Equal(extraSubtype.Prefix, extra.SubPrefix);
                Assert.NotEqual(result.Record.VersionRaw, extra.VersionRaw);

                // И настоящий файл прошивки под именем СВОЕЙ версии.
                Assert.Equal(FirmwareNaming.BuildFirmwareFilename(number, ".psl"), extra.Filename);
                Assert.True(File.Exists(Path.Combine(VersionLayout.FirmwareFolder(extra.DiskPath), extra.Filename)));
            }

            // Ярлыков больше нет ни одного — ни через IShortcutCreator, ни файлами на диске.
            Assert.Empty(shortcuts.Created);
            Assert.Empty(Directory.GetFiles(Root, "*.lnk", SearchOption.AllDirectories));
        }
        finally { File.Delete(src); }
    }

    /// <summary>Жалоба дословно: «попробовал 2.0 и FD залить по отдельности, но почему-то у них один
    /// номер прошивки 1.1.0005.0001, хотя по иерархии 2.0 это 0, а FD это 1». Номер копии строился
    /// копированием полей основной записи, поэтому и префикс подтипа был чужой.</summary>
    [Fact]
    public void Firmware_ExtraSubtype_NumberUsesItsOwnSubtypePrefix()
    {
        var (group, subtypes) = SeedPj();
        var pj20 = subtypes.Single(s => s.Name == "2.0");
        var fd = subtypes.Single(s => s.Name == "FD");
        var mod = _db.GetAllModifications().First(m => m.ControllerName == "SMH5");
        var src = WriteTempFile(".psl");
        try
        {
            var result = FirmwareUploadService.Upload(_db, _hierarchy, new FirmwareUploadRequest
            {
                SourcePath = src,
                Group = group,
                Subtype = pj20,
                ExtraSubtypes = new List<EquipmentSubType> { fd },
                Modification = mod,
                LaunchTypes = new() { "УПП" },
                Description = "2.0 и FD",
                IncludeDateInVersion = false,
                RootPath = Root,
                NewDiskLayout = true,
                AuthorUserName = "tester",
            });

            var main = result.Record!;
            var copy = _db.GetFwVersionById(result.ExtraFwVersionIds.Single())!;

            Assert.Equal($"{group.Prefix}.{pj20.Prefix}.{mod.HwVersion:D4}.0001", main.VersionRaw);
            Assert.Equal($"{group.Prefix}.{fd.Prefix}.{mod.HwVersion:D4}.0001", copy.VersionRaw);
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

        var link = ParamFileLinkService.LinkToExtraSubtypes(_db, _hierarchy, Root, subtypes[0].Id!.Value, primary,
            new[] { subtypes[1], subtypes[2], subtypes[0] }
                .Select(s => new ParamFileLinkService.SubtypeTarget(s, group.Name)), shortcuts);

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

        var link = ParamFileLinkService.LinkToExtraSubtypes(_db, _hierarchy, Root, subtypes[0].Id!.Value, primary,
            new[] { new ParamFileLinkService.SubtypeTarget(subtypes[1], group.Name) },
            new FakeShortcuts { Throw = new IOException("сеть недоступна") });

        Assert.Single(link.CreatedIds);
        Assert.Contains(link.Warnings, w => w.Contains(subtypes[1].Name));
    }
}
