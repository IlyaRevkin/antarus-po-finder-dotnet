using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.Core.Loader;

public sealed record LoaderProjectFiles(string? LfsPath, string? PslPath)
{
    public bool HasLfs => LfsPath is not null;
    public bool HasPsl => PslPath is not null;
    public bool HasAny => HasLfs || HasPsl;
}

/// <summary>Поиск файлов Segnetics внутри папки версии: .psl — исходный проект SMLogix, .lfs —
/// скомпилированный файл, который и заливается в контроллер. Используется и кнопками «Открыть файл
/// PSL/LFS» в карточке поиска, и самим лоадером (что собирать / что заливать).</summary>
public static class LoaderFiles
{
    public const string PslExtension = ".psl";
    public const string LfsExtension = ".lfs";

    public static string? FindPsl(string dir) => Find(dir, PslExtension);
    public static string? FindLfs(string dir) => Find(dir, LfsExtension);

    /// <summary>Сначала верхний уровень папки, потом вложенные — файл, лежащий прямо в папке версии,
    /// почти всегда и есть нужный, а во вложенных папках чаще попадаются копии/бэкапы.
    /// Не бросает: недоступная папка (сеть отвалилась, нет прав) — это «не нашли», а не ошибка,
    /// иначе один битый путь ронял бы отрисовку всей выдачи поиска.</summary>
    public static string? Find(string dir, string extension)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        try
        {
            return EnumerateSafe(dir, SearchOption.TopDirectoryOnly).FirstOrDefault(f => HasExt(f, extension))
                ?? EnumerateSafe(dir, SearchOption.AllDirectories).FirstOrDefault(f => HasExt(f, extension));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Первый найденный файл нужного расширения по списку папок-кандидатов (локальный кэш
    /// сначала, сетевая папка последней — см. SearchView.CandidateFolders).</summary>
    public static string? FindIn(IEnumerable<string> dirs, string extension)
    {
        foreach (var dir in dirs)
            if (Find(dir, extension) is { } hit) return hit;
        return null;
    }

    /// <summary>Находит файл для команды открытия с учётом выбора оператора в модерации.
    /// Подсказка используется только для подходящего расширения; при отсутствии подходящей подсказки
    /// возвращается первый файл по обычному порядку поиска. Выбор источника для загрузки в ПЛК этим
    /// методом не определяется.</summary>
    public static string? ResolvePreferHint(IEnumerable<string> dirs, string? executableHint, params string[] extensions)
    {
        var dirList = dirs as IReadOnlyList<string> ?? dirs.ToList();
        if (ExecutableHintResolver.Normalize(executableHint) is not null)
            foreach (var dir in dirList)
                if (ExecutableHintResolver.Resolve(dir, executableHint) is { } hinted
                    && extensions.Any(e => HasExt(hinted, e)))
                    return hinted;
        foreach (var ext in extensions)
            if (FindIn(dirList, ext) is { } hit) return hit;
        return null;
    }

    /// <summary>Находит оба допустимых источника интерактивной загрузки. Каждый тип выбирается по
    /// одинаковому порядку папок: точная локальная копия раньше сетевой папки той же версии.</summary>
    public static LoaderProjectFiles FindDeploymentFiles(IEnumerable<string> dirs)
    {
        var candidates = dirs.ToArray();
        return new LoaderProjectFiles(
            FindIn(candidates, LfsExtension),
            FindIn(candidates, PslExtension));
    }

    private static bool HasExt(string path, string extension) =>
        string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateSafe(string dir, SearchOption option)
    {
        try { return Directory.EnumerateFiles(dir, "*", option); }
        catch (Exception) { return Array.Empty<string>(); }
    }
}
