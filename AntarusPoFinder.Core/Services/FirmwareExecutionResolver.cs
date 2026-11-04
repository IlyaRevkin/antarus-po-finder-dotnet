using System.Collections.Generic;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Где лежат файлы ВЫБРАННОГО исполнения этой версии — один ответ на всех: и на кнопки
/// карточки («Открыть LFS/PSL», «Открыть папку»), и на заливку в ПЛК.
///
/// Склейка трёх вещей, каждая из которых живёт отдельно и отдельно же проверяется:
/// <list type="bullet">
/// <item><see cref="VersionLayout"/> — раскладка на диске (единственное место, которое её знает);</item>
/// <item><see cref="FirmwareExecutionPick"/> — правило «что отдать и о чём предупредить»;</item>
/// <item>справочник исполнений шкафа из БД — его сюда ПЕРЕДАЮТ, класс в базу не ходит.</item>
/// </list>
///
/// Зачем склейка вообще нужна: папки исполнения и «пропустить папки исполнений при поиске базовой
/// сборки» — две половины одного решения, и разойтись им нельзя. Разойдись они — базовая сборка
/// молча подменится файлом чужого исполнения, то есть наладчик зальёт в шкаф не ту конфигурацию, а
/// это единственное, чего в этой задаче нельзя (docs/firmware-executions.md).</summary>
public sealed record FirmwareExecutionTarget(
    FirmwareExecutionPick Pick,
    IReadOnlyList<string> Folders,
    IReadOnlyList<string> SkipFolders)
{
    /// <summary>Выбранное исполнение (пусто — исходная сборка).</summary>
    public string Execution => Pick.Execution;

    /// <summary>Конфигурация под выбранное исполнение в этой версии есть.</summary>
    public bool Configured => Pick.Configured;

    /// <summary>Текст предупреждения для карточки — показывать там же, где показана версия.</summary>
    public string Warning => Pick.Warning;
}

public static class FirmwareExecutionResolver
{
    /// <summary>Решить, откуда брать файлы.
    /// </summary>
    /// <param name="versionDirs">Папки ИМЕННО этой версии в порядке предпочтения: локальная копия,
    /// потом сетевая (см. SearchView.VersionFolders). Соседние версии сюда попадать не должны — для
    /// исполнений это так же недопустимо, как и для .lfs: взять конфигурацию «3 насоса» из прошлой
    /// версии значит выдать устаревшую прошивку под видом свежей.</param>
    /// <param name="requested">Что выбрал человек: имя исполнения или пусто (исходная сборка).</param>
    /// <param name="catalog">Справочник исполнений ШКАФА (Database.GetSubtypeExecutions). По нему и
    /// только по нему папка внутри «Прошивка\» опознаётся как исполнение, а не как часть проекта.</param>
    public static FirmwareExecutionTarget Resolve(
        IEnumerable<string?> versionDirs, string? requested, IEnumerable<string>? catalog)
    {
        var dirs = versionDirs?.Where(d => !string.IsNullOrWhiteSpace(d)).ToList() ?? new List<string?>();
        var catalogList = (catalog ?? Enumerable.Empty<string>()).ToList();

        var builtAnywhere = dirs
            .SelectMany(d => VersionLayout.BuiltExecutions(d, catalogList))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();
        var pick = FirmwareExecutionPick.Pick(requested, builtAnywhere);

        if (pick.Execution.Length > 0 && pick.Configured)
            // Собранное исполнение: ищем ТОЛЬКО в его папках. Базовая сборка сюда не подмешивается —
            // иначе файл исполнения, которого не оказалось нужного расширения (есть .psl, нет .lfs),
            // молча заменился бы базовым.
            return new FirmwareExecutionTarget(pick, VersionLayout.ExecutionFolders(dirs, pick.Execution),
                System.Array.Empty<string>());

        // Исходная сборка — своя или выданная вместо несобранного исполнения. Папки исполнений из
        // поиска исключаются: базовая сборка это базовая сборка.
        return new FirmwareExecutionTarget(pick,
            dirs.Select(d => d!).ToList(),
            VersionLayout.ExecutionFoldersToSkip(dirs, catalogList));
    }
}
