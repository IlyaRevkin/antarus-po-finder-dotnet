using System.Collections.Generic;
using System.Linq;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    /// <summary>Записывает текущее состояние модерации прошивки в журнал решений — узкий канал,
    /// которым решение уезжает к остальным машинам НЕЗАВИСИМО от того, кто выгружает полный снимок
    /// (см. таблицу moderation_log, ExportedModerationDecision и
    /// ConfigSyncService.PushModerationOnly).
    ///
    /// Вызывается ПОСЛЕ того, как решение уже применено к строке (MarkFwVersionReleased,
    /// TombstoneFwVersion, откат, архивирование): метод не решает ничего сам, он лишь снимает с
    /// записи её нынешние released/archived/status/deleted_at и переводит адресацию прошивки в
    /// переносимую (sync_id подтипа и модели контроллера + version_raw). Права здесь не проверяются
    /// и не выдаются — кто МОЖЕТ модерировать, решает роль на стороне UI (см. RolesConfig: страница
    /// «Модерация прошивок» доступна наладчику и администратору), канал только доставляет уже
    /// принятое решение.
    ///
    /// Ничего не пишет, если версии нет или у неё нет переносимого ключа (пустой version_raw) —
    /// такое решение всё равно некому было бы применить на чужой машине.</summary>
    public void RecordModerationDecision(int fwVersionId, string author)
    {
        using var r = ExecuteReader("""
            SELECT fv.version_raw, fv.released, fv.archived, fv.status, fv.deleted_at,
                   eg.name AS group_name, es.name AS subtype_name, es.sync_id AS subtype_sync_id,
                   cm.name AS ctrl_name, cm.sync_id AS controller_sync_id
            FROM fw_versions fv
            JOIN equipment_subtypes es ON fv.subtype_id   = es.id
            JOIN equipment_groups   eg ON es.group_id     = eg.id
            JOIN controller_models  cm ON fv.controller_id = cm.id
            WHERE fv.id=@id
            """, cmd => cmd.Parameters.AddWithValue("@id", fwVersionId));
        if (!r.Read()) return;

        var decision = new ExportedModerationDecision
        {
            SubtypeSyncId = GetString(r, "subtype_sync_id"),
            SubtypeName = GetString(r, "subtype_name"),
            GroupName = GetString(r, "group_name"),
            ControllerSyncId = GetString(r, "controller_sync_id"),
            ControllerName = GetString(r, "ctrl_name"),
            VersionRaw = GetString(r, "version_raw"),
            Released = GetInt(r, "released"),
            Archived = GetInt(r, "archived"),
            Status = GetString(r, "status", "active"),
            DeletedAt = GetString(r, "deleted_at"),
            Ts = NowIsoPreciseTs(),
            Author = author ?? "",
        };
        if (string.IsNullOrEmpty(decision.VersionRaw)) return;
        InsertModerationDecision(decision);
    }

    private void InsertModerationDecision(ExportedModerationDecision d) =>
        ExecuteNonQuery("""
            INSERT INTO moderation_log(subtype_sync_id, subtype_name, group_name, controller_sync_id,
                controller_name, version_raw, released, archived, status, deleted_at, ts, author)
            VALUES(@ss,@sn,@gn,@cs,@cn,@vr,@rel,@arch,@st,@del,@ts,@a)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@ss", d.SubtypeSyncId ?? "");
            cmd.Parameters.AddWithValue("@sn", d.SubtypeName ?? "");
            cmd.Parameters.AddWithValue("@gn", d.GroupName ?? "");
            cmd.Parameters.AddWithValue("@cs", d.ControllerSyncId ?? "");
            cmd.Parameters.AddWithValue("@cn", d.ControllerName ?? "");
            cmd.Parameters.AddWithValue("@vr", d.VersionRaw ?? "");
            cmd.Parameters.AddWithValue("@rel", d.Released);
            cmd.Parameters.AddWithValue("@arch", d.Archived);
            cmd.Parameters.AddWithValue("@st", d.Status ?? "");
            cmd.Parameters.AddWithValue("@del", d.DeletedAt ?? "");
            cmd.Parameters.AddWithValue("@ts", d.Ts ?? "");
            cmd.Parameters.AddWithValue("@a", d.Author ?? "");
        });

    /// <summary>Последние решения модерации для выгрузки в общий конфиг, по возрастанию отметки
    /// времени. Ограничено сверху ровно как GetRecentHwRewrites: журнал не должен расти в общем
    /// конфиге бесконечно, а машина, отставшая больше чем на <paramref name="limit"/> решений, всё
    /// равно получит верное состояние из самих строк fw_versions снимка.</summary>
    public List<ExportedModerationDecision> GetRecentModerationDecisions(int limit = 500)
    {
        var recent = new List<ExportedModerationDecision>();
        using (var r = ExecuteReader("""
            SELECT subtype_sync_id, subtype_name, group_name, controller_sync_id, controller_name,
                   version_raw, released, archived, status, deleted_at, ts, author
            FROM moderation_log ORDER BY id DESC LIMIT @lim
            """, cmd => cmd.Parameters.AddWithValue("@lim", limit)))
            while (r.Read())
                recent.Add(new ExportedModerationDecision
                {
                    SubtypeSyncId = GetString(r, "subtype_sync_id"),
                    SubtypeName = GetString(r, "subtype_name"),
                    GroupName = GetString(r, "group_name"),
                    ControllerSyncId = GetString(r, "controller_sync_id"),
                    ControllerName = GetString(r, "controller_name"),
                    VersionRaw = GetString(r, "version_raw"),
                    Released = GetInt(r, "released"),
                    Archived = GetInt(r, "archived"),
                    Status = GetString(r, "status"),
                    DeletedAt = GetString(r, "deleted_at"),
                    Ts = GetString(r, "ts"),
                    Author = GetString(r, "author"),
                });
        recent.Reverse(); // по возрастанию ts (id монотонен) — так их и склеивают/применяют
        return recent;
    }

    /// <summary>Принимает чужие решения в СВОЙ журнал, чтобы эта машина пересылала их дальше своим
    /// собственным экспортом. Без этого решение жило бы только в той версии общего конфига, куда его
    /// дописал автор: ближайший полный экспорт администратора перезаписывает файл целиком, и машина,
    /// не успевшая синхронизироваться до этого момента, решения бы уже не увидела (сами строки
    /// fw_versions в снимке администратора его к тому моменту, конечно, несут — но только если
    /// администратор сам успел его применить).
    ///
    /// Дедупликация по DedupKey (в него входит ts) — одно и то же решение возвращается сюда при
    /// каждом обмене, а два РАЗНЫХ решения по одной версии обязаны остаться двумя записями.
    /// Возвращает число реально добавленных.</summary>
    public int AbsorbModerationDecisions(IEnumerable<ExportedModerationDecision> incoming)
    {
        var known = new HashSet<string>(GetRecentModerationDecisions(int.MaxValue).Select(d => d.DedupKey()), StringComparer.Ordinal);
        var added = 0;
        foreach (var d in incoming)
        {
            if (string.IsNullOrEmpty(d.VersionRaw)) continue;
            if (!known.Add(d.DedupKey())) continue;
            InsertModerationDecision(d);
            added++;
        }
        return added;
    }
}
