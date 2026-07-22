using AntarusPoFinder.App.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>NetworkPathHelper had zero test coverage before this. TryResolveUnc's only real branch
/// (an actual mapped network drive resolving to its \\server\share form via WNetGetConnection) needs
/// a genuinely mapped drive letter on the machine running the test, which nothing in this suite can
/// set up portably/deterministically in CI — so these tests cover every OTHER branch the method's own
/// doc comment promises a caller can rely on: empty input, an already-UNC path passed straight through,
/// a local (non-mapped) drive-letter path correctly falling back to null instead of a bogus UNC guess,
/// and a malformed path never throwing past this method (the doc's "resolution fails for any reason"
/// contract).</summary>
public class NetworkPathHelperTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryResolveUnc_EmptyOrWhitespaceOrNull_ReturnsNull(string? path)
    {
        Assert.Null(NetworkPathHelper.TryResolveUnc(path!));
    }

    [Theory]
    [InlineData(@"\\server\share\Software\Antarus Finder")]
    [InlineData(@"\\ant-srv\общая\папка")]
    public void TryResolveUnc_AlreadyUncPath_ReturnedUnchanged(string uncPath)
    {
        Assert.Equal(uncPath, NetworkPathHelper.TryResolveUnc(uncPath));
    }

    [Fact]
    public void TryResolveUnc_LocalNonMappedDrive_ReturnsNull_NotABogusPath()
    {
        // C:\ is the system drive on any machine this test runs on — never a WNetGetConnection-mapped
        // network drive, so this exercises the "not a network drive" branch (result != 0) without
        // needing an actually-mapped drive letter, which nothing in the test environment can set up.
        var result = NetworkPathHelper.TryResolveUnc(@"C:\Windows");
        Assert.Null(result);
    }

    [Fact]
    public void TryResolveUnc_RelativePath_ResolvesAgainstCurrentDrive_NotAMappedNetworkDrive_ReturnsNull()
    {
        // A bare relative path still goes through Path.GetFullPath (see the method itself) and picks
        // up whatever drive the test process is running from — on the build/dev machine that's always
        // a local drive, so this must come back null exactly like the absolute-path case above, not
        // throw and not fabricate a UNC path.
        var result = NetworkPathHelper.TryResolveUnc("some\\relative\\path");
        Assert.Null(result);
    }

    [Fact]
    public void TryResolveUnc_PathWithInvalidCharacters_DoesNotThrow_ReturnsNull()
    {
        // Path.GetFullPath can throw for genuinely invalid input (embedded NUL, etc.) — the method's
        // own catch-all must turn that into "don't know" (null), never let it propagate and crash
        // whatever's building the phone-instructions dialog around it.
        var result = NetworkPathHelper.TryResolveUnc("Z:\\bad\0path");
        Assert.Null(result);
    }
}
