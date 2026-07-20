using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
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
