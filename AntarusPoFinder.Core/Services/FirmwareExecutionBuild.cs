using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Положить готовую конфигурацию исполнения в УЖЕ СУЩЕСТВУЮЩУЮ версию.
///
/// Это и есть ответ на главную просьбу: «не могу сделать одну и ту же прошивку в разных исполнениях,
/// только путём увеличения версии sw, а так нельзя». Здесь версия НЕ заводится, номер ПО НЕ
/// выдаётся и запись в БД НЕ создаётся — файл просто ложится в свою папку внутри «Прошивка\».
/// Поэтому и номер остаётся один: его некому увеличить.
///
/// Чего этот класс намеренно НЕ делает:
/// <list type="bullet">
/// <item>не переименовывает файл в номер версии. У базовой сборки каноническое имя нужно, чтобы её
///       узнавали среди соседей в одной папке; у исполнения своя папка, и собственное имя файла —
///       единственное, по чему человек на диске поймёт, что за конфигурация внутри;</item>
/// <item>не трогает базовую сборку и другие исполнения;</item>
/// <item>не удаляет чужие файлы молча: папка, в которой уже что-то лежит, требует явного согласия
///       (<paramref name="replaceExisting"/>). Конфигурация исполнения — это то, что уедет в шкаф,
///       и перезаписать её «заодно» нельзя.</item>
/// </list></summary>
public static class FirmwareExecutionBuild
{
    public sealed record Result(bool Ok, string Folder, string? Error, IReadOnlyList<string> Copied)
    {
        /// <summary>В папке исполнения уже что-то лежит — нужно согласие человека на замену.
        /// Отдельный признак, а не просто текст ошибки: по нему вызывающий задаёт вопрос и
        /// повторяет вызов, а не показывает тупик.</summary>
        public bool NeedsReplaceConfirmation { get; init; }
    }

    /// <summary>Куда ляжет конфигурация и почему это сейчас нельзя — БЕЗ единой записи на диск.
    ///
    /// Нужен предпросмотру («куда ляжет») формы загрузки. Считать путь второй раз, своим
    /// Path.Combine, ему нельзя: разойдись предпросмотр с тем, что реально делает <see cref="Place"/>,
    /// — и человек увидит один путь, а файл уедет по другому. Поэтому обе стороны спрашивают здесь.</summary>
    public sealed record Plan(string Folder, string? Error)
    {
        public bool Ok => Error is null;
    }

    /// <inheritdoc cref="Plan"/>
    /// <param name="versionDir">Папка версии, в которую кладём.</param>
    /// <param name="execution">Исполнение из справочника шкафа.</param>
    /// <param name="sourceFiles">null — файлы ещё не выбраны и проверять их не надо (предпросмотр
    /// показывает папку уже тогда, когда источник ещё не перетащили).</param>
    public static Plan Prepare(string? versionDir, string? execution, IEnumerable<string>? sourceFiles)
    {
        if (string.IsNullOrWhiteSpace(versionDir) || !SafeDirExists(versionDir))
            return new Plan("", "Папка версии не найдена на диске.");

        if (VersionLayout.ExecutionFolderName(execution).Length == 0)
            return new Plan("", "Не выбрано исполнение.");

        var folder = VersionLayout.ExecutionWriteFolder(versionDir!, execution);
        if (sourceFiles is not null && !sourceFiles.Any(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f)))
            return new Plan(folder, "Не выбрано ни одного файла конфигурации.");
        return new Plan(folder, null);
    }

    /// <summary>Скопировать файлы конфигурации в «&lt;версия&gt;\Прошивка\&lt;исполнение&gt;\».</summary>
    /// <param name="versionDir">Папка версии на диске — та самая, в которой лежит базовая сборка.</param>
    /// <param name="execution">Исполнение из справочника шкафа.</param>
    /// <param name="sourceFiles">Что копировать: один файл прошивки или несколько (проект с
    /// сопровождающими файлами).</param>
    /// <param name="replaceExisting">Согласие заменить то, что в папке исполнения уже лежит.</param>
    public static Result Place(string? versionDir, string? execution, IEnumerable<string> sourceFiles,
        bool replaceExisting = false)
    {
        var files = sourceFiles?.Where(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f)).ToList()
                    ?? new List<string>();

        // Проверки и путь — те же, что показал предпросмотр: один расчёт на обе стороны.
        var plan = Prepare(versionDir, execution, files);
        if (!plan.Ok) return new Result(false, plan.Folder, plan.Error, Array.Empty<string>());
        var folder = plan.Folder;
        try
        {
            if (SafeDirExists(folder) && Directory.EnumerateFileSystemEntries(folder).Any() && !replaceExisting)
                return new Result(false, folder,
                    $"В папке исполнения «{execution}» этой версии уже есть файлы.", Array.Empty<string>())
                { NeedsReplaceConfirmation = true };

            if (replaceExisting && SafeDirExists(folder))
                foreach (var old in Directory.EnumerateFiles(folder)) File.Delete(old);

            Directory.CreateDirectory(folder);
            var copied = new List<string>();
            foreach (var src in files)
            {
                var dst = Path.Combine(folder, Path.GetFileName(src));
                File.Copy(src, dst, overwrite: true);
                copied.Add(dst);
            }
            return new Result(true, folder, null, copied);
        }
        catch (Exception e)
        {
            // Ошибку не глотаем и «по возможности» не продолжаем: половина скопированной
            // конфигурации хуже, чем её отсутствие, — отсутствие видно на карточке, а половина нет.
            return new Result(false, folder, e.Message, Array.Empty<string>());
        }
    }

    /// <summary>Убрать конфигурацию исполнения из версии — «больше так не собираем именно в этой
    /// версии». Справочник шкафа при этом не трогается: убрать исполнение совсем — другое действие
    /// и другое решение (см. Database.DeleteSubtypeExecution).</summary>
    public static Result Remove(string? versionDir, string? execution)
    {
        if (string.IsNullOrWhiteSpace(versionDir) || !SafeDirExists(versionDir))
            return new Result(false, "", "Папка версии не найдена на диске.", Array.Empty<string>());
        if (VersionLayout.ExecutionFolderName(execution).Length == 0)
            return new Result(false, "", "Не выбрано исполнение.", Array.Empty<string>());

        var folder = VersionLayout.ExecutionFolder(versionDir!, execution);
        if (!SafeDirExists(folder)) return new Result(true, folder, null, Array.Empty<string>());
        try
        {
            Directory.Delete(folder, recursive: true);
            return new Result(true, folder, null, Array.Empty<string>());
        }
        catch (Exception e)
        {
            return new Result(false, folder, e.Message, Array.Empty<string>());
        }
    }

    private static bool SafeDirExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Directory.Exists(path); }
        catch (Exception) { return false; }
    }
}
