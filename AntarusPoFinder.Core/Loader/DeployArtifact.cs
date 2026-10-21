using System;
using System.IO;

namespace AntarusPoFinder.Core.Loader;

/// <summary>ЧТО ИМЕННО ОТПРАВЛЯТЬ В КОНТРОЛЛЕР.
///
/// <b>Две неверные версии подряд — и почему они здесь записаны.</b>
/// 15.10.2026 SMH5 после заливки из Finder уходили в бесконечную перезагрузку. Сначала решили, что
/// им не хватает подготовки ПЛК, и форматирование для них сделали обязательным. 21.10.2026 Илья
/// проверил: «через лоджик и через лоадер всё ок и без форматирования» — значит дело не в
/// подготовке. Тогда возникла вторая версия: Finder заливает голый .lfs, а человек открывает
/// проект, поэтому у SMH5 остаётся несогласованная ELF-часть. Её тоже проверили, и она не
/// подтвердилась: «в лоадере я заливаю только лфс». Тот же файл, тот же способ — и руками работает.
///
/// Значит разница не в ТИПЕ файла. Поэтому правило выбора вернулось к прежнему (собранный .lfs —
/// первый, он не требует пересборки), а поиск причины продолжается в другом месте: что именно
/// Finder передаёт Automation и какой план операции тот строит. Ради этого здесь остался
/// <see cref="LogLine"/>, а окно загрузки пишет в журнал план, пришедший от Loader, — без этих
/// записей следующая версия причины будет такой же догадкой, как две предыдущие.
///
/// Польза от разбора всё же осталась: .zop и .psl теперь ВИДНЫ программе (раньше она знала только
/// .lfs и .psl, а выбирала всегда .lfs). Если в папке версии лежит .zop — заливается он: это проект
/// целиком, и спорить с ним не о чем.</summary>
public static class DeployArtifact
{
    public const string ZopExtension = ".zop";

    /// <summary>Результат выбора: путь к файлу либо причина, по которой заливать нечем.</summary>
    public sealed record Choice(string? Path, string? Problem)
    {
        public bool Ok => Path is not null;
    }

    /// <summary>Выбрать файл для заливки.
    ///
    /// Порядок: .zop (проект целиком) → .lfs (собранный, заливается без пересборки) → .psl
    /// (Loader соберёт сам). Собранный файл раньше исходника намеренно: именно так работает человек
    /// руками, а пересборка изолированным SMLogix при заливке — это другой файл, чем тот, что
    /// проверил программист.</summary>
    public static Choice Pick(string? zopPath, string? pslPath, string? lfsPath, string? controllerName)
    {
        if (!string.IsNullOrWhiteSpace(zopPath)) return new Choice(zopPath, null);
        if (!string.IsNullOrWhiteSpace(lfsPath)) return new Choice(lfsPath, null);
        if (!string.IsNullOrWhiteSpace(pslPath)) return new Choice(pslPath, null);

        return new Choice(null, "В папке версии нет ни собранного файла (.lfs/.zop), ни проекта (.psl).");
    }

    /// <summary>Строка в журнал операции: что выбрали и почему. Журнал заливки читают, когда
    /// контроллер повёл себя странно, и «какой файл ушёл в ПЛК» — первый вопрос.</summary>
    public static string LogLine(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ZopExtension => $"В ПЛК уходит {System.IO.Path.GetFileName(path)}: проект целиком, обе части (LFS и ELF).",
        LoaderFiles.PslExtension => $"В ПЛК уходит {System.IO.Path.GetFileName(path)}: Loader соберёт проект сам и зальёт результат.",
        _ => $"В ПЛК уходит {System.IO.Path.GetFileName(path)}: только LFS-часть.",
    };
}
