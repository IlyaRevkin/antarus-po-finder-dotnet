using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AntarusPoFinder.Core.Data;

/// <summary>One field of a held-back hierarchy conflict, "my value" vs "the value on disk" — see
/// Database.GetPendingHierarchyConflicts. Whole-row resolution only (per the operator's approved
/// design), these are for DISPLAY, not picked individually.</summary>
public class HierarchyConflictFieldDiff
{
    [JsonPropertyName("field")] public string FieldLabel { get; set; } = "";
    [JsonPropertyName("local")] public string LocalValue { get; set; } = "";
    [JsonPropertyName("incoming")] public string IncomingValue { get; set; } = "";
}

/// <summary>A single hierarchy row held back because both the local copy and the incoming one were
/// edited since they last agreed — the "Конфликты синхронизации" dialog shows one of these per row,
/// full field set on both sides, and lets the operator keep one whole row or the other.</summary>
public class HierarchyConflictItem
{
    public string SyncId { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string DisplayLabel { get; set; } = "";
    public List<HierarchyConflictFieldDiff> Fields { get; set; } = new();
}

public partial class Database
{
    // ── Watermarks — "last time this specific row was confirmed in sync between machines" ──────

    private string GetHierarchyWatermark(string syncId)
    {
        if (string.IsNullOrEmpty(syncId)) return "";
        return ExecuteScalar("SELECT last_synced_at FROM hierarchy_sync_watermarks WHERE sync_id=@s",
            cmd => cmd.Parameters.AddWithValue("@s", syncId)) as string ?? "";
    }

    private void SetHierarchyWatermark(string syncId, string timestamp)
    {
        if (string.IsNullOrEmpty(syncId) || string.IsNullOrEmpty(timestamp)) return;
        ExecuteNonQuery("""
            INSERT INTO hierarchy_sync_watermarks(sync_id, last_synced_at) VALUES(@s,@t)
            ON CONFLICT(sync_id) DO UPDATE SET last_synced_at=excluded.last_synced_at
            """, cmd => { cmd.Parameters.AddWithValue("@s", syncId); cmd.Parameters.AddWithValue("@t", timestamp); });
    }

    private string GetLastResolvedIncomingAt(string syncId)
    {
        if (string.IsNullOrEmpty(syncId)) return "";
        return ExecuteScalar("SELECT last_resolved_incoming_at FROM hierarchy_sync_watermarks WHERE sync_id=@s",
            cmd => cmd.Parameters.AddWithValue("@s", syncId)) as string ?? "";
    }

    /// <summary>Records that a conflict for this sync_id was just resolved against an incoming
    /// snapshot timestamped <paramref name="resolvedIncomingUpdatedAt"/> — see
    /// ClassifyHierarchyChange's early-out, which is what actually stops the SAME still-unresolved
    /// incoming value from re-triggering on the next sync.</summary>
    private void MarkHierarchyConflictResolved(string syncId, string timestamp, string resolvedIncomingUpdatedAt)
    {
        if (string.IsNullOrEmpty(syncId) || string.IsNullOrEmpty(timestamp)) return;
        ExecuteNonQuery("""
            INSERT INTO hierarchy_sync_watermarks(sync_id, last_synced_at, last_resolved_incoming_at) VALUES(@s,@t,@r)
            ON CONFLICT(sync_id) DO UPDATE SET last_synced_at=excluded.last_synced_at, last_resolved_incoming_at=excluded.last_resolved_incoming_at
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@s", syncId);
            cmd.Parameters.AddWithValue("@t", timestamp);
            cmd.Parameters.AddWithValue("@r", resolvedIncomingUpdatedAt);
        });
    }

    /// <summary>Decides what a field-level difference on an already-matched hierarchy row means:
    /// a genuine two-sided conflict (both changed independently since they last agreed), a normal
    /// forward update (only the incoming side moved), or "keep local" (only the local side moved,
    /// or neither side's edit is traceable — see below). This is the fix for the confirmed bug where
    /// ImportHierarchyDataCore used to apply the incoming snapshot on ANY field difference, with no
    /// regard for which side actually changed or when.
    ///
    /// Conflict = local.updated_at AND incoming.updated_at are both strictly newer than the watermark
    /// (the last point this row's sync_id was reconciled on this machine — see SetHierarchyWatermark).
    /// A missing sync_id (older export, never round-tripped) has no watermark history at all, so it
    /// falls back to the pre-fix behavior (always apply incoming) rather than inventing a conflict
    /// with no evidence either side actually diverged. Same fallback applies when NEITHER timestamp
    /// is newer than the watermark but the fields still differ anyway — that only happens when an
    /// edit bypassed the updated_at-stamping Database methods entirely (raw SQL, or data written by an
    /// older app version that predates this column); with no timestamp evidence to reason from, this
    /// keeps the old last-exporter-wins behavior rather than silently freezing a real difference
    /// forever.</summary>
    private (bool Conflict, bool ApplyIncoming) ClassifyHierarchyChange(string syncId, string localUpdatedAt, string incomingUpdatedAt)
    {
        if (string.IsNullOrEmpty(syncId)) return (false, true);

        // Already ruled on this exact incoming snapshot (or an older one) before — see
        // MarkHierarchyConflictResolved. Checked BEFORE the watermark math below because that math is
        // wall-clock-based with only 1-second resolution: a resolution can legitimately land in the
        // same clock-second as an earlier, unrelated reconcile of this row, which the timestamp
        // comparison alone can't tell apart from "nothing happened". This check is unambiguous — it
        // only fires for the literal incoming value the operator already saw and decided on.
        var lastResolved = GetLastResolvedIncomingAt(syncId);
        if (!string.IsNullOrEmpty(lastResolved) && !string.IsNullOrEmpty(incomingUpdatedAt) &&
            string.CompareOrdinal(incomingUpdatedAt, lastResolved) <= 0)
            return (false, false);

        var watermark = GetHierarchyWatermark(syncId);
        var localChanged = !string.IsNullOrEmpty(localUpdatedAt) && string.CompareOrdinal(localUpdatedAt, watermark) > 0;
        var incomingChanged = !string.IsNullOrEmpty(incomingUpdatedAt) && string.CompareOrdinal(incomingUpdatedAt, watermark) > 0;

        if (localChanged && incomingChanged) return (true, false);
        if (incomingChanged) return (false, true);   // only the incoming side moved forward
        if (localChanged) return (false, false);      // only local moved — don't clobber with stale incoming
        return (false, true);                          // neither traceable — legacy fallback
    }

    // ── Pending conflicts — persisted so they survive an app restart before being resolved ─────

    private void RecordPendingConflict(string entityType, string syncId, int localId, string displayLabel, string localJson, string incomingJson) =>
        ExecuteNonQuery("""
            INSERT INTO hierarchy_pending_conflicts(sync_id, entity_type, local_id, display_label, local_json, incoming_json, created_at)
            VALUES(@sy,@et,@id,@dl,@lj,@ij,@c)
            ON CONFLICT(sync_id) DO UPDATE SET entity_type=excluded.entity_type, local_id=excluded.local_id,
                display_label=excluded.display_label, local_json=excluded.local_json, incoming_json=excluded.incoming_json,
                created_at=excluded.created_at
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@sy", syncId);
            cmd.Parameters.AddWithValue("@et", entityType);
            cmd.Parameters.AddWithValue("@id", localId);
            cmd.Parameters.AddWithValue("@dl", displayLabel);
            cmd.Parameters.AddWithValue("@lj", localJson);
            cmd.Parameters.AddWithValue("@ij", incomingJson);
            cmd.Parameters.AddWithValue("@c", NowIso());
        });

    public int PendingHierarchyConflictCount() =>
        ExecuteScalar("SELECT COUNT(*) FROM hierarchy_pending_conflicts") is long l ? (int)l : 0;

    /// <summary>Everything currently held back, oldest-detected first, ready for the "Конфликты
    /// синхронизации" dialog — one item per row, full field set on both sides.</summary>
    public List<HierarchyConflictItem> GetPendingHierarchyConflicts()
    {
        var result = new List<HierarchyConflictItem>();
        using var r = ExecuteReader("SELECT sync_id, entity_type, display_label, local_json, incoming_json FROM hierarchy_pending_conflicts ORDER BY created_at");
        while (r.Read())
        {
            var entityType = r.GetString(1);
            result.Add(new HierarchyConflictItem
            {
                SyncId = r.GetString(0),
                EntityType = entityType,
                DisplayLabel = r.GetString(2),
                Fields = BuildFieldDiffs(entityType, r.GetString(3), r.GetString(4)),
            });
        }
        return result;
    }

    private static List<HierarchyConflictFieldDiff> BuildFieldDiffs(string entityType, string localJson, string incomingJson)
    {
        var fields = new List<HierarchyConflictFieldDiff>();
        void Add(string label, string local, string incoming) => fields.Add(new HierarchyConflictFieldDiff { FieldLabel = label, LocalValue = local, IncomingValue = incoming });

        switch (entityType)
        {
            case "group":
            {
                var l = JsonSerializer.Deserialize<ExportedGroup>(localJson)!;
                var inc = JsonSerializer.Deserialize<ExportedGroup>(incomingJson)!;
                Add("Название", l.Name, inc.Name);
                Add("Префикс", l.Prefix.ToString(), inc.Prefix.ToString());
                Add("Порядок", l.SortOrder.ToString(), inc.SortOrder.ToString());
                break;
            }
            case "subtype":
            {
                var l = JsonSerializer.Deserialize<ExportedSubType>(localJson)!;
                var inc = JsonSerializer.Deserialize<ExportedSubType>(incomingJson)!;
                Add("Группа", l.GroupName, inc.GroupName);
                Add("Название", l.Name, inc.Name);
                Add("Префикс", l.Prefix.ToString(), inc.Prefix.ToString());
                Add("Папка", l.FolderName, inc.FolderName);
                Add("Порядок", l.SortOrder.ToString(), inc.SortOrder.ToString());
                break;
            }
            case "controller":
            {
                var l = JsonSerializer.Deserialize<ExportedController>(localJson)!;
                var inc = JsonSerializer.Deserialize<ExportedController>(incomingJson)!;
                Add("Название", l.Name, inc.Name);
                Add("Порядок", l.SortOrder.ToString(), inc.SortOrder.ToString());
                break;
            }
            case "modification":
            {
                var l = JsonSerializer.Deserialize<ExportedModification>(localJson)!;
                var inc = JsonSerializer.Deserialize<ExportedModification>(incomingJson)!;
                Add("Контроллер", l.ControllerName, inc.ControllerName);
                Add("Название", l.DisplayName, inc.DisplayName);
                Add("Аппаратная версия", l.HwVersion.ToString(), inc.HwVersion.ToString());
                Add("Описание", l.Description, inc.Description);
                Add("Порядок", l.SortOrder.ToString(), inc.SortOrder.ToString());
                break;
            }
        }
        return fields;
    }

    /// <summary>Applies the operator's whole-row choice for one held-back conflict: <paramref
    /// name="keepIncoming"/> writes the disk copy's values over the local row, false leaves the local
    /// row exactly as-is. Either way, updated_at is bumped and the watermark advanced to now, so this
    /// exact conflict doesn't resurface on the next sync (per the approved design) — and the pending
    /// row is cleared. Best-effort: if the row this conflict refers to (or, for a subtype/modification,
    /// its parent group/controller) was deleted in the meantime, the pending entry is still cleared
    /// rather than getting stuck forever.</summary>
    public void ResolveHierarchyConflict(string syncId, bool keepIncoming)
    {
        (string EntityType, int LocalId, string IncomingJson)? row = null;
        using (var r = ExecuteReader("SELECT entity_type, local_id, incoming_json FROM hierarchy_pending_conflicts WHERE sync_id=@sy",
            cmd => cmd.Parameters.AddWithValue("@sy", syncId)))
            if (r.Read()) row = (r.GetString(0), r.GetInt32(1), r.GetString(2));

        if (row is null) return;
        var (entityType, localId, incomingJson) = row.Value;
        var now = NowIso();

        if (keepIncoming)
        {
            switch (entityType)
            {
                case "group":
                {
                    var inc = JsonSerializer.Deserialize<ExportedGroup>(incomingJson)!;
                    ExecuteNonQuery("UPDATE equipment_groups SET name=@n, prefix=@p, sort_order=@s, updated_at=@u WHERE id=@id", cmd =>
                    {
                        cmd.Parameters.AddWithValue("@n", inc.Name); cmd.Parameters.AddWithValue("@p", inc.Prefix);
                        cmd.Parameters.AddWithValue("@s", inc.SortOrder); cmd.Parameters.AddWithValue("@u", now);
                        cmd.Parameters.AddWithValue("@id", localId);
                    });
                    break;
                }
                case "subtype":
                {
                    var inc = JsonSerializer.Deserialize<ExportedSubType>(incomingJson)!;
                    var wantFolder = string.IsNullOrEmpty(inc.FolderName) ? inc.Name : inc.FolderName;
                    ExecuteNonQuery("UPDATE equipment_subtypes SET name=@n, prefix=@p, folder_name=@f, sort_order=@s, updated_at=@u WHERE id=@id", cmd =>
                    {
                        cmd.Parameters.AddWithValue("@n", inc.Name); cmd.Parameters.AddWithValue("@p", inc.Prefix);
                        cmd.Parameters.AddWithValue("@f", wantFolder); cmd.Parameters.AddWithValue("@s", inc.SortOrder);
                        cmd.Parameters.AddWithValue("@u", now); cmd.Parameters.AddWithValue("@id", localId);
                    });
                    break;
                }
                case "controller":
                {
                    var inc = JsonSerializer.Deserialize<ExportedController>(incomingJson)!;
                    ExecuteNonQuery("UPDATE controller_models SET name=@n, prefix=@p, sort_order=@s, updated_at=@u WHERE id=@id", cmd =>
                    {
                        cmd.Parameters.AddWithValue("@n", inc.Name); cmd.Parameters.AddWithValue("@p", inc.Prefix);
                        cmd.Parameters.AddWithValue("@s", inc.SortOrder); cmd.Parameters.AddWithValue("@u", now);
                        cmd.Parameters.AddWithValue("@id", localId);
                    });
                    break;
                }
                case "modification":
                {
                    var inc = JsonSerializer.Deserialize<ExportedModification>(incomingJson)!;
                    ExecuteNonQuery("UPDATE controller_modifications SET display_name=@n, hw_version=@h, sort_order=@s, description=@d, updated_at=@u WHERE id=@id", cmd =>
                    {
                        cmd.Parameters.AddWithValue("@n", inc.DisplayName); cmd.Parameters.AddWithValue("@h", inc.HwVersion);
                        cmd.Parameters.AddWithValue("@s", inc.SortOrder); cmd.Parameters.AddWithValue("@d", inc.Description);
                        cmd.Parameters.AddWithValue("@u", now); cmd.Parameters.AddWithValue("@id", localId);
                    });
                    break;
                }
            }
        }
        else
        {
            // Keep local as-is — just bump updated_at so THIS local value now reads as "confirmed
            // current" and doesn't look stale (and therefore re-flaggable) on the next sync.
            var table = entityType switch
            {
                "group" => "equipment_groups",
                "subtype" => "equipment_subtypes",
                "controller" => "controller_models",
                "modification" => "controller_modifications",
                _ => null,
            };
            if (table is not null)
                ExecuteNonQuery($"UPDATE {table} SET updated_at=@u WHERE id=@id", cmd =>
                { cmd.Parameters.AddWithValue("@u", now); cmd.Parameters.AddWithValue("@id", localId); });
        }

        // The incoming.updated_at that was actually part of THIS conflict — read generically (every
        // Exported* type carries the same "updated_at" JSON property) so the resolved-marker doesn't
        // need its own per-entity-type switch. This is what stops the SAME still-unchanged incoming
        // snapshot from re-triggering (as either a new conflict or a silent re-apply) on the next sync
        // — see ClassifyHierarchyChange's early-out.
        var resolvedIncomingUpdatedAt = "";
        try
        {
            using var doc = JsonDocument.Parse(incomingJson);
            if (doc.RootElement.TryGetProperty("updated_at", out var prop) && prop.ValueKind == JsonValueKind.String)
                resolvedIncomingUpdatedAt = prop.GetString() ?? "";
        }
        catch (JsonException) { /* malformed/legacy payload — no marker, falls back to normal watermark math */ }

        MarkHierarchyConflictResolved(syncId, now, resolvedIncomingUpdatedAt);
        ExecuteNonQuery("DELETE FROM hierarchy_pending_conflicts WHERE sync_id=@sy", cmd => cmd.Parameters.AddWithValue("@sy", syncId));
    }
}
