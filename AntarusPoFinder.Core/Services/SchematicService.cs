using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AntarusPoFinder.Core.Services;

public record SchematicHit(string CabinetName, string Path);

/// <summary>Scans the "second disk" (<see cref="ConfigService.SecondDiskPath"/>) for cabinet
/// (шкаф) electrical schematics. Walks the ENTIRE folder tree under the configured path (any
/// depth — cabinets are commonly grouped under territory/area subfolders, so a top-level-only
/// scan misses them), and matches every schematic file it finds against the query — either as a
/// substring (default) or, when <c>exactWord</c> is set, as a whole word (same "точное совпадение
/// слова" semantics as the firmware/parameter search, so e.g. «ПЧ» doesn't also match «КПЧ»).
/// Expected structure on the second disk (either works, at any nesting depth):
///   .../ПЖ-101/схема.pdf   — folder named after the cabinet; every schematic file inside counts
///   .../НГР-205.pdf        — file named directly after the cabinet
/// Every matching file is returned, not just the first one per folder.</summary>
public class SchematicService
{
    private static readonly string[] SchematicExtensions =
        { ".pdf", ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".dwg", ".dxf" };

    private static readonly Regex WordSplitter = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    private string? _cachedDiskPath;
    private List<ScannedFile> _cache = new();

    private record ScannedFile(string CabinetName, string UpperMatchText, string Path);

    public void InvalidateCache()
    {
        _cachedDiskPath = null;
        _cache = new();
    }

    /// <summary>All schematic files found on disk, sorted by cabinet name. Cached per disk path
    /// until InvalidateCache().</summary>
    public List<SchematicHit> CabinetHits(string diskPath) =>
        Scanned(diskPath).Select(f => new SchematicHit(f.CabinetName, f.Path)).ToList();

    /// <summary>Schematic files whose cabinet name (folder and/or file name) matches every word of
    /// the query — partial substring by default, whole-word only when <paramref name="exactWord"/>
    /// is set.</summary>
    public List<SchematicHit> Matches(string diskPath, string query, bool exactWord = false) =>
        SearchService.SearchWithLayoutFallback(query, exactWord, (q, ex) => MatchesCore(diskPath, q, ex));

    public List<SchematicHit> Matches(string diskPath, string query, bool exactWord,
        bool allowFallback, out bool usedFallback, out string convertedQuery) =>
        SearchService.SearchWithLayoutFallback(query, exactWord, (q, ex) => MatchesCore(diskPath, q, ex),
            allowFallback, out usedFallback, out convertedQuery);

    private List<SchematicHit> MatchesCore(string diskPath, string query, bool exactWord)
    {
        var tokens = SearchService.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return new();

        return Scanned(diskPath)
            .Where(f => tokens.All(t => TokenMatches(t, f.UpperMatchText, exactWord)))
            .Select(f => new SchematicHit(f.CabinetName, f.Path))
            .OrderBy(h => h.CabinetName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<ScannedFile> Scanned(string diskPath)
    {
        if (string.IsNullOrEmpty(diskPath) || !Directory.Exists(diskPath)) return new();
        if (_cachedDiskPath == diskPath) return _cache;

        _cache = Scan(diskPath);
        _cachedDiskPath = diskPath;
        return _cache;
    }

    /// <summary>Same whole-word-vs-substring matching as the firmware/parameter search.</summary>
    private static bool TokenMatches(string token, string upperField, bool exactWord)
    {
        if (!exactWord) return upperField.Contains(token, StringComparison.Ordinal);
        return WordSplitter.Split(upperField).Any(w => w == token);
    }

    private static List<ScannedFile> Scan(string diskPath)
    {
        var hits = new List<ScannedFile>();
        var rootFull = System.IO.Path.GetFullPath(diskPath)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        try
        {
            foreach (var file in Directory.EnumerateFiles(diskPath, "*", SearchOption.AllDirectories))
            {
                var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if (!SchematicExtensions.Contains(ext)) continue;

                var fileNameNoExt = System.IO.Path.GetFileNameWithoutExtension(file).Trim();
                var parentDir = System.IO.Path.GetDirectoryName(file);
                var parentName = string.IsNullOrEmpty(parentDir) ? null : System.IO.Path.GetFileName(parentDir).Trim();

                // A file sitting directly under the configured root has no meaningful "cabinet
                // folder" — only a nested parent folder (any depth) counts as folder-grouping.
                var groupedByFolder = !string.IsNullOrEmpty(parentName) &&
                    !string.Equals(
                        parentDir!.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
                        rootFull, StringComparison.OrdinalIgnoreCase);

                var cabinetName = groupedByFolder ? parentName! : fileNameNoExt;
                var matchText = groupedByFolder ? $"{parentName} {fileNameNoExt}" : fileNameNoExt;

                hits.Add(new ScannedFile(cabinetName, matchText.ToUpperInvariant(), file));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Second disk unreachable — treat as empty, same as before.
        }
        return hits.OrderBy(h => h.CabinetName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
