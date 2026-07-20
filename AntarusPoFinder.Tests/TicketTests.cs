using System;
using System.IO;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Covers the Core.Data.Database half of the Тикеты feature — the event-file read/write
/// side (TicketSyncService) lives in AntarusPoFinder.App, which this test project deliberately
/// doesn't reference (it's a net8.0-windows WPF exe, same reason no other App service has a test
/// here either) — so this only exercises what every machine's local apply ultimately runs through:
/// idempotent "create", and last-writer-wins "status change" by timestamp.</summary>
public class TicketTests
{
    private static string NewTempDb() => Path.Combine(Path.GetTempPath(), $"antarus_ticket_test_{Guid.NewGuid():N}.db");

    private static void Cleanup(params string[] dbPaths)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var db in dbPaths)
            foreach (var f in new[] { db, db + "-wal", db + "-shm" })
                if (File.Exists(f)) File.Delete(f);
    }

    private static Ticket NewTicket(string id, string status = TicketStatus.Open, string at = "2026-07-20T10:00:00.000") => new()
    {
        Id = id,
        Type = TicketType.Bug,
        Text = "тестовый тикет",
        Status = status,
        CreatedBy = "ivanov",
        CreatedByRole = "naladchik",
        CreatedAt = at,
        UpdatedAt = at,
    };

    [Fact]
    public void InsertTicketIfMissing_IsIdempotent()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            var t = NewTicket("t1");

            db.InsertTicketIfMissing(t);
            db.InsertTicketIfMissing(t); // simulates the same "create" event being applied twice

            var all = db.GetTickets();
            Assert.Single(all, x => x.Id == "t1");
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ApplyTicketStatusIfNewer_AppliesNewerEvent_IgnoresOlderOrEqual()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            db.InsertTicketIfMissing(NewTicket("t1", at: "2026-07-20T10:00:00.000"));

            var appliedNewer = db.ApplyTicketStatusIfNewer("t1", TicketStatus.InProgress, "2026-07-20T11:00:00.000");
            Assert.True(appliedNewer);
            Assert.Equal(TicketStatus.InProgress, db.GetTickets().Find(t => t.Id == "t1")!.Status);

            // An older/duplicate event (e.g. re-pulled from the share, or arriving from another
            // machine out of order) must never move status backwards.
            db.ApplyTicketStatusIfNewer("t1", TicketStatus.Open, "2026-07-20T10:30:00.000");
            Assert.Equal(TicketStatus.InProgress, db.GetTickets().Find(t => t.Id == "t1")!.Status);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ApplyTicketStatusIfNewer_UnknownTicket_ReturnsFalse_DoesNotThrow()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            // "status" event pulled before its ticket's "create" event — TicketSyncService.PullNewEvents
            // relies on this returning false-ish/no-op so it can leave the file unmarked and retry later.
            var applied = db.ApplyTicketStatusIfNewer("unknown-ticket", TicketStatus.Closed, "2026-07-20T10:00:00.000");
            Assert.False(applied);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void TicketSyncApplied_TracksProcessedEventFiles()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            Assert.False(db.IsTicketSyncFileApplied("20260720_100000000_create_abc.json"));

            db.MarkTicketSyncFileApplied("20260720_100000000_create_abc.json");
            Assert.True(db.IsTicketSyncFileApplied("20260720_100000000_create_abc.json"));

            // Marking twice must not throw (INSERT OR IGNORE).
            db.MarkTicketSyncFileApplied("20260720_100000000_create_abc.json");
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void TicketOutbox_EnqueueFlushRemove_RoundTrips()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            db.EnqueueTicketOutbox("file1.json", "{\"a\":1}");
            db.EnqueueTicketOutbox("file2.json", "{\"a\":2}");

            var pending = db.GetTicketOutbox();
            Assert.Equal(2, pending.Count);

            db.RemoveTicketOutbox("file1.json");
            var remaining = db.GetTicketOutbox();
            Assert.Single(remaining);
            Assert.Equal("file2.json", remaining[0].Filename);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void InsertTicketIfMissing_TwoDistinctTickets_BothReturnedByGetTickets()
    {
        // Regression test: two DIFFERENT tickets (different ids, different authors) must never
        // collapse into one when read back — reported live as "2 tickets exist, only 1 shown".
        // Root cause investigation (live re-test + a synthetic two-different-machine sync repro)
        // found GetTickets()/InsertTicketIfMissing itself always returns/stores both correctly; the
        // reproducible part of the report was actually the DataGrid display (an extremely long
        // ticket text visually dominating the row — see TicketsGrid's ElementStyle/TicketDetailDialog
        // fix), not a data-layer collapse. This test locks in the data-layer half regardless.
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            var t1 = new Ticket { Id = "t1", Type = TicketType.Bug, Text = "мсмчсмчс", Status = TicketStatus.Closed, CreatedBy = "Ilia", CreatedByRole = "administrator", CreatedAt = "2026-07-20T19:44:35.547", UpdatedAt = "2026-07-20T19:44:43.563" };
            var t2 = new Ticket { Id = "t2", Type = TicketType.Bug, Text = new string('А', 2000), Status = TicketStatus.Open, CreatedBy = "Ilia", CreatedByRole = "administrator", CreatedAt = "2026-07-20T19:46:03.849", UpdatedAt = "2026-07-20T19:46:03.849" };
            db.InsertTicketIfMissing(t1);
            db.InsertTicketIfMissing(t2);
            db.InsertTicketIfMissing(t1); // re-applying the same "create" event must stay idempotent, not duplicate

            var all = db.GetTickets();
            Assert.Equal(2, all.Count); // both distinct tickets present — never collapses to 1
            Assert.Contains(all, t => t.Id == "t1" && t.Status == TicketStatus.Closed);
            Assert.Contains(all, t => t.Id == "t2" && t.Text.Length == 2000);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void GetTickets_OrdersNewestFirst()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            db.InsertTicketIfMissing(NewTicket("old", at: "2026-07-01T10:00:00.000"));
            db.InsertTicketIfMissing(NewTicket("new", at: "2026-07-20T10:00:00.000"));

            var all = db.GetTickets();
            Assert.Equal("new", all[0].Id);
            Assert.Equal("old", all[1].Id);
        }
        finally { Cleanup(path); }
    }
}
