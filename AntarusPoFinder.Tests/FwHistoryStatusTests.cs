using System.Collections.Generic;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Окно «История версий» показывало «Активна» у каждой не откатанной строки — то есть у всей
/// истории сразу (жалоба: «загружаю прошивку, а в истории все активные»). См. FwHistoryStatus.</summary>
public class FwHistoryStatusTests
{
    private static FwVersionRecord V(int hw, int sw, string status = "active") =>
        new() { HwVersion = hw, SwVersion = sw, VersionRaw = $"1.1.{hw:000}.{sw:0000}", Status = status };

    [Fact]
    public void OnlyNewestVersionIsCurrent_RestAreSuperseded()
    {
        var versions = new List<FwVersionRecord> { V(1, 3), V(1, 2), V(1, 1) };

        var labels = FwHistoryStatus.Labels(versions);

        Assert.Equal(new[] { FwHistoryStatus.Current, FwHistoryStatus.Superseded, FwHistoryStatus.Superseded }, labels);
    }

    [Fact]
    public void RolledBackKeepsItsOwnLabel_AndIsNeverCurrent()
    {
        // Свежайшая по порядку строка откатана — актуальной становится следующая живая.
        var versions = new List<FwVersionRecord> { V(1, 3, "rolled_back"), V(1, 2), V(1, 1) };

        var labels = FwHistoryStatus.Labels(versions);

        Assert.Equal(new[] { FwHistoryStatus.RolledBack, FwHistoryStatus.Current, FwHistoryStatus.Superseded }, labels);
    }

    [Fact]
    public void NewestOfEachHwStaysCurrentForThatHw()
    {
        var versions = new List<FwVersionRecord> { V(2, 2), V(2, 1), V(1, 5), V(1, 4) };

        var labels = FwHistoryStatus.Labels(versions);

        Assert.Equal(new[]
        {
            FwHistoryStatus.Current,
            FwHistoryStatus.Superseded,
            FwHistoryStatus.CurrentForHw(1),
            FwHistoryStatus.Superseded,
        }, labels);
    }

    [Fact]
    public void EmptyHistory_NoLabels()
    {
        Assert.Empty(FwHistoryStatus.Labels(new List<FwVersionRecord>()));
    }
}
