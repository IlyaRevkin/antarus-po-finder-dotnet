using System;
using System.Collections.Generic;
using System.IO;

namespace AntarusPoFinder.Core.Services;

/// <summary>Откуда берётся файл HMI-проекта. Зеркально <see cref="PlcOpenSources"/>, но у панели свой
/// порядок источников: отдельно загруженная папка проекта (чекбокс «Добавить HMI-проект» при
/// загрузке) идёт раньше любого детекта по расширениям.</summary>
public sealed record HmiOpenSources
{
    /// <summary>Путь к отдельно загруженному HMI-проекту из записи версии (FwVersionRecord.HmiPath) —
    /// папка «{версия}_hmi» либо файл.</summary>
    public string? HmiPath { get; init; }

    /// <summary>Общая папка «HMI» рядом с папкой контроллера — фоллбэк, когда путь из записи уже не
    /// существует (папку перенесли/переименовали на диске).</summary>
    public string? SiblingHmiFolder { get; init; }

    /// <summary>Явно указанный оператором файл панели (FwVersionRecord.HmiExecutableHint).</summary>
    public string? ExecutableHint { get; init; }

    /// <summary>Папки версии для поиска по подсказке — см. SearchView.CandidateFolders.</summary>
    public IReadOnlyList<string> CandidateFolders { get; init; } = Array.Empty<string>();

    /// <summary>Папка прошивки в кэше + сетевая, обход со вложенными — для детекта по расширениям.</summary>
    public IReadOnlyList<string> FilteredFolders { get; init; } = Array.Empty<string>();
}

/// <summary>Какой именно файл откроет кнопка «Открыть HMI проект» — то же разделение, что и у
/// <see cref="PlcOpenResolver"/>: одно решение и для открытия, и для подписи кнопки, чтобы написанное
/// на кнопке расширение не могло разойтись с тем, что реально откроется.
///
/// Ходит на диск (в т.ч. на сетевой) — звать из фонового потока или по клику, не из отрисовки.</summary>
public static class HmiOpenResolver
{
    /// <summary>Путь, который откроется, или null — открывать нечего. Может вернуть ПАПКУ проекта
    /// (оператор не указал исполняемый файл внутри) — тогда расширения на кнопке не будет.</summary>
    public static string? Resolve(HmiOpenSources src)
    {
        // 1. Отдельно загруженный проект панели: сам путь, а если его уже нет на диске — общая папка
        //    «HMI» рядом с контроллером. Внутри открываем указанный оператором файл, если он указан.
        if (!string.IsNullOrEmpty(src.HmiPath))
        {
            var folder = Exists(src.HmiPath) ? src.HmiPath
                : Exists(src.SiblingHmiFolder) ? src.SiblingHmiFolder
                : null;
            // Путь в записи есть, но на диске нет ни его, ни общей папки — это «не найдено», и
            // подменять его детектом по расширениям нельзя: открылся бы файл другого проекта.
            if (folder is null) return null;
            if (ExecutableHintResolver.Resolve(folder, src.ExecutableHint) is { } hintedInProject) return hintedInProject;
            // Подсказки нет, но в папке проекта ровно один файл панели — открывать надо его, а не
            // папку: оператору всё равно оставалось сделать по ней двойной клик, а на кнопке при этом
            // не было расширения. Ровно один — значит выбор однозначен; больше одного (или ноль) —
            // открываем папку, пусть оператор выберет сам.
            if (ExecutableHintResolver.AutoDetect(folder, PlcOpenResolver.HmiExtensions) is { } onlyProjectFile)
                return Path.Combine(folder, onlyProjectFile);
            return folder;
        }

        // 2. Панель лежит в папке самой версии, и оператор указал, какой файл в ней — панельный.
        if (ExecutableHintResolver.Normalize(src.ExecutableHint) is not null)
            foreach (var dir in src.CandidateFolders)
                if (ExecutableHintResolver.Resolve(dir, src.ExecutableHint) is { } hinted)
                    return hinted;

        // 3. Старый детект по расширениям панели.
        return PlcOpenResolver.FindByExtensions(src.FilteredFolders, PlcOpenResolver.HmiExtensions);
    }

    /// <summary>Расширение того, что откроет кнопка — то же, что пишется на ней. null — откроется
    /// папка либо файл без расширения.</summary>
    public static string? ResolveExtension(HmiOpenSources src) => PlcOpenResolver.ExtensionOf(Resolve(src));

    private static bool Exists(string? path) =>
        !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));
}
