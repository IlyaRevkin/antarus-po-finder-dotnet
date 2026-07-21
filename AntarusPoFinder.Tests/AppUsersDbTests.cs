using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Covers the Database.AppUsers / Database.ConfigExchange half of the roster feature: the
/// local CRUD (TouchOrCreateAppUser/SetAppUserRole/GetAppUsers) and the config-sync merge (role is
/// last-writer-wins by role_updated_at, in EITHER direction — an admin can promote or demote, unlike
/// the one-way "advances" state machines elsewhere in ConfigExchange).</summary>
public class AppUsersDbTests
{
    private static string NewTempDb() => Path.Combine(Path.GetTempPath(), $"antarus_appusers_db_test_{System.Guid.NewGuid():N}.db");

    private static void Cleanup(params string[] dbPaths)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var db in dbPaths)
            foreach (var f in new[] { db, db + "-wal", db + "-shm" })
                if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void TouchOrCreateAppUser_NewLogin_DefaultsToNaladchik()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            var u = db.TouchOrCreateAppUser("revkin.i");

            Assert.Equal("naladchik", u.Role);
            Assert.False(string.IsNullOrEmpty(u.SyncId));
            Assert.Equal(u.FirstLoginAt, u.LastLoginAt);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void SetAppUserRole_ChangesRole_AndBumpsRoleUpdatedAt()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            var u = db.TouchOrCreateAppUser("revkin.i");
            var originalRoleUpdatedAt = u.RoleUpdatedAt;

            db.SetAppUserRole(u.Id!.Value, "administrator");

            var reloaded = db.FindAppUserByLogin("revkin.i")!;
            Assert.Equal("administrator", reloaded.Role);
            Assert.True(string.CompareOrdinal(reloaded.RoleUpdatedAt, originalRoleUpdatedAt) >= 0);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void GetAppUsers_OrdersByLogin()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            db.TouchOrCreateAppUser("zorin.a");
            db.TouchOrCreateAppUser("ivanov.p");

            var all = db.GetAppUsers();
            Assert.Equal(2, all.Count);
            Assert.Equal("ivanov.p", all[0].AdLogin);
            Assert.Equal("zorin.a", all[1].AdLogin);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Import_NewUserOnA_PropagatesToB_ViaExportImport()
    {
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);

            var u = dbA.TouchOrCreateAppUser("revkin.i");
            dbA.SetAppUserRole(u.Id!.Value, "programmer");

            var counts = dbB.ImportHierarchyData(dbA.ExportHierarchyData());
            Assert.Equal(1, counts.AppUsersAdded);

            var onB = dbB.FindAppUserByLogin("revkin.i");
            Assert.NotNull(onB);
            Assert.Equal("programmer", onB!.Role);

            // Idempotent re-import.
            var second = dbB.ImportHierarchyData(dbA.ExportHierarchyData());
            Assert.Equal(0, second.AppUsersAdded);
            Assert.Equal(0, second.AppUsersUpdated);
        }
        finally { Cleanup(pathA, pathB); }
    }

    [Fact]
    public void Import_RoleChangedOnA_AfterBAlreadyKnowsTheUser_PropagatesNewerRoleToB()
    {
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);

            // Handshake: both machines already know this login as naladchik.
            var uA = dbA.TouchOrCreateAppUser("revkin.i");
            dbB.ImportHierarchyData(dbA.ExportHierarchyData());
            Assert.Equal("naladchik", dbB.FindAppUserByLogin("revkin.i")!.Role);

            // Administrator promotes them on machine A (later in time than the handshake above).
            System.Threading.Thread.Sleep(1100); // NowIso() has 1-second resolution
            dbA.SetAppUserRole(uA.Id!.Value, "administrator");

            var counts = dbB.ImportHierarchyData(dbA.ExportHierarchyData());
            Assert.Equal(1, counts.AppUsersUpdated);
            Assert.Equal("administrator", dbB.FindAppUserByLogin("revkin.i")!.Role);
        }
        finally { Cleanup(pathA, pathB); }
    }

    [Fact]
    public void Import_OlderRoleChangeOnA_NeverOverwritesNewerLocalRoleChangeOnB()
    {
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);

            // Handshake.
            var uA = dbA.TouchOrCreateAppUser("revkin.i");
            dbB.ImportHierarchyData(dbA.ExportHierarchyData());
            var uB = dbB.FindAppUserByLogin("revkin.i")!;

            // A's stale export, frozen right after the handshake (still naladchik).
            var staleExportFromA = dbA.ExportHierarchyData();

            // B's administrator promotes the user LATER and locally — this must win even though A's
            // export (computed earlier, from A's now-outdated state) gets imported afterwards.
            System.Threading.Thread.Sleep(1100);
            dbB.SetAppUserRole(uB.Id!.Value, "administrator");

            var counts = dbB.ImportHierarchyData(staleExportFromA);
            Assert.Equal(0, counts.AppUsersUpdated); // B's newer local role change must not be reverted
            Assert.Equal("administrator", dbB.FindAppUserByLogin("revkin.i")!.Role);
        }
        finally { Cleanup(pathA, pathB); }
    }

    [Fact]
    public void MergeAppUsersOnly_LetsANonAdminRosterEntryReachAnotherMachine_WithoutAFullImport()
    {
        // Regression for the reported bug: A (administrator, the only role with a full config push)
        // never saw B (naladchik, first-time AD login) in Настройки → Пользователи, because only
        // the administrator's machine ever pushed — B's own new roster row had no way out. This
        // simulates ConfigSyncService.PushAppUsersOnly's round trip in-memory: B merges A's roster
        // into its own DB (what a "pull" already did before this fix), then produces its OWN merged
        // roster and hands it to A via MergeAppUsersOnly (the new narrow non-admin "push") — after
        // that, A must see B too, without B ever running a full hierarchy export/import.
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);

            var uA = dbA.TouchOrCreateAppUser("revkin.i");
            dbA.SetAppUserRole(uA.Id!.Value, "administrator");
            dbB.TouchOrCreateAppUser("ivanov.p"); // naladchik on B, never pushed anywhere yet

            // B already pulls A's roster today (existing auto-pull behavior).
            dbB.MergeAppUsersOnly(ToExported(dbA.GetAppUsers()));
            Assert.NotNull(dbB.FindAppUserByLogin("revkin.i"));

            // Without this fix, A never receives anything back from B.
            var countsOnA = dbA.MergeAppUsersOnly(ToExported(dbB.GetAppUsers()));
            Assert.Equal(1, countsOnA.AppUsersAdded); // ivanov.p is new to A

            Assert.NotNull(dbA.FindAppUserByLogin("ivanov.p"));
            Assert.NotNull(dbA.FindAppUserByLogin("revkin.i"));
        }
        finally { Cleanup(pathA, pathB); }
    }

    [Fact]
    public void DeleteAppUser_RemovesLocalRosterEntry()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            var u = db.TouchOrCreateAppUser("revkin.i");

            db.DeleteAppUser(u.Id!.Value);

            Assert.Null(db.FindAppUserByLogin("revkin.i"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void DeleteAppUser_ThenReImportFromAnotherMachine_ReAddsAsNaladchik()
    {
        // Documents the known trade-off from Database.DeleteAppUser's doc comment: deletion is
        // local-only, so a machine that still has the login in its own roster will bring it back.
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);

            var uA = dbA.TouchOrCreateAppUser("revkin.i");
            dbA.SetAppUserRole(uA.Id!.Value, "administrator");
            dbB.ImportHierarchyData(dbA.ExportHierarchyData());
            var uB = dbB.FindAppUserByLogin("revkin.i")!;

            dbB.DeleteAppUser(uB.Id!.Value);
            Assert.Null(dbB.FindAppUserByLogin("revkin.i"));

            dbB.ImportHierarchyData(dbA.ExportHierarchyData());
            var reAdded = dbB.FindAppUserByLogin("revkin.i");
            Assert.NotNull(reAdded);
            Assert.Equal("administrator", reAdded!.Role);
        }
        finally { Cleanup(pathA, pathB); }
    }

    private static List<ExportedAppUser> ToExported(List<AppUser> users) =>
        users.Select(u => new ExportedAppUser
        {
            SyncId = u.SyncId, AdLogin = u.AdLogin, Role = u.Role,
            FirstLoginAt = u.FirstLoginAt, LastLoginAt = u.LastLoginAt, RoleUpdatedAt = u.RoleUpdatedAt,
        }).ToList();

    [Fact]
    public void Import_NeverDeletesAUserEvenIfMissingFromTheIncomingExport()
    {
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);

            dbB.TouchOrCreateAppUser("only.on.b");
            var counts = dbB.ImportHierarchyData(dbA.ExportHierarchyData()); // A has zero users

            Assert.Equal(0, counts.AppUsersAdded);
            Assert.Equal(0, counts.AppUsersUpdated);
            Assert.NotNull(dbB.FindAppUserByLogin("only.on.b")); // still there — roster is never pruned via sync
        }
        finally { Cleanup(pathA, pathB); }
    }
}
