using System;
using System.IO;

namespace AntarusPoFinder.Core.Loader;

/// <summary>ЧТО ИМЕННО ОТПРАВЛЯТЬ В КОНТРОЛЛЕР.
///
/// <b>Из-за чего заведено.</b> SMH5 после заливки из Finder уходил в бесконечную перезагрузку, а те
/// же файлы, залитые из SMLogix или из самого Loader, работали — без всякого форматирования
/// (сообщение Ильи 21.10.2026: «проблема точно в фаиндере, так как и через лоджик и через лоадер
/// всё ок и без форматирования»). Разница была не в подготовке ПЛК, а в АРТЕФАКТЕ: Finder выбирал
/// <c>files.LfsPath ?? files.PslPath</c>, то есть предпочитал .lfs, а человек в Loader открывал
/// проект.
///
/// Почему это ломает именно SMH5. План операции у Loader зависит от типа файла
/// (docs/loader/LOADER_AUTOMATION_API.md, «Поддерживаемые артефакты»):
/// <list type="bullet">
/// <item><c>.lfs</c> — проверка подписи и загрузка LFS. И всё.</item>
/// <item><c>.psl</c> — сборка изолированным SMLogix, затем загрузка собранного.</item>
/// <item><c>.zop</c> — извлечение, загрузка LFS, ожидание готовности, <b>загрузка ELF</b>.</item>
/// </list>
/// У SMH5 (Rockchip) проект состоит из двух частей — LFS и ELF. Залив один LFS, мы оставляем в
/// контроллере ЧУЖУЮ ELF-часть: прежнего проекта или заводскую. Пара несогласованна, и до первого
/// снятия питания это не видно — ровно то, что наблюдал Илья.
///
/// <b>Правило.</b> Заливаем файл, который несёт проект ЦЕЛИКОМ: .zop (обе части) или .psl (Loader
/// соберёт сам). Голый .lfs — только там, где ELF-части нет в принципе и где так заливали годами
/// (SMH4 и прочие старые семейства): запрещать им привычный путь из-за чужой беды нельзя.
///
/// Форматирование к этому отношения не имеет и больше не навязывается: подготовка ПЛК — отдельный
/// вопрос про состояние контроллера, а не способ компенсировать неверно выбранный файл.</summary>
public static class DeployArtifact
{
    public const string ZopExtension = ".zop";

    /// <summary>Семейства, где проект состоит из LFS + ELF и голый LFS заливать нельзя.
    ///
    /// Список, а не признак «новое поколение»: следующий контроллер на той же платформе появится
    /// раньше, чем мы узнаем, как его отличать программно, и тогда сюда добавится одна строка.</summary>
    private static readonly string[] TwoPartFamilies = ["SMH5"];

    public static bool NeedsWholeProject(string? controllerName)
    {
        if (string.IsNullOrWhiteSpace(controllerName)) return false;
        var name = controllerName.Trim().ToUpperInvariant();
        return Array.Exists(TwoPartFamilies, f => name.StartsWith(f, StringComparison.Ordinal));
    }

    /// <summary>Результат выбора: путь к файлу либо причина, по которой заливать нечем.</summary>
    public sealed record Choice(string? Path, string? Problem)
    {
        public bool Ok => Path is not null;
    }

    /// <summary>Выбрать файл для заливки.
    ///
    /// Порядок одинаков для всех: целый проект сначала, собранный LFS — последним. Разница только в
    /// том, что для двухчастных семейств последний вариант не разрешён вовсе.</summary>
    public static Choice Pick(string? zopPath, string? pslPath, string? lfsPath, string? controllerName)
    {
        if (!string.IsNullOrWhiteSpace(zopPath)) return new Choice(zopPath, null);
        if (!string.IsNullOrWhiteSpace(pslPath)) return new Choice(pslPath, null);

        if (!string.IsNullOrWhiteSpace(lfsPath))
        {
            if (!NeedsWholeProject(controllerName)) return new Choice(lfsPath, null);

            return new Choice(null,
                $"В папке версии есть только собранный {System.IO.Path.GetFileName(lfsPath)}, а {controllerName?.Trim()} "
                + "так заливать нельзя: его проект состоит из двух частей (LFS и ELF), и от одного LFS "
                + "в контроллере остаётся чужая ELF-часть — он доработает до снятия питания, а потом "
                + "уйдёт в бесконечную перезагрузку. Положите в папку версии исходный проект (.psl) "
                + "или .zop — Loader соберёт и зальёт обе части сам.");
        }

        return new Choice(null, "В папке версии нет ни проекта (.psl/.zop), ни собранного файла (.lfs).");
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
