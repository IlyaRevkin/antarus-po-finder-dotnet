using System.Collections.Generic;
using System.Linq;

namespace AntarusPoFinder.Core.Data;

/// <summary>Решение по вопросу «это та прошивка, которую вы искали?»: спрашивать, засчитывать молча
/// или не засчитывать вовсе. Ровно та же тройка состояний, что у подсказки про раскладку
/// (LayoutFallbackDecision), и учится так же — несколькими одинаковыми ответами подряд.</summary>
public enum UsageConfirmDecision { Ask, Always, Never }

/// <summary>Одна строка чужого вклада в статистику — то, что уезжает в общий конфиг и приезжает с
/// других машин. Прошивка адресуется переносимо (sync_id подтипа и модели контроллера + version_raw):
/// локальные id прошивок на разных машинах не совпадают.</summary>
public record SharedFwUsageRow(string Origin, string QueryKey, string SubtypeSyncId, string ControllerSyncId,
    string VersionRaw, int Uses, string LastUsedAt, int Weight = 0);

/// <summary>Одна строка таблицы просмотра статистики (Настройки → Общие) — накопленное число выборов
/// по паре «запрос → конкретная версия», уже с человекочитаемым названием подтипа/контроллера вместо
/// внутренних id. Uses — сумма своего вклада и уже известного чужого (см. GetAllFwUsage), то же самое
/// сложение, что и в GetFwUsageForQuery, только сразу по всем запросам, а не по одному. LocalUses/
/// LocalWeight — доля ИМЕННО этой машины из тех же сумм: ручная правка в таблице статистики трогает
/// только свой вклад, поэтому редактору нужно знать, сколько из показанного числа своё, а сколько
/// чужой снимок (его правкой не сдвинуть — синхронизация вернёт назад).</summary>
public record FwUsageStatRow(string QueryKey, string SubtypeName, string ControllerName, string VersionRaw,
    int Uses, string LastUsedAt, int? LocalVersionId = null, int Weight = 0, int LocalUses = 0, int LocalWeight = 0);

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

    /// <summary>Задать вклад ЭТОЙ машины по паре «запрос → версия» напрямую (ручная правка веса в
    /// таблице статистики и в модерации прошивки — оператор поднимает/опускает версию под запрос
    /// осознанно, не дожидаясь, пока частота накопится сама). Ноль и меньше — убрать строку совсем.
    /// Правится только СВОЙ вклад (fw_search_usage); чужой (fw_usage_shared) приходит снимком с других
    /// машин и здесь не трогается — иначе следующая синхронизация всё равно вернула бы его значение.</summary>
    public void SetLocalFwUsage(string queryKey, int fwVersionId, int uses)
    {
        if (string.IsNullOrWhiteSpace(queryKey) || fwVersionId <= 0) return;
        if (uses <= 0)
        {
            // Обнуляем ТОЛЬКО счётчик, но строку удаляем лишь если на ней не висит ручной вес: uses и
            // weight делят одну строку (query_key, fw_version_id), и снести её целиком значило бы
            // молча стереть ручной вес заодно со счётчиком (см. SetLocalFwWeight).
            ExecuteNonQuery("""
                UPDATE fw_search_usage SET uses = 0, last_used_at = @t WHERE query_key = @q AND fw_version_id = @v;
                DELETE FROM fw_search_usage WHERE query_key = @q AND fw_version_id = @v AND uses <= 0 AND weight <= 0;
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("@q", queryKey.Trim());
                cmd.Parameters.AddWithValue("@v", fwVersionId);
                cmd.Parameters.AddWithValue("@t", NowIso());
            });
            return;
        }
        ExecuteNonQuery("""
            INSERT INTO fw_search_usage (query_key, fw_version_id, uses, last_used_at)
            VALUES (@q, @v, @u, @t)
            ON CONFLICT(query_key, fw_version_id) DO UPDATE SET uses = @u, last_used_at = @t
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@q", queryKey.Trim());
            cmd.Parameters.AddWithValue("@v", fwVersionId);
            cmd.Parameters.AddWithValue("@u", uses);
            cmd.Parameters.AddWithValue("@t", NowIso());
        });
    }

    /// <summary>Задать ручной вес прошивки под запрос — ОТДЕЛЬНО от счётчика открытий (uses). Это и
    /// есть «вес в поиске» из окна модерации: оператор осознанно поднимает версию под конкретный
    /// запрос, а ранжирование складывает этот вес с накопленным счётчиком открытий, а не заменяет его
    /// (см. Database.Search.cs Rank). Ноль и меньше — убрать ручной вес; сама строка остаётся, если на
    /// ней ещё висит ненулевой счётчик открытий. Правится только вклад ЭТОЙ машины (fw_search_usage);
    /// чужой вес приходит снимком (fw_usage_shared) и здесь не трогается — как и у счётчика.</summary>
    public void SetLocalFwWeight(string queryKey, int fwVersionId, int weight)
    {
        if (string.IsNullOrWhiteSpace(queryKey) || fwVersionId <= 0) return;
        weight = Math.Max(0, weight);
        if (weight == 0)
        {
            ExecuteNonQuery("""
                UPDATE fw_search_usage SET weight = 0 WHERE query_key = @q AND fw_version_id = @v;
                DELETE FROM fw_search_usage WHERE query_key = @q AND fw_version_id = @v AND uses <= 0 AND weight <= 0;
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("@q", queryKey.Trim());
                cmd.Parameters.AddWithValue("@v", fwVersionId);
            });
            return;
        }
        ExecuteNonQuery("""
            INSERT INTO fw_search_usage (query_key, fw_version_id, weight, last_used_at)
            VALUES (@q, @v, @w, @t)
            ON CONFLICT(query_key, fw_version_id) DO UPDATE SET weight = @w
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@q", queryKey.Trim());
            cmd.Parameters.AddWithValue("@v", fwVersionId);
            cmd.Parameters.AddWithValue("@w", weight);
            cmd.Parameters.AddWithValue("@t", NowIso());
        });
    }

    /// <summary>Служебный «запрос» для ручного счётчика версии, выставленного не из поиска, а прямо в
    /// истории версий (SetLocalFwUsageVersionTotal). Ключ намеренно в нижнем регистре: все настоящие
    /// ключи проходят через SearchService.Normalize с ToUpperInvariant, поэтому строчный "manual"
    /// нормализованным запросом не порождается никогда, поэтому ранжирование поиска (GetFwUsageForQuery по конкретному запросу) его
    /// сам собой не подхватывает: ручная правка меняет ОБЩЕЕ «сколько раз выбирали» (карточка/история),
    /// не подтасовывая выдачу под какой-то один запрос. Из по-запросной статистики и из обмена между
    /// машинами он тоже исключён (см. GetAllFwUsage/GetFwUsageQueriesForVersion/ExportFwUsage) — это
    /// правка счётчика ровно этой машины, а не переносимый факт «выбирали по запросу X».</summary>
    public const string ManualUsageKey = "manual";

    /// <summary>Задать суммарный вклад ЭТОЙ машины в счётчик обращений версии одним числом — правка
    /// «кол-во обращений» прямо в истории версий, где счётчик показан агрегатом по всем запросам, а не
    /// по одному. Итог локального счётчика версии становится ровно newTotal:
    /// • newTotal ≥ уже накопленного по реальным запросам — разница уходит в служебную строку
    ///   (ManualUsageKey), настоящая по-запросная статистика сохраняется;
    /// • newTotal меньше (оператор осознанно занижает/обнуляет) — реальные по-запросные строки этой
    ///   машины для версии удаляются, и остаётся только служебная строка на newTotal.
    /// Чужой вклад (fw_usage_shared) не трогаем — он приходит снимком с других машин; общий показанный
    /// итог (GetFwUsageTotal) поэтому может быть больше newTotal ровно на сумму чужих вкладов.</summary>
    public void SetLocalFwUsageVersionTotal(int fwVersionId, int newTotal)
    {
        if (fwVersionId <= 0) return;
        newTotal = Math.Max(0, newTotal);

        var real = ExecuteScalar(
            "SELECT COALESCE(SUM(uses),0) FROM fw_search_usage WHERE fw_version_id=@v AND query_key<>@m",
            cmd => { cmd.Parameters.AddWithValue("@v", fwVersionId); cmd.Parameters.AddWithValue("@m", ManualUsageKey); })
            is long l ? (int)l : 0;

        if (newTotal < real)
        {
            // Ниже реально накопленного числом не опустить, не тронув сами по-запросные счётчики —
            // оператор явно переопределяет счётчик, поэтому реальные счётчики этой машины обнуляем.
            // Обнуляем ТОЛЬКО uses, а строки с ненулевым ручным весом оставляем: переопределение
            // счётчика открытий не должно заодно стирать ручной вес (см. weight/SetLocalFwWeight).
            ExecuteNonQuery("""
                UPDATE fw_search_usage SET uses = 0 WHERE fw_version_id = @v;
                DELETE FROM fw_search_usage WHERE fw_version_id = @v AND uses <= 0 AND weight <= 0;
                """, cmd => cmd.Parameters.AddWithValue("@v", fwVersionId));
            real = 0;
        }
        // Остаток держим служебной строкой (0 ⇒ SetLocalFwUsage удалит её).
        SetLocalFwUsage(ManualUsageKey, fwVersionId, newTotal - real);
    }

    /// <summary>Запросы, которым ЭТА машина проставила ручной ВЕС для конкретной версии — для
    /// редактора «вес в поиске» в окне модерации прошивки. Именно вес (weight), а не счётчик открытий
    /// (uses): счётчик копится сам и правится в таблице статистики/истории версий, а здесь оператор
    /// задаёт вес осознанно и адресно. Только строки с ненулевым весом (weight&gt;0) — строки, где есть
    /// лишь накопленный счётчик, к «ручному весу» отношения не имеют. Только свой вклад: чужой вес
    /// правится у своей машины-источника. Служебная строка счётчика (ManualUsageKey) сюда не попадает
    /// — у неё нет настоящего запроса.</summary>
    public List<(string QueryKey, int Weight)> GetFwUsageQueriesForVersion(int fwVersionId)
    {
        var result = new List<(string, int)>();
        if (fwVersionId <= 0) return result;
        using var reader = ExecuteReader(
            "SELECT query_key, weight FROM fw_search_usage WHERE fw_version_id=@v AND query_key<>@m AND weight>0 ORDER BY weight DESC, query_key",
            cmd => { cmd.Parameters.AddWithValue("@v", fwVersionId); cmd.Parameters.AddWithValue("@m", ManualUsageKey); });
        while (reader.Read())
            result.Add((GetString(reader, "query_key"), GetInt(reader, "weight")));
        return result;
    }

    /// <summary>Счётчик открытий (Uses) и ручной вес (Weight) каждой версии ИМЕННО по этому запросу —
    /// на всех машинах вместе. И то, и другое суммируется по машинам (свой вклад + чужие снимки):
    /// счётчик — потому что каждый открывал у себя; вес — потому что если и другая машина подняла эту
    /// версию под тот же запрос, общий ручной перевес логично сильнее. Чужой вклад досчитывается
    /// вторым запросом: прошивки, которой на этой машине нет, в выдаче всё равно не будет, поэтому
    /// JOIN по переносимому ключу отбрасывает её сам собой. Ранжирование обходится с двумя числами
    /// по-разному (Database.Search.cs): счётчик — через порог и множитель, вес — напрямую.</summary>
    public Dictionary<int, (int Uses, int Weight)> GetFwUsageForQuery(string queryKey)
    {
        var result = new Dictionary<int, (int Uses, int Weight)>();
        if (string.IsNullOrWhiteSpace(queryKey)) return result;

        void Add(int id, int uses, int weight)
        {
            var cur = result.TryGetValue(id, out var e) ? e : (0, 0);
            result[id] = (cur.Item1 + uses, cur.Item2 + weight);
        }

        using (var reader = ExecuteReader(
            "SELECT fw_version_id, uses, weight FROM fw_search_usage WHERE query_key = @q",
            cmd => cmd.Parameters.AddWithValue("@q", queryKey)))
            while (reader.Read())
                Add(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));

        using (var reader = ExecuteReader($"""
            SELECT fv.id, SUM(u.uses) AS uses, SUM(u.weight) AS weight
            FROM fw_usage_shared u
            {SharedUsageJoin}
            WHERE u.query_key = @q
            GROUP BY fv.id
            """, cmd => cmd.Parameters.AddWithValue("@q", queryKey)))
            while (reader.Read())
                Add(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));

        return result;
    }

    /// <summary>Вклад ТОЛЬКО этой машины в счётчик версии (свои по-запросные строки + служебная
    /// строка ручной правки ManualUsageKey), без чужих снимков (fw_usage_shared). Именно это число
    /// задаёт SetLocalFwUsageVersionTotal, поэтому редактор «кол-во обращений» показывает его как
    /// текущее значение — тогда «оставить как есть» не меняет счётчик (в отличие от общего итога, в
    /// который на многомашинной установке подмешан чужой вклад).</summary>
    public int GetLocalFwUsageTotal(int fwVersionId) =>
        ExecuteScalar("SELECT COALESCE(SUM(uses), 0) FROM fw_search_usage WHERE fw_version_id = @v",
            cmd => cmd.Parameters.AddWithValue("@v", fwVersionId)) is long l ? (int)l : 0;

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

    /// <summary>Вся накопленная статистика разом — таблица просмотра в Настройках → Общие (кому
    /// интересно, что вообще накопилось, не открывая карточку за карточкой в поиске). Свой вклад
    /// (fw_search_usage) и уже известный чужой (fw_usage_shared) складываются по одной и той же
    /// прошивке — тот же принцип, что и в GetFwUsageForQuery, но по всем запросам сразу, а не по
    /// одному. Порог ранжирования (ConfigService.FwUsageThreshold) здесь НЕ применяется — это
    /// таблица «что вообще накопилось», а не «что сейчас двигает выдачу»: строка ниже порога всё
    /// равно должна быть видна, иначе непонятно, почему редкий выбор до сих пор не влияет на поиск.</summary>
    public List<FwUsageStatRow> GetAllFwUsage()
    {
        var byKey = new Dictionary<(string Query, int SubtypeId, int ControllerId, string VersionRaw), FwUsageStatRow>();

        void Add(string query, int subtypeId, int controllerId, string subtypeName, string controllerName,
            string versionRaw, int uses, int weight, string lastUsedAt, int localVersionId, bool isLocal)
        {
            var key = (query, subtypeId, controllerId, versionRaw);
            if (byKey.TryGetValue(key, out var existing))
                byKey[key] = existing with
                {
                    Uses = existing.Uses + uses,
                    Weight = existing.Weight + weight,
                    // Своя доля копится только из своего же читателя (fw_search_usage) — по ней редактор
                    // отделяет правимый свой вклад от неприкасаемого чужого снимка.
                    LocalUses = existing.LocalUses + (isLocal ? uses : 0),
                    LocalWeight = existing.LocalWeight + (isLocal ? weight : 0),
                    LastUsedAt = string.CompareOrdinal(lastUsedAt, existing.LastUsedAt) > 0 ? lastUsedAt : existing.LastUsedAt,
                    // Локальный id одной и той же прошивки одинаков в обеих ветках (свой вклад и чужой
                    // резолвятся в одну строку fw_versions), поэтому берём первый ненулевой.
                    LocalVersionId = existing.LocalVersionId ?? (localVersionId > 0 ? localVersionId : null),
                };
            else
                byKey[key] = new FwUsageStatRow(query, subtypeName, controllerName, versionRaw, uses, lastUsedAt,
                    localVersionId > 0 ? localVersionId : null, weight, isLocal ? uses : 0, isLocal ? weight : 0);
        }

        using (var reader = ExecuteReader("""
            SELECT u.query_key, fv.id AS fw_id, fv.subtype_id, fv.controller_id, es.name AS subtype_name, cm.name AS ctrl_name,
                   fv.version_raw, u.uses, u.weight, u.last_used_at
            FROM fw_search_usage u
            JOIN fw_versions        fv ON fv.id = u.fw_version_id
            JOIN equipment_subtypes es ON es.id = fv.subtype_id
            JOIN controller_models  cm ON cm.id = fv.controller_id
            WHERE u.query_key <> @m
            """, cmd => cmd.Parameters.AddWithValue("@m", ManualUsageKey)))
            while (reader.Read())
                Add(GetString(reader, "query_key"), GetInt(reader, "subtype_id"), GetInt(reader, "controller_id"),
                    GetString(reader, "subtype_name"), GetString(reader, "ctrl_name"),
                    GetString(reader, "version_raw"), GetInt(reader, "uses"), GetInt(reader, "weight"),
                    GetString(reader, "last_used_at"), GetInt(reader, "fw_id"), isLocal: true);

        using (var reader = ExecuteReader($"""
            SELECT u.query_key, fv.id AS fw_id, fv.subtype_id, fv.controller_id, es.name AS subtype_name, cm.name AS ctrl_name,
                   fv.version_raw, u.uses, u.weight, u.last_used_at
            FROM fw_usage_shared u
            {SharedUsageJoin}
            """))
            while (reader.Read())
                Add(GetString(reader, "query_key"), GetInt(reader, "subtype_id"), GetInt(reader, "controller_id"),
                    GetString(reader, "subtype_name"), GetString(reader, "ctrl_name"),
                    GetString(reader, "version_raw"), GetInt(reader, "uses"), GetInt(reader, "weight"),
                    GetString(reader, "last_used_at"), GetInt(reader, "fw_id"), isLocal: false);

        // Строки с нулевым и счётчиком, и весом сюда попасть не должны (пустая строка не хранится), но
        // на всякий случай отбрасываем их — «накопилось 0 выборов, 0 веса» показывать незачем.
        return byKey.Values
            .Where(r => r.Uses > 0 || r.Weight > 0)
            .OrderByDescending(r => r.Uses)
            .ThenByDescending(r => r.Weight)
            .ThenBy(r => r.QueryKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
    /// в снимке вклад остальных.
    ///
    /// <paramref name="includeOwnWeight"/> — делиться ли СВОИМ ручным весом (ConfigService.
    /// FwWeightShared): если нет, свой вес уезжает нулём (счётчик открытий делится всегда — это общая
    /// статистика). Чужой вес пересылается дальше как есть независимо от флага: его прислали машины,
    /// которые сами решили им делиться, а эта только ретранслирует снимок целиком.</summary>
    public List<SharedFwUsageRow> ExportFwUsage(string origin, bool includeOwnWeight = false)
    {
        var result = new List<SharedFwUsageRow>();

        using (var reader = ExecuteReader("""
            SELECT u.query_key, es.sync_id AS subtype_sync_id, cm.sync_id AS controller_sync_id,
                   fv.version_raw, u.uses, u.weight, u.last_used_at
            FROM fw_search_usage u
            JOIN fw_versions        fv ON fv.id = u.fw_version_id
            JOIN equipment_subtypes es ON es.id = fv.subtype_id
            JOIN controller_models  cm ON cm.id = fv.controller_id
            WHERE es.sync_id <> '' AND cm.sync_id <> '' AND u.query_key <> @m
            """, cmd => cmd.Parameters.AddWithValue("@m", ManualUsageKey)))
            while (reader.Read())
                result.Add(new SharedFwUsageRow(origin, GetString(reader, "query_key"),
                    GetString(reader, "subtype_sync_id"), GetString(reader, "controller_sync_id"),
                    GetString(reader, "version_raw"), GetInt(reader, "uses"), GetString(reader, "last_used_at"),
                    includeOwnWeight ? GetInt(reader, "weight") : 0));

        using (var reader = ExecuteReader("""
            SELECT origin, query_key, subtype_sync_id, controller_sync_id, version_raw, uses, weight, last_used_at
            FROM fw_usage_shared WHERE origin <> @o
            """, cmd => cmd.Parameters.AddWithValue("@o", origin)))
            while (reader.Read())
                result.Add(new SharedFwUsageRow(GetString(reader, "origin"), GetString(reader, "query_key"),
                    GetString(reader, "subtype_sync_id"), GetString(reader, "controller_sync_id"),
                    GetString(reader, "version_raw"), GetInt(reader, "uses"), GetString(reader, "last_used_at"),
                    GetInt(reader, "weight")));

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
                    (origin, query_key, subtype_sync_id, controller_sync_id, version_raw, uses, weight, last_used_at)
                VALUES (@o, @q, @s, @c, @v, @u, @w, @t)
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("@o", row.Origin);
                cmd.Parameters.AddWithValue("@q", row.QueryKey);
                cmd.Parameters.AddWithValue("@s", row.SubtypeSyncId);
                cmd.Parameters.AddWithValue("@c", row.ControllerSyncId);
                cmd.Parameters.AddWithValue("@v", row.VersionRaw);
                cmd.Parameters.AddWithValue("@u", row.Uses);
                cmd.Parameters.AddWithValue("@w", row.Weight);
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
