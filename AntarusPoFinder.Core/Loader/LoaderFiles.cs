using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AntarusPoFinder.Core.Loader;

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

    private static bool HasExt(string path, string extension) =>
        string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateSafe(string dir, SearchOption option)
    {
        try { return Directory.EnumerateFiles(dir, "*", option); }
        catch (Exception) { return Array.Empty<string>(); }
    }
}
