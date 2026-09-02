using System.Collections.Generic;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    /// <summary>Формат времени в таблице notifications. Тот же «yyyy-MM-dd HH:mm:ss», что у NowIso,
    /// — сортировка по строке совпадает с сортировкой по времени.</summary>
    private const string NotificationTimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Вся история, свежие сверху. Порядок — по времени, при совпадении до секунды по id:
    /// два уведомления в одну секунду это норма (тик синхронизации сыплет пачкой), и без второго
    /// ключа их порядок был бы случайным.</summary>
    public List<StoredNotification> GetNotifications(int limit = 200)
    {
        var result = new List<StoredNotification>();
        using var r = ExecuteReader(
            "SELECT id, text, category, created_at, repeats, is_read FROM notifications ORDER BY created_at DESC, id DESC LIMIT @lim",
            cmd => cmd.Parameters.AddWithValue("@lim", limit));
        while (r.Read())
            result.Add(new StoredNotification
            {
                Id = r.GetInt64(0),
                Text = r.GetString(1),
                Category = ParseCategory(r.GetString(2)),
                When = ParseNotificationTime(r.GetString(3)),
                Repeats = r.GetInt32(4),
                IsRead = r.GetInt32(5) != 0,
            });
        return result;
    }

    /// <summary>Записывает уведомление и отдаёт строку, какой она стала.
    ///
    /// Повтор ровно того же текста НЕ заводит новую строку: поднимает существующую наверх, увеличивает
    /// счётчик повторов и снимает признак «прочитано» — случилось снова, значит это снова новость.
    /// Правило то же, что было в памяти (NotificationHistoryOps), просто теперь оно живёт там же, где
    /// сама история: иначе после перезапуска та же залипшая ошибка заводила бы вторую строку.
    ///
    /// ⚠️ Сравнение текста — обычное SQLite-равенство по байтам, БЕЗ COLLATE NOCASE. Это осознанно:
    /// NOCASE в SQLite сворачивает только латиницу (см. CLAUDE.md), то есть по-русски работал бы
    /// через раз, а «то же самое сообщение» и должно означать посимвольно то же самое.
    ///
    /// <paramref name="limit"/> — сколько строк держать всего; лишние старые удаляются тут же.</summary>
    public StoredNotification SaveNotification(string text, NotificationCategory category, DateTime when, int limit = 200)
    {
        var stamp = when.ToString(NotificationTimeFormat);

        var existingId = ExecuteScalar("SELECT id FROM notifications WHERE text=@t ORDER BY id DESC LIMIT 1",
            cmd => cmd.Parameters.AddWithValue("@t", text)) as long?;

        if (existingId is long id)
        {
            ExecuteNonQuery("UPDATE notifications SET created_at=@c, repeats=repeats+1, is_read=0, category=@g WHERE id=@id", cmd =>
            {
                cmd.Parameters.AddWithValue("@c", stamp);
                cmd.Parameters.AddWithValue("@g", category.ToString());
                cmd.Parameters.AddWithValue("@id", id);
            });
            var repeats = ExecuteScalar("SELECT repeats FROM notifications WHERE id=@id",
                cmd => cmd.Parameters.AddWithValue("@id", id)) as long? ?? 1;
            return new StoredNotification { Id = id, Text = text, Category = category, When = when, Repeats = (int)repeats, IsRead = false };
        }

        ExecuteNonQuery("INSERT INTO notifications(text, category, created_at, repeats, is_read) VALUES(@t,@g,@c,1,0)", cmd =>
        {
            cmd.Parameters.AddWithValue("@t", text);
            cmd.Parameters.AddWithValue("@g", category.ToString());
            cmd.Parameters.AddWithValue("@c", stamp);
        });
        var newId = (long)(ExecuteScalar("SELECT last_insert_rowid()") ?? 0L);
        TrimNotifications(limit);
        return new StoredNotification { Id = newId, Text = text, Category = category, When = when, Repeats = 1, IsRead = false };
    }

    /// <summary>Пометить одно уведомление прочитанным. Именно поштучно, а не «всё разом при открытии
    /// окна»: счётчик обязан убавляться только на том, что человек действительно увидел.</summary>
    public void MarkNotificationRead(long id) =>
        ExecuteNonQuery("UPDATE notifications SET is_read=1 WHERE id=@id",
            cmd => cmd.Parameters.AddWithValue("@id", id));

    public void MarkAllNotificationsRead() =>
        ExecuteNonQuery("UPDATE notifications SET is_read=1 WHERE is_read=0");

    /// <summary>Удалить одно уведомление — тикет: «сделать возможность удалять каждое уведомление
    /// поштучно». Надгробия нет и не нужно: таблица машинная, никуда не уезжает.</summary>
    public void DeleteNotification(long id) =>
        ExecuteNonQuery("DELETE FROM notifications WHERE id=@id",
            cmd => cmd.Parameters.AddWithValue("@id", id));

    public void ClearNotifications() => ExecuteNonQuery("DELETE FROM notifications");

    /// <summary>Оставляет только <paramref name="limit"/> самых свежих. Ключ отбора тот же, что у
    /// GetNotifications, иначе обрезка выкидывала бы не то, что человек считает старым.</summary>
    public void TrimNotifications(int limit) =>
        ExecuteNonQuery("""
            DELETE FROM notifications WHERE id NOT IN (
                SELECT id FROM notifications ORDER BY created_at DESC, id DESC LIMIT @lim
            )
            """, cmd => cmd.Parameters.AddWithValue("@lim", limit));

    /// <summary>Неизвестное имя категории (запись от более новой версии приложения, где категорий
    /// больше) читается как «Общие», а не роняет чтение всей истории.</summary>
    private static NotificationCategory ParseCategory(string raw) =>
        Enum.TryParse<NotificationCategory>(raw, out var parsed) ? parsed : NotificationCategory.General;

    private static DateTime ParseNotificationTime(string raw) =>
        DateTime.TryParse(raw, out var parsed) ? parsed : DateTime.Now;
}
