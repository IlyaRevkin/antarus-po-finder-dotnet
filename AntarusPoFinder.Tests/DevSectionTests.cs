using System.Linq;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Domain;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>СЕКЦИЯ «ДЛЯ ПРОГРАММИСТА» И РАЗДЕЛЁННЫЕ ТИКЕТЫ (переделка 2.x, выпущена в 2.2.0.0).
///
/// Просьбы Ильи от 15.10.2026: «все же раздели вкладку тикетов программиста и тикетов по
/// приложению» и «вкладку тикетов для программиста сделай отображаемой слева как у наладчика есть
/// параметры ПЧ упп наклейки и та».
///
/// До этого баги прошивок были второй пилюлей внутри страницы «Тикеты», которая сама лежала в
/// «Дополнительно»: два клика и не на виду у того, кому эти баги адресованы.
///
/// Главное, что проверяется здесь, — не красота меню, а правило, нарушение которого дорого:
/// страница обязана быть доступна тем ролям, которым её показывают. До 2.2 здесь же стоял
/// предохранитель «ничего из переделки не протекло в стабильную сборку»; переделка выпущена,
/// раскладка теперь одна, и вместо него проверяется единственное оставшееся отличие — «Мобильный
/// доступ» (см. Мобильный_доступ_остаётся_бета_пунктом).</summary>
public class DevSectionTests
{
    [Fact]
    public void Баги_прошивок_это_отдельный_пункт_меню_в_секции_программиста()
    {
        // Явно по раскладке БЕТЫ, а не по «текущей»: так тест говорит одно и то же независимо от
        // того, в какой конфигурации собран сам проект тестов.
        var item = RolesConfig.NavItemsFor(beta: true).Single(n => n.PageId == "fwbugs");

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
    /// <summary>Тикеты и хранилище — СЕРВИСНОЕ, и в меню их нет вовсе: «тикеты это сервисное
    /// это убрать надо в настройки», «хранилище не должно быть у программиста». Страницы живут
    /// вкладками настроек, права при этом не тронуты — иначе вкладка вела бы в никуда.</summary>
    [Fact]
    public void Сервисные_страницы_убраны_из_меню_но_доступ_остался()
    {
        var beta = RolesConfig.NavItemsFor(beta: true);

        Assert.DoesNotContain(beta, n => n.PageId == "tickets");
        Assert.DoesNotContain(beta, n => n.PageId == "hosting");

        foreach (var (role, pages) in RolesConfig.RoleAccess)
            Assert.True(pages.Contains("tickets"), $"роль «{role}» потеряла страницу тикетов");
        Assert.Contains("hosting", RolesConfig.RoleAccess["programmer"]);
    }

    /// <summary>Раскладка меню ОДНА на обе сборки — переделка 2.x выпущена в 2.2.0.0, и прежней
    /// «стабильной» раскладки (тикеты в «Дополнительно», загрузка ПО в общем списке) больше нет
    /// нигде. Проверяется явно: случайно отрастить вторую раскладку легко, а разъехавшееся меню
    /// между рабочей версией и бетой — это вопрос «а куда делся пункт» на ровном месте.</summary>
    [Fact]
    public void Раскладка_меню_одна_и_та_же_в_обеих_сборках()
    {
        var stable = RolesConfig.NavItemsFor(beta: false);
        var beta = RolesConfig.NavItemsFor(beta: true);

        Assert.Equal(NavSection.Dev, beta.Single(n => n.PageId == "fwbugs").Section);
        Assert.Equal(NavSection.Dev, stable.Single(n => n.PageId == "fwbugs").Section);
        Assert.DoesNotContain(stable, n => n.Section == NavSection.More);

        // Всё, кроме «Мобильного доступа», совпадает до последнего пункта и до порядка.
        Assert.Equal(beta.Where(n => n.PageId != "mobile"), stable);
    }

    /// <summary>⚠️ ЕДИНСТВЕННОЕ, ЧТО ОСТАЛОСЬ ОТЛИЧАТЬСЯ. «Мобильный доступ» поднимает на машине
    /// слушателя локальной сети, и живьём на объекте это не проверено (docs/mobile-access-plan.md).
    /// Пункт меню — ЕДИНСТВЕННЫЙ вход на страницу (MainWindowViewModel разбирает "mobile" только по
    /// переходу из меню), поэтому его отсутствие в рабочей раскладке и есть весь предохранитель.
    /// Права в RoleAccess при этом оставлены: уберёшь их — и в бете страница перестанет
    /// открываться.</summary>
    [Fact]
    public void Мобильный_доступ_остаётся_бета_пунктом()
    {
        Assert.Contains(RolesConfig.NavItemsFor(beta: true), n => n.PageId == "mobile");
        Assert.DoesNotContain(RolesConfig.NavItemsFor(beta: false), n => n.PageId == "mobile");

        Assert.Contains("mobile", RolesConfig.RoleAccess["naladchik"]);
        Assert.Contains("mobile", RolesConfig.RoleAccess["administrator"]);
    }
}
