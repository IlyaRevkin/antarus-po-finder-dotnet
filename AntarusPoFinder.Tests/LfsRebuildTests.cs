using System.IO;
using AntarusPoFinder.Core.Loader;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Пересборка .lfs в любой момент.
///
/// Просьба владельца (09.09.2026): «нужно сделать постоянной кнопку сборки LFS, чтобы по сути в
/// любой момент можно было пересобрать. Или если вышел psl без обновления версии, то опять же
/// можно было пересобрать».
///
/// Раньше решение отдавало «уже есть, делать нечего» и плана сборки не давало — кнопка пропадала.
/// Теперь план отдаётся и при существующем .lfs, а перезапись подтверждается отдельно: это рабочий
/// файл, которым уже могли воспользоваться.</summary>
public class LfsRebuildTests
{
    private static string MakeVersion(string root, string version, bool withLfs)
    {
        var dir = Path.Combine(root, "ПО", "НГР", "КНС", "SMH4", version);
        VersionLayout.EnsureFolders(dir);
        var fw = VersionLayout.FirmwareFolder(dir);
        File.WriteAllText(Path.Combine(fw, version + ".psl"), "исходник");
        if (withLfs) File.WriteAllText(Path.Combine(fw, version + ".lfs"), "собранный");
        return dir;
    }

    [Fact]
    public void ExistingLfs_StillOffersRebuild_WithPlan()
    {
        using var tmp = new TempRoot();
        var dir = MakeVersion(tmp.Path, "1.2.0004.0001", withLfs: true);

        var d = LfsConversionService.Decide(dir, null, null);

        Assert.Equal(LfsConversionNeed.AlreadyPresent, d.Need);
        Assert.NotNull(d.Plan);   // ← без плана кнопка пряталась, и пересобрать было нечем
    }

    [Fact]
    public void MissingLfs_OffersBuild()
    {
        using var tmp = new TempRoot();
        var dir = MakeVersion(tmp.Path, "1.2.0004.0002", withLfs: false);

        var d = LfsConversionService.Decide(dir, null, null);

        Assert.Equal(LfsConversionNeed.Build, d.Need);
        Assert.NotNull(d.Plan);
    }

    /// <summary>Без исходника пересобирать нечем — это по-прежнему тупик, и кнопку показывать не из
    /// чего.</summary>
    [Fact]
    public void LfsWithoutSource_GivesNoPlan()
    {
        using var tmp = new TempRoot();
        var dir = Path.Combine(tmp.Path, "ПО", "НГР", "КНС", "SMH4", "1.2.0004.0003");
        VersionLayout.EnsureFolders(dir);
        File.WriteAllText(Path.Combine(VersionLayout.FirmwareFolder(dir), "1.2.0004.0003.lfs"), "собранный");

        var d = LfsConversionService.Decide(dir, null, null);

        Assert.Equal(LfsConversionNeed.AlreadyPresent, d.Need);
        Assert.Null(d.Plan);
    }

    /// <summary>Главное ради чего всё затевалось: исходник правили после сборки. Человек должен
    /// увидеть это рядом с кнопкой, а не сверять даты в проводнике.</summary>
    [Fact]
    public void SourceNewerThanBuild_IsSaidOutLoud()
    {
        using var tmp = new TempRoot();
        var dir = MakeVersion(tmp.Path, "1.2.0004.0004", withLfs: true);
        var fw = VersionLayout.FirmwareFolder(dir);
        File.SetLastWriteTime(Path.Combine(fw, "1.2.0004.0004.lfs"), System.DateTime.Now.AddHours(-3));
        File.SetLastWriteTime(Path.Combine(fw, "1.2.0004.0004.psl"), System.DateTime.Now);

        var d = LfsConversionService.Decide(dir, null, null);

        Assert.Contains("Исходник новее", d.Message);
    }

    [Fact]
    public void FreshBuild_DoesNotClaimSourceIsNewer()
    {
        using var tmp = new TempRoot();
        var dir = MakeVersion(tmp.Path, "1.2.0004.0005", withLfs: true);
        var fw = VersionLayout.FirmwareFolder(dir);
        File.SetLastWriteTime(Path.Combine(fw, "1.2.0004.0005.psl"), System.DateTime.Now.AddHours(-3));
        File.SetLastWriteTime(Path.Combine(fw, "1.2.0004.0005.lfs"), System.DateTime.Now);

        var d = LfsConversionService.Decide(dir, null, null);

        Assert.DoesNotContain("Исходник новее", d.Message);
    }

    /// <summary>У Pixel первого поколения и не-Segnetics загрузчика нет вовсе — никакой пересборки
    /// им предлагать нельзя, сколько бы файлов рядом ни лежало.</summary>
    [Theory]
    [InlineData("PIXEL")]
    [InlineData("PIXEL-2511")]
    [InlineData("KINCO")]
    public void ControllersWithoutLoader_NeverRebuild(string controller)
        => Assert.False(ControllerLoadMethod.SupportsLoader(controller));

    [Theory]
    [InlineData("SMH4")]
    [InlineData("PIXEL2-1321")]
    public void ControllersWithLoader_MayRebuild(string controller)
        => Assert.True(ControllerLoadMethod.SupportsLoader(controller));
}
