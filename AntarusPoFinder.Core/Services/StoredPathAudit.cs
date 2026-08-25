using System.Collections.Generic;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Одна группа записей базы с одинаковым путём: путь и сколько записей на него ссылается.
/// Считать группами, а не строками, дешевле и честнее — на диске у тысячи прошивок пара сотен
/// уникальных папок, а человеку в отчёте нужны именно папки.</summary>
/// <param name="Path">Значение disk_path как оно лежит в базе.</param>
/// <param name="Records">Сколько записей ссылается на этот путь.</param>
public sealed record StoredPathGroup(string Path, int Records);

/// <summary>Сколько записей ссылается на конкретный чужой корень («Z:», «\\ant_srv\Software»).</summary>
public sealed record ForeignRootUse(string Root, int Records);

/// <param name="Records">Всего записей с непустым путём.</param>
/// <param name="Foreign">Из них записанных от ЧУЖОГО корня — не того, каким рабочий диск подключён
/// на этой машине.</param>
/// <param name="Rescued">Из чужих — те, которые программа сама приводит к своему корню (в пути есть
/// якорная папка «ПО»/«Параметры»). Открываются нормально, вмешательства не требуют.</param>
/// <param name="Broken">Из чужих — те, которые привести не получается: якоря в пути нет, и он уйдёт
/// в чужую букву как есть. Вот это и есть поломка.</param>
/// <param name="ForeignRoots">На какие именно чужие корни ссылаются — по убыванию количества.</param>
/// <param name="BrokenSample">Пример непривязываемого пути, чтобы в тикете было что показать.</param>
public sealed record StoredPathAuditResult(
    int Records,
    int Foreign,
    int Rescued,
    int Broken,
    IReadOnlyList<ForeignRootUse> ForeignRoots,
    string BrokenSample)
{
    public static readonly StoredPathAuditResult Empty =
        new(0, 0, 0, 0, System.Array.Empty<ForeignRootUse>(), "");
}

/// <summary>Отвечает на вопрос «сколько путей в базе указывают на диск, которого на этой машине
/// нет» — тот самый класс поломки, который разбирался вручную в коммите «Параметры открываются по
/// своему диску, а не по чужой букве».
///
/// Напоминание, откуда берётся проблема: <c>fw_versions.disk_path</c> и <c>param_files.disk_path</c>
/// лежат в базе АБСОЛЮТНЫМИ — с буквой диска той машины, которая файл заливала, — и общий конфиг
/// разносит эту букву по всем. Спасает <see cref="FirmwarePathLocalizer"/>: он опознаёт в пути
/// якорную папку «ПО»/«Параметры» и переставляет хвост на корень ЭТОЙ машины. Значит, чужая буква
/// в базе сама по себе — НЕ поломка, а норма, и красить её в красный нельзя: так приучают смотреть
/// мимо настоящих отказов. Поломка — только путь, у которого якоря нет: его привести не к чему, и
/// он уйдёт в чужую букву дословно.
///
/// Поэтому итог разделён на <see cref="StoredPathAuditResult.Rescued"/> и
/// <see cref="StoredPathAuditResult.Broken"/>, а не сведён к одному числу.</summary>
public static class StoredPathAudit
{
    /// <summary>Корень пути в том виде, в каком его сравнивают между машинами: «Z:» для буквы,
    /// «\\сервер\шара» для UNC, пустая строка для относительного/непонятного. Регистр не важен.</summary>
    public static string RootOf(string? path)
    {
        var value = (path ?? "").Trim();
        if (value.Length == 0) return "";

        if (value.StartsWith(@"\\", System.StringComparison.Ordinal))
        {
            // \\сервер\шара\что-то → \\сервер\шара. Одного сервера мало: на \\srv\Soft и \\srv\Docs
            // разные права и разное содержимое, и путать их в отчёте нельзя.
            var parts = value[2..].Split(new[] { '\\', '/' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "";
            return parts.Length == 1 ? @"\\" + parts[0] : @"\\" + parts[0] + @"\" + parts[1];
        }

        if (value.Length >= 2 && value[1] == ':') return value[..2].ToUpperInvariant();
        return "";
    }

    /// <summary>Считает по группам путей из базы. <paramref name="localRoot"/> — корень рабочего
    /// диска ЭТОЙ машины (<c>root_path</c>); пустой означает «корень не настроен», и тогда сравнивать
    /// не с чем — возвращается только общее число записей.</summary>
    public static StoredPathAuditResult Audit(IEnumerable<StoredPathGroup> groups, string? localRoot)
    {
        var localRootKey = RootOf(localRoot);
        var records = 0;
        var foreign = 0;
        var rescued = 0;
        var broken = 0;
        var brokenSample = "";
        var byRoot = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var path = (group.Path ?? "").Trim();
            if (path.Length == 0) continue;
            var count = group.Records > 0 ? group.Records : 1;
            records += count;

            if (localRootKey.Length == 0) continue; // корень не настроен — сравнивать не с чем

            var pathRoot = RootOf(path);
            if (pathRoot.Length == 0) continue; // относительный путь — не наш случай, оставляем как есть
            if (string.Equals(pathRoot, localRootKey, System.StringComparison.OrdinalIgnoreCase)) continue;

            foreign += count;
            byRoot[pathRoot] = byRoot.TryGetValue(pathRoot, out var had) ? had + count : count;

            // Приводится ли путь к нашему корню — ровно тем же способом, каким это делают страницы
            // поиска, карточка и «Параметры». Localize возвращает путь БЕЗ ИЗМЕНЕНИЙ, когда якоря
            // «ПО»/«Параметры» в нём нет, — это и есть признак «привести не получилось».
            var localized = FirmwarePathLocalizer.Localize(path, localRoot!);
            if (!string.Equals(localized, path, System.StringComparison.Ordinal))
            {
                rescued += count;
            }
            else
            {
                broken += count;
                if (brokenSample.Length == 0) brokenSample = path;
            }
        }

        var roots = byRoot
            .Select(kv => new ForeignRootUse(kv.Key, kv.Value))
            .OrderByDescending(r => r.Records)
            .ThenBy(r => r.Root, System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new StoredPathAuditResult(records, foreign, rescued, broken, roots, brokenSample);
    }

    /// <summary>«Z: — 1240, \\old_srv\Soft — 12» для отчёта и тикета.</summary>
    public static string DescribeRoots(IReadOnlyList<ForeignRootUse> roots) =>
        roots.Count == 0 ? "" : string.Join(", ", roots.Select(r => $"{r.Root} — {r.Records}"));
}
