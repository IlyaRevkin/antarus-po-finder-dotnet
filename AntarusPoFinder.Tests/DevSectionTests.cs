using System.Linq;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Domain;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>СЕКЦИЯ «ДЛЯ ПРОГРАММИСТА» И РАЗДЕЛЁННЫЕ ТИКЕТЫ (бета 2.0).
///
/// Просьбы Ильи от 15.10.2026: «все же раздели вкладку тикетов программиста и тикетов по
/// приложению» и «вкладку тикетов для программиста сделай отображаемой слева как у наладчика есть
/// параметры ПЧ упп наклейки и та».
///
/// До этого баги прошивок были второй пилюлей внутри страницы «Тикеты», которая сама лежала в
/// «Дополнительно»: два клика и не на виду у того, кому эти баги адресованы.
///
/// Главное, что проверяется здесь, — не красота меню, а два правила, нарушение которых дорого:
/// страница обязана быть доступна тем ролям, которым её показывают, и ничего из этой переделки не
/// должно протечь в СТАБИЛЬНУЮ программу: обе собираются из одного дерева.</summary>
public class DevSectionTests
{
    [Fact]
    public void Баги_прошивок_это_отдельный_пункт_меню_в_секции_программиста()
    {
        var item = RolesConfig.NavItems.Single(n => n.PageId == "fwbugs");

        Assert.Equal(NavSection.Dev, item.Section);
        Assert.Equal("Баги прошивок", item.Label);
    }

    /// <summary>Пункт меню без права на страницу — это кнопка, которая ничего не открывает. Правим
    /// то же, что ловит проверка «Чистки диска» в SidebarSetupSectionTests, только с другой
    /// стороны.</summary>
    [Fact]
    public void Страница_доступна_тем_ролям_которым_её_показывают()
    {
        Assert.Contains("fwbugs", RolesConfig.RoleAccess["programmer"]);
        Assert.Contains("fwbugs", RolesConfig.RoleAccess["administrator"]);
    }

    /// <summary>Наладчику отдельный пункт не нужен: баг прошивки он заводит жучком на карточке
    /// выдачи, а смотрит на общей странице «Тикеты». Лишний пункт в его меню — это ещё одна строка,
    /// которую он будет обходить взглядом каждый день.</summary>
    [Fact]
    public void Наладчику_отдельный_пункт_не_показывается()
    {
        Assert.DoesNotContain("fwbugs", RolesConfig.RoleAccess["naladchik"]);
    }

    /// <summary>Тикеты по программе остались там же, где были: эта переделка РАЗДЕЛЯЕТ, а не
    /// переносит. Человек, который шёл жаловаться на программу, должен найти её на прежнем месте.</summary>
    [Fact]
    public void Тикеты_по_программе_остались_в_дополнительном()
    {
        var tickets = RolesConfig.NavItems.Single(n => n.PageId == "tickets");

        Assert.Equal(NavSection.More, tickets.Section);
        foreach (var (role, pages) in RolesConfig.RoleAccess)
            Assert.True(pages.Contains("tickets"), $"роль «{role}» потеряла страницу тикетов");
    }

    /// <summary>⚠️ Самое важное здесь. Стабильная программа и бета собираются из ОДНОГО дерева, и
    /// без отсечки ближайший обычный релиз принёс бы всем недоделанную перестройку меню.
    ///
    /// Проверяется сам предохранитель в коде, а не его следствие: поднять MainWindowViewModel в
    /// тесте нельзя (ему нужны БД, конфиг и окно), а убрать одну строку из конструктора — можно и
    /// незаметно. Тест читает исходник ровно по той причине, по которой существует: он должен
    /// покраснеть, если отсечку выкинут.</summary>
    [Fact]
    public void Секция_программиста_не_попадает_в_стабильную_сборку()
    {
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "..", "..", "..", "..",
            "AntarusPoFinder.App", "ViewModels", "MainWindowViewModel.cs"));
        var code = System.IO.File.ReadAllText(path);

        Assert.Contains("NavSection.Dev && !AppIdentity.IsBeta", code);
    }
}
