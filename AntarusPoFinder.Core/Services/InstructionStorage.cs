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
                {
                    ArchivePrevious(dest, sourcePath);
                    File.Copy(sourcePath, dest, overwrite: true);
                }
                return dest;
            }
        }
        return FileSystemHelpers.CopyFileOrFolderShallow(sourcePath, targetFolder);
    }

    /// <summary>Папка, в которую уезжает прежняя редакция инструкции. То же слово, что у таблиц
    /// параметров: человек, однажды понявший, что это значит, не должен разгадывать заново.</summary>
    public const string PreviousFolderName = "Прежние редакции";

    /// <summary>Убрать прежнюю инструкцию в архив, прежде чем на её место ляжет новая.
    ///
    /// До этого перезаливка делала <c>File.Copy(overwrite: true)</c>, то есть прошлая редакция
    /// исчезала молча и безвозвратно. А инструкция — документ, который правят: «добавили пункт про
    /// насос» и «переписали половину» выглядят одинаково, пока не с чем сравнить, и ответить
    /// «а что было в прошлой версии» было нечем.
    ///
    /// Имя получает слепок времени (<c>инструкция_2.1.0004_20261028-0915.pdf</c>), потому что
    /// редакций бывает несколько в день, и «.bak» на второй перезаливке затирал бы сам себя —
    /// ровно та же беда, от которой уходим.
    ///
    /// Байт-в-байт тот же файл не архивируется: повторная загрузка того же документа — обычное
    /// дело (переложили из другой папки), и плодить от неё копии значит засорять папку так, что в
    /// ней перестают искать.</summary>
    private static void ArchivePrevious(string dest, string sourcePath)
    {
        try
        {
            if (!File.Exists(dest)) return;

            var existing = new FileInfo(dest);
            var incoming = new FileInfo(sourcePath);
            if (incoming.Exists && existing.Length == incoming.Length && SameBytes(dest, sourcePath)) return;

            var folder = Path.Combine(Path.GetDirectoryName(dest) ?? "", PreviousFolderName);
            Directory.CreateDirectory(folder);

            var stamp = existing.LastWriteTime.ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture);
            var archived = Path.Combine(folder,
                $"{Path.GetFileNameWithoutExtension(dest)}_{stamp}{Path.GetExtension(dest)}");

            // Две редакции в одну минуту — редкость, но не невозможность: даём второй свой суффикс
            // вместо того, чтобы затереть первую (ради чего всё и затевалось).
            var attempt = 1;
            while (File.Exists(archived))
            {
                archived = Path.Combine(folder,
                    $"{Path.GetFileNameWithoutExtension(dest)}_{stamp}-{++attempt}{Path.GetExtension(dest)}");
            }

            File.Move(dest, archived);
        }
        catch (Exception)
        {
            // Не вышло убрать в архив — пусть лучше встанет новая инструкция, чем загрузка
            // свалится на полпути. Потеря прежней редакции неприятна, отсутствие новой хуже.
        }
    }

    /// <summary>Тот же ли это файл. Сравнение по содержимому, а не по дате: документ, перенесённый
    /// между папками, меняет дату и остаётся тем же самым.</summary>
    private static bool SameBytes(string a, string b)
    {
        try
        {
            using var left = File.OpenRead(a);
            using var right = File.OpenRead(b);
            var bufferLeft = new byte[CompareBuffer];
            var bufferRight = new byte[CompareBuffer];
            int read;
            while ((read = left.Read(bufferLeft, 0, bufferLeft.Length)) > 0)
            {
                var mirrored = right.Read(bufferRight, 0, read);
                if (mirrored != read) return false;
                for (var i = 0; i < read; i++)
                {
                    if (bufferLeft[i] != bufferRight[i]) return false;
                }
            }
            return right.Read(bufferRight, 0, 1) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private const int CompareBuffer = 64 * 1024;

    private static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception) { return null; }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
