using System.Collections.Generic;

namespace AntarusPoFinder.App.ViewModels;

public static class RolesConfig
{
    public static readonly (string PageId, string Label)[] NavItems =
    [
        ("search", "Поиск"),
        ("inspection", "Осмотр"),
        ("newversions", "Модерация тегов"),
        ("upload", "Загрузка ПО"),
        ("params", "Параметры ПЧ/УПП"),
        ("network", "Сетевые диски"),
    ];

    /// <summary>Все три роли работают с одним и тем же общим диском, поэтому пути и интервал
    /// синхронизации (страница "network") доступны всем — не только администратору, который раньше
    /// был единственным, кто мог их настроить через полноценные Настройки.</summary>
    public static readonly Dictionary<string, HashSet<string>> RoleAccess = new()
    {
        ["naladchik"] = ["search", "inspection", "newversions", "params", "network"],
        ["programmer"] = ["search", "upload", "params", "network"],
        ["administrator"] = ["search", "inspection", "newversions", "upload", "params", "settings", "network"],
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
