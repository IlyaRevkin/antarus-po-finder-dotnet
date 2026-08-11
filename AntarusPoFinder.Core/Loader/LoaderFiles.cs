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

    /// <summary>Файл с учётом выбора оператора в модерации (FwVersionRecord.ExecutableHint).
    ///
    /// Когда в ОДНОЙ папке версии лежит несколько прошивок (например, пачка .lfs пожарных шкафов),
    /// «первый найденный» — не обязательно тот, что нужен. Оператор указывает нужный файл в модерации,
    /// и этот выбор обязаны уважать все команды над файлом версии: и «Открыть прошивку ПЛК», и
    /// «Открыть LFS/PSL», и заливка в контроллер (см. FindDeploymentFiles) — иначе в контроллер уезжает
    /// чужая прошивка, а это уже не «неудобно», а испорченный шкаф.
    ///
    /// Подсказка используется, только если указывает на файл с одним из <paramref name="extensions"/>
    /// (для «Открыть LFS» — только .lfs): подсказка на .psl не должна подставляться там, где ждут
    /// именно .lfs. Если подсказки нет или она другого расширения — прежний поиск «первый по списку
    /// расширений».</summary>
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

    /// <summary>Оба допустимых источника интерактивной загрузки. Каждый тип выбирается по одинаковому
    /// порядку папок: точная локальная копия раньше сетевой папки той же версии.
    ///
    /// <paramref name="executableHint"/> — выбор оператора в модерации, и он ГЛАВНЕЕ порядка папок:
    /// если указан конкретный .lfs (или .psl), заливается именно он. Без этого в папке с пачкой
    /// прошивок пожарных шкафов в контроллер уезжала первая попавшаяся — ровно та жалоба, что чинили
    /// в 1.54.1. Подсказка применяется по каждому расширению отдельно, поэтому указанный .psl не
    /// подменяет собой .lfs и наоборот (см. <see cref="ResolvePreferHint"/>).</summary>
    public static LoaderProjectFiles FindDeploymentFiles(IEnumerable<string> dirs, string? executableHint = null)
    {
        var candidates = dirs as IReadOnlyList<string> ?? dirs.ToList();
        return new LoaderProjectFiles(
            ResolvePreferHint(candidates, executableHint, LfsExtension),
            ResolvePreferHint(candidates, executableHint, PslExtension));
    }

    /// <summary>Имя собранного файла для этого исходника: <c>проект.psl</c> → <c>проект.lfs</c>.
    /// Одно место на всех — и на запрос к Automation (outputPath), и на публикацию результата.</summary>
    public static string LfsNameFor(string pslPath) =>
        Path.GetFileNameWithoutExtension(pslPath) + LfsExtension;

    private static bool HasExt(string path, string extension) =>
        string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateSafe(string dir, SearchOption option)
    {
        try { return Directory.EnumerateFiles(dir, "*", option); }
        catch (Exception) { return Array.Empty<string>(); }
    }
}
