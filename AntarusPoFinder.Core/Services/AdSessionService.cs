using System;
using AntarusPoFinder.Core.Data;

namespace AntarusPoFinder.Core.Services;

/// <summary>"Как долго не переспрашивать AD-логин на этом компьютере" — a personal choice made by
/// whoever just typed their password in, not an organization-wide policy: <see cref="Default"/>
/// defers to the administrator's configured default (see ConfigService.AdRequireLoginDefaultDays),
/// <see cref="Custom"/> is an explicit number of days this one person picked for themself (e.g. a
/// programmer's own home laptop set to 30 days instead of the office default), and
/// <see cref="Always"/> never expires until someone logs in again explicitly — deliberately the
/// least secure option, surfaced with a warning in the login UI rather than hidden away.</summary>
public enum AdSessionMode { Default, Custom, Always }

public record AdLoginSession(string AdLogin, AdSessionMode Mode, int CustomDays, string ValidUntil);

/// <summary>Backs the mandatory-AD-login-at-startup gate (Настройки → Общие → «Требовать вход по
/// AD при запуске») — per-machine, per-login caching of "this login already proved who they are
/// recently enough" so the gate doesn't ask for a password on every single launch. Never stores the
/// password itself, only the fact and the expiry. See AntarusPoFinder.App.Views.
/// AdStartupLoginDialog/RoleSwitchDialog for the two places a session gets recorded, and
/// AntarusPoFinder.App.App.OnStartup for where it's checked.</summary>
public static class AdSessionService
{
    private const string IsoFormat = "yyyy-MM-ddTHH:mm:ss";

    public static bool IsValid(Database db, string normalizedLogin, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(normalizedLogin)) return false;
        var session = db.GetAdLoginSession(normalizedLogin);
        if (session is null) return false;
        if (session.Mode == AdSessionMode.Always) return true;
        return !string.IsNullOrEmpty(session.ValidUntil)
            && DateTime.TryParseExact(session.ValidUntil, IsoFormat, null, System.Globalization.DateTimeStyles.None, out var until)
            && now <= until;
    }

    /// <summary><paramref name="defaultDays"/> is read from ConfigService at the moment of login and
    /// baked into the resulting valid_until — if the administrator changes the default later, that
    /// only affects logins recorded after the change, not ones already cached (same as any other
    /// cache expiry: changing the policy doesn't retroactively rewrite timestamps already issued).</summary>
    public static void RecordLogin(Database db, string normalizedLogin, AdSessionMode mode, int customDays, int defaultDays, DateTime now)
    {
        var validUntil = mode == AdSessionMode.Always
            ? ""
            : now.AddDays(Math.Max(0, mode == AdSessionMode.Custom ? customDays : defaultDays)).ToString(IsoFormat);
        db.SaveAdLoginSession(normalizedLogin, mode, Math.Max(0, customDays), validUntil);
    }
}
