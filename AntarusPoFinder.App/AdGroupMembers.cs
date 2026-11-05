using System.Collections.Generic;
using System.DirectoryServices.AccountManagement;
using System.Linq;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App;

/// <summary>Чем кончилась попытка перечислить ОДНУ настроенную группу.
///
/// Здесь нарочно три разных «не получилось», а не один флаг успеха: администратор, нажавший
/// «Обновить из домена» и увидевший пустой результат, обязан понимать, что именно не вышло —
/// группа не настроена, такой группы нет в домене, или прав не хватило. Молчаливое «ничего не
/// изменилось» — ровно та беда, из-за которой он второй раз спрашивает, почему список неверный.</summary>
/// <param name="Role">Роль приложения, за которую отвечает группа (administrator/programmer/naladchik).</param>
/// <param name="GroupName">Что записано в настройке ad_group_&lt;role&gt;.</param>
/// <param name="Configured">Настройка вообще заполнена.</param>
/// <param name="Ok">Группа найдена и её состав прочитан.</param>
/// <param name="Error">Человеческая причина, если не вышло.</param>
/// <param name="Logins">sAMAccountName членов группы, включая вложенные группы.</param>
public record AdGroupProbe(string Role, string GroupName, bool Configured, bool Ok, string? Error, List<string> Logins);

/// <summary>Итог обхода всех трёх групп. <see cref="Complete"/> — прочитаны ВСЕ настроенные группы
/// (и хотя бы одна настроена): только в этом случае можно честно сказать «такого-то в домене нет»
/// про тех, кого в составе не оказалось (см. AdRosterSync.Apply).</summary>
public record AdGroupScan(List<AdGroupProbe> Probes, string? FatalError)
{
    public bool AnyConfigured => Probes.Any(p => p.Configured);
    public bool Complete => FatalError is null && AnyConfigured && Probes.Where(p => p.Configured).All(p => p.Ok);
    public IEnumerable<AdGroupMember> Members =>
        Probes.Where(p => p.Ok).SelectMany(p => p.Logins.Select(l => new AdGroupMember(p.Role, l)));
}

/// <summary>ПЕРЕЧИСЛИТЬ СОСТАВ НАСТРОЕННЫХ AD-ГРУПП — обратная сторона
/// <see cref="WindowsGroupAuth.DetectRoleForUser"/>.
///
/// Там спрашивают «какая роль у ЭТОГО человека», и спросить можно только когда он прямо сейчас
/// вводит пароль. Из-за этого ростер приложения знал ровно тех, кто хоть раз вошёл, и ровно ту
/// роль, которая была у них на момент входа. Здесь спрашивают наоборот — «кто входит в группу» —
/// и потому ответ получается про всех сразу и не ждёт, пока каждый перелогинится.
///
/// Живёт в App, а не в Core: System.DirectoryServices.AccountManagement — только Windows, а Core
/// собирается под чистый net8.0. Чистая часть (разрешение ролей и правила применения к базе) —
/// в Core.AdRosterSync, она и покрыта тестами; сюда тестом не дотянуться без живого домена.</summary>
public static class AdGroupMembers
{
    /// <summary>Перечислить состав групп. <paramref name="login"/>/<paramref name="password"/>
    /// пустые — работаем от учётной записи текущего сеанса Windows (обычный случай: администратор
    /// сидит под своей доменной учёткой, и спрашивать у него пароль незачем). Непустые — от
    /// указанной учётной записи: машина может быть под общим/локальным аккаунтом, см. комментарий
    /// к WindowsGroupAuth.</summary>
    public static AdGroupScan Enumerate(ConfigService cfg, string domain, string? login, string? password)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return new AdGroupScan(new List<AdGroupProbe>(), "Не указан домен — заполните «Настройки → Подключение → Домен».");

        PrincipalContext ctx;
        try
        {
            ctx = string.IsNullOrWhiteSpace(login)
                ? new PrincipalContext(ContextType.Domain, domain)
                : new PrincipalContext(ContextType.Domain, domain, login, password);
        }
        catch (PrincipalServerDownException)
        {
            return new AdGroupScan(new List<AdGroupProbe>(),
                $"Не удалось связаться с доменом «{domain}» — проверьте сеть, VPN и имя домена.");
        }
        catch (Exception ex)
        {
            return new AdGroupScan(new List<AdGroupProbe>(), $"Домен «{domain}» не отвечает: {ex.Message}");
        }

        using (ctx)
        {
            var probes = new List<AdGroupProbe>();
            foreach (var role in RoleFromGroups.RolesHighestFirst)
            {
                var group = cfg.Get($"ad_group_{role}").Trim();
                if (group.Length == 0)
                {
                    probes.Add(new AdGroupProbe(role, "", Configured: false, Ok: false,
                        "группа для этой роли не настроена (Настройки → Подключение)", new List<string>()));
                    continue;
                }
                probes.Add(Probe(ctx, role, group));
            }
            return new AdGroupScan(probes, null);
        }
    }

    private static AdGroupProbe Probe(PrincipalContext ctx, string role, string group)
    {
        try
        {
            using var principal = GroupPrincipal.FindByIdentity(ctx, group);
            if (principal is null)
                return new AdGroupProbe(role, group, true, false,
                    $"группы «{group}» в домене нет — проверьте название в настройках", new List<string>());

            // recursive: true — вложенные группы. В настоящем AD права почти никогда не висят
            // плоским списком людей: «Программисты ПО Finder» запросто состоит из отдела целиком.
            // Без рекурсии половина людей просто не нашлась бы, и список снова оказался бы неполным.
            var logins = new List<string>();
            foreach (var member in principal.GetMembers(recursive: true))
            {
                using (member)
                {
                    var name = member.SamAccountName;
                    if (!string.IsNullOrWhiteSpace(name)) logins.Add(name);
                }
            }
            return new AdGroupProbe(role, group, true, true, null, logins);
        }
        catch (PrincipalServerDownException)
        {
            return new AdGroupProbe(role, group, true, false, "домен перестал отвечать", new List<string>());
        }
        catch (UnauthorizedAccessException)
        {
            return new AdGroupProbe(role, group, true, false,
                "недостаточно прав на чтение состава группы", new List<string>());
        }
        catch (Exception ex)
        {
            return new AdGroupProbe(role, group, true, false, ex.Message, new List<string>());
        }
    }
}
