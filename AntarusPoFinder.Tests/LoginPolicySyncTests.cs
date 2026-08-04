using System.IO;
using System.Text.Json.Nodes;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Infrastructure;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Версия 1.63: политика единого входа (Keycloak/OIDC + AD) синхронизируется между машинами
/// через общий конфиг — администратор задаёт её один раз, и она доезжает до всех. Здесь проверяются
/// обе стороны сделки: ключи ПОЛИТИКИ входа реально уезжают в снимок и приезжают на другую машину, а
/// машинно-локальные (кто здесь входил в прошлый раз, путь к лоадеру) — нет. И запасной вход:
/// в режиме oidc вход по доменной учётной записи остаётся доступным, чтобы недоступный Keycloak
/// никого не запирал.</summary>
public class LoginPolicySyncTests
{
    /// <summary>Ключ политики входа (способ входа, домен, адрес HTTP-проверки, сопоставление групп
    /// ролям, параметры Keycloak) уезжает в снимок и приезжает на другую машину. А машинно-локальный
    /// (ad_last_login, loader_exe_path) — не попадает в снимок вовсе и не перетирает значение соседа.</summary>
    [Fact]
    public void LoginPolicyKeysTravelBetweenMachines_ButMachineLocalOnesStayPut()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        // ── Администратор на машине A настраивает единый вход ──
        m.CfgA.SetAdAuthMode("oidc");
        m.CfgA.Set("ad_domain", "КорпДомен");
        m.CfgA.SetAdHttpUrl("https://corp.example/cloud");
        m.CfgA.Set("ad_group_administrator", "ПО Finder Админы");
        m.CfgA.Set("ad_group_programmer", "ПО Finder Программисты");
        m.CfgA.Set("ad_group_naladchik", "ПО Finder Наладчики");
        m.CfgA.SetOidcAuthority("https://sso.antarus.su/realms/antarus");
        m.CfgA.SetOidcClientId("po-finder");
        m.CfgA.SetOidcGroupsClaim("groups");

        // ── А это должно остаться на A и НЕ уехать: кто входил в прошлый раз и путь к лоадеру ──
        m.CfgA.SetAdLastLogin("ivanov.i");
        m.CfgA.Set("loader_exe_path", @"C:\Loader\loader.exe");

        ConfigSyncService.Export(m.SvcA, root, "profileA");

        // Снимок на диске: ключи политики в нём есть с нужными значениями, машинно-локальных — нет.
        var bytes = File.ReadAllBytes(ConfigSyncService.ConfigPathFor(root));
        var payload = JsonNode.Parse(ConfigFileCrypto.TryDecrypt(bytes)!)!.AsObject();

        Assert.Equal("oidc", (string?)payload["ad_auth_mode"]);
        Assert.Equal("КорпДомен", (string?)payload["ad_domain"]);
        Assert.Equal("https://corp.example/cloud", (string?)payload["ad_http_url"]);
        Assert.Equal("ПО Finder Админы", (string?)payload["ad_group_administrator"]);
        Assert.Equal("ПО Finder Программисты", (string?)payload["ad_group_programmer"]);
        Assert.Equal("ПО Finder Наладчики", (string?)payload["ad_group_naladchik"]);
        Assert.Equal("https://sso.antarus.su/realms/antarus", (string?)payload["oidc_authority"]);
        Assert.Equal("po-finder", (string?)payload["oidc_client_id"]);

        Assert.False(payload.ContainsKey("ad_last_login"), "ad_last_login — машинно-локальный, в снимок уезжать не должен");
        Assert.False(payload.ContainsKey("loader_exe_path"), "loader_exe_path — машинно-локальный, в снимок уезжать не должен");

        // ── Машина B забирает обновление ──
        var update = ConfigSyncService.CheckForUpdate(m.SvcB, out var err);
        Assert.True(err is null, err);
        Assert.NotNull(update); // изменение политики входа само по себе обязано считаться обновлением

        ConfigSyncService.Apply(m.SvcB, update!.ConfigPath, root);

        // Политика приехала на B.
        Assert.Equal("oidc", m.CfgB.AdAuthMode());
        Assert.Equal("КорпДомен", m.CfgB.Get("ad_domain"));
        Assert.Equal("https://corp.example/cloud", m.CfgB.AdHttpUrl());
        Assert.Equal("ПО Finder Админы", m.CfgB.Get("ad_group_administrator"));
        Assert.Equal("https://sso.antarus.su/realms/antarus", m.CfgB.OidcAuthority());
        Assert.Equal("po-finder", m.CfgB.OidcClientId());
        Assert.True(m.CfgB.OidcConfigured());

        // Машинно-локальные значения A на B не перетёрлись — у B они остались своими (пустыми).
        Assert.Equal("", m.CfgB.Get("ad_last_login"));
        Assert.Equal("", m.CfgB.Get("loader_exe_path"));
    }

    /// <summary>Совместимость: ключ политики входа больше НЕ в SkipSettingsKeys (уезжает), а
    /// машинно-локальные и транспорт обмена — остаются. Читает список отражением (см. ConfigSyncSkipKeys),
    /// чтобы падать явно, если политику случайно вернут в локальные.</summary>
    [Fact]
    public void SkipSettingsKeys_ExcludesLoginPolicy_ButKeepsMachineLocalAndTransport()
    {
        var skipped = ConfigSyncSkipKeys.Read();

        // Политика входа — синхронизируется (в списке НЕТ).
        Assert.DoesNotContain("ad_auth_mode", skipped);
        Assert.DoesNotContain("ad_domain", skipped);
        Assert.DoesNotContain("ad_http_url", skipped);
        Assert.DoesNotContain("ad_group_administrator", skipped);
        Assert.DoesNotContain("ad_group_programmer", skipped);
        Assert.DoesNotContain("ad_group_naladchik", skipped);
        Assert.DoesNotContain("oidc_authority", skipped);
        Assert.DoesNotContain("oidc_client_id", skipped);
        Assert.DoesNotContain("oidc_groups_claim", skipped);

        // А это остаётся строго локальным.
        Assert.Contains("ad_last_login", skipped);
        Assert.Contains("ad_require_login", skipped);
        Assert.Contains("loader_exe_path", skipped);
        Assert.Contains("sync_transport", skipped);
        Assert.Contains("server_url", skipped);
        // Пароли/хеши — никогда.
        Assert.Contains("admin_password", skipped);
        Assert.Contains("programmer_password", skipped);
    }

    /// <summary>Запасной вход: раз способ входа теперь прилетает всем разом, режим oidc не должен
    /// прятать вход по AD навсегда — иначе машина с недоступным Keycloak осталась бы с одним аварийным
    /// паролём. Проверяем инвариант окна входа через вынесенную в Core политику StartupLoginOptions
    /// (её же зовёт AdStartupLoginDialog.ApplyAuthMode) — без поднятия WPF.</summary>
    [Fact]
    public void OidcMode_KeepsAdLoginAvailableAsFallback()
    {
        // oidc с настроенными параметрами — корпоративный вход основной, но вход по AD доступен
        // (лишь свёрнут по умолчанию).
        Assert.True(StartupLoginOptions.ShowCorporateLogin("oidc", oidcConfigured: true));
        Assert.True(StartupLoginOptions.AdFallbackAvailable("oidc", oidcConfigured: true));
        Assert.True(StartupLoginOptions.AdFallbackCollapsedInitially("oidc", oidcConfigured: true));

        // oidc выбран, но не настроен — корпоративной панели нет, вход по AD показан сразу.
        Assert.False(StartupLoginOptions.ShowCorporateLogin("oidc", oidcConfigured: false));
        Assert.True(StartupLoginOptions.AdFallbackAvailable("oidc", oidcConfigured: false));
        Assert.False(StartupLoginOptions.AdFallbackCollapsedInitially("oidc", oidcConfigured: false));

        // Обычный режим AD — корпоративной панели нет, вход по AD основной и не свёрнут.
        Assert.False(StartupLoginOptions.ShowCorporateLogin("both", oidcConfigured: true));
        Assert.True(StartupLoginOptions.AdFallbackAvailable("both", oidcConfigured: true));
        Assert.False(StartupLoginOptions.AdFallbackCollapsedInitially("both", oidcConfigured: true));
    }
}
