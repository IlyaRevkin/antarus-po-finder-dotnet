using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Covers the "у коллеги Z:\Software, у меня \\ant_srv\Software, прошивка не находится" fix:
/// a firmware/parameter path stored with one machine's root prefix must re-root onto this machine's
/// root by anchoring on the well-known ПО/Параметры folder (see FirmwarePathLocalizer).</summary>
public class FirmwarePathLocalizerTests
{
    [Fact]
    public void Localize_UncStoredPath_ReRootedOntoLocalMappedDrive()
    {
        var stored = @"\\ant_srv\Software\Antarus Finder\ПО\НГР\НГР-2.0 SMH5\PIXEL\1.1.0044.0001.20260201_0000";
        var localRoot = @"Z:\Software\Antarus Finder";

        var result = FirmwarePathLocalizer.Localize(stored, localRoot);

        Assert.Equal(@"Z:\Software\Antarus Finder\ПО\НГР\НГР-2.0 SMH5\PIXEL\1.1.0044.0001.20260201_0000", result);
    }

    [Fact]
    public void Localize_MappedDriveStoredPath_ReRootedOntoLocalUnc()
    {
        // The reverse direction — colleague's mapped-drive path arriving on the UNC machine.
        var stored = @"Z:\Software\Antarus Finder\ПО\НГР\НГР-2.0 SMH5\PIXEL\1.1.0044.0001.20260201_0000";
        var localRoot = @"\\ant_srv\Software\Antarus Finder";

        var result = FirmwarePathLocalizer.Localize(stored, localRoot);

        Assert.Equal(@"\\ant_srv\Software\Antarus Finder\ПО\НГР\НГР-2.0 SMH5\PIXEL\1.1.0044.0001.20260201_0000", result);
    }

    [Fact]
    public void Localize_SameMachine_ReturnsIdenticalPath()
    {
        var stored = @"Z:\Software\Antarus Finder\ПО\НГР\PIXEL\1.1.0044.0001";
        var result = FirmwarePathLocalizer.Localize(stored, @"Z:\Software\Antarus Finder");
        Assert.Equal(stored, result);
    }

    [Fact]
    public void Localize_ParamsAnchor_AlsoReRooted()
    {
        var stored = @"\\ant_srv\Software\Antarus Finder\Параметры\НГР\Segnetics\params.knt";
        var result = FirmwarePathLocalizer.Localize(stored, @"Z:\Software\Antarus Finder");
        Assert.Equal(@"Z:\Software\Antarus Finder\Параметры\НГР\Segnetics\params.knt", result);
    }

    [Fact]
    public void Localize_DifferentRootDepth_StillAnchorsOnPo()
    {
        // The two machines don't even share the same folder depth before ПО — the anchor still wins.
        var stored = @"D:\shares\deep\nested\Antarus\ПО\НГР\PIXEL\1.1.0044.0001";
        var result = FirmwarePathLocalizer.Localize(stored, @"Z:\Software\Antarus Finder");
        Assert.Equal(@"Z:\Software\Antarus Finder\ПО\НГР\PIXEL\1.1.0044.0001", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Localize_EmptyStoredPath_ReturnedUnchanged(string stored)
    {
        Assert.Equal(stored, FirmwarePathLocalizer.Localize(stored, @"Z:\Software"));
    }

    [Fact]
    public void Localize_EmptyLocalRoot_ReturnsStoredVerbatim()
    {
        var stored = @"\\ant_srv\Software\Antarus Finder\ПО\НГР\PIXEL\1.1.0044.0001";
        Assert.Equal(stored, FirmwarePathLocalizer.Localize(stored, ""));
    }

    [Fact]
    public void Localize_NonHierarchyPath_NoAnchor_ReturnedUnchanged()
    {
        // No ПО/Параметры segment — nothing to anchor on, so we must not fabricate a re-rooted path.
        var stored = @"C:\Temp\somefile.psl";
        Assert.Equal(stored, FirmwarePathLocalizer.Localize(stored, @"Z:\Software\Antarus Finder"));
    }

    [Fact]
    public void Localize_TrailingSeparator_DoesNotProduceDoubleSeparator()
    {
        var stored = @"\\ant_srv\Software\Antarus Finder\ПО\НГР\PIXEL\1.1.0044.0001\";
        var result = FirmwarePathLocalizer.Localize(stored, @"Z:\Software\Antarus Finder");
        Assert.Equal(@"Z:\Software\Antarus Finder\ПО\НГР\PIXEL\1.1.0044.0001", result);
    }
}
