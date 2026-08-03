using System.Collections.Generic;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    /// <summary>Записывает один перенос версии прошивки на другую модель контроллера в журнал (см.
    /// таблицу ctrl_reassign_log и ExportedCtrlReassign). Зовётся ПОСЛЕ успешного
    /// HierarchyService.ReassignFwVersionToController на машине, где оператор менял контроллер:
    /// журнал уезжает в общий конфиг и проигрывается остальными машинами как ПЕРЕНОС (запись + папка
    /// на диске), а не как «удалили строку под старым контроллером и завели новую под новым».</summary>
    public void RecordCtrlReassign(ExportedCtrlReassign e) =>
        ExecuteNonQuery("""
            INSERT INTO ctrl_reassign_log(subtype_sync_id, subtype_name, group_name,
                old_controller_sync_id, old_controller_name, new_controller_sync_id, new_controller_name,
                version_raw, ts, author)
            VALUES(@ss,@sn,@gn,@os,@on,@ns,@nn,@vr,@ts,@a)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@ss", e.SubtypeSyncId ?? "");
            cmd.Parameters.AddWithValue("@sn", e.SubtypeName ?? "");
            cmd.Parameters.AddWithValue("@gn", e.GroupName ?? "");
            cmd.Parameters.AddWithValue("@os", e.OldControllerSyncId ?? "");
            cmd.Parameters.AddWithValue("@on", e.OldControllerName ?? "");
            cmd.Parameters.AddWithValue("@ns", e.NewControllerSyncId ?? "");
            cmd.Parameters.AddWithValue("@nn", e.NewControllerName ?? "");
            cmd.Parameters.AddWithValue("@vr", e.VersionRaw ?? "");
            cmd.Parameters.AddWithValue("@ts", e.Ts ?? "");
            cmd.Parameters.AddWithValue("@a", e.Author ?? "");
        });

    /// <summary>Последние записи журнала переносов для выгрузки в общий конфиг, по возрастанию
    /// отметки времени — ровно как GetRecentHwRewrites.</summary>
    public List<ExportedCtrlReassign> GetRecentCtrlReassigns(int limit = 200)
    {
        var recent = new List<ExportedCtrlReassign>();
        using (var r = ExecuteReader("""
            SELECT subtype_sync_id, subtype_name, group_name, old_controller_sync_id, old_controller_name,
                   new_controller_sync_id, new_controller_name, version_raw, ts, author
            FROM ctrl_reassign_log ORDER BY id DESC LIMIT @lim
            """, cmd => cmd.Parameters.AddWithValue("@lim", limit)))
            while (r.Read())
                recent.Add(new ExportedCtrlReassign
                {
                    SubtypeSyncId = GetString(r, "subtype_sync_id"),
                    SubtypeName = GetString(r, "subtype_name"),
                    GroupName = GetString(r, "group_name"),
                    OldControllerSyncId = GetString(r, "old_controller_sync_id"),
                    OldControllerName = GetString(r, "old_controller_name"),
                    NewControllerSyncId = GetString(r, "new_controller_sync_id"),
                    NewControllerName = GetString(r, "new_controller_name"),
                    VersionRaw = GetString(r, "version_raw"),
                    Ts = GetString(r, "ts"),
                    Author = GetString(r, "author"),
                });
        recent.Reverse();
        return recent;
    }

    /// <summary>sync_id подтипа по локальному id — чтобы записать перенос переносимо (на разных
    /// машинах локальные id подтипов разные). Пустая строка, если подтипа нет.</summary>
    public string GetSubtypeSyncId(int subtypeId) =>
        ExecuteScalar("SELECT sync_id FROM equipment_subtypes WHERE id=@id",
            cmd => cmd.Parameters.AddWithValue("@id", subtypeId)) as string ?? "";

    /// <summary>Локальный id подтипа по sync_id, с запасным резолвом по паре «тип шкафа + имя
    /// подтипа». Запасной путь нужен по той же причине, что GetControllerIdByName у hw-переписывания:
    /// sync_id приёмник перенимает у отправителя лишь ВНУТРИ ImportHierarchyData, а проигрывание
    /// событий идёт раньше него — на самом первом контакте по sync_id подтип ещё не находится.
    /// Имена подтипов уникальны в пределах типа шкафа (по ним же приёмник и перенимает sync_id).</summary>
    public int? GetSubtypeIdBySyncIdOrName(string syncId, string groupName, string subtypeName)
    {
        if (!string.IsNullOrEmpty(syncId) &&
            ExecuteScalar("SELECT id FROM equipment_subtypes WHERE sync_id=@s",
                cmd => cmd.Parameters.AddWithValue("@s", syncId)) is long bySync)
            return (int)bySync;

        if (string.IsNullOrEmpty(subtypeName)) return null;
        var byName = ExecuteScalar("""
            SELECT es.id FROM equipment_subtypes es JOIN equipment_groups eg ON es.group_id = eg.id
            WHERE eg.name=@g AND es.name=@n
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@g", groupName ?? "");
            cmd.Parameters.AddWithValue("@n", subtypeName);
        });
        return byName is long l ? (int)l : null;
    }
}
