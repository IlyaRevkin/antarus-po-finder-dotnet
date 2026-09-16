using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Куда ложится на диск ПАПКА ПРОЕКТА — при загрузке новой версии и когда проект панели
/// докладывают к уже загруженной. Правило одно: папка едет целиком и под СВОИМ именем, потому что
/// среда связывает проект с папкой по имени (см. <see cref="ProjectTree"/> и соседний
/// ProjectTreeRenameTests). Высыпать её содержимое в «Прошивка\» или «HMI\» — то же самое
/// расхождение имён, что и переименование файла в одиночку.</summary>
public class ProjectFolderStorageTests
{
    /// <summary>Папка проекта произвольного вендора — та же, что в ProjectTreeRenameTests.</summary>
    private static string MakeProject(string parent, string name, string ext)
    {
        var folder = Path.Combine(parent, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ext), "проект");
        File.WriteAllText(Path.Combine(folder, name + ".bak"), "резервная копия");
        Directory.CreateDirectory(Path.Combine(folder, "image"));
        File.WriteAllText(Path.Combine(folder, "image", "logo.png"), "картинка");
        return folder;
    }

    // ── Проект панели, доложенный к версии ──────────────────────────────────

    /// <summary>Проект панели кладётся в «HMI» ПОД СВОИМ ИМЕНЕМ. Раньше его содержимое высыпалось
    /// прямо в «HMI» — то есть папка проекта начинала называться «HMI», а файл внутри оставался
    /// прежним, и пара расходилась ровно так же.</summary>
    [Fact]
    public void HmiProject_ProjectTreeFolder_KeepsItsOwnName()
    {
        using var root = new TempRoot();
        var versionDir = Path.Combine(root.Path, "1.0.0005.0001");
        VersionLayout.EnsureFolders(versionDir);
        var source = MakeProject(root.Path, "УПД_MK070_v8-26", ".dpj");

        var stored = FirmwareAttachmentsService.CopyHmiProject(
            VersionLayout.SlotFolder(versionDir, HierarchyFolders.Hmi), "1.0.0005.0001", source);

        Assert.Equal(Path.Combine(versionDir, HierarchyFolders.Hmi, "УПД_MK070_v8-26"), stored);
        Assert.True(File.Exists(Path.Combine(stored, "УПД_MK070_v8-26.dpj")));
    }

    /// <summary>И то же самое, когда указали не папку, а сам ФАЙЛ проекта: «проект-папка» узнаётся по
    /// строению, а не по списку расширений (до правки так работал только .fsprj, а .dpj копировался
    /// одиноким файлом под именем «{версия}_hmi.dpj» — заведомо мёртвый проект).</summary>
    [Fact]
    public void HmiProject_EntryFilePicked_StoresTheWholeFolder()
    {
        using var root = new TempRoot();
        var versionDir = Path.Combine(root.Path, "1.0.0005.0001");
        VersionLayout.EnsureFolders(versionDir);
        var source = MakeProject(root.Path, "УПД_MK070_v8-26", ".dpj");

        var stored = FirmwareAttachmentsService.CopyHmiProject(
            VersionLayout.SlotFolder(versionDir, HierarchyFolders.Hmi), "1.0.0005.0001",
            Path.Combine(source, "УПД_MK070_v8-26.dpj"));

        Assert.True(Directory.Exists(stored));
        Assert.True(File.Exists(Path.Combine(stored, "УПД_MK070_v8-26.dpj")));
        Assert.True(File.Exists(Path.Combine(stored, "image", "logo.png")));
        Assert.False(File.Exists(Path.Combine(versionDir, HierarchyFolders.Hmi, "1.0.0005.0001_hmi.dpj")));
    }
}

/// <summary>Загрузка папки проекта: она обязана доехать на диск целиком и под своим именем. Отдельный
/// класс, потому что загрузке нужна база — см. VersionFolderRootTests.</summary>
public class ProjectFolderUploadTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public ProjectFolderUploadTests()
    {
        _db = new Database(_dbFile.Path);
        _hierarchy = new HierarchyService(_db);
        _hierarchy.EnsureStructure(Root);
    }

    public void Dispose() { _db.Dispose(); _dbFile.Dispose(); _tempRoot.Dispose(); }

    /// <summary>Загружаем папку проекта KINCO и указываем файл проекта как саму прошивку — так это и
    /// делается в форме загрузки. До правки файл ложился в «Прошивка» под каноническим именем, а
    /// папка проекта не доезжала вовсе: среда открывала проект без ресурсов и расширений.</summary>
    [Fact]
    public void Upload_ProjectFolder_LandsWholeAndKeepsItsNames()
    {
        var group = _db.GetAllEquipmentGroups().Single(g => g.Name == "ТГР");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).Single();
        var mod = _db.GetAllModifications().Single(m => m.ControllerName == "SMH5" && m.DisplayName == "SMH5");

        var source = Path.Combine(Root, "УПД_MK070_v8-26");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "УПД_MK070_v8-26.dpj"), "проект");
        File.WriteAllText(Path.Combine(source, "УПД_MK070_v8-26.pkgx"), "пакет");
        Directory.CreateDirectory(Path.Combine(source, "vg"));
        File.WriteAllText(Path.Combine(source, "vg", "vector.dat"), "ресурс");

        var result = FirmwareUploadService.Upload(_db, _hierarchy, new FirmwareUploadRequest
        {
            SourcePath = source,
            SourceMainFile = "УПД_MK070_v8-26.dpj",
            Group = group,
            Subtype = subtype,
            Modification = mod,
            LaunchTypes = new() { "УПП" },
            Description = "проект панели",
            IncludeDateInVersion = false,
            RootPath = Root,
            AuthorUserName = "tester",
            NewDiskLayout = true,
        });

        Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
        var fw = VersionLayout.FirmwareFolder(result.Record!.DiskPath);
        var project = Path.Combine(fw, "УПД_MK070_v8-26");

        // Папка проекта на месте, имена внутри не тронуты, ресурсы доехали.
        Assert.True(File.Exists(Path.Combine(project, "УПД_MK070_v8-26.dpj")));
        Assert.True(File.Exists(Path.Combine(project, "vg", "vector.dat")));
        // И ни одного файла проекта под каноническим именем прямо в «Прошивка».
        Assert.Empty(Directory.EnumerateFiles(fw, "*.dpj"));
    }
}
