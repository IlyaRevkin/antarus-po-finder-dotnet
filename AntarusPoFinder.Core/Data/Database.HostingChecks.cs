using System;
using System.Collections.Generic;

namespace AntarusPoFinder.Core.Data;

/// <summary>Что мы в последний раз видели на хостинге. Кэш наблюдений, а не источник правды: правда
/// живёт в бакете, и страница «Хранилище» спрашивает её запросом (см. HostingSyncService).
///
/// Нужен ради одной вещи — значка «на хостинге» на карточке прошивки. Выдача поиска рисуется
/// мгновенно и по десятку карточек за раз; ходить в сеть на каждую было бы и медленно, и незачем:
/// человеку в этот момент нужен не свежайший факт, а «в прошлый раз проверяли — лежало». Точный
/// ответ он получит на странице «Хранилище», где для этого есть кнопка.
///
/// Таблица СТРОГО локальная и в общий конфиг не уезжает: это наблюдение конкретной машины в
/// конкретный момент, а не решение, которое надо распространять.</summary>
public partial class Database
{
    /// <summary>Запомнить результат проверки одного объекта.</summary>
    public void SaveHostingCheck(string objectKey, bool present, string url)
    {
        if (string.IsNullOrWhiteSpace(objectKey)) return;

        ExecuteNonQuery("""
            INSERT INTO hosting_checks(object_key, present, url, checked_at)
            VALUES(@k, @p, @u, @t)
            ON CONFLICT(object_key) DO UPDATE SET present = @p, url = @u, checked_at = @t
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@k", objectKey);
            cmd.Parameters.AddWithValue("@p", present ? 1 : 0);
            cmd.Parameters.AddWithValue("@u", url ?? "");
            cmd.Parameters.AddWithValue("@t", NowIso());
        });
    }

    /// <summary>Все наблюдения разом. Выдача поиска перебирает карточки пачкой, и ходить в базу на
    /// каждую было бы квадратично — та же причина, что и у GetRowTagState.</summary>
    public Dictionary<string, (bool Present, string CheckedAt)> GetHostingChecks()
    {
        var result = new Dictionary<string, (bool, string)>(StringComparer.Ordinal);
        using var reader = ExecuteReader("SELECT object_key, present, checked_at FROM hosting_checks");
        while (reader.Read())
            result[reader.GetString(0)] = (reader.GetInt32(1) != 0, reader.GetString(2));
        return result;
    }

    /// <summary>Наблюдение по одному ключу. null — про этот объект мы ничего не знаем, и показывать
    /// надо именно «не проверялось», а не «нет на хостинге»: это разные вещи.</summary>
    public (bool Present, string CheckedAt)? GetHostingCheck(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey)) return null;
        using var reader = ExecuteReader(
            "SELECT present, checked_at FROM hosting_checks WHERE object_key = @k",
            cmd => cmd.Parameters.AddWithValue("@k", objectKey));
        return reader.Read() ? (reader.GetInt32(0) != 0, reader.GetString(1)) : null;
    }
}
