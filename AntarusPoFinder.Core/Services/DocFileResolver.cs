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
    public static string? Resolve(string? storedPath, string? sharedFolder)
    {
        var stored = StoredCandidate(storedPath);
        var shared = LatestFileIn(sharedFolder);
        if (stored is null) return shared;
        if (shared is null) return stored;
        return WrittenAt(shared) > WrittenAt(stored) ? shared : stored;
    }

    private static string? StoredCandidate(string? storedPath)
    {
        if (string.IsNullOrEmpty(storedPath)) return null;
        if (File.Exists(storedPath)) return storedPath;
        return Directory.Exists(storedPath) ? LatestFileIn(storedPath) : null;
    }

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
    /// не существует или недоступна (отвалившаяся сетевая шара — это «нечего открыть», не ошибка).</summary>
    public static string? LatestFileIn(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
