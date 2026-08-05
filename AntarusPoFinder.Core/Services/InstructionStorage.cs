using System;
using System.Collections.Generic;
using System.IO;
using AntarusPoFinder.Core.Infrastructure;

namespace AntarusPoFinder.Core.Services;

/// <summary>Куда физически ложится файл инструкции при загрузке версии и при догрузке к уже
/// существующей — ОДНО место на оба пути (FirmwareUploadService и FirmwareAttachmentsService),
/// чтобы «доложенная» инструкция оказывалась ровно там же, где и приложенная сразу.
///
/// Смысл третьего диска — см. <see cref="InstructionDiskResolver"/>. Здесь важны два следствия:
///
/// 1. **В базу пишется путь на ПЕРВОМ диске, даже когда файл уехал на третий.** Путь из
///    <c>fw_versions.instructions_path</c> разъезжается по машинам синхронизацией, а буква третьего
///    диска у каждой машины своя — записав туда «T:\…», мы сломали бы карточку у всех, у кого
///    третий диск подключён под другой буквой или не подключён вовсе. Читающая сторона сама
///    считает зеркало от своих настроек (<see cref="InstructionDiskResolver.PreferredReadFolder"/>),
///    поэтому первого диска в записи достаточно.
/// 2. **На первом диске остаётся ярлык .lnk** (если включено): коллега со старым клиентом, который
///    про третий диск не знает, иначе увидит пустую папку «Инструкция» и решит, что инструкции нет.
///    Ярлык — не документ: оба резолвера документов его пропускают (DocFileResolver.IsShortcut).
///
/// Недоступный/ненастроенный третий диск — не ошибка: файл просто ложится на первый, как до
/// появления этой возможности.</summary>
public static class InstructionStorage
{
    /// <summary>Что и куда положили. <paramref name="StoredPath"/> — то, что надо записать в БД
    /// (всегда на первом диске, см. комментарий класса); пусто — не положили ничего.</summary>
    public sealed record Placement(string StoredPath, string? ActualPath, bool WentToThirdDisk);

    /// <summary>Копирует инструкцию в папку «Инструкция» этого контроллера — на третий диск, если он
    /// настроен и доступен, иначе на первый. Бросает те же исключения, что и обычное копирование:
    /// вызывающий уже оборачивает их в предупреждение и загрузку из-за инструкции не отменяет.
    ///
    /// <paramref name="shortcuts"/> = null (тесты, консольные пути) — ярлык просто не создаётся.
    /// Неудача самого ярлыка тоже не фатальна: файл уже лёг, поэтому она уходит в
    /// <paramref name="warnings"/>, а не наверх исключением.
    ///
    /// <paramref name="versionRaw"/> — строка версии, к которой инструкцию прикладывают. Задана —
    /// положенный файл сразу получает каноническое имя «инструкция_&lt;версия&gt;.&lt;расширение&gt;»
    /// (см. <see cref="InstructionNaming"/>): имя проверяется и правится в момент укладки, а не
    /// когда-нибудь потом перестройкой диска. Пусто — имя источника сохраняется, как было раньше.</summary>
    public static Placement Copy(string sourcePath, string instructionFolderOnFirstDisk,
        string firstRoot, string? thirdRoot, bool createShortcut, IShortcutCreator? shortcuts,
        List<string> warnings, string versionRaw = "")
    {
        var target = InstructionDiskResolver.PreferredWriteFolder(firstRoot, thirdRoot, instructionFolderOnFirstDisk);
        if (string.IsNullOrEmpty(target)) return new Placement("", null, false);

        var wentToThird = !PathsEqual(target, instructionFolderOnFirstDisk);
        var actual = CopyIntoFolder(sourcePath, target, versionRaw);
        if (string.IsNullOrEmpty(actual)) return new Placement("", null, false);

        // Настоящая инструкция легла — заглушке рядом с ней делать нечего, причём ни на одном из
        // дисков: иначе рядом с документом вечно лежал бы файл, утверждающий, что документа нет.
        // Убирать её надо ДО переименования: имя у заглушки то же самое каноническое, и пока она
        // лежит, настоящий документ не смог бы под ним встать (см. InstructionStub).
        InstructionStub.RemoveForVersion(instructionFolderOnFirstDisk, firstRoot, thirdRoot);

        // Имя проверяется и правится ровно здесь — в единственном месте, через которое инструкция
        // попадает на диск и при загрузке версии, и при догрузке к уже существующей.
        actual = InstructionNaming.EnsureCanonicalName(actual, versionRaw);

        // В БД — путь на первом диске: тот же файл, только с корнем первого диска. Для копирования
        // папки CopyFileOrFolderShallow возвращает саму папку назначения, поэтому обратное
        // отображение работает одинаково и для файла, и для папки.
        var stored = wentToThird
            ? BackToFirstDisk(actual, firstRoot, thirdRoot, instructionFolderOnFirstDisk)
            : actual;

        if (wentToThird && createShortcut)
            TryCreateShortcut(instructionFolderOnFirstDisk, actual, shortcuts, warnings);

        return new Placement(stored, actual, wentToThird);
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

    /// <summary>Обратное отображение «путь на третьем диске → как он выглядел бы на первом». Считаем
    /// от папки инструкции (она известна с обеих сторон), а не разбором корней ещё раз — так проще и
    /// не зависит от того, чем закончились пути (слешем или нет).</summary>
    private static string BackToFirstDisk(string actualOnThird, string firstRoot, string? thirdRoot,
        string instructionFolderOnFirstDisk)
    {
        var mirrorFolder = InstructionDiskResolver.Mirror(firstRoot, thirdRoot, instructionFolderOnFirstDisk);
        if (mirrorFolder is null) return actualOnThird;

        var full = SafeFullPath(actualOnThird);
        var mirror = SafeFullPath(mirrorFolder)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full is null || mirror is null) return actualOnThird;

        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), mirror, StringComparison.OrdinalIgnoreCase))
            return instructionFolderOnFirstDisk;

        var prefix = mirror + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return actualOnThird;

        return Path.Combine(instructionFolderOnFirstDisk, full[prefix.Length..]);
    }

    /// <summary>Ярлык на уехавший файл — рядом с тем местом, где раньше лежал сам файл. Имя ярлыка =
    /// имя файла + .lnk, поэтому перезаливка инструкции ярлык обновляет, а не плодит второй.</summary>
    private static void TryCreateShortcut(string folderOnFirstDisk, string actualPath,
        IShortcutCreator? shortcuts, List<string> warnings)
    {
        if (shortcuts is null) return;
        try
        {
            Directory.CreateDirectory(folderOnFirstDisk);
            var name = Directory.Exists(actualPath)
                ? new DirectoryInfo(actualPath.TrimEnd(Path.DirectorySeparatorChar)).Name
                : Path.GetFileName(actualPath);
            var linkPath = Path.Combine(folderOnFirstDisk, name + ".lnk");
            shortcuts.Create(linkPath, actualPath, "Инструкция лежит на диске инструкций");
        }
        catch (Exception ex)
        {
            warnings.Add($"Инструкция: файл положен на третий диск, но ярлык на первом не создан — {ex.Message}");
        }
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
