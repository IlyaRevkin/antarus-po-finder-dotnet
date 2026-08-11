using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Чистильщик мусора на диске прошивок. Половина тестов здесь — про то, чего он делать НЕ
/// должен, и это не перестраховка: просьба Ильи заканчивалась словами «нужно продумать как сделать
/// чтобы работало когда допустим может быть svg или какие-то файлы для работы плк которые шьются не
/// бинарником как кинко или овен чтобы они в мусор не улетели». Ошибочно удалённый файл прошивки
/// стоит дороже любого количества незамеченного мусора.</summary>
public class DiskCleanupScannerTests
{
    /// <summary>Умолчания белых списков из БД (Database.HierarchySeed): ПЛК, HMI, схемы. «svg» ни в
    /// один из них не входит — на этом и держится проверка «незнакомое не удаляем».</summary>
    private static readonly string[] Plc = { "psl", "lfs", "kpr", "kpj", "dpj" };
    private static readonly string[] Hmi = { "fsprj", "emt", "emtp", "emsln" };
    private static readonly string[] Schematic = { "pdf", "dwg", "dxf", "jpg", "png" };

    private static string Touch(string folder, string name, string content = "x")
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Папка версии по НОВОЙ раскладке: «Прошивка» + четыре папки документов.</summary>
    private static (FwVersionRecord Record, string Dir) MakeVersion(string root, string versionRaw,
        string filename = "", string executableHint = "")
    {
        var dir = Path.Combine(root, "ПО", "ПЖ", "2.0", "SMH5", versionRaw);
        VersionLayout.EnsureFolders(dir);
        return (new FwVersionRecord
        {
            VersionRaw = versionRaw,
            DiskPath = dir,
            Filename = filename,
            ExecutableHint = executableHint,
        }, dir);
    }

    private static DiskCleanupScanner.CleanupInput Input(string root, params FwVersionRecord[] versions) =>
        new(root, versions, Plc, Hmi, Schematic, System.Array.Empty<string>());

    private static DiskCleanupScanner.Finding? Find(DiskCleanupScanner.CleanupPlan plan, string path) =>
        plan.Findings.FirstOrDefault(f => f.Path == path);

    // ── Случай из жалобы ─────────────────────────────────────────────────────

    /// <summary>Дословно: «в папке ПЖ у меня лежит файл пж_smh5_4.36.psl и он лежит по пути
    /// ..\ПО\ПЖ\2.0\SMH5\1.0.0005.0001\Прошивка — соответственно он должен предложить переименовать
    /// файл в 1.0.0005.0001.psl. А ещё там лежит файл инструкции, он мусор, его убрать». Обе находки
    /// обязаны появиться в ОДНОМ прогоне: инструкция рядом с прошивкой не должна отменять
    /// переименование (мигратор на таком отказывается — см. DiskCleanupScanner.PlanRename).</summary>
    [Fact]
    public void Plan_ComplaintCase_RenamesFirmwareAndMovesInstructionOut()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersion(root.Path, "1.0.0005.0001");
        var firmware = Touch(VersionLayout.FirmwareFolder(dir), "пж_smh5_4.36.psl");
        var instruction = Touch(VersionLayout.FirmwareFolder(dir), "Инструкция ПЖ 2.0.pdf");

        var plan = DiskCleanupScanner.Plan(Input(root.Path, record));

        var rename = Find(plan, firmware);
        Assert.NotNull(rename);
        Assert.Equal(DiskCleanupScanner.Issue.FirmwareName, rename!.Issue);
        Assert.Equal(DiskCleanupScanner.Act.Rename, rename.Action);
        Assert.True(rename.Selected);
        Assert.Equal(Path.Combine(VersionLayout.FirmwareFolder(dir), "1.0.0005.0001.psl"), rename.Target);

        var move = Find(plan, instruction);
        Assert.NotNull(move);
        Assert.Equal(DiskCleanupScanner.Issue.WrongFolder, move!.Issue);
        Assert.Equal(DiskCleanupScanner.Act.Move, move.Action);
        Assert.True(move.Selected);
        // Едет сразу под каноническим именем инструкции — иначе следующая же перестройка диска
        // переименовала бы её второй операцией.
        Assert.Equal(Path.Combine(VersionLayout.SlotFolder(dir, HierarchyFolders.Instructions),
            "инструкция_1.0.0005.0001.pdf"), move.Target);

        // Сухой прогон: на диске ничего не поменялось.
        Assert.True(File.Exists(firmware));
        Assert.True(File.Exists(instruction));
        Assert.False(File.Exists(rename.Target));
        Assert.False(File.Exists(move.Target));
    }

    [Fact]
    public void Apply_ComplaintCase_MovesAndRenames_ReportsDbRename_AndIsIdempotent()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersion(root.Path, "1.0.0005.0001", filename: "пж_smh5_4.36.psl");
        Touch(VersionLayout.FirmwareFolder(dir), "пж_smh5_4.36.psl");
        Touch(VersionLayout.FirmwareFolder(dir), "Инструкция ПЖ 2.0.pdf");

        var renamed = new List<DiskCleanupScanner.Finding>();
        DiskCleanupScanner.Apply(DiskCleanupScanner.Plan(Input(root.Path, record)), renamed.Add);

        Assert.True(File.Exists(Path.Combine(VersionLayout.FirmwareFolder(dir), "1.0.0005.0001.psl")));
        Assert.True(File.Exists(Path.Combine(VersionLayout.SlotFolder(dir, HierarchyFolders.Instructions),
            "инструкция_1.0.0005.0001.pdf")));
        Assert.False(File.Exists(Path.Combine(VersionLayout.FirmwareFolder(dir), "пж_smh5_4.36.psl")));

        // Колбэк отдаёт ровно то, чем правится filename/executable_hint: без этого база осталась бы
        // с именем файла, которого на диске уже нет.
        var report = Assert.Single(renamed);
        Assert.Equal("пж_smh5_4.36.psl", report.OldName);
        Assert.Equal("1.0.0005.0001.psl", report.NewName);
        Assert.Equal(new[] { dir }, report.RecordPaths);

        // Повторный прогон не находит ничего.
        Assert.Empty(DiskCleanupScanner.Plan(Input(root.Path, record)).Findings);
    }

    // ── Чего трогать нельзя ──────────────────────────────────────────────────

    /// <summary>«.svg» не входит ни в один белый список — и всё равно не мусор: он попадает в
    /// «нужно решить», без предложенного действия и без галочки.</summary>
    [Fact]
    public void Plan_SvgIsNeverJunk_AndIsNotPreselected()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersion(root.Path, "1.0.0005.0001", filename: "1.0.0005.0001.psl");
        Touch(VersionLayout.FirmwareFolder(dir), "1.0.0005.0001.psl");
        var svg = Touch(VersionLayout.FirmwareFolder(dir), "мнемосхема.svg");

        var plan = DiskCleanupScanner.Plan(Input(root.Path, record));

        var finding = Find(plan, svg);
        Assert.NotNull(finding);
        Assert.Equal(DiskCleanupScanner.Issue.NeedsDecision, finding!.Issue);
        Assert.Equal(DiskCleanupScanner.Act.None, finding.Action);
        Assert.False(finding.Selected);

        // И даже прогон «выполнить всё отмеченное» его не трогает.
        DiskCleanupScanner.Apply(plan, renamed: null);
        Assert.True(File.Exists(svg));
    }

    /// <summary>Проект KINCO шьётся не бинарником: .kpr/.kpj/.dpj стоят в белом списке ПЛК, поэтому
    /// находкой не становятся вовсе.</summary>
    [Fact]
    public void Plan_KincoProjectFiles_AreNotFindingsAtAll()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersion(root.Path, "1.0.0005.0001");
        var kpr = Touch(VersionLayout.FirmwareFolder(dir), "1.0.0005.0001.kpr");
        var dpj = Touch(VersionLayout.FirmwareFolder(dir), "1.0.0005.0001.dpj");

        var plan = DiskCleanupScanner.Plan(Input(root.Path, record));

        Assert.Null(Find(plan, kpr));
        Assert.Null(Find(plan, dpj));
        // Два файла с расширениями из белого списка — переименование не предлагается вовсе:
        // какой из них «главный», знает только executable_hint.
        Assert.DoesNotContain(plan.Findings, f => f.Issue == DiskCleanupScanner.Issue.FirmwareName);
        Assert.NotEmpty(plan.Skipped);
    }

    /// <summary>Расширение, добавленное оператором в белый список, перевешивает даже закрытый список
    /// служебного мусора: «.bak» в списке мусора, но раз его внесли в allowed_extensions — это
    /// рабочий формат чьего-то ПЛК, а не огрызок.</summary>
    [Fact]
    public void Plan_WhitelistedExtension_BeatsTheJunkList()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersion(root.Path, "1.0.0005.0001");
        var file = Touch(VersionLayout.FirmwareFolder(dir), "1.0.0005.0001.bak");

        var plan = DiskCleanupScanner.Plan(new DiskCleanupScanner.CleanupInput(
            root.Path, new[] { record }, new[] { "psl", "bak" }, Hmi, Schematic,
            System.Array.Empty<string>()));

        Assert.Null(Find(plan, file));
    }

    /// <summary>Файл, на который ссылается запись в базе, не удаляется и не объявляется непонятным —
    /// даже если его расширение неизвестно ни одному белому списку (ровно случай «шьётся не
    /// бинарником»: оператор выбрал файл подсказкой «чем открывать»).</summary>
    [Fact]
    public void Plan_FileReferencedByDb_IsLeftAlone()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersion(root.Path, "1.0.0005.0001",
            filename: "1.0.0005.0001.psl", executableHint: "owen_boot.dat");
        Touch(VersionLayout.FirmwareFolder(dir), "1.0.0005.0001.psl");
        var hinted = Touch(VersionLayout.FirmwareFolder(dir), "owen_boot.dat");

        var plan = DiskCleanupScanner.Plan(Input(root.Path, record));

        Assert.Null(Find(plan, hinted));
    }

    /// <summary>Внутрь папки проекта чистильщик не заходит: проект это единое целое, и лежащий в нём
    /// временный файл — дело среды разработки, а не чистки.</summary>
    [Fact]
    public void Plan_DoesNotDescendIntoProjectFolders()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersion(root.Path, "1.0.0005.0001");
        var inner = Touch(Path.Combine(VersionLayout.FirmwareFolder(dir), "plc_project"), "build.tmp");

        var plan = DiskCleanupScanner.Plan(Input(root.Path, record));

        Assert.Null(Find(plan, inner));
        Assert.Empty(plan.Findings);
    }

    // ── Мусор и перенос ──────────────────────────────────────────────────────

    [Fact]
    public void Plan_ServiceJunk_IsProposedForDeletion_ButNotPreselected()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersion(root.Path, "1.0.0005.0001");
        var thumbs = Touch(VersionLayout.FirmwareFolder(dir), "Thumbs.db");
        var lockFile = Touch(VersionLayout.SlotFolder(dir, HierarchyFolders.Instructions), "~$инструкция.docx");
        var empty = Touch(VersionLayout.FirmwareFolder(dir), "обрыв.dat", content: "");

        var plan = DiskCleanupScanner.Plan(Input(root.Path, record));

        foreach (var path in new[] { thumbs, lockFile, empty })
        {
            var finding = Find(plan, path);
            Assert.NotNull(finding);
            Assert.Equal(DiskCleanupScanner.Issue.Junk, finding!.Issue);
            Assert.Equal(DiskCleanupScanner.Act.Delete, finding.Action);
            Assert.False(finding.Selected);
        }

        // Ничего не отмечено — «выполнить отмеченное» оставляет диск как был.
        DiskCleanupScanner.Apply(plan, renamed: null);
        Assert.True(File.Exists(thumbs));

        foreach (var f in plan.Findings) f.Selected = true;
        DiskCleanupScanner.Apply(plan, renamed: null);
        Assert.False(File.Exists(thumbs));
        Assert.False(File.Exists(lockFile));
        Assert.False(File.Exists(empty));
        Assert.Empty(DiskCleanupScanner.Plan(Input(root.Path, record)).Findings);
    }

    /// <summary>Файл прошивки и папка проекта, оставшиеся в корне уже перестроенной версии, — не
    /// мусор, а «не в своей папке»: их место в «Прошивка».</summary>
    [Fact]
    public void Plan_FirmwareAndProjectFolderInVersionRoot_MoveIntoFirmwareFolder()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersion(root.Path, "1.0.0005.0001", filename: "1.0.0005.0001.psl");
        var stray = Touch(dir, "1.0.0005.0001.psl");
        Directory.CreateDirectory(Path.Combine(dir, "plc"));
        // CHANGELOG.md остаётся в корне версии — его читает досмотр диска по фиксированному пути.
        var changelog = Touch(dir, ChangelogFile.FileName, "# 1.0.0005.0001\n");

        var plan = DiskCleanupScanner.Plan(Input(root.Path, record));
        Assert.Null(Find(plan, changelog));

        DiskCleanupScanner.Apply(plan, renamed: null);

        Assert.True(File.Exists(Path.Combine(VersionLayout.FirmwareFolder(dir), "1.0.0005.0001.psl")));
        Assert.True(Directory.Exists(Path.Combine(VersionLayout.FirmwareFolder(dir), "plc")));
        Assert.False(File.Exists(stray));
        Assert.True(File.Exists(changelog));
        Assert.Empty(DiskCleanupScanner.Plan(Input(root.Path, record)).Findings);
    }

    /// <summary>Занятая цель не перезаписывается: одноимённый файл в «Прошивка» — чужая работа, и
    /// затирать её чистка не вправе. Операция уходит в skip, свой файл остаётся на месте и попадёт
    /// в следующий прогон.</summary>
    [Fact]
    public void Apply_TargetTaken_SkipsInsteadOfOverwriting()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersion(root.Path, "1.0.0005.0001");
        var stray = Touch(dir, "1.0.0005.0001.psl", "наш");
        Touch(VersionLayout.FirmwareFolder(dir), "1.0.0005.0001.psl", "чужой");

        var plan = DiskCleanupScanner.Plan(Input(root.Path, record));
        DiskCleanupScanner.Apply(plan, renamed: null);

        Assert.Equal("чужой", File.ReadAllText(Path.Combine(VersionLayout.FirmwareFolder(dir), "1.0.0005.0001.psl")));
        Assert.Equal("наш", File.ReadAllText(stray));
        Assert.Contains(plan.Findings, f => f.Status == "skip");
    }

    /// <summary>Действие по строке меняет человек: незнакомый файл, помеченный им как мусор,
    /// удаляется — но только после того, как он сам это выбрал.</summary>
    [Fact]
    public void Apply_HonoursActionChangedByOperator()
    {
        using var root = new TempRoot();
        var (record, dir) = MakeVersion(root.Path, "1.0.0005.0001");
        var junk = Touch(VersionLayout.FirmwareFolder(dir), "заметки.txt");

        var plan = DiskCleanupScanner.Plan(Input(root.Path, record));
        var finding = Find(plan, junk);
        Assert.NotNull(finding);
        Assert.Contains(DiskCleanupScanner.Act.Delete, finding!.AllowedActions);

        finding.Action = DiskCleanupScanner.Act.Delete;
        finding.Selected = true;
        DiskCleanupScanner.Apply(plan, renamed: null);

        Assert.False(File.Exists(junk));
    }
}
