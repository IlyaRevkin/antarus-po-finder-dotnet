using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Вторая половина этапа 4 (docs/hierarchy-rework-plan.md): мало НАУЧИТЬСЯ читать обе
/// раскладки — надо ещё и класть по-новому там, где диск уже перестроен. Здесь проверяется именно
/// запись: пять папок у новой версии, файл прошивки внутри «Прошивка», документы в своих папках
/// версии, и — главное — что до перестройки диска всё это НЕ включается само.
///
/// Почему выключено по умолчанию: на неперестроенном диске половина версий лежала бы по-новому,
/// половина по-старому, а коллега со старым клиентом не нашёл бы файл свежей прошивки вовсе. Флаг
/// (ConfigService.DiskLayoutV2) ставит перестройка диска и разносит синхронизация — это свойство
/// общего диска, а не машины.</summary>
public class NewVersionLayoutWriteTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public NewVersionLayoutWriteTests()
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

    private (EquipmentGroup Group, EquipmentSubType Subtype, ControllerModification Mod) SeedTgrSmh5()
    {
        var group = _db.GetAllEquipmentGroups().Single(g => g.Name == "ТГР");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).Single();
        var mod = _db.GetAllModifications().Single(m => m.ControllerName == "SMH5" && m.DisplayName == "SMH5");
        return (group, subtype, mod);
    }

    private static string WriteTempFile(string extension, string content = "firmware")
    {
        var path = Path.Combine(Path.GetTempPath(), $"antarus_layout_test_{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, content);
        return path;
    }

    private FirmwareUploadRequest BaseRequest(string src, EquipmentGroup g, EquipmentSubType s, ControllerModification m) => new()
    {
        SourcePath = src,
        Group = g,
        Subtype = s,
        Modification = m,
        LaunchTypes = new() { "УПП" },
        Description = "загрузка на перестроенный диск",
        IncludeDateInVersion = false,
        RootPath = Root,
        AuthorUserName = "tester",
    };

    // ── Сам слой раскладки ───────────────────────────────────────────────────

    [Fact]
    public void EnsureFolders_CreatesFiveFolders_AndIsIdempotent()
    {
        var dir = Path.Combine(Root, "версия");
        Directory.CreateDirectory(dir);

        Assert.False(VersionLayout.HasAllFolders(dir));
        Assert.Equal(5, VersionLayout.EnsureFolders(dir));
        Assert.True(VersionLayout.HasAllFolders(dir));
        Assert.True(Directory.Exists(VersionLayout.FirmwareFolder(dir)));
        foreach (var slot in VersionLayout.SlotFolderNames)
            Assert.True(Directory.Exists(VersionLayout.SlotFolder(dir, slot)), slot);

        // Повторный вызов ничего не создаёт — на этом стоит идемпотентность перестройки.
        Assert.Equal(0, VersionLayout.EnsureFolders(dir));
    }

    /// <summary>Пустая папка документа внутри версии НЕ прячет общий документ контроллера: пока в ней
    /// нет файлов, читается общая папка. Без этого свойства пять папок ломали бы всё, что уже лежит.</summary>
    [Fact]
    public void EmptySlotFolder_DoesNotHideControllerDocument()
    {
        var ctrl = Path.Combine(Root, "SMH5");
        var version = Path.Combine(ctrl, "1.0.0005.0001");
        Directory.CreateDirectory(version);
        VersionLayout.EnsureFolders(version);

        var shared = Path.Combine(ctrl, HierarchyFolders.Instructions);
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "инструкция.pdf"), "pdf");

        Assert.Equal(shared, VersionLayout.SlotBestReadFolder(version, ctrl, HierarchyFolders.Instructions));
    }

    // ── Загрузка новой версии ────────────────────────────────────────────────

    [Fact]
    public void Upload_WithNewLayout_PutsFirmwareAndDocsInsideVersionFolder()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var src = WriteTempFile(".psl");
        var instruction = WriteTempFile(".pdf", "инструкция");
        try
        {
            var request = BaseRequest(src, group, subtype, mod);
            request.NewDiskLayout = true;
            request.InstructionsSourcePath = instruction;

            var result = FirmwareUploadService.Upload(_db, _hierarchy, request);

            Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
            var versionDir = result.DestinationFolder!;

            // Пять папок и файл прошивки — внутри «Прошивка», а не в корне.
            Assert.True(VersionLayout.HasAllFolders(versionDir));
            Assert.True(File.Exists(Path.Combine(VersionLayout.FirmwareFolder(versionDir), result.DestinationFilename!)));
            Assert.False(File.Exists(Path.Combine(versionDir, result.DestinationFilename!)));

            // CHANGELOG.md остаётся в корне папки версии: его читают по фиксированному пути.
            Assert.True(File.Exists(Path.Combine(versionDir, ChangelogFile.FileName)));

            // Инструкция — в своей папке версии, и запись в БД указывает туда же.
            var ownInstructions = VersionLayout.SlotFolder(versionDir, HierarchyFolders.Instructions);
            Assert.StartsWith(ownInstructions, result.Record!.InstructionsPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(result.Record.InstructionsPath));

            // И она находится чтением — тем же путём, которым её ищет карточка версии.
            Assert.Equal(ownInstructions,
                VersionLayout.SlotBestReadFolder(versionDir, VersionLayout.ControllerFolderOf(versionDir),
                    HierarchyFolders.Instructions));
        }
        finally
        {
            File.Delete(src);
            File.Delete(instruction);
        }
    }

    /// <summary>Диск не перестроен — загрузка обязана вести себя ровно как раньше: файл в корне папки
    /// версии, документы в общих папках контроллера. Иначе одна обновлённая машина начала бы
    /// раскладывать версии по-новому на диске, где все остальные лежат по-старому.</summary>
    [Fact]
    public void Upload_WithoutFlag_KeepsOldLayout()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var src = WriteTempFile(".psl");
        var instruction = WriteTempFile(".pdf", "инструкция");
        try
        {
            var request = BaseRequest(src, group, subtype, mod);
            request.InstructionsSourcePath = instruction;

            var result = FirmwareUploadService.Upload(_db, _hierarchy, request);

            var versionDir = result.DestinationFolder!;
            Assert.True(File.Exists(Path.Combine(versionDir, result.DestinationFilename!)));
            Assert.False(Directory.Exists(VersionLayout.FirmwareFolder(versionDir)));
            Assert.StartsWith(_hierarchy.InstrPath(Root, group.Name, subtype.Name, mod.ControllerName),
                result.Record!.InstructionsPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(src);
            File.Delete(instruction);
        }
    }

    /// <summary>Этап 5 на загрузке: новая ОПЦ-версия ложится в «ОПЦ» СВОЕГО контроллера, а папка
    /// называется номером заявки и SN — по ним шкаф и ищут на диске.</summary>
    [Fact]
    public void Upload_Opc_GoesIntoControllerOpcFolder_NamedByRequestAndSn()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var src = WriteTempFile(".psl");
        try
        {
            var request = BaseRequest(src, group, subtype, mod);
            request.OpcEnabled = true;
            request.RequestNumRaw = "1312";
            request.CabinetSnRaw = "42";

            var result = FirmwareUploadService.Upload(_db, _hierarchy, request);

            Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
            var expected = Path.Combine(
                HierarchyService.GroupSubFolder(Root, group.Name, subtype.Name), mod.ControllerName,
                HierarchyFolders.Opc, "01312_SN00042");
            Assert.Equal(expected, result.DestinationFolder);
            Assert.True(Directory.Exists(expected));

            // Контроллер такой версии читается ИЗ ПУТИ — ровно то, ради чего этап 5 и делался.
            Assert.Equal(Path.Combine(HierarchyService.GroupSubFolder(Root, group.Name, subtype.Name), mod.ControllerName),
                VersionLayout.ControllerFolderOf(expected));
        }
        finally { File.Delete(src); }
    }

    // ── Догрузка к уже существующей версии ───────────────────────────────────

    /// <summary>«Доложить карту к загруженной версии» кладёт её туда же, куда положила бы загрузка: в
    /// папку внутри версии у перестроенной версии и в общую папку контроллера у прежней. Решает это
    /// сам VersionLayout по факту на диске — отдельного флага здесь не нужно.</summary>
    [Fact]
    public void Attachments_FollowVersionLayout()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var src = WriteTempFile(".psl");
        var map = WriteTempFile(".xlsx", "карта");
        try
        {
            var result = FirmwareUploadService.Upload(_db, _hierarchy, BaseRequest(src, group, subtype, mod));
            var record = _db.GetFwVersionById(result.FwVersionId)!;
            var versionDir = result.DestinationFolder!;

            // Пока версия старой раскладки — карта ложится в общую папку контроллера.
            var applied = FirmwareAttachmentsService.Apply(_db, _hierarchy, record, new FirmwareAttachmentsRequest
            {
                RootPath = Root,
                GroupName = group.Name,
                SubtypeName = subtype.Name,
                ControllerName = mod.ControllerName,
                IoMapSourcePath = map,
            });
            Assert.Empty(applied.Warnings);
            Assert.StartsWith(_hierarchy.IoMapPath(Root, group.Name, subtype.Name, mod.ControllerName),
                record.IoMapPath, StringComparison.OrdinalIgnoreCase);

            // Версию перестроили — та же догрузка идёт уже внутрь версии.
            VersionLayout.EnsureFolders(versionDir);
            var map2 = WriteTempFile(".xlsx", "карта 2");
            try
            {
                FirmwareAttachmentsService.Apply(_db, _hierarchy, record, new FirmwareAttachmentsRequest
                {
                    RootPath = Root,
                    GroupName = group.Name,
                    SubtypeName = subtype.Name,
                    ControllerName = mod.ControllerName,
                    IoMapSourcePath = map2,
                });
                Assert.StartsWith(VersionLayout.SlotFolder(versionDir, HierarchyFolders.IoMap),
                    record.IoMapPath, StringComparison.OrdinalIgnoreCase);
            }
            finally { File.Delete(map2); }
        }
        finally
        {
            File.Delete(src);
            File.Delete(map);
        }
    }
}
