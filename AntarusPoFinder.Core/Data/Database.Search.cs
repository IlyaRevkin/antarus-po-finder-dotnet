using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.Core.Data;

/// <summary>Условия, которыми оператор сужает выдачу поиска, — панель «Фильтры» в поиске. Каждое
/// поле null/пустое означает «не фильтровать по нему». Считаются ДО подсчёта очков: фильтр — это
/// «показывай только такое», а не «подними повыше».</summary>
public record FirmwareSearchFilters
{
    public int? GroupId { get; init; }
    public int? SubtypeId { get; init; }
    public int? ControllerId { get; init; }
    public string? LaunchType { get; init; }

    public static readonly FirmwareSearchFilters None = new();

    public bool IsEmpty =>
        GroupId is null && SubtypeId is null && ControllerId is null &&
        string.IsNullOrWhiteSpace(LaunchType);
}

/// <summary>Строка выдачи вместе с тем, чем она заслужила своё место: очки релевантности, сколько
/// раз ИМЕННО эту версию выбирали по такому же запросу (UsageCount — авто-счётчик открытий) и ручной
/// вес под этот запрос (Weight), проставленный оператором осознанно. И то, и другое — из
/// Database.FwUsage.cs; в Rank они входят по-разному (счётчик через порог и множитель, вес
/// напрямую).</summary>
public record ScoredFwVersion(FwVersionRecord Row, int Score, int UsageCount, int Weight = 0);

public partial class Database
{
    private static readonly Regex WordSplitter = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>True if <paramref name="token"/> occurs in <paramref name="field"/> — either as a
    /// substring (default) or as a whole word delimited by non-letter/digit characters
    /// (<paramref name="exactWord"/>), which is what lets a query for "ПЧ" avoid also matching "КПЧ".</summary>
    private static bool TokenMatches(string token, string? field, bool exactWord)
    {
        if (string.IsNullOrEmpty(field)) return false;
        var f = field.ToUpperInvariant();
        if (!exactWord) return f.Contains(token, StringComparison.Ordinal);
        return WordSplitter.Split(f).Any(w => w == token);
    }

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Схлопывает пробелы, обрезает края, поднимает регистр — но НАМЕРЕННО не трогает
    /// дефисы, точки и прочие разделители. В точном (позиционном) поиске «НГР-2.0» и «НГР 2.0» —
    /// разные запросы: оператор так и просил — если вместо точки поставить пробел, это уже другое
    /// совпадение.</summary>
    private static string CollapseForOrdered(string? s) =>
        WhitespaceRun.Replace(s ?? "", " ").Trim().ToUpperInvariant();

    /// <summary>Точное (позиционное) совпадение: <paramref name="phraseUpper"/> встречается в
    /// <paramref name="haystackUpper"/> как непрерывная подстрока, начинающаяся НА ГРАНИЦЕ СЛОВА —
    /// в начале строки или сразу после разделителя (не буквы и не цифры). Отсюда два свойства,
    /// которые и просил наладчик: «НГР-2» находит «НГР-2.0 SMH5» (запрос — префикс от начала слова,
    /// разделители и порядок совпадают), но «ПЧ» не всплывает внутри «КПЧ» (там совпадение началось
    /// бы в середине слова).</summary>
    private static bool OrderedContains(string phraseUpper, string haystackUpper)
    {
        if (phraseUpper.Length == 0 || haystackUpper.Length == 0) return false;
        var idx = 0;
        while ((idx = haystackUpper.IndexOf(phraseUpper, idx, StringComparison.Ordinal)) >= 0)
        {
            if (idx == 0 || !char.IsLetterOrDigit(haystackUpper[idx - 1])) return true;
            idx++;
        }
        return false;
    }

    // ── Индекс поиска ─────────────────────────────────────────────────────────
    // Раньше КАЖДЫЙ поиск заново вычитывал все fw_versions с тремя JOIN'ами и только потом считал
    // очки в памяти. На каждое нажатие «Найти», на каждое переключение режима, на каждый молчаливый
    // повтор запроса (возврат на вкладку, тик синхронизации, закрытие диалога тегов) — полный
    // проход по таблице на потоке интерфейса. Теперь снимок читается один раз и переиспользуется,
    // пока данные не поменялись: ревизия поднимается на любой записи, которая трогает fw_versions
    // или справочники (см. BumpDataRevisionIfNeeded) — то есть про инвалидацию нельзя забыть,
    // добавив новый метод записи.

    private List<FwVersionRecord>? _searchIndex;
    private int _searchIndexRevision = -1;
    private int _dataRevision;

    /// <summary>Сколько раз данные, которые видит поиск, менялись за жизнь соединения. Публичная —
    /// на неё же опираются тесты, проверяющие, что снимок действительно пересобирается.</summary>
    public int DataRevision => _dataRevision;

    private const string SearchIndexSql = """
        SELECT fv.*,
               es.group_id     AS group_id,
               eg.name         AS group_name,
               es.name         AS subtype_name,
               es.folder_name  AS subtype_folder,
               cm.name         AS ctrl_name
        FROM fw_versions fv
        JOIN equipment_subtypes es ON fv.subtype_id  = es.id
        JOIN equipment_groups   eg ON es.group_id    = eg.id
        JOIN controller_models  cm ON fv.controller_id = cm.id
        WHERE fv.archived = 0 AND (fv.status IS NULL OR fv.status = 'active') AND {0}
        ORDER BY fv.id DESC
        """;

    /// <summary>Таблицы, изменение которых видно поиску. Проверка по тексту запроса — намеренно
    /// грубая: лишний пересбор снимка стоит одного SELECT'а, а пропущенный означал бы выдачу,
    /// которая молча отстаёт от базы.</summary>
    private static readonly string[] SearchAffectingTables =
        { "fw_versions", "equipment_subtypes", "equipment_groups", "controller_models" };

    private void BumpDataRevisionIfNeeded(string sql)
    {
        foreach (var table in SearchAffectingTables)
            if (sql.Contains(table, StringComparison.OrdinalIgnoreCase))
            {
                _dataRevision++;
                return;
            }
    }

    private List<FwVersionRecord> SearchIndex()
    {
        if (_searchIndex is not null && _searchIndexRevision == _dataRevision) return _searchIndex;

        var rows = new List<FwVersionRecord>();
        using (var reader = ExecuteReader(string.Format(SearchIndexSql, NotDeleted("fv"))))
        {
            while (reader.Read())
            {
                var rec = ReadFwVersion(reader);
                rec.GroupId = GetInt(reader, "group_id");
                rec.GroupName = GetString(reader, "group_name");
                rec.SubtypeName = GetString(reader, "subtype_name");
                rec.SubtypeFolder = GetString(reader, "subtype_folder");
                rec.CtrlName = GetString(reader, "ctrl_name");
                rows.Add(rec);
            }
        }

        _searchIndex = rows;
        _searchIndexRevision = _dataRevision;
        return rows;
    }

    // ── Поиск ─────────────────────────────────────────────────────────────────

    /// <summary>Return the highest-scoring fw_version per (subtype_id, controller_id) whose group/
    /// subtype/controller/tag fields contain the query tokens (each query token is matched AGAINST
    /// the field — not the other way around — so a short query like "pixel" finds "pixel2").</summary>
    public List<FwVersionRecord> SearchFwVersionsByTokens(IReadOnlyList<string> tokens, bool exactWord = false) =>
        SearchFwVersions(tokens, exactWord).Select(x => x.Row).ToList();

    /// <summary>Полный вариант с фильтрами и счётчиком выбора. <paramref name="usageQueryKey"/> —
    /// нормализованный запрос, по которому смотрится статистика «что по такому запросу обычно
    /// ставят» (см. Database.FwUsage.cs); пустой — статистика не учитывается.
    /// <paramref name="usageThreshold"/> — сколько раз версию должны выбрать по этому запросу,
    /// прежде чем накопленная частота начнёт двигать выдачу (см. EffectiveUsage/Rank ниже и
    /// ConfigService.FwUsageThreshold); 1 по умолчанию — единственный выбор уже учитывается, ровно
    /// как было до появления настраиваемого порога.
    /// <paramref name="usageMultiplier"/> — на сколько умножать вклад счётчика открытий в ранг (см.
    /// Rank/ConfigService.FwUsageMultiplier); 1 по умолчанию — прежнее поведение. Ручной вес
    /// (Weight) множитель НЕ трогает: он и так задаётся оператором в тех же «баллах», что и потолок
    /// авто-вклада, и складывается напрямую.</summary>
    public List<ScoredFwVersion> SearchFwVersions(IReadOnlyList<string> tokens, bool exactWord = false,
        FirmwareSearchFilters? filters = null, string usageQueryKey = "", string phrase = "", int usageThreshold = 1,
        double usageMultiplier = 1)
    {
        filters ??= FirmwareSearchFilters.None;

        var rows = SearchIndex().Where(r => PassesFilters(r, filters)).ToList();
        if (rows.Count == 0) return new();

        var usage = string.IsNullOrEmpty(usageQueryKey)
            ? new Dictionary<int, (int Uses, int Weight)>()
            : GetFwUsageForQuery(usageQueryKey);

        var qTokens = tokens.Where(t => !string.IsNullOrEmpty(t) && t.Length >= 2)
            .Select(t => t.ToUpperInvariant()).ToArray();

        // Запрос пустой, но фильтры заданы — это осмысленный «покажи всё такое»: выдаём отобранное
        // фильтрами, порядок — по частоте выбора, потом по свежести.
        if (qTokens.Length == 0)
        {
            if (filters.IsEmpty) return new();
            return Deduplicate(rows.Select(r => new ScoredFwVersion(r, 0, UsesOf(r, usage), WeightOf(r, usage))),
                usageThreshold, usageMultiplier);
        }

        // Два принципиально разных режима, а не одно матчирование с флажком:
        //   • Точный (галочка «Точное совпадение слова») — ПОЗИЦИОННЫЙ: запрос целиком, в том же
        //     порядке и с теми же разделителями, должен непрерывно встретиться в названии/тегах —
        //     см. SearchOrdered. Это то, чего ждёт наладчик: «НГР-2» находит «НГР-2.0 SMH5», а
        //     «НГР 2 0» (пробелы вместо точки) — уже нет.
        //   • Обычный — ПО КЛЮЧЕВЫМ СЛОВАМ: каждое слово запроса ищется по отдельности в названии,
        //     типе пуска и тегах (см. SearchByKeywords). Находит шире, ранжирует по числу совпавших
        //     слов, весу и частоте выбора.
        var scored = exactWord
            ? SearchOrdered(rows, tokens, phrase, usage)
            : SearchByKeywords(rows, qTokens, phrase, usage);

        return Deduplicate(scored, usageThreshold, usageMultiplier);
    }

    /// <summary>Обычный поиск: каждое слово запроса (>= 2 символов) ищется подстрокой в полях
    /// названия и тегах, а тип пуска сверяется целым значением. Очки складываются по всем словам —
    /// чем больше слов запроса совпало, тем выше версия. Полное совпадение нормализованного запроса
    /// с ОДНИМ тегом добавляет крупный бонус, поднимая точно поименованную прошивку в самый верх, не
    /// отсекая при этом остальное — обычный поиск остаётся широким.</summary>
    private List<ScoredFwVersion> SearchByKeywords(List<FwVersionRecord> rows, string[] qTokens, string phrase,
        IReadOnlyDictionary<int, (int Uses, int Weight)> usage)
    {
        var normalizedPhrase = string.IsNullOrEmpty(phrase) ? "" : SearchService.Normalize(phrase);
        var scored = new List<ScoredFwVersion>();

        foreach (var row in rows)
        {
            var fields = new[] { row.GroupName, row.SubtypeName, row.SubtypeFolder, row.CtrlName };
            var tags = TagString.Parse(row.Tags);
            var launchTypes = row.LaunchTypes ?? new List<string>();

            int score = 0;
            foreach (var token in qTokens)
            {
                if (fields.Any(f => TokenMatches(token, f, false))) score += 1;
                // Тег весит больше названия папки: тег проставлен человеком осознанно, совпадение в
                // названии подтипа может быть случайным.
                if (tags.Any(t => TokenMatches(token, t, false))) score += 2;
                // Сравнение целым значением, а не подстрокой: список типов пуска закрытый
                // (ConfigService.LaunchTypes), и почти каждый короткий в нём — подстрока длинного
                // («ПЧ» в «КПЧ», «ПП» в «УПП»). Подстрочно «НГР ПЧ» поднимало ещё и шкафы с КПЧ —
                // тип пуска не то поле, где полезно угадывать.
                if (launchTypes.Any(lt => string.Equals(lt, token, StringComparison.OrdinalIgnoreCase)))
                    score += 2;
            }

            // Запрос целиком совпал с ОДНИМ тегом — прямое указание на конкретную прошивку: она
            // всплывает наверх, но выдача не сужается (для «ровно одна прошивка» есть точный поиск).
            if (normalizedPhrase.Length > 0 && tags.Any(t => SearchService.Normalize(t) == normalizedPhrase))
                score += PhraseTagBonus;

            if (score == 0) continue;
            scored.Add(new ScoredFwVersion(row, score, UsesOf(row, usage), WeightOf(row, usage)));
        }

        return scored;
    }

    /// <summary>Точный (позиционный) поиск. Запрос как есть — со схлопнутыми пробелами, но с
    /// сохранёнными дефисами/точками и порядком слов — должен непрерывно встретиться, начиная с
    /// границы слова, либо в тегах (тогда бонус как за тег-фразу), либо в общем «стоге» из названия,
    /// типа пуска и тегов. Стог склеивается через пробел, поэтому запрос вроде «НГР 2.0» находит
    /// версию, у которой «НГР» — тип шкафа, а «2.0» — подтип (два соседних поля), даже если такого
    /// тега нет.</summary>
    private List<ScoredFwVersion> SearchOrdered(List<FwVersionRecord> rows, IReadOnlyList<string> tokens,
        string phrase, IReadOnlyDictionary<int, (int Uses, int Weight)> usage)
    {
        // SearchFwVersionsByTokens зовёт без сырой фразы — тогда собираем её из токенов.
        var phraseUpper = CollapseForOrdered(string.IsNullOrEmpty(phrase) ? string.Join(" ", tokens) : phrase);
        if (phraseUpper.Length < 2) return new();

        var scored = new List<ScoredFwVersion>();
        foreach (var row in rows)
        {
            var tags = TagString.Parse(row.Tags);
            var tagText = CollapseForOrdered(string.Join(" ", tags));

            var parts = new List<string?> { row.GroupName, row.SubtypeName, row.SubtypeFolder, row.CtrlName };
            parts.AddRange(tags);
            parts.AddRange(row.LaunchTypes ?? new List<string>());
            var haystack = CollapseForOrdered(string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p))));

            var inTag = OrderedContains(phraseUpper, tagText);
            if (!inTag && !OrderedContains(phraseUpper, haystack)) continue;

            var score = inTag ? PhraseTagBonus : 3;
            scored.Add(new ScoredFwVersion(row, score, UsesOf(row, usage), WeightOf(row, usage)));
        }

        return scored;
    }

    /// <summary>Потолок вклада ОДНОГО ЛИШЬ счётчика открытий (до умножения на множитель): «десять раз
    /// ставили именно её» должно поднимать версию среди РАВНО подходящих, а не вытаскивать наверх
    /// прошивку от другого шкафа только потому, что её часто открывали. Ручной вес (Weight) этим
    /// потолком НЕ ограничен — он и есть осознанный рычаг «поставить выше»; чтобы гарантированно
    /// обойти самую популярную версию с тем же score, оператору достаточно задать вес больше
    /// MaxUsageBonus×множитель (это число показывается в Настройках как ориентир).</summary>
    private const int MaxUsageBonus = 5;

    /// <summary>Совпадение запроса с тегом целиком весит больше любого набора отдельных слов.</summary>
    private const int PhraseTagBonus = 10;

    private static int UsesOf(FwVersionRecord row, IReadOnlyDictionary<int, (int Uses, int Weight)> usage) =>
        row.Id is int id && usage.TryGetValue(id, out var e) ? e.Uses : 0;

    private static int WeightOf(FwVersionRecord row, IReadOnlyDictionary<int, (int Uses, int Weight)> usage) =>
        row.Id is int id && usage.TryGetValue(id, out var e) ? e.Weight : 0;

    /// <summary>Порог статистики (Задача «порог влияния статистики на ранжирование»): выбор,
    /// которых по этому запросу набралось МЕНЬШЕ порога, ранжирование не двигает вовсе — чтобы один
    /// случайный клик не поднимал версию наравне с той, которую ставят стабильно. Сырое
    /// ScoredFwVersion.UsageCount при этом не трогается нигде — карточка в поиске
    /// («по этому запросу выбирали N раз») продолжает показывать правду независимо от порога,
    /// обрезается только вклад в Rank/сортировку ниже. Ручного веса порог не касается — он задан
    /// осознанно и действует сразу.</summary>
    private static int EffectiveUsage(int uses, int usageThreshold) => uses >= Math.Max(1, usageThreshold) ? uses : 0;

    /// <summary>Вклад авто-счётчика открытий в ранг: обрезанный потолком MaxUsageBonus и умноженный
    /// на множитель (ConfigService.FwUsageMultiplier). Множитель 0 полностью отключает влияние
    /// популярности, оставляя ранжирование на очках релевантности и ручном весе.</summary>
    private static int AutoUsageBonus(int uses, int usageThreshold, double usageMultiplier) =>
        (int)Math.Round(Math.Min(EffectiveUsage(uses, usageThreshold), MaxUsageBonus) * Math.Max(0, usageMultiplier),
            MidpointRounding.AwayFromZero);

    private static List<ScoredFwVersion> Deduplicate(IEnumerable<ScoredFwVersion> scored, int usageThreshold,
        double usageMultiplier)
    {
        var seen = new Dictionary<(int, int), ScoredFwVersion>();
        foreach (var entry in scored)
        {
            var key = (entry.Row.SubtypeId, entry.Row.ControllerId);
            if (!seen.TryGetValue(key, out var existing) ||
                Rank(entry, usageThreshold, usageMultiplier) > Rank(existing, usageThreshold, usageMultiplier))
                seen[key] = entry;
        }

        return seen.Values
            .OrderByDescending(e => Rank(e, usageThreshold, usageMultiplier))
            .ThenByDescending(e => AutoUsageBonus(e.UsageCount, usageThreshold, usageMultiplier) + e.Weight)
            .ThenByDescending(e => e.Row.Id ?? 0)
            .ToList();
    }

    /// <summary>Ранг = очки релевантности + вклад счётчика открытий (с потолком и множителем) + ручной
    /// вес (напрямую, без потолка). Ручной вес живёт в тех же «баллах», что и очки/авто-вклад, — так
    /// оператор понимает, какое число ставить: больше MaxUsageBonus×множитель, чтобы обойти
    /// популярность, больше PhraseTagBonus, чтобы обойти даже точное совпадение тега.</summary>
    private static int Rank(ScoredFwVersion e, int usageThreshold, double usageMultiplier) =>
        e.Score + AutoUsageBonus(e.UsageCount, usageThreshold, usageMultiplier) + e.Weight;

    private static bool PassesFilters(FwVersionRecord row, FirmwareSearchFilters f)
    {
        if (f.GroupId is int g && row.GroupId != g) return false;
        if (f.SubtypeId is int s && row.SubtypeId != s) return false;
        if (f.ControllerId is int c && row.ControllerId != c) return false;
        if (!string.IsNullOrWhiteSpace(f.LaunchType) &&
            !(row.LaunchTypes ?? new List<string>()).Any(lt => string.Equals(lt, f.LaunchType, StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    /// <summary>Все теги, реально проставленные на непустых (не удалённых, активных) версиях —
    /// для выпадающего списка тегов в фильтрах поиска. Отличается от GetAllTags(): тот отдаёт
    /// справочник целиком, включая теги, которые ещё никому не поставили.</summary>
    public List<string> GetTagsInUse()
    {
        var tags = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var row in SearchIndex())
            foreach (var tag in TagString.Parse(row.Tags))
                tags.Add(tag);
        return tags.ToList();
    }

    /// <summary>Same token-matching approach as <see cref="SearchFwVersionsByTokens"/>, applied to
    /// uploaded parameter files (matched by group/subtype/manufacturer/filename/tags).</summary>
    public List<ParamFile> SearchParamFilesByTokens(IReadOnlyList<string> tokens, bool exactWord = false)
    {
        var qTokens = tokens.Where(t => !string.IsNullOrEmpty(t) && t.Length >= 2)
            .Select(t => t.ToUpperInvariant()).ToArray();
        if (qTokens.Length == 0) return new();

        var files = GetParamFiles();

        int Score(ParamFile f)
        {
            var fields = new[] { f.GroupName, f.SubtypeName, f.FolderName, f.Manufacturer, f.Filename };
            var tags = TagString.Parse(f.Tags);

            int score = qTokens.Count(token => fields.Any(field => TokenMatches(token, field, exactWord)));
            score += qTokens.Count(token => tags.Any(t => TokenMatches(token, t, exactWord))) * 2;
            return score;
        }

        return files.Select(f => (File: f, Score: Score(f)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.File)
            .ToList();
    }
}
