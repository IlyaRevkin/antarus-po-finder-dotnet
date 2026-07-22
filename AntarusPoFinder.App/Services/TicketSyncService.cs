using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App.Services;

/// <summary>Событие тикета (создание или смена статуса), одно на файл. Каждое событие — свой файл
/// в общей папке на сетевом диске (maildir-стиль), а не строки в одном общем журнале: несколько
/// компьютеров пишут независимо и одновременно, и добавление отдельных файлов не требует
/// блокировок/чтения-изменения-записи одного файла по SMB, в отличие от единого append-only лога.
/// Идемпотентно на приёме (см. Database.InsertTicketIfMissing/ApplyTicketStatusIfNewer), так что
/// повторная обработка одного и того же файла — не проблема.</summary>
public record TicketEvent(
    string EventId, string TicketId, string EventType,
    string? TicketType, string? Text, string Status,
    string CreatedBy, string CreatedByRole, string At);

/// <summary>Синхронизация тикетов между машинами через тот же сетевой «Конфиг» канал, что и
/// ConfigSyncService, но отдельным механизмом: ConfigSyncService каждый раз перезаписывает единый
/// снимок (po_finder_config.json) — годится для иерархии/настроек, где авторитетный источник один
/// (администратор жмёт «Отправить сейчас», либо периодический таймер), но тикеты создают ВСЕ роли
/// с разных машин в любой момент, и снимок с одной машины неизбежно потерял бы тикеты, ещё не
/// попавшие в её локальную БД. Вместо этого — append-only событийный лог: каждое действие
/// (создание тикета, смена статуса) сразу становится отдельным JSON-файлом в
/// root/Конфиг/tickets/, и каждая машина независимо подтягивает файлы, которые ещё не видела.</summary>
public static class TicketSyncService
{
    public static string TicketsDir(string root) => Path.Combine(root, "Конфиг", "tickets");

    /// <summary>Where a ticket's attached files live — plain files on the same shared drive the
    /// ticket events themselves sync through, not a DB blob (per the operator's explicit call: don't
    /// bloat SQLite or complicate the event-log sync with binary payloads). Deliberately NOT tracked
    /// in the event log or the Ticket record at all — the directory listing on whichever machine is
    /// looking IS the list of attachments, same as how every other shared-file feature in this app
    /// (firmware/params/schematics) already treats the network drive as the source of truth instead
    /// of mirroring a file list into SQLite. The only cost: attachments are invisible on a machine
    /// where the shared drive isn't currently reachable, same as any other disk-backed feature here.</summary>
    public static string AttachmentsDir(string root, string ticketId) => Path.Combine(TicketsDir(root), "attachments", ticketId);

    public static (string Filename, string Payload) BuildCreateEvent(Ticket t)
    {
        var ev = new TicketEvent(Guid.NewGuid().ToString(), t.Id, "create", t.Type, t.Text, t.Status, t.CreatedBy, t.CreatedByRole, t.CreatedAt);
        return (FileNameFor(ev), JsonSerializer.Serialize(ev));
    }

    public static (string Filename, string Payload) BuildStatusEvent(string ticketId, string status, string by, string role, string atIso)
    {
        var ev = new TicketEvent(Guid.NewGuid().ToString(), ticketId, "status", null, null, status, by, role, atIso);
        return (FileNameFor(ev), JsonSerializer.Serialize(ev));
    }

    private static string FileNameFor(TicketEvent ev) =>
        $"{DateTime.UtcNow:yyyyMMdd_HHmmssfff}_{ev.EventType}_{ev.EventId}.json";

    /// <summary>Writes every not-yet-sent event this machine has queued (see Database.
    /// EnqueueTicketOutbox — filled at the moment a ticket is created/its status changed) onto the
    /// shared drive. Best-effort per file: a file that fails to write (share unreachable, momentary
    /// lock) is left in the outbox and retried on the next call — see TicketsView.Activate, called
    /// every time the page is opened, not only right after the triggering action.</summary>
    public static int FlushOutbox(AppServices services, string root) => FlushOutbox(services, root, out _);

    /// <summary>Same as <see cref="FlushOutbox(AppServices,string)"/>, plus <paramref name="failedCount"/>
    /// — how many queued events failed to write this pass. A single failure is unremarkable (share
    /// hiccup, retried next call), but same as the config auto-push bug (see MainWindowViewModel.
    /// PushConfigNow): if the share stays unreachable for hours, tickets/status changes would
    /// otherwise sit in the outbox forever with zero trace anywhere. Callers that run this
    /// periodically/on page-open should surface a non-zero count to the operator instead of staying
    /// silent forever.</summary>
    public static int FlushOutbox(AppServices services, string root, out int failedCount)
    {
        failedCount = 0;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return 0;

        var dir = TicketsDir(root);
        Directory.CreateDirectory(dir);

        var sent = 0;
        foreach (var (filename, payload) in services.Db.GetTicketOutbox())
        {
            try
            {
                var finalPath = Path.Combine(dir, filename);
                if (!File.Exists(finalPath))
                {
                    // Write-then-rename: a reader listing the directory mid-write (another machine's
                    // pull, over SMB) never sees a half-written file, since the rename is atomic on
                    // the same volume.
                    var tmp = finalPath + ".tmp";
                    File.WriteAllText(tmp, payload);
                    File.Move(tmp, finalPath, overwrite: true);
                }
                services.Db.RemoveTicketOutbox(filename);
                sent++;
            }
            catch { failedCount++; /* share unreachable/locked right now — stays queued, retried next flush; caller decides whether to surface failedCount */ }
        }
        return sent;
    }

    /// <summary>Pulls and applies event files this machine hasn't processed yet. "Create" events are
    /// applied before "status" events in every pass (independent of filename order) so a status
    /// change pulled in the same batch as its ticket's creation is never skipped for arriving
    /// "before" it alphabetically. A status event for a ticket whose "create" isn't known locally yet
    /// (still queued elsewhere, or briefly out of order across two separate pulls) is left unmarked —
    /// it's retried on the next pull instead of being silently dropped.</summary>
    public static int PullNewEvents(AppServices services, string root) => PullNewEvents(services, root, out _);

    /// <summary>Same as <see cref="PullNewEvents(AppServices,string)"/>, plus <paramref name="failedCount"/>
    /// — how many event files failed to read this pass (usually another machine's write still in
    /// flight, which resolves itself on the next pull). If a file is instead permanently corrupt
    /// (truncated write that never gets fixed by anyone), it would otherwise fail every single pull
    /// forever with no trace — same class of bug as FlushOutbox's failedCount, see there.</summary>
    public static int PullNewEvents(AppServices services, string root, out int failedCount)
    {
        failedCount = 0;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return 0;

        var dir = TicketsDir(root);
        if (!Directory.Exists(dir)) return 0;

        var pending = new List<(string FileName, TicketEvent Ev)>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            var name = Path.GetFileName(file);
            if (services.Db.IsTicketSyncFileApplied(name)) continue;
            try
            {
                var ev = JsonSerializer.Deserialize<TicketEvent>(File.ReadAllText(file));
                if (ev is not null) pending.Add((name, ev));
            }
            catch { failedCount++; /* still being written by another machine right now — picked up next pull */ }
        }

        var applied = 0;
        var knownTicketIds = new HashSet<string>(services.Db.GetTickets().Select(t => t.Id));

        foreach (var (name, ev) in pending.Where(p => p.Ev.EventType == "create"))
        {
            services.Db.InsertTicketIfMissing(new Ticket
            {
                Id = ev.TicketId,
                Type = ev.TicketType ?? TicketType.Other,
                Text = ev.Text ?? "",
                Status = ev.Status,
                CreatedBy = ev.CreatedBy,
                CreatedByRole = ev.CreatedByRole,
                CreatedAt = ev.At,
                UpdatedAt = ev.At,
            });
            services.Db.MarkTicketSyncFileApplied(name);
            knownTicketIds.Add(ev.TicketId);
            applied++;
        }

        foreach (var (name, ev) in pending.Where(p => p.Ev.EventType == "status"))
        {
            if (!knownTicketIds.Contains(ev.TicketId)) continue; // create not seen yet — retry later
            services.Db.ApplyTicketStatusIfNewer(ev.TicketId, ev.Status, ev.At);
            services.Db.MarkTicketSyncFileApplied(name);
            applied++;
        }

        return applied;
    }
}
