using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Один файл внутри выбранной для загрузки папки — то, из чего оператор собирает ответ на
/// вопрос «что отсюда взять и что из этого является прошивкой».</summary>
/// <param name="RelativePath">Путь от корня выбранной папки («Driver\App.exe»).</param>
/// <param name="Size">Размер в байтах; -1 — прочитать не удалось (файл исчез, нет прав).</param>
/// <param name="IsJunk">Служебный мусор файловой системы (см. <see cref="JunkFiles"/>) — по
/// умолчанию не берётся и подписан причиной.</param>
/// <param name="LooksLikeFirmware">Расширение из списка «родных» для прошивки/панели.</param>
public sealed record FolderFileEntry(string RelativePath, long Size, bool IsJunk, bool LooksLikeFirmware);

/// <summary>Что взять из папки, выбранной в форме загрузки прошивки.
///
/// <b>Зачем понадобилось.</b> Раньше папка копировалась на диск ЦЕЛИКОМ, а выбор файла внутри неё был
/// только подсказкой «чем открывать» (<see cref="ExecutableHintResolver"/>): имя файла оставалось
/// прежним, а рядом с прошивкой уезжало всё, что лежало в папке у программиста, — заметки, старые
/// сборки, `Thumbs.db`. Жалоба «мусор в папках остаётся, файл не переименован» — ровно про это.
///
/// Теперь выбор из папки устроен как выбор одиночного файла: отмеченная прошивка ложится в
/// «Прошивка» под каноническим именем (<see cref="Domain.FirmwareNaming.BuildFirmwareFilename"/>), а
/// сопровождающие файлы едут только те, которые отметили руками. Здесь — перечисление и умолчания,
/// сам выбор показывает FolderContentsDialog, а копирует FirmwareUploadService.</summary>
public static class FolderUploadPick
{
    /// <summary>Всё содержимое папки, в порядке показа: сначала корень, потом вложенное. Нечитаемая
    /// папка — пустой список, как и у <see cref="ExecutableHintResolver.ListRelativeFiles"/>:
    /// загрузка обязана уметь работать, даже когда перечислить содержимое не вышло.</summary>
    public static List<FolderFileEntry> List(string folder, IReadOnlyCollection<string> knownExtensions,
        int maxFiles = 5000)
    {
        var result = new List<FolderFileEntry>();
        foreach (var relative in ExecutableHintResolver.ListRelativeFiles(folder, maxFiles))
        {
            long size = -1;
            try { size = new FileInfo(Path.Combine(folder, relative)).Length; }
            catch (Exception) { /* исчез между перечислением и опросом — покажем без размера */ }

            result.Add(new FolderFileEntry(
                relative,
                size,
                JunkFiles.IsJunk(relative),
                knownExtensions.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase)));
        }
        return result;
    }

    /// <summary>Какой файл предложить как саму прошивку: единственный с «родным» расширением. Их
    /// несколько или нет вовсе — null, выбирает оператор. То же правило, что у
    /// <see cref="ExecutableHintResolver.AutoDetect"/>, и это не совпадение: список кандидатов
    /// должен предлагать ровно то, что до сих пор подставлялось молча.</summary>
    public static string? DefaultMain(IEnumerable<FolderFileEntry> entries)
    {
        var matches = entries.Where(e => e.LooksLikeFirmware && !e.IsJunk).Take(2).ToList();
        return matches.Count == 1 ? matches[0].RelativePath : null;
    }

    /// <summary>Человеческий размер файла для таблицы выбора. -1 (не прочитали) — пустая строка:
    /// придумывать «0 КБ» там, где размер неизвестен, значит врать.</summary>
    public static string SizeLabel(long size)
    {
        if (size < 0) return "";
        if (size < 1024) return $"{size} Б";
        if (size < 1024 * 1024) return $"{size / 1024.0:0.#} КБ";
        return $"{size / (1024.0 * 1024.0):0.#} МБ";
    }
}
