using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

public class SchematicServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AntarusSchematicTest_" + Guid.NewGuid());

    public SchematicServiceTests()
    {
        // Second disk laid out with a territory subfolder — the bug being fixed is that the old
        // scan only looked at the disk's direct children, so anything nested one level deeper
        // (as real second-disk layouts are) was never found at all.
        Directory.CreateDirectory(Path.Combine(_root, "Территория 1", "ПЖ-101"));
        File.WriteAllText(Path.Combine(_root, "Территория 1", "ПЖ-101", "схема.pdf"), "x");
        File.WriteAllText(Path.Combine(_root, "Территория 1", "ПЖ-101", "схема_ревизия2.pdf"), "x");

        Directory.CreateDirectory(Path.Combine(_root, "Территория 2", "НГР-205"));
        File.WriteAllText(Path.Combine(_root, "Территория 2", "НГР-205", "схема.pdf"), "x");

        // Bare file named directly after the cabinet, at root — still supported.
        File.WriteAllText(Path.Combine(_root, "КПЧ-9.pdf"), "x");

        // Should never match a "ПЧ" query — exact-word mode exists precisely for this.
        Directory.CreateDirectory(Path.Combine(_root, "Территория 1", "КПЧ-12"));
        File.WriteAllText(Path.Combine(_root, "Территория 1", "КПЧ-12", "схема.pdf"), "x");

        Directory.CreateDirectory(Path.Combine(_root, "НАСОС-5"));
        File.WriteAllText(Path.Combine(_root, "НАСОС-5", "схема.pdf"), "x");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Matches_FindsFilesNestedUnderTerritorySubfolders()
    {
        var hits = new SchematicService().Matches(_root, "ПЖ-101");
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Equal("ПЖ-101", h.CabinetName));
    }

    [Fact]
    public void Matches_ReturnsEveryMatchingPdf_NotJustFirstPerFolder()
    {
        var hits = new SchematicService().Matches(_root, "ПЖ-101");
        Assert.Contains(hits, h => h.Path.EndsWith("схема.pdf"));
        Assert.Contains(hits, h => h.Path.EndsWith("схема_ревизия2.pdf"));
    }

    [Fact]
    public void Matches_PartialSubstring_FindsBareFileAtRoot()
    {
        var hits = new SchematicService().Matches(_root, "КПЧ-9");
        Assert.Single(hits);
        Assert.Equal("КПЧ-9", hits[0].CabinetName);
    }

    [Fact]
    public void Matches_PartialMode_QueryPCH_AlsoMatchesKPCH()
    {
        var hits = new SchematicService().Matches(_root, "ПЧ", exactWord: false);
        Assert.Contains(hits, h => h.CabinetName == "КПЧ-12");
        Assert.Contains(hits, h => h.CabinetName == "КПЧ-9");
    }

    [Fact]
    public void Matches_ExactWord_QueryPCH_DoesNotMatchKPCH()
    {
        var hits = new SchematicService().Matches(_root, "ПЧ", exactWord: true);
        Assert.DoesNotContain(hits, h => h.CabinetName == "КПЧ-12");
        Assert.DoesNotContain(hits, h => h.CabinetName == "КПЧ-9");
    }

    [Fact]
    public void Matches_QueryTypedOnWrongKeyboardLayout_StillFindsCabinet()
    {
        // "yfcjc-5" is what a EN-US-layout keyboard produces if the operator forgot to switch
        // layout while meaning to type "насос-5" (ЙЦУКЕН) — same physical keys, wrong active
        // layout. The as-typed query finds nothing, so the layout-fallback in
        // SearchService.SearchWithLayoutFallback must kick in.
        var hits = new SchematicService().Matches(_root, "yfcjc-5");
        Assert.Single(hits);
        Assert.Equal("НАСОС-5", hits[0].CabinetName);
    }

    [Fact]
    public void Matches_NoHit_ReturnsEmpty()
    {
        var hits = new SchematicService().Matches(_root, "НЕТ_ТАКОГО_ШКАФА");
        Assert.Empty(hits);
    }
}
