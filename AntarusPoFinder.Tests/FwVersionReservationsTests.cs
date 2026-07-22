using System;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Database.FwVersionReservations had zero test coverage before this, despite a real field
/// bug (Раунд 26): expires_at was written/compared with inconsistent timestamp formats ('T' separator
/// vs the space NowIso() actually uses), so the plain string comparison in ExpireStaleReservations
/// ('expires_at &lt; @now') almost never fired — reservations that were supposed to expire just never
/// did. These tests lock in the fixed behavior: creation, TTL expiry (including a same-format
/// regression guard), the countdown data ExpiresAt feeds (Раунд 39), and fulfillment.</summary>
public class FwVersionReservationsTests
{
    /// <summary>Owns both the TempDb (file cleanup) and the Database (connection) so every test can
    /// get a ready-seeded (subtype, controller, hw_version) combo with a single `using var s =
    /// Seed();` instead of repeating the TempDb+Database+lookup boilerplate. Disposes the Database
    /// connection before the underlying TempDb file cleanup — same ordering TempDb's own doc comment
    /// calls out as required.</summary>
    private sealed class SeedContext : IDisposable
    {
        public required TempDb DbFile { get; init; }
        public required Database Db { get; init; }
        public int SubtypeId { get; init; }
        public int ControllerId { get; init; }
        public int HwVersion { get; init; }
        public int EqPrefix { get; init; }
        public int SubPrefix { get; init; }

        public void Dispose()
        {
            Db.Dispose();
            DbFile.Dispose();
        }
    }

    private static SeedContext Seed()
    {
        var dbFile = new TempDb();
        var db = new Database(dbFile.Path);
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name != "—");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "PIXEL");
        return new SeedContext
        {
            DbFile = dbFile, Db = db, SubtypeId = subtype.Id!.Value, ControllerId = mod.ControllerId,
            HwVersion = mod.HwVersion, EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
        };
    }

    [Fact]
    public void ReserveNextVersion_CreatesOpenReservation_WithComputedRawVersion()
    {
        using var s = Seed();
        var db = s.Db;

        var reservation = db.ReserveNextVersion(s.SubtypeId, s.ControllerId, s.HwVersion,
            s.EqPrefix, s.SubPrefix, "ivanov", includeDate: false, ttlHours: 72);

        Assert.True(reservation.Id > 0);
        Assert.Equal("reserved", reservation.Status);
        Assert.Equal("ivanov", reservation.ReservedBy);
        Assert.NotEmpty(reservation.VersionRaw);
        Assert.NotEmpty(reservation.ExpiresAt);

        var active = db.GetActiveReservations(s.SubtypeId, s.ControllerId, s.HwVersion);
        Assert.Contains(active, r => r.Id == reservation.Id);
    }

    [Fact]
    public void ReserveNextVersion_TtlNull_NeverExpires()
    {
        using var s = Seed();
        var db = s.Db;

        var reservation = db.ReserveNextVersion(s.SubtypeId, s.ControllerId, s.HwVersion,
            s.EqPrefix, s.SubPrefix, "ivanov", ttlHours: null);

        Assert.Equal("", reservation.ExpiresAt);

        // Even "as of" a date far in the future, a never-expiring reservation must stay open.
        var expired = db.ExpireStaleReservations(asOfIso: "2099-01-01 00:00:00");
        Assert.Equal(0, expired);
        Assert.Contains(db.GetActiveReservations(s.SubtypeId, s.ControllerId, s.HwVersion),
            r => r.Id == reservation.Id);
    }

    [Fact]
    public void ReserveNextVersion_SecondReservation_SkipsPastFirstReservedNumber_EvenIfNotYetUploaded()
    {
        // GetReservedMaxSwVersion must count 'reserved' (not just fw_versions), otherwise two
        // concurrent reservations for the same combo would collide on the same version number
        // before either is actually uploaded.
        using var s = Seed();
        var db = s.Db;

        var first = db.ReserveNextVersion(s.SubtypeId, s.ControllerId, s.HwVersion, s.EqPrefix, s.SubPrefix, "ivanov", includeDate: false);
        var second = db.ReserveNextVersion(s.SubtypeId, s.ControllerId, s.HwVersion, s.EqPrefix, s.SubPrefix, "petrov", includeDate: false);

        Assert.NotEqual(first.VersionRaw, second.VersionRaw);
    }

    [Fact]
    public void ExpireStaleReservations_PastExpiry_CancelsReservation_AndNeverReleasesNumberAgain()
    {
        using var s = Seed();
        var db = s.Db;

        var reservation = db.ReserveNextVersion(s.SubtypeId, s.ControllerId, s.HwVersion, s.EqPrefix, s.SubPrefix, "ivanov",
            includeDate: false, ttlHours: 1);

        // "Now" 2 hours later than reservation — same NowIso()-shaped string format the real
        // ExpireStaleReservations/reserved_at path always uses (see IsoPlusHours's doc: comparison
        // only sorts correctly if both sides share that exact "yyyy-MM-dd HH:mm:ss" shape).
        var reservedAt = DateTime.ParseExact(reservation.ReservedAt, "yyyy-MM-dd HH:mm:ss", null);
        var asOf = reservedAt.AddHours(2).ToString("yyyy-MM-dd HH:mm:ss");

        var expiredCount = db.ExpireStaleReservations(asOfIso: asOf);
        Assert.Equal(1, expiredCount);

        var active = db.GetActiveReservations(s.SubtypeId, s.ControllerId, s.HwVersion);
        Assert.DoesNotContain(active, r => r.Id == reservation.Id);

        // The cancelled number must never be handed out again — a THIRD reservation for the same
        // combo must not collide with the expired one's version_raw.
        var next = db.ReserveNextVersion(s.SubtypeId, s.ControllerId, s.HwVersion, s.EqPrefix, s.SubPrefix, "sidorov", includeDate: false);
        Assert.NotEqual(reservation.VersionRaw, next.VersionRaw);
    }

    [Fact]
    public void ExpireStaleReservations_NotYetExpired_LeavesReservationOpen()
    {
        using var s = Seed();
        var db = s.Db;

        var reservation = db.ReserveNextVersion(s.SubtypeId, s.ControllerId, s.HwVersion, s.EqPrefix, s.SubPrefix, "ivanov",
            includeDate: false, ttlHours: 72);

        var reservedAt = DateTime.ParseExact(reservation.ReservedAt, "yyyy-MM-dd HH:mm:ss", null);
        var asOf = reservedAt.AddHours(1).ToString("yyyy-MM-dd HH:mm:ss"); // well within the 72h TTL

        var expiredCount = db.ExpireStaleReservations(asOfIso: asOf);
        Assert.Equal(0, expiredCount);
        Assert.Contains(db.GetActiveReservations(s.SubtypeId, s.ControllerId, s.HwVersion), r => r.Id == reservation.Id);
    }

    /// <summary>Раунд 26 regression guard: expires_at must be written in the exact same
    /// "yyyy-MM-dd HH:mm:ss" shape ExpireStaleReservations compares against via plain string '&lt;'.
    /// A 'T' separator (typical DateTime.ToString("o")/round-trip format) sorts AFTER a space at that
    /// character position ('T' = 0x54 > ' ' = 0x20) for any same-day comparison, which is exactly how
    /// this silently never expired anything in production before the fix.</summary>
    [Fact]
    public void ExpiresAt_UsesSpaceSeparatedFormat_NotIsoTSeparator()
    {
        using var s = Seed();
        var db = s.Db;

        var reservation = db.ReserveNextVersion(s.SubtypeId, s.ControllerId, s.HwVersion, s.EqPrefix, s.SubPrefix, "ivanov",
            includeDate: false, ttlHours: 1);

        Assert.DoesNotContain('T', reservation.ExpiresAt);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", reservation.ExpiresAt);
    }

    [Fact]
    public void FulfillReservation_MarksFulfilled_RemovesFromActiveList()
    {
        using var s = Seed();
        var db = s.Db;

        var reservation = db.ReserveNextVersion(s.SubtypeId, s.ControllerId, s.HwVersion, s.EqPrefix, s.SubPrefix, "ivanov", includeDate: false);

        var userA = db.GetOrCreateUser("ivanov", "ivanov");
        var fwId = db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = s.SubtypeId, ControllerId = s.ControllerId, EqPrefix = s.EqPrefix, SubPrefix = s.SubPrefix,
            HwVersion = s.HwVersion, SwVersion = 1, DtStr = "20260101_0000", VersionRaw = reservation.VersionRaw,
            Filename = "fw.psl", DiskPath = "", Description = "fulfilling upload", Status = "active", AuthorId = userA.Id,
        });

        db.FulfillReservation(reservation.Id!.Value, fwId);

        var active = db.GetActiveReservations(s.SubtypeId, s.ControllerId, s.HwVersion);
        Assert.DoesNotContain(active, r => r.Id == reservation.Id);

        var allOpen = db.GetAllOpenReservations();
        Assert.DoesNotContain(allOpen, r => r.Id == reservation.Id);
    }

    [Fact]
    public void CancelReservation_RemovesFromActiveList_ButNumberStaysPermanentlySkipped()
    {
        using var s = Seed();
        var db = s.Db;

        var reservation = db.ReserveNextVersion(s.SubtypeId, s.ControllerId, s.HwVersion, s.EqPrefix, s.SubPrefix, "ivanov", includeDate: false);
        db.CancelReservation(reservation.Id!.Value);

        Assert.DoesNotContain(db.GetActiveReservations(s.SubtypeId, s.ControllerId, s.HwVersion), r => r.Id == reservation.Id);

        var next = db.ReserveNextVersion(s.SubtypeId, s.ControllerId, s.HwVersion, s.EqPrefix, s.SubPrefix, "petrov", includeDate: false);
        Assert.NotEqual(reservation.VersionRaw, next.VersionRaw);
    }

    [Fact]
    public void GetAllOpenReservations_JoinsHierarchyNames()
    {
        using var s = Seed();
        var db = s.Db;

        var reservation = db.ReserveNextVersion(s.SubtypeId, s.ControllerId, s.HwVersion, s.EqPrefix, s.SubPrefix, "ivanov", includeDate: false);

        var all = db.GetAllOpenReservations();
        var row = all.Single(r => r.Id == reservation.Id);
        Assert.Equal("НГР", row.GroupName);
        Assert.Equal("PIXEL", row.CtrlName);
        Assert.False(string.IsNullOrEmpty(row.SubtypeName));
    }
}
