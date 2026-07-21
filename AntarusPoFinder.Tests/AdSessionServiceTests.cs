using System;
using System.IO;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Covers the mandatory-AD-login-at-startup cache (AdSessionService/Database.
/// GetAdLoginSession/SaveAdLoginSession) — the gate itself (AdStartupLoginDialog) lives in
/// AntarusPoFinder.App and needs a real Window/AD, so it's only exercised live; the pure expiry
/// math and persistence underneath it is fully testable here, same seam as AppUserAuthServiceTests.</summary>
public class AdSessionServiceTests
{
    private static string NewTempDb() => Path.Combine(Path.GetTempPath(), $"antarus_adsession_test_{Guid.NewGuid():N}.db");

    private static void Cleanup(params string[] dbPaths)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var db in dbPaths)
            foreach (var f in new[] { db, db + "-wal", db + "-shm" })
                if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void NoSessionRecorded_IsNotValid()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            Assert.False(AdSessionService.IsValid(db, "revkin.i", DateTime.Now));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void DefaultMode_ValidUntilDefaultDaysElapsed_ThenInvalid()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            var now = new DateTime(2026, 1, 1, 9, 0, 0);
            AdSessionService.RecordLogin(db, "revkin.i", AdSessionMode.Default, customDays: 0, defaultDays: 14, now: now);

            Assert.True(AdSessionService.IsValid(db, "revkin.i", now.AddDays(13)));
            Assert.True(AdSessionService.IsValid(db, "revkin.i", now.AddDays(14)));
            Assert.False(AdSessionService.IsValid(db, "revkin.i", now.AddDays(15)));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void CustomMode_UsesCustomDays_IgnoresAdministratorDefault()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            var now = new DateTime(2026, 1, 1);
            // A programmer on their own laptop picks 30 days even though the admin default is 14.
            AdSessionService.RecordLogin(db, "prog.p", AdSessionMode.Custom, customDays: 30, defaultDays: 14, now: now);

            Assert.True(AdSessionService.IsValid(db, "prog.p", now.AddDays(29)));
            Assert.False(AdSessionService.IsValid(db, "prog.p", now.AddDays(31)));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void AlwaysMode_NeverExpires()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            var now = new DateTime(2026, 1, 1);
            AdSessionService.RecordLogin(db, "home.laptop.dev", AdSessionMode.Always, customDays: 0, defaultDays: 14, now: now);

            Assert.True(AdSessionService.IsValid(db, "home.laptop.dev", now.AddYears(5)));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void LoginIsCaseInsensitive_MatchesRosterNormalization()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            var now = new DateTime(2026, 1, 1);
            AdSessionService.RecordLogin(db, "revkin.i", AdSessionMode.Always, customDays: 0, defaultDays: 14, now: now);

            Assert.True(AdSessionService.IsValid(db, "REVKIN.I", now));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void RecordLogin_Twice_OverwritesPreviousChoice_DoesNotCreateSecondRow()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            var now = new DateTime(2026, 1, 1);
            AdSessionService.RecordLogin(db, "revkin.i", AdSessionMode.Always, customDays: 0, defaultDays: 14, now: now);
            AdSessionService.RecordLogin(db, "revkin.i", AdSessionMode.Default, customDays: 0, defaultDays: 14, now: now);

            // Switched away from "always" back to a 14-day window — it should actually expire now.
            Assert.False(AdSessionService.IsValid(db, "revkin.i", now.AddDays(15)));

            var session = db.GetAdLoginSession("revkin.i");
            Assert.NotNull(session);
            Assert.Equal(AdSessionMode.Default, session!.Mode);
        }
        finally { Cleanup(path); }
    }
}
