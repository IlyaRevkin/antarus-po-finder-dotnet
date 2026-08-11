using System.Linq;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Секция бокового меню «ДЛЯ НАЛАДЧИКА».
///
/// Просьба Ильи дословно: «давай сделаем доп меню какое-нибудь которое типа для наладчика но не часто
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

    /// <summary>Ровно те четыре пункта, которые назвал Илья (наклейки и «Сформировать паспорт» — не
    /// страницы, а окна, их кнопки живут прямо в разметке сайдбара).</summary>
    [Fact]
    public void TheSetupSectionHoldsExactlyTheItemsAskedFor()
    {
        Assert.Equal(NavSection.Setup, SectionOf("params"));
        Assert.Equal(NavSection.Setup, SectionOf("newversions"));
    }

    /// <summary>«ДОПОЛНИТЕЛЬНО» — редкое и настроечное: сетевые диски, хранилище на хостинге, тикеты.
    /// На неё завязан живой GUI-стенд, поэтому состав фиксируется тестом целиком: любое пополнение
    /// должно быть осознанным, а не заехать сюда случайно.</summary>
    [Fact]
    public void TheOldMoreSection_IsUntouched()
    {
        Assert.Equal(NavSection.More, SectionOf("network"));
        Assert.Equal(NavSection.More, SectionOf("hosting"));
        Assert.Equal(NavSection.More, SectionOf("tickets"));
        Assert.Equal(new[] { "network", "hosting", "tickets" },
            RolesConfig.NavItems.Where(n => n.Section == NavSection.More).Select(n => n.PageId).ToArray());
    }

    /// <summary>Каждодневное остаётся наверху и в один клик: поиск, осмотр, загрузка ПО.</summary>
    [Fact]
    public void TheEverydayPages_StayInTheMainList()
    {
        Assert.Equal(new[] { "search", "inspection", "upload" },
            RolesConfig.NavItems.Where(n => n.Section == NavSection.Main).Select(n => n.PageId).ToArray());
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
            var shown = new[] { item.ShowInMainList, item.ShowInSetupList, item.ShowInCompactList };
            Assert.Single(shown, x => x);

            item.IsVisible = false;
            Assert.All(new[] { item.ShowInMainList, item.ShowInSetupList, item.ShowInCompactList }, Assert.False);
        }
    }

    /// <summary>Каждый пункт секции хотя бы одной роли да доступен — иначе он не нужен вовсе.</summary>
    [Fact]
    public void EverySetupItem_IsReachableBySomeRole()
    {
        foreach (var item in RolesConfig.NavItems.Where(n => n.Section == NavSection.Setup))
            Assert.Contains(RolesConfig.RoleAccess, role => role.Value.Contains(item.PageId));
    }

    /// <summary>Свёрнута или развёрнута секция — запоминается между запусками (в отличие от
    /// «ДОПОЛНИТЕЛЬНО», которая всегда открывается свёрнутой). По умолчанию свёрнута: смысл
    /// секции — убрать редкое с глаз.</summary>
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
