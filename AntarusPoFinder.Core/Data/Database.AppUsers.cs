using System;
using System.Collections.Generic;
using AntarusPoFinder.Core.Domain;
using Microsoft.Data.Sqlite;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    private const string AppUserSelectCols =
        "id, ad_login, role, first_login_at, last_login_at, role_updated_at, sync_id";

    public AppUser? FindAppUserByLogin(string normalizedLogin)
    {
        using var r = ExecuteReader($"SELECT {AppUserSelectCols} FROM app_users WHERE ad_login=@l COLLATE NOCASE",
            cmd => cmd.Parameters.AddWithValue("@l", normalizedLogin));
        return r.Read() ? ReadAppUser(r) : null;
    }

    /// <summary>First time this AD login is seen locally: creates it with the default role
    /// (Наладчик) so the operator can go straight to work — see AppUser class doc. An already-known
    /// login just gets last_login_at bumped; its role is left untouched here (see SetAppUserRole for
    /// the only thing that changes it locally).</summary>
    public AppUser TouchOrCreateAppUser(string normalizedLogin, string defaultRole = "naladchik")
    {
        var now = NowIso();
        var existing = FindAppUserByLogin(normalizedLogin);
        if (existing is null)
        {
            var syncId = Guid.NewGuid().ToString();
            ExecuteNonQuery("""
                INSERT INTO app_users(ad_login, role, first_login_at, last_login_at, role_updated_at, sync_id)
                VALUES(@l, @r, @f, @la, @ru, @sy)
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("@l", normalizedLogin);
                cmd.Parameters.AddWithValue("@r", defaultRole);
                cmd.Parameters.AddWithValue("@f", now);
                cmd.Parameters.AddWithValue("@la", now);
                cmd.Parameters.AddWithValue("@ru", now);
                cmd.Parameters.AddWithValue("@sy", syncId);
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
    /// against a role changed earlier elsewhere.</summary>
    public void SetAppUserRole(int id, string role)
    {
        ExecuteNonQuery("UPDATE app_users SET role=@r, role_updated_at=@ru WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@r", role);
            cmd.Parameters.AddWithValue("@ru", NowIso());
            cmd.Parameters.AddWithValue("@id", id);
        });
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
    };
}
