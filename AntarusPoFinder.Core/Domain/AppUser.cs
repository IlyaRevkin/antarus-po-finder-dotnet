namespace AntarusPoFinder.Core.Domain;

/// <summary>A row in the app's own AD-login roster (Настройки → Пользователи) — assigns a role by
/// AD login without depending on IT ever setting up/maintaining AD groups (see
/// AppUserAuthService.Login, AntarusPoFinder.App.WindowsGroupAuth.DetectRoleForUser for the
/// AD-group-based alternative this supplements). Source of truth for role ONLY for logins that come
/// through the roster path — the AD-group login path (Часть 1) still derives its role from AD group
/// membership directly and never touches this table.</summary>
public class AppUser
{
    public int? Id { get; set; }

    /// <summary>Normalized AD login (domain/suffix stripped, lowercased — see
    /// AppUserAuthService.NormalizeAdLogin), unique per machine. sync_id correlates the same
    /// person's row across machines once they've exchanged a config, same pattern as every other
    /// synced entity (see Database.ConfigExchange).</summary>
    public string AdLogin { get; set; } = "";

    public string Role { get; set; } = "naladchik";

    public string FirstLoginAt { get; set; } = "";
    public string LastLoginAt { get; set; } = "";

    /// <summary>Timestamp of the last ROLE change specifically (not a login) — used as the
    /// last-writer-wins tiebreaker when merging this row during config sync (see
    /// Database.ConfigExchange), because LastLoginAt changes on every machine on every login and
    /// doesn't tell you who most recently made a deliberate role decision.</summary>
    public string RoleUpdatedAt { get; set; } = "";

    public string SyncId { get; set; } = "";

    /// <summary>ОТКУДА взялась роль в этой строке — см. <see cref="AppUserRoleSource"/>. Пустая
    /// строка означает «неизвестно»: так выглядят все записи, заведённые до появления этого поля,
    /// и так же выглядит роль, приехавшая синхронизацией со старой машины. Поле едет вместе с
    /// ролью и по тому же правилу последней записи (role_updated_at): менять «откуда» в отрыве от
    /// самой роли бессмысленно, а вот роль без пояснения, откуда она, — это ровно та таблица, по
    /// которой администратор не мог понять, почему роль не та.</summary>
    public string RoleSource { get; set; } = "";

    /// <summary>Когда эту строку в последний раз сверяли с составом настроенных AD-групп
    /// («Обновить из домена» на вкладке «Пользователи»). Пусто — не сверяли ни разу.</summary>
    public string DomainCheckedAt { get; set; } = "";

    /// <summary>Домен, ИЗ КОТОРОГО этот человек в последний раз входил — так, как он был указан
    /// при входе («ELITA», «elita.local»). Пусто — ни одного входа на версии с этим полем не было
    /// (запись завели сверкой с доменом либо она старше поля).
    ///
    /// <b>Зачем.</b> «Я не знаю, к какому домену отношусь я, и тем более — другие». Про СВОЙ
    /// компьютер отвечает MachineDomain (Windows знает это и так), а про коллег ответить может
    /// только сама программа, и только записав факт: вот этот человек вошёл отсюда и тогда-то.
    /// Разные домены у разных строк — не ошибка, а нормальная жизнь компании с несколькими
    /// доменами; показывать это надо как факт, а не прятать.</summary>
    public string LogonDomain { get; set; } = "";

    /// <summary>Когда был тот самый вход (см. <see cref="LogonDomain"/>). Хранится отдельно от
    /// LastLoginAt: при слиянии конфигов домен обязан ехать вместе с отметкой СВОЕГО времени,
    /// иначе домен с одной машины склеился бы с датой входа с другой и наврал бы.</summary>
    public string LogonDomainAt { get; set; } = "";

    /// <summary>Нашёлся ли человек хоть в одной настроенной AD-группе при ПОСЛЕДНЕЙ сверке
    /// (<see cref="DomainCheckedAt"/>). Ставится только тогда, когда перечислены ВСЕ настроенные
    /// группы: по частично прочитанному домену нельзя честно сказать «такого в домене нет».</summary>
    public bool DomainFound { get; set; }
}
