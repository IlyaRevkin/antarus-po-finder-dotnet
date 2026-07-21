using AntarusPoFinder.Core.Services;
using Microsoft.Data.Sqlite;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    /// <summary>Per-machine only (never exported/merged — see ConfigSyncService.SkipKeys and
    /// Database.ConfigExchange, neither of which touches this table): how long THIS computer
    /// should trust a successful AD login for THIS ad_login before asking again. Keyed on the
    /// normalized login rather than app_users.id so it survives that roster row not existing yet
    /// (AD-group role path never creates one) and needs no foreign key into a table that does sync.</summary>
    public AdLoginSession? GetAdLoginSession(string normalizedLogin)
    {
        using var r = ExecuteReader(
            "SELECT ad_login, mode, custom_days, valid_until FROM ad_login_sessions WHERE ad_login=@l COLLATE NOCASE",
            cmd => cmd.Parameters.AddWithValue("@l", normalizedLogin));
        return r.Read() ? ReadAdLoginSession(r) : null;
    }

    /// <summary>Upsert — one row per login per machine. Called on every successful AD login (both
    /// the optional in-app "switch role" dialog and the mandatory startup gate), regardless of
    /// whether the mandatory gate is even enabled, so turning it on later immediately benefits from
    /// whatever "remember me" choice was already on file for that login.</summary>
    public void SaveAdLoginSession(string normalizedLogin, AdSessionMode mode, int customDays, string validUntil)
    {
        ExecuteNonQuery("""
            INSERT INTO ad_login_sessions(ad_login, mode, custom_days, valid_until)
            VALUES(@l, @m, @c, @v)
            ON CONFLICT(ad_login) DO UPDATE SET mode=@m, custom_days=@c, valid_until=@v
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@l", normalizedLogin);
            cmd.Parameters.AddWithValue("@m", mode.ToString().ToLowerInvariant());
            cmd.Parameters.AddWithValue("@c", customDays);
            cmd.Parameters.AddWithValue("@v", validUntil);
        });
    }

    private static AdLoginSession ReadAdLoginSession(SqliteDataReader r) => new(
        r.GetString(0),
        System.Enum.TryParse<AdSessionMode>(r.GetString(1), true, out var mode) ? mode : AdSessionMode.Default,
        r.GetInt32(2),
        r.GetString(3));
}
