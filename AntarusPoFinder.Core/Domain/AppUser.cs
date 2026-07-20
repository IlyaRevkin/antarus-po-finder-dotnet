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
}
