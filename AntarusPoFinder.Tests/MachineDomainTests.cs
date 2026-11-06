using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Определение домена компьютера: разбор того, что отдала Windows. Сам поход в AD
/// (App/Services/MachineDomainProbe) тестом не проверить — нужен живой контроллер домена, — поэтому
/// все решения вынесены сюда и проверяются на сырых значениях.</summary>
public class MachineDomainTests
{
    [Fact]
    public void ComputerDomain_wins_and_is_reported_as_source()
    {
        var info = MachineDomain.FromProbe("ELITA", "WS-07", "elita.local", @"\\DC01", "elita.local", null);

        Assert.True(info.Joined);
        Assert.Equal("ELITA", info.NetBios);
        Assert.Equal("elita.local", info.Dns);
        Assert.Equal("elita.local", info.Best);
        Assert.Equal("DC01", info.LogonServer);
        Assert.Contains("Active Directory", info.Source);
        // Короткое и полное имя — одно и то же место, поэтому в тексте оно названо один раз,
        // полным именем: «ELITA / elita.local» выглядело бы как два разных домена.
        Assert.Contains("в домене elita.local", info.Describe());
    }

    /// <summary>Главная проверка: вне домена Windows кладёт в USERDOMAIN имя самого компьютера.
    /// Без этого приложение объявило бы доменом имя машины и пошло бы искать в нём группы.</summary>
    [Fact]
    public void Local_account_is_not_a_domain()
    {
        var info = MachineDomain.FromProbe("WS-07", "WS-07", "", "", "", null);

        Assert.False(info.Joined);
        Assert.Equal("", info.Best);
        Assert.Contains("не в домене", info.Describe());
    }

    /// <summary>Контроллер домена недоступен (ноутбук вне сети): полного имени нет, но сеанс всё
    /// равно доменный — короткое имя известно, и работать с ним можно.</summary>
    [Fact]
    public void Session_domain_without_network_still_counts()
    {
        var info = MachineDomain.FromProbe("ELITA", "WS-07", "", @"\\DC01", "", "сервер не найден");

        Assert.True(info.Joined);
        Assert.Equal("ELITA", info.Best);
        Assert.Contains("сервер не найден", info.Describe());
    }

    [Fact]
    public void Dns_variable_is_used_when_ad_is_unreachable()
    {
        var info = MachineDomain.FromProbe("ELITA", "WS-07", "elita.local", "", "", null);

        Assert.True(info.Joined);
        Assert.Equal("elita.local", info.Dns);
        Assert.Contains("USERDNSDOMAIN", info.Source);
    }

    /// <summary>Расхождение «компьютер в одном домене, в настройках другой» обязано быть названо
    /// вслух: молча оно выглядит как «сверка опять никого не нашла».</summary>
    [Fact]
    public void Hint_says_when_configured_domain_differs()
    {
        var info = MachineDomain.FromProbe("ELITA", "WS-07", "elita.local", "", "elita.local", null);

        Assert.Contains("другой домен", MachineDomain.Hint(info, "antarus.local"));
        Assert.DoesNotContain("другой домен", MachineDomain.Hint(info, "ELITA"));
        Assert.DoesNotContain("другой домен", MachineDomain.Hint(info, ""));
    }

    [Fact]
    public void Hint_on_a_machine_outside_the_domain_still_mentions_the_setting()
    {
        var info = MachineDomain.FromProbe("WS-07", "WS-07", "", "", "", null);
        var hint = MachineDomain.Hint(info, "Elita");

        Assert.Contains("не в домене", hint);
        Assert.Contains("Elita", hint);
    }

    [Theory]
    [InlineData("ELITA", "elita.local", true)]
    [InlineData("elita.local", "ELITA.", true)]
    [InlineData(@"\\ELITA", "elita", true)]
    [InlineData("ELITA", "antarus.local", false)]
    [InlineData("", "elita", false)]
    public void Same_matches_short_and_full_names(string a, string b, bool expected) =>
        Assert.Equal(expected, MachineDomain.Same(a, b));
}
