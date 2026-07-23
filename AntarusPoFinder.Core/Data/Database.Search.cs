using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Data;

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

    /// <summary>Return the highest-scoring fw_version per (subtype_id, controller_id) whose group/
    /// subtype/controller/tag fields contain the query tokens (each query token is matched AGAINST
    /// the field — not the other way around — so a short query like "pixel" finds "pixel2").</summary>
    public List<FwVersionRecord> SearchFwVersionsByTokens(IReadOnlyList<string> tokens, bool exactWord = false)
    {
        var rows = new List<FwVersionRecord>();
        using (var reader = ExecuteReader($"""
            SELECT fv.*,
                   eg.name AS group_name,
                   es.name AS subtype_name,
                   es.folder_name AS subtype_folder,
                   cm.name AS ctrl_name
            FROM fw_versions fv
            JOIN equipment_subtypes es ON fv.subtype_id  = es.id
            JOIN equipment_groups   eg ON es.group_id    = eg.id
            JOIN controller_models  cm ON fv.controller_id = cm.id
            WHERE fv.archived = 0 AND (fv.status IS NULL OR fv.status = 'active') AND {NotDeleted("fv")}
            ORDER BY fv.id DESC
            """))
        {
            while (reader.Read())
            {
                var rec = ReadFwVersion(reader);
                rec.GroupName = GetString(reader, "group_name");
                rec.SubtypeName = GetString(reader, "subtype_name");
                rec.SubtypeFolder = GetString(reader, "subtype_folder");
                rec.CtrlName = GetString(reader, "ctrl_name");
                rows.Add(rec);
            }
        }

        if (rows.Count == 0) return rows;

        var qTokens = tokens.Where(t => !string.IsNullOrEmpty(t) && t.Length >= 2)
            .Select(t => t.ToUpperInvariant()).ToArray();
        if (qTokens.Length == 0) return new();

        int Score(FwVersionRecord row)
        {
            var fields = new[] { row.GroupName, row.SubtypeName, row.SubtypeFolder, row.CtrlName };
            var tagWords = (row.Tags ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Тип пуска (УПП/ПП/ПЧ/КПЧ) оператор отмечает при загрузке версии и потом ищет ровно по
            // нему — «НГР ПЧ». До этого launch_types не участвовал в подсчёте вообще: отмеченный при
            // загрузке тип пуска нигде в поиске не учитывался, находилось только то, что случайно
            // совпало с именем подтипа/тегом. Вес как у тега, а не как у имени: это явно
            // проставленный признак версии, а не побочное совпадение в названии папки.
            var launchTypes = row.LaunchTypes ?? new List<string>();

            int score = 0;
            foreach (var token in qTokens)
            {
                if (fields.Any(f => TokenMatches(token, f, exactWord))) score += 1;
                if (tagWords.Any(t => TokenMatches(token, t, exactWord))) score += 2;
                // Сравнение целым значением, а не подстрокой, и НЕЗАВИСИМО от «точного совпадения
                // слова»: список типов пуска закрытый (ConfigService.LaunchTypes), и почти каждый
                // короткий в нём — подстрока длинного («ПЧ» в «КПЧ», «ПП» в «УПП»). Подстрочно
                // «НГР ПЧ» поднимало ещё и шкафы с КПЧ — тип пуска не то поле, где полезно угадывать.
                if (launchTypes.Any(lt => string.Equals(lt, token, StringComparison.OrdinalIgnoreCase)))
                    score += 2;
            }
            return score;
        }

        var seen = new Dictionary<(int, int), (int Score, FwVersionRecord Row)>();
        foreach (var row in rows)
        {
            var sc = Score(row);
            if (sc == 0) continue;
            var key = (row.SubtypeId, row.ControllerId);
            if (!seen.TryGetValue(key, out var existing) || sc > existing.Score)
                seen[key] = (sc, row);
        }

        return seen.Values.OrderByDescending(v => v.Score).Select(v => v.Row).ToList();
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
            var tagWords = (f.Tags ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);

            int score = qTokens.Count(token => fields.Any(field => TokenMatches(token, field, exactWord)));
            score += qTokens.Count(token => tagWords.Any(t => TokenMatches(token, t, exactWord))) * 2;
            return score;
        }

        return files.Select(f => (File: f, Score: Score(f)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.File)
            .ToList();
    }
}
