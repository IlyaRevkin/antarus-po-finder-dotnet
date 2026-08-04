using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Разовая перестройка уже накопленного диска. Свойства, без которых её нельзя выпускать
/// (docs/hierarchy-rework-plan.md, 4.1), и проверяются здесь: сухой прогон ничего не меняет,
/// повторный прогон не делает ничего, переименование не трогает многофайловые папки (там имя файла
/// привязано к executable_hint у коллег), а переезд инструкций идёт только на настроенный третий
/// диск.</summary>
public class DiskLayoutMigratorTests
{
    private sealed class RecordingShortcuts : IShortcutCreator
    {
        public List<(string Link, string Target)> Created { get; } = new();
        public void Create(string shortcutPath, string targetPath, string description)
        {
            Created.Add((shortcutPath, targetPath));
            File.WriteAllText(shortcutPath, "lnk");
        }
    }

    private static string Touch(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "x");
        return path;
    }

    /// <summary>Папка версии по канонической раскладке + запись о ней, как её видит вкладка
    /// «Прошивки».</summary>
    private static (FwVersionRecord Record, string Dir) MakeVersion(string root, string versionRaw,
        string fileName, string requestNum = "", string cabinetSn = "", string executableHint = "")
    {
        var dir = Path.Combine(root, "ПО", "ПЖ", "2.0", "SMH5", versionRaw);
        Touch(dir, fileName);
        return (new FwVersionRecord
        {
            VersionRaw = versionRaw,
            DiskPath = dir,
            Filename = fileName,
            RequestNum = requestNum,
            CabinetSn = cabinetSn,
            ExecutableHint = executableHint,
        }, dir);
    }

    private static DiskLayoutMigrator.MigrationInput Input(string root, string? third,
        IReadOnlyList<FwVersionRecord> versions, bool rename = true, bool instructions = false, bool shortcuts = false,
        bool fold = false, bool opc = false) =>
        new(root, third, shortcuts, versions, new DiskLayoutMigrator.MigrationOptions(rename, instructions, fold, opc));

    /// <summary>ОПЦ-версия в ПРЕЖНЕЙ раскладке: общая папка «ОПЦ» на уровне подтипа, имя папки —
    /// строка версии. Именно её и переносит этап 5.</summary>
    private static (FwVersionRecord Record, string Dir) MakeLegacyOpc(string root, string versionRaw,
        string fileName, string requestNum = "", string cabinetSn = "")
    {
        var dir = Path.Combine(root, "ПО", "ПЖ", "2.0", HierarchyFolders.Opc, versionRaw);
        Touch(dir, fileName);
        // Папка контроллера должна существовать — иначе переносить некуда, и план честно откажется.
        Directory.CreateDirectory(Path.Combine(root, "ПО", "ПЖ", "2.0", "SMH5"));
        return (new FwVersionRecord
        {
            Id = 7,
            VersionRaw = versionRaw,
            DiskPath = dir,
            Filename = fileName,
            IsOpc = true,
            RequestNum = requestNum,
            CabinetSn = cabinetSn,
            GroupName = "ПЖ",
            SubtypeName = "2.0",
            CtrlName = "SMH5",
        }, dir);
    }

    // ── Переименование файла прошивки ────────────────────────────────────────

    [Fact]
    public void Plan_DoesNotTouchDisk_AndRenamesToVersionFolderName()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersion(root.Path, "1.0.0004.0003.20260101_1200", "1.0.0004.0003_20260101_1200.PSL");

        var plan = DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { record }));

        var op = Assert.Single(plan.Ops);
        Assert.Equal(DiskLayoutMigrator.OpKind.RenameFirmware, op.Kind);
        Assert.Equal(Path.Combine(dir, "1.0.0004.0003.20260101_1200.psl"), op.Target);
        // Сухой прогон: файл на диске ещё под старым именем.
        Assert.True(File.Exists(Path.Combine(dir, "1.0.0004.0003_20260101_1200.PSL")));
        Assert.False(File.Exists(op.Target));
    }

    [Fact]
    public void Apply_RenamesFile_ReportsRenameForDb_AndIsIdempotent()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersion(root.Path, "1.0.0004.0003", "1.0.0004.0003_старое.psl");
        var renames = new List<DiskLayoutMigrator.Op>();

        DiskLayoutMigrator.Apply(DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { record })),
            renames.Add, shortcuts: null);

        Assert.True(File.Exists(Path.Combine(dir, "1.0.0004.0003.psl")));
        // Колбэк отдаёт ровно то, чем правятся filename/executable_hint у всех записей этой папки.
        var rename = Assert.Single(renames);
        Assert.Equal("1.0.0004.0003_старое.psl", rename.OldName);
        Assert.Equal("1.0.0004.0003.psl", rename.NewName);
        Assert.Equal(new[] { dir }, rename.RecordPaths);

        // Второй прогон видит уже переименованное и не делает ничего.
        Assert.Empty(DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { record })).Ops);
    }

    [Fact]
    public void Apply_RenameDifferingOnlyInCase_Succeeds()
    {
        using var root = new TempRoot();
        // Старые имена писались заглавными: для Windows «….PSL» и «….psl» — один и тот же файл, и
        // прямой File.Move на них падает «файл уже существует».
        var (record, dir) = MakeVersion(root.Path, "1.0.0004.0003", "1.0.0004.0003.PSL");

        DiskLayoutMigrator.Apply(DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { record })),
            renamed: null, shortcuts: null);

        var name = Path.GetFileName(Directory.EnumerateFiles(dir).Single());
        Assert.Equal("1.0.0004.0003.psl", name);
    }

    [Fact]
    public void Plan_MultiFileVersionFolder_IsSkippedWithReason()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersion(root.Path, "1.0.0004.0003", "ПРОЕКТ.PSL", executableHint: "ПРОЕКТ.PSL");
        Touch(dir, "ресурсы.bin");

        var plan = DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { record }));

        // Имя файла в такой папке — единственный носитель подсказки «чем открывать», а она у коллег
        // при импорте конфига не обновляется: переименуем — сломаем им «Открыть прошивку ПЛК».
        Assert.Empty(plan.Ops);
        Assert.Contains(plan.Skipped, s => s.Contains("в папке 2 файла"));
        Assert.True(File.Exists(Path.Combine(dir, "ПРОЕКТ.PSL")));
    }

    [Fact]
    public void Plan_ChangelogIsNotAFirmwareFile()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersion(root.Path, "1.0.0004.0003", "1.0.0004.0003.PSL");
        Touch(dir, "CHANGELOG.md");   // пишется самой программой — файлом прошивки не считается

        var op = Assert.Single(DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { record })).Ops);

        Assert.Equal(Path.Combine(dir, "1.0.0004.0003.psl"), op.Target);
    }

    [Fact]
    public void Plan_OpcMarkersStayInFilename()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersion(root.Path, "1.0.0036.0001", "старое.psl", requestNum: "01312", cabinetSn: "00042");

        var op = Assert.Single(DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { record })).Ops);

        // Заявка и SN живут ТОЛЬКО в имени файла (ParseOpcMarkers) — потерять их переименованием нельзя.
        Assert.Equal(Path.Combine(dir, "1.0.0036.0001_(01312)_SN00042.psl"), op.Target);
    }

    [Fact]
    public void Plan_SameFolderSharedBySeveralRecords_PlannedOnce()
    {
        using var root = new TempRoot();
        var (first, dir) = MakeVersion(root.Path, "1.0.0004.0003", "старое.psl");
        // Конфигурации одного шкафа делят папку версии — операций должно остаться столько же, сколько файлов.
        var second = new FwVersionRecord { VersionRaw = first.VersionRaw, DiskPath = dir, Filename = first.Filename };

        var plan = DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { first, second }));

        Assert.Single(plan.Ops);
    }

    [Fact]
    public void Plan_StaleDiskPath_KeepsBothPathsForDbUpdate()
    {
        using var root = new TempRoot();
        var (actual, dir) = MakeVersion(root.Path, "1.0.0004.0003.20260101_1200", "старое.psl");
        // У второй записи disk_path устарел: папку на диске переименовали, и найдена она соседом по
        // метке сборки (FirmwareDiskPresence). Если править базу только по найденной папке, у такой
        // записи в filename навсегда останется имя файла, которого на диске уже нет.
        var stale = new FwVersionRecord
        {
            VersionRaw = actual.VersionRaw,
            DiskPath = Path.Combine(Path.GetDirectoryName(dir)!, "1.0.0004.0002.20260101_1200"),
            Filename = actual.Filename,
        };

        var op = Assert.Single(DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { actual, stale })).Ops);

        Assert.Equal(dir, op.VersionDir);
        Assert.Equal(new[] { dir, stale.DiskPath }, op.RecordPaths);
    }

    // ── Переезд инструкций на третий диск ────────────────────────────────────

    [Fact]
    public void Instructions_MoveToThirdDisk_WithShortcutLeftBehind()
    {
        using var root = new TempRoot();
        using var third = new TempRoot();
        var instrFolder = Path.Combine(root.Path, "ПО", "ПЖ", "2.0", "SMH5", HierarchyFolders.Instructions);
        Touch(instrFolder, "инструкция.pdf");
        Touch(instrFolder, "старая.pdf.lnk");   // ярлык — не документ, его не переносим

        var shortcuts = new RecordingShortcuts();
        var plan = DiskLayoutMigrator.Apply(
            DiskLayoutMigrator.Plan(Input(root.Path, third.Path, Array.Empty<FwVersionRecord>(),
                rename: false, instructions: true, shortcuts: true)),
            renamed: null, shortcuts);

        var moved = Path.Combine(third.Path, "ПО", "ПЖ", "2.0", "SMH5", HierarchyFolders.Instructions, "инструкция.pdf");
        Assert.True(File.Exists(moved));
        Assert.False(File.Exists(Path.Combine(instrFolder, "инструкция.pdf")));
        Assert.Equal(Path.Combine(instrFolder, "инструкция.pdf.lnk"), Assert.Single(shortcuts.Created).Link);
        Assert.All(plan.Ops, op => Assert.Equal("ok", op.Status));

        // Повторный прогон: переносить больше нечего (ярлык документом не считается).
        Assert.Empty(DiskLayoutMigrator.Plan(Input(root.Path, third.Path, Array.Empty<FwVersionRecord>(),
            rename: false, instructions: true, shortcuts: true)).Ops);
    }

    [Fact]
    public void Instructions_ThirdDiskNotConfigured_NothingPlannedButReasonReported()
    {
        using var root = new TempRoot();
        Touch(Path.Combine(root.Path, "ПО", "ПЖ", HierarchyFolders.Instructions), "инструкция.pdf");

        var plan = DiskLayoutMigrator.Plan(Input(root.Path, "", Array.Empty<FwVersionRecord>(),
            rename: false, instructions: true));

        Assert.Empty(plan.Ops);
        Assert.Contains(plan.Skipped, s => s.Contains("Третий диск не настроен"));
    }

    // ── Этап 4: файлы прошивки внутрь «Прошивка\» ────────────────────────────

    /// <summary>Файлы уезжают в подпапку, CHANGELOG.md остаётся в корне папки версии (его читают по
    /// фиксированному пути), а сама папка версии не переименовывается — именно поэтому disk_path
    /// остаётся валидным у всех коллег, включая не обновившихся.</summary>
    [Fact]
    public void Fold_MovesFirmwareFilesOnly_AndKeepsVersionFolderName()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersion(root.Path, "1.0.0004.0003", "1.0.0004.0003.psl");
        Touch(dir, "ресурсы.bin");
        Touch(dir, ChangelogFile.FileName);

        DiskLayoutMigrator.Apply(
            DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { record }, rename: false, fold: true)),
            renamed: null, shortcuts: null);

        var inner = VersionLayout.FirmwareFolder(dir);
        Assert.True(File.Exists(Path.Combine(inner, "1.0.0004.0003.psl")));
        Assert.True(File.Exists(Path.Combine(inner, "ресурсы.bin")));
        // Журнал остался наверху, папка версии на месте.
        Assert.True(File.Exists(Path.Combine(dir, ChangelogFile.FileName)));
        Assert.True(Directory.Exists(dir));
        Assert.Equal(record.DiskPath, dir);
    }

    [Fact]
    public void Fold_IsIdempotent_AndSecondRunPlansNothing()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersion(root.Path, "1.0.0004.0003", "1.0.0004.0003.psl");

        DiskLayoutMigrator.Apply(
            DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { record }, rename: false, fold: true)),
            renamed: null, shortcuts: null);

        Assert.Empty(DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { record }, rename: false, fold: true)).Ops);
        Assert.Single(Directory.EnumerateFiles(VersionLayout.FirmwareFolder(dir)));
    }

    /// <summary>Прогон, прерванный обрывом шары, дочищается следующим: файл, оставшийся наверху,
    /// переезжает, а уже лежащий внизу одноимённый НЕ затирается — удалять чужую копию эта операция
    /// не вправе.</summary>
    [Fact]
    public void Fold_ResumesAfterInterruption_AndNeverOverwrites()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersion(root.Path, "1.0.0004.0003", "1.0.0004.0003.psl");
        var inner = VersionLayout.FirmwareFolder(dir);
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "1.0.0004.0003.psl"), "уже перенесён коллегой");
        Touch(dir, "второй.bin");

        DiskLayoutMigrator.Apply(
            DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { record }, rename: false, fold: true)),
            renamed: null, shortcuts: null);

        Assert.Equal("уже перенесён коллегой", File.ReadAllText(Path.Combine(inner, "1.0.0004.0003.psl")));
        Assert.True(File.Exists(Path.Combine(inner, "второй.bin")));
        // Свой одноимённый остался наверху и попадёт в следующий прогон.
        Assert.True(File.Exists(Path.Combine(dir, "1.0.0004.0003.psl")));
    }

    /// <summary>Перестройка заводит у версии все пять папок, а не одну «Прошивка»: ровно этого от неё
    /// и ждут — открыть папку версии в проводнике и увидеть, куда что класть. Пустая папка документа
    /// при этом не прячет общий документ контроллера (см. VersionLayout.SlotBestReadFolder и
    /// NewVersionLayoutWriteTests).</summary>
    [Fact]
    public void Fold_CreatesAllFiveVersionFolders()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersion(root.Path, "1.0.0004.0003", "1.0.0004.0003.psl");

        DiskLayoutMigrator.Apply(
            DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { record }, rename: false, fold: true)),
            renamed: null, shortcuts: null);

        Assert.True(VersionLayout.HasAllFolders(dir));
        foreach (var slot in VersionLayout.SlotFolderNames)
            Assert.True(Directory.Exists(VersionLayout.SlotFolder(dir, slot)), slot);
    }

    /// <summary>Файлы уже перенесены (или их удалили руками), но папок нет — прогон всё равно заводит
    /// их. Иначе версия, у которой перестройку прервали на середине, осталась бы полуготовой навсегда:
    /// файлы внизу, папок документов нет, и ни один следующий прогон её не увидит.</summary>
    [Fact]
    public void Fold_VersionWithoutFiles_StillGetsItsFolders()
    {
        using var root = new TempRoot();
        var dir = Path.Combine(root.Path, "ПО", "ПЖ", "2.0", "SMH5", "1.0.0004.0003");
        Directory.CreateDirectory(dir);
        var record = new FwVersionRecord { VersionRaw = "1.0.0004.0003", DiskPath = dir };

        var plan = DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { record }, rename: false, fold: true));
        Assert.Single(plan.Ops);
        Assert.Contains("папки версии", plan.Ops[0].Note);

        DiskLayoutMigrator.Apply(plan, renamed: null, shortcuts: null);
        Assert.True(VersionLayout.HasAllFolders(dir));
        Assert.Equal("ok", plan.Ops[0].Status);

        // И на этом всё: повторный прогон видеть тут больше нечего.
        Assert.Empty(DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { record }, rename: false, fold: true)).Ops);
    }

    /// <summary>Папки версии на диске нет вовсе (запись пережила удаление папки) — перестройка её не
    /// трогает и, главное, НЕ создаёт дерево папок там, где прошивки не было: иначе один прогон
    /// насыпал бы на диск пустых «версий» по всем осиротевшим записям базы.</summary>
    [Fact]
    public void Fold_MissingVersionFolder_IsNotPlannedAndNothingIsCreated()
    {
        using var root = new TempRoot();
        var dir = Path.Combine(root.Path, "ПО", "ПЖ", "2.0", "SMH5", "1.0.0004.0003");
        var record = new FwVersionRecord { VersionRaw = "1.0.0004.0003", DiskPath = dir };

        var plan = DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { record }, rename: false, fold: true));

        Assert.Empty(plan.Ops);
        Assert.False(Directory.Exists(dir));
    }

    // ── Этап 5: ОПЦ внутрь контроллера ───────────────────────────────────────

    /// <summary>Папка уезжает в «ОПЦ» своего контроллера под именем заявки/SN, disk_path правится
    /// колбэком и только ПОСЛЕ удачного переноса, а номер версии остаётся восстановимым — журнал
    /// дописан до переименования.</summary>
    [Fact]
    public void Opc_MovesInsideController_WritesChangelogFirst_AndReportsRepoint()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeLegacyOpc(root.Path, "3.0.005.0777", "3.0.005.0777.psl", "01312", "00042");
        var repoints = new List<DiskLayoutMigrator.Op>();

        DiskLayoutMigrator.Apply(
            DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { record }, rename: false, opc: true)),
            renamed: null, shortcuts: null, repointed: repoints.Add);

        var moved = Path.Combine(root.Path, "ПО", "ПЖ", "2.0", "SMH5", HierarchyFolders.Opc, "01312_SN00042");
        Assert.True(Directory.Exists(moved));
        Assert.False(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(moved, "3.0.005.0777.psl")));

        // Номер версии восстановим по журналу — имя папки им больше не является.
        Assert.Equal("3.0.005.0777", OpcLayout.ResolveVersion(moved)?.Raw);

        var repoint = Assert.Single(repoints);
        Assert.Equal(7, repoint.FwVersionId);
        Assert.Equal(moved, repoint.Target);
    }

    /// <summary>Существующий CHANGELOG.md операция не переписывает: там уже лежат описание и типы
    /// пуска, и потерять их переносом папки нельзя.</summary>
    [Fact]
    public void Opc_ExistingChangelogIsNotOverwritten()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeLegacyOpc(root.Path, "3.0.005.0777", "3.0.005.0777.psl", "01312");
        ChangelogFile.Write(dir, FwVersionNumber.Parse("3.0.005.0777")!, new[] { "УПП" }, "правки коллеги", new[] { "тег" });
        var before = File.ReadAllText(Path.Combine(dir, ChangelogFile.FileName));

        DiskLayoutMigrator.Apply(
            DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { record }, rename: false, opc: true)),
            renamed: null, shortcuts: null);

        var moved = Path.Combine(root.Path, "ПО", "ПЖ", "2.0", "SMH5", HierarchyFolders.Opc, "01312");
        Assert.Equal(before, File.ReadAllText(Path.Combine(moved, ChangelogFile.FileName)));
    }

    /// <summary>Две сборки под один шкаф дают одно имя папки — вторая уходит в Skipped с внятной
    /// причиной, а не затирает первую.</summary>
    [Fact]
    public void Opc_TargetNameTaken_IsSkippedRatherThanOverwritten()
    {
        using var root = new TempRoot();
        var (first, _) = MakeLegacyOpc(root.Path, "3.0.005.0777", "a.psl", "01312");
        var (second, secondDir) = MakeLegacyOpc(root.Path, "3.0.005.0778", "b.psl", "01312");

        var plan = DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { first, second }, rename: false, opc: true));

        Assert.Single(plan.Ops);
        Assert.Contains(plan.Skipped, s => s.Contains("уже занято"));
        DiskLayoutMigrator.Apply(plan, renamed: null, shortcuts: null);
        // Вторая осталась на месте со своим файлом — ничего не потеряно.
        Assert.True(File.Exists(Path.Combine(secondDir, "b.psl")));
    }

    [Fact]
    public void Opc_SecondRunPlansNothing()
    {
        using var root = new TempRoot();
        var (record, _) = MakeLegacyOpc(root.Path, "3.0.005.0777", "3.0.005.0777.psl", "01312");

        var plan = DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { record }, rename: false, opc: true));
        DiskLayoutMigrator.Apply(plan, renamed: null, shortcuts: null, repointed: op => record.DiskPath = op.Target);

        Assert.Empty(DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { record }, rename: false, opc: true)).Ops);
    }

    /// <summary>Порядок этапов в одном прогоне: сначала «Прошивка\» внутри версии, потом переезд ОПЦ.
    /// Наоборот было бы неверно — переехавшая папка перестала бы совпадать с disk_path, по которому
    /// этап 4 ищет свои папки в этом же прогоне.</summary>
    [Fact]
    public void FoldAndOpc_InOneRun_FirmwareFolderTravelsWithTheMovedFolder()
    {
        using var root = new TempRoot();
        var (record, _) = MakeLegacyOpc(root.Path, "3.0.005.0777", "3.0.005.0777.psl", "01312");

        DiskLayoutMigrator.Apply(
            DiskLayoutMigrator.Plan(Input(root.Path, null, new[] { record }, rename: false, fold: true, opc: true)),
            renamed: null, shortcuts: null);

        var moved = Path.Combine(root.Path, "ПО", "ПЖ", "2.0", "SMH5", HierarchyFolders.Opc, "01312");
        Assert.True(File.Exists(Path.Combine(VersionLayout.FirmwareFolder(moved), "3.0.005.0777.psl")));
    }
}
