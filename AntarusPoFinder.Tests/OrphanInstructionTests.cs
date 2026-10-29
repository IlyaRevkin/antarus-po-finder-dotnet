using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Потерянные инструкции: документ на диске есть, а версии, которой он принадлежал, в базе
/// нет. «Если прошивка этой версии потерялась или перезаписалась новой датой, предыдущую ссылку
/// можно было бы прилинковать к новой, а файндер сам бы вычислял потерянные так инструкции» (Илья).
///
/// Дата стоит в самом номере версии, а номер — это имя папки: перезалив ту же сборку с датой,
/// получаем ДРУГУЮ папку и пустую «Инструкция» у новой записи, тогда как документ остался в прежней
/// папке, на которую больше никто не смотрит.</summary>
public class OrphanInstructionTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public OrphanInstructionTests()
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

    private FwVersionRecord SeedVersion(string sourceName = "source.psl")
    {
        var group = _db.GetAllEquipmentGroups().Single(g => g.Name == "ТГР");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).Single();
        var mod = _db.GetAllModifications().Single(m => m.ControllerName == "SMH5" && m.DisplayName == "SMH5");

        var src = Path.Combine(Root, sourceName);
        File.WriteAllText(src, "прошивка");
        var upload = FirmwareUploadService.Upload(_db, _hierarchy, new FirmwareUploadRequest
        {
            SourcePath = src,
            Group = group,
            Subtype = subtype,
            Modification = mod,
            LaunchTypes = new() { "УПП" },
            Description = "загрузка",
            IncludeDateInVersion = false,
            RootPath = Root,
            AuthorUserName = "tester",
        });
        Assert.Equal(FirmwareUploadOutcome.Success, upload.Outcome);
        return upload.Record!;
    }

    /// <summary>Папка версии, о которой база не знает: ровно то, что остаётся на диске от
    /// перезалитой с новой датой или потерянной прошивки. Внутри — её инструкция.</summary>
    private (string Dir, string Document) SeedOrphan(FwVersionRecord live, string version, string text)
    {
        var controllerFolder = Path.GetDirectoryName(live.DiskPath)!;
        var dir = Path.Combine(controllerFolder, version);
        VersionLayout.EnsureFolders(dir);
        var folder = VersionLayout.SlotFolder(dir, HierarchyFolders.Instructions);
        var document = Path.Combine(folder, $"инструкция_{version}.pdf");
        File.WriteAllText(document, text);
        return (dir, document);
    }

    [Fact]
    public void Инструкция_без_своей_версии_находится_и_получает_кандидатов()
    {
        var live = SeedVersion();
        var orphanVersion = live.VersionRaw + ".20260422_1348";
        var (dir, document) = SeedOrphan(live, orphanVersion, "инструкция потерянной версии");

        var found = OrphanInstructions.Find(_db, Root);

        var orphan = Assert.Single(found);
        Assert.Equal(document, orphan.Document);
        Assert.Equal(dir, orphan.VersionDir);
        Assert.False(orphan.Hopeless);

        // Первым кандидатом — версия с тем же номером: отличается только дата в хвосте, то есть это
        // та же сборка, перезалитая заново.
        Assert.True(orphan.Candidates[0].SameNumber);
        Assert.Equal(live.Id, orphan.Candidates[0].Version.Id);
    }

    [Fact]
    public void Папки_живых_версий_сиротами_не_считаются()
    {
        var live = SeedVersion();
        var folder = VersionLayout.SlotBestReadFolder(live.DiskPath,
            VersionLayout.ControllerFolderOf(live.DiskPath), HierarchyFolders.Instructions)!;
        File.WriteAllText(Path.Combine(folder, $"инструкция_{live.VersionRaw}.pdf"), "своя инструкция");

        Assert.Empty(OrphanInstructions.Find(_db, Root));
    }

    [Fact]
    public void Привязка_копирует_документ_новой_версии_и_не_трогает_исходный()
    {
        var live = SeedVersion();
        var (_, document) = SeedOrphan(live, live.VersionRaw + ".20260422_1348", "инструкция потерянной версии");
        var orphan = OrphanInstructions.Find(_db, Root).Single();

        var result = OrphanInstructions.Link(_db, Root, orphan, live);

        Assert.Empty(result.Warnings);
        Assert.False(result.ReplacedExisting);
        Assert.Equal("инструкция потерянной версии", File.ReadAllText(result.StoredPath));
        Assert.Equal(result.StoredPath, _db.GetFwVersionById(live.Id!.Value)!.InstructionsPath);
        // Имя каноническое — по этому адресу ведёт наклеенный на шкаф QR новой версии.
        Assert.True(InstructionNaming.IsCanonical(result.StoredPath, live.VersionRaw));

        // Исходный документ остаётся на месте: найдись потерянная версия позже, её инструкция лежит
        // там, где лежала.
        Assert.True(File.Exists(document));
    }

    [Fact]
    public void Разобранная_находка_второй_раз_не_предлагается()
    {
        var live = SeedVersion();
        SeedOrphan(live, live.VersionRaw + ".20260422_1348", "инструкция потерянной версии");
        var orphan = OrphanInstructions.Find(_db, Root).Single();

        OrphanInstructions.Link(_db, Root, orphan, live);

        // Отметка стоит на ДИСКЕ, а не в базе: иначе у коллеги та же находка всплывала бы снова.
        Assert.Equal(live.VersionRaw, InstructionOwnership.Read(orphan.Folder)?.LinkedTo);
        Assert.Empty(OrphanInstructions.Find(_db, Root));
    }

    [Fact]
    public void Прежняя_инструкция_получателя_уезжает_в_архив()
    {
        var live = SeedVersion();
        var targetFolder = VersionLayout.SlotBestReadFolder(live.DiskPath,
            VersionLayout.ControllerFolderOf(live.DiskPath), HierarchyFolders.Instructions)!;
        File.WriteAllText(Path.Combine(targetFolder, $"инструкция_{live.VersionRaw}.pdf"), "редакция получателя");

        SeedOrphan(live, live.VersionRaw + ".20260422_1348", "инструкция потерянной версии");
        var orphan = OrphanInstructions.Find(_db, Root).Single();

        var result = OrphanInstructions.Link(_db, Root, orphan, live);

        Assert.True(result.ReplacedExisting);
        Assert.Equal("инструкция потерянной версии", File.ReadAllText(result.StoredPath));
        var archive = Path.Combine(Path.GetDirectoryName(result.StoredPath)!, InstructionStorage.PreviousFolderName);
        var kept = Directory.GetFiles(archive).Single();
        Assert.Equal("редакция получателя", File.ReadAllText(kept));
    }
}
