using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Покрывает разбиение операций «БД → диск» на фазы, сделанное по жалобе «кажется, что
/// приложение зависает, когда оно в фоне что-то делает». Смысл разбиения в том, что дисковую фазу
/// вызывающий уносит в Task.Run, а обе БД-фазы оставляет на потоке интерфейса: соединение SQLite
/// одно на приложение и не потокобезопасно.
///
/// Ключевая проверка здесь нестандартная — дисковые фазы прогоняются на ЗАКРЫТОМ соединении с БД.
/// Если в такую фазу когда-нибудь вернётся обращение к базе, тест упадёт сразу, а не превратится в
/// плавающую гонку у пользователя, которую с этого ПК не воспроизвести. Обычное «результат тот же,
/// что у однофазного вызова» проверяется тут же рядом — сами однофазные методы остались как обёртки
/// и продолжают покрываться своими прежними тестами.</summary>
public class BackgroundPhaseSplitTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public BackgroundPhaseSplitTests()
    {
        _db = new Database(_dbFile.Path);
        _hierarchy = new HierarchyService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
        _tempRoot.Dispose();
    }

    // ── Структура диска ──────────────────────────────────────────────────────

    /// <summary>План считается по БД и на диск не смотрит — иначе его нельзя было бы строить на
    /// потоке интерфейса, ради чего всё и затевалось.</summary>
    [Fact]
    public void PlanStructure_CreatesNothingOnDisk()
    {
        var plan = _hierarchy.PlanStructure(Root);

        Assert.NotEmpty(plan.Folders);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
    }

    [Fact]
    public void ApplyStructurePlan_CreatesSameTreeAsEnsureStructure()
    {
        using var reference = new TempRoot();
        var expected = _hierarchy.EnsureStructure(reference.Path);

        var actual = HierarchyService.ApplyStructurePlan(_hierarchy.PlanStructure(Root));

        Assert.Equal(expected.CreatedCount, actual.CreatedCount);
        Assert.Equal(RelativeTree(reference.Path), RelativeTree(Root));
    }

    /// <summary>Дисковая фаза не должна ходить в БД — доказываем, закрыв соединение до вызова.</summary>
    [Fact]
    public void ApplyStructurePlan_RunsWithDatabaseClosed()
    {
        StructurePlan plan;
        using (var dbFile = new TempDb())
        {
            using var db = new Database(dbFile.Path);
            plan = new HierarchyService(db).PlanStructure(Root);
        }

        var result = HierarchyService.ApplyStructurePlan(plan);

        Assert.True(result.Ok);
        Assert.True(Directory.Exists(Path.Combine(Root, "ПО")));
    }

    // ── Досмотр прошивок на диске ────────────────────────────────────────────

    [Fact]
    public void FwSyncPhases_ImportVersionFoundOnDisk()
    {
        _hierarchy.EnsureStructure(Root);
        var versionDir = SeedFirmwareFolderOnDisk("3.0.005.0777");

        var scan = HierarchyService.ScanFwDisk(_hierarchy.PlanFwSync(Root));
        var result = _hierarchy.ImportFwCandidates(scan);

        Assert.True(result.Ok);
        Assert.Equal(1, result.Added);
        var stored = AllFwVersions().Single(v => v.VersionRaw == "3.0.005.0777");
        Assert.Equal(versionDir, stored.DiskPath);
        Assert.Equal("firmware.psl", stored.Filename);
    }

    /// <summary>Второй прогон не должен ничего добавлять — известные версии отсекаются ещё в БД-фазе
    /// (PlanFwSync), а не запросом на каждую найденную папку посреди обхода диска, как было раньше.</summary>
    [Fact]
    public void FwSyncPhases_SecondRun_SkipsAlreadyKnown()
    {
        _hierarchy.EnsureStructure(Root);
        SeedFirmwareFolderOnDisk("3.0.005.0777");
        _hierarchy.ImportFwCandidates(HierarchyService.ScanFwDisk(_hierarchy.PlanFwSync(Root)));

        var second = _hierarchy.ImportFwCandidates(HierarchyService.ScanFwDisk(_hierarchy.PlanFwSync(Root)));

        Assert.Equal(0, second.Added);
        Assert.Equal(1, second.Skipped);
        Assert.Single(AllFwVersions().Where(v => v.VersionRaw == "3.0.005.0777"));
    }

    [Fact]
    public void ScanFwDisk_RunsWithDatabaseClosed()
    {
        _hierarchy.EnsureStructure(Root);
        SeedFirmwareFolderOnDisk("3.0.005.0777");

        FwSyncPlan plan;
        using (var dbFile = new TempDb())
        {
            using var db = new Database(dbFile.Path);
            plan = new HierarchyService(db).PlanFwSync(Root);
        }

        var scan = HierarchyService.ScanFwDisk(plan);

        Assert.Contains(scan.Candidates, c => c.Version.Raw == "3.0.005.0777");
    }

    // ── Скан неизвестного ────────────────────────────────────────────────────

    [Fact]
    public void ScanUnknownFiles_WithSnapshot_MatchesInstanceCall()
    {
        _hierarchy.EnsureStructure(Root);
        var stray = Path.Combine(Root, "ПО", "НеизвестныйТип");
        Directory.CreateDirectory(stray);

        var viaSnapshot = HierarchyService.ScanUnknownFiles(Root, _hierarchy.SnapshotNames());
        var viaInstance = _hierarchy.ScanUnknownFiles(Root);

        Assert.Equal(
            viaInstance.Select(e => e.Path).OrderBy(p => p, StringComparer.Ordinal),
            viaSnapshot.Select(e => e.Path).OrderBy(p => p, StringComparer.Ordinal));
        Assert.Contains(viaSnapshot, e => e.Path == stray);
    }

    [Fact]
    public void ScanUnknownFiles_RunsWithDatabaseClosed()
    {
        _hierarchy.EnsureStructure(Root);
        Directory.CreateDirectory(Path.Combine(Root, "ПО", "НеизвестныйТип"));

        HierarchyNames names;
        using (var dbFile = new TempDb())
        {
            using var db = new Database(dbFile.Path);
            names = new HierarchyService(db).SnapshotNames();
        }

        var unknown = HierarchyService.ScanUnknownFiles(Root, names);

        Assert.Contains(unknown, e => e.Name == "НеизвестныйТип");
    }

    // ── Загрузка прошивки ────────────────────────────────────────────────────

    /// <summary>До копирования на диск не должно попасть ничего: оба подтверждения («неизвестное
    /// расширение», «версия существует») спрашиваются между Prepare и CopyFiles, и отказ пользователя
    /// обязан не оставить следов — ровно как в однофазной версии.</summary>
    [Fact]
    public void Prepare_WritesNothingToDisk()
    {
        _hierarchy.EnsureStructure(Root);
        var src = WriteTempFile(".psl");
        try
        {
            var (plan, failure) = FirmwareUploadService.Prepare(_db, _hierarchy, BaseRequest(src));

            Assert.Null(failure);
            Assert.NotNull(plan);
            Assert.False(Directory.Exists(plan!.DestinationFolder));
            Assert.Empty(AllFwVersions());
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Prepare_UnknownExtension_AsksBeforeTouchingDisk()
    {
        _hierarchy.EnsureStructure(Root);
        var src = WriteTempFile(".xyz");
        try
        {
            var (plan, failure) = FirmwareUploadService.Prepare(_db, _hierarchy, BaseRequest(src));

            Assert.Null(plan);
            Assert.Equal(FirmwareUploadOutcome.NeedsConfirmation, failure!.Outcome);
            Assert.Equal(FirmwareConfirmationKind.UnknownExtension, failure.ConfirmationKind);
            Assert.Empty(AllFwVersions());
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void UploadPhases_ProduceSameResultAsSingleCall()
    {
        _hierarchy.EnsureStructure(Root);
        var src = WriteTempFile(".psl");
        try
        {
            var (plan, _) = FirmwareUploadService.Prepare(_db, _hierarchy, BaseRequest(src));
            var copy = FirmwareUploadService.CopyFiles(plan!);
            var result = FirmwareUploadService.Register(_db, _hierarchy, plan!, copy);

            Assert.Equal(FirmwareUploadOutcome.Success, result.Outcome);
            Assert.Null(copy.IoErrorMessage);
            Assert.True(File.Exists(Path.Combine(result.DestinationFolder!, result.DestinationFilename!)));
            Assert.True(File.Exists(Path.Combine(result.DestinationFolder!, "CHANGELOG.md")));

            var stored = _db.GetFwVersionById(result.FwVersionId);
            Assert.NotNull(stored);
            Assert.Equal("3.0.005.0001", stored!.VersionRaw);
            Assert.Equal("тестовая загрузка", stored.Description);
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void CopyFiles_RunsWithDatabaseClosed()
    {
        _hierarchy.EnsureStructure(Root);
        var src = WriteTempFile(".psl");
        try
        {
            FirmwareUploadPlan? plan;
            using (var dbFile = new TempDb())
            {
                using var db = new Database(dbFile.Path);
                var hierarchy = new HierarchyService(db);
                (plan, _) = FirmwareUploadService.Prepare(db, hierarchy, BaseRequestFor(db, src));
            }

            var copy = FirmwareUploadService.CopyFiles(plan!);

            Assert.Null(copy.IoErrorMessage);
            Assert.True(File.Exists(Path.Combine(plan!.DestinationFolder, copy.DestinationFilename)));
        }
        finally { File.Delete(src); }
    }

    /// <summary>Провал копирования не должен доходить до БД — в однофазной версии это был выход по
    /// FirmwareUploadOutcome.IoError до всякой записи, теперь то же самое выражено через
    /// IoErrorMessage, и вызывающий обязан на нём остановиться.</summary>
    [Fact]
    public void CopyFiles_MissingSource_ReportsIoErrorWithoutDbRow()
    {
        _hierarchy.EnsureStructure(Root);
        var src = WriteTempFile(".psl");
        FirmwareUploadPlan? plan;
        try
        {
            (plan, _) = FirmwareUploadService.Prepare(_db, _hierarchy, BaseRequest(src));
        }
        finally { File.Delete(src); }

        var copy = FirmwareUploadService.CopyFiles(plan!);

        Assert.NotNull(copy.IoErrorMessage);
        Assert.Empty(AllFwVersions());
    }

    // ── Синхронизация конфига между машинами ─────────────────────────────────

    /// <summary>Асинхронный путь синхронизации — тот же самый экспорт/импорт, что и раньше: то, что
    /// чтение файла с шары уехало в фоновый поток, не должно менять НИЧЕГО в результате.</summary>
    [Fact]
    public async Task AsyncConfigSync_DeliversCatalogueChangeToOtherMachine()
    {
        using var machines = new TwoMachines();
        machines.SetSharedRoot();

        var group = machines.DbA.GetAllEquipmentGroups().First();
        machines.DbA.UpsertEquipmentSubtype(new EquipmentSubType { GroupId = group.Id!.Value, Name = "ФазовыйТест", Prefix = 9 });

        await ConfigSyncService.ExportAsync(machines.SvcA, machines.Root.Path, "profileA (Администратор)");

        var (info, error, snapshot) = await ConfigSyncService.CheckForUpdateAsync(machines.SvcB);
        Assert.Null(error);
        Assert.NotNull(info);
        Assert.NotNull(snapshot);

        await ConfigSyncService.ApplyAsync(machines.SvcB, snapshot!, machines.Root.Path);

        Assert.Contains(machines.DbB.GetSubtypesForGroup(group.Id!.Value), s => s.Name == "ФазовыйТест");
    }

    /// <summary>После применения та же проверка обязана сказать «нового нет» — иначе фоновый тик
    /// синхронизировался бы по кругу, гоняя диск и индикатор без конца.</summary>
    [Fact]
    public async Task AsyncConfigSync_SecondCheck_ReportsNothingNew()
    {
        using var machines = new TwoMachines();
        machines.SetSharedRoot();

        var group = machines.DbA.GetAllEquipmentGroups().First();
        machines.DbA.UpsertEquipmentSubtype(new EquipmentSubType { GroupId = group.Id!.Value, Name = "ФазовыйТест", Prefix = 9 });
        await ConfigSyncService.ExportAsync(machines.SvcA, machines.Root.Path, "profileA (Администратор)");

        var (_, _, snapshot) = await ConfigSyncService.CheckForUpdateAsync(machines.SvcB);
        await ConfigSyncService.ApplyAsync(machines.SvcB, snapshot!, machines.Root.Path);

        var (again, errorAgain, _) = await ConfigSyncService.CheckForUpdateAsync(machines.SvcB);

        Assert.Null(errorAgain);
        Assert.Null(again);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private System.Collections.Generic.List<FwVersionRecord> AllFwVersions() =>
        _db.GetFwVersions(includeArchived: true, includeRolledBack: true);

    /// <summary>ТГР + «—» + SMH5 — простейшая комбинация из сида, без лишнего сегмента подтипа в пути
    /// (та же, что используют FirmwareUploadServiceTests).</summary>
    private static (EquipmentGroup Group, EquipmentSubType Subtype, ControllerModification Mod) SeedTgrSmh5(Database db)
    {
        var group = db.GetAllEquipmentGroups().Single(g => g.Name == "ТГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).Single();
        var mod = db.GetAllModifications().Single(m => m.ControllerName == "SMH5" && m.DisplayName == "SMH5");
        return (group, subtype, mod);
    }

    private FirmwareUploadRequest BaseRequest(string sourcePath) => BaseRequestFor(_db, sourcePath);

    private FirmwareUploadRequest BaseRequestFor(Database db, string sourcePath)
    {
        var (group, subtype, mod) = SeedTgrSmh5(db);
        return new FirmwareUploadRequest
        {
            SourcePath = sourcePath,
            Group = group,
            Subtype = subtype,
            Modification = mod,
            LaunchTypes = new() { "УПП" },
            Description = "тестовая загрузка",
            IncludeDateInVersion = false,
            RootPath = Root,
            AuthorUserName = "tester",
        };
    }

    /// <summary>Кладёт на диск папку версии с файлом прошивки — ровно то, что видит досмотр диска,
    /// когда версию залил коллега с другой машины, а в этой БД её ещё нет.</summary>
    private string SeedFirmwareFolderOnDisk(string versionRaw)
    {
        var (group, subtype, mod) = SeedTgrSmh5(_db);
        var subPath = subtype.Name == "—"
            ? Path.Combine(Root, "ПО", group.Name)
            : Path.Combine(Root, "ПО", group.Name, subtype.Name);
        var versionDir = Path.Combine(subPath, mod.ControllerName, versionRaw);
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "firmware.psl"), "dummy");
        return versionDir;
    }

    private static string WriteTempFile(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"antarus_phase_test_{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, "dummy firmware bytes");
        return path;
    }

    private static string[] RelativeTree(string root) =>
        Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(d => d[root.Length..])
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToArray();
}
