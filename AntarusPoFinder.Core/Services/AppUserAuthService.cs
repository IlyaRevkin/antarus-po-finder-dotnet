using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

public record AppUserLoginResult(bool Success, string? Role, string? Error, bool IsNewUser, AppUser? User);

/// <summary>Часть 2 — вход по AD-логину в СОБСТВЕННЫЙ ростер приложения (таблица app_users),
/// ортогональный к WindowsGroupAuth.DetectRoleForUser (роль по AD-группе, Часть 1) — этот путь не
/// заменяет тот, он лишь ловит всех, для кого группу никто не завёл/не поддерживает. Пароль
/// проверяется тем же самым механизмом (через <see cref="IAdCredentialValidator"/> — по умолчанию
/// прямой LDAP-бинд, тот же, что уже использует WindowsGroupAuth); отличие только в том, ОТКУДА
/// берётся роль после успешной проверки пароля — не из членства в AD-группе, а из локальной
/// таблицы, которую этот же метод и заполняет при первом входе логина. См. RoleSwitchDialog
/// (AntarusPoFinder.App) за тем, как оба пути (группа и ростер) сочетаются в одном диалоге входа.</summary>
public static class AppUserAuthService
{
    /// <summary>login as typed by the operator — either "login@domain" or "DOMAIN\login" (both
    /// already accepted by the existing AD-group login field, see RoleSwitchDialog.AdLoginInput).
    /// Strips whichever wrapper is present and lowercases, so "revkin.i@Elita", "Elita\revkin.i" and
    /// "REVKIN.I@ELITA" all resolve to the same roster row instead of three different ones.</summary>
    public static string NormalizeAdLogin(string rawLogin)
    {
        var login = (rawLogin ?? "").Trim();

        var atIdx = login.IndexOf('@');
        if (atIdx >= 0) login = login[..atIdx];

        var slashIdx = login.IndexOf('\\');
        if (slashIdx >= 0) login = login[(slashIdx + 1)..];

        return login.ToLowerInvariant();
    }

    /// <summary>Validates the password, then resolves the role from the app's own roster: an
    /// unknown login is created on the spot with the default role (Наладчик) and let straight in —
    /// no blocking, no waiting for administrator approval, per the operator's explicit decision (IT
    /// may never get around to maintaining AD groups). A known login keeps whatever role is stored
    /// (possibly changed by an administrator, locally or on another machine and pulled in via config
    /// sync) — it is never re-derived from AD group membership on this path.</summary>
    public static AppUserLoginResult Login(Database db, IAdCredentialValidator validator, string domain, string login, string password)
    {
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(login))
            return new AppUserLoginResult(false, null, "Укажите домен и логин.", false, null);

        if (!validator.Validate(domain, login, password, out var authError))
            return new AppUserLoginResult(false, null, authError ?? "Не удалось войти — проверьте логин и пароль.", false, null);

        var normalized = NormalizeAdLogin(login);
        var isNew = db.FindAppUserByLogin(normalized) is null;
        var user = db.TouchOrCreateAppUser(normalized);
        return new AppUserLoginResult(true, user.Role, null, isNew, user);
    }
}
