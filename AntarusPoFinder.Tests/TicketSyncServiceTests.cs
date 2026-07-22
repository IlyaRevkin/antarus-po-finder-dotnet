using System;
using System.IO;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Regression coverage for the failedCount out-param added to TicketSyncService.FlushOutbox/
/// PullNewEvents (see MainWindowViewModel/TicketsView task notes on catch blocks that used to swallow
/// a per-file sync failure with zero trace — same bug class as the app auto-update Round 35 incident).
/// Before this, a share that stayed unreachable/a corrupt event file meant tickets silently never
/// synced with nothing anywhere to show for it; these tests lock in that a failure is now counted and
/// reported back to the caller instead of vanishing into a bare `catch { }`.</summary>
public class TicketSyncServiceTests
{
    [Fact]
    public void FlushOutbox_FileCannotBeWritten_CountsFailureButLeavesEventQueued()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var ticket = new Ticket
        {
            Id = Guid.NewGuid().ToString(), Type = TicketType.Bug, Text = "flush-failure test",
            Status = TicketStatus.Open, CreatedBy = "profileA", CreatedByRole = "programmer",
            CreatedAt = "2026-07-21T10:00:00.000", UpdatedAt = "2026-07-21T10:00:00.000",
        };
        m.DbA.InsertTicketIfMissing(ticket);
        var (filename, payload) = TicketSyncService.BuildCreateEvent(ticket);
        m.DbA.EnqueueTicketOutbox(filename, payload);

        // Sabotage the write: pre-create a DIRECTORY at the exact path FlushOutbox would move the
        // event file to, so File.Move(tmp, finalPath) throws instead of succeeding — simulates a
        // locked/unwritable share without needing a real second process holding a file handle.
        var ticketsDir = TicketSyncService.TicketsDir(root);
        Directory.CreateDirectory(Path.Combine(ticketsDir, filename));

        var sent = TicketSyncService.FlushOutbox(m.SvcA, root, out var failedCount);

        Assert.Equal(0, sent);
        Assert.Equal(1, failedCount);
        // Failed event must stay in the outbox for the next retry, not get dropped.
        Assert.Single(m.DbA.GetTicketOutbox());
    }

    [Fact]
    public void FlushOutbox_NoFailures_ReportsZero()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var ticket = new Ticket
        {
            Id = Guid.NewGuid().ToString(), Type = TicketType.Bug, Text = "flush-success test",
            Status = TicketStatus.Open, CreatedBy = "profileA", CreatedByRole = "programmer",
            CreatedAt = "2026-07-21T10:00:00.000", UpdatedAt = "2026-07-21T10:00:00.000",
        };
        m.DbA.InsertTicketIfMissing(ticket);
        var (filename, payload) = TicketSyncService.BuildCreateEvent(ticket);
        m.DbA.EnqueueTicketOutbox(filename, payload);

        var sent = TicketSyncService.FlushOutbox(m.SvcA, root, out var failedCount);

        Assert.Equal(1, sent);
        Assert.Equal(0, failedCount);
        Assert.Empty(m.DbA.GetTicketOutbox());
    }

    [Fact]
    public void PullNewEvents_CorruptEventFile_CountsFailureAndSkipsIt_ValidEventsStillApplied()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        // A valid event from "machine A" ...
        var ticket = new Ticket
        {
            Id = Guid.NewGuid().ToString(), Type = TicketType.Bug, Text = "pull test",
            Status = TicketStatus.Open, CreatedBy = "profileA", CreatedByRole = "programmer",
            CreatedAt = "2026-07-21T10:00:00.000", UpdatedAt = "2026-07-21T10:00:00.000",
        };
        m.DbA.InsertTicketIfMissing(ticket);
        var (filename, payload) = TicketSyncService.BuildCreateEvent(ticket);
        m.DbA.EnqueueTicketOutbox(filename, payload);
        TicketSyncService.FlushOutbox(m.SvcA, root, out var flushFailed);
        Assert.Equal(0, flushFailed);

        // ... alongside a corrupt/truncated event file (simulates a write caught mid-transfer, or a
        // genuinely malformed payload) sitting in the same shared "Конфиг/tickets" folder.
        var ticketsDir = TicketSyncService.TicketsDir(root);
        var corruptPath = Path.Combine(ticketsDir, "20260721_100500000_create_corrupt.json");
        File.WriteAllText(corruptPath, "{ not valid json ");

        var applied = TicketSyncService.PullNewEvents(m.SvcB, root, out var failedCount);

        Assert.Equal(1, applied); // the valid ticket still landed
        Assert.Equal(1, failedCount); // the corrupt one was counted, not silently ignored
        Assert.Contains(m.DbB.GetTickets(), t => t.Id == ticket.Id);
        // The corrupt file must not be marked as applied — it should be retried on the next pull
        // (e.g. once the writing machine finishes/fixes it), not permanently skipped.
        Assert.False(m.DbB.IsTicketSyncFileApplied("20260721_100500000_create_corrupt.json"));
    }
}
