using System;
using System.Linq;
using System.Threading;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Covers the hierarchy conflict-resolution feature added after EndToEndSyncTests proved
/// (not just suspected) that two machines editing the SAME group/subtype/controller/modification field
/// between syncs silently lets whoever exports LAST clobber the other's edit with no warning. These
/// tests exercise the fix: Database.ClassifyHierarchyChange (via ImportHierarchyDataCore) now detects
/// a genuine two-sided edit using each row's updated_at against a per-row "last agreed" watermark
/// (hierarchy_sync_watermarks — NOT just "both timestamps look recent"), holds the row back instead of
/// auto-applying it, and lets the operator resolve it via Database.ResolveHierarchyConflict — whole row
/// at a time, per the approved "Конфликты синхронизации" design, never per-field.</summary>
public class ConflictResolutionTests
{
    /// <summary>Regression test for the exact Round-26 class of bug called out in the task: a
    /// timestamp column written with one separator ("T") compared against one written with another
    /// (space) silently breaks ordinal comparison forever. equipment_groups.updated_at must use the
    /// SAME format Database.NowIso() already uses everywhere else it matters (fw_version_reservations.
    /// expires_at, tickets.updated_at) — space-separated "yyyy-MM-dd HH:mm:ss", no 'T', no timezone —
    /// and two timestamps taken a second apart must compare as expected via plain string.CompareOrdinal
    /// (exactly how ClassifyHierarchyChange compares them, no DateTime.Parse involved).</summary>
    [Fact]
    public void UpdatedAt_UsesNowIsoFormat_AndOrdinalCompareOrdersCorrectly()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var group = db.GetAllEquipmentGroups().First();
        Assert.False(string.IsNullOrEmpty(group.UpdatedAt));
        // Exactly "yyyy-MM-dd HH:mm:ss" — a literal 'T' anywhere here is exactly the historical bug.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", group.UpdatedAt);
        Assert.DoesNotContain("T", group.UpdatedAt);

        Thread.Sleep(1100);
        db.RenameEquipmentGroup(group.Id!.Value, group.Name); // touches updated_at without changing the name
        var reRead = db.GetAllEquipmentGroups().First(g => g.Id == group.Id);

        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", reRead.UpdatedAt);
        // Same format on both sides of the comparison the conflict detector actually performs.
        Assert.True(string.CompareOrdinal(reRead.UpdatedAt, group.UpdatedAt) > 0,
            $"expected '{reRead.UpdatedAt}' > '{group.UpdatedAt}' under plain ordinal compare");
    }

    [Fact]
    public void RealConflict_BothSidesRenameSameGroup_IsHeldBack_NotAutoApplied()
    {
        using var dbFileA = new TempDb();
        using var dbFileB = new TempDb();
        using var dbA = new Database(dbFileA.Path);
        using var dbB = new Database(dbFileB.Path);

        // Handshake: while both sides still match on every field, sync once so sync_ids correlate
        // AND a watermark gets established for every matched row (see SetHierarchyWatermark calls
        // in ImportHierarchyDataCore's "fields already match" / "local wins" branches) — without
        // this, ClassifyHierarchyChange would see an empty watermark on the FIRST real edit and
        // (correctly, but not what this test wants) call it a conflict too, since it can't tell
        // "never synced" apart from "synced and then both diverged".
        dbB.ImportHierarchyData(dbA.ExportHierarchyData());

        var groupA = dbA.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        var groupB = dbB.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        Assert.Equal(groupA.SyncId, groupB.SyncId); // handshake correlated them

        // A third, untouched group — proves a real conflict elsewhere doesn't block unrelated changes.
        var otherGroupA = dbA.GetAllEquipmentGroups().First(g => g.Name == "НГР");

        Thread.Sleep(1100); // NowIso() has 1-second resolution — must clear the watermark's second.

        // Both machines rename the SAME group to DIFFERENT values — a genuine concurrent edit,
        // both through the real app-level edit path (RenameEquipmentGroup), which is what actually
        // stamps updated_at now.
        dbA.RenameEquipmentGroup(groupA.Id!.Value, "ПЖ-НА-МАШИНЕ-A");
        dbB.RenameEquipmentGroup(groupB.Id!.Value, "ПЖ-НА-МАШИНЕ-B");
        // A also makes an unrelated, non-conflicting change B never touched.
        dbA.RenameEquipmentGroup(otherGroupA.Id!.Value, "НГР-ПЕРЕИМЕНОВАНА-A");

        Thread.Sleep(1100);
        var exported = dbA.ExportHierarchyData();
        var counts = dbB.ImportHierarchyData(exported);

        // The conflicting group: NOT auto-applied, B keeps its own value.
        Assert.Equal(1, counts.ConflictsFound);
        // Exactly 1 GroupsUpdated — the OTHER (non-conflicting) rename below, never the
        // conflicting one itself (a conflict must never also count as an applied update).
        Assert.Equal(1, counts.GroupsUpdated);
        var groupBAfter = dbB.GetAllEquipmentGroups().First(g => g.Id == groupB.Id);
        Assert.Equal("ПЖ-НА-МАШИНЕ-B", groupBAfter.Name); // own edit preserved, not silently overwritten

        Assert.Equal(1, dbB.PendingHierarchyConflictCount());
        var conflict = dbB.GetPendingHierarchyConflicts().Single();
        Assert.Equal("group", conflict.EntityType);
        var nameField = conflict.Fields.Single(f => f.FieldLabel == "Название");
        Assert.Equal("ПЖ-НА-МАШИНЕ-B", nameField.LocalValue);
        Assert.Equal("ПЖ-НА-МАШИНЕ-A", nameField.IncomingValue);

        // The UNRELATED non-conflicting rename DID propagate — a conflict elsewhere never blocks it.
        var otherGroupB = dbB.GetAllEquipmentGroups().First(g => g.Name == "НГР-ПЕРЕИМЕНОВАНА-A");
        Assert.NotNull(otherGroupB);
    }

    [Fact]
    public void ResolveConflict_KeepOwn_PreservesLocalValue_AndDoesNotReappearOnNextSync()
    {
        using var dbFileA = new TempDb();
        using var dbFileB = new TempDb();
        using var dbA = new Database(dbFileA.Path);
        using var dbB = new Database(dbFileB.Path);
        dbB.ImportHierarchyData(dbA.ExportHierarchyData());

        var groupA = dbA.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        var groupB = dbB.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");

        Thread.Sleep(1100);
        dbA.RenameEquipmentGroup(groupA.Id!.Value, "ПЖ-A");
        dbB.RenameEquipmentGroup(groupB.Id!.Value, "ПЖ-B");
        Thread.Sleep(1100);

        var exported1 = dbA.ExportHierarchyData();
        dbB.ImportHierarchyData(exported1);
        Assert.Equal(1, dbB.PendingHierarchyConflictCount());

        dbB.ResolveHierarchyConflict(groupB.SyncId, keepIncoming: false); // "оставить мою"
        Assert.Equal(0, dbB.PendingHierarchyConflictCount());
        var afterResolve = dbB.GetAllEquipmentGroups().First(g => g.Id == groupB.Id);
        Assert.Equal("ПЖ-B", afterResolve.Name);

        // A syncs the SAME unchanged export again (e.g. the next periodic pull tick before A has
        // made any further edit) — must NOT resurrect the same conflict, and must NOT silently
        // overwrite B's now-confirmed value either.
        var counts2 = dbB.ImportHierarchyData(exported1);
        Assert.Equal(0, counts2.ConflictsFound);
        Assert.Equal(0, dbB.PendingHierarchyConflictCount());
        var stillB = dbB.GetAllEquipmentGroups().First(g => g.Id == groupB.Id);
        Assert.Equal("ПЖ-B", stillB.Name);
    }

    [Fact]
    public void ResolveConflict_KeepIncoming_AdoptsDiskValue_AndDoesNotReappearOnNextSync()
    {
        using var dbFileA = new TempDb();
        using var dbFileB = new TempDb();
        using var dbA = new Database(dbFileA.Path);
        using var dbB = new Database(dbFileB.Path);
        dbB.ImportHierarchyData(dbA.ExportHierarchyData());

        var groupA = dbA.GetAllEquipmentGroups().First(g => g.Name == "ТГР");
        var groupB = dbB.GetAllEquipmentGroups().First(g => g.Name == "ТГР");

        Thread.Sleep(1100);
        dbA.RenameEquipmentGroup(groupA.Id!.Value, "ТГР-с-диска");
        dbB.RenameEquipmentGroup(groupB.Id!.Value, "ТГР-своя");
        Thread.Sleep(1100);

        var exported1 = dbA.ExportHierarchyData();
        dbB.ImportHierarchyData(exported1);
        Assert.Equal(1, dbB.PendingHierarchyConflictCount());

        dbB.ResolveHierarchyConflict(groupB.SyncId, keepIncoming: true); // "оставить с диска"
        Assert.Equal(0, dbB.PendingHierarchyConflictCount());
        var afterResolve = dbB.GetAllEquipmentGroups().First(g => g.Id == groupB.Id);
        Assert.Equal("ТГР-с-диска", afterResolve.Name);

        var counts2 = dbB.ImportHierarchyData(exported1);
        Assert.Equal(0, counts2.ConflictsFound);
        Assert.Equal(0, dbB.PendingHierarchyConflictCount());
        var stillA = dbB.GetAllEquipmentGroups().First(g => g.Id == groupB.Id);
        Assert.Equal("ТГР-с-диска", stillA.Name);
    }

    [Fact]
    public void SubtypeConflict_BothSidesRenameSameSubtype_IsHeldBack()
    {
        using var dbFileA = new TempDb();
        using var dbFileB = new TempDb();
        using var dbA = new Database(dbFileA.Path);
        using var dbB = new Database(dbFileB.Path);
        dbB.ImportHierarchyData(dbA.ExportHierarchyData());

        var groupA = dbA.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        var subA = dbA.GetSubtypesForGroup(groupA.Id!.Value).First(s => s.Name == "ХП");
        var groupB = dbB.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        var subB = dbB.GetSubtypesForGroup(groupB.Id!.Value).First(s => s.Name == "ХП");
        Assert.Equal(subA.SyncId, subB.SyncId);

        Thread.Sleep(1100);
        dbA.RenameEquipmentSubtype(subA.Id!.Value, "ХП-A", "ХП-A");
        dbB.RenameEquipmentSubtype(subB.Id!.Value, "ХП-B", "ХП-B");
        Thread.Sleep(1100);

        var exported = dbA.ExportHierarchyData();
        var counts = dbB.ImportHierarchyData(exported);

        Assert.Equal(1, counts.ConflictsFound);
        Assert.Equal(0, counts.SubtypesUpdated);
        var subBAfter = dbB.GetSubtypesForGroup(groupB.Id!.Value).First(s => s.Id == subB.Id);
        Assert.Equal("ХП-B", subBAfter.Name); // preserved, not silently overwritten

        var conflict = dbB.GetPendingHierarchyConflicts().Single(c => c.EntityType == "subtype");
        var nameField = conflict.Fields.Single(f => f.FieldLabel == "Название");
        Assert.Equal("ХП-B", nameField.LocalValue);
        Assert.Equal("ХП-A", nameField.IncomingValue);
    }
}
