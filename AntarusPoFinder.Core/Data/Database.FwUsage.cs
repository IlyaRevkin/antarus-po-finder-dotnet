using System;
using System.Collections.Generic;

namespace AntarusPoFinder.Core.Data;

/// <summary>«По такому запросу обычно ставят вот эту прошивку» — счётчик выбора версии из выдачи
/// поиска.
///
/// Зачем: у одного и того же шкафа находится несколько подходящих версий, и правильную наладчик
/// каждый раз выбирает заново — по памяти, а новый сотрудник вообще наугад. Считаем факт: искали
/// такими словами → открыли/залили вот эту версию. Десять раз выбрали одну, семь раз другую,
/// остальные по разу или ни разу — в следующий раз первой идёт та, которую ставят чаще.
///
/// Счётчик локальный (в общий конфиг не выгружается): это привычка конкретного человека на его
/// рабочем месте, а не общий справочник, и затирать чужую статистику своей — не то, что нужно.
/// Влияние на выдачу ограничено (Database.Search.cs, MaxUsageBonus): частота двигает версию среди
/// одинаково подходящих, но не вытаскивает наверх прошивку от другого шкафа.</summary>
public partial class Database
{
    /// <summary>Записать выбор. queryKey — нормализованный запрос (SearchService.Normalize), пустой
    /// означает «выбрали не из поиска» и не пишется: без запроса статистика бессмысленна.</summary>
    public void RecordFwUsage(string queryKey, int fwVersionId)
    {
        if (string.IsNullOrWhiteSpace(queryKey) || fwVersionId <= 0) return;

        ExecuteNonQuery("""
            INSERT INTO fw_search_usage (query_key, fw_version_id, uses, last_used_at)
            VALUES (@q, @v, 1, @t)
            ON CONFLICT(query_key, fw_version_id) DO UPDATE SET
                uses = uses + 1,
                last_used_at = @t
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@q", queryKey);
            cmd.Parameters.AddWithValue("@v", fwVersionId);
            cmd.Parameters.AddWithValue("@t", NowIso());
        });
    }

    /// <summary>Сколько раз каждую версию выбирали ИМЕННО по этому запросу.</summary>
    public Dictionary<int, int> GetFwUsageForQuery(string queryKey)
    {
        var result = new Dictionary<int, int>();
        if (string.IsNullOrWhiteSpace(queryKey)) return result;

        using var reader = ExecuteReader(
            "SELECT fw_version_id, uses FROM fw_search_usage WHERE query_key = @q",
            cmd => cmd.Parameters.AddWithValue("@q", queryKey));
        while (reader.Read())
            result[reader.GetInt32(0)] = reader.GetInt32(1);
        return result;
    }

    /// <summary>Сколько раз версию выбирали по всем запросам вместе — для строки на карточке.</summary>
    public int GetFwUsageTotal(int fwVersionId) =>
        ExecuteScalar("SELECT COALESCE(SUM(uses), 0) FROM fw_search_usage WHERE fw_version_id = @v",
            cmd => cmd.Parameters.AddWithValue("@v", fwVersionId)) is long l ? (int)l : 0;

    /// <summary>Удалённая версия статистику за собой не тянет — иначе счётчик по её id молча
    /// достался бы новой записи с тем же rowid (SQLite переиспользует id после удаления).</summary>
    public void ForgetFwUsage(int fwVersionId) =>
        ExecuteNonQuery("DELETE FROM fw_search_usage WHERE fw_version_id = @v",
            cmd => cmd.Parameters.AddWithValue("@v", fwVersionId));
}
