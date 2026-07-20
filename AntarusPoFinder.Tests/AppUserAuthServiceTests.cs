using System.IO;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Fake IAdCredentialValidator — the real one (LdapAdCredentialValidator, in
/// AntarusPoFinder.App) needs a reachable AD domain, which isn't available in this environment (or
/// CI). Lets AppUserAuthService.Login's actual decision logic (roster lookup/create, role handed
/// back) be exercised without any AD/network dependency — see class doc on
/// AntarusPoFinder.Core.Services.IAdCredentialValidator for why this is a first-class seam.</summary>
public class FakeAdCredentialValidator : IAdCredentialValidator
{
    public bool ShouldSucceed { get; set; } = true;
    public string? FailureError { get; set; } = "Неверный логин или пароль.";

    public bool Validate(string domain, string login, string password, out string? error)
    {
        error = ShouldSucceed ? null : FailureError;
        return ShouldSucceed;
    }
}

/// <summary>Covers Часть 2 — вход по собственному ростеру приложения (AppUserAuthService.Login),
/// ортогональному к AD-группам (WindowsGroupAuth.DetectRoleForUser, only testable live/in App
/// project — see RoleSwitchDialog for how the two combine).</summary>
public class AppUserAuthServiceTests
{
    private static string NewTempDb() => Path.Combine(Path.GetTempPath(), $"antarus_appuser_test_{System.Guid.NewGuid():N}.db");

    private static void Cleanup(params string[] dbPaths)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var db in dbPaths)
            foreach (var f in new[] { db, db + "-wal", db + "-shm" })
                if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void Login_UnknownLogin_CreatesRosterEntry_DefaultsToNaladchik_LetsThemStraightIn()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            var validator = new FakeAdCredentialValidator();

            var result = AppUserAuthService.Login(db, validator, "Elita", "revkin.i", "pw");

            Assert.True(result.Success);
            Assert.True(result.IsNewUser);
            Assert.Equal("naladchik", result.Role);
            Assert.NotNull(result.User);
            Assert.Equal("revkin.i", result.User!.AdLogin);
            Assert.False(string.IsNullOrEmpty(result.User.FirstLoginAt));
            Assert.Equal(result.User.FirstLoginAt, result.User.LastLoginAt);

            var roster = db.GetAppUsers();
            Assert.Single(roster);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Login_KnownLogin_UsesStoredRole_NeverResetsToDefault()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            var validator = new FakeAdCredentialValidator();

            var first = AppUserAuthService.Login(db, validator, "Elita", "revkin.i", "pw");
            db.SetAppUserRole(first.User!.Id!.Value, "administrator"); // admin promotes them locally

            var second = AppUserAuthService.Login(db, validator, "Elita", "revkin.i", "pw");

            Assert.True(second.Success);
            Assert.False(second.IsNewUser);
            Assert.Equal("administrator", second.Role); // stored role wins, not re-defaulted to naladchik

            Assert.Single(db.GetAppUsers()); // still one roster row, not a duplicate
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Login_WrongPassword_NeverCreatesRosterEntry()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            var validator = new FakeAdCredentialValidator { ShouldSucceed = false, FailureError = "Неверный логин или пароль." };

            var result = AppUserAuthService.Login(db, validator, "Elita", "revkin.i", "wrongpw");

            Assert.False(result.Success);
            Assert.Equal("Неверный логин или пароль.", result.Error);
            Assert.Null(result.Role);
            Assert.Empty(db.GetAppUsers());
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData("revkin.i@Elita")]
    [InlineData("Elita\\revkin.i")]
    [InlineData("REVKIN.I@ELITA")]
    [InlineData("revkin.i")]
    public void Login_AcceptsAllExistingLoginFormats_AllResolveToTheSameRosterRow(string typedLogin)
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            var validator = new FakeAdCredentialValidator();

            AppUserAuthService.Login(db, validator, "Elita", "revkin.i", "pw"); // baseline, bare form
            var second = AppUserAuthService.Login(db, validator, "Elita", typedLogin, "pw");

            Assert.False(second.IsNewUser, $"'{typedLogin}' should have matched the existing 'revkin.i' roster row");
            Assert.Single(db.GetAppUsers());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Login_MissingDomainOrLogin_FailsWithoutCallingValidator()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            var validator = new FakeAdCredentialValidator();

            var result = AppUserAuthService.Login(db, validator, "", "revkin.i", "pw");
            Assert.False(result.Success);
            Assert.Empty(db.GetAppUsers());
        }
        finally { Cleanup(path); }
    }
}
