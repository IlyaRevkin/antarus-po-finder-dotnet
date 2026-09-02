using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>«Куда класть файлы ЭТОЙ версии» — вопрос, на который в программе отвечали в нескольких
/// местах и не всегда одинаково. С диска пришла картина: у контроллера PIXEL2 рядом с папками версий
/// лежат «HMI», «Инструкция», «Карта Modbus», «Карта ВВ», у самой версии внутри — ещё одна «HMI», а
/// файл прошивки при этом в корне папки версии, а не в «Прошивка\».
///
/// Здесь закрепляются три правила, каждое из которых нарушалось:
/// <list type="number">
/// <item><description>уровень («своя папка версии» или «общая папка контроллера») выбирает ДИСК —
/// есть ли у версии её «Прошивка\» — и выбирает одинаково для всех четырёх документов и для всех
/// операций: загрузки, модерации, доп. материалов;</description></item>
/// <item><description>доложенный файл прошивки ложится туда же, где лежат остальные файлы прошивки
/// этой версии, то есть в «Прошивка\» у перестроенной;</description></item>
/// <item><description>если папки версии на диске нет, её «родителем» подменять нельзя — родитель это
/// папка КОНТРОЛЛЕРА, и файл уезжал прямо в неё.</description></item>
/// </list></summary>
public class VersionFolderRootTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public VersionFolderRootTests()
    {
        _db = new Database(_dbFile.Path);
        _hierarchy = new HierarchyService(_db);
        _hierarchy.EnsureStructure(Root);
    }

    public void Dispose() { _db.Dispose(); _dbFile.Dispose(); _tempRoot.Dispose(); }

    private (EquipmentGroup, EquipmentSubType, ControllerModification) SeedTgrSmh5()
    {
        var group = _db.GetAllEquipmentGroups().Single(g => g.Name == "ТГР");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).Single();
        var mod = _db.GetAllModifications().Single(m => m.ControllerName == "SMH5" && m.DisplayName == "SMH5");
        return (group, subtype, mod);
    }

    private string WriteSourceFile(string name, string content = "содержимое")
    {
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private (FwVersionRecord Record, FirmwareAttachmentsRequest Request) Upload(bool newLayout)
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var result = FirmwareUploadService.Upload(_db, _hierarchy, new FirmwareUploadRequest
        {
            SourcePath = WriteSourceFile($"source_{Guid.NewGuid():N}.psl", "прошивка"),
            Group = group,
            Subtype = subtype,
            Modification = mod,
            LaunchTypes = new() { "УПП" },
            Description = "загрузка",
            IncludeDateInVersion = false,
            RootPath = Root,
            AuthorUserName = "tester",
            NewDiskLayout = newLayout,
        });
        Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
        return (result.Record!, new FirmwareAttachmentsRequest
        {
            RootPath = Root,
            GroupName = group.Name,
            SubtypeName = subtype.Name,
            ControllerName = mod.ControllerName,
        });
    }

    // ── Один уровень на все четыре документа и на все операции ───────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UploadAndLaterEdit_PutDocumentsOnTheSameLevel(bool newLayout)
    {
        var (record, request) = Upload(newLayout);
        request.IoMapSourcePath = WriteSourceFile("карта.xlsx");
        request.ModbusMapSourcePath = WriteSourceFile("modbus.xlsx");
        request.InstructionsSourcePath = WriteSourceFile("инструкция.pdf");
        request.HmiSourcePath = WriteSourceFile("panel.fsprj");

        var result = FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);
        Assert.Empty(result.Warnings);

        var versionDir = record.DiskPath;
        var ctrl = VersionLayout.ControllerFolderOf(versionDir)!;
        // Уровень один и тот же у всех четырёх — и он ровно тот, который выбирает раскладка по диску.
        foreach (var (slot, stored) in new[]
                 {
                     (HierarchyFolders.IoMap, record.IoMapPath),
                     (HierarchyFolders.Modbus, record.ModbusMapPath),
                     (HierarchyFolders.Instructions, record.InstructionsPath),
                     (HierarchyFolders.Hmi, record.HmiPath),
                 })
            Assert.StartsWith(VersionLayout.SlotWriteFolder(versionDir, ctrl, slot), stored,
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Загрузка спрашивает про уровень тот же класс и в тот же момент, что и все остальные:
    /// уже ПОСЛЕ того, как дисковая фаза завела папки версии. Раньше она решала это по флагу и
    /// заранее — из-за чего у версии, чью «Прошивка\» так и не завели, документы одной загрузки
    /// оказывались внутри папки версии, а документы следующей правки — в папке контроллера.</summary>
    [Fact]
    public void UploadPlan_AsksTheDisk_NotTheFlag()
    {
        var (group, subtype, mod) = SeedTgrSmh5();
        var (plan, failure) = FirmwareUploadService.Prepare(_db, _hierarchy, new FirmwareUploadRequest
        {
            SourcePath = WriteSourceFile("src.psl", "прошивка"),
            Group = group,
            Subtype = subtype,
            Modification = mod,
            LaunchTypes = new() { "УПП" },
            Description = "загрузка",
            IncludeDateInVersion = false,
            RootPath = Root,
            AuthorUserName = "tester",
            NewDiskLayout = true,
        });
        Assert.Null(failure);
        Assert.NotNull(plan);

        // Папки версии ещё нет — значит и «своей» папки HMI у неё пока нет: ответ тот же, что дала бы
        // модерация в эту же секунду.
        Assert.Equal(VersionDocFolders.WriteFolder(plan!.DestinationFolder, plan.ControllerFolder,
            HierarchyFolders.Hmi), plan.HmiFolder);

        // А после дисковой фазы ответ меняется вместе с диском — на «своя папка версии».
        FirmwareUploadService.CopyFiles(plan);
        Assert.Equal(VersionLayout.SlotFolder(plan.DestinationFolder, HierarchyFolders.Hmi), plan.HmiFolder);
    }

    // ── Доложенный файл прошивки ────────────────────────────────────────────

    [Fact]
    public void AddedFirmwareFile_GoesIntoTheFirmwareFolder_OnARebuiltVersion()
    {
        var (record, request) = Upload(newLayout: true);
        request.PslFileSourcePath = WriteSourceFile("доложенный.psl", "исходник");

        var result = FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);

        Assert.Empty(result.Warnings);
        Assert.True(File.Exists(Path.Combine(VersionLayout.FirmwareFolder(record.DiskPath), "доложенный.psl")));
        // И НЕ в корне папки версии: туда его клал прежний код, а чистильщик потом честно предлагал
        // перенести файл вниз («Файл прошивки лежит в корне папки версии»).
        Assert.False(File.Exists(Path.Combine(record.DiskPath, "доложенный.psl")));
    }

    [Fact]
    public void AddedFirmwareFile_StaysInTheVersionFolder_OnAnOldLayoutVersion()
    {
        var (record, request) = Upload(newLayout: false);
        request.PlcFileSourcePath = WriteSourceFile("boot.lfs", "загрузочный");

        var result = FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);

        Assert.Empty(result.Warnings);
        Assert.True(File.Exists(Path.Combine(record.DiskPath, "boot.lfs")));
    }

    /// <summary>Папки версии на диске нет (переименовали, удалили, disk_path разошёлся с диском).
    /// Прежний код брал её РОДИТЕЛЯ — то есть папку контроллера — и файл прошивки ложился прямо туда,
    /// вперемешку с папками версий. Правильный ответ — внятное предупреждение и ни одного записанного
    /// байта.</summary>
    [Fact]
    public void AddedFirmwareFile_NeverFallsBackToTheControllerFolder()
    {
        var (record, request) = Upload(newLayout: true);
        var ctrl = VersionLayout.ControllerFolderOf(record.DiskPath)!;
        FileSystemHelpersDelete(record.DiskPath);
        request.PslFileSourcePath = WriteSourceFile("осиротевший.psl", "исходник");

        var result = FirmwareAttachmentsService.Apply(_db, _hierarchy, record, request);

        Assert.Contains(result.Warnings, w => w.Contains("папка версии", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(ctrl, "осиротевший.psl")));
        Assert.Empty(Directory.EnumerateFiles(ctrl, "*", SearchOption.TopDirectoryOnly));
    }

    private static void FileSystemHelpersDelete(string dir) => Directory.Delete(dir, recursive: true);
}
