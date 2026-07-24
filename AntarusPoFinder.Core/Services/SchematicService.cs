using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

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
/// Every matching file is returned, not just the first one per folder.
///
/// This service itself stays synchronous and knows nothing about threads — the caller decides where
/// to run it. In practice that's SearchView, which pushes the cold-cache walk (the expensive part,
/// see EnsureScanned) into a background Task.Run so the network-share enumeration never blocks the
/// UI thread. Not thread-safe by itself (the cache fields below aren't locked) — fine today because
/// SearchView is this service's only caller and dedupes concurrent scans of the same disk path itself
/// before ever reaching here; don't call this from more than one place at once without adding
/// locking.</summary>
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

    /// <summary>Кэш для этого диска уже наполнен — значит, поиск по нему отработает мгновенно,
    /// обходить диск заново не нужно. Вызывающему это нужно, чтобы решить, показывать ли индикатор
    /// занятости и кнопку «Остановить»: ради фильтрации готового списка в памяти они только мешают.</summary>
    public bool IsScanned(string diskPath) =>
        !string.IsNullOrEmpty(diskPath) && _cachedDiskPath == diskPath;

    /// <summary>Прогревает кэш для указанного диска без фильтрации по запросу — тяжёлая часть
    /// (полный обход дерева папок, Directory.EnumerateFiles по SearchOption.AllDirectories) идёт
    /// именно здесь; последующие Matches() для того же diskPath обращаются к уже наполненному кэшу
    /// и работают быстро, как обычная фильтрация списка в памяти. Отдельный метод существует специально
    /// для того, чтобы вызывающий (SearchView.PerformSchemasSearchAsync) мог обернуть в фоновый поток
    /// именно обход — и не плодить параллельные обходы одного и того же диска, если несколько поисков
    /// подряд целятся в один diskPath, — а сам подбор совпадений по конкретному запросу (Matches, с его
    /// out-параметрами раскладки-фолбэка) оставить синхронным на потоке интерфейса.
    ///
    /// <paramref name="onFound"/> вызывается на КАЖДЫЙ найденный файл схемы прямо по ходу обхода, на
    /// том же потоке, где выполняется этот метод (у SearchView — фоновый Task.Run): благодаря этому
    /// выдачу видно, не дожидаясь конца обхода сетевой шары на сотни гигабайт. Тёплый кэш обработчик
    /// не зовёт — обходить нечего, вызывающий берёт готовый список через Matches().
    ///
    /// <paramref name="ct"/> прерывает обход (кнопка «Остановить»). Прерванный обход кэш НЕ пишет:
    /// иначе следующий поиск принял бы обрезанный список за полный и «терял» бы половину диска до
    /// перезапуска программы. То, что успели показать оператору, вызывающий, конечно, оставляет на
    /// экране — но это его выдача, а не наш кэш.</summary>
    public void EnsureScanned(string diskPath, CancellationToken ct = default, Action<SchematicHit>? onFound = null)
    {
        if (string.IsNullOrEmpty(diskPath) || !Directory.Exists(diskPath)) return;
        if (_cachedDiskPath == diskPath) return;

        var scanned = Scan(diskPath, ct, onFound);
        ct.ThrowIfCancellationRequested();
        _cache = scanned;
        _cachedDiskPath = diskPath;
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
        var tokens = QueryTokens(query);
        if (tokens.Length == 0) return new();

        return Scanned(diskPath)
            .Where(f => tokens.All(t => TokenMatches(t, f.UpperMatchText, exactWord)))
            .Select(f => new SchematicHit(f.CabinetName, f.Path))
            .OrderBy(h => h.CabinetName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Слова запроса в том же нормализованном виде, в каком с ними сверяется MatchesCore —
    /// нужны отдельно тому, кто фильтрует выдачу по одному файлу за раз (потоковый обход, см.
    /// EnsureScanned/HitMatches), а не списком целиком.</summary>
    public static string[] QueryTokens(string query) =>
        SearchService.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Подходит ли уже найденный файл под запрос — ровно то же правило, что и в MatchesCore,
    /// но по одному файлу: так потоковый обход решает, показывать ли файл, не дожидаясь конца.
    /// Текст для сверки восстанавливается из самого результата (папка-шкаф + имя файла) — для файла
    /// прямо в корне это даёт имя дважды («НГР-205 НГР-205» вместо «НГР-205»), что на результат не
    /// влияет ни при поиске подстроки, ни при поиске целого слова.</summary>
    public static bool HitMatches(SchematicHit hit, IReadOnlyList<string> tokens, bool exactWord)
    {
        if (tokens.Count == 0) return false;
        var text = $"{hit.CabinetName} {System.IO.Path.GetFileNameWithoutExtension(hit.Path)}".ToUpperInvariant();
        foreach (var token in tokens)
            if (!TokenMatches(token, text, exactWord)) return false;
        return true;
    }

    private List<ScannedFile> Scanned(string diskPath)
    {
        if (string.IsNullOrEmpty(diskPath) || !Directory.Exists(diskPath)) return new();
        if (_cachedDiskPath == diskPath) return _cache;

        _cache = Scan(diskPath, CancellationToken.None, null);
        _cachedDiskPath = diskPath;
        return _cache;
    }

    /// <summary>Same whole-word-vs-substring matching as the firmware/parameter search.</summary>
    private static bool TokenMatches(string token, string upperField, bool exactWord)
    {
        if (!exactWord) return upperField.Contains(token, StringComparison.Ordinal);
        return WordSplitter.Split(upperField).Any(w => w == token);
    }

    private static List<ScannedFile> Scan(string diskPath, CancellationToken ct, Action<SchematicHit>? onFound)
    {
        var hits = new List<ScannedFile>();
        var rootFull = System.IO.Path.GetFullPath(diskPath)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        try
        {
            foreach (var file in Directory.EnumerateFiles(diskPath, "*", SearchOption.AllDirectories))
            {
                // Проверка на каждом файле, а не раз в N: сам EnumerateFiles по сетевой шаре может
                // «задуматься» на папке, но между файлами прерывание отрабатывает сразу.
                ct.ThrowIfCancellationRequested();

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
                onFound?.Invoke(new SchematicHit(cabinetName, file));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Second disk unreachable — treat as empty, same as before.
        }
        return hits.OrderBy(h => h.CabinetName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
