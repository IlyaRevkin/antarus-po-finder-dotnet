using System;
using System.Collections.Generic;

namespace AntarusPoFinder.Core.Data;

/// <summary>Решение по вопросу «это та прошивка, которую вы искали?»: спрашивать, засчитывать молча
/// или не засчитывать вовсе. Ровно та же тройка состояний, что у подсказки про раскладку
/// (LayoutFallbackDecision), и учится так же — несколькими одинаковыми ответами подряд.</summary>
public enum UsageConfirmDecision { Ask, Always, Never }

/// <summary>Одна строка чужого вклада в статистику — то, что уезжает в общий конфиг и приезжает с
/// других машин. Прошивка адресуется переносимо (sync_id подтипа и модели контроллера + version_raw):
/// локальные id прошивок на разных машинах не совпадают.</summary>
public record SharedFwUsageRow(string Origin, string QueryKey, string SubtypeSyncId, string ControllerSyncId,
    string VersionRaw, int Uses, string LastUsedAt);

/// <summary>«По такому запросу обычно ставят вот эту версию» — счётчик выбора версии из выдачи
/// поиска.
///
/// Зачем: у одного и того же шкафа находится несколько подходящих версий, и правильную наладчик
/// каждый раз выбирает заново — по памяти, а новый сотрудник вообще наугад. Считаем факт: искали
/// такими словами → открыли/залили вот эту версию. Десять раз выбрали одну, семь раз другую,
/// остальные по разу или ни разу — в следующий раз первой идёт та, которую ставят чаще.
///
/// Статистика ОБЩАЯ: вклад этой машины (fw_search_usage) уезжает в общий конфиг, вклад остальных
/// приезжает оттуда (fw_usage_shared) и складывается с местным при чтении. Вклады хранятся раздельно
/// по машине-источнику именно поэтому: чужой вклад приходит снимком целиком, и складывать его в один
/// общий счётчик значило бы пересчитывать одно и то же на каждой синхронизации.
///
/// Влияние на выдачу ограничено (Database.Search.cs, MaxUsageBonus): частота двигает версию среди
/// одинаково подходящих, но не вытаскивает наверх прошивку от другого шкафа.</summary>
public partial class Database
{
    /// <summary>Сколько одинаковых ответов подряд на «это та прошивка?» превращаются в решение —
    /// после этого вопрос больше не задаётся. Тот же смысл и то же значение по умолчанию, что у
    /// LayoutFallbackDecisionThreshold.</summary>
    public const int UsageConfirmDecisionThreshold = 3;

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

    /// <summary>Сколько раз каждую версию выбирали ИМЕННО по этому запросу — на всех машинах вместе.
    /// Чужой вклад досчитывается вторым запросом: прошивки, которой на этой машине нет, в выдаче всё
    /// равно не будет, поэтому JOIN по переносимому ключу отбрасывает её сам собой.</summary>
    public Dictionary<int, int> GetFwUsageForQuery(string queryKey)
    {
        var result = new Dictionary<int, int>();
        if (string.IsNullOrWhiteSpace(queryKey)) return result;

        using (var reader = ExecuteReader(
            "SELECT fw_version_id, uses FROM fw_search_usage WHERE query_key = @q",
            cmd => cmd.Parameters.AddWithValue("@q", queryKey)))
            while (reader.Read())
                result[reader.GetInt32(0)] = reader.GetInt32(1);

        using (var reader = ExecuteReader($"""
            SELECT fv.id, SUM(u.uses) AS uses
            FROM fw_usage_shared u
            {SharedUsageJoin}
            WHERE u.query_key = @q
            GROUP BY fv.id
            """, cmd => cmd.Parameters.AddWithValue("@q", queryKey)))
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                result[id] = (result.TryGetValue(id, out var mine) ? mine : 0) + reader.GetInt32(1);
            }

        return result;
    }

    /// <summary>Сколько раз версию выбирали по всем запросам вместе — для строки на карточке.</summary>
    public int GetFwUsageTotal(int fwVersionId)
    {
        var mine = ExecuteScalar("SELECT COALESCE(SUM(uses), 0) FROM fw_search_usage WHERE fw_version_id = @v",
            cmd => cmd.Parameters.AddWithValue("@v", fwVersionId)) is long l ? (int)l : 0;

        var others = ExecuteScalar($"""
            SELECT COALESCE(SUM(u.uses), 0)
            FROM fw_usage_shared u
            {SharedUsageJoin}
            WHERE fv.id = @v
            """, cmd => cmd.Parameters.AddWithValue("@v", fwVersionId)) is long o ? (int)o : 0;

        return mine + others;
    }

    /// <summary>Удалённая версия статистику за собой не тянет — иначе счётчик по её id молча
    /// достался бы новой записи с тем же rowid (SQLite переиспользует id после удаления). Чужой
    /// вклад привязан не к id, а к переносимому ключу, и вместе с самой версией исчезает из выдачи
    /// сам — трогать его здесь не нужно.</summary>
    public void ForgetFwUsage(int fwVersionId) =>
        ExecuteNonQuery("DELETE FROM fw_search_usage WHERE fw_version_id = @v",
            cmd => cmd.Parameters.AddWithValue("@v", fwVersionId));

    /// <summary>Переносимый ключ (подтип+модель контроллера по sync_id, version_raw) → локальная
    /// строка fw_versions. Один и тот же JOIN нужен и при чтении по запросу, и при подсчёте итога.</summary>
    private const string SharedUsageJoin = """
        JOIN equipment_subtypes es ON es.sync_id = u.subtype_sync_id
        JOIN controller_models  cm ON cm.sync_id = u.controller_sync_id
        JOIN fw_versions        fv ON fv.subtype_id = es.id AND fv.controller_id = cm.id
                                  AND fv.version_raw = u.version_raw
        """;

    // ── Обмен статистикой между машинами ──────────────────────────────────────

    /// <summary>Кто эта машина в общей статистике. GUID, а не имя компьютера/логин: под одним
    /// логином работают с разных мест, а нужен ровно «этот экземпляр программы со своей базой».
    /// Ключ обязан быть в ConfigSyncService.SkipSettingsKeys — уехав в общий конфиг, он сделал бы
    /// все машины одним источником, и их вклады затирали бы друг друга.</summary>
    public string UsageOriginId()
    {
        var id = GetSetting(UsageOriginSettingKey);
        if (string.IsNullOrEmpty(id))
        {
            id = Guid.NewGuid().ToString("N");
            SetSetting(UsageOriginSettingKey, id);
        }
        return id;
    }

    public const string UsageOriginSettingKey = "fw_usage_origin_id";

    /// <summary>Всё, что эта машина знает о статистике: собственный вклад (переведённый в переносимые
    /// ключи под именем origin) плюс уже известные ей чужие вклады. Чужие пересылаются дальше
    /// намеренно — общий конфиг переписывается целиком, и машина, выгрузившая только своё, стёрла бы
    /// в снимке вклад остальных.</summary>
    public List<SharedFwUsageRow> ExportFwUsage(string origin)
    {
        var result = new List<SharedFwUsageRow>();

        using (var reader = ExecuteReader("""
            SELECT u.query_key, es.sync_id AS subtype_sync_id, cm.sync_id AS controller_sync_id,
                   fv.version_raw, u.uses, u.last_used_at
            FROM fw_search_usage u
            JOIN fw_versions        fv ON fv.id = u.fw_version_id
            JOIN equipment_subtypes es ON es.id = fv.subtype_id
            JOIN controller_models  cm ON cm.id = fv.controller_id
            WHERE es.sync_id <> '' AND cm.sync_id <> ''
            """))
            while (reader.Read())
                result.Add(new SharedFwUsageRow(origin, GetString(reader, "query_key"),
                    GetString(reader, "subtype_sync_id"), GetString(reader, "controller_sync_id"),
                    GetString(reader, "version_raw"), GetInt(reader, "uses"), GetString(reader, "last_used_at")));

        using (var reader = ExecuteReader("""
            SELECT origin, query_key, subtype_sync_id, controller_sync_id, version_raw, uses, last_used_at
            FROM fw_usage_shared WHERE origin <> @o
            """, cmd => cmd.Parameters.AddWithValue("@o", origin)))
            while (reader.Read())
                result.Add(new SharedFwUsageRow(GetString(reader, "origin"), GetString(reader, "query_key"),
                    GetString(reader, "subtype_sync_id"), GetString(reader, "controller_sync_id"),
                    GetString(reader, "version_raw"), GetInt(reader, "uses"), GetString(reader, "last_used_at")));

        return result;
    }

    /// <summary>Принять чужие вклады. Свой (origin == selfOrigin) отбрасывается: источник истины по
    /// нему — местная fw_search_usage, а в снимке лежит его же копия, возможно устаревшая. Вклад
    /// каждой машины заменяется целиком (снимок, а не приращение), поэтому повторная синхронизация
    /// того же снимка ничего не удваивает.</summary>
    public int ImportFwUsage(IEnumerable<SharedFwUsageRow>? rows, string selfOrigin)
    {
        if (rows is null) return 0;

        var applied = 0;
        var seenOrigins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Origin) || row.Origin == selfOrigin) continue;

            if (seenOrigins.Add(row.Origin))
                ExecuteNonQuery("DELETE FROM fw_usage_shared WHERE origin = @o",
                    cmd => cmd.Parameters.AddWithValue("@o", row.Origin));

            ExecuteNonQuery("""
                INSERT OR REPLACE INTO fw_usage_shared
                    (origin, query_key, subtype_sync_id, controller_sync_id, version_raw, uses, last_used_at)
                VALUES (@o, @q, @s, @c, @v, @u, @t)
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("@o", row.Origin);
                cmd.Parameters.AddWithValue("@q", row.QueryKey);
                cmd.Parameters.AddWithValue("@s", row.SubtypeSyncId);
                cmd.Parameters.AddWithValue("@c", row.ControllerSyncId);
                cmd.Parameters.AddWithValue("@v", row.VersionRaw);
                cmd.Parameters.AddWithValue("@u", row.Uses);
                cmd.Parameters.AddWithValue("@t", row.LastUsedAt);
            });
            applied++;
        }
        return applied;
    }

    /// <summary>Сброс статистики — и своей, и приехавшей. Чтобы сброс дошёл до остальных машин, а не
    /// был отменён первым же чужим снимком со старыми числами, вызывающий проставляет отметку времени
    /// сброса в настройках (ConfigService.FwUsageResetAt) — она уезжает в общий конфиг, и каждая
    /// машина, увидев отметку новее своей, чистит статистику у себя (ConfigSyncService).</summary>
    public void ResetAllFwUsage()
    {
        ExecuteNonQuery("DELETE FROM fw_search_usage");
        ExecuteNonQuery("DELETE FROM fw_usage_shared");
    }

    /// <summary>Сколько всего выборов сейчас учтено — для подписи у кнопки сброса, чтобы «сбросить»
    /// не было прыжком в неизвестность.</summary>
    public int TotalFwUsageCount()
    {
        var mine = ExecuteScalar("SELECT COALESCE(SUM(uses), 0) FROM fw_search_usage") is long l ? (int)l : 0;
        var others = ExecuteScalar("SELECT COALESCE(SUM(uses), 0) FROM fw_usage_shared") is long o ? (int)o : 0;
        return mine + others;
    }

    // ── «Это та прошивка, которую вы искали?» ────────────────────────────────

    public UsageConfirmDecision GetFwUsageConfirmDecision() =>
        (ExecuteScalar("SELECT decision FROM fw_usage_confirm_feedback WHERE id = 1") as string) switch
        {
            "always" => UsageConfirmDecision.Always,
            "never" => UsageConfirmDecision.Never,
            _ => UsageConfirmDecision.Ask,
        };

    /// <summary>Ответ оператора на вопрос. Решение выводится из накопленного перевеса ответов, ровно
    /// как у подсказки про раскладку: подтверждает подряд — статистика дальше пишется молча, отвергает
    /// подряд — не пишется и вопрос больше не задаётся.</summary>
    public void RecordFwUsageConfirmFeedback(bool confirmed, int threshold = UsageConfirmDecisionThreshold)
    {
        ExecuteNonQuery("""
            INSERT INTO fw_usage_confirm_feedback(id, yes_count, no_count) VALUES(1, @yes, @no)
            ON CONFLICT(id) DO UPDATE SET
                yes_count = yes_count + excluded.yes_count,
                no_count  = no_count + excluded.no_count
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@yes", confirmed ? 1 : 0);
            cmd.Parameters.AddWithValue("@no", confirmed ? 0 : 1);
        });

        int yes, no;
        using (var reader = ExecuteReader("SELECT yes_count, no_count FROM fw_usage_confirm_feedback WHERE id = 1"))
        {
            if (!reader.Read()) return;
            yes = GetInt(reader, "yes_count");
            no = GetInt(reader, "no_count");
        }

        if (yes - no >= threshold) SetFwUsageConfirmDecision(UsageConfirmDecision.Always);
        else if (no - yes >= threshold) SetFwUsageConfirmDecision(UsageConfirmDecision.Never);
    }

    /// <summary>Прямой путь к решению — галочка «больше не спрашивать» в самом вопросе: ответ на неё
    /// не «накапливается», оператор сказал прямо.</summary>
    public void SetFwUsageConfirmDecision(UsageConfirmDecision decision)
    {
        var value = decision switch
        {
            UsageConfirmDecision.Always => "always",
            UsageConfirmDecision.Never => "never",
            _ => "",
        };
        ExecuteNonQuery("""
            INSERT INTO fw_usage_confirm_feedback(id, decision) VALUES(1, @d)
            ON CONFLICT(id) DO UPDATE SET decision = excluded.decision
            """, cmd => cmd.Parameters.AddWithValue("@d", value));
    }

    /// <summary>Забыть выученное — вопрос снова начнёт задаваться (Настройки → Общие, рядом со
    /// сбросом обучения подсказки про раскладку).</summary>
    public void ResetFwUsageConfirmLearning() => ExecuteNonQuery("DELETE FROM fw_usage_confirm_feedback");
}
