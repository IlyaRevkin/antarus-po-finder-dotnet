using System;
using System.Collections.Generic;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.Core.Data;

/// <summary>СЕАНСЫ РАБОТЫ ПРОГРАММЫ — хранение и слияние. Смысл и правила — в
/// <see cref="AppSession"/> и <see cref="AppSessions"/>; здесь только SQL.
///
/// Соседка «Подключений» (Database.SyncParticipants), но отвечает на другой вопрос: там одна строка
/// на УСТАНОВКУ («какая у неё версия»), здесь — одна на КАЖДЫЙ ЗАПУСК («кто работает прямо
/// сейчас и кого можно отключить»). Хранить одно в другом не вышло бы: строка участника
/// перезаписывается при каждом обмене, а сеансов за день у одной машины несколько, и завершать надо
/// конкретный.</summary>
public partial class Database
{
    /// <summary>Завести сеанс — один раз при запуске программы. Идентификатор придумывает
    /// приложение (новый GUID на запуск) и держит его у себя: по нему же оно потом узнаёт свою
    /// строку в общем списке.</summary>
    public void StartSession(AppSession session)
    {
        if (string.IsNullOrWhiteSpace(session.SessionId)) return;
        var now = NowIsoPrecise();
        ExecuteNonQuery("""
            INSERT INTO app_sessions
                (session_id, origin_id, machine_name, user_login, app_version, channel,
                 started_at, heartbeat_at, ended_at, ended_by, closed_at)
            VALUES (@s, @o, @m, @u, @v, @c, @t, @t, '', '', '')
            ON CONFLICT(session_id) DO UPDATE SET heartbeat_at = excluded.heartbeat_at
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@s", session.SessionId);
            cmd.Parameters.AddWithValue("@o", session.OriginId.Length > 0 ? session.OriginId : UsageOriginId());
            cmd.Parameters.AddWithValue("@m", session.MachineName);
            cmd.Parameters.AddWithValue("@u", session.UserLogin);
            cmd.Parameters.AddWithValue("@v", session.AppVersion);
            cmd.Parameters.AddWithValue("@c", session.Channel);
            cmd.Parameters.AddWithValue("@t", string.IsNullOrEmpty(session.StartedAt) ? now : session.StartedAt);
        });
    }

    /// <summary>Отметиться живым. Логин обновляется заодно: роль и учётку в этом приложении меняют
    /// не перезапуском, а диалогом смены роли (RoleSwitchDialog) — не обнови мы логин, сеанс остался
    /// бы подписан тем, кто сидел за машиной утром.</summary>
    public void HeartbeatSession(string sessionId, string userLogin)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        ExecuteNonQuery("""
            UPDATE app_sessions
               SET heartbeat_at = @t,
                   user_login   = CASE WHEN @u <> '' THEN @u ELSE user_login END
             WHERE session_id = @s AND closed_at = ''
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@t", NowIsoPrecise());
            cmd.Parameters.AddWithValue("@u", userLogin ?? "");
            cmd.Parameters.AddWithValue("@s", sessionId);
        });
    }

    /// <summary>Сеанс закончился — программа закрывается. Повторный вызов ничего не меняет: момент
    /// выхода один, и перебивать его более поздним значило бы растягивать уже закрытый сеанс.</summary>
    public void CloseSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        ExecuteNonQuery(
            "UPDATE app_sessions SET closed_at = @t WHERE session_id = @s AND closed_at = ''",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@t", NowIsoPrecise());
                cmd.Parameters.AddWithValue("@s", sessionId);
            });
    }

    /// <summary>Администратор просит завершить чужой сеанс. Это ЗАПИСЬ ПРОСЬБЫ, а не выход:
    /// программа на той машине увидит её ближайшим тиком и выйдет сама, дождавшись окончания долгой
    /// операции (см. AppSessions.ShutdownDelayReason).
    ///
    /// Условие `ended_at = ''` — не оптимизация: просьба липкая (см. AppSessions.Merge), и
    /// повторное нажатие не должно сдвигать момент решения вперёд, иначе слияние начнёт выбирать
    /// между двумя разными «раньше» и подпись разъедется со временем.</summary>
    public void RequestEndSession(string sessionId, string endedBy)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        ExecuteNonQuery(
            "UPDATE app_sessions SET ended_at = @t, ended_by = @b WHERE session_id = @s AND ended_at = ''",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@t", NowIsoPrecise());
                cmd.Parameters.AddWithValue("@b", endedBy ?? "");
                cmd.Parameters.AddWithValue("@s", sessionId);
            });
    }

    public AppSession? FindSession(string sessionId)
    {
        using var reader = ExecuteReader(
            SessionSelect + " WHERE session_id = @s",
            cmd => cmd.Parameters.AddWithValue("@s", sessionId ?? ""));
        return reader.Read() ? ReadSession(reader) : null;
    }

    /// <summary>Весь список, свежие сверху. Отбор «свой/все» делается НЕ здесь, а в
    /// AppSessions.Visible: это правило показа, и проверять его надо тестом, а не глазами по
    /// SQL-строке.</summary>
    public List<AppSession> GetSessions()
    {
        var result = new List<AppSession>();
        using var reader = ExecuteReader(SessionSelect + " ORDER BY heartbeat_at DESC");
        while (reader.Read()) result.Add(ReadSession(reader));
        return result;
    }

    /// <summary>Принять чужие сеансы из общего конфига. Построчное слияние по правилам
    /// <see cref="AppSessions.Merge"/> — читаем свою копию, сливаем, пишем обратно.
    ///
    /// Чтением-и-записью, а не одним хитрым UPSERT'ом намеренно: правило слияния здесь не «кто
    /// свежее», а «каждое поле по-своему» (отметка жизни — максимум, завершение — липкое), и
    /// выразить это условиями в SQL значило бы спрятать единственное место, которое обязано быть
    /// покрыто тестом.</summary>
    public void MergeSessions(IEnumerable<AppSession> incoming)
    {
        foreach (var row in incoming)
        {
            if (string.IsNullOrWhiteSpace(row.SessionId)) continue;
            var merged = AppSessions.Merge(FindSession(row.SessionId), row);
            ExecuteNonQuery("""
                INSERT INTO app_sessions
                    (session_id, origin_id, machine_name, user_login, app_version, channel,
                     started_at, heartbeat_at, ended_at, ended_by, closed_at)
                VALUES (@s, @o, @m, @u, @v, @c, @st, @hb, @ea, @eb, @ca)
                ON CONFLICT(session_id) DO UPDATE SET
                    origin_id    = excluded.origin_id,
                    machine_name = excluded.machine_name,
                    user_login   = excluded.user_login,
                    app_version  = excluded.app_version,
                    channel      = excluded.channel,
                    started_at   = excluded.started_at,
                    heartbeat_at = excluded.heartbeat_at,
                    ended_at     = excluded.ended_at,
                    ended_by     = excluded.ended_by,
                    closed_at    = excluded.closed_at
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("@s", merged.SessionId);
                cmd.Parameters.AddWithValue("@o", merged.OriginId);
                cmd.Parameters.AddWithValue("@m", merged.MachineName);
                cmd.Parameters.AddWithValue("@u", merged.UserLogin);
                cmd.Parameters.AddWithValue("@v", merged.AppVersion);
                cmd.Parameters.AddWithValue("@c", merged.Channel);
                cmd.Parameters.AddWithValue("@st", merged.StartedAt);
                cmd.Parameters.AddWithValue("@hb", merged.HeartbeatAt);
                cmd.Parameters.AddWithValue("@ea", merged.EndedAt);
                cmd.Parameters.AddWithValue("@eb", merged.EndedBy);
                cmd.Parameters.AddWithValue("@ca", merged.ClosedAt);
            });
        }
    }

    /// <summary>Убрать давно закрытые сеансы. Чистка СВОЕЙ базы, а не удаление «у всех»: общий
    /// конфиг удаления не переносит, и строка вернётся с машины, где срок ещё не вышел. Порог
    /// (AppSessions.KeepClosedFor) поэтому и большой — чтобы он истёк у всех примерно разом.</summary>
    public int PruneOldSessions(DateTime now)
    {
        var cutoff = now.Subtract(AppSessions.KeepClosedFor).ToString("yyyy-MM-dd HH:mm:ss");
        return ExecuteNonQuery("""
            DELETE FROM app_sessions
             WHERE heartbeat_at <> ''
               AND heartbeat_at < @cut
            """, cmd => cmd.Parameters.AddWithValue("@cut", cutoff));
    }

    private const string SessionSelect =
        "SELECT session_id, origin_id, machine_name, user_login, app_version, channel, " +
        "started_at, heartbeat_at, ended_at, ended_by, closed_at FROM app_sessions";

    private static AppSession ReadSession(Microsoft.Data.Sqlite.SqliteDataReader reader) => new()
    {
        SessionId = GetString(reader, "session_id"),
        OriginId = GetString(reader, "origin_id"),
        MachineName = GetString(reader, "machine_name"),
        UserLogin = GetString(reader, "user_login"),
        AppVersion = GetString(reader, "app_version"),
        Channel = GetString(reader, "channel"),
        StartedAt = GetString(reader, "started_at"),
        HeartbeatAt = GetString(reader, "heartbeat_at"),
        EndedAt = GetString(reader, "ended_at"),
        EndedBy = GetString(reader, "ended_by"),
        ClosedAt = GetString(reader, "closed_at"),
    };
}
