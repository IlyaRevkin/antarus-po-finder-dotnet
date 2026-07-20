namespace AntarusPoFinder.Core.Services;

/// <summary>Contract for "prove this AD login+password pair is genuine", independent of the actual
/// mechanism used to check it. Способ №1 (<c>LdapAdCredentialValidator</c>, in AntarusPoFinder.App —
/// needs System.DirectoryServices.AccountManagement, Windows-only) wraps the already-working direct
/// LDAP bind (WindowsGroupAuth.ValidateAdCredentials). Способ №2 — checking against Antarus's own
/// internal web server instead of binding LDAP directly, for machines/networks where a direct LDAP
/// bind isn't reachable — will be a second implementation of this same interface once IT confirms a
/// working request/response format (not yet — see session notes). Nothing else in the login flow
/// (AppUserAuthService, RoleSwitchDialog) needs to change to add it: just construct the other
/// implementation and pass it in instead.</summary>
public interface IAdCredentialValidator
{
    /// <summary>Never throws — implementations must turn "domain unreachable", "bad domain name" and
    /// "wrong credentials" all into a false result with a human-readable <paramref name="error"/>,
    /// same contract as the LDAP implementation it wraps.</summary>
    bool Validate(string domain, string login, string password, out string? error);
}
