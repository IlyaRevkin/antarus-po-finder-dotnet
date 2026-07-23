using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using AntarusPoFinder.App;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Infrastructure;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Regression guard for the exact leak this fix (Фикс 6) exists to prevent: a shared config
/// export must never include any of ConfigSyncService.SkipSettingsKeys — most importantly
/// admin_password/programmer_password, but the same guarantee should hold for every other
/// per-machine-only key in that list (local disk paths, AD-gate timers, etc — see that field's own
/// doc comment). Reads SkipSettingsKeys via reflection instead of hand-copying the list here: the
/// point is that a FUTURE key added to settings but forgotten in SkipSettingsKeys fails this test
/// too, not just the two password keys that originally motivated it. ConfigSyncService itself is
/// out of this round's edit scope (owned by a parallel change), so this test only reads it.</summary>
public class ConfigExportSkipSettingsKeysTests
{
    private static HashSet<string> ReadSkipSettingsKeys()
    {
        var field = typeof(ConfigSyncService).GetField("SkipSettingsKeys", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field); // если поле переименуют/удалят, тест должен упасть явно, а не тихо стать no-op
        return (HashSet<string>)field!.GetValue(null)!;
    }

    [Fact]
    public void Export_NeverIncludesAnyKeyFromSkipSettingsKeys()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);
        var hier = new HierarchyService(db);
        using var root = new TempRoot();
        cfg.SetRootPath(root.Path);
        var services = new AppServices(db, cfg, hier) { CurrentAdLogin = "test.profile" };

        // Populate the two password keys with obviously-identifiable values, so a leak here would
        // also be easy to spot by eye in a failing assert, not just via the general key sweep below.
        cfg.SetAdminPassword("SuperSecretAdminPass123");
        cfg.SetProgrammerPassword("SuperSecretProgrammerPass456");

        ConfigSyncService.Export(services, root.Path, "test.profile");

        var configPath = ConfigSyncService.ConfigPathFor(root.Path);
        Assert.True(File.Exists(configPath));

        var bytes = File.ReadAllBytes(configPath);
        var json = ConfigFileCrypto.TryDecrypt(bytes);
        Assert.NotNull(json);

        var payload = JsonNode.Parse(json!)!.AsObject();
        var skipKeys = ReadSkipSettingsKeys();

        foreach (var key in skipKeys)
            Assert.False(payload.ContainsKey(key), $"Export() payload must never contain skip-listed key '{key}'");
    }

    [Fact]
    public void Export_SpecificallyDoesNotLeakPasswordHashes()
    {
        // Narrower, more literal regression test for the historical incident this fix references
        // (admin/programmer passwords leaking onto the shared drive) — kept alongside the general
        // sweep above so that specific failure mode has its own clearly-named test.
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);
        var hier = new HierarchyService(db);
        using var root = new TempRoot();
        cfg.SetRootPath(root.Path);
        var services = new AppServices(db, cfg, hier) { CurrentAdLogin = "test.profile" };

        cfg.SetAdminPassword("SuperSecretAdminPass123");
        cfg.SetProgrammerPassword("SuperSecretProgrammerPass456");

        ConfigSyncService.Export(services, root.Path, "test.profile");

        var bytes = File.ReadAllBytes(ConfigSyncService.ConfigPathFor(root.Path));
        var json = ConfigFileCrypto.TryDecrypt(bytes)!;

        Assert.DoesNotContain("admin_password", json);
        Assert.DoesNotContain("programmer_password", json);
        Assert.DoesNotContain("SuperSecretAdminPass123", json);
        Assert.DoesNotContain("SuperSecretProgrammerPass456", json);
    }
}
