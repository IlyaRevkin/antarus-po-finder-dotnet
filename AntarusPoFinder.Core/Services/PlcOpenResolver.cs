using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Loader;

namespace AntarusPoFinder.Core.Services;

/// <summary>Папки, в которых ищется файл проекта ПЛК. Разные шаги ищут в разных наборах — это не
/// избыточность, а осознанная разница (см. SearchView.CandidateFolders/VersionFolders):
/// <list type="bullet">
/// <item><description><b>Candidate</b> — точная папка версии в локальном кэше, соседние версии этой
/// же прошивки, и только потом сетевая папка. Для «чем открыть» подмена соседней версией —
/// приемлемый фоллбэк.</description></item>
/// <item><description><b>Version</b> — строго папки ЭТОЙ версии. Для .psl/.lfs подмена соседней
/// версией недопустима: в контроллер уехала бы чужая прошивка.</description></item>
/// <item><description><b>Filtered</b> — вся папка прошивки в кэше + сетевая папка, обход со
/// вложенными (проекты, где программа ПЛК и панель лежат рядом).</description></item>
/// </list></summary>
public sealed record PlcOpenSources
{
    public IReadOnlyList<string> CandidateFolders { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> VersionFolders { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FilteredFolders { get; init; } = Array.Empty<string>();

    /// <summary>Явно указанный оператором при загрузке исполняемый файл (FwVersionRecord.ExecutableHint).</summary>
    public string? ExecutableHint { get; init; }

    /// <summary>Папка версии на сетевом диске — последний фоллбэк: показать содержимое папки
    /// полезнее, чем сказать «не найдено».</summary>
    public string? NetworkFolder { get; init; }
}

/// <summary>Какой именно файл откроет кнопка «Открыть прошивку ПЛК».
///
/// Одно место и для самого открытия, и для подписи кнопки. Раньше решение жило только внутри
/// SearchView.OpenPlc, а карточка отдельно угадывала «.psl или .lfs» — из-за чего расширение на
/// кнопке появлялось лишь у проектов Segnetics, а у KINCO и любых прочих кнопка оставалась без
/// расширения, хотя открывался вполне конкретный файл. Теперь расширение берётся у того самого
/// файла, который реально откроется — для любого типа проекта.
///
/// Ходит на диск (в т.ч. на сетевой) — звать из фонового потока, не из отрисовки.</summary>
public static class PlcOpenResolver
{
    /// <summary>Проекты, где программа ПЛК и панель лежат в ОДНОЙ папке — это не только KINCO: то же
    /// бывает у любого вендора, где панель собирается отдельным файлом рядом с программой.</summary>
    public static readonly string[] PlcExtensions = { ".kpr", ".kpj", ".kpro", ".cpj", ".prj" };
    public static readonly string[] HmiExtensions = { ".dpj", ".emt", ".emtp", ".emsln" };

    /// <summary>Сопроводительные файлы — открывать их вместо проекта бессмысленно.</summary>
    private static readonly string[] DocExtensions = { ".md", ".txt", ".log" };

    /// <summary>Путь, который откроется по «Открыть прошивку ПЛК», или null — открывать нечего.
    /// Может вернуть ПАПКУ (последний фоллбэк), а не файл.</summary>
    public static string? Resolve(PlcOpenSources src)
    {
        // 1. Явно указанный оператором файл — работает для любого проекта и для файлов во вложенных
        //    папках; ему приоритет над любой эвристикой.
        if (ExecutableHintResolver.Normalize(src.ExecutableHint) is not null)
            foreach (var dir in src.CandidateFolders)
                if (ExecutableHintResolver.Resolve(dir, src.ExecutableHint) is { } hinted)
                    return hinted;

        // 2. Проект Segnetics: открывать надо именно .psl (исходник SMLogix) — .lfs не открывается
        //    ничем, кроме лоадера, а общая эвристика «первый непонятный файл в папке» вполне могла
        //    взять его и молча открыть блокнот.
        if (LoaderFiles.FindIn(src.VersionFolders, LoaderFiles.PslExtension) is { } psl) return psl;

        // 3. Программа ПЛК и панель рядом — берём именно программу ПЛК, иначе «первый подходящий
        //    файл в папке» может открыть файл панели.
        if (FindByExtensions(src.FilteredFolders, HmiExtensions) is not null
            && FindByExtensions(src.FilteredFolders, PlcExtensions) is { } plc) return plc;

        // 4. Общий детект «первый непонятный файл» по тем же папкам-кандидатам.
        foreach (var dir in src.CandidateFolders)
            if (FindUsableFile(dir, src.ExecutableHint) is { } target) return target;

        return !string.IsNullOrEmpty(src.NetworkFolder) && Directory.Exists(src.NetworkFolder)
            ? src.NetworkFolder
            : null;
    }

    /// <summary>Расширение файла, который откроется, в нижнем регистре и с точкой («.psl») — его и
    /// пишем на кнопке. null — открывается папка либо файл без расширения, тогда кнопка без скобок:
    /// пустые скобки хуже, чем их отсутствие.</summary>
    public static string? ExtensionOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) ? null : ext.ToLowerInvariant();
    }

    /// <summary>Расширение того, что откроет кнопка — Resolve + ExtensionOf одним вызовом.</summary>
    public static string? ResolveExtension(PlcOpenSources src) => ExtensionOf(Resolve(src));

    private static string? FindByExtensions(IEnumerable<string> dirs, string[] exts)
    {
        foreach (var dir in dirs)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            try
            {
                var hit = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()));
                if (hit is not null) return hit;
            }
            catch (Exception)
            {
                // Недоступная папка (сеть отвалилась, нет прав) — это «не нашли», а не ошибка:
                // один битый путь не должен ронять отрисовку всей выдачи поиска.
            }
        }
        return null;
    }

    /// <summary>Первый файл в папке, похожий на открываемый: подсказка оператора, иначе первый
    /// не-сопроводительный файл верхнего уровня.</summary>
    private static string? FindUsableFile(string dir, string? hint)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        if (ExecutableHintResolver.Resolve(dir, hint) is { } preferred) return preferred;
        try
        {
            return Directory.EnumerateFiles(dir)
                .FirstOrDefault(f => !DocExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
