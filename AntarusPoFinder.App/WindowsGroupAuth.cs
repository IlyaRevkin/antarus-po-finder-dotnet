using System;
using System.DirectoryServices.AccountManagement;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App;

/// <summary>Optional AD-group-based role detection, supplementing (not replacing) the shared role
/// passwords. Authenticates an explicitly-entered domain\login+password against AD directly —
/// not the Windows session the app happens to be running under, since the operator may be logged
/// into Windows as a different (e.g. shared/local) account than their AD identity.</summary>
public static class WindowsGroupAuth
{
    /// <summary>Validates a domain\login+password pair against AD directly (LDAP bind under those
    /// credentials). Never throws: unreachable domain / bad domain name / wrong credentials all
    /// come back as a false result with a human-readable error.</summary>
    public static bool ValidateAdCredentials(string domain, string login, string password, out string? error) =>
        ValidateAdCredentials(domain, login, password, out error, out _);

    /// <summary>Same check, plus <paramref name="unavailable"/> — true only for "the domain
    /// controller itself couldn't be reached" (PrincipalServerDownException), false for every other
    /// failure including a rejected password. Lets LdapAdCredentialValidator.ValidateWithStatus
    /// classify the failure for the способ="оба" fallback without duplicating the try/catch here.</summary>
    public static bool ValidateAdCredentials(string domain, string login, string password, out string? error, out bool unavailable)
    {
        unavailable = false;
        try
        {
            using var ctx = new PrincipalContext(ContextType.Domain, domain, login, password);
            if (ctx.ValidateCredentials(login, password))
            {
                error = null;
                return true;
            }
            error = "Неверный логин или пароль.";
            return false;
        }
        catch (PrincipalServerDownException)
        {
            unavailable = true;
            error = $"Не удалось связаться с доменом «{domain}» — проверьте сеть/имя домена.";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Ошибка входа: {ex.Message}";
            return false;
        }
    }

    /// <summary>Checks the configured per-role AD group names (Settings keys ad_group_administrator
    /// / ad_group_programmer / ad_group_naladchik) against the AD identity supplied via
    /// login/password, highest privilege first — наладчик last, since it's the most numerous/default
    /// role and a login that happens to sit in both an admin/programmer group and a naladchik group
    /// should resolve to the more privileged one. Call only after <see cref="ValidateAdCredentials"/>
    /// has already succeeded. Returns null if none match or none are configured — the caller then
    /// falls back to the app's own roster (see AppUserAuthService in AntarusPoFinder.Core.Services).</summary>
    public static string? DetectRoleForUser(ConfigService cfg, string domain, string login, string password)
    {
        try
        {
            using var ctx = new PrincipalContext(ContextType.Domain, domain, login, password);
            using var user = UserPrincipal.FindByIdentity(ctx, login);
            if (user is null) return null;

            foreach (var role in new[] { "administrator", "programmer", "naladchik" })
            {
                var group = cfg.Get($"ad_group_{role}");
                if (string.IsNullOrWhiteSpace(group)) continue;
                try
                {
                    using var groupPrincipal = GroupPrincipal.FindByIdentity(ctx, group);
                    if (groupPrincipal is not null && user.IsMemberOf(groupPrincipal))
                        return role;
                }
                catch { /* this specific group lookup failed — try the next role */ }
            }
            return null;
        }
        // The AD bind/user lookup itself failed here (domain became unreachable moments after
        // ValidateAdCredentials already succeeded, or lookup denied by AD permissions) — same "return
        // null" contract as "no group matched"/"none configured": the caller (App/RoleSwitchDialog)
        // falls back to the app's own roster either way, so there's nothing extra to distinguish here.
        catch
        {
            return null;
        }
    }
}
