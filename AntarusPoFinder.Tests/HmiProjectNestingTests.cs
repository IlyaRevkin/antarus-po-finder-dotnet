using System.IO;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Куда ложится проект панели.
///
/// Жалоба владельца (09.09.2026): «HMI почему-то в папке HMI, которая в прошивке, ещё одну папку
/// создавала с hmi_прошивка».
///
/// Имя «{версия}_hmi» пошло от ОБЩЕЙ папки HMI контроллера: там рядом лежат проекты разных версий,
/// и без версии в имени они бы смешались. Внутри СВОЕЙ папки версии этот уровень лишний — папка и
/// так принадлежит одной версии.
///
/// Старую раскладку продолжаем поддерживать: уже разложенное на сетевой шаре не переносим, чтение
/// обоих вариантов работает. Иначе ради косметики пришлось бы двигать чужие файлы.</summary>
public class HmiProjectNestingTests
{
    private static string MakeProject(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "panel.fsprj"), "проект");
        Directory.CreateDirectory(Path.Combine(dir, "Drivers"));
        File.WriteAllText(Path.Combine(dir, "Drivers", "driver.dll"), "драйвер");
        return dir;
    }

    [Fact]
    public void InsideVersionFolder_ProjectGoesStraightIntoHmi_WithoutExtraLevel()
    {
        using var tmp = new TempRoot();
        var versionDir = Path.Combine(tmp.Path, "ПО", "НГР", "КНС", "SMH4", "1.2.0004.0001");
        VersionLayout.EnsureFolders(versionDir);
        var hmiFolder = VersionLayout.SlotFolder(versionDir, "HMI");

        var src = MakeProject(Path.Combine(tmp.Path, "исходный-проект"));
        var dst = FirmwareAttachmentsService.CopyHmiProject(hmiFolder, "1.2.0004.0001", src);

        Assert.Equal(
            Path.TrimEndingDirectorySeparator(hmiFolder),
            Path.TrimEndingDirectorySeparator(dst));
        Assert.True(File.Exists(Path.Combine(hmiFolder, "panel.fsprj")), "проект должен лежать прямо в HMI");
        Assert.True(File.Exists(Path.Combine(hmiFolder, "Drivers", "driver.dll")), "вложенные папки должны переехать целиком");
        Assert.False(Directory.Exists(Path.Combine(hmiFolder, "1.2.0004.0001_hmi")), "лишнего уровня быть не должно");
    }

    /// <summary>Общая папка HMI контроллера — там версий много, и уровень с номером обязателен,
    /// иначе проекты разных версий смешаются в одной куче.</summary>
    [Fact]
    public void InControllerHmiFolder_VersionLevelIsKept()
    {
        using var tmp = new TempRoot();
        var ctrlHmi = Path.Combine(tmp.Path, "ПО", "НГР", "КНС", "SMH4", "HMI");
        Directory.CreateDirectory(ctrlHmi);

        var src = MakeProject(Path.Combine(tmp.Path, "исходный-проект"));
        var dst = FirmwareAttachmentsService.CopyHmiProject(ctrlHmi, "1.2.0004.0001", src);

        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.Combine(ctrlHmi, "1.2.0004.0001_hmi")),
            Path.TrimEndingDirectorySeparator(dst));
        Assert.True(File.Exists(Path.Combine(dst, "panel.fsprj")));
    }

    /// <summary>Повторная выкладка того же проекта не должна ни падать, ни плодить копию: раньше
    /// копирование папки в саму себя роняло «файл занят другим процессом».</summary>
    [Fact]
    public void RepeatedCopy_IntoVersionHmi_IsHarmless()
    {
        using var tmp = new TempRoot();
        var versionDir = Path.Combine(tmp.Path, "ПО", "НГР", "КНС", "SMH4", "1.2.0004.0002");
        VersionLayout.EnsureFolders(versionDir);
        var hmiFolder = VersionLayout.SlotFolder(versionDir, "HMI");

        var src = MakeProject(Path.Combine(tmp.Path, "исходный-проект"));
        FirmwareAttachmentsService.CopyHmiProject(hmiFolder, "1.2.0004.0002", src);
        var again = FirmwareAttachmentsService.CopyHmiProject(hmiFolder, "1.2.0004.0002", hmiFolder);

        Assert.Equal(
            Path.TrimEndingDirectorySeparator(hmiFolder),
            Path.TrimEndingDirectorySeparator(again));
        Assert.False(Directory.Exists(Path.Combine(hmiFolder, "1.2.0004.0002_hmi")));
    }
}
