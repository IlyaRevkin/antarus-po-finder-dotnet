using System;
using System.Text;
using System.Text.Json.Nodes;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Корпоративный вход (Keycloak / OpenID Connect). Сам поход в браузер тестами не
/// покрывается — он требует живого сервера входа; проверяется то, что от него зависит и что можно
/// сломать незаметно: разбор токена, соответствие «группы из токена → роль приложения» и то, что
/// новый способ входа не включается сам собой на существующих установках.</summary>
public class CorporateLoginTests
{
    private static ConfigService NewCfg(out Database db, out TempDb file)
    {
        file = new TempDb();
        db = new Database(file.Path);
        return new ConfigService(db);
    }

    private static string FakeJwt(string payloadJson)
    {
        static string B64(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return B64("""{"alg":"RS256"}""") + "." + B64(payloadJson) + ".подпись-здесь-не-проверяется";
    }

    [Fact]
    public void DecodeJwtPayload_ReadsClaims_AndSurvivesGarbage()
    {
        var payload = OidcIdentityProvider.DecodeJwtPayload(
            FakeJwt("""{"preferred_username":"ivanov.i","name":"Иванов И.","groups":["/Antarus/Админы"]}"""));

        Assert.NotNull(payload);
        Assert.Equal("ivanov.i", (string?)payload!["preferred_username"]);
        Assert.Equal("Иванов И.", (string?)payload["name"]);
        Assert.Equal("/Antarus/Админы", (string?)((JsonArray)payload["groups"]!)[0]);

        // Мусор вместо токена — null, а не исключение: вход обязан закончиться понятным отказом.
        Assert.Null(OidcIdentityProvider.DecodeJwtPayload("не-токен"));
        Assert.Null(OidcIdentityProvider.DecodeJwtPayload("a.b"));
    }

    [Fact]
    public void RoleFromGroups_MatchesConfiguredGroups_HighestPrivilegeFirst()
    {
        var cfg = NewCfg(out var db, out var file);
        using (db) using (file)
        {
            cfg.Set("ad_group_administrator", "ПО Finder Админы");
            cfg.Set("ad_group_programmer", "ПО Finder Программисты");
            cfg.Set("ad_group_naladchik", "ПО Finder Наладчики");

            // Keycloak отдаёт группы путём — сравниваем и последний сегмент.
            Assert.Equal("administrator",
                RoleFromGroups.Detect(cfg, new[] { "/Antarus/ПО Finder Админы" }));
            // В двух группах сразу — побеждает более привилегированная, как и у AD.
            Assert.Equal("administrator",
                RoleFromGroups.Detect(cfg, new[] { "ПО Finder Наладчики", "ПО Finder Админы" }));
            Assert.Equal("naladchik",
                RoleFromGroups.Detect(cfg, new[] { "по finder наладчики" }));   // регистр не важен
        }
    }

    [Fact]
    public void RoleFromGroups_NoMatch_ReturnsNull_SoRosterDecides()
    {
        var cfg = NewCfg(out var db, out var file);
        using (db) using (file)
        {
            cfg.Set("ad_group_administrator", "ПО Finder Админы");

            // Ничего не совпало / групп нет / группы не настроены — роль решает ростер приложения,
            // где новому человеку достаётся МИНИМАЛЬНАЯ роль. Молча выдавать привилегии нельзя.
            Assert.Null(RoleFromGroups.Detect(cfg, new[] { "Бухгалтерия" }));
            Assert.Null(RoleFromGroups.Detect(cfg, Array.Empty<string>()));
            Assert.Null(RoleFromGroups.Detect(cfg, null));
        }
    }

    [Fact]
    public void AuthMode_DefaultsUnchanged_AndOidcNeedsBothParameters()
    {
        var cfg = NewCfg(out var db, out var file);
        using (db) using (file)
        {
            // Существующая установка ничего не меняла — способ входа остался прежним, серверный
            // обмен выключен: новые возможности не включаются сами собой.
            Assert.Equal("both", cfg.AdAuthMode());
            Assert.Equal("fileshare", cfg.SyncTransport());
            Assert.False(cfg.OidcConfigured());

            cfg.SetAdAuthMode("oidc");
            Assert.Equal("oidc", cfg.AdAuthMode());

            cfg.SetOidcAuthority("https://sso.antarus.su/realms/antarus/");
            Assert.Equal("https://sso.antarus.su/realms/antarus", cfg.OidcAuthority());  // хвостовой слеш срезан
            Assert.False(cfg.OidcConfigured());          // без клиента входить нечем
            cfg.SetOidcClientId("po-finder");
            Assert.True(cfg.OidcConfigured());

            // Незнакомое значение не должно превращаться в «какой-нибудь» способ входа.
            cfg.SetAdAuthMode("выдумка");
            Assert.Equal("ldap", cfg.AdAuthMode());
        }
    }
}
