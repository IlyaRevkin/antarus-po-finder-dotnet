namespace AntarusPoFinder.App.ViewModels;

public static class RolesConfig
{
    /// <summary>Пункты бокового меню и блок, в котором каждый живёт (см. <see cref="NavSection"/>).
    /// Порядок — порядок кнопок внутри своего блока.</summary>
    public static readonly (string PageId, string Label, NavSection Section)[] NavItems =
    [
        ("search", "Поиск", NavSection.Main),
        ("inspection", "Осмотр", NavSection.Main),
        ("upload", "Загрузка ПО", NavSection.Main),
        // Секция «ДЛЯ НАЛАДЧИКА» — редкое, но именно рабочее: параметры ПЧ/УПП и модерация прошивок.
        // Наклейки и «Сформировать паспорт» — там же, но они не страницы, а окна, поэтому их кнопки
        // прописаны прямо в MainWindow.xaml, а не здесь.
        ("params", "Параметры ПЧ/УПП", NavSection.Setup),
        ("newversions", "Модерация прошивок", NavSection.Setup),
        ("network", "Сетевые диски", NavSection.More),
        ("hosting", "Хранилище", NavSection.More),
        ("tickets", "Тикеты", NavSection.More),
        // «Чистка диска» пунктом меню БЫЛА, но переехала в Настройки → «Чистка диска» (просьба Ильи
        // от 12.08.2026: «кнопку чистка мусора вкладку перенеси в настройки лучше наверное»).
        // Заходят туда раз в месяц и ради разовой уборки, а не ради работы, — место такой страницы
        // среди настроек, а не в списке рабочих экранов. Права не поменялись: вкладку видит только
        // администратор (SettingsView.ApplyRoleVisibility), ровно как раньше пункт меню.
    ];

    /// <summary>Все три роли работают с одним и тем же общим диском, поэтому пути и интервал
    /// синхронизации (страница "network") доступны всем — не только администратору, который раньше
    /// был единственным, кто мог их настроить через полноценные Настройки. "tickets" (баг-репорты/
    /// предложения) точно так же доступна всем ролям — что именно каждая роль видит/может там
    /// делать решается внутри TicketsView (CreatedBy-фильтр для наладчика/программиста, полный
    /// доступ и смена статуса для администратора), не через видимость самого пункта меню.
    /// "settings" тоже теперь доступна наладчику/программисту — что именно из неё видно каждой роли
    /// решается внутри SettingsView (ApplyRoleVisibility: урезанный набор вкладок + урезанное
    /// "Общие"), не через видимость самого пункта меню, тем же способом что и "tickets".
    ///
    /// Страницы "cleanup" здесь больше нет: «Чистка диска» стала вкладкой Настроек. Она
    /// переименовывает, переносит и безвозвратно удаляет файлы на общем диске, то есть чужую работу,
    /// поэтому осталась строго администраторской — теперь это видимость вкладки
    /// (SettingsView.ApplyRoleVisibility), а не строка в этом справочнике.</summary>
    public static readonly Dictionary<string, HashSet<string>> RoleAccess = new()
    {
        // «hosting» — состояние выкладки на хостинг. Наладчику там делать нечего: он инструкции не
        // выкладывает, а читает их по QR. Программист выкладывает и обязан видеть, доехало ли.
        ["naladchik"] = ["search", "inspection", "newversions", "params", "settings", "network", "tickets"],
        ["programmer"] = ["search", "upload", "params", "settings", "network", "hosting", "tickets"],
        ["administrator"] = ["search", "inspection", "newversions", "upload", "params", "settings", "network", "hosting", "tickets"],
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
