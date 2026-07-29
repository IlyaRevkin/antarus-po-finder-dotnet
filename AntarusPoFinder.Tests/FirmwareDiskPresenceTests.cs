using System.IO;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Жалоба: «выбрал фильтр — найдено 0 и приписка “скрыто отсутствующих на диске”, но
/// прошивка на диске есть и hw я переименовал». Причина — папку версии переименовали на диске
/// (откат дописал «_ОТКАТАНО», правка hw сменила имя), а disk_path в базе остался прежним: точной
/// папки по нему нет, и версию без локальной копии прятали как «удалённую». Файлы при этом лежат
/// рядом, под соседним именем с тем же номером версии — FirmwareDiskPresence обязана это увидеть.</summary>
public class FirmwareDiskPresenceTests
{
    private static string CtrlDir(TempRoot root) =>
        Path.Combine(root.Path, "ПО", "ПЖ", "КПЧ", "SMH4");

    private static string SeedVersionFolder(TempRoot root, string folderName)
    {
        var dir = Path.Combine(CtrlDir(root), folderName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "fw.psl"), "x");
        return dir;
    }

    [Fact]
    public void ExactFolderWithFiles_IsPresent()
    {
        using var root = new TempRoot();
        var raw = "1.1.4.36.20260716_0848";
        var dir = SeedVersionFolder(root, raw);

        Assert.True(FirmwareDiskPresence.VersionPresentOnDisk(dir, raw));
    }

    [Fact]
    public void RolledBackRename_ExactPathMissing_ButSiblingWithSameNumberIsPresent()
    {
        using var root = new TempRoot();
        var raw = "1.1.4.38.20260716_1814";
        // На диске папку переименовали при откате; disk_path в базе указывает на прежнее имя.
        SeedVersionFolder(root, raw + "_ОТКАТАНО");
        var stalePath = Path.Combine(CtrlDir(root), raw);

        Assert.False(Directory.Exists(stalePath));
        Assert.True(FirmwareDiskPresence.VersionPresentOnDisk(stalePath, raw));
    }

    [Fact]
    public void ReDatedRename_SameHwSw_DifferentDate_IsPresent()
    {
        using var root = new TempRoot();
        // Перезалили ту же версию с другой датой — номер (eq.sub.hw.sw) тот же.
        SeedVersionFolder(root, "1.1.4.38.20260801_0900");
        var stalePath = Path.Combine(CtrlDir(root), "1.1.4.38.20260716_1814");

        Assert.True(FirmwareDiskPresence.VersionPresentOnDisk(stalePath, "1.1.4.38.20260716_1814"));
    }

    [Fact]
    public void GenuinelyDeleted_ControllerFolderExists_NoMatchingSibling_IsAbsent()
    {
        using var root = new TempRoot();
        // В папке контроллера есть другие версии, но именно этой (с её номером) — нет.
        SeedVersionFolder(root, "1.1.4.40.20260801_0900");
        var deleted = Path.Combine(CtrlDir(root), "1.1.4.99.20260101_0000");

        Assert.False(FirmwareDiskPresence.VersionPresentOnDisk(deleted, "1.1.4.99.20260101_0000"));
    }

    [Fact]
    public void RenamedHw_SameBuildStamp_IsPresent()
    {
        using var root = new TempRoot();
        // hw переписали прямо на диске (1.1.4 → 1.1.5), а метку даты-времени сборки не трогали —
        // это ТА ЖЕ сборка под другим именем, файлы на месте. Прятать её нельзя: ровно жалоба
        // «прошивка есть на диске и hw я переименовал, а поиск её не находит».
        SeedVersionFolder(root, "1.1.5.38.20260716_1814");
        var stalePath = Path.Combine(CtrlDir(root), "1.1.4.38.20260716_1814");

        Assert.True(FirmwareDiskPresence.VersionPresentOnDisk(stalePath, "1.1.4.38.20260716_1814"));
    }

    [Fact]
    public void DifferentHwAndDate_DoesNotCountAsThisVersion()
    {
        using var root = new TempRoot();
        // Другой hw И другая дата-время — это ДРУГАЯ сборка, а не переименованная наша: за нашу
        // версию её выдавать нельзя (иначе настоящее удаление маскировалось бы чужой прошивкой).
        SeedVersionFolder(root, "1.1.5.38.20260801_0900");
        var stalePath = Path.Combine(CtrlDir(root), "1.1.4.38.20260716_1814");

        Assert.False(FirmwareDiskPresence.VersionPresentOnDisk(stalePath, "1.1.4.38.20260716_1814"));
    }

    [Fact]
    public void MatchingSiblingButEmpty_IsNotCountedAsPresent()
    {
        using var root = new TempRoot();
        // Папку той же версии оставили, но пустой — файлов нет, показывать нечего.
        Directory.CreateDirectory(Path.Combine(CtrlDir(root), "1.1.4.38.20260716_1814_ОТКАТАНО"));
        var stalePath = Path.Combine(CtrlDir(root), "1.1.4.38.20260716_1814");

        Assert.False(FirmwareDiskPresence.VersionPresentOnDisk(stalePath, "1.1.4.38.20260716_1814"));
    }

    [Fact]
    public void ControllerFolderMissing_IsAbsent()
    {
        using var root = new TempRoot();
        var stalePath = Path.Combine(CtrlDir(root), "1.1.4.38.20260716_1814");

        Assert.False(FirmwareDiskPresence.VersionPresentOnDisk(stalePath, "1.1.4.38.20260716_1814"));
    }

    [Fact]
    public void EmptyFirmwareDir_IsAbsent()
    {
        Assert.False(FirmwareDiskPresence.VersionPresentOnDisk("", "1.1.4.38.20260716_1814"));
        Assert.False(FirmwareDiskPresence.VersionPresentOnDisk(null, "1.1.4.38.20260716_1814"));
    }
}
