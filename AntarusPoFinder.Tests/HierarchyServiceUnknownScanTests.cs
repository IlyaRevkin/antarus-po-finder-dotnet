using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Covers the "проверь, обрабатывает ли сканирование неизвестных также ПАПКИ, а не только
/// файлы" audit — a fresh Database seeds the default catalogue (ПЖ/НГР/ТГР/ВЗУ/ШУЗ groups, ВЗУ
/// notably has BOTH a "—" placeholder subtype and a real "ПИ" subtype side by side, see
/// HierarchyDefaultsData), so HierarchyService.EnsureStructure builds a real multi-level tree to
/// scan against — no live network drive needed, this is exactly what ScanUnknown_Click drives in the
/// app.</summary>
public class HierarchyServiceUnknownScanTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string _root => _tempRoot.Path;

    public HierarchyServiceUnknownScanTests()
    {
        _db = new Database(_dbFile.Path);
        _hierarchy = new HierarchyService(_db);
        _hierarchy.EnsureStructure(_root);
    }

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
        _tempRoot.Dispose();
    }

    [Fact]
    public void ScanUnknownFiles_TopLevelUnknownGroupFolder_IsDetected()
    {
        var unknownGroupDir = Path.Combine(_root, "ПО", "НеизвестныйТип");
        Directory.CreateDirectory(unknownGroupDir);

        var result = _hierarchy.ScanUnknownFiles(_root);

        Assert.Contains(result, e => e.Path == unknownGroupDir && e.Type == "dir");
    }

    [Fact]
    public void ScanUnknownFiles_TopLevelUnknownFile_IsDetected()
    {
        var unknownFile = Path.Combine(_root, "ПО", "stray.txt");
        File.WriteAllText(unknownFile, "");

        var result = _hierarchy.ScanUnknownFiles(_root);

        Assert.Contains(result, e => e.Path == unknownFile && e.Type == "file");
    }

    [Fact]
    public void ScanUnknownFiles_UnknownSubtypeFolderNestedUnderKnownGroup_IsDetected()
    {
        // ВЗУ is a real group (seeded) — an unrecognised subtype folder dropped directly under it
        // used to be completely invisible to the old top-level-only scan.
        var unknownSubtypeDir = Path.Combine(_root, "ПО", "ВЗУ", "НовыйПодтип");
        Directory.CreateDirectory(unknownSubtypeDir);

        var result = _hierarchy.ScanUnknownFiles(_root);

        Assert.Contains(result, e => e.Path == unknownSubtypeDir && e.Type == "dir" && e.Section == "ПО");
    }

    [Fact]
    public void ScanUnknownFiles_UnknownControllerFolderNestedUnderKnownSubtype_IsDetected()
    {
        // ВЗУ\ПИ is a real subtype folder (seeded) — an unrecognised controller folder inside it was
        // likewise invisible to the old scan.
        var unknownControllerDir = Path.Combine(_root, "ПО", "ВЗУ", "ПИ", "НЕИЗВЕСТНЫЙ_КОНТРОЛЛЕР");
        Directory.CreateDirectory(unknownControllerDir);

        var result = _hierarchy.ScanUnknownFiles(_root);

        Assert.Contains(result, e => e.Path == unknownControllerDir && e.Type == "dir");
    }

    [Fact]
    public void ScanUnknownFiles_UnknownFileNestedUnderKnownSubtype_IsDetected()
    {
        var unknownFile = Path.Combine(_root, "ПО", "НГР", "stray_notes.txt");
        File.WriteAllText(unknownFile, "");

        var result = _hierarchy.ScanUnknownFiles(_root);

        Assert.Contains(result, e => e.Path == unknownFile && e.Type == "file");
    }

    [Fact]
    public void ScanUnknownFiles_RealFirmwareVersionFolderInsideKnownController_IsNeverFlagged()
    {
        // Version folder names are free-form (e.g. "2.1.042.001.20260422_1348") and were never meant
        // to be checked against a fixed known-name list — recursion must stop at the controller level.
        var controllerDir = Path.Combine(_root, "ПО", "ВЗУ", "ПИ", "SMH4");
        Assert.True(Directory.Exists(controllerDir), "seeded structure should already contain this controller folder");
        var versionDir = Path.Combine(controllerDir, "2.1.042.001.20260422_1348");
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "firmware.psl"), "");

        var result = _hierarchy.ScanUnknownFiles(_root);

        Assert.DoesNotContain(result, e => e.Path.StartsWith(versionDir, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result, e => e.Path == versionDir);
    }

    [Fact]
    public void ScanUnknownFiles_WellFormedTreeAfterEnsureStructure_ReportsNothingUnknown()
    {
        var result = _hierarchy.ScanUnknownFiles(_root);
        Assert.Empty(result);
    }

    [Fact]
    public void ScanUnknownFiles_UnknownManufacturerFolderUnderKnownParamsSubtype_IsDetected()
    {
        var unknownManufacturerDir = Path.Combine(_root, "Параметры", "ВЗУ", "ПИ", "НеизвестныйПроизводитель");
        Directory.CreateDirectory(unknownManufacturerDir);

        var result = _hierarchy.ScanUnknownFiles(_root);

        Assert.Contains(result, e => e.Path == unknownManufacturerDir && e.Section == "Параметры");
    }

    [Fact]
    public void ScanUnknownFiles_MultipleNestedUnknowns_AllReportedTogether()
    {
        var a = Path.Combine(_root, "ПО", "НГР", "СтранныйПодтип");
        var b = Path.Combine(_root, "ПО", "ВЗУ", "ЧужойКонтроллер");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);

        var result = _hierarchy.ScanUnknownFiles(_root);

        Assert.Contains(result, e => e.Path == a);
        Assert.Contains(result, e => e.Path == b);
        Assert.Equal(2, result.Count);
    }
}
