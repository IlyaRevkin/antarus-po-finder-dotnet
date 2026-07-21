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

    /// <summary>Merges an incoming AD roster into the local table — natural key is ad_login
    /// (COLLATE NOCASE), matched by sync_id first like every other entity in Database.ConfigExchange,
    /// falling back to login for first contact. Role is last-writer-wins by role_updated_at IN EITHER
    /// DIRECTION — unlike reservations/fw_versions this is not a one-way "only ever advances" state
    /// machine, an administrator can promote OR demote — so a role changed more recently must win
    /// regardless of which side (local vs incoming) made the more recent change. first_login_at keeps
    /// whichever side's is earlier (a historical fact, never moved backwards); last_login_at keeps
    /// whichever is later (most recent login witnessed by any machine). Nobody is ever removed from
    /// the roster via sync. Shared by the full hierarchy import AND by <see cref="MergeAppUsersOnly"/>
    /// — the latter lets a non-administrator machine contribute its own roster entry to the shared
    /// config without the (admin-only) risk of pushing a full hierarchy snapshot, see
    /// ConfigSyncService.PushAppUsersOnly.</summary>
    private void MergeAppUsersInto(List<ExportedAppUser> incoming, ImportCounts counts, bool apply)
    {
        foreach (var u in incoming)
        {
            if (string.IsNullOrWhiteSpace(u.AdLogin)) continue;

            var existing = FindAppUserBySyncOrLogin(u.SyncId, u.AdLogin);
            if (existing is null)
            {
                counts.AppUsersAdded++;
                if (apply)
                {
                    var sync = string.IsNullOrEmpty(u.SyncId) ? Guid.NewGuid().ToString() : u.SyncId;
                    ExecuteNonQuery("""
                        INSERT INTO app_users(ad_login, role, first_login_at, last_login_at, role_updated_at, sync_id)
                        VALUES(@l,@r,@f,@la,@ru,@sy)
                        """, cmd =>
                    {
                        cmd.Parameters.AddWithValue("@l", u.AdLogin); cmd.Parameters.AddWithValue("@r", u.Role);
                        cmd.Parameters.AddWithValue("@f", u.FirstLoginAt); cmd.Parameters.AddWithValue("@la", u.LastLoginAt);
                        cmd.Parameters.AddWithValue("@ru", u.RoleUpdatedAt); cmd.Parameters.AddWithValue("@sy", sync);
                    });
                }
                continue;
            }

            var incomingRoleWins = string.CompareOrdinal(u.RoleUpdatedAt, existing.RoleUpdatedAt) > 0;
            var wantRole = incomingRoleWins ? u.Role : existing.Role;
            var wantRoleUpdatedAt = incomingRoleWins ? u.RoleUpdatedAt : existing.RoleUpdatedAt;
            var wantFirst = string.IsNullOrEmpty(existing.FirstLoginAt) ||
                (!string.IsNullOrEmpty(u.FirstLoginAt) && string.CompareOrdinal(u.FirstLoginAt, existing.FirstLoginAt) < 0)
                ? u.FirstLoginAt : existing.FirstLoginAt;
            var wantLast = string.CompareOrdinal(u.LastLoginAt, existing.LastLoginAt) > 0 ? u.LastLoginAt : existing.LastLoginAt;
            var adoptSyncId = !string.IsNullOrEmpty(u.SyncId) && u.SyncId != existing.SyncId;

            var changed = wantRole != existing.Role || wantFirst != existing.FirstLoginAt || wantLast != existing.LastLoginAt || adoptSyncId;
            if (!changed) continue;

            if (wantRole != existing.Role) counts.AppUsersUpdated++;
            if (!apply) continue;

            ExecuteNonQuery("UPDATE app_users SET role=@r, first_login_at=@f, last_login_at=@la, role_updated_at=@ru, sync_id=@sy WHERE id=@id", cmd =>
            {
                cmd.Parameters.AddWithValue("@r", wantRole); cmd.Parameters.AddWithValue("@f", wantFirst);
                cmd.Parameters.AddWithValue("@la", wantLast); cmd.Parameters.AddWithValue("@ru", wantRoleUpdatedAt);
                cmd.Parameters.AddWithValue("@id", existing.Id!.Value);
                cmd.Parameters.AddWithValue("@sy", adoptSyncId ? u.SyncId : existing.SyncId);
            });
        }
    }

    /// <summary>Public narrow entry point for a non-administrator's "push just my roster entry"
    /// (see ConfigSyncService.PushAppUsersOnly) — merges an incoming roster (read from the shared
    /// config file) into the local table using the exact same rule as a full hierarchy import,
    /// without touching any other table.</summary>
    public ImportCounts MergeAppUsersOnly(List<ExportedAppUser> incoming)
    {
        var counts = new ImportCounts();
        MergeAppUsersInto(incoming, counts, apply: true);
        return counts;
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
