using System.Collections.Generic;

namespace AntarusPoFinder.App.ViewModels;

public static class RolesConfig
{
    public static readonly (string PageId, string Label)[] NavItems =
    [
        ("search", "Поиск"),
        ("inspection", "Осмотр"),
        ("newversions", "Модерация прошивок"),
        ("upload", "Загрузка ПО"),
        ("params", "Параметры ПЧ/УПП"),
        // Паспорта шкафов — компактным пунктом в свёрнутой секции «ДОПОЛНИТЕЛЬНО» рядом с
        // «Сетевыми дисками» и «Тикетами»: заходят сюда редко (загрузить/поправить шаблон), а
        // печатают паспорт с карточки поиска или прямо из выдачи.
        ("passports", "Паспорта шкафов"),
        ("network", "Сетевые диски"),
        ("tickets", "Тикеты"),
    ];

    /// <summary>Все три роли работают с одним и тем же общим диском, поэтому пути и интервал
    /// синхронизации (страница "network") доступны всем — не только администратору, который раньше
    /// был единственным, кто мог их настроить через полноценные Настройки. "tickets" (баг-репорты/
    /// предложения) точно так же доступна всем ролям — что именно каждая роль видит/может там
    /// делать решается внутри TicketsView (CreatedBy-фильтр для наладчика/программиста, полный
    /// доступ и смена статуса для администратора), не через видимость самого пункта меню.
    /// "settings" тоже теперь доступна наладчику/программисту — что именно из неё видно каждой роли
    /// решается внутри SettingsView (ApplyRoleVisibility: урезанный набор вкладок + урезанное
    /// "Общие"), не через видимость самого пункта меню, тем же способом что и "tickets".</summary>
    public static readonly Dictionary<string, HashSet<string>> RoleAccess = new()
    {
        // "passports" доступна всем ролям по той же причине, что "params": паспорт печатает наладчик,
        // а заводит/правит шаблон обычно программист или администратор — запрет на страницу означал бы,
        // что наладчик не может даже посмотреть, что за паспорт у шкафа.
        ["naladchik"] = ["search", "inspection", "newversions", "params", "passports", "settings", "network", "tickets"],
        ["programmer"] = ["search", "upload", "params", "passports", "settings", "network", "tickets"],
        ["administrator"] = ["search", "inspection", "newversions", "upload", "params", "passports", "settings", "network", "tickets"],
    };

    public static readonly (string RoleId, string Label)[] Roles =
    [
        ("naladchik", "Наладчик"),
        ("programmer", "Программист"),
        ("administrator", "Администратор"),
    ];

    public static string RoleLabel(string roleId)
    {
        foreach (var (id, label) in Roles)
            if (id == roleId) return label;
        return roleId;
    }
}
