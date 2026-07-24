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
    /// <summary>Порядок: сохранённый у версии путь, если он указывает на существующий файл; если это
    /// папка — самый свежий файл в ней; иначе — общая папка документа рядом с папкой контроллера,
    /// снова самый свежий файл. null — открывать нечего (папки нет или она пуста), тогда и пункта в
    /// меню карточки быть не должно.</summary>
    public static string? Resolve(string? storedPath, string? sharedFolder)
    {
        if (!string.IsNullOrEmpty(storedPath))
        {
            if (File.Exists(storedPath)) return storedPath;
            if (Directory.Exists(storedPath) && LatestFileIn(storedPath) is { } inStored) return inStored;
        }
        return string.IsNullOrEmpty(sharedFolder) ? null : LatestFileIn(sharedFolder);
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
