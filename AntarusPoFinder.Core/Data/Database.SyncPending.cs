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

    /// <summary>К какому объекту относится правка — id прошивки числом, «param:12» у файла
    /// параметров, пусто у правки справочника. По нему отбирается то, что умеет уехать узким каналом
    /// без администратора (см. MainWindowViewModel.SendPendingChangesNow).</summary>
    public string Subject { get; set; } = "";
}

public partial class Database
{
    /// <summary>subject — к какому объекту относится правка (для правок прошивки — её FwVersionId в
    /// виде строки), чтобы карточка выдачи точечно показала «правки этой прошивки ещё не на диске».
    /// Пусто — правка без привязки к конкретной прошивке (тип/подтип/контроллер и т.п.).</summary>
    public void AddSyncPendingChange(string changeType, string description, string author, string subject = "") =>
        ExecuteNonQuery("INSERT INTO sync_pending_changes(ts, author, change_type, description, subject) VALUES(@t,@a,@ty,@d,@s)", cmd =>
        {
            cmd.Parameters.AddWithValue("@t", NowIso());
            cmd.Parameters.AddWithValue("@a", author);
            cmd.Parameters.AddWithValue("@ty", changeType);
            cmd.Parameters.AddWithValue("@d", description);
            cmd.Parameters.AddWithValue("@s", subject ?? "");
        });

    /// <summary>Множество subject'ов ещё не отправленных правок (пустые отброшены) — по нему карточка
    /// выдачи решает, подсвечивать ли «правки этой прошивки ещё не на диске». Копится, пока
    /// «Отправить всё» не очистит накопитель (ClearSyncPendingChanges после успешного экспорта).</summary>
    public HashSet<string> GetPendingSubjectKeys()
    {
        var result = new HashSet<string>();
        using var r = ExecuteReader("SELECT DISTINCT subject FROM sync_pending_changes WHERE subject <> ''");
        while (r.Read())
            result.Add(GetString(r, "subject"));
        return result;
    }

    /// <summary>Oldest first — что накопилось раньше, показывается выше в развёрнутом списке плашки.</summary>
    public List<SyncPendingChange> GetSyncPendingChanges()
    {
        var result = new List<SyncPendingChange>();
        using var r = ExecuteReader("SELECT id, ts, author, change_type, description, subject FROM sync_pending_changes ORDER BY id");
        while (r.Read())
            result.Add(new SyncPendingChange
            {
                Id = r.GetInt32(0), Ts = GetString(r, "ts"), Author = GetString(r, "author"),
                ChangeType = GetString(r, "change_type"), Description = GetString(r, "description"),
                Subject = GetString(r, "subject"),
            });
        return result;
    }

    public int SyncPendingChangeCount() =>
        ExecuteScalar("SELECT COUNT(*) FROM sync_pending_changes") is long l ? (int)l : 0;

    /// <summary>Вызывается ConfigSyncService после каждого успешного полного экспорта — снимок по
    /// определению уносит на диск ВСЁ текущее состояние этой машины, значит и всё, что накопилось
    /// здесь, уже отправлено.</summary>
    public void ClearSyncPendingChanges() => ExecuteNonQuery("DELETE FROM sync_pending_changes");

    /// <summary>Снимает из накопителя правки ПО НАЗВАННЫМ объектам — то, что зовут после успешной
    /// отправки прошивки узким каналом (ConfigSyncService.PushFirmwareChange). Полного экспорта при
    /// этом не было, и остальное в очереди (правки справочника, которые узкий канал не переносит)
    /// обязано в ней остаться: плашка «N изменений не отправлено» должна называть ровно то, что
    /// действительно не отправлено, иначе она врёт в одну или в другую сторону.
    ///
    /// Сравнение subject'ов — точное и по байтам. Это не имена из справочника, а машинные ключи
    /// («37», «param:12»), собранные нами же; регистронезависимость тут не нужна и опасна ровно по
    /// той же причине, по какой она подводит с кириллицей (см. правило про COLLATE NOCASE).</summary>
    public int ClearSyncPendingChangesForSubjects(IEnumerable<string> subjects)
    {
        var cleared = 0;
        foreach (var subject in subjects.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal))
            cleared += ExecuteNonQuery("DELETE FROM sync_pending_changes WHERE subject = @s",
                cmd => cmd.Parameters.AddWithValue("@s", subject));
        return cleared;
    }
}
