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
        IReadOnlyList<FwVersionRecord> versions, bool rename = true, bool instructions = false, bool shortcuts = false) =>
        new(root, third, shortcuts, versions, new DiskLayoutMigrator.MigrationOptions(rename, instructions));

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
}
