using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Infrastructure;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Covers the password-hashing migration in Database.cs (SeedDefaultAdminPasswordHash /
/// MigratePlaintextPasswordsToHashesOnce) — the part of the security fix that has to work correctly
/// against real SQLite databases, not just PasswordHasher in isolation (see PasswordHasherTests) or
/// ConfigService's Verify*/Set* wiring in isolation.</summary>
public class PasswordMigrationTests
{
    [Fact]
    public void FreshDatabase_SeedsHashedDefaultAdminPassword_NotPlaintext()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var stored = db.GetSetting("admin_password");

        Assert.True(PasswordHasher.IsHashed(stored));
        Assert.NotEqual("12345", stored);
    }

    [Fact]
    public void FreshDatabase_ConfigService_VerifyAdminPassword_AcceptsTheDocumentedDefault()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        // "12345" — тот же дефолт, что наладчики использовали всегда (см. ConfigService.Defaults),
        // теперь просто хранится хешем, а не открытым текстом.
        Assert.True(cfg.VerifyAdminPassword("12345"));
        Assert.False(cfg.VerifyAdminPassword("wrong"));
    }

    [Fact]
    public void FreshDatabase_ProgrammerPasswordStaysEmpty_MeaningNotRequired()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        Assert.Equal("", cfg.ProgrammerPassword());
        // Пустой пароль программиста = проверка не требуется — see ConfigService.VerifyProgrammerPassword.
        Assert.True(cfg.VerifyProgrammerPassword("anything the operator happens to type"));
        Assert.True(cfg.VerifyProgrammerPassword(""));
    }

    [Fact]
    public void ExistingDatabase_PlaintextAdminPassword_IsRehashedOnNextOpen()
    {
        using var dbFile = new TempDb();

        // Simulates a database that already existed before this fix, with a custom admin password
        // someone had saved through Настройки while it was still stored as plaintext. Also resets
        // the one-time migration flag that the FIRST `new Database(...)` above already consumed
        // (it seeds+migrates on every construction) — a genuinely pre-fix database never had that
        // flag set at all, so clearing it back to "" is what actually reproduces that starting state
        // for the second `new Database(...)` below to migrate from.
        using (var db = new Database(dbFile.Path))
        {
            db.SetSetting("admin_password", "MyCustomPlaintextPassword");
            db.SetSetting("migration_password_hash_done", "");
        }

        // Re-opening runs Migrate() again — this is where MigratePlaintextPasswordsToHashesOnce
        // must catch the plaintext value and rehash it in place.
        using var reopened = new Database(dbFile.Path);
        var stored = reopened.GetSetting("admin_password");

        Assert.True(PasswordHasher.IsHashed(stored));
        Assert.True(PasswordHasher.Verify("MyCustomPlaintextPassword", stored));

        var cfg = new ConfigService(reopened);
        Assert.True(cfg.VerifyAdminPassword("MyCustomPlaintextPassword"));
    }

    [Fact]
    public void ExistingDatabase_PlaintextProgrammerPassword_IsRehashedOnNextOpen()
    {
        using var dbFile = new TempDb();

        using (var db = new Database(dbFile.Path))
        {
            db.SetSetting("programmer_password", "OldPlainProgrammerPass");
            db.SetSetting("migration_password_hash_done", ""); // see sibling admin-password test for why
        }

        using var reopened = new Database(dbFile.Path);
        var stored = reopened.GetSetting("programmer_password");

        Assert.True(PasswordHasher.IsHashed(stored));
        var cfg = new ConfigService(reopened);
        Assert.True(cfg.VerifyProgrammerPassword("OldPlainProgrammerPass"));
        Assert.False(cfg.VerifyProgrammerPassword("wrong"));
    }

    [Fact]
    public void ExistingDatabase_EmptyProgrammerPassword_StaysEmptyNotHashed()
    {
        using var dbFile = new TempDb();

        using (var db = new Database(dbFile.Path))
            db.SetSetting("programmer_password", ""); // явно пусто — "пароль не задан"

        using var reopened = new Database(dbFile.Path);

        // Пустая строка не должна превратиться в хеш пустой строки — это разные состояния (см.
        // ConfigService.SetProgrammerPassword doc: непустой хеш всегда означает "пароль задан").
        Assert.Equal("", reopened.GetSetting("programmer_password"));
    }

    [Fact]
    public void ExistingDatabase_AlreadyHashedPassword_IsNotRehashedAgain()
    {
        using var dbFile = new TempDb();
        string originalHash;

        using (var db = new Database(dbFile.Path))
        {
            var cfg = new ConfigService(db);
            cfg.SetAdminPassword("SomePassword");
            originalHash = db.GetSetting("admin_password");
        }

        // Re-opening must leave an already-hashed value untouched (byte-for-byte) — re-hashing it
        // would still verify correctly (Hash is idempotent-safe), but this pins down that the
        // migration recognizes its own format and genuinely skips it, not just "happens to work".
        using var reopened = new Database(dbFile.Path);
        Assert.Equal(originalHash, reopened.GetSetting("admin_password"));
    }

    [Fact]
    public void SetAdminPassword_ThenVerify_RoundTrips()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        cfg.SetAdminPassword("NewAdminPass123");

        Assert.True(PasswordHasher.IsHashed(db.GetSetting("admin_password")));
        Assert.True(cfg.VerifyAdminPassword("NewAdminPass123"));
        Assert.False(cfg.VerifyAdminPassword("12345")); // старый дефолт больше не подходит
    }

    [Fact]
    public void SetProgrammerPassword_Empty_ClearsRequirement()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        cfg.SetProgrammerPassword("SomePassword");
        Assert.True(cfg.VerifyProgrammerPassword("SomePassword"));

        cfg.SetProgrammerPassword(""); // явная очистка — снова "не задан"
        Assert.Equal("", db.GetSetting("programmer_password"));
        Assert.True(cfg.VerifyProgrammerPassword("literally anything"));
    }
}
