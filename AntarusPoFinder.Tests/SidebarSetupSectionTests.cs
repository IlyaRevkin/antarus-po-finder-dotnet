using System.Linq;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Секция бокового меню «ДЛЯ НАЛАДЧИКА».
///
/// Просьба была такая: «давай сделаем доп меню какое-нибудь которое типа для наладчика но не часто
/// используется, там будет параметры ПЧ/УПП, наклейки, паспорта как раз и модерацию мб туда
/// вынести», и повторно: «я вроде писала, что хочу разделить вкладку наладчика, где будет ещё
/// параметры ПЧ/УПП и т.п.». До этого пункты были раскиданы: «Параметры ПЧ/УПП» и «Модерация
/// прошивок» стояли в основном списке наравне с Поиском, а «Паспорта шкафов» и «Наклейки» лежали в
/// «ДОПОЛНИТЕЛЬНО» вместе с Настройками и Тикетами — про это он и сказал, что «наклейки и паспорта в
/// том блоке не в тему».
///
/// Здесь проверяется РАСКЛАДКА (кто в каком блоке) и то, что переезд ничего не поменял в правах:
/// доступ ролей задаётся RolesConfig.RoleAccess и от блока не зависит вовсе.</summary>
public class SidebarSetupSectionTests
{
    private static NavSection SectionOf(string pageId) =>
        RolesConfig.NavItems.Single(n => n.PageId == pageId).Section;

    /// <summary>Ровно те четыре пункта, которые названы (наклейки и «Сформировать паспорт» — не
    /// страницы, а окна, их кнопки живут прямо в разметке сайдбара).</summary>
    [Fact]
    public void TheSetupSectionHoldsExactlyTheItemsAskedFor()
    {
        Assert.Equal(NavSection.Setup, SectionOf("params"));
        Assert.Equal(NavSection.Setup, SectionOf("newversions"));
    }

    /// <summary>СЕКЦИИ «ДОПОЛНИТЕЛЬНО» БОЛЬШЕ НЕТ (выпущено в 2.2.0.0). Жили в ней «Хранилище» и
    /// «Тикеты», и обе — сервисное, а не работа: «тикеты это сервисное это убрать надо в
    /// настройки», «хранилище не должно быть у программиста». Теперь это вкладки настроек
    /// (SettingsView), а «Сетевые диски» ушли отсюда ещё раньше — на них переходят щелчком по
    /// состоянию диска в нижней строке.
    ///
    /// Проверяется ПУСТОТА секции, а не её отсутствие в перечислении: NavSection.More из кода не
    /// выброшена (на неё завязан ShowInCompactList), и вернуть туда пункт — одна строка, которую
    /// надо заметить. Права ролей при этом обязаны остаться: страницы никуда не делись.</summary>
    [Fact]
    public void TheOldMoreSection_IsEmptyNow()
    {
        Assert.Empty(RolesConfig.NavItems.Where(n => n.Section == NavSection.More));
        Assert.DoesNotContain(RolesConfig.NavItems, n => n.PageId == "hosting");
        Assert.DoesNotContain(RolesConfig.NavItems, n => n.PageId == "tickets");

        foreach (var (role, pages) in RolesConfig.RoleAccess)
            Assert.True(pages.Contains("tickets"), $"роль «{role}» потеряла страницу тикетов");
        Assert.Contains("hosting", RolesConfig.RoleAccess["programmer"]);
    }

    /// <summary>Пункта «Сетевые диски» в меню больше нет, но САМА СТРАНИЦА доступна — её открывают
    /// щелчком по состоянию диска и по значку синхронизации в нижней строке (ShowSyncDetailsCommand
    /// → Navigate("network")), и ссылкой из Настроек.
    ///
    /// Права при этом обязаны остаться: убрать страницу из RoleAccess значило бы, что нижняя строка
    /// ведёт в никуда. Это ровно обратный случай «Чистки диска» ниже — там страницы не стало вовсе,
    /// здесь не стало только пункта меню.</summary>
    [Fact]
    public void NetworkPage_LeftTheMenuButStaysReachable()
    {
        Assert.DoesNotContain(RolesConfig.NavItems, n => n.PageId == "network");
        foreach (var (role, pages) in RolesConfig.RoleAccess)
            Assert.True(pages.Contains("network"), $"роль «{role}» потеряла доступ к сетевым дискам");
    }

    /// <summary>«Чистка диска» больше не пункт меню: она переехала во вкладку Настроек (просьба
    /// от 12.08.2026). Пункта не должно остаться ни в списке меню, ни в правах ролей — иначе роль
    /// «видела» бы страницу, которую построить уже некому: фабрики страниц "cleanup" в
    /// MainWindowViewModel тоже нет. Что вкладка осталась администраторской, проверяется на настоящем
    /// контроле — ViewsRenderTests.DiskCleanupTab_IsAdministratorOnly.</summary>
    [Fact]
    public void DiskCleanup_IsNoLongerASidebarPage()
    {
        Assert.DoesNotContain(RolesConfig.NavItems, n => n.PageId == "cleanup");
        foreach (var (role, pages) in RolesConfig.RoleAccess)
            Assert.False(pages.Contains("cleanup"), $"роль «{role}» всё ещё видит страницу cleanup");
    }

    /// <summary>Наверху, в один клик и без заголовка — ПОИСК, и только он: ради него программу и
    /// открывают. «Осмотр» и «Загрузка ПО» ушли отсюда в свои секции (выпущено в 2.2.0.0) — «для
    /// программиста перенеси загрузку прошивок а для наладчика вкладку осмотр»: это их работа, а
    /// не общий список. Состав фиксируется целиком, чтобы пополнение было осознанным.</summary>
    [Fact]
    public void TheEverydayPages_StayInTheMainList()
    {
        Assert.Equal(new[] { "search" },
            RolesConfig.NavItems.Where(n => n.Section == NavSection.Main).Select(n => n.PageId).ToArray());

        Assert.Equal(NavSection.Setup, SectionOf("inspection"));
        Assert.Equal(NavSection.Dev, SectionOf("upload"));
    }

    /// <summary>Переезд — это ТОЛЬКО про место кнопки: кто какую страницу видит, по-прежнему решает
    /// RolesConfig.RoleAccess. Модерация прошивок программисту не положена — и в новой секции она у
    /// него тоже не появляется.</summary>
    [Fact]
    public void MovingItemsAround_ChangedNobodysAccess()
    {
        var items = RolesConfig.NavItems.Select(n => new NavItem(n.PageId, n.Label, n.Section)).ToList();
        var allowed = RolesConfig.RoleAccess["programmer"];
        foreach (var item in items) item.IsVisible = allowed.Contains(item.PageId);

        Assert.Contains(items, i => i.PageId == "params" && i.ShowInSetupList);
        // Программисту модерация не положена — пункта в секции нет, а не «есть, но серый».
        Assert.DoesNotContain(items, i => i.PageId == "newversions" && i.ShowInSetupList);
        Assert.DoesNotContain("newversions", RolesConfig.RoleAccess["programmer"]);

        // Наладчику и администратору она положена — и видна.
        foreach (var role in new[] { "naladchik", "administrator" })
            Assert.Contains("newversions", RolesConfig.RoleAccess[role]);
    }

    /// <summary>Пункт рисуется ровно в одном блоке: три ItemsControl'а сайдбара привязаны к одной
    /// коллекции, и «видно и там, и там» означало бы двойную кнопку.</summary>
    [Fact]
    public void AnItemShowsUpInExactlyOneList()
    {
        foreach (var (pageId, label, section) in RolesConfig.NavItems)
        {
            var item = new NavItem(pageId, label, section) { IsVisible = true };
            // Списков четыре: к основному, «ДЛЯ НАЛАДЧИКА» и компактному добавилась секция «ДЛЯ
            // ПРОГРАММИСТА» (выпущено в 2.2.0.0). Перечислять их
            // здесь обязательно ВСЕ — иначе пункт новой секции выглядел бы «нигде не показывается»,
            // и тест ловил бы ровно наоборот: не ошибку, а свою неполноту.
            var shown = new[] { item.ShowInMainList, item.ShowInSetupList, item.ShowInDevList, item.ShowInCompactList };
            Assert.Single(shown, x => x);

            item.IsVisible = false;
            Assert.All(
                new[] { item.ShowInMainList, item.ShowInSetupList, item.ShowInDevList, item.ShowInCompactList },
                Assert.False);
        }
    }

    /// <summary>Каждый пункт секции хотя бы одной роли да доступен — иначе он не нужен вовсе.</summary>
    [Fact]
    public void EverySetupItem_IsReachableBySomeRole()
    {
        foreach (var item in RolesConfig.NavItems.Where(n => n.Section == NavSection.Setup))
            Assert.Contains(RolesConfig.RoleAccess, role => role.Value.Contains(item.PageId));
    }

    /// <summary>Настройка «свёрнута ли секция» и её правило «не уезжать в общий конфиг».
    ///
    /// ⚠️ СЕЙЧАС ЕЮ НИКТО НЕ ПОЛЬЗУЕТСЯ: секции бокового меню раскрыты всегда и не сворачиваются
    /// (MainWindowViewModel.SectionsCollapsible — «по умолчанию эти поля развернуты и убери вообще
    /// чтобы они сворачивались»). Сама настройка не выброшена — она читается и пишется прежним
    /// кодом и оживёт, если сворачивание вернут; тест держит её рабочей и, главное, держит её в
    /// списке ключей, которые НЕ синхронизируются: состояние окна на одном компьютере не должно
    /// разворачивать секцию у всех.</summary>
    [Fact]
    public void TheCollapsedState_IsRemembered_AndStaysOnThisMachine()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        Assert.False(cfg.SidebarSetupExpanded());

        cfg.SetSidebarSetupExpanded(true);
        Assert.True(cfg.SidebarSetupExpanded());

        // Состояние окна на конкретном компьютере — в общий конфиг не уезжает, иначе развёрнутая у
        // одного секция разворачивалась бы у всех.
        Assert.Contains("sidebar_setup_expanded", ConfigSyncSkipKeys.Read());
    }
}
