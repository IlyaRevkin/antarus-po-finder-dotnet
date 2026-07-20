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
    public static bool ValidateAdCredentials(string domain, string login, string password, out string? error)
    {
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
    /// / ad_group_programmer) against the AD identity supplied via login/password, highest
    /// privilege first. Call only after <see cref="ValidateAdCredentials"/> has already succeeded.
    /// Returns null if none match or none are configured.</summary>
    public static string? DetectRoleForUser(ConfigService cfg, string domain, string login, string password)
    {
        try
        {
            using var ctx = new PrincipalContext(ContextType.Domain, domain, login, password);
            using var user = UserPrincipal.FindByIdentity(ctx, login);
            if (user is null) return null;

            foreach (var role in new[] { "administrator", "programmer" })
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
        catch
        {
            return null;
        }
    }
}
