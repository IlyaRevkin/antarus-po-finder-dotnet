using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Covers SearchService.ConvertLayout — the EN-QWERTY/RU-ЙЦУКЕН keyboard-layout remap used
/// as a search fallback (see SearchWithLayoutFallback) when the operator forgets to switch layout
/// before typing a tag or shkaf type.</summary>
public class SearchServiceLayoutTests
{
    [Fact]
    public void ConvertLayout_LatinTypedInsteadOfCyrillic_RecoversTheIntendedWord()
    {
        // The exact example reported: typed "gj;fh" (EN-US layout active) meaning "пожар" (ЙЦУКЕН).
        Assert.Equal("пожар", SearchService.ConvertLayout("gj;fh"));
    }

    [Fact]
    public void ConvertLayout_PreservesCase()
    {
        Assert.Equal("Пожар", SearchService.ConvertLayout("Gj;fh"));
    }

    [Fact]
    public void ConvertLayout_CyrillicTypedInsteadOfLatin_AlsoConvertsBack()
    {
        Assert.Equal("kinco", SearchService.ConvertLayout("лштсщ"));
    }

    [Fact]
    public void ConvertLayout_LeavesDigitsAndUnmappedPunctuationUnchanged()
    {
        Assert.Equal("101-205", SearchService.ConvertLayout("101-205"));
    }

    [Fact]
    public void SearchWithLayoutFallback_UsesAsTypedResultWhenNonEmpty_NeverConverts()
    {
        var calls = 0;
        var result = SearchService.SearchWithLayoutFallback("KINCO", false, (q, ex) =>
        {
            calls++;
            return new System.Collections.Generic.List<int> { 1 };
        });

        Assert.Single(result);
        Assert.Equal(1, calls); // fallback attempt must not even run once the first try already hit
    }

    [Fact]
    public void SearchWithLayoutFallback_RetriesConvertedQuery_WhenAsTypedFindsNothing()
    {
        var seenQueries = new System.Collections.Generic.List<string>();
        var result = SearchService.SearchWithLayoutFallback("gj;fh", false, (q, ex) =>
        {
            seenQueries.Add(q);
            return q == "пожар" ? new System.Collections.Generic.List<int> { 1 } : new System.Collections.Generic.List<int>();
        });

        Assert.Single(result);
        Assert.Equal(new[] { "gj;fh", "пожар" }, seenQueries);
    }

    [Fact]
    public void SearchWithLayoutFallback_AllowFallbackFalse_NeverRetries()
    {
        var calls = 0;
        var result = SearchService.SearchWithLayoutFallback("gj;fh", false, (q, ex) =>
        {
            calls++;
            return new System.Collections.Generic.List<int>();
        }, allowFallback: false, out var usedFallback, out var convertedQuery);

        Assert.Empty(result);
        Assert.Equal(1, calls);
        Assert.False(usedFallback);
        Assert.Equal("gj;fh", convertedQuery);
    }

    [Fact]
    public void SearchWithLayoutFallback_ReportsConversionOnlyWhenItProducedResults()
    {
        var result = SearchService.SearchWithLayoutFallback("gj;fh", false, (q, ex) =>
            q == "пожар" ? new System.Collections.Generic.List<int> { 1 } : new System.Collections.Generic.List<int>(),
            allowFallback: true, out var usedFallback, out var convertedQuery);

        Assert.Single(result);
        Assert.True(usedFallback);
        Assert.Equal("пожар", convertedQuery);
    }
}
