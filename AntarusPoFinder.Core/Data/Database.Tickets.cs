using System;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    public bool TicketExists(string id) =>
        ExecuteScalar("SELECT 1 FROM tickets WHERE id=@id", cmd => cmd.Parameters.AddWithValue("@id", id)) is not null;

    /// <summary>Applies a "create" ticket event — a no-op if the ticket already exists locally
    /// (whether created here originally, or a "create" event for it was already applied from an
    /// earlier pull). Tickets never change type/text/author after creation, only status.</summary>
    public void InsertTicketIfMissing(Ticket t)
    {
        if (TicketExists(t.Id)) return;
        ExecuteNonQuery("""
            INSERT INTO tickets(id, ticket_type, text, status, created_by, created_by_role, created_at, updated_at)
            VALUES(@id, @type, @text, @status, @by, @role, @created, @updated)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@id", t.Id);
            cmd.Parameters.AddWithValue("@type", t.Type);
            cmd.Parameters.AddWithValue("@text", t.Text);
            cmd.Parameters.AddWithValue("@status", t.Status);
            cmd.Parameters.AddWithValue("@by", t.CreatedBy);
            cmd.Parameters.AddWithValue("@role", t.CreatedByRole);
            cmd.Parameters.AddWithValue("@created", t.CreatedAt);
            cmd.Parameters.AddWithValue("@updated", t.UpdatedAt);
        });
    }

    /// <summary>Applies a "status changed" ticket event — last-writer-wins by the event's own
    /// timestamp (<paramref name="updatedAt"/>), so pulling events out of order (or from several
    /// machines) still converges to the same final status everywhere. Silently does nothing if the
    /// ticket's "create" event hasn't been applied locally yet — see TicketSyncService.PullNewEvents,
    /// which only marks a status file as processed once that's true, so it's retried on a later pull
    /// rather than being lost.</summary>
    public bool ApplyTicketStatusIfNewer(string ticketId, string status, string updatedAt)
    {
        var current = ExecuteScalar("SELECT updated_at FROM tickets WHERE id=@id", cmd => cmd.Parameters.AddWithValue("@id", ticketId)) as string;
        if (current is null) return false;
        if (string.CompareOrdinal(updatedAt, current) <= 0) return true; // known but stale — already applied, don't retry forever

        ExecuteNonQuery("UPDATE tickets SET status=@s, updated_at=@u WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@s", status);
            cmd.Parameters.AddWithValue("@u", updatedAt);
            cmd.Parameters.AddWithValue("@id", ticketId);
        });
        return true;
    }

    public List<Ticket> GetTickets()
    {
        var result = new List<Ticket>();
        using var r = ExecuteReader("SELECT id, ticket_type, text, status, created_by, created_by_role, created_at, updated_at FROM tickets ORDER BY created_at DESC");
        while (r.Read())
            result.Add(new Ticket
            {
                Id = r.GetString(0),
                Type = GetString(r, "ticket_type", TicketType.Other),
                Text = GetString(r, "text"),
                Status = GetString(r, "status", TicketStatus.Open),
                CreatedBy = GetString(r, "created_by"),
                CreatedByRole = GetString(r, "created_by_role"),
                CreatedAt = GetString(r, "created_at"),
                UpdatedAt = GetString(r, "updated_at"),
            });
        return result;
    }

    /// <summary>Применяет тикет, приехавший из хранилища на хостинге (TicketStorageSync).
    ///
    /// Тикета нет — заводим целиком. Есть — меняем ТОЛЬКО статус, и только если чужая отметка
    /// времени позже нашей. Текст, тип и автор не переписываются никогда, потому что и не меняются:
    /// тикет после создания правят одним-единственным способом — сменой статуса (то же правило, что
    /// у журнала на сетевом диске, см. InsertTicketIfMissing). Приедь сюда чужой текст — на двух
    /// машинах разошлись бы две редакции одной жалобы, и «кто прав» решал бы порядок обмена.
    ///
    /// Возвращает true, если у нас что-то поменялось, — по этому счёту показывается «получено».</summary>
    public bool ApplyRemoteTicket(Ticket t)
    {
        if (string.IsNullOrWhiteSpace(t.Id)) return false;

        var current = ExecuteScalar("SELECT updated_at FROM tickets WHERE id=@id",
            cmd => cmd.Parameters.AddWithValue("@id", t.Id)) as string;
        if (current is null)
        {
            InsertTicketIfMissing(t);
            return true;
        }

        if (string.CompareOrdinal(t.UpdatedAt, current) <= 0) return false;

        var status = ExecuteScalar("SELECT status FROM tickets WHERE id=@id",
            cmd => cmd.Parameters.AddWithValue("@id", t.Id)) as string ?? "";
        ExecuteNonQuery("UPDATE tickets SET status=@s, updated_at=@u WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@s", t.Status);
            cmd.Parameters.AddWithValue("@u", t.UpdatedAt);
            cmd.Parameters.AddWithValue("@id", t.Id);
        });
        // Отметка времени подвинулась, а статус тот же (обмен по кругу, повторная выкладка) — для
        // человека не поменялось ничего, и в счётчик «получено» это попадать не должно.
        return !string.Equals(status, t.Status, StringComparison.Ordinal);
    }

    // ── Sync bookkeeping ─────────────────────────────────────────────────────

    // ── Переписка по тикету ──────────────────────────────────────────────────

    /// <summary>Реплики тикета по времени. Порядок — по created_at, а при одинаковом времени по id:
    /// две реплики, написанные в одну секунду на разных машинах, должны лечь в один и тот же порядок
    /// у всех, иначе переписка выглядит по-разному в разных копиях.</summary>
    public List<TicketComment> GetTicketComments(string ticketId)
    {
        var list = new List<TicketComment>();
        using var r = ExecuteReader(
            "SELECT id, ticket_id, author, author_role, text, created_at FROM ticket_comments WHERE ticket_id=@t ORDER BY created_at, id",
            cmd => cmd.Parameters.AddWithValue("@t", ticketId));
        while (r.Read())
            list.Add(new TicketComment
            {
                Id = r.GetString(0), TicketId = r.GetString(1), Author = r.GetString(2),
                AuthorRole = r.GetString(3), Text = r.GetString(4), CreatedAt = r.GetString(5),
            });
        return list;
    }

    /// <summary>Положить реплику, если такой ещё нет. Возвращает true, если появилась новая.
    ///
    /// INSERT OR IGNORE, а не «проверить и вставить»: та же реплика приезжает по двум каналам —
    /// с сетевого диска и из хранилища, — и гонка между ними не должна ни падать, ни задваивать.</summary>
    public bool AddTicketCommentIfMissing(TicketComment c)
    {
        if (string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.TicketId)) return false;
        var before = ExecuteScalar("SELECT COUNT(*) FROM ticket_comments WHERE id=@id",
            cmd => cmd.Parameters.AddWithValue("@id", c.Id)) is long n && n > 0;
        if (before) return false;

        ExecuteNonQuery("""
            INSERT OR IGNORE INTO ticket_comments(id, ticket_id, author, author_role, text, created_at)
            VALUES(@id, @t, @a, @r, @x, @c)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@id", c.Id);
            cmd.Parameters.AddWithValue("@t", c.TicketId);
            cmd.Parameters.AddWithValue("@a", c.Author ?? "");
            cmd.Parameters.AddWithValue("@r", c.AuthorRole ?? "");
            cmd.Parameters.AddWithValue("@x", c.Text ?? "");
            cmd.Parameters.AddWithValue("@c", c.CreatedAt ?? "");
        });
        return true;
    }

    /// <summary>Написать реплику здесь, у себя. В отличие от приёма чужой, ДВИГАЕТ updated_at самого
    /// тикета — и это не косметика, а то, на чём держится перенос переписки: обмен решает, отдавать
    /// ли объект, сравнивая updated_at с тем, что видел в хранилище. Не двинь мы его, реплика
    /// осталась бы лежать только на этой машине.</summary>
    public TicketComment AddTicketComment(string ticketId, string author, string authorRole, string text)
    {
        var comment = new TicketComment
        {
            Id = Guid.NewGuid().ToString(),
            TicketId = ticketId,
            Author = author ?? "",
            AuthorRole = authorRole ?? "",
            Text = (text ?? "").Trim(),
            CreatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
        };
        AddTicketCommentIfMissing(comment);
        ExecuteNonQuery("UPDATE tickets SET updated_at=@u WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@u", comment.CreatedAt);
            cmd.Parameters.AddWithValue("@id", ticketId);
        });
        return comment;
    }

    public bool IsTicketSyncFileApplied(string filename) =>
        ExecuteScalar("SELECT 1 FROM ticket_sync_applied WHERE filename=@f", cmd => cmd.Parameters.AddWithValue("@f", filename)) is not null;

    public void MarkTicketSyncFileApplied(string filename) =>
        ExecuteNonQuery("INSERT OR IGNORE INTO ticket_sync_applied(filename) VALUES(@f)", cmd => cmd.Parameters.AddWithValue("@f", filename));

    /// <summary>Events this machine created but hasn't yet managed to write to the shared drive
    /// (share was unreachable at the time — see TicketSyncService.FlushOutbox). Flushed opportunistically
    /// every time the Тикеты page is opened, not just at the moment of creation, so a temporarily
    /// offline naladchik PC still eventually gets its tickets/status changes onto the share.</summary>
    public void EnqueueTicketOutbox(string filename, string payload) =>
        ExecuteNonQuery("INSERT OR REPLACE INTO ticket_outbox(filename, payload) VALUES(@f, @p)", cmd =>
        {
            cmd.Parameters.AddWithValue("@f", filename);
            cmd.Parameters.AddWithValue("@p", payload);
        });

    public List<(string Filename, string Payload)> GetTicketOutbox()
    {
        var result = new List<(string, string)>();
        using var r = ExecuteReader("SELECT filename, payload FROM ticket_outbox ORDER BY filename");
        while (r.Read())
            result.Add((r.GetString(0), r.GetString(1)));
        return result;
    }

    public void RemoveTicketOutbox(string filename) =>
        ExecuteNonQuery("DELETE FROM ticket_outbox WHERE filename=@f", cmd => cmd.Parameters.AddWithValue("@f", filename));

    // ── Хранилище на хостинге: что мы там в последний раз видели ─────────────
    // Таблица ticket_storage_seen, см. её описание в Database.cs и TicketStorageSync.

    public TicketStorageSeen? GetTicketStorageSeen(string objectKey)
    {
        using var r = ExecuteReader(
            "SELECT obj_key, ticket_id, remote_updated_at, remote_tag FROM ticket_storage_seen WHERE obj_key=@k",
            cmd => cmd.Parameters.AddWithValue("@k", objectKey));
        if (!r.Read()) return null;
        return new TicketStorageSeen(r.GetString(0), GetString(r, "ticket_id"),
            GetString(r, "remote_updated_at"), GetString(r, "remote_tag"));
    }

    public void SaveTicketStorageSeen(string objectKey, string ticketId, string remoteUpdatedAt, string remoteTag) =>
        ExecuteNonQuery("""
            INSERT INTO ticket_storage_seen(obj_key, ticket_id, remote_updated_at, remote_tag)
            VALUES(@k, @id, @u, @tag)
            ON CONFLICT(obj_key) DO UPDATE SET ticket_id=@id, remote_updated_at=@u, remote_tag=@tag
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@k", objectKey);
            cmd.Parameters.AddWithValue("@id", ticketId);
            cmd.Parameters.AddWithValue("@u", remoteUpdatedAt);
            cmd.Parameters.AddWithValue("@tag", remoteTag);
        });
}

/// <summary>Состояние объекта тикета в хранилище на момент последнего удачного обмена.</summary>
public sealed record TicketStorageSeen(string ObjectKey, string TicketId, string RemoteUpdatedAt, string RemoteTag);
