using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Отметка «эта версия сейчас в шкафах» доезжает до остальных.
///
/// Жалоба Ильи 18.09.2026: «коллега сделал KINCO ПЖ 2.0, делает текущей, а она откатывается».
/// Отметку ставят именно для того, чтобы остальные видели, какая версия считается текущей, когда
/// более свежая по номеру забракована. А ездить она не ездила вовсе: жила только на машине того,
/// кто её поставил, и у всех прочих текущей снова оказывалась самая свежая по номеру.
///
/// Переносится ТОЛЬКО «поставлена», как и «инструкции не будет»: машина со старой программой поля
/// не присылает, и её молчание снимало бы отметку туда-сюда при каждом обмене.</summary>
public class ManualCurrentSyncTests
{
    private static int AddVersion(Database db, int sw, string execution = "")
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "ХП");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");
        return db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion,
            SwVersion = sw,
            DtStr = $"2026010{sw}_0000",
            VersionRaw = $"3.2.000{mod.HwVersion}.000{sw}.2026010{sw}_0000",
            Filename = "fw.psl",
            LaunchTypes = new List<string> { "ПЧ" },
            Status = "active",
            Execution = execution,
        });
    }

    private static void Sync(TwoMachines m)
    {
        ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");
        ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(m.Root.Path), m.Root.Path);
    }

    private static bool IsMarked(Database db, string versionRaw) =>
        db.GetAllFwVersionsWithNames().Any(v => v.VersionRaw == versionRaw && v.ManualCurrent);

    [Fact]
    public void TheMark_TravelsToTheOtherMachine()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        AddVersion(m.DbA, 1);
        var older = m.DbA.GetAllFwVersionsWithNames().Single(v => v.SwVersion == 1);
        AddVersion(m.DbA, 2);

        // Свежая по номеру забракована, в шкафах осталась первая — отмечаем её руками.
        Assert.True(m.DbA.SetFwVersionManualCurrent(older.Id!.Value));

        Sync(m);

        Assert.True(IsMarked(m.DbB, older.VersionRaw),
            "отметка не доехала: " + string.Join(", ", m.DbB.GetAllFwVersionsWithNames().Select(v => $"{v.VersionRaw}={v.ManualCurrent}")));
    }

    /// <summary>В группе отметка остаётся ОДНА и после обмена. Иначе у получателя оказались бы две
    /// «текущих», и метка перестала бы что-либо значить.</summary>
    [Fact]
    public void AfterSync_OnlyOneMarkSurvivesInTheGroup()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        AddVersion(m.DbA, 1);
        AddVersion(m.DbA, 2);
        var first = m.DbA.GetAllFwVersionsWithNames().Single(v => v.SwVersion == 1);
        var second = m.DbA.GetAllFwVersionsWithNames().Single(v => v.SwVersion == 2);

        m.DbA.SetFwVersionManualCurrent(second.Id!.Value);
        Sync(m);

        // Теперь на A передумали и отметили первую.
        m.DbA.SetFwVersionManualCurrent(first.Id!.Value);
        Sync(m);

        var marked = m.DbB.GetAllFwVersionsWithNames().Where(v => v.ManualCurrent).Select(v => v.VersionRaw).ToList();
        Assert.Equal(new[] { first.VersionRaw }, marked);
    }

    /// <summary>Машина, которая об отметке ещё не знает, не должна её снимать. Это то же правило,
    /// что у «инструкции не будет»: молчание старой программы — не решение.</summary>
    [Fact]
    public void ASnapshotWithoutTheMark_DoesNotClearIt()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        AddVersion(m.DbA, 1);
        AddVersion(m.DbA, 2);
        Sync(m);

        // Отметку поставили на B (у A её нет и не будет).
        var older = m.DbB.GetAllFwVersionsWithNames().Single(v => v.SwVersion == 1);
        m.DbB.SetFwVersionManualCurrent(older.Id!.Value);

        // Приезжает снимок A — без отметки.
        Sync(m);

        Assert.True(IsMarked(m.DbB, older.VersionRaw), "чужое молчание сняло отметку");
    }
}
