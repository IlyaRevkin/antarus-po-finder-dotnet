using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Дерево проекта среды разработки: «папка + одноимённый файл проекта + подпапки ресурсов».
///
/// Жалоба владельца дословно: «опять с кинко проблема: ты переименовываешь файл, а папка старой
/// остаётся, и в итоге он из-за расхождения названий не может найти расширения. Важно, чтобы оно
/// нормально работало, и это должно работать не только для конкретного ПЛК, а в целом для разных
/// производителей, чтобы работало универсально».
///
/// Образец с диска (KINCO, «v8.26 (Котельная)»): папка «УПД_MK070_v8-26», внутри неё
/// «УПД_MK070_v8-26.dpj», «.pkgx», «.bak» и подпапки image, sound, vg, tar, HMI0, temp. Имя папки и
/// имя файла проекта совпадают — по нему среда их и связывает.
///
/// Здесь проверяется ОБЩЕЕ правило (<see cref="ProjectTree"/>), а не «список расширений KINCO»:
/// признак структурный, поэтому те же проверки идут по расширениям разных производителей.</summary>
public class ProjectTreeRenameTests
{
    /// <summary>Папка проекта произвольного вендора: файл, названный как папка, плюс подпапка
    /// ресурсов и резервная копия рядом — ровно то, что лежит в образце с диска.</summary>
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

    // ── Само правило ────────────────────────────────────────────────────────

    /// <summary>KINCO (.dpj), Codesys (.project), Weintek (.cmtp), Delta (.dvp), ОВЕН (.owen) — пять
    /// разных вендоров и ни одного списка расширений в коде: узнаётся строение, а не имя формата.</summary>
    [Theory]
    [InlineData(".dpj")]
    [InlineData(".project")]
    [InlineData(".cmtp")]
    [InlineData(".dvp")]
    [InlineData(".owen")]
    public void ProjectTree_IsRecognisedByShape_ForAnyVendor(string ext)
    {
        using var root = new TempRoot();
        var folder = MakeProject(root.Path, "УПД_MK070_v8-26", ext);
        var entry = Path.Combine(folder, "УПД_MK070_v8-26" + ext);

        Assert.True(ProjectTree.IsProjectTree(folder));
        Assert.True(ProjectTree.IsEntryFile(entry));
        Assert.True(ProjectTree.RenameWouldBreak(entry));
        Assert.NotNull(ProjectTree.WhyRenameWouldBreak(entry));
    }

    /// <summary>Тот же проект без подпапок: KINCO рядом с «.dpj» держит «.pkgx» и «.bak» под тем же
    /// именем, и пары «папка + одноимённый файл» уже достаточно. Отдельный тест, потому что в
    /// предыдущем срабатывает первый признак (окружение в подпапках) и второй остаётся непроверенным.</summary>
    [Fact]
    public void ProjectTree_FolderAndFileOfTheSameName_IsEnough()
    {
        using var root = new TempRoot();
        var folder = Path.Combine(root.Path, "УПД_MK070_v8-26");
        Directory.CreateDirectory(folder);
        var entry = Path.Combine(folder, "УПД_MK070_v8-26.dpj");
        File.WriteAllText(entry, "проект");
        File.WriteAllText(Path.Combine(folder, "УПД_MK070_v8-26.pkgx"), "пакет");

        Assert.True(ProjectTree.IsProjectTree(folder));
        Assert.True(ProjectTree.RenameWouldBreak(entry));
        Assert.Contains("назван так же, как папка", ProjectTree.WhyRenameWouldBreak(entry));
    }

    /// <summary>Обратная сторона правила: НАША папка версии с единственным файлом внутри деревом
    /// проекта не является, и приводить её файл к каноническому имени по-прежнему обязаны. Без этой
    /// проверки «защита» тихо отменила бы то, ради чего чистильщик диска и делался.</summary>
    [Fact]
    public void OurVersionFolder_WithASingleFile_IsNotAProjectTree()
    {
        using var root = new TempRoot();
        var dir = Path.Combine(root.Path, "1.0.0005.0001");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "1.0.0005.0001.psl");
        File.WriteAllText(file, "прошивка");

        Assert.False(ProjectTree.IsProjectTree(dir));
        Assert.False(ProjectTree.RenameWouldBreak(file));
    }

    // ── Перестройка раскладки диска ─────────────────────────────────────────

    /// <summary>Перестройка диска делает ту же операцию, что и чистильщик, но защиты от дерева
    /// проекта у неё не было вовсе: файл переименовывался, а окружение оставалось со своими именами.
    /// Проект KINCO, высыпанный в «Прошивка\», — именно этот случай.</summary>
    [Fact]
    public void Migrator_ProjectTreeInFirmwareFolder_IsNotRenamed()
    {
        using var root = new TempRoot();
        var dir = Path.Combine(root.Path, "ПО", "ПЖ", "2.0", "MK070", "1.0.0005.0001");
        var fw = VersionLayout.FirmwareFolder(dir);
        Directory.CreateDirectory(fw);
        var entry = Path.Combine(fw, "УПД_MK070_v8-26.dpj");
        File.WriteAllText(entry, "проект");
        Directory.CreateDirectory(Path.Combine(fw, "image"));
        File.WriteAllText(Path.Combine(fw, "image", "logo.png"), "картинка");

        var record = new FwVersionRecord { VersionRaw = "1.0.0005.0001", DiskPath = dir, Filename = "УПД_MK070_v8-26.dpj" };
        var plan = DiskLayoutMigrator.Plan(new DiskLayoutMigrator.MigrationInput(root.Path,
            new[] { record }, new DiskLayoutMigrator.MigrationOptions(true, false, false)));

        Assert.DoesNotContain(plan.Ops, o => o.Kind == DiskLayoutMigrator.OpKind.RenameFirmware);
        Assert.Contains(plan.Skipped, s => s.Contains("папки проекта"));
        Assert.True(File.Exists(entry));
    }

    /// <summary>И наоборот: одинокий файл прошивки перестройка по-прежнему приводит к каноническому
    /// имени. Проверка стоит рядом намеренно — она ловит защиту, отменившую всё подряд.</summary>
    [Fact]
    public void Migrator_LoneFirmwareFile_IsStillRenamed()
    {
        using var root = new TempRoot();
        var dir = Path.Combine(root.Path, "ПО", "ПЖ", "2.0", "SMH5", "1.0.0005.0001");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "пж_smh5_4.36.psl"), "прошивка");

        var record = new FwVersionRecord { VersionRaw = "1.0.0005.0001", DiskPath = dir, Filename = "пж_smh5_4.36.psl" };
        var plan = DiskLayoutMigrator.Plan(new DiskLayoutMigrator.MigrationInput(root.Path,
            new[] { record }, new DiskLayoutMigrator.MigrationOptions(true, false, false)));

        Assert.Contains(plan.Ops, o => o.Kind == DiskLayoutMigrator.OpKind.RenameFirmware
                                       && Path.GetFileName(o.Target) == "1.0.0005.0001.psl");
    }

    // ── Копия версии под другой подтип ──────────────────────────────────────

    /// <summary>Копия версии переименовывает файл прошивки в каноническое имя НОВОГО номера. Внутри
    /// папки проекта это и давало жалобу: файл получал новое имя, а папка вокруг него оставалась со
    /// старым — «из-за расхождения названий не может найти расширения».</summary>
    [Fact]
    public void VersionCopy_ProjectTree_KeepsFolderAndFileNamesTogether()
    {
        using var root = new TempRoot();
        var src = Path.Combine(root.Path, "1.0.0005.0001");
        var srcFw = VersionLayout.FirmwareFolder(src);
        Directory.CreateDirectory(srcFw);
        MakeProject(srcFw, "УПД_MK070_v8-26", ".dpj");
        var dst = Path.Combine(root.Path, "1.1.0005.0001");

        var result = VersionFolderCopy.Copy(src, dst, "1.0.0005.0001", "1.1.0005.0001",
            "УПД_MK070_v8-26.dpj", "1.1.0005.0001.dpj");

        var copied = Path.Combine(VersionLayout.FirmwareFolder(dst), "УПД_MK070_v8-26");
        Assert.True(File.Exists(Path.Combine(copied, "УПД_MK070_v8-26.dpj")));
        Assert.False(File.Exists(Path.Combine(copied, "1.1.0005.0001.dpj")));
        // В базе должно оказаться то же, что на диске, иначе «открыть прошивку» промахнётся.
        Assert.Equal("УПД_MK070_v8-26.dpj", result.FirmwareFileName);
    }

    /// <summary>Обычный файл прошивки копия по-прежнему переименовывает: у копии свой номер версии,
    /// и имя обязано быть каноническим.</summary>
    [Fact]
    public void VersionCopy_LoneFirmwareFile_IsStillRenamed()
    {
        using var root = new TempRoot();
        var src = Path.Combine(root.Path, "1.0.0005.0001");
        var srcFw = VersionLayout.FirmwareFolder(src);
        Directory.CreateDirectory(srcFw);
        File.WriteAllText(Path.Combine(srcFw, "пж_smh5_4.36.psl"), "прошивка");
        var dst = Path.Combine(root.Path, "1.1.0005.0001");

        var result = VersionFolderCopy.Copy(src, dst, "1.0.0005.0001", "1.1.0005.0001",
            "пж_smh5_4.36.psl", "1.1.0005.0001.psl");

        Assert.Equal("1.1.0005.0001.psl", result.FirmwareFileName);
        Assert.True(File.Exists(Path.Combine(VersionLayout.FirmwareFolder(dst), "1.1.0005.0001.psl")));
    }
}
