using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.App;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
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
/// Uses the AppServices(Database, ConfigService, HierarchyService) test-only constructor — plain
/// `new AppServices()` can't represent two machines in one process because ConfigService.AppData/
/// DbPath are `static readonly`, resolved once per process.</summary>
public class EndToEndSyncTests
{
    private static string NewTempDb() => Path.Combine(Path.GetTempPath(), $"antarus_e2e_{Guid.NewGuid():N}.db");
    private static string NewTempRoot() => Path.Combine(Path.GetTempPath(), $"antarus_e2e_root_{Guid.NewGuid():N}");

    private static void Cleanup(string pathA, string pathB, string root)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var db in new[] { pathA, pathB })
            foreach (var f in new[] { db, db + "-wal", db + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        if (Directory.Exists(root))
        {
            // ConfigSyncService.Export protects the config file from external edits (read-only +
            // possibly ACL'd) — clear that before recursive delete, or cleanup itself throws.
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FullTwoMachineRoundTrip_HierarchyFirmwareHmiModbusTicketReservation_DeleteAndParamFileStability()
    {
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        var root = NewTempRoot();
        Directory.CreateDirectory(root);
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);
            var cfgA = new ConfigService(dbA);
            var cfgB = new ConfigService(dbB);
            var hierA = new HierarchyService(dbA);
            var hierB = new HierarchyService(dbB);
            var svcA = new AppServices(dbA, cfgA, hierA) { CurrentAdLogin = "profileA" };
            var svcB = new AppServices(dbB, cfgB, hierB) { CurrentAdLogin = "profileB" };
            cfgA.SetRootPath(root);
            cfgB.SetRootPath(root);

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
            var newMod = dbA.GetAllModifications().First(m => m.DisplayName == "PIXEL2-E2E");

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
            Assert.Null(checkErr);
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
            Assert.Contains(dbB.GetAllModifications(), m => m.DisplayName == "PIXEL2-E2E");

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
            Assert.Null(checkErrA);
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
            var modIdA = dbA.GetAllModifications().First(m => m.DisplayName == "PIXEL2-E2E").Id!.Value;
            var modIdB = dbB.GetAllModifications().First(m => m.DisplayName == "PIXEL2-E2E").Id!.Value;
            UpdateModificationDescription(pathA, modIdA, "изменено на машине A");
            UpdateModificationDescription(pathB, modIdB, "изменено на машине B");

            System.Threading.Thread.Sleep(1100);
            ConfigSyncService.Export(svcA, root, "profileA");
            var updateForB2 = ConfigSyncService.CheckForUpdate(svcB, out _);
            Assert.NotNull(updateForB2);
            ConfigSyncService.Apply(svcB, updateForB2!.ConfigPath, root);

            var modAfter = dbB.GetAllModifications().First(m => m.DisplayName == "PIXEL2-E2E");
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
        finally
        {
            Cleanup(pathA, pathB, root);
        }
    }

    private static void UpdateModificationDescription(string dbPath, int modificationId, string description)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE controller_modifications SET description=@d WHERE id=@id";
        cmd.Parameters.AddWithValue("@d", description);
        cmd.Parameters.AddWithValue("@id", modificationId);
        cmd.ExecuteNonQuery();
    }
}
