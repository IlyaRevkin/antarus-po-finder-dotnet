using System;
using System.IO;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Covers PslInspector.Inspect — the only test that ever exercised it used to point at a
/// real Segnetics SMLogix export on one developer's D: drive (D:\MyFolder\...) and silently passed,
/// asserting nothing at all, whenever that path didn't exist — i.e. on every other machine and in CI.
/// TestData/sample_smh4.psl is a synthetic-but-structurally-real .psl: a valid 56-byte SMLogix header
/// (the exact magic bytes PslInspector checks, plus a "working" composition-mode value at the
/// documented byte offset) followed by a small JSON blob whose "key" field is shaped like a real
/// Segnetics device key ("SMH4-1234-01-2") — built directly against PslInspector's own header layout
/// and DeviceKeyRe/DeviceJsonRe regexes (see PslInspector.cs) so it runs the exact same parsing path a
/// real export would, not just a file that happens to be named ".psl".</summary>
public class PslInspectorTests
{
    private static string SamplePath => Path.Combine(AppContext.BaseDirectory, "TestData", "sample_smh4.psl");

    [Fact]
    public void Inspect_SyntheticSmh4Sample_DetectsModelAndModification()
    {
        Assert.True(File.Exists(SamplePath), $"test resource missing: {SamplePath}");

        var info = PslInspector.Inspect(SamplePath);

        Assert.Equal("SMH4", info.Plc.Model);
        Assert.Equal("1234", info.Plc.Modification);
        Assert.Equal("SMH4-1234-01-2", info.Plc.DeviceKey);
    }

    [Fact]
    public void Inspect_SyntheticSmh4Sample_ReadsWorkingCompositionFromHeader()
    {
        var info = PslInspector.Inspect(SamplePath);

        Assert.True(info.Composition.HeaderValid);
        Assert.Equal("working", info.Composition.Mode);
        Assert.Equal(1u, info.Composition.Raw);
    }

    [Fact]
    public void Inspect_MissingFile_Throws()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"antarus_psl_missing_{Guid.NewGuid():N}.psl");
        Assert.Throws<FileNotFoundException>(() => PslInspector.Inspect(missing));
    }
}
