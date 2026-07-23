using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Всё, что связано с «каким файлом внутри загруженной папки открывается прошивка/HMI-проект»
/// (см. FwVersionRecord.ExecutableHint/HmiExecutableHint).
///
/// Исторически подсказка была просто именем файла в КОРНЕ папки — но реальные проекты кладут
/// исполняемый файл во вложенную папку (драйверы/ресурсы рядом), и тогда указать его было нечем:
/// UploadView показывал плоский список файлов верхнего уровня, а «Открыть прошивку ПЛК» просто брал
/// первый попавшийся не-документ. Теперь подсказка — это ОТНОСИТЕЛЬНЫЙ путь от папки версии
/// («Driver\App.exe»), и старый формат (просто имя файла) остаётся его частным случаем, т.е. уже
/// сохранённые подсказки продолжают работать без миграции.</summary>
public static class ExecutableHintResolver
{
    /// <summary>Полный путь к файлу, на который указывает подсказка, или null — если подсказки нет,
    /// файла нет, или подсказка пытается выйти за пределы папки (абсолютный путь/«..» — на такие
    /// значения нельзя полагаться, они могли прийти с другой машины через синхронизацию конфига).</summary>
    public static string? Resolve(string? folder, string? hint)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(hint)) return null;
        var normalized = Normalize(hint);
        if (normalized is null) return null;

        var full = Path.GetFullPath(Path.Combine(folder, normalized));
        var root = Path.GetFullPath(folder);
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(full) ? full : null;
    }

    /// <summary>Относительный путь подсказки, приведённый к виду «подпапка\файл» — или null, если
    /// значение нельзя использовать (пустое, абсолютное, с выходом наверх через «..»).</summary>
    public static string? Normalize(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return null;
        var value = hint.Trim().Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);
        if (value.Length == 0) return null;
        if (Path.IsPathRooted(value)) return null;
        var parts = value.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p == "..")) return null;
        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    /// <summary>Все файлы папки рекурсивно, относительными путями, отсортированные так же, как их
    /// показывает диалог выбора (сначала корень, потом вложенные). Пустой список, если папку не
    /// удалось прочитать — вызывающий код всегда должен уметь работать без подсказки.</summary>
    public static List<string> ListRelativeFiles(string folder, int maxFiles = 5000)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return new List<string>();
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Take(maxFiles)
                .Select(f => Path.GetRelativePath(folder, f))
                .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar))
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    /// <summary>Единственный файл с «родным» расширением во всём дереве папки — тогда спрашивать
    /// оператора не о чем. Если таких файлов ноль или больше одного, возвращает null: ноль — значит
    /// подсказку надо выбрать руками, больше одного — значит выбор неоднозначен (раньше проверялся
    /// только верхний уровень, из-за чего проект с .psl во вложенной папке всегда требовал ручного
    /// выбора, хотя выбирать было не из чего).</summary>
    public static string? AutoDetect(string folder, IReadOnlyCollection<string> knownExtensions)
    {
        var matches = ListRelativeFiles(folder)
            .Where(f => knownExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}
