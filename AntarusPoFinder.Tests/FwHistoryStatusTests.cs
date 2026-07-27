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

    // ── ManualCurrent (ручная отметка «Сделать текущей») ────────────────────────

    private static FwVersionRecord V(int hw, int sw, string status, bool manualCurrent) =>
        new() { HwVersion = hw, SwVersion = sw, VersionRaw = $"1.1.{hw:000}.{sw:0000}", Status = status, ManualCurrent = manualCurrent };

    [Fact]
    public void ManualCurrent_OverridesTheNewestBySwVersion()
    {
        // Более новую по номеру версию (sw3) на практике забраковали — оператор вручную отметил
        // текущей sw2 через «Сделать текущей» (Database.SetFwVersionManualCurrent).
        var versions = new List<FwVersionRecord>
        {
            V(1, 3, "active", manualCurrent: false),
            V(1, 2, "active", manualCurrent: true),
            V(1, 1, "active", manualCurrent: false),
        };

        var labels = FwHistoryStatus.Labels(versions);

        Assert.Equal(new[] { FwHistoryStatus.Superseded, FwHistoryStatus.Current, FwHistoryStatus.Superseded }, labels);
    }

    [Fact]
    public void ManualCurrent_WithoutAnyOverride_BehavesLikeBefore()
    {
        // Без единой отметки в группе результат не должен отличаться от старого поведения.
        var versions = new List<FwVersionRecord>
        {
            V(1, 3, "active", manualCurrent: false),
            V(1, 2, "active", manualCurrent: false),
            V(1, 1, "active", manualCurrent: false),
        };

        var labels = FwHistoryStatus.Labels(versions);

        Assert.Equal(new[] { FwHistoryStatus.Current, FwHistoryStatus.Superseded, FwHistoryStatus.Superseded }, labels);
    }

    [Fact]
    public void ManualCurrent_InANonDominantHwGroup_OnlyAffectsItsOwnHwLabel()
    {
        // hw2 — доминирующая группа (даёт общую «Текущая»). Ручная отметка внутри hw1 должна поменять
        // только «Текущая (HW 1)» — общая «Текущая» у hw2 не должна сдвинуться.
        var versions = new List<FwVersionRecord>
        {
            V(2, 2, "active", manualCurrent: false),
            V(2, 1, "active", manualCurrent: false),
            V(1, 5, "active", manualCurrent: false),
            V(1, 4, "active", manualCurrent: true),
        };

        var labels = FwHistoryStatus.Labels(versions);

        Assert.Equal(new[]
        {
            FwHistoryStatus.Current,
            FwHistoryStatus.Superseded,
            FwHistoryStatus.Superseded,
            FwHistoryStatus.CurrentForHw(1),
        }, labels);
    }

    [Fact]
    public void ManualCurrent_OnARolledBackVersion_IsIgnored()
    {
        // На откатанной версии ManualCurrent теоретически быть не должно (Database.RollbackFwVersion
        // сбрасывает флаг), но даже если бы флаг остался — откатанная версия никогда не должна стать
        // «текущей»: Labels фильтрует rolled_back из alive до применения ManualCurrent.
        var versions = new List<FwVersionRecord>
        {
            V(1, 2, "rolled_back", manualCurrent: true),
            V(1, 1, "active", manualCurrent: false),
        };

        var labels = FwHistoryStatus.Labels(versions);

        Assert.Equal(new[] { FwHistoryStatus.RolledBack, FwHistoryStatus.Current }, labels);
    }

    // ── LabelsByGroup (таблица «Прошивки» — несколько шкафов сразу) ─────────────

    private static FwVersionRecord Vg(int id, int subtypeId, int controllerId, int hw, int sw, string dtStr, string status = "active") =>
        new() { Id = id, SubtypeId = subtypeId, ControllerId = controllerId, HwVersion = hw, SwVersion = sw, DtStr = dtStr, VersionRaw = $"1.1.{hw:000}.{sw:0000}.{dtStr}", Status = status };

    [Fact]
    public void LabelsByGroup_ComputesStatusIndependentlyPerCabinet()
    {
        // Жалоба: «5 версий одного шкафа — все Активна». Два разных шкафа (разные subtype/controller)
        // не должны влиять на статус друг друга — самая свежая версия КАЖДОГО отдельно «Текущая».
        var versions = new List<FwVersionRecord>
        {
            Vg(1, subtypeId: 10, controllerId: 100, hw: 1, sw: 3, dtStr: "20260103_0000"),
            Vg(2, subtypeId: 10, controllerId: 100, hw: 1, sw: 2, dtStr: "20260102_0000"),
            Vg(3, subtypeId: 10, controllerId: 100, hw: 1, sw: 1, dtStr: "20260101_0000"),
            Vg(4, subtypeId: 20, controllerId: 200, hw: 1, sw: 1, dtStr: "20260101_0000"),
        };

        var labels = FwHistoryStatus.LabelsByGroup(versions);

        // id=1 — самая свежая версия первого шкафа (sw3) → «Текущая»; id=2/3 — «Заменена».
        Assert.Equal(FwHistoryStatus.Current, labels[1]);
        Assert.Equal(FwHistoryStatus.Superseded, labels[2]);
        Assert.Equal(FwHistoryStatus.Superseded, labels[3]);
        // Единственная версия ВТОРОГО шкафа — «Текущая» сама по себе, а не «Заменена» версией id=1.
        Assert.Equal(FwHistoryStatus.Current, labels[4]);
    }

    [Fact]
    public void LabelsByGroup_RolledBackVersion_KeepsItsOwnLabel()
    {
        var versions = new List<FwVersionRecord>
        {
            Vg(1, subtypeId: 10, controllerId: 100, hw: 1, sw: 2, dtStr: "20260102_0000", status: "rolled_back"),
            Vg(2, subtypeId: 10, controllerId: 100, hw: 1, sw: 1, dtStr: "20260101_0000"),
        };

        var labels = FwHistoryStatus.LabelsByGroup(versions);

        Assert.Equal(FwHistoryStatus.RolledBack, labels[1]);
        Assert.Equal(FwHistoryStatus.Current, labels[2]);
    }
}
