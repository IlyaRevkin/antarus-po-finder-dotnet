using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Загрузка прошивки ПАПКОЙ: что из папки реально едет на диск и как это называется.
///
/// До этих правок папка копировалась целиком и под своими именами, а выбор файла внутри неё был
/// только подсказкой «чем открывать» — отсюда жалоба «выбираю файл, он всё равно оставшиеся файлы в
/// папке тоже тянет, сам файл не переименовывает в соответствии с нормой, кладёт прошивку в корень
/// а не в папку „Прошивка“». Здесь закреплены все три ответа.</summary>
public class FolderUploadPickTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public FolderUploadPickTests()
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

    private (EquipmentGroup group, EquipmentSubType subtype, ControllerModification mod) SeedTgrSmh5()
    {
        var group = _db.GetAllEquipmentGroups().Single(g => g.Name == "ТГР");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).Single();
        var mod = _db.GetAllModifications().Single(m => m.ControllerName == "SMH5" && m.DisplayName == "SMH5");
        return (group, subtype, mod);
    }

    /// <summary>Папка «как у программиста»: сама прошивка, заметка рядом, вложенный файл проекта и
    /// служебный мусор проводника.</summary>
    private static string MakeSourceFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"antarus_folder_upload_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "пж_smh5_v4.31.16.psl"), "firmware");
        File.WriteAllText(Path.Combine(dir, "заметки.txt"), "старые заметки");
        File.WriteAllText(Path.Combine(dir, "Thumbs.db"), "junk");
        Directory.CreateDirectory(Path.Combine(dir, "Driver"));
        File.WriteAllText(Path.Combine(dir, "Driver", "lib.dll"), "driver");
        return dir;
    }

    private FirmwareUploadRequest BaseRequest(string sourcePath, EquipmentGroup group, EquipmentSubType subtype,
        ControllerModification mod) => new()
    {
        SourcePath = sourcePath,
        Group = group,
        Subtype = subtype,
        Modification = mod,
        LaunchTypes = new() { "УПП" },
        Description = "загрузка папкой",
        IncludeDateInVersion = false,
        RootPath = Root,
        AuthorUserName = "tester",
        NewDiskLayout = true,
    };

    // ── что предлагает диалог ────────────────────────────────────────────────

    [Fact]
    public void List_MarksJunkAndFirmwareCandidates()
    {
        var dir = MakeSourceFolder();
        try
        {
            var entries = FolderUploadPick.List(dir, new[] { ".psl", ".lfs" });

            Assert.Equal(4, entries.Count);
            var junk = entries.Single(e => e.RelativePath == "Thumbs.db");
            Assert.True(junk.IsJunk);
            Assert.False(junk.LooksLikeFirmware);

            var psl = entries.Single(e => e.RelativePath == "пж_smh5_v4.31.16.psl");
            Assert.True(psl.LooksLikeFirmware);
            Assert.False(psl.IsJunk);
            Assert.True(psl.Size > 0);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void DefaultMain_PicksTheOnlyFirmwareFile()
    {
        var dir = MakeSourceFolder();
        try
        {
            var entries = FolderUploadPick.List(dir, new[] { ".psl" });
            Assert.Equal("пж_smh5_v4.31.16.psl", FolderUploadPick.DefaultMain(entries));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void DefaultMain_NullWhenAmbiguous()
    {
        var dir = MakeSourceFolder();
        try
        {
            File.WriteAllText(Path.Combine(dir, "второй.psl"), "firmware2");
            var entries = FolderUploadPick.List(dir, new[] { ".psl" });
            Assert.Null(FolderUploadPick.DefaultMain(entries));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── что реально копируется ───────────────────────────────────────────────

    [Fact]
    public void Upload_FolderWithPickedFile_CopiesOnlyItAndRenamesIt()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var dir = MakeSourceFolder();
        try
        {
            var request = BaseRequest(dir, group, subtype, mod);
            request.SourceMainFile = "пж_smh5_v4.31.16.psl";

            var result = FirmwareUploadService.Upload(_db, _hierarchy, request);

            Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
            var firmwareFolder = VersionLayout.FirmwareFolder(result.DestinationFolder!);
            var files = Directory.GetFiles(firmwareFolder).Select(Path.GetFileName).ToList();

            // Ровно один файл, под каноническим именем — и никакого «заметки.txt» рядом.
            var expected = FirmwareNaming.BuildFirmwareFilename(FwVersionNumber.Parse(result.Record!.VersionRaw)!, ".psl");
            Assert.Equal(new[] { expected }, files);
            Assert.Equal(expected, result.DestinationFilename);
            Assert.Empty(Directory.GetDirectories(firmwareFolder));

            // И в корне папки версии прошивки нет — только CHANGELOG.md и пять папок.
            Assert.Equal(new[] { ChangelogFile.FileName },
                Directory.GetFiles(result.DestinationFolder!).Select(Path.GetFileName).ToArray());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Upload_FolderWithExtras_KeepsTheirNamesAndNesting()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var dir = MakeSourceFolder();
        try
        {
            var request = BaseRequest(dir, group, subtype, mod);
            request.SourceMainFile = "пж_smh5_v4.31.16.psl";
            request.SourceFolderFiles = new() { Path.Combine("Driver", "lib.dll") };

            var result = FirmwareUploadService.Upload(_db, _hierarchy, request);

            Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
            var firmwareFolder = VersionLayout.FirmwareFolder(result.DestinationFolder!);
            Assert.True(File.Exists(Path.Combine(firmwareFolder, "Driver", "lib.dll")));
            Assert.False(File.Exists(Path.Combine(firmwareFolder, "заметки.txt")));
            Assert.False(File.Exists(Path.Combine(firmwareFolder, "Thumbs.db")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Переименованный файл обязан оставаться открываемым: подсказка «чем открывать»
    /// пересчитывается копированием, иначе она указывала бы на имя, которого на диске уже нет.</summary>
    [Fact]
    public void Upload_FolderWithPickedFile_HintPointsAtTheRenamedFile()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var dir = MakeSourceFolder();
        try
        {
            var request = BaseRequest(dir, group, subtype, mod);
            request.SourceMainFile = "пж_smh5_v4.31.16.psl";
            request.ExecutableHint = "пж_smh5_v4.31.16.psl";

            var result = FirmwareUploadService.Upload(_db, _hierarchy, request);

            Assert.True(result.IsSuccess);
            Assert.Equal(result.DestinationFilename, result.Record!.ExecutableHint);
            Assert.NotNull(ExecutableHintResolver.Resolve(
                VersionLayout.FirmwareFolder(result.DestinationFolder!), result.Record.ExecutableHint));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Прежнее поведение никуда не делось: без выбора файла папка едет целиком. На это
    /// опирается всё, что зовёт загрузку не из формы (тесты, досмотр диска).</summary>
    [Fact]
    public void Upload_FolderWithoutPickedFile_CopiesEverythingAsBefore()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var dir = MakeSourceFolder();
        try
        {
            var result = FirmwareUploadService.Upload(_db, _hierarchy, BaseRequest(dir, group, subtype, mod));

            Assert.True(result.IsSuccess);
            var firmwareFolder = VersionLayout.FirmwareFolder(result.DestinationFolder!);
            Assert.True(File.Exists(Path.Combine(firmwareFolder, "заметки.txt")));
            Assert.True(File.Exists(Path.Combine(firmwareFolder, "пж_smh5_v4.31.16.psl")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Расширение отмеченного в папке файла проверяется так же, как у одиночного: раньше
    /// папка не проверялась вовсе, потому что «главный» файл в ней не был известен.</summary>
    [Fact]
    public void Upload_FolderWithUnknownExtensionPick_AsksForConfirmation()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var dir = MakeSourceFolder();
        try
        {
            var request = BaseRequest(dir, group, subtype, mod);
            request.SourceMainFile = "заметки.txt";

            var result = FirmwareUploadService.Upload(_db, _hierarchy, request);

            Assert.Equal(FirmwareUploadOutcome.NeedsConfirmation, result.Outcome);
            Assert.Equal(FirmwareConfirmationKind.UnknownExtension, result.ConfirmationKind);

            request.ConfirmUnknownExtension = true;
            Assert.True(FirmwareUploadService.Upload(_db, _hierarchy, request).IsSuccess);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Путь, уводящий за пределы папки-источника, не копируется никогда — он мог приехать
    /// из чужого конфига или из подправленного руками списка.</summary>
    [Fact]
    public void ExtraFilesToCopy_DropsEscapingAndMissingPaths()
    {
        var dir = MakeSourceFolder();
        try
        {
            var extras = FirmwareUploadService.ExtraFilesToCopy(dir, "пж_smh5_v4.31.16.psl", new[]
            {
                @"..\соседняя.psl",
                @"C:\Windows\notepad.exe",
                "нет-такого-файла.txt",
                "пж_smh5_v4.31.16.psl",
                "заметки.txt",
                "заметки.txt",
            });

            Assert.Equal(new[] { "заметки.txt" }, extras);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
