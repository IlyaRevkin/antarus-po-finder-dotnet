using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>A single search hit, replacing the Python app's duck-typed _HierarchyRule/_HierarchyVersion.</summary>
public class HierarchyResult
{
    public int SubtypeId { get; init; }
    public int ControllerId { get; init; }
    public string Name { get; init; } = "";
    public string Controller { get; init; } = "";
    public string EquipmentType { get; init; } = "";

    /// <summary>Имя подтипа само по себе — то, каким оно стоит СЕГМЕНТОМ ПУТИ на диске. В
    /// <see cref="Name"/> его взять нельзя: там подпись для человека, склеенная из папки подтипа и
    /// контроллера («ПЖ-FD SMH5»). Нужно, чтобы карточка знала, где лежат документы ЕЁ записи, а не
    /// того подтипа, чьи файлы она показывает (см. <see cref="VersionDocFolders"/>).</summary>
    public string SubtypeName { get; init; } = "";
    public string WorkType { get; init; } = "";
    public string IoMapPath { get; init; } = "";
    public string InstructionsPath { get; init; } = "";
    public string ModbusMapPath { get; init; } = "";
    public string HmiPath { get; init; } = "";
    public string ExecutableHint { get; init; } = "";
    public string HmiExecutableHint { get; init; } = "";

    /// <summary>Absolute path to the version folder on the network disk.</summary>
    public string FirmwareDir { get; init; } = "";

    public string VersionRaw { get; init; } = "";
    public string Description { get; init; } = "";
    public string Tags { get; init; } = "";

    /// <summary>Имя КОНФИГУРАЦИИ шкафа, если выдача попала именно в неё (см. столбец config_name в
    /// Database.cs и FirmwareConfigService). Пусто — обычная прошивка, показывать нечего.
    ///
    /// Конфигурация — не отдельная прошивка: файлы, номер версии и папка на диске те же самые,
    /// отличается только комплектация шкафа, под которую вариант заранее заготовлен («2 насоса»,
    /// «2 насоса + жокей и задвижка»). Выдача поиска отдаёт одну строку на прошивку — ту конфигурацию,
    /// чьи теги совпали с запросом, — поэтому карточке нужно показать, ЧТО ИМЕННО совпало: без пометки
    /// наладчик видит обычную карточку прошивки и не понимает, почему у неё «не те» теги.</summary>
    public string ConfigName { get; init; } = "";
    public DateTime? UploadDate { get; init; }
    public int Score { get; init; }
    public int FwVersionId { get; init; }

    /// <summary>Сколько РАЗНЫХ слов запроса совпало с этой версией в обычном поиске (см.
    /// Database.SearchByKeywords). Выдача по нему отделяет «совпало всё, что ввели» от «совпало
    /// только одно общее слово» и прячет вторые под «Показать ещё». 0 — поиск без слов (только
    /// фильтры) либо результат не из поиска (скан обновлений).</summary>
    public int MatchedTokens { get; init; }

    /// <summary>Сколько раз ИМЕННО эту версию выбирали по такому же запросу (см.
    /// Database.FwUsage.cs). 0 — ни разу либо запрос новый.</summary>
    public int UsageCount { get; init; }
}

public static class SearchService
{
    // Разделители слов запроса. Скобки/кавычки/двоеточие входят сюда наравне с запятой и дефисом:
    // в обозначении шкафа «НГР-КПЧ-3-1,5(3,8А)-РВР» скобки склеивали куски в мусорные токены «5(3»,
    // «8А)», которые ни с чем не совпадали. Точка НАМЕРЕННО не разделитель — «2.0» должно оставаться
    // одним словом (иначе распалось бы на «2» и «0», а односимвольные токены поиск отбрасывает).
    private static readonly Regex Separators = new(@"[,;:\-/\\()\[\]{}""«»]+", RegexOptions.Compiled);

    public static string Normalize(string q)
    {
        var collapsed = Separators.Replace(q, " ");
        return Regex.Replace(collapsed, @"\s+", " ").Trim().ToUpperInvariant();
    }

    /// <summary>EN QWERTY -> RU ЙЦУКЕН, keyed by physical key position (both layouts put these
    /// letters on the same keys on a standard Windows keyboard) — lowercase only, case is
    /// reapplied by <see cref="ConvertLayout"/>.</summary>
    private static readonly Dictionary<char, char> EnToRu = new()
    {
        ['q'] = 'й', ['w'] = 'ц', ['e'] = 'у', ['r'] = 'к', ['t'] = 'е', ['y'] = 'н', ['u'] = 'г',
        ['i'] = 'ш', ['o'] = 'щ', ['p'] = 'з', ['['] = 'х', [']'] = 'ъ',
        ['a'] = 'ф', ['s'] = 'ы', ['d'] = 'в', ['f'] = 'а', ['g'] = 'п', ['h'] = 'р', ['j'] = 'о',
        ['k'] = 'л', ['l'] = 'д', [';'] = 'ж', ['\''] = 'э',
        ['z'] = 'я', ['x'] = 'ч', ['c'] = 'с', ['v'] = 'м', ['b'] = 'и', ['n'] = 'т', ['m'] = 'ь',
        [','] = 'б', ['.'] = 'ю', ['`'] = 'ё',
    };

    private static readonly Dictionary<char, char> RuToEn =
        EnToRu.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

    /// <summary>Best-effort fix for a query typed with the wrong OS keyboard layout active — e.g.
    /// "gj;fh" typed on an EN-US layout while the operator meant to type the Russian word "пожар"
    /// on ЙЦУКЕН (same physical keys, wrong active layout). A pure per-character remap by key
    /// position, not real transliteration — good enough since it's only ever tried as a fallback
    /// after the as-typed query already found nothing (see <see cref="SearchWithLayoutFallback"/>),
    /// so it can never turn a correct hit into a wrong one.</summary>
    public static string ConvertLayout(string q)
    {
        var chars = new char[q.Length];
        for (var i = 0; i < q.Length; i++)
        {
            var c = q[i];
            var lower = char.ToLowerInvariant(c);
            if (EnToRu.TryGetValue(lower, out var ru)) chars[i] = char.IsUpper(c) ? char.ToUpperInvariant(ru) : ru;
            else if (RuToEn.TryGetValue(lower, out var en)) chars[i] = char.IsUpper(c) ? char.ToUpperInvariant(en) : en;
            else chars[i] = c;
        }
        return new string(chars);
    }

    /// <summary>Runs <paramref name="searchFn"/> with the query as typed; if that finds nothing,
    /// retries once with <see cref="ConvertLayout"/> applied. Shared by firmware/parameter/schematic
    /// search so a forgotten keyboard-layout switch is forgiven the same way in all three search
    /// modes.</summary>
    public static List<T> SearchWithLayoutFallback<T>(string query, bool exactWord, Func<string, bool, List<T>> searchFn) =>
        SearchWithLayoutFallback(query, exactWord, searchFn, allowFallback: true, out _, out _);

    /// <summary>Same fallback as above, but lets a caller (a) skip the retry entirely — used when
    /// either the "Настройки → Общие" toggle is off or the operator's own history has taught the app
    /// this exact query is never a layout mistake — and (b) find out whether the converted query is
    /// what actually produced the results, so the UI can ask "это точно оно?" only when it's not
    /// already sure from past feedback (see Database.LayoutFallback and SearchView's usage).</summary>
    public static List<T> SearchWithLayoutFallback<T>(string query, bool exactWord, Func<string, bool, List<T>> searchFn,
        bool allowFallback, out bool usedFallback, out string convertedQuery)
    {
        usedFallback = false;
        convertedQuery = query;

        var results = searchFn(query, exactWord);
        if (results.Count > 0 || !allowFallback) return results;

        convertedQuery = ConvertLayout(query);
        if (convertedQuery == query) return results;

        var converted = searchFn(convertedQuery, exactWord);
        if (converted.Count > 0) usedFallback = true;
        return converted;
    }

    /// <summary><paramref name="usageThreshold"/> — см. Database.SearchFwVersions: сколько раз
    /// версию должны выбрать по этому запросу, прежде чем статистика начнёт двигать выдачу (по
    /// умолчанию 1 — единственный выбор уже учитывается, как было до появления настраиваемого
    /// порога; реальный вызывающий код передаёт ConfigService.FwUsageThreshold()).</summary>
    public static List<HierarchyResult> Search(Database db, string query, bool exactWord = false,
        FirmwareSearchFilters? filters = null, int usageThreshold = 1, double usageMultiplier = 1, string localRoot = "") =>
        SearchWithLayoutFallback(query, exactWord, (q, ex) => SearchCore(db, q, ex, filters, usageThreshold, usageMultiplier, localRoot));

    public static List<HierarchyResult> Search(Database db, string query, bool exactWord,
        bool allowFallback, out bool usedFallback, out string convertedQuery, FirmwareSearchFilters? filters = null,
        int usageThreshold = 1, double usageMultiplier = 1, string localRoot = "") =>
        SearchWithLayoutFallback(query, exactWord, (q, ex) => SearchCore(db, q, ex, filters, usageThreshold, usageMultiplier, localRoot),
            allowFallback, out usedFallback, out convertedQuery);

    /// <summary>Ключ статистики выбора: тот же нормализованный запрос, что идёт в поиск, — чтобы
    /// «НГР ПЧ», «нгр  пч» и «НГР, ПЧ» считались одним и тем же запросом.</summary>
    public static string UsageKey(string query) => Normalize(query);

    private static List<HierarchyResult> SearchCore(Database db, string query, bool exactWord,
        FirmwareSearchFilters? filters, int usageThreshold, double usageMultiplier, string localRoot = "")
    {
        var normalized = Normalize(query);
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Пустой запрос с заданными фильтрами — осмысленный «покажи всё такое», этот случай
        // разбирает сам Database.SearchFwVersions; пустой запрос без фильтров ничего не ищет.
        if (tokens.Length == 0 && (filters is null || filters.IsEmpty)) return new();

        var rows = db.SearchFwVersions(tokens, exactWord, filters, UsageKey(query), query, usageThreshold, usageMultiplier);

        return rows.Select((row, idx) => ToHierarchyResult(row.Row, rows.Count - idx, row.UsageCount, localRoot, row.MatchedTokens)).ToList();
    }

    /// <summary>Maps a joined fw_versions row (group/subtype/controller names already populated by the
    /// caller's query) to a HierarchyResult — the same shape Search() returns, reused by the firmware-
    /// update scan so it can hand rows straight to FirmwareSync.CopyToLocal.
    ///
    /// <paramref name="localRoot"/> — this machine's root_path (ConfigService.RootPath()). When set,
    /// every disk path on the result is re-rooted onto it via <see cref="FirmwarePathLocalizer"/>, so a
    /// firmware uploaded on a machine that stored the share as "\\ant_srv\Software" opens/downloads on a
    /// machine that mounts it as "Z:\Software" and vice versa. Empty (the default) keeps the stored
    /// paths verbatim — used by callers that only need the Name (e.g. HistoryDialog.LocalName).</summary>
    public static HierarchyResult ToHierarchyResult(FwVersionRecord row, int score = 0, int usageCount = 0, string localRoot = "", int matchedTokens = 0)
    {
        var sub = !string.IsNullOrEmpty(row.SubtypeFolder) ? row.SubtypeFolder : row.SubtypeName;
        var name = $"{sub} {row.CtrlName}".Trim();

        DateTime? uploadDate = null;
        if (DateTime.TryParse(row.UploadDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            uploadDate = d;

        return new HierarchyResult
        {
            SubtypeId = row.SubtypeId,
            ControllerId = row.ControllerId,
            FwVersionId = row.Id ?? 0,
            Name = name,
            SubtypeName = row.SubtypeName,
            Controller = row.CtrlName,
            EquipmentType = row.GroupName,
            WorkType = string.Join(", ", row.LaunchTypes),
            IoMapPath = FirmwarePathLocalizer.Localize(row.IoMapPath, localRoot),
            InstructionsPath = FirmwarePathLocalizer.Localize(row.InstructionsPath, localRoot),
            ModbusMapPath = FirmwarePathLocalizer.Localize(row.ModbusMapPath, localRoot),
            HmiPath = FirmwarePathLocalizer.Localize(row.HmiPath, localRoot),
            ExecutableHint = row.ExecutableHint,
            HmiExecutableHint = row.HmiExecutableHint,
            FirmwareDir = FirmwarePathLocalizer.Localize(row.DiskPath, localRoot),
            VersionRaw = row.VersionRaw,
            Description = row.Description,
            Tags = row.Tags,
            ConfigName = row.ConfigName,
            UploadDate = uploadDate,
            Score = score,
            UsageCount = usageCount,
            MatchedTokens = matchedTokens,
        };
    }
}
