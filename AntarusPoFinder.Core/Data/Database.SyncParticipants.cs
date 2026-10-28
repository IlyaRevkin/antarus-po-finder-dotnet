using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace AntarusPoFinder.Core.Data;

/// <summary>КТО СЕЙЧАС РАБОТАЕТ С ОБЩИМ КОНФИГОМ И НА КАКОЙ ВЕРСИИ.
///
/// Нужно затем, что почти каждая живучая беда этого года приходила от машины со старой версией:
/// подтип, воскресший у коллеги; прошивки, висящие в модерации вечно; тег, возвращающийся после
/// снятия. Чинилось это в новой версии, а разъезжалось — со старой, и узнать, у кого именно она
/// осталась, было неоткуда: общий конфиг безличен, и спросить «а кто это прислал» было нельзя.
///
/// Теперь каждая машина подписывает свой вклад: кто она, под кем работают, какая версия, какой
/// канал (рабочая или бета) и когда её видели в последний раз. Строки едут в общем конфиге рядом со
/// статистикой выборов и по тем же правилам: пересылаются чужие тоже, иначе машина, выгрузившая
/// только себя, стёрла бы в снимке всех остальных.
///
/// Ключ — тот же GUID установки, что и у статистики (<see cref="UsageOriginId"/>), а не имя
/// компьютера: под одним логином работают с разных мест, а нужна ровно эта копия программы со своей
/// базой.</summary>
public partial class Database
{
    public sealed class SyncParticipant
    {
        public string OriginId { get; init; } = "";
        public string MachineName { get; init; } = "";
        public string UserName { get; init; } = "";
        public string AppVersion { get; init; } = "";
        public string Channel { get; init; } = "";
        public string LastSeenAt { get; init; } = "";

        /// <summary>Машина, которой общий конфиг больше не доверяет: её правки не применяются, пока
        /// она не обновится. Ставится не вручную, а сравнением версии с минимальной.</summary>
        public bool Blocked { get; init; }
    }

    /// <summary>Ключ, под которым машина помнит СВОЮ версию. Нужен ядру, которое о сборке
    /// приложения ничего не знает: приложение записывает версию при старте, а экспорт конфига
    /// подписывает ею снимок. Местный ключ — в общий конфиг не уезжает (см. SkipSettingsKeys).</summary>
    public const string OwnAppVersionKey = "own_app_version";

    /// <summary>Отметиться самому — при каждом обмене конфигом.</summary>
    public void RecordOwnParticipation(
        string machineName,
        string userName,
        string appVersion,
        string channel)
    {
        ExecuteNonQuery("""
            INSERT INTO sync_participants (origin_id, machine_name, user_name, app_version, channel, last_seen_at, blocked)
            VALUES (@o, @m, @u, @v, @c, @t, 0)
            ON CONFLICT(origin_id) DO UPDATE SET
                machine_name = excluded.machine_name,
                user_name    = excluded.user_name,
                app_version  = excluded.app_version,
                channel      = excluded.channel,
                last_seen_at = excluded.last_seen_at
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@o", UsageOriginId());
            cmd.Parameters.AddWithValue("@m", machineName);
            cmd.Parameters.AddWithValue("@u", userName);
            cmd.Parameters.AddWithValue("@v", appVersion);
            cmd.Parameters.AddWithValue("@c", channel);
            cmd.Parameters.AddWithValue("@t", NowIsoPrecise());
        });
    }

    /// <summary>Принять чужие отметки. Побеждает та, что свежее: машина могла быть выключена неделю,
    /// и её старая строка не должна затирать то, что о ней знают другие.</summary>
    public void MergeParticipants(IEnumerable<SyncParticipant> incoming)
    {
        foreach (var p in incoming)
        {
            if (string.IsNullOrWhiteSpace(p.OriginId)) continue;
            ExecuteNonQuery("""
                INSERT INTO sync_participants (origin_id, machine_name, user_name, app_version, channel, last_seen_at, blocked)
                VALUES (@o, @m, @u, @v, @c, @t, 0)
                ON CONFLICT(origin_id) DO UPDATE SET
                    machine_name = excluded.machine_name,
                    user_name    = excluded.user_name,
                    app_version  = excluded.app_version,
                    channel      = excluded.channel,
                    last_seen_at = excluded.last_seen_at
                WHERE excluded.last_seen_at > sync_participants.last_seen_at
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("@o", p.OriginId);
                cmd.Parameters.AddWithValue("@m", p.MachineName);
                cmd.Parameters.AddWithValue("@u", p.UserName);
                cmd.Parameters.AddWithValue("@v", p.AppVersion);
                cmd.Parameters.AddWithValue("@c", p.Channel);
                cmd.Parameters.AddWithValue("@t", p.LastSeenAt);
            });
        }
    }

    public List<SyncParticipant> GetParticipants()
    {
        var result = new List<SyncParticipant>();
        using var reader = ExecuteReader(
            "SELECT origin_id, machine_name, user_name, app_version, channel, last_seen_at, blocked " +
            "FROM sync_participants ORDER BY last_seen_at DESC");
        while (reader.Read())
        {
            result.Add(new SyncParticipant
            {
                OriginId = GetString(reader, "origin_id"),
                MachineName = GetString(reader, "machine_name"),
                UserName = GetString(reader, "user_name"),
                AppVersion = GetString(reader, "app_version"),
                Channel = GetString(reader, "channel"),
                LastSeenAt = GetString(reader, "last_seen_at"),
                Blocked = GetInt(reader, "blocked") != 0,
            });
        }
        return result;
    }

    /// <summary>Пометить машину отказавшей в доверии (или вернуть доверие). Ставится по результату
    /// сравнения версий, а не прихотью: «заблокирован» здесь значит ровно «версия ниже минимальной,
    /// правки не принимаются».</summary>
    /// <summary>Убрать строку из списка. Уборка, а не запрет: машина появится снова при ближайшей
    /// своей синхронизации — чтобы «забыть» означало именно «почистить список», а не «отключить
    /// коллегу», для чего есть минимальная версия.</summary>
    public void ForgetParticipant(string originId) =>
        ExecuteNonQuery("DELETE FROM sync_participants WHERE origin_id = @o",
            cmd => cmd.Parameters.AddWithValue("@o", originId));

    public void SetParticipantBlocked(string originId, bool blocked) =>
        ExecuteNonQuery("UPDATE sync_participants SET blocked = @b WHERE origin_id = @o", cmd =>
        {
            cmd.Parameters.AddWithValue("@b", blocked ? 1 : 0);
            cmd.Parameters.AddWithValue("@o", originId);
        });
}
