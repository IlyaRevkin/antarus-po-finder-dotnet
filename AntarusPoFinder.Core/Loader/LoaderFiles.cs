using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Services;

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

    /// <summary>Файл прошивки с учётом выбора оператора в модерации (FwVersionRecord.ExecutableHint).
    ///
    /// Когда в ОДНОЙ папке версии лежит несколько прошивок (например, пачка .lfs пожарных шкафов),
    /// «первый найденный» — не обязательно тот, что нужен. Оператор указывает нужный файл в модерации,
    /// и «Открыть прошивку ПЛК» его уже уважает (см. PlcOpenResolver) — а заливка в контроллер и
    /// кнопки «Открыть LFS/PSL» брали первый попавшийся, отсюда жалоба «залил не ту прошивку».
    ///
    /// Подсказка используется, только если указывает на файл с одним из <paramref name="extensions"/>
    /// (для заливки — .lfs, затем .psl; для «Открыть LFS» — только .lfs): подсказка на .psl не должна
    /// подставляться там, где ждут именно .lfs. Если подсказки нет или она указывает на файл другого
    /// расширения — прежний поиск «первый по списку расширений».</summary>
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

    private static bool HasExt(string path, string extension) =>
        string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateSafe(string dir, SearchOption option)
    {
        try { return Directory.EnumerateFiles(dir, "*", option); }
        catch (Exception) { return Array.Empty<string>(); }
    }
}
