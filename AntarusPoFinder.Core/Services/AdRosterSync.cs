using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Один человек, перечисленный в составе настроенной AD-группы: роль, которую даёт эта
/// группа, и его логин уже в нормализованном виде (<see cref="AppUserAuthService.NormalizeAdLogin"/>).</summary>
public record AdGroupMember(string Role, string Login);

/// <summary>Чем кончилась сверка ростера с доменом — ровно те числа, которые показываются человеку.
/// <paramref name="NotFoundInDomain"/> имеет смысл только при полной сверке (см.
/// <see cref="AdRosterSync.Apply"/>), иначе он равен нулю.</summary>
public record AdRosterSyncResult(int Added, int RoleChanged, int Confirmed, int NotFoundInDomain);

/// <summary>ПРИВЕСТИ РОСТЕР «Пользователи» К ТОМУ, ЧТО РЕАЛЬНО В ДОМЕНЕ.
///
/// <b>Зачем.</b> Ростер наполняется ФАКТОМ ВХОДА, а не составом групп. Отсюда обе половины жалобы
/// Ильи: кто ни разу не входил на этой инсталляции — того в списке нет вовсе, а у кого роль
/// записалась при давнем первом входе — у того висит она, пока он не войдёт заново на версии с
/// исправлением (а при режиме «не переспрашивать никогда» он может не входить заново годами, см.
/// AdSessionService.AdSessionMode.Always). Перечисление членов групп закрывает оба случая сразу и
/// не ждёт, пока все перелогинятся.
///
/// Здесь живёт только ЧИСТАЯ часть — разрешение ролей и правила применения к базе. Поход в сам
/// домен — в AntarusPoFinder.App.AdGroupMembers: System.DirectoryServices.AccountManagement
/// работает только под Windows, а Core собирается под net8.0 и покрыт тестами без домена.
///
/// <b>Три правила, из-за которых это не «затереть ростер составом групп».</b>
/// <list type="number">
/// <item>Никого не удаляем. Кого в группах нет — остаётся в списке с прежней ролью и с пометкой,
///   что последняя сверка его в домене не нашла. Удаление — отдельное осознанное действие
///   администратора (Database.DeleteAppUser), а не побочный эффект нажатия кнопки: человек мог
///   уволиться, а мог просто выпасть из группы на время.</item>
/// <item>«Не найден» ставим ТОЛЬКО при полной сверке. Если хоть одна настроенная группа не
///   прочиталась (нет прав, группа переименована, домен отвалился на середине), объявлять всех
///   остальных «в домене нет» — враньё, и именно такое враньё администратор и принял бы за
///   очередную поломку списка.</item>
/// <item>Роль берётся самая привилегированная из совпавших — как и при входе
///   (<see cref="RoleFromGroups.RolesHighestFirst"/>, WindowsGroupAuth.DetectRoleForUser): человек в
///   группе админов и в группе наладчиков — администратор.</item>
/// </list></summary>
public static class AdRosterSync
{
    /// <summary>Логин → роль по самой привилегированной из групп, в которых он состоит. Дубликаты
    /// внутри одной группы и один человек в нескольких группах — штатная ситуация AD, а не ошибка.</summary>
    public static Dictionary<string, string> ResolveRoles(IEnumerable<AdGroupMember> members)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in members)
        {
            if (string.IsNullOrWhiteSpace(m.Login)) continue;
            var login = AppUserAuthService.NormalizeAdLogin(m.Login);
            if (login.Length == 0) continue;

            if (!result.TryGetValue(login, out var current)) { result[login] = m.Role; continue; }
            if (Rank(m.Role) < Rank(current)) result[login] = m.Role;
        }
        return result;
    }

    /// <summary>Чем меньше число, тем привилегированнее роль. Неизвестная роль — в самый конец,
    /// чтобы она никогда не победила настоящую.</summary>
    private static int Rank(string role)
    {
        var idx = Array.IndexOf(RoleFromGroups.RolesHighestFirst, role);
        return idx < 0 ? int.MaxValue : idx;
    }

    /// <summary>Применить состав групп к ростеру. <paramref name="complete"/> — удалось ли
    /// перечислить ВСЕ настроенные группы (см. правило 2 в описании класса): только при true
    /// отсутствующим в домене ставится пометка «не найден».</summary>
    public static AdRosterSyncResult Apply(Database db, IEnumerable<AdGroupMember> members, bool complete, DateTime now)
    {
        var resolved = ResolveRoles(members);
        var checkedAt = now.ToString("yyyy-MM-dd HH:mm");
        int added = 0, changed = 0, confirmed = 0, missing = 0;

        foreach (var (login, role) in resolved)
        {
            var existing = db.FindAppUserByLogin(login);
            if (existing is null)
            {
                db.CreateAppUserFromDomain(login, role, checkedAt);
                added++;
                continue;
            }

            // Роль или её источник разошлись с доменом — переписываем и отмечаем источник «домен».
            // Отдельно считаем именно СМЕНУ роли: администратору важно, скольким людям права
            // поменялись, а не сколько строк технически обновилось.
            if (!string.Equals(existing.Role, role, StringComparison.Ordinal))
            {
                db.SetAppUserRole(existing.Id!.Value, role, AppUserRoleSource.Domain);
                changed++;
            }
            else if (existing.RoleSource != AppUserRoleSource.Domain)
            {
                db.SetAppUserRole(existing.Id!.Value, role, AppUserRoleSource.Domain);
                confirmed++;
            }
            else confirmed++;

            db.MarkAppUserDomainChecked(existing.Id!.Value, found: true, checkedAt);
        }

        if (complete)
            foreach (var u in db.GetAppUsers())
            {
                if (resolved.ContainsKey(u.AdLogin)) continue;
                db.MarkAppUserDomainChecked(u.Id!.Value, found: false, checkedAt);
                missing++;
            }

        return new AdRosterSyncResult(added, changed, confirmed, missing);
    }
}
