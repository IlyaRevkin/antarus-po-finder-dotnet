using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Возврат версии в строй доезжает до остальных машин.
///
/// Срочная жалоба Ильи 24.09.2026: «выложил новую ПЖ 2.0, а индексировалась старая; откатил старую,
/// делаю актуальной новую — она откатывается сама. Сделал актуальной, сразу отправил изменения для
/// синхронизации — всё равно откатилась».
///
/// Причина: состояние переносилось МОНОТОННО — «активная может стать откатанной, обратно никогда».
/// Откат уезжал ко всем, а его отмена нет, и первый же снимок машины, которая об отмене не знает,
/// откатывал версию снова. Дверь в одну сторону.
///
/// Теперь у состояния есть отметка времени, и выигрывает более позднее решение. У снимков со старой
/// версии программы отметки нет — там поведение прежнее, иначе чужое молчание отменяло бы свежий
/// откат.</summary>
public class UnrollbackTravelsTests
{
    private static int AddVersion(Database db, int sw)
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
            VersionRaw = $"3.2.0004.000{sw}.2026010{sw}_0000",
            Filename = "fw.psl",
            LaunchTypes = new List<string> { "ПЧ" },
            Status = "active",
        });
    }

    private static void Sync(TwoMachines m)
    {
        ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");
        ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(m.Root.Path), m.Root.Path);
    }

    private static string StatusOf(Database db, string versionRaw) =>
        db.GetAllFwVersionsWithNames(includeArchived: true).Single(v => v.VersionRaw == versionRaw).Status;

    [Fact]
    public void RollbackTravels_AndSoDoesItsUndo()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        AddVersion(m.DbA, 1);
        Sync(m);
        var raw = m.DbA.GetAllFwVersionsWithNames().Single(v => v.SwVersion == 1).VersionRaw;

        // Откатили на A — доезжает до B.
        var idA = m.DbA.GetAllFwVersionsWithNames(includeArchived: true).Single(v => v.VersionRaw == raw).Id!.Value;
        Assert.True(m.DbA.RollbackFwVersion(idA));
        Sync(m);
        Assert.Equal("rolled_back", StatusOf(m.DbB, raw));

        // Передумали и вернули в строй — это тоже обязано доехать.
        Assert.True(m.DbA.UnrollbackFwVersion(idA));
        Sync(m);
        Assert.Equal("active", StatusOf(m.DbB, raw));
    }

    /// <summary>И обратно: машина, которая об отмене ещё не знает, не должна откатывать версию
    /// снова. Это и есть «откатывается сама» — её снимок приезжал со старым состоянием.</summary>
    [Fact]
    public void AStaleSnapshot_DoesNotRollItBackAgain()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        AddVersion(m.DbA, 1);
        Sync(m);
        var raw = m.DbA.GetAllFwVersionsWithNames().Single(v => v.SwVersion == 1).VersionRaw;
        var idA = m.DbA.GetAllFwVersionsWithNames(includeArchived: true).Single(v => v.VersionRaw == raw).Id!.Value;

        m.DbA.RollbackFwVersion(idA);
        Sync(m);

        // На B версию вернули в строй — позже, чем A её откатила.
        var idB = m.DbB.GetAllFwVersionsWithNames(includeArchived: true).Single(v => v.VersionRaw == raw).Id!.Value;
        Assert.True(m.DbB.UnrollbackFwVersion(idB));

        // Приезжает снимок A — он всё ещё считает версию откатанной.
        Sync(m);

        Assert.Equal("active", StatusOf(m.DbB, raw));
    }
}
