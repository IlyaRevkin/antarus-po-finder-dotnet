using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    public string WorkType { get; init; } = "";
    public string IoMapPath { get; init; } = "";
    public string InstructionsPath { get; init; } = "";
    public string HmiPath { get; init; } = "";
    public string ExecutableHint { get; init; } = "";
    public string HmiExecutableHint { get; init; } = "";

    /// <summary>Absolute path to the version folder on the network disk.</summary>
    public string FirmwareDir { get; init; } = "";

    public string VersionRaw { get; init; } = "";
    public string Description { get; init; } = "";
    public string Tags { get; init; } = "";
    public DateTime? UploadDate { get; init; }
    public int Score { get; init; }
    public int FwVersionId { get; init; }
}

public static class SearchService
{
    private static readonly Regex Separators = new(@"[,;\-/\\]+", RegexOptions.Compiled);

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
    public static List<T> SearchWithLayoutFallback<T>(string query, bool exactWord, Func<string, bool, List<T>> searchFn)
    {
        var results = searchFn(query, exactWord);
        if (results.Count > 0) return results;

        var converted = ConvertLayout(query);
        return converted != query ? searchFn(converted, exactWord) : results;
    }

    public static List<HierarchyResult> Search(Database db, string query, bool exactWord = false) =>
        SearchWithLayoutFallback(query, exactWord, (q, ex) => SearchCore(db, q, ex));

    private static List<HierarchyResult> SearchCore(Database db, string query, bool exactWord)
    {
        var normalized = Normalize(query);
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return new();

        var rows = db.SearchFwVersionsByTokens(tokens, exactWord);

        return rows.Select((row, idx) => ToHierarchyResult(row, rows.Count - idx)).ToList();
    }

    /// <summary>Maps a joined fw_versions row (group/subtype/controller names already populated by the
    /// caller's query) to a HierarchyResult — the same shape Search() returns, reused by the firmware-
    /// update scan so it can hand rows straight to FirmwareSync.CopyToLocal.</summary>
    public static HierarchyResult ToHierarchyResult(FwVersionRecord row, int score = 0)
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
            Controller = row.CtrlName,
            EquipmentType = row.GroupName,
            WorkType = string.Join(", ", row.LaunchTypes),
            IoMapPath = row.IoMapPath,
            InstructionsPath = row.InstructionsPath,
            HmiPath = row.HmiPath,
            ExecutableHint = row.ExecutableHint,
            HmiExecutableHint = row.HmiExecutableHint,
            FirmwareDir = row.DiskPath,
            VersionRaw = row.VersionRaw,
            Description = row.Description,
            Tags = row.Tags,
            UploadDate = uploadDate,
            Score = score,
        };
    }
}
