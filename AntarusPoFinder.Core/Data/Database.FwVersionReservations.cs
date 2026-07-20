using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Data;

/// <summary>
/// Version-reservation flow: a programmer locks in the next sw_version BEFORE building the
/// firmware binary (the number has to be embedded in the firmware before it's compiled — see
/// the colleague's requirement this feature was built for), then later the upload picks that
/// exact reservation instead of recomputing MAX+1, guaranteeing the saved file's number matches
/// what's baked into the binary even if someone else uploaded in between.
/// </summary>
public partial class Database
{
    /// <summary>Highest sw_version ever reserved for this combo — regardless of status. Factored
    /// out of GetNextSwVersion (Database.FwVersions.cs) so the live preview and new reservations
    /// never suggest a number someone else already reserved. Deliberately NOT filtered to
    /// status='reserved': a cancelled reservation's number must still never be handed out again
    /// (see CancelReservation — "cancelled numbers are just skipped forever"), and a fulfilled
    /// reservation's number is already covered by its fw_versions row anyway, so including it here
    /// too is redundant but harmless.</summary>
    private int GetReservedMaxSwVersion(int subtypeId, int controllerId, int hwVersion)
    {
        int max = 0;
        using var reader = ExecuteReader("""
            SELECT version_raw FROM fw_version_reservations
            WHERE subtype_id=@s AND controller_id=@c AND hw_version=@h
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@s", subtypeId);
            cmd.Parameters.AddWithValue("@c", controllerId);
            cmd.Parameters.AddWithValue("@h", hwVersion);
        });
        while (reader.Read())
        {
            var parsed = FwVersionNumber.Parse(GetString(reader, "version_raw"));
            if (parsed is not null && parsed.SwVersion > max) max = parsed.SwVersion;
        }
        return max;
    }

    /// <summary>Reserves the next sw_version atomically-in-spirit: computed as MAX across both
    /// active fw_versions AND active reservations for this (subtype, controller, hw_version), so
    /// two concurrent reservations (or a reservation racing a live non-reserved upload) never
    /// collide. Builds the FwVersionNumber with DateTime.Now (unless includeDate is false — the
    /// manager decided the date/time stamp isn't actually needed) and stores its exact Raw string.
    /// ttlHours (null = never expires) is resolved by the caller — normally ConfigService.
    /// ReservationTtlHours(), or a value the programmer typed in to override it for just this one
    /// reservation (see UploadView.ReserveVersion_Click) — this method just stamps expires_at from
    /// whatever it's given.</summary>
    public FwVersionReservation ReserveNextVersion(int subtypeId, int controllerId, int hwVersion,
        int eqPrefix, int subPrefix, string reservedBy, bool includeDate = true, int? ttlHours = null)
    {
        int nextSw = GetNextSwVersion(subtypeId, controllerId, hwVersion);
        var fwv = FwVersionNumber.Build(eqPrefix, subPrefix, hwVersion, nextSw, includeDate: includeDate);
        var reservedAt = NowIso();
        var expiresAt = ttlHours is > 0 ? IsoPlusHours(reservedAt, ttlHours.Value) : "";

        ExecuteNonQuery("""
            INSERT INTO fw_version_reservations
               (subtype_id, controller_id, hw_version, version_raw, status, reserved_by, reserved_at, expires_at)
            VALUES (@s, @c, @h, @v, 'reserved', @by, @at, @exp)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@s", subtypeId);
            cmd.Parameters.AddWithValue("@c", controllerId);
            cmd.Parameters.AddWithValue("@h", hwVersion);
            cmd.Parameters.AddWithValue("@v", fwv.Raw);
            cmd.Parameters.AddWithValue("@by", reservedBy);
            cmd.Parameters.AddWithValue("@at", reservedAt);
            cmd.Parameters.AddWithValue("@exp", expiresAt);
        });
        var id = ExecuteScalar("SELECT last_insert_rowid()");

        return new FwVersionReservation
        {
            Id = id is long l ? (int)l : -1,
            SubtypeId = subtypeId,
            ControllerId = controllerId,
            HwVersion = hwVersion,
            VersionRaw = fwv.Raw,
            Status = "reserved",
            ReservedBy = reservedBy,
            ReservedAt = reservedAt,
            ExpiresAt = expiresAt,
        };
    }

    /// <summary>Must produce the exact same "yyyy-MM-dd HH:mm:ss" format as NowIso() — expiry is
    /// checked via plain string comparison (expires_at &lt; @now in ExpireStaleReservations), which
    /// only sorts chronologically if every stored timestamp uses that identical, fixed-width shape.</summary>
    private static string IsoPlusHours(string isoNow, int hours) =>
        System.DateTime.ParseExact(isoNow, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
            .AddHours(hours).ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>Auto-cancels any open reservation past its expires_at — same "number is skipped
    /// forever, never reused" philosophy as an explicit CancelReservation. Called periodically from
    /// MainWindowViewModel.RunSync(), not on a dedicated timer — that tick already runs on
    /// sync_interval_min and touching the DB an extra time there is cheap. asOfIso lets tests
    /// simulate "later" without sleeping — production callers always omit it.</summary>
    public int ExpireStaleReservations(string? asOfIso = null)
    {
        var now = asOfIso ?? NowIso();
        var staleIds = new List<int>();
        using (var r = ExecuteReader("""
            SELECT id FROM fw_version_reservations
            WHERE status='reserved' AND expires_at <> '' AND expires_at < @now
            """, cmd => cmd.Parameters.AddWithValue("@now", now)))
            while (r.Read())
                staleIds.Add(r.GetInt32(0));

        foreach (var id in staleIds)
            CancelReservation(id);

        return staleIds.Count;
    }

    /// <summary>Open (status='reserved') reservations for this exact combo — feeds the
    /// upload-time "use a reservation" picker.</summary>
    public List<FwVersionReservation> GetActiveReservations(int subtypeId, int controllerId, int hwVersion)
    {
        var result = new List<FwVersionReservation>();
        using var reader = ExecuteReader("""
            SELECT * FROM fw_version_reservations
            WHERE subtype_id=@s AND controller_id=@c AND hw_version=@h AND status='reserved'
            ORDER BY reserved_at DESC
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@s", subtypeId);
            cmd.Parameters.AddWithValue("@c", controllerId);
            cmd.Parameters.AddWithValue("@h", hwVersion);
        });
        while (reader.Read())
            result.Add(ReadReservation(reader));
        return result;
    }

    /// <summary>All open reservations across every combo, joined with hierarchy names — feeds the
    /// Settings management list.</summary>
    public List<FwVersionReservation> GetAllOpenReservations()
    {
        const string sql = """
            SELECT r.*, eg.name AS group_name, es.name AS subtype_name, cm.name AS ctrl_name
            FROM fw_version_reservations r
            JOIN equipment_subtypes es ON r.subtype_id   = es.id
            JOIN equipment_groups   eg ON es.group_id    = eg.id
            JOIN controller_models  cm ON r.controller_id = cm.id
            WHERE r.status = 'reserved'
            ORDER BY r.reserved_at DESC
            """;
        var result = new List<FwVersionReservation>();
        using var reader = ExecuteReader(sql);
        while (reader.Read())
        {
            var rec = ReadReservation(reader);
            rec.GroupName = GetString(reader, "group_name");
            rec.SubtypeName = GetString(reader, "subtype_name");
            rec.CtrlName = GetString(reader, "ctrl_name");
            result.Add(rec);
        }
        return result;
    }

    /// <summary>Marks a reservation as consumed by an actual upload.</summary>
    public void FulfillReservation(int reservationId, int fwVersionId) =>
        ExecuteNonQuery("UPDATE fw_version_reservations SET status='fulfilled', fulfilled_fw_version_id=@f WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@f", fwVersionId);
            cmd.Parameters.AddWithValue("@id", reservationId);
        });

    /// <summary>Cancels a reservation. Cancelled numbers are never reused/renumbered — they're
    /// simply skipped forever, same philosophy as a rolled-back fw_version.</summary>
    public void CancelReservation(int reservationId) =>
        ExecuteNonQuery("UPDATE fw_version_reservations SET status='cancelled' WHERE id=@id",
            cmd => cmd.Parameters.AddWithValue("@id", reservationId));

    private static FwVersionReservation ReadReservation(SqliteDataReader r) => new()
    {
        Id = GetInt(r, "id"),
        SubtypeId = GetInt(r, "subtype_id"),
        ControllerId = GetInt(r, "controller_id"),
        HwVersion = GetInt(r, "hw_version"),
        VersionRaw = GetString(r, "version_raw"),
        Status = GetString(r, "status", "reserved"),
        ReservedBy = GetString(r, "reserved_by"),
        ReservedAt = GetString(r, "reserved_at"),
        FulfilledFwVersionId = GetIntOrNull(r, "fulfilled_fw_version_id"),
        ExpiresAt = GetString(r, "expires_at"),
    };
}
