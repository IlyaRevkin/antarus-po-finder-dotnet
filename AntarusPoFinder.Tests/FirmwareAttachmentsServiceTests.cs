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
    public void Apply_AddsPlcFile_IntoVersionFolderItself()
    {
        var (record, request) = SeedUploadedVersion();
        // Тикет коллеги: доложить недостающий .lfs (или .psl для Segnetics) к уже загруженной версии.
        request.PlcFileSourcePath = WriteSourceFile("доложенная.lfs");

        var result = FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);

        Assert.Empty(result.Warnings);
        Assert.Contains(result.Applied, a => a.Contains("Файл прошивки"));
        // Лёг в САМУ папку версии (disk_path), а не в общие папки контроллера — по файлам этой папки
        // карточка и считает флаги LFS/PSL.
        Assert.True(File.Exists(Path.Combine(record.DiskPath, "доложенная.lfs")));
    }

    [Fact]
    public void Apply_PlcFileMissingSource_WarnsAndAddsNothing()
    {
        var (record, request) = SeedUploadedVersion();
        request.PlcFileSourcePath = Path.Combine(Root, "нет-такого.lfs");

        var result = FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);

        Assert.False(result.AnythingChanged);
        Assert.Single(result.Warnings);
        Assert.Contains("Файл прошивки", result.Warnings[0]);
    }

    [Fact]
    public void Apply_AddsPslSourceFile_IntoVersionFolderItself()
    {
        var (record, request) = SeedUploadedVersion();
        // У Segnetics исходник .psl докладывается ОТДЕЛЬНЫМ полем, тем же способом, что и загрузочный .lfs.
        request.PslFileSourcePath = WriteSourceFile("исходник.psl");

        var result = FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);

        Assert.Empty(result.Warnings);
        Assert.Contains(result.Applied, a => a.Contains("Файл прошивки"));
        Assert.True(File.Exists(Path.Combine(record.DiskPath, "исходник.psl")));
    }

    [Fact]
    public void Apply_AddsBothLfsAndPsl_InOneCall()
    {
        var (record, request) = SeedUploadedVersion();
        // Segnetics: два разных файла за один заход — оба ложатся в саму папку версии.
        request.PlcFileSourcePath = WriteSourceFile("сборка.lfs");
        request.PslFileSourcePath = WriteSourceFile("исходник.psl");

        var result = FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);

        Assert.Empty(result.Warnings);
        Assert.True(File.Exists(Path.Combine(record.DiskPath, "сборка.lfs")));
        Assert.True(File.Exists(Path.Combine(record.DiskPath, "исходник.psl")));
    }

    [Fact]
    public void Apply_FilesPickedFromWhereTheyAlreadyLie_NoFileBusyError()
    {
        var (record, request) = SeedUploadedVersion();
        // Диалоги модерации открываются в папках самой версии на сервере — то есть по умолчанию
        // предлагают ровно те файлы, что там уже лежат. Указав разом HMI, .lfs и .psl оттуда же,
        // оператор получал «файл занят другим процессом»: копирование файла в самого себя.
        var lfs = Path.Combine(record.DiskPath, "сборка.lfs");
        File.WriteAllText(lfs, "x");
        var psl = Path.Combine(record.DiskPath, "исходник.psl");
        File.WriteAllText(psl, "x");

        var hmiSrc = Path.Combine(Root, "hmi_src");
        Directory.CreateDirectory(hmiSrc);
        File.WriteAllText(Path.Combine(hmiSrc, "panel.fsprj"), "x");
        File.WriteAllText(Path.Combine(hmiSrc, "model.bin"), "x");
        request.HmiSourcePath = hmiSrc;
        FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);

        // Второй заход: все три поля указывают на уже сохранённые файлы.
        request.HmiSourcePath = Path.Combine(record.HmiPath, "panel.fsprj");
        request.PlcFileSourcePath = lfs;
        request.PslFileSourcePath = psl;

        var result = FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);

        Assert.Empty(result.Warnings);
        Assert.True(File.Exists(lfs));
        Assert.True(File.Exists(psl));
        Assert.True(File.Exists(Path.Combine(record.HmiPath, "panel.fsprj")));
        Assert.True(File.Exists(Path.Combine(record.HmiPath, "model.bin")));
    }

    [Fact]
    public void Apply_HmiFsprjFilePicked_StoresWholeProjectFolder()
    {
        var (record, request) = SeedUploadedVersion();
        // Оператор выбирает файл .fsprj — это точка входа, а проект живёт всей папкой вокруг него.
        var project = Path.Combine(Root, "Проект панели");
        Directory.CreateDirectory(Path.Combine(project, "Driver"));
        File.WriteAllText(Path.Combine(project, "panel.fsprj"), "x");
        File.WriteAllText(Path.Combine(project, "Driver", "lib.dll"), "x");
        request.HmiSourcePath = Path.Combine(project, "panel.fsprj");

        var result = FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);

        Assert.Empty(result.Warnings);
        Assert.Equal($"{record.VersionRaw}_hmi", Path.GetFileName(record.HmiPath));
        Assert.True(File.Exists(Path.Combine(record.HmiPath, "panel.fsprj")));
        Assert.True(File.Exists(Path.Combine(record.HmiPath, "Driver", "lib.dll")));
    }

    [Fact]
    public void Apply_HmiRepairedWithWholeFolder_RemovesTheOldSingleFileCopy()
    {
        var (record, request) = SeedUploadedVersion();
        // Так проект панели лежал на диске до исправления: один переименованный файл. Он открывается
        // пустым, и оставлять его рядом с починенной папкой нельзя — у половины версий контроллера в
        // общей папке «HMI» лежат оба, и открывался (а потом уезжал коллегам) именно обрубок.
        var stray = Path.Combine(Root, "HMI-старый", $"{record.VersionRaw}_hmi.fsprj");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        File.WriteAllText(stray, "обрубок");
        record.HmiPath = stray;

        var project = Path.Combine(Root, "Оригинал проекта");
        Directory.CreateDirectory(Path.Combine(project, "Driver"));
        File.WriteAllText(Path.Combine(project, "panel.fsprj"), "x");
        File.WriteAllText(Path.Combine(project, "Driver", "lib.dll"), "x");
        request.HmiSourcePath = Path.Combine(project, "panel.fsprj");

        var result = FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);

        Assert.Empty(result.Warnings);
        Assert.False(File.Exists(stray));
        Assert.True(File.Exists(Path.Combine(record.HmiPath, "panel.fsprj")));
        Assert.True(File.Exists(Path.Combine(record.HmiPath, "Driver", "lib.dll")));
    }

    [Fact]
    public void Apply_HmiPathPointedAtSomeoneElsesFile_LeavesItAlone()
    {
        var (record, request) = SeedUploadedVersion();
        // Имя не наше («{версия}_hmi») — значит файл положил человек, и удалять его мы не вправе.
        var foreignFile = Path.Combine(Root, "HMI-старый", "panel.fsprj");
        Directory.CreateDirectory(Path.GetDirectoryName(foreignFile)!);
        File.WriteAllText(foreignFile, "чужой");
        record.HmiPath = foreignFile;

        var project = Path.Combine(Root, "Оригинал проекта");
        Directory.CreateDirectory(Path.Combine(project, "Driver"));
        File.WriteAllText(Path.Combine(project, "panel.fsprj"), "x");
        File.WriteAllText(Path.Combine(project, "Driver", "lib.dll"), "x");
        request.HmiSourcePath = Path.Combine(project, "panel.fsprj");

        FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);

        Assert.True(File.Exists(foreignFile));
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
