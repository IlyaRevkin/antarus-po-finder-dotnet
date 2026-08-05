using System;
using System.IO;
using System.Linq;
using System.Text;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что зашивать в QR-код на этикетке инструкции.
///
/// Порядок предпочтений ровно один и он важен:
/// 1. <b>Веб-ссылка</b> — базовый адрес диска инструкций + путь файла относительно корня диска. Это
///    единственный вариант, который открывается С ТЕЛЕФОНА, ради чего этикетка и заводится.
/// 2. <b>Сетевой путь</b> (<c>\\сервер\шара\…</c>) — если базовый адрес не задан. С рабочего
///    компьютера открывается, с телефона нет; лучше, чем пустой QR.
///
/// <b>Кириллица в ссылку идёт КАК ЕСТЬ, без процентного кодирования.</b> Раньше каждый сегмент
/// прогонялся через <c>Uri.EscapeDataString</c> — и одна русская буква превращалась в шесть символов
/// («П» → «%D0%9F»). Путь вида «ПО/НГР/КНС/SMH5/…/Инструкция/инструкция_2.1.0042.0001.pdf» раздувался
/// втрое, ссылка под кодом становилась нечитаемой лапшой, а сам QR — заметно плотнее: в байтовом
/// режиме «%D0%9F» это 6 байт против 2 байт той же буквы в UTF-8. Чем плотнее код, тем крупнее нужна
/// наклейка и тем хуже его берёт телефон — то есть кодирование било ровно по тому, ради чего этикетка
/// и заводится.
///
/// Так можно: адрес с не-ASCII символами — это IRI (RFC 3987), браузеры и сканеры QR принимают его и
/// сами переводят в проценты уже на проводе. Экранируем ПОСЕГМЕНТНО и только то, что иначе сломает
/// разбор адреса, — пробел, «%», «#», «?» и прочие структурные знаки (см. <see cref="EscapeSegment"/>).
/// Посегментно, а не строку целиком: слеши-разделители кодировать нельзя, иначе ссылка станет битой.</summary>
public static class LabelLinkBuilder
{
    /// <summary>Символы, которые в сегменте пути обязаны быть закодированы: они либо разделяют части
    /// самого адреса («#», «?»), либо не допускаются в URI вовсе («%» — начало escape-последовательности,
    /// пробел, кавычки, скобки), либо служат разделителем пути на другой платформе («\»). Буквы —
    /// любые, включая кириллицу — остаются как есть.</summary>
    private const string MustEscape = " %#?/\\\"<>[]^`{|}";

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
            .Select(EscapeSegment);

        return baseUrl.TrimEnd('/') + "/" + string.Join("/", segments);
    }

    /// <summary>Одно имя папки/файла, пригодное для подстановки в путь адреса. Кодируется только то,
    /// что иначе сломает разбор (см. <see cref="MustEscape"/>) и управляющие символы; буквы любого
    /// алфавита, цифры, точки, дефисы, скобки и подчёркивания остаются собой — благодаря этому ссылка
    /// на «…/Инструкция/инструкция_2.1.0042.0001.pdf» читается глазами и втрое короче в QR.</summary>
    public static string EscapeSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment)) return "";

        var sb = new StringBuilder(segment.Length);
        foreach (var ch in segment)
        {
            if (ch >= 0x20 && ch != 0x7F && MustEscape.IndexOf(ch) < 0)
            {
                sb.Append(ch);
                continue;
            }
            // Все обязательные к кодированию символы — ASCII, поэтому один байт на символ; UTF-8
            // берётся всё равно, чтобы формула не зависела от содержимого списка.
            foreach (var b in Encoding.UTF8.GetBytes(new[] { ch }))
                sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
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
