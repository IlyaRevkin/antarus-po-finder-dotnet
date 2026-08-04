using System;
using System.IO;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Самый свежий актуальный файл сопроводительного документа версии — карта ВВ (in/out),
/// карта Modbus, инструкция.
///
/// Зачем отдельно: раньше пункт «Карта in/out» показывался по одному лишь заполненному пути в базе,
/// и висел даже когда файла на диске уже не было («кнопка есть, а открывать нечего»). А открывался
/// путь конкретной версии — то есть документ, приложенный когда-то давно, вместо актуального.
/// Документы лежат в общей папке рядом с папкой контроллера и обновляются независимо от версий
/// прошивки, поэтому правильный ответ — всегда самый свежий файл этой папки.
///
/// Ходит на диск (в т.ч. на сетевой) — звать из фонового потока или по клику, не из отрисовки.</summary>
public static class DocFileResolver
{
    /// <summary>Самый свежий из двух кандидатов: сохранённого у версии пути (файл; если это папка —
    /// самый свежий файл в ней) и общей папки документа рядом с папкой контроллера. Побеждает тот, что
    /// новее по времени изменения — «всегда открывать последний актуальный файл» из требования: путь,
    /// записанный к версии год назад, не должен перебивать карту, обновлённую в общей папке на прошлой
    /// неделе. null — открывать нечего (файла нет, папки нет или она пуста), тогда и пункта в меню
    /// карточки быть не должно.</summary>
    /// <param name="excludeSubfolder">Имя подпапки, содержимое которой документом не считается —
    /// «Прежние редакции» у паспорта шкафа (см. PassportService.ResolveDoc). Без этого прежняя
    /// редакция, у которой время изменения оказалось свежее (файл копируется со своей датой, а не с
    /// датой загрузки), выигрывала бы у актуального документа — ровно наоборот к требованию
    /// «всегда открывать свежую». null — прежнее поведение: вся папка целиком.</param>
    public static string? Resolve(string? storedPath, string? sharedFolder, string? excludeSubfolder = null)
    {
        var stored = StoredCandidate(storedPath);
        var shared = LatestFileIn(sharedFolder, excludeSubfolder);
        if (stored is null) return shared;
        if (shared is null) return stored;
        return WrittenAt(shared) > WrittenAt(stored) ? shared : stored;
    }

    private static string? StoredCandidate(string? storedPath)
    {
        if (string.IsNullOrEmpty(storedPath)) return null;
        if (File.Exists(storedPath)) return IsShortcut(storedPath) ? null : storedPath;
        return Directory.Exists(storedPath) ? LatestFileIn(storedPath) : null;
    }

    /// <summary>Ярлык Windows — не документ. Общая проверка для обоих резолверов документации.</summary>
    public static bool IsShortcut(string path) =>
        string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase);

    /// <summary>Недоступный файл (шара отвалилась между обходом и сравнением) считается самым старым —
    /// сравнение не должно падать из-за одного пути.</summary>
    private static DateTime WrittenAt(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>Самый свежий по времени изменения файл во всём дереве папки, или null — папка пуста,
    /// не существует или недоступна (отвалившаяся сетевая шара — это «нечего открыть», не ошибка).
    ///
    /// Ярлыки .lnk пропускаются: с появлением третьего диска инструкций на первом остаётся ярлык на
    /// уехавший файл (для коллег со старым клиентом, см. InstructionDiskResolver), и он — самый
    /// свежий файл в папке. Открыть его тоже можно, но тогда «последний актуальный документ» вечно
    /// оказывался бы ярлыком, а не самим документом.</summary>
    public static string? LatestFileIn(string? folder, string? excludeSubfolder = null)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => !IsShortcut(f) && !IsUnder(folder, f, excludeSubfolder))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Лежит ли файл внутри подпапки с таким именем (на любой глубине от <paramref name="root"/>).
    /// Пустое имя — не исключаем ничего.</summary>
    public static bool IsUnder(string root, string file, string? subfolderName)
    {
        if (string.IsNullOrEmpty(subfolderName)) return false;
        return Path.GetRelativePath(root, file)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .SkipLast(1)
            .Any(segment => string.Equals(segment, subfolderName, StringComparison.OrdinalIgnoreCase));
    }
}
