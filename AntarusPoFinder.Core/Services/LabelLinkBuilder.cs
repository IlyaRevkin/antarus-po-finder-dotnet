using System;
using System.IO;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что зашивать в QR-код на этикетке инструкции.
///
/// Порядок предпочтений ровно один и он важен:
/// 1. <b>Веб-ссылка</b> — базовый адрес диска инструкций + путь файла относительно корня диска. Это
///    единственный вариант, который открывается С ТЕЛЕФОНА, ради чего этикетка и заводится.
/// 2. <b>Сетевой путь</b> (<c>\\сервер\шара\…</c>) — если базовый адрес не задан. С рабочего
///    компьютера открывается, с телефона нет; лучше, чем пустой QR.
///
/// Кириллица и пробелы экранируются ПОСЕГМЕНТНО (<c>Uri.EscapeDataString</c> по каждому имени папки
/// отдельно): экранировать собранную строку целиком нельзя — «Карта ВВ» и любой путь с «/» дали бы
/// битую ссылку, а слеши-разделители оказались бы закодированы вместе с именами.</summary>
public static class LabelLinkBuilder
{
    /// <summary>Веб-ссылка на файл. null — базового адреса нет, файл лежит вне корня диска или путь
    /// битый: ссылку строить не из чего.</summary>
    public static string? BuildUrl(string? baseUrl, string? diskRoot, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(diskRoot) || string.IsNullOrWhiteSpace(filePath))
            return null;

        var relative = RelativeTo(diskRoot, filePath);
        if (relative is null) return null;

        var segments = relative
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);

        return baseUrl.TrimEnd('/') + "/" + string.Join("/", segments);
    }

    /// <summary>Путь файла относительно корня диска, или null, если файл лежит вне его. Сравнение
    /// без учёта регистра — Windows.</summary>
    public static string? RelativeTo(string diskRoot, string filePath)
    {
        string root, full;
        try
        {
            root = Path.GetFullPath(diskRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            full = Path.GetFullPath(filePath);
        }
        catch (Exception)
        {
            return null;
        }

        var prefix = root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full[prefix.Length..] : null;
    }
}
