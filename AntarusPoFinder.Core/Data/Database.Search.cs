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

/// <summary>Строка выдачи вместе с тем, чем она заслужила своё место: очки релевантности и сколько
/// раз ИМЕННО эту версию выбирали по такому же запросу (Database.FwUsage.cs).</summary>
public record ScoredFwVersion(FwVersionRecord Row, int Score, int UsageCount);

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
    /// ставят» (см. Database.FwUsage.cs); пустой — статистика не учитывается.</summary>
    public List<ScoredFwVersion> SearchFwVersions(IReadOnlyList<string> tokens, bool exactWord = false,
        FirmwareSearchFilters? filters = null, string usageQueryKey = "", string phrase = "")
    {
        filters ??= FirmwareSearchFilters.None;

        var rows = SearchIndex().Where(r => PassesFilters(r, filters)).ToList();
        if (rows.Count == 0) return new();

        var usage = string.IsNullOrEmpty(usageQueryKey)
            ? new Dictionary<int, int>()
            : GetFwUsageForQuery(usageQueryKey);

        var qTokens = tokens.Where(t => !string.IsNullOrEmpty(t) && t.Length >= 2)
            .Select(t => t.ToUpperInvariant()).ToArray();

        // Запрос пустой, но фильтры заданы — это осмысленный «покажи всё такое»: выдаём отобранное
        // фильтрами, порядок — по частоте выбора, потом по свежести.
        if (qTokens.Length == 0)
        {
            if (filters.IsEmpty) return new();
            return Deduplicate(rows.Select(r => new ScoredFwVersion(r, 0, Uses(r, usage))));
        }

        var normalizedPhrase = string.IsNullOrEmpty(phrase) ? "" : SearchService.Normalize(phrase);

        var scored = new List<ScoredFwVersion>();
        bool anyPhraseTagHit = false;
        var phraseTagRows = new List<ScoredFwVersion>();

        foreach (var row in rows)
        {
            var fields = new[] { row.GroupName, row.SubtypeName, row.SubtypeFolder, row.CtrlName };
            var tags = TagString.Parse(row.Tags);
            var launchTypes = row.LaunchTypes ?? new List<string>();

            int score = 0;
            int matchedTokens = 0;
            foreach (var token in qTokens)
            {
                bool hit = false;
                if (fields.Any(f => TokenMatches(token, f, exactWord))) { score += 1; hit = true; }
                // Тег весит больше названия папки: тег проставлен человеком осознанно, совпадение в
                // названии подтипа может быть случайным.
                if (tags.Any(t => TokenMatches(token, t, exactWord))) { score += 2; hit = true; }
                // Сравнение целым значением, а не подстрокой, и НЕЗАВИСИМО от «точного совпадения
                // слова»: список типов пуска закрытый (ConfigService.LaunchTypes), и почти каждый
                // короткий в нём — подстрока длинного («ПЧ» в «КПЧ», «ПП» в «УПП»). Подстрочно
                // «НГР ПЧ» поднимало ещё и шкафы с КПЧ — тип пуска не то поле, где полезно угадывать.
                if (launchTypes.Any(lt => string.Equals(lt, token, StringComparison.OrdinalIgnoreCase)))
                {
                    score += 2;
                    hit = true;
                }
                if (hit) matchedTokens++;
            }

            // Запрос целиком совпал с ОДНИМ тегом («шкаф управления пожарными насосами Антарус
            // ПЖ-ПП-2-…»): это уже не совпадение слов, а прямое указание на конкретную прошивку.
            var phraseTag = normalizedPhrase.Length > 0 &&
                tags.Any(t => SearchService.Normalize(t) == normalizedPhrase);
            if (phraseTag)
            {
                score += PhraseTagBonus;
                matchedTokens = qTokens.Length;
                anyPhraseTagHit = true;
            }

            if (score == 0) continue;

            // «Точное совпадение слова» — это ещё и «все слова запроса, а не любое из них». Раньше
            // хватало одного совпавшего слова: тег с полным названием шкафа поднимал нужную версию
            // наверх, но следом шло всё, что случайно совпало словом «шкаф». Оператор, поставивший
            // галочку и вбивший точное название, ждёт ровно одну прошивку — теперь так и есть.
            if (exactWord && matchedTokens < qTokens.Length) continue;

            var entry = new ScoredFwVersion(row, score, Uses(row, usage));
            scored.Add(entry);
            if (phraseTag) phraseTagRows.Add(entry);
        }

        // Тег-фраза найдена — остальное к этому запросу отношения не имеет.
        if (anyPhraseTagHit && exactWord) scored = phraseTagRows;

        return Deduplicate(scored);
    }

    /// <summary>Насколько сильно частота выбора может подвинуть выдачу. Ограничена сознательно:
    /// «десять раз ставили именно её» должно поднимать версию среди РАВНО подходящих, а не
    /// вытаскивать наверх прошивку от другого шкафа только потому, что её часто открывали.</summary>
    private const int MaxUsageBonus = 5;

    /// <summary>Совпадение запроса с тегом целиком весит больше любого набора отдельных слов.</summary>
    private const int PhraseTagBonus = 10;

    private static int Uses(FwVersionRecord row, IReadOnlyDictionary<int, int> usage) =>
        row.Id is int id && usage.TryGetValue(id, out var n) ? n : 0;

    private static List<ScoredFwVersion> Deduplicate(IEnumerable<ScoredFwVersion> scored)
    {
        var seen = new Dictionary<(int, int), ScoredFwVersion>();
        foreach (var entry in scored)
        {
            var key = (entry.Row.SubtypeId, entry.Row.ControllerId);
            if (!seen.TryGetValue(key, out var existing) ||
                Rank(entry) > Rank(existing))
                seen[key] = entry;
        }

        return seen.Values
            .OrderByDescending(Rank)
            .ThenByDescending(e => e.UsageCount)
            .ThenByDescending(e => e.Row.Id ?? 0)
            .ToList();
    }

    private static int Rank(ScoredFwVersion e) => e.Score + Math.Min(e.UsageCount, MaxUsageBonus);

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
