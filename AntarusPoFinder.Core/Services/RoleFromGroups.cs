using System.Collections.Generic;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Роль приложения по списку групп пользователя, пришедшему из токена корпоративного входа
/// (<see cref="OidcIdentityProvider"/>). Настройки те же самые, что у AD-входа — ad_group_administrator
/// / ad_group_programmer / ad_group_naladchik: заводить второй набор полей ради того же вопроса «какая
/// группа означает какую роль» смысла нет, а с федерацией AD в Keycloak это буквально те же группы.
///
/// Отличие от <c>WindowsGroupAuth.DetectRoleForUser</c> ровно одно: там членство спрашивают у самого
/// домена, здесь оно уже пришло claim'ом — поэтому проверка чистая, без похода в сеть, и потому
/// вынесена в Core и покрыта тестами.
///
/// Два правила, из-за которых это не однострочник:
/// 1. **Сначала самая привилегированная роль** — как и у AD: человек в группе админов и в группе
///    наладчиков должен получить админа, а не наоборот.
/// 2. **Keycloak отдаёт группы путём** («/Antarus/ПО Finder/Администраторы»), а в настройке записано
///    имя группы. Поэтому сравниваем и полное значение, и последний сегмент пути.
///
/// Никто не совпал (или группы не настроены) — null: вызывающая сторона откатывается на ростер
/// приложения, где новый человек получает МИНИМАЛЬНУЮ роль. Так и решили: без совпадения — минимум,
/// а не «пусть будет программист».</summary>
public static class RoleFromGroups
{
    /// <summary>Роли от самой привилегированной к самой обычной — порядок разбора.</summary>
    public static readonly string[] RolesHighestFirst = { "administrator", "programmer", "naladchik" };

    public static string? Detect(ConfigService cfg, IEnumerable<string>? groups)
    {
        if (groups is null) return null;
        var claimed = groups.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToList();
        if (claimed.Count == 0) return null;

        foreach (var role in RolesHighestFirst)
        {
            var configured = cfg.Get($"ad_group_{role}").Trim();
            if (configured.Length == 0) continue;
            if (claimed.Any(g => Matches(g, configured))) return role;
        }
        return null;
    }

    private static bool Matches(string claimed, string configured) =>
        string.Equals(claimed, configured, StringComparison.OrdinalIgnoreCase)
        || string.Equals(LastSegment(claimed), configured, StringComparison.OrdinalIgnoreCase)
        || string.Equals(claimed, LastSegment(configured), StringComparison.OrdinalIgnoreCase);

    private static string LastSegment(string value)
    {
        var trimmed = value.Trim().Trim('/');
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }
}
