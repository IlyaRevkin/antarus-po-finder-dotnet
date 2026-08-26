using System.Collections.Generic;
using System.Linq;
using System.Text;
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
/// <summary><paramref name="MatchedTokens"/> — сколько РАЗНЫХ слов запроса совпало с этой версией
/// в обычном поиске (см. SearchByKeywords). Нужно выдаче, чтобы отделить «совпало всё, что ввели»
/// от «совпало только одно общее слово» и убрать вторые под «Показать ещё». В точном поиске и в
/// пустом запросе с фильтрами число одинаково у всех строк — там сворачивать нечего.</summary>
public record ScoredFwVersion(FwVersionRecord Row, int Score, int UsageCount, int Weight = 0, int MatchedTokens = 0);

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
        { "fw_versions", "equipment_subtypes", "equipment_groups", "controller_models", "fw_attachments" };

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

        // Доп. материалы — отдельным чтением, а не JOIN'ом к запросу выше: вложений у версии может
        // быть сколько угодно, и JOIN размножил бы строки прошивок. Одно чтение на пересборку снимка,
        // а не по запросу на карточку (см. GetFwAttachmentSearchText).
        var attachments = GetFwAttachmentSearchText();
        if (attachments.Count > 0)
            foreach (var rec in rows)
                if (rec.Id is int id && attachments.TryGetValue(id, out var text))
                    rec.AttachmentsText = text;

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
            : SearchByKeywords(rows, RepairMixedLayout(qTokens, rows), phrase, usage);

        return Deduplicate(scored, usageThreshold, usageMultiplier);
    }

    /// <summary>Обычный (не «в кавычках») поиск — модель «как в Гугле»: находит широко по отдельным
    /// словам, а порядок выдачи определяют два принципа, которые и просил наладчик — «чем больше слов
    /// запроса совпало и чем ближе они к тому, как написано в запросе, тем выше».
    ///   1. Число СОВПАВШИХ СЛОВ доминирует (MatchedTokenWeight): версия, где нашлись 3 слова из 3,
    ///      всегда выше той, где нашлись 2, — сколько бы «весомых» тегов ни было у второй.
    ///   2. Внутри равного числа слов вес полей (тег > название > тип пуска) и точность фразы решают
    ///      порядок: все слова запроса совпали (AllTokensBonus), слова стоят рядом и в том же
    ///      порядке, что в запросе (PhraseAdjacencyBonus), запрос целиком равен одному тегу
    ///      (PhraseTagBonus — прямое указание на конкретную прошивку). Всё это ПОДНИМАЕТ, но НЕ
    ///      отсекает — обычный поиск остаётся широким (сужает — только галочка «в кавычках»).</summary>
    private List<ScoredFwVersion> SearchByKeywords(List<FwVersionRecord> rows, string[] qTokens, string phrase,
        IReadOnlyDictionary<int, (int Uses, int Weight)> usage)
    {
        var normalizedPhrase = string.IsNullOrEmpty(phrase) ? "" : SearchService.Normalize(phrase);
        // Фраза для проверки соседства слов — со схлопнутыми пробелами, но сохранёнными
        // дефисами/точками и порядком, ровно как в точном поиске (см. SearchOrdered).
        var orderedPhrase = CollapseForOrdered(string.IsNullOrEmpty(phrase) ? string.Join(" ", qTokens) : phrase);
        // Считается один раз на весь поиск, а не на каждую строку: подстановка в САМОМ запросе —
        // редкий случай (ищут по шаблону), а её проверка иначе повторялась бы на каждой прошивке.
        var queryHasWildcard = TagPattern.HasWildcard(normalizedPhrase) || TagPattern.HasWildcard(orderedPhrase);
        var scored = new List<ScoredFwVersion>();

        foreach (var row in rows)
        {
            var fields = new[] { row.GroupName, row.SubtypeName, row.SubtypeFolder, row.CtrlName };
            var tags = TagString.Parse(row.Tags);
            var launchTypes = row.LaunchTypes ?? new List<string>();

            int matchedTokens = 0;
            int weighted = 0;
            foreach (var token in qTokens)
            {
                bool hit = false;
                if (fields.Any(f => TokenMatches(token, f, false))) { weighted += 1; hit = true; }
                // Тег весит больше названия папки: тег проставлен человеком осознанно, совпадение в
                // названии подтипа может быть случайным.
                if (tags.Any(t => TokenMatches(token, t, false))) { weighted += 2; hit = true; }
                // Сравнение целым значением, а не подстрокой: список типов пуска закрытый
                // (ConfigService.LaunchTypes), и почти каждый короткий в нём — подстрока длинного
                // («ПЧ» в «КПЧ», «ПП» в «УПП»). Подстрочно «НГР ПЧ» поднимало ещё и шкафы с КПЧ —
                // тип пуска не то поле, где полезно угадывать.
                if (launchTypes.Any(lt => string.Equals(lt, token, StringComparison.OrdinalIgnoreCase)))
                { weighted += 2; hit = true; }
                // Вид и комментарий доп. материала: «краткое руководство для наладчика» пишут ровно
                // затем, чтобы файл потом нашли этими словами. Вес как у названия, а не как у тега:
                // комментарий — свободный текст в несколько слов, случайное пересечение в нём куда
                // вероятнее, чем в теге, который вешают осознанно и коротко.
                if (TokenMatches(token, row.AttachmentsText, false)) { weighted += 1; hit = true; }
                if (hit) matchedTokens++;
            }

            // Тег-шаблон, совпавший с запросом ЦЕЛИКОМ (см. AnyTagMatchesWholeQuery), — это прямое
            // попадание в конкретный шкаф, а не случайное пересечение по словам. Засчитываем его как
            // «совпали все слова запроса»: иначе строка либо не попала бы в выдачу вовсе (буквальных
            // совпадений может не быть ни одного — «(9-14А)» ни с чем в шаблоне «(*-*А)» посимвольно
            // не совпадает), либо уехала бы под «Показать ещё» как слабое совпадение.
            var wildcardTagHit = AnyTagMatchesWholeQuery(tags, queryHasWildcard, normalizedPhrase, orderedPhrase);
            if (wildcardTagHit) matchedTokens = qTokens.Length;

            if (matchedTokens == 0) continue;

            // «Больше совпавших слов → выше»: число совпавших слов доминирует над вкладом весов полей.
            int score = matchedTokens * MatchedTokenWeight + weighted;

            // «Точнее запрос → выше» — три ступени точности, каждая только поднимает:
            // (а) совпали ВСЕ слова запроса — уверенное попадание, а не случайное пересечение по одному слову;
            if (qTokens.Length > 1 && matchedTokens == qTokens.Length) score += AllTokensBonus;

            // (б) слова стоят рядом и в том же порядке, что в запросе, — самый точный из широких результатов;
            if (orderedPhrase.Length >= 2)
            {
                var tagText = CollapseForOrdered(string.Join(" ", tags));
                if (OrderedContains(orderedPhrase, tagText) || OrderedContains(orderedPhrase, BuildOrderedHaystack(row, tags)))
                    score += PhraseAdjacencyBonus;
            }

            // (в) запрос целиком равен ОДНОМУ тегу — прямое указание на конкретную прошивку: в самый
            // верх, но выдача не сужается (для «ровно одна прошивка» есть галочка «в кавычках»).
            if (wildcardTagHit ||
                (normalizedPhrase.Length > 0 && tags.Any(t => SearchService.Normalize(t) == normalizedPhrase)))
                score += PhraseTagBonus;

            scored.Add(new ScoredFwVersion(row, score, UsesOf(row, usage), WeightOf(row, usage), matchedTokens));
        }

        return scored;
    }

    /// <summary>Чинит РАСКЛАДКУ каждого слова обычного поиска по отдельности — в отличие от сплошной
    /// замены всего запроса в SearchService.SearchWithLayoutFallback, которая срабатывает, только если
    /// как есть не нашлось НИЧЕГО. Оператор пишет вперемешку: «рукея» (это «hertz», набранный на
    /// ЙЦУКЕН, не переключив раскладку), «кпч» русскими, «smh» латиницей. Сплошная замена такой запрос
    /// испортила бы («кпч»→«rgx», «smh»→«ыьр»), а как есть — «рукея» не совпадает ни с чем, и НГР
    /// находится лишь по общим «кпч»/«smh», вытаскивая заодно чужие SMH. Поэтому слово переводим в
    /// другую раскладку, только если как есть его нет в индексе, а переведённое — есть: та же гарантия
    /// «не превратит верное совпадение в ошибочное», что и у сплошной замены, но послованно.
    ///
    /// Работает ТОЛЬКО для смешанного запроса — когда хоть одно слово совпало как есть. Если как есть
    /// не совпало ни одно (вся раскладка неверная), чинить тут нечего: такой запрос вернёт пустую
    /// выдачу, и его целиком подхватит SearchService.SearchWithLayoutFallback (с вопросом «была
    /// включена не та раскладка?»), поведение которого мы не трогаем.</summary>
    private static string[] RepairMixedLayout(string[] qTokens, List<FwVersionRecord> rows)
    {
        if (qTokens.Length < 2) return qTokens;

        var hay = BuildLayoutHaystack(rows);
        // Хоть одно слово должно совпадать как есть — иначе это не «смешанная», а сплошь неверная
        // раскладка, и ею занимается сплошной фолбэк уровнем выше.
        if (!qTokens.Any(t => hay.Contains(t, StringComparison.Ordinal))) return qTokens;

        var repaired = new string[qTokens.Length];
        var changed = false;
        for (var i = 0; i < qTokens.Length; i++)
        {
            var token = qTokens[i];
            if (hay.Contains(token, StringComparison.Ordinal)) { repaired[i] = token; continue; }

            var converted = SearchService.ConvertLayout(token).ToUpperInvariant();
            if (converted != token && hay.Contains(converted, StringComparison.Ordinal))
            {
                repaired[i] = converted;
                changed = true;
            }
            else repaired[i] = token;
        }

        return changed ? repaired : qTokens;
    }

    /// <summary>Один большой «стог» из всех искомых полей индекса (uppercase), поля разделены '\n',
    /// чтобы подстрочная проверка слова из <see cref="RepairMixedLayout"/> не склеивала соседние
    /// поля в ложное совпадение. Токены поиска — минимум 2 буквенно-цифровых символа, '\n' в них не
    /// попадает.</summary>
    private static string BuildLayoutHaystack(List<FwVersionRecord> rows)
    {
        var sb = new StringBuilder();

        void Append(string? s)
        {
            if (string.IsNullOrEmpty(s)) return;
            sb.Append('\n').Append(s.ToUpperInvariant());
        }

        foreach (var row in rows)
        {
            Append(row.GroupName);
            Append(row.SubtypeName);
            Append(row.SubtypeFolder);
            Append(row.CtrlName);
            foreach (var tag in TagString.Parse(row.Tags)) Append(tag);
            foreach (var lt in row.LaunchTypes ?? new List<string>()) Append(lt);
            Append(row.AttachmentsText);
        }

        return sb.ToString();
    }

    /// <summary>Совпал ли запрос ЦЕЛИКОМ хотя бы с одним тегом строки С УЧЁТОМ ПОДСТАНОВКИ —
    /// звёздочки в теге (или в самом запросе), см. <see cref="TagPattern"/>. Отвечает ровно за
    /// шаблоны: обычное равенство «запрос = тег» проверяется отдельно и осталось прежним.
    ///
    /// Сверяем в двух нормализациях сразу, потому что запрос доезжает сюда в обеих и обе одинаково
    /// осмысленны: <paramref name="normalizedPhrase"/> — как SearchService.Normalize (скобки и дефисы
    /// заменены пробелами: «ПЖ-ПП-2-(9-14А)» → «ПЖ ПП 2 9 14А»), <paramref name="orderedPhrase"/> —
    /// как CollapseForOrdered (разделители сохранены). Тег-шаблон «…(*-*А)…» совпадает с конкретным
    /// названием шкафа в любой из них.
    ///
    /// Быстрый отсев: если звёздочки нет ни в запросе, ни в конкретном теге — сравнение вообще не
    /// выполняется. Тегов без звёздочки подавляющее большинство, и на них поиск обязан стоить ровно
    /// столько же, сколько стоил до появления шаблонов (полного перебора всех тегов регулярками, о
    /// котором просили не делать, здесь нет вовсе — только IndexOf('*') на короткой строке).</summary>
    private static bool AnyTagMatchesWholeQuery(IReadOnlyList<string> tags, bool queryHasWildcard,
        string normalizedPhrase, string orderedPhrase)
    {
        if (tags.Count == 0) return false;
        foreach (var tag in tags)
        {
            if (!queryHasWildcard && !TagPattern.HasWildcard(tag)) continue;
            if (normalizedPhrase.Length > 0 && TagPattern.MatchesEither(SearchService.Normalize(tag), normalizedPhrase))
                return true;
            if (orderedPhrase.Length > 0 && TagPattern.MatchesEither(CollapseForOrdered(tag), orderedPhrase))
                return true;
        }
        return false;
    }

    /// <summary>Общий «стог» для позиционных проверок: название группы/подтипа/папки/контроллера +
    /// теги + типы пуска, склеенные через пробел и приведённые к CollapseForOrdered. Собран одним
    /// местом, чтобы обычный и точный поиск считали соседство слов одинаково.</summary>
    private static string BuildOrderedHaystack(FwVersionRecord row, IReadOnlyList<string> tags)
    {
        var parts = new List<string?> { row.GroupName, row.SubtypeName, row.SubtypeFolder, row.CtrlName };
        parts.AddRange(tags);
        parts.AddRange(row.LaunchTypes ?? new List<string>());
        // Доп. материалы — в общем стоге наравне с остальным: в кавычках («точное совпадение») ищут
        // в том числе и точную формулировку из комментария к файлу.
        parts.Add(row.AttachmentsText);
        return CollapseForOrdered(string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p))));
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

        // Точное совпадение с тегом-ШАБЛОНОМ (см. AnyTagMatchesWholeQuery/TagPattern): наладчик
        // вводит полное название шкафа в кавычках — и находит прошивку, помеченную тегом со
        // звёздочкой вместо ампеража. Нормализованная форма запроса считается один раз на весь поиск.
        var normalizedPhrase = string.IsNullOrEmpty(phrase) ? "" : SearchService.Normalize(phrase);
        var queryHasWildcard = TagPattern.HasWildcard(normalizedPhrase) || TagPattern.HasWildcard(phraseUpper);

        var scored = new List<ScoredFwVersion>();
        foreach (var row in rows)
        {
            var tags = TagString.Parse(row.Tags);
            var tagText = CollapseForOrdered(string.Join(" ", tags));
            var haystack = BuildOrderedHaystack(row, tags);

            var inTag = OrderedContains(phraseUpper, tagText) ||
                        AnyTagMatchesWholeQuery(tags, queryHasWildcard, normalizedPhrase, phraseUpper);
            if (!inTag && !OrderedContains(phraseUpper, haystack)) continue;

            var score = inTag ? PhraseTagBonus : 3;
            // Точный (позиционный) поиск: либо вся фраза совпала целиком, либо строки нет в выдаче —
            // «частичных» совпадений тут не бывает. Ставим всем одинаковое число, чтобы выдача не
            // прятала ничего под «Показать ещё» в этом режиме.
            scored.Add(new ScoredFwVersion(row, score, UsesOf(row, usage), WeightOf(row, usage), tokens.Count));
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

    /// <summary>Вес одного совпавшего слова запроса в обычном поиске. Держится заведомо выше суммы
    /// весов полей за одно слово (тег 2 + название 1 + тип пуска 2 = 5 максимум), чтобы «совпало
    /// больше слов запроса» почти всегда перевешивало «слово нашлось в более весомом поле» —
    /// это и есть «больше совпадений → выше».</summary>
    private const int MatchedTokenWeight = 8;

    /// <summary>Бонус за то, что совпали ВСЕ слова многословного запроса (а не часть).</summary>
    private const int AllTokensBonus = 4;

    /// <summary>Бонус за то, что слова запроса стоят в выдаче рядом и в том же порядке, что в запросе
    /// (позиционное совпадение фразы). Ниже PhraseTagBonus: точное равенство одному тегу — сильнее,
    /// чем просто соседство слов.</summary>
    private const int PhraseAdjacencyBonus = 6;

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

    /// <summary>КОНФИГУРАЦИИ шкафа (см. столбец config_name в Database.cs) — это записи одной и той же
    /// прошивки с разными наборами тегов, и схлопывает их в одну строку выдачи ровно тот же механизм,
    /// что и всегда: одна строка на пару (подтип, контроллер), с максимальным рангом. Наладчик, вбивший
    /// название своего шкафа, получает ту конфигурацию, чьи теги совпали, а не десяток одинаковых
    /// прошивок — это и требовалось.
    ///
    /// Единственная добавка — предпочтение при РАВНОМ ранге: побеждает основная запись (config_name
    /// пуст). На «общем» запросе («НГР SMH5», пустой запрос с фильтрами) все конфигурации набирают
    /// одинаковые очки, и без этого правила наверх выходила бы произвольная из них — наладчик видел бы
    /// комплектацию соседнего шкафа там, где вопрос о конкретной комплектации вообще не стоял.</summary>
    private static List<ScoredFwVersion> Deduplicate(IEnumerable<ScoredFwVersion> scored, int usageThreshold,
        double usageMultiplier)
    {
        var seen = new Dictionary<(int, int), ScoredFwVersion>();
        foreach (var entry in scored)
        {
            var key = (entry.Row.SubtypeId, entry.Row.ControllerId);
            if (!seen.TryGetValue(key, out var existing))
            {
                seen[key] = entry;
                continue;
            }

            var rank = Rank(entry, usageThreshold, usageMultiplier);
            var existingRank = Rank(existing, usageThreshold, usageMultiplier);
            if (rank > existingRank ||
                (rank == existingRank && entry.Row.ConfigName.Length == 0 && existing.Row.ConfigName.Length > 0))
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

    /// <summary>Найденный ДОКУМЕНТ-таблица параметров.
    ///
    /// <paramref name="Rows"/> — те строки последней живой редакции, которые совпали с запросом. Они
    /// нужны не для красоты: наладчик ищет «P0-10» или «максимальная частота», и ответ «нашёлся
    /// документ „Задание Modbus“» без строки, из-за которой он нашёлся, заставляет открывать документ
    /// и искать глазами заново.</summary>
    public record ParamTableHit(ParamTable Table, int Score, List<ParamTableRow> Rows, string Subtypes);

    /// <summary>Поиск по документам-таблицам параметров: название, теги, производитель, имя файла —
    /// и СОДЕРЖИМОЕ последней живой редакции (код настройки, название параметра, описание).
    ///
    /// Содержимое здесь главное. Просьба владельца была дословной: «наладчик на объекте должен
    /// находить нужную таблицу», а ищет он не по названию документа (его он не помнит), а по коду
    /// параметра или по тому, как параметр называется в аппарате.
    ///
    /// Ищем по ПОСЛЕДНЕЙ живой редакции, а не по всем сразу: прежние редакции — прошлое, и находка
    /// в снятом два года назад значении сбивала бы с толку. Какая редакция последняя, решает
    /// ParamTableNumbering (по времени, а не по хранимому номеру — тот присвоила чужая машина).</summary>
    public List<ParamTableHit> SearchParamTablesByTokens(IReadOnlyList<string> tokens, bool exactWord = false)
    {
        var qTokens = tokens.Where(t => !string.IsNullOrEmpty(t) && t.Length >= 2)
            .Select(t => t.ToUpperInvariant()).ToArray();
        if (qTokens.Length == 0) return new();

        var hits = new List<ParamTableHit>();
        foreach (var table in GetParamTables())
        {
            if (table.Id is not int tableId) continue;

            var head = new[] { table.Name, table.Manufacturer, table.Filename };
            var tags = TagString.Parse(table.Tags);

            // Веса: название/производитель/имя файла — 2, теги — 3, содержимое — 1. Документ,
            // НАЗВАННЫЙ искомым словом, обязан стоять выше документа, где это слово просто
            // встречается в сорока строках; тег ставят руками, и он точнее всего остального.
            var score = qTokens.Count(token => head.Any(field => TokenMatches(token, field, exactWord))) * 2;
            score += qTokens.Count(token => tags.Any(t => TokenMatches(token, t, exactWord))) * 3;

            var matched = BestMatchingRows(LatestParamTableRows(tableId), qTokens, exactWord);
            // Счёт по строкам — ОДИН РАЗ НА СЛОВО, а не по числу совпавших строк: иначе таблица на
            // сорок строк перебивала бы точное попадание в название просто своим размером.
            score += qTokens.Count(token => matched.Any(r => RowMatches(r, token, exactWord)));

            if (score == 0) continue;

            var subtypes = string.Join(", ", Services.ParamTableBinding
                .For(this, table.DiskPath, table.Filename).Links.Select(l => l.Display));
            hits.Add(new ParamTableHit(table, score, matched, subtypes));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Table.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private bool RowMatches(ParamTableRow row, string token, bool exactWord) =>
        TokenMatches(token, row.Code, exactWord)
        || TokenMatches(token, row.Title, exactWord)
        || TokenMatches(token, row.Description, exactWord);

    /// <summary>Какие строки показать как «вот из-за чего нашлось».
    ///
    /// ⚠️ <b>Код настройки разбивается на слова.</b> SearchService.Normalize считает дефис
    /// разделителем (иначе не нашлись бы «НГР-2.0» и «НГР 2 0» одним запросом), поэтому «P0-10»
    /// приходит сюда двумя словами — «P0» и «10». Отдай мы все строки, где встретилось хоть одно,
    /// и на запрос «P0-10» человек получил бы весь блок P0-xx.
    ///
    /// Правило: если есть строки, совпавшие со ВСЕМИ словами запроса, показываем только их. Нет
    /// таких — берём те, что совпали с наибольшим числом слов.</summary>
    private List<ParamTableRow> BestMatchingRows(List<ParamTableRow> rows, string[] tokens, bool exactWord)
    {
        var scored = rows
            .Select(r => (Row: r, Hits: tokens.Count(t => RowMatches(r, t, exactWord))))
            .Where(x => x.Hits > 0)
            .ToList();
        if (scored.Count == 0) return new();

        var best = scored.Max(x => x.Hits);
        return scored.Where(x => x.Hits == best).Select(x => x.Row).ToList();
    }

    /// <summary>Строки последней ЖИВОЙ редакции документа — то, что документ означает сегодня.</summary>
    public List<ParamTableRow> LatestParamTableRows(int tableId)
    {
        var latest = Services.ParamTableNumbering.LiveRevisions(this, tableId).FirstOrDefault();
        return latest?.Id is int revisionId ? GetParamTableRows(revisionId) : new();
    }
}
