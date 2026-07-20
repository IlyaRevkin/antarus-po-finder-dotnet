namespace AntarusPoFinder.Core.Services;

/// <summary>"Rejected by the target" vs "couldn't even ask the target" — a wrong password and an
/// unreachable server both come back as a false <see cref="IAdCredentialValidator.Validate"/> result,
/// but the UI (RoleSwitchDialog) and the способ="both" fallback logic need to tell them apart: a
/// rejected password should never silently fall through to a second check (that would turn "I typed
/// the wrong password" into a confusing double prompt), while an unreachable способ should.</summary>
public enum AdValidationStatus
{
    Success,
    InvalidCredentials,
    /// <summary>The target (domain controller or HTTP endpoint) could not be reached/answered at all
    /// — network down, wrong domain/URL, timeout, TLS failure, etc. Never means "password was wrong".</summary>
    Unavailable,
}

/// <summary>Contract for "prove this AD login+password pair is genuine", independent of the actual
/// mechanism used to check it. Способ №1 (<c>LdapAdCredentialValidator</c>, in AntarusPoFinder.App —
/// needs System.DirectoryServices.AccountManagement, Windows-only) wraps the already-working direct
/// LDAP bind (WindowsGroupAuth.ValidateAdCredentials). Способ №2 (<c>HttpAdCredentialValidator</c>,
/// also in AntarusPoFinder.App) checks the password against Antarus's own internal web server
/// (NTLM/Negotiate over HTTPS) instead of binding LDAP directly, for machines/networks where a direct
/// LDAP bind isn't reachable but the internal site is. Настройки → Общие → "Способ проверки" picks
/// which one(s) RoleSwitchDialog actually constructs and calls — see
/// AntarusPoFinder.App.AdCredentialValidatorFactory. Nothing else in the login flow
/// (AppUserAuthService) needs to change to add a способ: just construct the other implementation.</summary>
public interface IAdCredentialValidator
{
    /// <summary>Never throws — implementations must turn "domain/server unreachable", "bad domain
    /// name/URL" and "wrong credentials" all into a false result with a human-readable
    /// <paramref name="error"/>, same contract as the LDAP implementation it wraps.</summary>
    bool Validate(string domain, string login, string password, out string? error);

    /// <summary>Same check as <see cref="Validate"/>, but classifies a failure as rejected
    /// credentials vs. an unreachable target (see <see cref="AdValidationStatus"/>) — needed for the
    /// способ="оба" fallback (try способ №1, only fall back to №2 if №1 couldn't even reach the
    /// domain) and for showing the operator an accurate error ("сервер недоступен" vs "неверный
    /// пароль" are different problems with different next steps). Default implementation just wraps
    /// <see cref="Validate"/> and can't tell the two apart (treats every failure as
    /// InvalidCredentials) — real implementations (LdapAdCredentialValidator,
    /// HttpAdCredentialValidator) both override it with an actual classification.</summary>
    AdValidationStatus ValidateWithStatus(string domain, string login, string password, out string? error)
    {
        var ok = Validate(domain, login, password, out error);
        return ok ? AdValidationStatus.Success : AdValidationStatus.InvalidCredentials;
    }
}
