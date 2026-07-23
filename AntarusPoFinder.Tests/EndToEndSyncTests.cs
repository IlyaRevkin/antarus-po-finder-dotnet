using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.App;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Full two-machine round trip through the REAL sync code paths (ConfigSyncService +
/// TicketSyncService, not just Database.ImportHierarchyData directly like ConfigSyncTests) — two
/// independent profiles (own Database/ConfigService/HierarchyService, matching how two separate PCs
/// each have their own local AppData) sharing one on-disk folder standing in for the network drive's
/// "Конфиг" channel. Written after a live-app audit round (see PC memory) that found most sync fixes
/// so far were verified only at the Database.ImportHierarchyData level, never end-to-end through
/// Export()/Apply()/CheckForUpdate()/TicketSyncService — this fills that gap for the six scenarios
/// specifically called out as untested: hierarchy+firmware+HMI/modbus+ticket+reservation propagation,
/// delete propagation, concurrent-edit "conflict" behavior, and param_files staying stable across
/// repeated rounds.
///
/// Uses the TestHelpers.TwoMachines fixture (own Database/ConfigService/HierarchyService/AppServices
/// per simulated machine, via AppServices' test-only (Database, ConfigService, HierarchyService)
/// constructor — plain `new AppServices()` can't represent two machines in one process because
/// ConfigService.AppData/DbPath are `static readonly`, resolved once per process).</summary>
public class EndToEndSyncTests
{
    [Fact]
    public void FullTwoMachineRoundTrip_HierarchyFirmwareHmiModbusTicketReservation_DeleteAndParamFileStability()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;
        var dbA = m.DbA; var dbB = m.DbB;
        var hierA = m.HierA;
        var svcA = m.SvcA; var svcB = m.SvcB;

        // ── Step 2: build up profile A ───────────────────────────────────────
        var groupA = dbA.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var newSubtypeId = dbA.UpsertEquipmentSubtype(new EquipmentSubType
        {
            GroupId = groupA.Id!.Value, Name = "Е2Е-ТЕСТ", Prefix = 77, FolderName = "Е2Е-ТЕСТ", SortOrder = 99,
        });
        var deletableSubtypeId = dbA.UpsertEquipmentSubtype(new EquipmentSubType
        {
            GroupId = groupA.Id!.Value, Name = "Е2Е-НА-УДАЛЕНИЕ", Prefix = 78, FolderName = "Е2Е-НА-УДАЛЕНИЕ", SortOrder = 100,
        });
        var pixel2 = dbA.GetAllControllerModels().First(c => c.Name == "PIXEL2");
        dbA.AddControllerModification(pixel2.Id!.Value, "PIXEL2-E2E", 5, "новая тестовая модификация для e2e-синка");
        var newMod = dbA.GetAllModifications().First(m2 => m2.DisplayName == "PIXEL2-E2E");

        // Upload firmware WITH an HMI project and a modbus map — real files on the shared
        // "disk", not just DB rows, same as UploadView.Upload_Click actually does.
        var fwDstFolder = hierA.FwPath(root, groupA.Name, "Е2Е-ТЕСТ", newMod.ControllerName, "77.99.5.1.20260721_1000");
        Directory.CreateDirectory(fwDstFolder);
        File.WriteAllText(Path.Combine(fwDstFolder, "firmware.psl"), "fake plc firmware");

        var hmiRoot = hierA.HmiPath(root, groupA.Name, "Е2Е-ТЕСТ", newMod.ControllerName);
        var hmiDstFolder = Path.Combine(hmiRoot, "77.99.5.1.20260721_1000_hmi");
        Directory.CreateDirectory(hmiDstFolder);
        File.WriteAllText(Path.Combine(hmiDstFolder, "screen1.fsprj"), "fake hmi project");
        File.WriteAllText(Path.Combine(hmiDstFolder, "driver.dll"), "fake driver");

        var modbusDstFolder = hierA.ModbusMapPath(root, groupA.Name, "Е2Е-ТЕСТ", newMod.ControllerName);
        Directory.CreateDirectory(modbusDstFolder);
        File.WriteAllText(Path.Combine(modbusDstFolder, "modbus_map.xlsx"), "fake modbus map");

        var userA = dbA.GetOrCreateUser("profileA", "profileA");
        var newFwId = dbA.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = newSubtypeId, ControllerId = newMod.ControllerId,
            EqPrefix = groupA.Prefix, SubPrefix = 77, HwVersion = newMod.HwVersion, SwVersion = 1,
            DtStr = "20260721_1000", VersionRaw = "77.99.5.1.20260721_1000",
            Filename = "firmware.psl", DiskPath = fwDstFolder,
            Description = "e2e sync test upload", Changelog = "e2e sync test upload",
            HmiPath = hmiDstFolder, HmiExecutableHint = "screen1.fsprj",
            ModbusMapPath = Path.Combine(modbusDstFolder, "modbus_map.xlsx"),
            AuthorId = userA.Id, Status = "active",
        });
        Assert.True(newFwId > 0);

        // A ticket, created the same way TicketsView does: insert locally + enqueue outbox event.
        var ticket = new Ticket
        {
            Id = Guid.NewGuid().ToString(), Type = TicketType.Bug, Text = "e2e sync test ticket",
            Status = TicketStatus.Open, CreatedBy = "profileA", CreatedByRole = "programmer",
            CreatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"), UpdatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        };
        dbA.InsertTicketIfMissing(ticket);
        var (evFile, evPayload) = TicketSyncService.BuildCreateEvent(ticket);
        dbA.EnqueueTicketOutbox(evFile, evPayload);

        // A reserved version number for the new subtype/controller.
        var reservation = dbA.ReserveNextVersion(newSubtypeId, newMod.ControllerId, newMod.HwVersion,
            groupA.Prefix, 77, "profileA", includeDate: true, ttlHours: 72);
        Assert.Equal("reserved", reservation.Status);

        // ── Step 3: A pushes to the shared disk, B pulls and applies ────────────
        var exportResult = ConfigSyncService.Export(svcA, root, "profileA");
        Assert.True(File.Exists(ConfigSyncService.ConfigPathFor(root)));
        var ticketsSent = TicketSyncService.FlushOutbox(svcA, root);
        Assert.Equal(1, ticketsSent);

        var update = ConfigSyncService.CheckForUpdate(svcB, out var checkErr);
        Assert.True(checkErr is null, checkErr);
        Assert.NotNull(update);
        Assert.True(update!.Diff.TotalChanges > 0);

        var applyResult = ConfigSyncService.Apply(svcB, update.ConfigPath, root);
        Assert.Equal(exportResult.ExportedAt, applyResult.ExportedAt);
        var ticketsApplied = TicketSyncService.PullNewEvents(svcB, root);
        Assert.Equal(1, ticketsApplied);

        // Hierarchy landed on B.
        var groupB = dbB.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtypeB = dbB.GetSubtypesForGroup(groupB.Id!.Value).FirstOrDefault(s => s.Name == "Е2Е-ТЕСТ");
        Assert.NotNull(subtypeB);
        Assert.NotNull(dbB.GetSubtypesForGroup(groupB.Id!.Value).FirstOrDefault(s => s.Name == "Е2Е-НА-УДАЛЕНИЕ"));
        Assert.Contains(dbB.GetAllModifications(), m2 => m2.DisplayName == "PIXEL2-E2E");

        // Firmware + HMI + modbus fields landed on B, fully populated (not blank).
        var fwB = dbB.GetAllFwVersionsWithNames(includeArchived: true).FirstOrDefault(f => f.VersionRaw == "77.99.5.1.20260721_1000");
        Assert.NotNull(fwB);
        Assert.Equal(hmiDstFolder, fwB!.HmiPath);
        Assert.Equal("screen1.fsprj", fwB.HmiExecutableHint);
        Assert.Equal(Path.Combine(modbusDstFolder, "modbus_map.xlsx"), fwB.ModbusMapPath);

        // Ticket landed on B.
        Assert.Contains(dbB.GetTickets(), t => t.Id == ticket.Id && t.Text == "e2e sync test ticket");

        // Reservation landed on B (natural-key matched, still "reserved").
        var resB = dbB.GetActiveReservations(subtypeB!.Id!.Value, newMod.ControllerId, newMod.HwVersion);
        Assert.Contains(resB, r => r.VersionRaw == reservation.VersionRaw && r.Status == "reserved");

        // ── Step 4: B deletes the empty test subtype, A must see the deletion, not a resurrection ──
        var subtypeToDeleteB = dbB.GetSubtypesForGroup(groupB.Id!.Value).First(s => s.Name == "Е2Е-НА-УДАЛЕНИЕ");
        dbB.DeleteEquipmentSubtype(subtypeToDeleteB.Id!.Value);
        Assert.DoesNotContain(dbB.GetSubtypesForGroup(groupB.Id!.Value), s => s.Name == "Е2Е-НА-УДАЛЕНИЕ");

        // exported_at has second resolution (see ConfigSyncService.Export) — two exports inside
        // the same wall-clock second are indistinguishable to CheckForUpdate's ordinal compare,
        // which would make the second one silently invisible. A real gap between two machines'
        // manual pushes is normally seconds/minutes, not milliseconds, so this sleep just
        // reflects realistic timing rather than working around a bug in what's under test.
        System.Threading.Thread.Sleep(1100);
        var exportB = ConfigSyncService.Export(svcB, root, "profileB");
        var updateForA = ConfigSyncService.CheckForUpdate(svcA, out var checkErrA);
        Assert.True(checkErrA is null, checkErrA);
        Assert.NotNull(updateForA);
        ConfigSyncService.Apply(svcA, updateForA!.ConfigPath, root);

        Assert.DoesNotContain(dbA.GetSubtypesForGroup(groupA.Id!.Value), s => s.Name == "Е2Е-НА-УДАЛЕНИЕ");
        // The OTHER new subtype (which has firmware attached) must have survived on A untouched —
        // deletion propagation must be scoped to what was actually deleted, not a blanket wipe.
        Assert.Contains(dbA.GetSubtypesForGroup(groupA.Id!.Value), s => s.Name == "Е2Е-ТЕСТ");
        Assert.Contains(dbA.GetAllFwVersionsWithNames(includeArchived: true), f => f.VersionRaw == "77.99.5.1.20260721_1000");

        // ── Step 5: concurrent edit of the SAME field on both machines ──────────
        // Documents actual behavior, since there is NO conflict-resolution screen anywhere in
        // this codebase (grepped: no "конфликт" outside .resources.dll, no updated_at/version
        // column on equipment_groups/equipment_subtypes/controller_modifications — only
        // fw_versions.status/released and tickets.updated_at get an "only advances" merge rule).
        // Both machines already have PIXEL2-E2E from step 3. Change its description independently
        // on both sides, then push A -> B: this is a plain last-exporter-wins full-field UPDATE
        // (Database.ConfigExchange, the "controller modifications" block), so B's own untransmitted
        // edit is silently overwritten with no warning and no merge UI — verified here exactly as
        // it happens today, not as it "should" happen.
        var modIdA = dbA.GetAllModifications().First(m2 => m2.DisplayName == "PIXEL2-E2E").Id!.Value;
        var modIdB = dbB.GetAllModifications().First(m2 => m2.DisplayName == "PIXEL2-E2E").Id!.Value;
        UpdateModificationDescription(m.PathA, modIdA, "изменено на машине A");
        UpdateModificationDescription(m.PathB, modIdB, "изменено на машине B");

        System.Threading.Thread.Sleep(1100);
        ConfigSyncService.Export(svcA, root, "profileA");
        var updateForB2 = ConfigSyncService.CheckForUpdate(svcB, out _);
        Assert.NotNull(updateForB2);
        ConfigSyncService.Apply(svcB, updateForB2!.ConfigPath, root);

        var modAfter = dbB.GetAllModifications().First(m2 => m2.DisplayName == "PIXEL2-E2E");
        Assert.Equal("изменено на машине A", modAfter.Description); // B's own edit was silently lost — no conflict prompt exists to catch this

        // ── Step 6: param_files must not grow across repeated sync rounds ───────
        var subtypeAforParams = dbA.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        var subForParams = dbA.GetSubtypesForGroup(subtypeAforParams.Id!.Value).First(s => s.Name == "ХП");
        dbA.AddParamFile(new ParamFile
        {
            SubtypeId = subForParams.Id!.Value, Manufacturer = "Danfoss", Filename = "e2e_params.dcfx",
            DiskPath = Path.Combine(root, "Параметры", "ПЖ", "ХП", "Danfoss", "e2e_params.dcfx"),
            Description = "e2e param file", UploadDate = "2026-07-21 10:00:00",
        });

        for (int round = 0; round < 3; round++)
        {
            System.Threading.Thread.Sleep(1100);
            ConfigSyncService.Export(svcA, root, "profileA");
            var upd = ConfigSyncService.CheckForUpdate(svcB, out _);
            if (upd is not null) ConfigSyncService.Apply(svcB, upd.ConfigPath, root);
        }

        var paramFilesB = dbB.GetParamFiles().Where(f => f.Filename == "e2e_params.dcfx").ToList();
        Assert.Single(paramFilesB); // must stay exactly one row after 3 repeated sync rounds, not grow
    }

    /// <summary>Две повторные жалобы пользователя одним сценарием: «добавил производителей ПЧ/УПП —
    /// у коллеги их нет» и «удалил тип шкафа, а он никуда не делся». Обе — про общий справочник,
    /// и обе проверяются здесь через реальный Export/CheckForUpdate/Apply, а не через
    /// ImportHierarchyData напрямую.
    ///
    /// Тип шкафа БЕЗ подтипов взят намеренно: зеркалирование удаления пропускает группу, на которую
    /// на принимающей машине ещё кто-то ссылается (GroupsSkippedDelete) — сознательная защита от
    /// сноса чужих данных, так что удаляемость проверяется на том случае, где она обязана работать.
    /// Последний круг — про воскрешение: удалённый тип не должен вернуться на следующем же обмене,
    /// именно так он возвращался раньше (см. FORTUS в памяти проекта).</summary>
    [Fact]
    public void CatalogChange_ManufacturerAddedAndEmptyGroupDeleted_ReachesOtherMachineAndStaysDeleted()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;
        var dbA = m.DbA; var dbB = m.DbB;

        dbA.AddParamManufacturer("Е2Е-ПРОИЗВОДИТЕЛЬ");
        var throwawayGroupId = dbA.UpsertEquipmentGroup(new EquipmentGroup
        {
            Name = "Е2Е-МУСОРНЫЙ-ТИП", Prefix = 91, SortOrder = 91,
        });
        Assert.Empty(dbA.GetSubtypesForGroup(throwawayGroupId));

        ConfigSyncService.Export(m.SvcA, root, "profileA");
        var update = ConfigSyncService.CheckForUpdate(m.SvcB, out var err);
        Assert.True(err is null, err);
        ConfigSyncService.Apply(m.SvcB, update!.ConfigPath, root);

        Assert.Contains("Е2Е-ПРОИЗВОДИТЕЛЬ", dbB.GetParamManufacturers());
        Assert.Contains(dbB.GetAllEquipmentGroups(), g => g.Name == "Е2Е-МУСОРНЫЙ-ТИП");

        // ── A удаляет и производителя, и тип шкафа — B обязан отзеркалить оба удаления ──
        System.Threading.Thread.Sleep(1100); // exported_at посекундный, см. соседние тесты
        dbA.DeleteParamManufacturer("Е2Е-ПРОИЗВОДИТЕЛЬ");
        dbA.DeleteEquipmentGroup(throwawayGroupId);

        ConfigSyncService.Export(m.SvcA, root, "profileA");
        var update2 = ConfigSyncService.CheckForUpdate(m.SvcB, out var err2);
        Assert.True(err2 is null, err2);
        var applied = ConfigSyncService.Apply(m.SvcB, update2!.ConfigPath, root);

        Assert.DoesNotContain("Е2Е-ПРОИЗВОДИТЕЛЬ", dbB.GetParamManufacturers());
        Assert.DoesNotContain(dbB.GetAllEquipmentGroups(), g => g.Name == "Е2Е-МУСОРНЫЙ-ТИП");
        Assert.True(applied.Counts.GroupsRemoved >= 1);

        // ── Ещё один обмен: удалённое не воскресает ──
        System.Threading.Thread.Sleep(1100);
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        var update3 = ConfigSyncService.CheckForUpdate(m.SvcB, out _);
        if (update3 is not null) ConfigSyncService.Apply(m.SvcB, update3.ConfigPath, root);
        Assert.DoesNotContain("Е2Е-ПРОИЗВОДИТЕЛЬ", dbB.GetParamManufacturers());
        Assert.DoesNotContain(dbB.GetAllEquipmentGroups(), g => g.Name == "Е2Е-МУСОРНЫЙ-ТИП");
    }

    private static void UpdateModificationDescription(string dbPath, int modificationId, string description)
    {
        // There's no in-app rename UI for this yet — opens a second raw connection to the same
        // already-open Database's underlying file (WAL mode allows concurrent readers/writers), same
        // trick ConfigSyncTests uses, so this exercises the sync layer's handling without needing
        // the feature to exist yet.
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE controller_modifications SET description=@d WHERE id=@id";
        cmd.Parameters.AddWithValue("@d", description);
        cmd.Parameters.AddWithValue("@id", modificationId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Repro for the reported "у меня путь \\ant-srv, у коллег Z:\ — коллега видит прошивки,
    /// которых по факту нет" bug: two machines reach the SAME physical shared drive under different
    /// local path notations (admin's UNC path vs. a colleague's mapped WebDAV drive letter). A's
    /// fw_versions.DiskPath is baked in under A's own root when uploaded — Apply() must rewrite that
    /// prefix onto B's own root (RemapFwPaths), or the imported row points at a path that doesn't
    /// resolve on B at all. This used to work via the (never-synced, by design) "root_path" settings
    /// key doubling as remap source — Round 38's fix to stop leaking local-only settings into the
    /// shared config (see ConfigSyncService.SkipSettingsKeys doc) silently broke it, since Export()
    /// stopped writing "root_path" at all, so Apply() always saw an empty oldRoot and never remapped.
    /// ConfigSyncService now carries the exporter's root separately as "source_root_path" metadata
    /// specifically to keep this working without re-leaking root_path as an applied setting.</summary>
    [Fact]
    public void Apply_DifferentMachineRoot_RemapsFwVersionDiskPath()
    {
        using var m = new TwoMachines();
        var sharedRoot = m.Root.Path; // stands in for the physical network drive both machines reach
        var dbA = m.DbA; var dbB = m.DbB;
        var hierA = m.HierA;
        var svcA = m.SvcA; var svcB = m.SvcB;
        m.CfgA.SetRootPath(sharedRoot); // A's own local notation, e.g. \\ant-srv\Software\Antarus Finder

        var group = dbA.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = dbA.GetSubtypesForGroup(group.Id!.Value).First();
        var pixel2 = dbA.GetAllControllerModels().First(c => c.Name == "PIXEL2");
        dbA.AddControllerModification(pixel2.Id!.Value, "PIXEL2-REMAP-TEST", 9, "remap test modification");
        var mod = dbA.GetAllModifications().First(m2 => m2.DisplayName == "PIXEL2-REMAP-TEST");

        var fwFolder = hierA.FwPath(sharedRoot, group.Name, subtype.Name, mod.ControllerName, "1.99.9.1.20260722_1000");
        Directory.CreateDirectory(fwFolder);
        File.WriteAllText(Path.Combine(fwFolder, "firmware.psl"), "fake plc firmware");
        var userA = dbA.GetOrCreateUser("profileA", "profileA");
        dbA.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix, HwVersion = mod.HwVersion, SwVersion = 1,
            DtStr = "20260722_1000", VersionRaw = "1.99.9.1.20260722_1000",
            Filename = "firmware.psl", DiskPath = fwFolder,
            Description = "remap test upload", Changelog = "remap test upload",
            AuthorId = userA.Id, Status = "active",
        });

        ConfigSyncService.Export(svcA, sharedRoot, "profileA");

        // B reaches the very same "Конфиг" folder (sharedRoot, standing in for the real network
        // share) but under a completely different local root notation of its own.
        var configPath = ConfigSyncService.ConfigPathFor(sharedRoot);
        const string machineBRoot = @"Z:\Software\Antarus Finder";
        ConfigSyncService.Apply(svcB, configPath, machineBRoot);

        var fwOnB = dbB.GetAllFwVersionsWithNames(includeArchived: true).Single(f => f.VersionRaw == "1.99.9.1.20260722_1000");
        Assert.StartsWith(machineBRoot, fwOnB.DiskPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sharedRoot, fwOnB.DiskPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Задача 3 — regression test for the exact scenario reported: "A удаляет прошивку → B
    /// ещё не синхронизировался → B синхронизируется → запись на B тоже удаляется, не воскресает".
    /// Before Database.TombstoneFwVersion existed, Настройки → Прошивки → «Удалить прошивку» did a
    /// bare DELETE — local-only, never left the deleting machine, so any OTHER machine that still had
    /// the row would resurrect it on its very next export (this app's own fw_versions import is
    /// additive-only otherwise, see ImportHierarchyDataCore's class doc). This proves both halves of
    /// the fix: the deletion itself reaches B on a normal sync (including the on-disk folder), AND a
    /// late-arriving "still active" copy of the same version (a stale snapshot taken before A deleted
    /// it, standing in for a third machine that hasn't caught up yet) can never revive it once B has
    /// mirrored the tombstone — same permanence guarantee real hierarchy rows already had.</summary>
    [Fact]
    public void FwVersionTombstone_DeletePropagatesToOtherMachineAndNeverResurrects()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;
        var dbA = m.DbA; var dbB = m.DbB;
        var hierA = m.HierA;
        var svcA = m.SvcA; var svcB = m.SvcB;

        var group = dbA.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = dbA.GetSubtypesForGroup(group.Id!.Value).First();
        var pixel2 = dbA.GetAllControllerModels().First(c => c.Name == "PIXEL2");
        dbA.AddControllerModification(pixel2.Id!.Value, "PIXEL2-TOMBSTONE-TEST", 3, "tombstone test modification");
        var mod = dbA.GetAllModifications().First(m2 => m2.DisplayName == "PIXEL2-TOMBSTONE-TEST");
        const string versionRaw = "1.99.3.1.20260722_1100";

        var fwFolder = hierA.FwPath(root, group.Name, subtype.Name, mod.ControllerName, versionRaw);
        Directory.CreateDirectory(fwFolder);
        File.WriteAllText(Path.Combine(fwFolder, "firmware.psl"), "fake plc firmware");
        var userA = dbA.GetOrCreateUser("profileA", "profileA");
        var fwId = dbA.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix, HwVersion = mod.HwVersion, SwVersion = 1,
            DtStr = "20260722_1100", VersionRaw = versionRaw,
            Filename = "firmware.psl", DiskPath = fwFolder,
            Description = "tombstone test upload", Changelog = "tombstone test upload",
            AuthorId = userA.Id, Status = "active",
        });
        Assert.True(fwId > 0);

        // B syncs while the firmware is still active — B genuinely had this row before A ever
        // deleted it, exactly the "B ещё не синхронизировался" starting point from the report.
        ConfigSyncService.Export(svcA, root, "profileA");
        var firstUpdate = ConfigSyncService.CheckForUpdate(svcB, out var firstErr);
        Assert.True(firstErr is null, firstErr);
        Assert.NotNull(firstUpdate);
        ConfigSyncService.Apply(svcB, firstUpdate!.ConfigPath, root);
        Assert.Contains(dbB.GetAllFwVersionsWithNames(includeArchived: true), f => f.VersionRaw == versionRaw);

        // A stale, pre-delete snapshot of A's whole hierarchy — stands in for a THIRD machine that
        // syncs later with an old copy still claiming this version is active, to prove a tombstone
        // can't be resurrected by a late-arriving "active" copy of the same row (see below).
        var staleSnapshot = dbA.ExportHierarchyData();

        // ── A deletes it, same as SettingsView.DeleteFirmware_Click: disk folder gone + tombstoned ──
        Assert.True(Directory.Exists(fwFolder));
        Directory.Delete(fwFolder, recursive: true);
        dbA.TombstoneFwVersion(fwId);
        Assert.DoesNotContain(dbA.GetAllFwVersionsWithNames(includeArchived: true), f => f.VersionRaw == versionRaw);

        // ── B, which never touched the deletion, syncs and must mirror it ──
        System.Threading.Thread.Sleep(1100); // exported_at second-resolution, see the other tests above
        ConfigSyncService.Export(svcA, root, "profileA");
        var update2 = ConfigSyncService.CheckForUpdate(svcB, out var err2);
        Assert.True(err2 is null, err2);
        Assert.NotNull(update2);
        var applyResult2 = ConfigSyncService.Apply(svcB, update2!.ConfigPath, root);
        Assert.True(applyResult2.Counts.FwVersionsRemoved >= 1);

        Assert.DoesNotContain(dbB.GetAllFwVersionsWithNames(includeArchived: true), f => f.VersionRaw == versionRaw);
        Assert.DoesNotContain(dbB.GetFwVersions(subtype.Id!.Value, mod.ControllerId, includeArchived: true, includeRolledBack: true),
            f => f.VersionRaw == versionRaw);

        // ── Resurrection check: importing the stale, pre-delete "still active" snapshot must NOT
        //    bring it back on B — the tombstone B already applied wins permanently. ──
        dbB.ImportHierarchyData(staleSnapshot);
        Assert.DoesNotContain(dbB.GetAllFwVersionsWithNames(includeArchived: true), f => f.VersionRaw == versionRaw);
    }
}
