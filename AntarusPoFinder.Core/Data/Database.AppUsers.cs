using System.Collections.Generic;
using AntarusPoFinder.Core.Domain;
using Microsoft.Data.Sqlite;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    private const string AppUserSelectCols =
        "id, ad_login, role, first_login_at, last_login_at, role_updated_at, sync_id, role_source, " +
        "domain_checked_at, domain_found, logon_domain, logon_domain_at";

    public AppUser? FindAppUserByLogin(string normalizedLogin)
    {
        using var r = ExecuteReader($"SELECT {AppUserSelectCols} FROM app_users WHERE ad_login=@l COLLATE NOCASE",
            cmd => cmd.Parameters.AddWithValue("@l", normalizedLogin));
        return r.Read() ? ReadAppUser(r) : null;
    }

    /// <summary>First time this AD login is seen locally: creates it with the default role
    /// (Наладчик) so the operator can go straight to work — see AppUser class doc. An already-known
    /// login just gets last_login_at bumped; its role is left untouched here (see SetAppUserRole for
    /// the only thing that changes it locally).
    ///
    /// <paramref name="roleSource"/> описывает ТОЛЬКО новосозданную строку — «эта роль не решение
    /// человека и не состав группы, это умолчание первого входа» (см. AppUserRoleSource). У уже
    /// существующей строки источник роли не трогается: вход — это не решение о роли.</summary>
    public AppUser TouchOrCreateAppUser(string normalizedLogin, string defaultRole = "naladchik",
        string roleSource = AppUserRoleSource.FirstLogin)
    {
        var now = NowIso();
        var existing = FindAppUserByLogin(normalizedLogin);
        if (existing is null)
        {
            var syncId = Guid.NewGuid().ToString();
            ExecuteNonQuery("""
                INSERT INTO app_users(ad_login, role, first_login_at, last_login_at, role_updated_at, sync_id, role_source)
                VALUES(@l, @r, @f, @la, @ru, @sy, @src)
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("@l", normalizedLogin);
                cmd.Parameters.AddWithValue("@r", defaultRole);
                cmd.Parameters.AddWithValue("@f", now);
                cmd.Parameters.AddWithValue("@la", now);
                cmd.Parameters.AddWithValue("@ru", now);
                cmd.Parameters.AddWithValue("@sy", syncId);
                cmd.Parameters.AddWithValue("@src", roleSource);
            });
            return FindAppUserByLogin(normalizedLogin)!;
        }

        ExecuteNonQuery("UPDATE app_users SET last_login_at=@la WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@la", now);
            cmd.Parameters.AddWithValue("@id", existing.Id!.Value);
        });
        existing.LastLoginAt = now;
        return existing;
    }

    /// <summary>Заводит строку ростера, НЕ отмечая её входом: человека перечислили в составе
    /// AD-группы («Обновить из домена»), а не увидели вошедшим. Поэтому first_login_at/last_login_at
    /// остаются пустыми — иначе в таблице появилась бы выдуманная дата входа, и колонки «первый/
    /// последний вход» перестали бы значить то, что написано. Ровно это и чинит вторую половину
    /// жалобы: «кто не входил — тех в списке нет вовсе».</summary>
    public AppUser CreateAppUserFromDomain(string normalizedLogin, string role, string checkedAt)
    {
        ExecuteNonQuery("""
            INSERT INTO app_users(ad_login, role, first_login_at, last_login_at, role_updated_at, sync_id,
                                  role_source, domain_checked_at, domain_found)
            VALUES(@l, @r, '', '', @ru, @sy, @src, @dc, 1)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@l", normalizedLogin);
            cmd.Parameters.AddWithValue("@r", role);
            cmd.Parameters.AddWithValue("@ru", NowIso());
            cmd.Parameters.AddWithValue("@sy", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("@src", AppUserRoleSource.Domain);
            cmd.Parameters.AddWithValue("@dc", checkedAt);
        });
        return FindAppUserByLogin(normalizedLogin)!;
    }

    public List<AppUser> GetAppUsers()
    {
        var result = new List<AppUser>();
        using var r = ExecuteReader($"SELECT {AppUserSelectCols} FROM app_users ORDER BY ad_login");
        while (r.Read()) result.Add(ReadAppUser(r));
        return result;
    }

    /// <summary>Administrator changing a user's role from Настройки → Пользователи. Bumps
    /// role_updated_at so this change wins the last-writer-wins merge the next time configs are
    /// exchanged between machines (see Database.ConfigExchange's app_users merge below), even
    /// against a role changed earlier elsewhere. Источник роли при этом становится «вручную» — см.
    /// перегрузку ниже.</summary>
    public void SetAppUserRole(int id, string role) => SetAppUserRole(id, role, AppUserRoleSource.Manual);

    /// <summary>То же самое, но с явным указанием, ОТКУДА роль (см. AppUserRoleSource).
    /// role_updated_at поднимается и тогда, когда поменялся только источник: источник едет
    /// синхронизацией вместе с ролью и по тому же правилу последней записи, значит без свежей
    /// отметки времени правда «эта роль из домена» не доехала бы до остальных машин.</summary>
    public void SetAppUserRole(int id, string role, string roleSource)
    {
        ExecuteNonQuery("UPDATE app_users SET role=@r, role_source=@src, role_updated_at=@ru WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@r", role);
            cmd.Parameters.AddWithValue("@src", roleSource);
            cmd.Parameters.AddWithValue("@ru", NowIso());
            cmd.Parameters.AddWithValue("@id", id);
        });
    }

    /// <summary>Отметить факт сверки строки с составом настроенных AD-групп, НЕ трогая саму роль и
    /// role_updated_at. Нужна для тех, кого в домене не нашли: их роль остаётся прежней (удаление —
    /// отдельное осознанное действие администратора, см. DeleteAppUser), но в таблице обязано быть
    /// видно, что последняя сверка их не подтвердила. Бить по role_updated_at здесь нельзя: это
    /// сделало бы «мы на него посмотрели» более свежим решением, чем чужая настоящая правка роли.</summary>
    public void MarkAppUserDomainChecked(int id, bool found, string checkedAt)
    {
        ExecuteNonQuery("UPDATE app_users SET domain_checked_at=@dc, domain_found=@df WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@dc", checkedAt);
            cmd.Parameters.AddWithValue("@df", found ? 1 : 0);
            cmd.Parameters.AddWithValue("@id", id);
        });
    }

    /// <summary>ЗАПОМНИТЬ, ИЗ КАКОГО ДОМЕНА ЧЕЛОВЕК ВОШЁЛ. Вызывается после каждого удачного входа
    /// по доменной учётной записи (AdStartupLoginDialog, RoleSwitchDialog).
    ///
    /// Своего рода журнал наблюдений, а не настройка: роль и её источник этим не трогаются вовсе
    /// (и role_updated_at не поднимается — иначе «он тут входил» перебило бы чужое настоящее
    /// решение о роли при слиянии конфигов). Нужно это ровно для одного вопроса, на который
    /// приложение раньше ответить не могло: «а другие к какому домену относятся». Спросить чужой
    /// компьютер нельзя — можно только записать факт входа, когда он случился.
    ///
    /// Пустой домен игнорируется: лучше честное «неизвестен», чем затёртая пустотой правда.</summary>
    public void RecordAppUserLogonDomain(int id, string domain, string? whenIso = null)
    {
        domain = (domain ?? "").Trim();
        if (domain.Length == 0) return;
        ExecuteNonQuery("UPDATE app_users SET logon_domain=@d, logon_domain_at=@at WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@d", domain);
            cmd.Parameters.AddWithValue("@at", string.IsNullOrEmpty(whenIso) ? NowIso() : whenIso);
            cmd.Parameters.AddWithValue("@id", id);
        });
    }

    /// <summary>То же самое по логину — удобство для путей входа, где строка ростера только что
    /// заведена/обновлена и отдельно её искать незачем.</summary>
    public void RecordAppUserLogonDomain(string normalizedLogin, string domain)
    {
        var user = FindAppUserByLogin(normalizedLogin);
        if (user?.Id is not null) RecordAppUserLogonDomain(user.Id.Value, domain);
    }

    /// <summary>Administrator removing a stale/mistaken roster entry from Настройки → Пользователи.
    /// Local-only: MergeAppUsersInto never deletes (see its doc — "Nobody is ever removed from the
    /// roster via sync"), so if another machine still has this login in its own local roster, the
    /// next config exchange will re-add it here. That's an acceptable trade-off for a rarely-used
    /// cleanup action rather than building a sync-wide tombstone mechanism for it.</summary>
    public void DeleteAppUser(int id) =>
        ExecuteNonQuery("DELETE FROM app_users WHERE id=@id", cmd => cmd.Parameters.AddWithValue("@id", id));

    /// <summary>Merges an incoming AD roster into the local table — natural key is ad_login
    /// (COLLATE NOCASE), matched by sync_id first like every other entity in Database.ConfigExchange,
    /// falling back to login for first contact. Role is last-writer-wins by role_updated_at IN EITHER
    /// DIRECTION — unlike reservations/fw_versions this is not a one-way "only ever advances" state
    /// machine, an administrator can promote OR demote — so a role changed more recently must win
    /// regardless of which side (local vs incoming) made the more recent change. role_source едет
    /// вместе с ролью и тем же решением: «какая роль» и «откуда она» — это один факт, разъехавшись,
    /// они врали бы администратору в точности так же, как врала таблица без источника вовсе.
    /// first_login_at keeps whichever side's is earlier (a historical fact, never moved backwards);
    /// last_login_at keeps whichever is later (most recent login witnessed by any machine).
    /// domain_checked_at/domain_found — ОТДЕЛЬНОЕ решение, не связанное с LWW по роли: берётся более
    /// свежая сверка с доменом, чья бы машина её ни делала (сверку делают с одной машины, а видеть
    /// результат должны все). Nobody is ever removed from the roster via sync. Shared by the full
    /// hierarchy import AND by <see cref="MergeAppUsersOnly"/> — the latter lets a non-administrator
    /// machine contribute its own roster entry to the shared config without the (admin-only) risk of
    /// pushing a full hierarchy snapshot, see ConfigSyncService.PushAppUsersOnly.</summary>
    private void MergeAppUsersInto(List<ExportedAppUser> incoming, ImportCounts counts, bool apply)
    {
        foreach (var u in incoming)
        {
            if (string.IsNullOrWhiteSpace(u.AdLogin)) continue;

            var existing = FindAppUserBySyncOrLogin(u.SyncId, u.AdLogin);
            if (existing is null)
            {
                counts.AppUsersAdded++;
                if (apply)
                {
                    var sync = string.IsNullOrEmpty(u.SyncId) ? Guid.NewGuid().ToString() : u.SyncId;
                    ExecuteNonQuery("""
                        INSERT INTO app_users(ad_login, role, first_login_at, last_login_at, role_updated_at, sync_id,
                                              role_source, domain_checked_at, domain_found, logon_domain, logon_domain_at)
                        VALUES(@l,@r,@f,@la,@ru,@sy,@src,@dc,@df,@ld,@lda)
                        """, cmd =>
                    {
                        cmd.Parameters.AddWithValue("@l", u.AdLogin); cmd.Parameters.AddWithValue("@r", u.Role);
                        cmd.Parameters.AddWithValue("@f", u.FirstLoginAt); cmd.Parameters.AddWithValue("@la", u.LastLoginAt);
                        cmd.Parameters.AddWithValue("@ru", u.RoleUpdatedAt); cmd.Parameters.AddWithValue("@sy", sync);
                        cmd.Parameters.AddWithValue("@src", u.RoleSource ?? "");
                        cmd.Parameters.AddWithValue("@dc", u.DomainCheckedAt ?? "");
                        cmd.Parameters.AddWithValue("@df", u.DomainFound ? 1 : 0);
                        cmd.Parameters.AddWithValue("@ld", u.LogonDomain ?? "");
                        cmd.Parameters.AddWithValue("@lda", u.LogonDomainAt ?? "");
                    });
                }
                continue;
            }

            var incomingRoleWins = string.CompareOrdinal(u.RoleUpdatedAt, existing.RoleUpdatedAt) > 0;
            var wantRole = incomingRoleWins ? u.Role : existing.Role;
            var wantRoleSource = incomingRoleWins ? (u.RoleSource ?? "") : existing.RoleSource;
            var wantRoleUpdatedAt = incomingRoleWins ? u.RoleUpdatedAt : existing.RoleUpdatedAt;
            var wantFirst = string.IsNullOrEmpty(existing.FirstLoginAt) ||
                (!string.IsNullOrEmpty(u.FirstLoginAt) && string.CompareOrdinal(u.FirstLoginAt, existing.FirstLoginAt) < 0)
                ? u.FirstLoginAt : existing.FirstLoginAt;
            var wantLast = string.CompareOrdinal(u.LastLoginAt, existing.LastLoginAt) > 0 ? u.LastLoginAt : existing.LastLoginAt;
            // Сверка с доменом: побеждает более поздняя, независимо от решения по роли.
            var incomingCheckWins = string.CompareOrdinal(u.DomainCheckedAt ?? "", existing.DomainCheckedAt) > 0;
            var wantCheckedAt = incomingCheckWins ? (u.DomainCheckedAt ?? "") : existing.DomainCheckedAt;
            var wantFound = incomingCheckWins ? u.DomainFound : existing.DomainFound;
            // Домен последнего входа — наблюдение, как и сверка: побеждает более ПОЗДНИЙ вход,
            // чьей бы машиной он ни был замечен. Человек ходит по нескольким компьютерам, и факт
            // «в прошлый вторник он входил из ELITA» должен быть виден всем, а не только той
            // машине, за которой он тогда сидел. Домен и его отметка времени едут строго парой.
            var incomingLogonWins = string.CompareOrdinal(u.LogonDomainAt ?? "", existing.LogonDomainAt) > 0;
            var wantLogonDomain = incomingLogonWins ? (u.LogonDomain ?? "") : existing.LogonDomain;
            var wantLogonAt = incomingLogonWins ? (u.LogonDomainAt ?? "") : existing.LogonDomainAt;
            var adoptSyncId = !string.IsNullOrEmpty(u.SyncId) && u.SyncId != existing.SyncId;

            var changed = wantRole != existing.Role || wantRoleSource != existing.RoleSource
                || wantFirst != existing.FirstLoginAt || wantLast != existing.LastLoginAt
                || wantCheckedAt != existing.DomainCheckedAt || wantFound != existing.DomainFound
                || wantLogonDomain != existing.LogonDomain || wantLogonAt != existing.LogonDomainAt || adoptSyncId;
            if (!changed) continue;

            if (wantRole != existing.Role) counts.AppUsersUpdated++;
            if (!apply) continue;

            ExecuteNonQuery("""
                UPDATE app_users SET role=@r, role_source=@src, first_login_at=@f, last_login_at=@la,
                                     role_updated_at=@ru, sync_id=@sy, domain_checked_at=@dc, domain_found=@df,
                                     logon_domain=@ld, logon_domain_at=@lda
                WHERE id=@id
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("@r", wantRole); cmd.Parameters.AddWithValue("@src", wantRoleSource);
                cmd.Parameters.AddWithValue("@f", wantFirst);
                cmd.Parameters.AddWithValue("@la", wantLast); cmd.Parameters.AddWithValue("@ru", wantRoleUpdatedAt);
                cmd.Parameters.AddWithValue("@dc", wantCheckedAt); cmd.Parameters.AddWithValue("@df", wantFound ? 1 : 0);
                cmd.Parameters.AddWithValue("@ld", wantLogonDomain); cmd.Parameters.AddWithValue("@lda", wantLogonAt);
                cmd.Parameters.AddWithValue("@id", existing.Id!.Value);
                cmd.Parameters.AddWithValue("@sy", adoptSyncId ? u.SyncId : existing.SyncId);
            });
        }
    }

    /// <summary>Public narrow entry point for a non-administrator's "push just my roster entry"
    /// (see ConfigSyncService.PushAppUsersOnly) — merges an incoming roster (read from the shared
    /// config file) into the local table using the exact same rule as a full hierarchy import,
    /// without touching any other table.</summary>
    public ImportCounts MergeAppUsersOnly(List<ExportedAppUser> incoming)
    {
        var counts = new ImportCounts();
        MergeAppUsersInto(incoming, counts, apply: true);
        return counts;
    }

    /// <summary>Sync-id-aware lookup for the config-exchange merge (see Database.ConfigExchange) —
    /// same pattern as FindBySyncOrName for the hierarchy tables, but keyed on ad_login instead of a
    /// group-scoped name.</summary>
    private AppUser? FindAppUserBySyncOrLogin(string syncId, string login)
    {
        if (!string.IsNullOrEmpty(syncId))
        {
            using var r1 = ExecuteReader($"SELECT {AppUserSelectCols} FROM app_users WHERE sync_id=@sy",
                cmd => cmd.Parameters.AddWithValue("@sy", syncId));
            if (r1.Read()) return ReadAppUser(r1);
        }
        return FindAppUserByLogin(login);
    }

    private static AppUser ReadAppUser(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(0),
        AdLogin = r.GetString(1),
        Role = GetString(r, "role", "naladchik"),
        FirstLoginAt = GetString(r, "first_login_at"),
        LastLoginAt = GetString(r, "last_login_at"),
        RoleUpdatedAt = GetString(r, "role_updated_at"),
        SyncId = GetString(r, "sync_id"),
        RoleSource = GetString(r, "role_source"),
        DomainCheckedAt = GetString(r, "domain_checked_at"),
        DomainFound = GetInt(r, "domain_found") != 0,
        LogonDomain = GetString(r, "logon_domain"),
        LogonDomainAt = GetString(r, "logon_domain_at"),
    };
}
