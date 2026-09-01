using AntarusPoFinder.Core.Loader;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>«На Pixel нет лоадера, а он LFS создал и через лоадер грузить пытается — должен открывать
/// .psl в SMLogix». И SMH, и Pixel — Segnetics с исходником .psl (SegneticsProject.IsRelevant у обоих
/// true), но Segnetics Loader грузит ТОЛЬКО SMH. ControllerLoadMethod — единственный источник этого
/// признака; по нему решают, показывать ли «Загрузить в ПЛК»/«Собрать LFS» или вести к открытию .psl.</summary>
public class ControllerLoadMethodTests
{
    [Theory]
    [InlineData("SMH4")]
    [InlineData("SMH5")]
    [InlineData("SMH2Gi")]
    [InlineData("smh4")]
    public void SmhControllers_SupportLoader(string controller) =>
        Assert.True(ControllerLoadMethod.SupportsLoader(controller));

    [Theory]
    // Pixel — тоже Segnetics и тоже с .psl, но загрузчика для него нет: грузится проектом SMLogix.
    [InlineData("PIXEL")]
    [InlineData("PIXEL2")]
    [InlineData("PIXEL-2511")]
    [InlineData("PIXEL2-1320")]
    [InlineData("pxl2")]
    [InlineData("TRIM5")]
    // Не-Segnetics вообще: у них Segnetics Loader тем более ни при чём.
    [InlineData("KINCO")]
    [InlineData("OWEN ПЛК110")]
    [InlineData("")]
    [InlineData(null)]
    public void NonSmhControllers_DoNotSupportLoader(string? controller) =>
        Assert.False(ControllerLoadMethod.SupportsLoader(controller));

    /// <summary>Признак — от СЕМЕЙСТВА, а не от файлов на диске: даже если рядом с Pixel-версией
    /// кто-то по ошибке уже собрал .lfs, заливать его загрузчиком всё равно нельзя. SegneticsProject
    /// (наличие .psl/.lfs) и ControllerLoadMethod (грузит ли лоадер) — про разное и расходятся у Pixel
    /// намеренно.</summary>
    [Fact]
    public void PixelIsSegneticsButLoaderless()
    {
        Assert.True(SegneticsProject.IsRelevant("PIXEL", "", foundLfs: true, foundPsl: true));
        Assert.False(ControllerLoadMethod.SupportsLoader("PIXEL"));
    }
}
