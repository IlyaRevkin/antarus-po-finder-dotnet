using System.Collections.Generic;

namespace AntarusPoFinder.Core.Data;

/// <summary>Одно изменение справочника, накопленное этой машиной и ещё не отправленное на общий
/// диск — то, что показывает плашка «Изменений готово к отправке: N» (см.
/// MainWindowViewModel.PushCatalogChange и AntarusPoFinder.App.Services.ConfigSyncService.Export,
/// который очищает накопитель после успешной отправки). Machine-local: таблица sync_pending_changes
/// никогда не входит в общий конфиг и не приезжает с других машин (нет в HierarchyExportData).</summary>
public class SyncPendingChange
{
    public int Id { get; set; }
    public string Ts { get; set; } = "";
    public string Author { get; set; } = "";
    public string ChangeType { get; set; } = "";
    public string Description { get; set; } = "";
}

public partial class Database
{
    public void AddSyncPendingChange(string changeType, string description, string author) =>
        ExecuteNonQuery("INSERT INTO sync_pending_changes(ts, author, change_type, description) VALUES(@t,@a,@ty,@d)", cmd =>
        {
            cmd.Parameters.AddWithValue("@t", NowIso());
            cmd.Parameters.AddWithValue("@a", author);
            cmd.Parameters.AddWithValue("@ty", changeType);
            cmd.Parameters.AddWithValue("@d", description);
        });

    /// <summary>Oldest first — что накопилось раньше, показывается выше в развёрнутом списке плашки.</summary>
    public List<SyncPendingChange> GetSyncPendingChanges()
    {
        var result = new List<SyncPendingChange>();
        using var r = ExecuteReader("SELECT id, ts, author, change_type, description FROM sync_pending_changes ORDER BY id");
        while (r.Read())
            result.Add(new SyncPendingChange
            {
                Id = r.GetInt32(0), Ts = GetString(r, "ts"), Author = GetString(r, "author"),
                ChangeType = GetString(r, "change_type"), Description = GetString(r, "description"),
            });
        return result;
    }

    public int SyncPendingChangeCount() =>
        ExecuteScalar("SELECT COUNT(*) FROM sync_pending_changes") is long l ? (int)l : 0;

    /// <summary>Вызывается ConfigSyncService после каждого успешного полного экспорта — снимок по
    /// определению уносит на диск ВСЁ текущее состояние этой машины, значит и всё, что накопилось
    /// здесь, уже отправлено.</summary>
    public void ClearSyncPendingChanges() => ExecuteNonQuery("DELETE FROM sync_pending_changes");
}
