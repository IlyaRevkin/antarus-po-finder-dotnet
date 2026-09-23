using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Дерево проекта среды разработки — то, что нельзя переименовывать по кускам.
///
/// <b>Общее правило, ради которого класс и заведён.</b> Проект почти любой среды это не файл, а
/// связка: <b>папка + одноимённый файл проекта + подпапки ресурсов</b>. Kinco (<c>.dpj</c> плюс
/// <c>.pkgx</c>/<c>.bak</c> и подпапки <c>image</c>, <c>sound</c>, <c>vg</c>, <c>tar</c>, <c>HMI0</c>),
/// Owen, Codesys, Delta, Weintek, FStudio — устроены одинаково, и среда связывает половинки ПО ИМЕНИ.
/// Переименуешь файл, а папку оставишь (или наоборот) — проект открывается, но своих расширений,
/// драйверов и картинок не находит. Ровно на это и жаловались: «ты переименовываешь файл, а папка
/// старой остаётся, и он из-за расхождения названий не может найти расширения».
///
/// <b>Признак — структурный, а не списком расширений.</b> Список вендоров кончается на следующем
/// заказчике, а строение — нет: «в папке лежит файл, названный так же, как папка» плюс «рядом с ним
/// ресурсы» узнаёт любого вендора, включая тех, о ком мы не слышали. Расширения тут не при чём, и
/// хардкодить их нельзя.
///
/// <b>Выбранный вариант: НЕ ТРОГАЕМ НИЧЕГО.</b> Развилка была из двух — переименовывать папку и файл
/// согласованно либо не переименовывать вовсе. Взят второй, и вот почему: согласованность пары
/// НЕОБХОДИМА, но НЕ ДОСТАТОЧНА. Своё имя проект пишет ещё и ВНУТРИ себя — в заголовке файла проекта,
/// в его резервных копиях (<c>.bak</c>, <c>.pkgx</c>), в описях ресурсов подпапок; переписать это мы
/// не можем и проверить тоже. Переименовав пару, мы получили бы внешне стройную папку и ту же самую
/// беду внутри — только искать её пришлось бы дольше. А каноническое имя нужно исключительно НАМ:
/// на диске оно удобство, а какой файл открывать, программа знает из <c>fw_versions.filename</c> и
/// <c>executable_hint</c>. Потерять удобство дешевле, чем отдать наладчику мёртвый проект.
///
/// Все, кто переименовывает файлы прошивки в каноническое имя (чистильщик диска, перестройка
/// раскладки, копия версии под другой подтип) спрашивают здесь <see cref="RenameWouldBreak"/>, и
/// ответ у них один на всех.</summary>
public static class ProjectTree
{
    /// <summary>Файл в папке, названный так же, как сама папка, — точка входа проекта. Их может быть
    /// несколько (у Kinco рядом лежат <c>.dpj</c>, <c>.pkgx</c> и <c>.bak</c> с одним именем);
    /// возвращается первый по алфавиту — нужен сам факт, а не выбор между ними.</summary>
    /// <summary>То же, но с предпочтением по расширению — для случая «надо ОТКРЫТЬ проект».
    ///
    /// Одноимённых файлов у проекта обычно несколько: у Kinco рядом с <c>.dpj</c> лежат <c>.pkgx</c>
    /// и <c>.bak</c>. Для вопроса «это дерево проекта?» годится любой, а вот открывать надо именно
    /// проект: по алфавиту первым оказывается <c>.bak</c>, то есть резервная копия, и открыв её
    /// человек правил бы вчерашнюю версию. Поэтому сперва ищем среди известных расширений среды и
    /// только потом откатываемся на «любой одноимённый».</summary>
    public static string? EntryFileIn(string? folder, IReadOnlyCollection<string> preferredExtensions)
    {
        var all = EntryFilesIn(folder);
        var preferred = all.FirstOrDefault(f =>
            preferredExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        return preferred ?? all.FirstOrDefault();
    }

    /// <summary>Все одноимённые папке файлы, по алфавиту.</summary>
    private static List<string> EntryFilesIn(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return new List<string>();
        var name = FolderName(folder!);
        if (name.Length == 0) return new List<string>();
        try
        {
            return Directory.EnumerateFiles(folder!, "*", SearchOption.TopDirectoryOnly)
                .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception) { return new List<string>(); }
    }

    public static string? EntryFileIn(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var name = FolderName(folder!);
        if (name.Length == 0) return null;
        try
        {
            return Directory.EnumerateFiles(folder!, "*", SearchOption.TopDirectoryOnly)
                .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception) { return null; }
    }

    /// <summary>Этот файл — точка входа в СВОЮ одноимённую папку.</summary>
    public static bool IsEntryFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var folder = SafeParent(filePath!);
        if (folder is null) return false;
        return string.Equals(Path.GetFileNameWithoutExtension(filePath!), FolderName(folder),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Папка — дерево проекта: в ней есть одноимённый файл И рядом с ним есть окружение
    /// (подпапки ресурсов или вторые файлы того же имени — резервные копии, пакеты).
    ///
    /// Одного совпадения имён мало: папка версии <c>1.0.0005.0001</c> с единственным файлом
    /// <c>1.0.0005.0001.psl</c> внутри — это НАША раскладка, а не чужой проект, и приводить её имя к
    /// каноническому мы обязаны.</summary>
    public static bool IsProjectTree(string? folder)
    {
        if (EntryFileIn(folder) is null) return false;
        if (CompanionFolders(folder!).Count > 0) return true;
        try
        {
            var name = FolderName(folder!);
            return Directory.EnumerateFiles(folder!, "*", SearchOption.TopDirectoryOnly)
                .Count(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase)) > 1;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Подпапки, которые НЕ являются служебными папками нашей раскладки. Хотя бы одна рядом
    /// с файлом означает, что это не одинокий файл, а дерево: исполняемый файл плюс драйверы,
    /// библиотеки и ресурсы. Символическая ссылка/junction — пустой список: что за ней лежит, мы не
    /// знаем, и делать по ней выводы нельзя.</summary>
    public static IReadOnlyList<string> CompanionFolders(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return Array.Empty<string>();
        try
        {
            if (IsLink(folder!)) return Array.Empty<string>();
            return Directory.EnumerateDirectories(folder!, "*", SearchOption.TopDirectoryOnly)
                .Where(d => !VersionLayout.IsVersionOwnFolder(FolderName(d)))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    /// <summary>Главный вопрос всех вызывающих: сломает ли переименование этого файла проект?
    /// Ломает по любому из двух признаков — файл является точкой входа в одноимённую папку
    /// (переименуем файл, а папка останется старой) либо рядом с ним лежит окружение проекта.
    ///
    /// Ходит на диск (в т.ч. на сетевой) — звать из фонового потока.</summary>
    public static bool RenameWouldBreak(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var folder = SafeParent(filePath!);
        if (folder is null) return false;
        if (CompanionFolders(folder).Count > 0) return true;
        return IsEntryFile(filePath) && IsProjectTree(folder);
    }

    /// <summary>Почему не переименовали — человеку в отчёт. null, когда переименование безопасно.</summary>
    public static string? WhyRenameWouldBreak(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var folder = SafeParent(filePath!);
        if (folder is null) return null;

        var companions = CompanionFolders(folder);
        if (companions.Count > 0)
            return $"рядом с файлом лежат папки проекта ({string.Join(", ", companions.Select(FolderName).Take(3))}) — " +
                   "имя не трогаем, иначе проект перестанет находить свои драйверы и дополнения";

        if (IsEntryFile(filePath) && IsProjectTree(folder))
            return $"файл назван так же, как папка «{FolderName(folder)}» — имя не трогаем: " +
                   "среда связывает проект с папкой по имени, и от переименования одной половины " +
                   "проект перестаёт находить свои расширения";

        return null;
    }

    private static string FolderName(string path) =>
        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static string? SafeParent(string path)
    {
        try { return Path.GetDirectoryName(path); }
        catch (Exception) { return null; }
    }

    private static bool IsLink(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception) { return false; }
    }
}
