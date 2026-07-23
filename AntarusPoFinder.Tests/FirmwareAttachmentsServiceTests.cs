using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Догрузка доп. файлов (Карта in/out, Карта modbus, Инструкция, HMI-проект) к УЖЕ
/// загруженной версии прошивки — раньше приложить их можно было только в момент загрузки, иначе
/// приходилось перезаливать версию заново.</summary>
public class FirmwareAttachmentsServiceTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public FirmwareAttachmentsServiceTests()
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

    private (FwVersionRecord record, FirmwareAttachmentsRequest request) SeedUploadedVersion()
    {
        var group = _db.GetAllEquipmentGroups().Single(g => g.Name == "ТГР");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).Single();
        var mod = _db.GetAllModifications().Single(m => m.ControllerName == "SMH5" && m.DisplayName == "SMH5");

        var src = Path.Combine(Root, "source.psl");
        File.WriteAllText(src, "dummy");
        var upload = FirmwareUploadService.Upload(_db, _hierarchy, new FirmwareUploadRequest
        {
            SourcePath = src,
            Group = group,
            Subtype = subtype,
            Modification = mod,
            LaunchTypes = new() { "УПП" },
            Description = "первая загрузка",
            IncludeDateInVersion = false,
            RootPath = Root,
            AuthorUserName = "tester",
        });
        Assert.Equal(FirmwareUploadOutcome.Success, upload.Outcome);

        return (upload.Record!, new FirmwareAttachmentsRequest
        {
            RootPath = Root,
            GroupName = group.Name,
            SubtypeName = subtype.Name,
            ControllerName = mod.ControllerName,
        });
    }

    private string WriteSourceFile(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "содержимое");
        return path;
    }

    [Fact]
    public void Apply_CopiesMapsAndInstructions_AndStoresPathsInDb()
    {
        var (record, request) = SeedUploadedVersion();
        request.IoMapSourcePath = WriteSourceFile("src_io.xlsx");
        request.ModbusMapSourcePath = WriteSourceFile("src_modbus.xlsx");
        request.InstructionsSourcePath = WriteSourceFile("src_instr.pdf");

        var result = FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);

        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.Applied.Count);
        Assert.True(File.Exists(record.IoMapPath));
        Assert.True(File.Exists(record.ModbusMapPath));
        Assert.True(File.Exists(record.InstructionsPath));

        var reloaded = _db.GetFwVersionById(record.Id!.Value)!;
        Assert.Equal(record.IoMapPath, reloaded.IoMapPath);
        Assert.Equal(record.ModbusMapPath, reloaded.ModbusMapPath);
        Assert.Equal(record.InstructionsPath, reloaded.InstructionsPath);
        // Файлы ложатся в те же общие папки контроллера, что и при загрузке новой версии.
        Assert.Contains("Карта ВВ", reloaded.IoMapPath);
    }

    [Fact]
    public void Apply_CopiesHmiFolder_UnderVersionName()
    {
        var (record, request) = SeedUploadedVersion();
        var hmiSrc = Path.Combine(Root, "hmi_src");
        Directory.CreateDirectory(Path.Combine(hmiSrc, "Driver"));
        File.WriteAllText(Path.Combine(hmiSrc, "project.fsprj"), "x");
        File.WriteAllText(Path.Combine(hmiSrc, "Driver", "lib.dll"), "x");
        request.HmiSourcePath = hmiSrc;

        var result = FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);

        Assert.Empty(result.Warnings);
        Assert.Equal($"{record.VersionRaw}_hmi", Path.GetFileName(record.HmiPath));
        Assert.True(File.Exists(Path.Combine(record.HmiPath, "project.fsprj")));
        // Вложенные папки проекта переносятся целиком, а не только верхний уровень.
        Assert.True(File.Exists(Path.Combine(record.HmiPath, "Driver", "lib.dll")));
        Assert.Equal(record.HmiPath, _db.GetFwVersionById(record.Id!.Value)!.HmiPath);
    }

    [Fact]
    public void Apply_ReplacingHmi_DropsFilesOfThePreviousProject()
    {
        var (record, request) = SeedUploadedVersion();
        var first = Path.Combine(Root, "hmi_v1");
        Directory.CreateDirectory(first);
        File.WriteAllText(Path.Combine(first, "старый.fsprj"), "x");
        request.HmiSourcePath = first;
        FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);

        var second = Path.Combine(Root, "hmi_v2");
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "новый.fsprj"), "x");
        request.HmiSourcePath = second;
        FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);

        Assert.True(File.Exists(Path.Combine(record.HmiPath, "новый.fsprj")));
        Assert.False(File.Exists(Path.Combine(record.HmiPath, "старый.fsprj")));
    }

    [Fact]
    public void Apply_EmptyValue_ClearsLinkButKeepsFilesOnDisk()
    {
        var (record, request) = SeedUploadedVersion();
        request.IoMapSourcePath = WriteSourceFile("src_io.xlsx");
        FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);
        var storedFile = record.IoMapPath;

        request.IoMapSourcePath = "";
        var result = FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);

        Assert.Contains(result.Applied, a => a.Contains("ссылка убрана"));
        Assert.Equal("", _db.GetFwVersionById(record.Id!.Value)!.IoMapPath);
        Assert.True(File.Exists(storedFile));
    }

    [Fact]
    public void Apply_UnchangedOrNullValues_ChangeNothing()
    {
        var (record, request) = SeedUploadedVersion();
        request.IoMapSourcePath = WriteSourceFile("src_io.xlsx");
        FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);
        var stored = record.IoMapPath;

        // Повторное «сохранение» диалога без правок: в поле лежит уже сохранённый путь.
        request.IoMapSourcePath = stored;
        request.ModbusMapSourcePath = null;
        var result = FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);

        Assert.False(result.AnythingChanged);
        Assert.Empty(result.Warnings);
        Assert.Equal(stored, _db.GetFwVersionById(record.Id!.Value)!.IoMapPath);
    }

    [Fact]
    public void Apply_MissingSource_WarnsButKeepsOtherAttachments()
    {
        var (record, request) = SeedUploadedVersion();
        request.IoMapSourcePath = Path.Combine(Root, "нет-такого-файла.xlsx");
        request.InstructionsSourcePath = WriteSourceFile("src_instr.pdf");

        var result = FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);

        Assert.Single(result.Warnings);
        Assert.Contains("Карта in/out", result.Warnings[0]);
        Assert.Equal(new[] { "Инструкция" }, result.Applied);
        Assert.True(File.Exists(record.InstructionsPath));
        Assert.Equal("", _db.GetFwVersionById(record.Id!.Value)!.IoMapPath);
    }

    [Fact]
    public void Apply_UnavailableRoot_ChangesNothing()
    {
        var (record, request) = SeedUploadedVersion();
        request.RootPath = Path.Combine(Root, "не-смонтировано");
        request.IoMapSourcePath = WriteSourceFile("src_io.xlsx");

        var result = FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);

        Assert.False(result.AnythingChanged);
        Assert.Single(result.Warnings);
        Assert.Equal("", _db.GetFwVersionById(record.Id!.Value)!.IoMapPath);
    }
}
