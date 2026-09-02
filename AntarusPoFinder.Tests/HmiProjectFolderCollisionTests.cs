using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Жалоба владельца: «Заливал с папкой HMI — криво зашилось, пол папок пропало, файлов нет,
/// исполняемый потерялся. Почему-то 3 папки HMI: там где прошивки, потом в конкретной прошивке типа
/// 2.4.1321.0006, и в ней в папке прошивка тоже hmi».
///
/// <b>Причина.</b> Проекты, где программа ПЛК и панель лежат ОДНОЙ папкой (KINCO и подобные),
/// разворачиваются подпапками <c>plc</c> и <c>hmi</c>. «HMI» — это ещё и имя одной из пяти папок
/// раскладки версии, а разницы в регистре файловая система Windows не видит. Пока «своя папка
/// версии» определялась только по ИМЕНИ (VersionLayout.IsVersionOwnFolder), перестройка диска
/// уносила в «Прошивка\» подпапку <c>plc</c> и оставляла <c>hmi</c> наверху: проект разрывался
/// пополам — ровно «пол папок пропало» и «исполняемый потерялся», если он лежал в панельной
/// половине. Мало того, оставшаяся половина становилась для программы папкой панели ЭТОЙ версии
/// (VersionLayout.SlotBestReadFolder выбирает свою папку версии, если в ней есть файлы), то есть
/// карточка показывала кусок проекта ПЛК как HMI-проект.
///
/// Здесь проверяется и сама развилка (VersionLayout.StrayProjectFolders), и обе операции, которые
/// чинят уже испорченный диск: перестройка (DiskLayoutMigrator) и чистильщик (DiskCleanupScanner).
/// Обе только ПЕРЕНОСЯТ — ни одна ничего не удаляет.</summary>
public class HmiProjectFolderCollisionTests
{
    private static string Touch(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "x");
        return path;
    }

    /// <summary>Папка версии, в корне которой распакован проект «ПЛК + панель одной папкой»:
    /// <c>plc\prog.kpj</c> и <c>hmi\panel.fsprj</c> + вложенная <c>hmi\Drivers\d.dll</c>.</summary>
    private static (FwVersionRecord Record, string Dir) MakeVersionWithUnpackedProject(string root, string versionRaw)
    {
        var dir = Path.Combine(root, "ПО", "ПЖ", "2.0", "SMH5", versionRaw);
        Touch(Path.Combine(dir, "plc"), "prog.kpj");
        Touch(Path.Combine(dir, "hmi"), "panel.fsprj");
        Touch(Path.Combine(dir, "hmi", "Drivers"), "d.dll");
        return (new FwVersionRecord { VersionRaw = versionRaw, DiskPath = dir, Filename = "prog.kpj" }, dir);
    }

    private static List<string> RelativeTree(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(e => Path.GetRelativePath(root, e))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ── Развилка «своя папка версии» / «часть проекта» ───────────────────────

    [Fact]
    public void StrayProjectFolders_TakesProjectHmiSubfolder_WhenTheRestOfTheProjectIsBesideIt()
    {
        using var root = new TempRoot();
        var (_, dir) = MakeVersionWithUnpackedProject(root.Path, "1.0.0005.0001");

        var strays = VersionLayout.StrayProjectFolders(dir);

        // Обе половины проекта — иначе перестройка унесёт вниз только «plc», и проект развалится.
        Assert.Equal(new[] { "hmi", "plc" },
            strays.Select(Path.GetFileName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void StrayProjectFolders_LeavesHmiAlone_WhenItHoldsOurStoredPanelProject()
    {
        using var root = new TempRoot();
        var dir = Path.Combine(root.Path, "ПО", "ПЖ", "2.0", "SMH5", "1.0.0005.0001");
        Touch(Path.Combine(dir, "plc"), "prog.kpj");
        // Так проект панели кладёт сама программа (FirmwareAttachmentsService.CopyHmiProject) —
        // «{версия}_hmi» внутри папки HMI версии. Это НАША папка, утащить её в «Прошивка\» нельзя.
        Touch(Path.Combine(dir, HierarchyFolders.Hmi, "1.0.0005.0001_hmi"), "panel.fsprj");

        var strays = VersionLayout.StrayProjectFolders(dir);

        Assert.Equal(new[] { "plc" }, strays.Select(Path.GetFileName));
    }

    [Fact]
    public void StrayProjectFolders_LeavesEmptyHmiFolderAlone()
    {
        using var root = new TempRoot();
        var dir = Path.Combine(root.Path, "ПО", "ПЖ", "2.0", "SMH5", "1.0.0005.0001");
        Touch(Path.Combine(dir, "plc"), "prog.kpj");
        Directory.CreateDirectory(VersionLayout.SlotFolder(dir, HierarchyFolders.Hmi));

        // Пустая — это ровно то, что заводит EnsureFolders у каждой версии.
        Assert.Equal(new[] { "plc" }, VersionLayout.StrayProjectFolders(dir).Select(Path.GetFileName));
    }

    [Fact]
    public void StrayProjectFolders_LeavesHmiAlone_WhenNothingElseInRootLooksLikeAProject()
    {
        using var root = new TempRoot();
        var dir = Path.Combine(root.Path, "ПО", "ПЖ", "2.0", "SMH5", "1.0.0005.0001");
        Touch(Path.Combine(dir, HierarchyFolders.Hmi), "panel.fsprj");

        // Второй половины проекта рядом нет — значит это просто папка панели версии, куда положили
        // файл руками. Сомнение всегда трактуется в пользу «не трогать».
        Assert.Empty(VersionLayout.StrayProjectFolders(dir));
    }

    [Fact]
    public void StrayProjectFolders_NeverTakesRussianSlotFolders()
    {
        using var root = new TempRoot();
        var dir = Path.Combine(root.Path, "ПО", "ПЖ", "2.0", "SMH5", "1.0.0005.0001");
        Touch(Path.Combine(dir, "plc"), "prog.kpj");
        foreach (var slot in new[] { HierarchyFolders.Instructions, HierarchyFolders.Modbus, HierarchyFolders.IoMap })
            Touch(Path.Combine(dir, slot), "документ.pdf");

        // Совпасть с подпапкой проекта могло только английское «hmi»; русские имена папок документов
        // разбираться не должны ни при каких условиях — там лежат настоящие документы версии.
        Assert.Equal(new[] { "plc" }, VersionLayout.StrayProjectFolders(dir).Select(Path.GetFileName));
    }

    // ── Перестройка диска: проект переезжает целиком ─────────────────────────

    [Fact]
    public void Migrator_MovesBothHalvesOfTheProject_AndGivesTheVersionItsOwnHmiFolderBack()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersionWithUnpackedProject(root.Path, "1.0.0005.0001");
        var before = RelativeTree(dir);

        DiskLayoutMigrator.Apply(DiskLayoutMigrator.Plan(new DiskLayoutMigrator.MigrationInput(
            root.Path, new[] { record },
            new DiskLayoutMigrator.MigrationOptions(RenameFirmwareFiles: false, FoldFilesIntoVersion: true))),
            renamed: null);

        var firmware = VersionLayout.FirmwareFolder(dir);
        Assert.True(File.Exists(Path.Combine(firmware, "plc", "prog.kpj")));
        Assert.True(File.Exists(Path.Combine(firmware, "hmi", "panel.fsprj")));
        Assert.True(File.Exists(Path.Combine(firmware, "hmi", "Drivers", "d.dll")));

        // Ни одного файла не потеряно — тот же набор относительных путей, только с «Прошивка\» впереди.
        var after = RelativeTree(firmware);
        Assert.Equal(before, after);

        // И пять папок версии на месте: «HMI» пришлось завести заново — прежняя уехала вниз вместе с
        // проектом, и без второго EnsureFolders версия осталась бы без одной из пяти.
        Assert.True(VersionLayout.HasAllFolders(dir));
        Assert.False(Directory.EnumerateFileSystemEntries(
            VersionLayout.SlotFolder(dir, HierarchyFolders.Hmi)).Any());
    }

    /// <summary>Уже испорченный диск: версия перестроена (есть «Прошивка\» с половиной проекта), а
    /// вторая половина так и осталась наверху папкой «hmi». Повторная перестройка обязана её
    /// подобрать — иначе испорченное этой ошибкой чинить было бы нечем.</summary>
    [Fact]
    public void Migrator_RepairsAlreadySplitProject()
    {
        using var root = new TempRoot();
        var dir = Path.Combine(root.Path, "ПО", "ПЖ", "2.0", "SMH5", "1.0.0005.0001");
        // Ровно та история, которой испорчен реальный диск: сначала проект лёг в корень версии
        // подпапками «plc» и «hmi», потом перестройка унесла вниз только «plc», а пять папок завела
        // поверх — своей «HMI» версия не получила, её место заняла половина проекта.
        Touch(Path.Combine(dir, "hmi"), "panel.fsprj");
        Touch(Path.Combine(dir, "hmi", "Drivers"), "d.dll");
        VersionLayout.EnsureFolders(dir);
        Touch(Path.Combine(VersionLayout.FirmwareFolder(dir), "plc"), "prog.kpj");
        var record = new FwVersionRecord { VersionRaw = "1.0.0005.0001", DiskPath = dir, Filename = "prog.kpj" };

        var plan = DiskLayoutMigrator.Plan(new DiskLayoutMigrator.MigrationInput(root.Path, new[] { record },
            new DiskLayoutMigrator.MigrationOptions(RenameFirmwareFiles: false, FoldFilesIntoVersion: true)));
        Assert.Contains(plan.Ops, o => o.Kind == DiskLayoutMigrator.OpKind.FoldIntoVersion);

        DiskLayoutMigrator.Apply(plan, renamed: null);

        Assert.True(File.Exists(Path.Combine(VersionLayout.FirmwareFolder(dir), "hmi", "panel.fsprj")));
        Assert.True(File.Exists(Path.Combine(VersionLayout.FirmwareFolder(dir), "hmi", "Drivers", "d.dll")));
        Assert.True(VersionLayout.HasAllFolders(dir));
    }

    /// <summary>Занятая цель — ничего не удаляем и ничего не затираем: половина проекта остаётся
    /// наверху и попадёт в следующий прогон. Это общее правило переноса, но проверить его надо именно
    /// на «hmi»: тут цена ошибки — чужой проект панели.</summary>
    [Fact]
    public void Migrator_DoesNotOverwriteAnExistingHmiFolderInsideFirmware()
    {
        using var root = new TempRoot();
        var dir = Path.Combine(root.Path, "ПО", "ПЖ", "2.0", "SMH5", "1.0.0005.0001");
        Touch(Path.Combine(dir, "plc"), "prog.kpj");
        Touch(Path.Combine(dir, "hmi"), "panel.fsprj");
        VersionLayout.EnsureFolders(dir);
        Touch(Path.Combine(VersionLayout.FirmwareFolder(dir), "hmi"), "чужой.fsprj");
        var record = new FwVersionRecord { VersionRaw = "1.0.0005.0001", DiskPath = dir, Filename = "prog.kpj" };

        DiskLayoutMigrator.Apply(DiskLayoutMigrator.Plan(new DiskLayoutMigrator.MigrationInput(
            root.Path, new[] { record },
            new DiskLayoutMigrator.MigrationOptions(RenameFirmwareFiles: false, FoldFilesIntoVersion: true))),
            renamed: null);

        Assert.True(File.Exists(Path.Combine(VersionLayout.FirmwareFolder(dir), "hmi", "чужой.fsprj")));
        Assert.True(File.Exists(Path.Combine(dir, "hmi", "panel.fsprj")));
    }

    // ── Чистильщик: та же находка, но построчно и с объяснением ──────────────

    [Fact]
    public void Cleanup_OffersToMoveTheProjectHmiFolder_ButNeverToDeleteIt()
    {
        using var root = new TempRoot();
        var dir = Path.Combine(root.Path, "ПО", "ПЖ", "2.0", "SMH5", "1.0.0005.0001");
        Touch(Path.Combine(dir, "plc"), "prog.kpj");
        Touch(Path.Combine(dir, "hmi"), "panel.fsprj");
        VersionLayout.EnsureFolders(dir);
        var record = new FwVersionRecord { VersionRaw = "1.0.0005.0001", DiskPath = dir };

        var plan = DiskCleanupScanner.Plan(new DiskCleanupScanner.CleanupInput(root.Path, new[] { record },
            new[] { "psl", "kpj" }, new[] { "fsprj" }, new[] { "pdf" }, Array.Empty<string>()));

        var finding = Assert.Single(plan.Findings, f => f.Path == Path.Combine(dir, "hmi"));
        Assert.Equal(DiskCleanupScanner.Issue.WrongFolder, finding.Issue);
        Assert.Equal(DiskCleanupScanner.Act.Move, finding.Action);
        Assert.Equal(Path.Combine(VersionLayout.FirmwareFolder(dir), "hmi"), finding.Target);
        Assert.DoesNotContain(DiskCleanupScanner.Act.Delete, finding.AllowedActions);
    }

    [Fact]
    public void Cleanup_LeavesTheVersionsOwnHmiFolderAlone()
    {
        using var root = new TempRoot();
        var dir = Path.Combine(root.Path, "ПО", "ПЖ", "2.0", "SMH5", "1.0.0005.0001");
        VersionLayout.EnsureFolders(dir);
        Touch(Path.Combine(dir, "plc"), "prog.kpj");
        Touch(Path.Combine(dir, HierarchyFolders.Hmi, "1.0.0005.0001_hmi"), "panel.fsprj");
        var record = new FwVersionRecord { VersionRaw = "1.0.0005.0001", DiskPath = dir };

        var plan = DiskCleanupScanner.Plan(new DiskCleanupScanner.CleanupInput(root.Path, new[] { record },
            new[] { "psl", "kpj" }, new[] { "fsprj" }, new[] { "pdf" }, Array.Empty<string>()));

        Assert.DoesNotContain(plan.Findings, f => f.Path == VersionLayout.SlotFolder(dir, HierarchyFolders.Hmi));
    }

    // ── Что видит карточка версии ───────────────────────────────────────────

    /// <summary>До перестройки половина проекта ПЛК выдавала себя за папку панели версии; после —
    /// панель снова читается из общей папки контроллера, как и полагается версии без своего
    /// HMI-проекта.</summary>
    [Fact]
    public void AfterRepair_VersionNoLongerServesHalfThePlcProjectAsItsPanel()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersionWithUnpackedProject(root.Path, "1.0.0005.0001");
        var ctrl = Path.GetDirectoryName(dir)!;
        Touch(Path.Combine(ctrl, HierarchyFolders.Hmi), "панель контроллера.fsprj");

        // Половина проекта ПЛК выдаёт себя за папку панели версии: в ней есть файлы, значит
        // SlotBestReadFolder выбирает её и общая папка контроллера перестаёт читаться.
        Assert.Equal(VersionLayout.SlotFolder(dir, HierarchyFolders.Hmi),
            VersionLayout.SlotBestReadFolder(dir, ctrl, HierarchyFolders.Hmi));

        DiskLayoutMigrator.Apply(DiskLayoutMigrator.Plan(new DiskLayoutMigrator.MigrationInput(
            root.Path, new[] { record },
            new DiskLayoutMigrator.MigrationOptions(RenameFirmwareFiles: false, FoldFilesIntoVersion: true))),
            renamed: null);

        Assert.Equal(Path.Combine(ctrl, HierarchyFolders.Hmi),
            VersionLayout.SlotBestReadFolder(dir, ctrl, HierarchyFolders.Hmi));
    }

    // ── Исполняемый файл панели во вложенной папке ──────────────────────────

    /// <summary>«Исполняемый потерялся»: файл панели лежит во ВЛОЖЕННОЙ папке проекта. После переезда
    /// проекта в «Прошивка\» он обязан находиться и подсказкой оператора, и детектом по расширениям —
    /// обе дороги идут по всему дереву, а не по верхнему уровню.</summary>
    [Fact]
    public void HmiExecutable_IsFound_WhenItSitsInANestedFolderOfTheMovedProject()
    {
        using var root = new TempRoot();
        var dir = Path.Combine(root.Path, "ПО", "ПЖ", "2.0", "SMH5", "1.0.0005.0001");
        Touch(Path.Combine(dir, "plc"), "prog.kpj");
        Touch(Path.Combine(dir, "hmi", "Панель", "Screens"), "panel.fsprj");
        var record = new FwVersionRecord { VersionRaw = "1.0.0005.0001", DiskPath = dir, Filename = "prog.kpj" };

        DiskLayoutMigrator.Apply(DiskLayoutMigrator.Plan(new DiskLayoutMigrator.MigrationInput(
            root.Path, new[] { record },
            new DiskLayoutMigrator.MigrationOptions(RenameFirmwareFiles: false, FoldFilesIntoVersion: true))),
            renamed: null);

        var moved = Path.Combine(VersionLayout.FirmwareFolder(dir), "hmi", "Панель", "Screens", "panel.fsprj");
        Assert.True(File.Exists(moved));

        // Детект по расширениям (HmiOpenResolver, источник 3) обходит папки версии со вложенными.
        Assert.Equal(moved, HmiOpenResolver.Resolve(new HmiOpenSources
        {
            CandidateFolders = VersionLayout.FirmwareFolders(dir),
            FilteredFolders = VersionLayout.FirmwareFolders(dir),
        }));

        // И подсказка оператора — она хранится относительным путём от папки прошивки.
        Assert.Equal(moved, ExecutableHintResolver.Resolve(VersionLayout.FirmwareFolder(dir),
            @"hmi\Панель\Screens\panel.fsprj"));
    }
}
