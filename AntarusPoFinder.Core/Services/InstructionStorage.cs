using System.Collections.Generic;
using System.IO;
using AntarusPoFinder.Core.Infrastructure;

namespace AntarusPoFinder.Core.Services;

/// <summary>Куда физически ложится файл инструкции при загрузке версии и при догрузке к уже
/// существующей — ОДНО место на оба пути (FirmwareUploadService и FirmwareAttachmentsService),
/// чтобы «доложенная» инструкция оказывалась ровно там же, где и приложенная сразу.
///
/// Инструкция кладётся рядом с прошивкой на первом диске (папка «Инструкция» контроллера или
/// версии) и, если хостинг настроен, вторым экземпляром уходит на хостинг
/// (<see cref="IInstructionPublisher"/>) по адресу, который повторяет путь на диске. Именно копия
/// на хостинге открывается по QR-коду с телефона. Ненастроенный хостинг — не ошибка: выкладка
/// молча не делается, файл всё равно лежит на диске.</summary>
public static class InstructionStorage
{
    /// <summary>Что и куда положили. <paramref name="StoredPath"/> — то, что надо записать в БД
    /// (путь на первом диске); пусто — не положили ничего. <paramref name="PublishedUrl"/> — адрес
    /// выложенной на хостинг копии или null, если хостинг не настроен либо выкладка не удалась
    /// (тогда причина уже лежит в warnings).</summary>
    public sealed record Placement(string StoredPath, string? ActualPath, string? PublishedUrl = null);

    /// <summary>Копирует инструкцию в папку «Инструкция» этого контроллера на первом диске. Бросает
    /// те же исключения, что и обычное копирование: вызывающий уже оборачивает их в предупреждение и
    /// загрузку из-за инструкции не отменяет.
    ///
    /// <paramref name="versionRaw"/> — строка версии, к которой инструкцию прикладывают. Задана —
    /// положенный файл сразу получает каноническое имя «инструкция_&lt;версия&gt;.&lt;расширение&gt;»
    /// (см. <see cref="InstructionNaming"/>): имя проверяется и правится в момент укладки, а не
    /// когда-нибудь потом перестройкой диска. Пусто — имя источника сохраняется, как было раньше.
    ///
    /// <paramref name="publisher"/> = null — хостинг не настроен (или вызывающему он не нужен):
    /// выкладки просто не происходит, всё остальное работает как прежде.</summary>
    public static Placement Copy(string sourcePath, string instructionFolderOnFirstDisk,
        string firstRoot, List<string> warnings, string versionRaw = "", IInstructionPublisher? publisher = null)
    {
        if (string.IsNullOrEmpty(instructionFolderOnFirstDisk)) return new Placement("", null);

        var actual = CopyIntoFolder(sourcePath, instructionFolderOnFirstDisk, versionRaw);
        if (string.IsNullOrEmpty(actual)) return new Placement("", null);

        // Настоящая инструкция легла — заглушке рядом с ней делать нечего: иначе рядом с документом
        // вечно лежал бы файл, утверждающий, что документа нет. Убирать её надо ДО переименования:
        // имя у заглушки то же самое каноническое, и пока она лежит, настоящий документ не смог бы
        // под ним встать (см. InstructionStub).
        InstructionStub.RemoveFrom(instructionFolderOnFirstDisk);

        // Имя проверяется и правится ровно здесь — в единственном месте, через которое инструкция
        // попадает на диск и при загрузке версии, и при догрузке к уже существующей.
        actual = InstructionNaming.EnsureCanonicalName(actual, versionRaw);

        var published = publisher?.Publish(actual, actual, firstRoot, warnings);

        return new Placement(actual, actual, published);
    }

    /// <summary>Положить инструкцию в папку СРАЗУ под каноническим именем
    /// «инструкция_&lt;версия&gt;.&lt;расширение&gt;» (см. <see cref="InstructionNaming"/>), а не под
    /// именем источника с переименованием следом.
    ///
    /// Разница видна ровно в одном случае — в ПЕРЕЗАЛИВКЕ, и она принципиальная. Копия под именем
    /// источника («Инструкция по эксплуатации v2.pdf») легла бы РЯДОМ, а каноническое имя осталось бы
    /// занято прошлой редакцией: переименовать поверх чужого файла
    /// <see cref="InstructionNaming.EnsureCanonicalName"/> не даст, и это правило верное — при
    /// перестройке диска затирать чужие документы нельзя. Итог был бы худший из возможных: по
    /// напечатанному и наклеенному QR открывалась бы СТАРАЯ инструкция, а новая лежала бы рядом
    /// незамеченной. Копирование сразу по каноническому пути делает то, чего от перезаливки и ждут:
    /// по постоянному адресу лежит текущая редакция.
    ///
    /// Инструкция ПАПКОЙ (сканы постранично) кладётся как раньше: переименовывать папку нельзя —
    /// её путь записан у коллег в <c>fw_versions.instructions_path</c>.</summary>
    private static string CopyIntoFolder(string sourcePath, string targetFolder, string versionRaw)
    {
        if (File.Exists(sourcePath) && !string.IsNullOrWhiteSpace(versionRaw))
        {
            var name = InstructionNaming.BuildFileName(versionRaw, Path.GetExtension(sourcePath));
            if (name.Length > 0)
            {
                Directory.CreateDirectory(targetFolder);
                var dest = Path.Combine(targetFolder, name);
                if (!PathsEqual(SafeFullPath(sourcePath) ?? sourcePath, SafeFullPath(dest) ?? dest))
                    File.Copy(sourcePath, dest, overwrite: true);
                return dest;
            }
        }
        return FileSystemHelpers.CopyFileOrFolderShallow(sourcePath, targetFolder);
    }

    private static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception) { return null; }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
