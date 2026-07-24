using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

    /// <summary>SearchView.PerformSchemasSearchAsync now runs the heavy walk (EnsureScanned, in a
    /// background Task.Run) separately from the actual query matching (Matches, synchronous on the UI
    /// thread afterwards — see its out-parameters) — this covers that split still finds everything the
    /// old one-shot Matches() call did, including files nested under territory subfolders.</summary>
    [Fact]
    public void EnsureScanned_ThenMatches_FindsNestedFiles_SameAsOneShotMatches()
    {
        var service = new SchematicService();
        service.EnsureScanned(_root);
        var hits = service.Matches(_root, "ПЖ-101");
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Equal("ПЖ-101", h.CabinetName));
    }

    /// <summary>EnsureScanned alone (no query yet) must still populate the cache that CabinetHits and
    /// Matches read from — otherwise the SearchView split would warm nothing and every search would
    /// re-walk the disk regardless.</summary>
    [Fact]
    public void EnsureScanned_PopulatesCacheUsedByCabinetHits()
    {
        var service = new SchematicService();
        service.EnsureScanned(_root);
        var all = service.CabinetHits(_root);
        Assert.Equal(6, all.Count); // 2 (ПЖ-101) + 1 (НГР-205) + 1 (КПЧ-9) + 1 (КПЧ-12) + 1 (НАСОС-5)
    }

    /// <summary>Выдача должна появляться ПО ХОДУ обхода, а не после него: диск на 400 ГБ читается
    /// минутами, и всё это время экран был пуст. onFound зовётся на каждый найденный файл.</summary>
    [Fact]
    public void EnsureScanned_ReportsEveryFileWhileWalking_NotOnlyAtTheEnd()
    {
        var streamed = new List<SchematicHit>();
        new SchematicService().EnsureScanned(_root, CancellationToken.None, h => streamed.Add(h));
        Assert.Equal(6, streamed.Count);
        Assert.Contains(streamed, h => h.CabinetName == "НГР-205");
    }

    /// <summary>Тёплый кэш обходить нечего — обработчик не зовётся вовсе, иначе повторный поиск
    /// перерисовывал бы выдачу дважды.</summary>
    [Fact]
    public void EnsureScanned_WarmCache_DoesNotStreamAgain()
    {
        var service = new SchematicService();
        service.EnsureScanned(_root);

        var streamed = new List<SchematicHit>();
        service.EnsureScanned(_root, CancellationToken.None, h => streamed.Add(h));
        Assert.Empty(streamed);
    }

    /// <summary>Кнопка «Остановить». Прерванный обход НЕ должен запоминаться как полный: иначе
    /// следующий поиск принял бы обрезанный список за весь диск и «терял» бы половину шкафов до
    /// перезапуска программы.</summary>
    [Fact]
    public void EnsureScanned_Cancelled_ThrowsAndLeavesCacheCold()
    {
        var service = new SchematicService();
        using var cts = new CancellationTokenSource();
        var streamed = 0;

        Assert.Throws<OperationCanceledException>(() =>
            service.EnsureScanned(_root, cts.Token, _ => { streamed++; cts.Cancel(); }));

        Assert.Equal(1, streamed);              // прервались сразу после первого же файла
        Assert.False(service.IsScanned(_root)); // кэш холодный — следующий поиск прочитает диск целиком
    }

    /// <summary>IsScanned — то, по чему SearchView решает, показывать ли индикатор занятости и кнопку
    /// «Остановить»: для готового списка в памяти они только мешают.</summary>
    [Fact]
    public void IsScanned_FalseBeforeWalk_TrueAfter()
    {
        var service = new SchematicService();
        Assert.False(service.IsScanned(_root));
        service.EnsureScanned(_root);
        Assert.True(service.IsScanned(_root));
    }

    /// <summary>Потоковый фильтр (по одному файлу, HitMatches) обязан давать ровно ту же выдачу, что
    /// и обычный Matches по готовому списку — иначе «остановил на середине» и «дождался конца» дали бы
    /// разные результаты по одному и тому же запросу.</summary>
    [Theory]
    [InlineData("ПЧ", false)]
    [InlineData("ПЧ", true)]
    [InlineData("ПЖ-101", false)]
    [InlineData("НГР", false)]
    public void HitMatches_PerFile_AgreesWithMatches_OverWholeList(string query, bool exactWord)
    {
        var service = new SchematicService();
        var tokens = SchematicService.QueryTokens(query);

        var streamed = new List<SchematicHit>();
        service.EnsureScanned(_root, CancellationToken.None, h =>
        {
            if (SchematicService.HitMatches(h, tokens, exactWord)) streamed.Add(h);
        });

        var byList = service.Matches(_root, query, exactWord);
        Assert.Equal(byList.Select(h => h.Path).OrderBy(p => p, StringComparer.Ordinal),
                     streamed.Select(h => h.Path).OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>Пустой запрос не должен «совпадать со всем»: на середине обхода это залило бы экран
    /// всеми файлами диска подряд.</summary>
    [Fact]
    public void HitMatches_EmptyQuery_MatchesNothing()
    {
        var hit = new SchematicHit("НГР-205", Path.Combine(_root, "Территория 2", "НГР-205", "схема.pdf"));
        Assert.False(SchematicService.HitMatches(hit, SchematicService.QueryTokens(""), exactWord: false));
    }
}
