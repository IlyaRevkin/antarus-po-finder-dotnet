using System.Collections.Generic;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    /// <summary>Отметка времени для строки журнала hw-переписывания — точная (микросекунды), чтобы
    /// две правки, сделанные оператором подряд, не слиплись в одну секунду: watermark на приёме
    /// сравнивает строго «строго новее» (см. ConfigSyncService.ReplayHwRewrites), и одинаковая отметка
    /// у двух событий потеряла бы второе. Формат совпадает с NowIsoPrecise (Database.FlatLists.cs).</summary>
    public static string NowIsoPreciseTs() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffffff");

    /// <summary>Записывает одно переписывание hw модификации контроллера в журнал (см. таблицу
    /// hw_rewrite_log и ExportedHwRewrite). Зовётся после успешного HierarchyService.
    /// RewriteControllerHwVersion на машине, где оператор правил hw — журнал уезжает в общий конфиг и
    /// проигрывается остальными машинами как операция-переименование.</summary>
    public void RecordHwRewrite(string controllerSyncId, string controllerName, int oldHw, int newHw, string ts, string author) =>
        ExecuteNonQuery(
            "INSERT INTO hw_rewrite_log(controller_sync_id,controller_name,old_hw,new_hw,ts,author) VALUES(@c,@n,@o,@w,@t,@a)",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@c", controllerSyncId ?? "");
                cmd.Parameters.AddWithValue("@n", controllerName ?? "");
                cmd.Parameters.AddWithValue("@o", oldHw);
                cmd.Parameters.AddWithValue("@w", newHw);
                cmd.Parameters.AddWithValue("@t", ts ?? "");
                cmd.Parameters.AddWithValue("@a", author ?? "");
            });

    /// <summary>Последние записи журнала hw-переписываний для выгрузки в общий конфиг, по возрастанию
    /// отметки времени. Ограничено сверху (переписывания редки, а машина, отставшая больше чем на
    /// <paramref name="limit"/> событий, всё равно получит верные fw_versions из самого снимка) —
    /// журнал не растёт бесконечно в общем конфиге.</summary>
    public List<ExportedHwRewrite> GetRecentHwRewrites(int limit = 200)
    {
        var recent = new List<ExportedHwRewrite>();
        using (var r = ExecuteReader(
            "SELECT controller_sync_id, controller_name, old_hw, new_hw, ts, author FROM hw_rewrite_log ORDER BY id DESC LIMIT @lim",
            cmd => cmd.Parameters.AddWithValue("@lim", limit)))
            while (r.Read())
                recent.Add(new ExportedHwRewrite
                {
                    ControllerSyncId = GetString(r, "controller_sync_id"),
                    ControllerName = GetString(r, "controller_name"),
                    OldHw = GetInt(r, "old_hw"),
                    NewHw = GetInt(r, "new_hw"),
                    Ts = GetString(r, "ts"),
                    Author = GetString(r, "author"),
                });
        recent.Reverse(); // отдаём по возрастанию ts (id монотонен) — так их и проигрывают на приёме
        return recent;
    }

    /// <summary>sync_id модели контроллера по локальному id — нужен, чтобы записать hw-переписывание
    /// переносимо (на разных машинах локальные id контроллеров разные). Пустая строка, если контроллер
    /// не найден или sync_id ещё не проставлен.</summary>
    public string GetControllerSyncId(int controllerId) =>
        ExecuteScalar("SELECT sync_id FROM controller_models WHERE id=@id",
            cmd => cmd.Parameters.AddWithValue("@id", controllerId)) as string ?? "";

    /// <summary>Локальный id модели контроллера по её sync_id — обратная операция для проигрывания
    /// приехавшего hw-переписывания. Null, если этот контроллер на данной машине ещё не заведён/не
    /// соотнесён (тогда fw_versions приедут корректными из самого снимка, проигрывать нечего).</summary>
    public int? GetControllerIdBySyncId(string controllerSyncId)
    {
        if (string.IsNullOrEmpty(controllerSyncId)) return null;
        return ExecuteScalar("SELECT id FROM controller_models WHERE sync_id=@s",
            cmd => cmd.Parameters.AddWithValue("@s", controllerSyncId)) is long id ? (int)id : (int?)null;
    }

    /// <summary>Локальный id контроллера по ИМЕНИ — запасной путь для проигрывания hw-переписывания,
    /// когда sync_id ещё не совпал. Это реальный, а не гипотетический случай: sync_id контроллера
    /// приёмник перенимает у отправителя внутри ImportHierarchyData, а ReplayHwRewrites выполняется
    /// РАНЬШЕ него в том же Apply (переименовать свою строку надо ДО импорта fw_versions, иначе он
    /// заведёт дубль). Значит на самой первой синхронизации, где это событие и важно, GetControllerId-
    /// BySyncId ещё вернул бы null (у приёмника пока свой, другой sync_id), переписывание бы не
    /// проиграло, а на следующем тике watermark уже ушёл вперёд — и старая строка навсегда осталась бы
    /// фантомом (ровно жалоба «папка не затирается»). Имя контроллера у всех машин одно и то же (по
    /// нему же приёмник и перенимает sync_id), поэтому оно — надёжный запасной ключ. Null, если такого
    /// имени нет.</summary>
    public int? GetControllerIdByName(string controllerName)
    {
        if (string.IsNullOrEmpty(controllerName)) return null;
        return ExecuteScalar("SELECT id FROM controller_models WHERE name=@n",
            cmd => cmd.Parameters.AddWithValue("@n", controllerName)) is long id ? (int)id : (int?)null;
    }

    /// <summary>id строки fw_versions с заданным натуральным ключом (подтип+контроллер+version_raw),
    /// не считая <paramref name="excludeId"/> — используется проигрыванием hw-переписывания, чтобы не
    /// переименовать «старую» строку в уже существующий ключ (иначе два ряда с одним version_raw). Null,
    /// если такой строки нет. Удалённые (deleted_at) не учитываются.</summary>
    /// <param name="configName">Имя конфигурации шкафа (см. столбец config_name в Database.cs), если
    /// ключ надо сверять точно, вместе с ним. null — не различать конфигурации: так работает
    /// проигрывание hw-переписывания, которому важно «есть ли ХОТЬ КАКАЯ-ТО строка с этим номером
    /// версии» (переименовывать в занятый номер нельзя в любом случае, вариант там или нет).</param>
    public int? FindFwVersionIdByNaturalKey(int subtypeId, int controllerId, string versionRaw, int excludeId,
        string? configName = null)
    {
        var sql = $"SELECT id FROM fw_versions WHERE subtype_id=@s AND controller_id=@c AND version_raw=@v AND id<>@x AND {NotDeleted()}";
        if (configName is not null) sql += " AND COALESCE(config_name,'') = @cfg";

        var found = ExecuteScalar(sql,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@s", subtypeId);
                cmd.Parameters.AddWithValue("@c", controllerId);
                cmd.Parameters.AddWithValue("@v", versionRaw ?? "");
                cmd.Parameters.AddWithValue("@x", excludeId);
                if (configName is not null) cmd.Parameters.AddWithValue("@cfg", configName);
            });
        return found is long id ? (int)id : (int?)null;
    }
}
