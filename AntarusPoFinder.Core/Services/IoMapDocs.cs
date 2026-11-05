using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>КАРТА ВХОДОВ-ВЫХОДОВ ДЛЯ ТЕЛЕФОНА: найти файл карты у версии и, если это книга Excel,
/// прочитать её в простую сетку, которую можно показать страницей.
///
/// <b>Почему вообще приходится выбирать.</b> Разобранного представления карты ВВ в программе нет
/// нигде: у версии лежит ПУТЬ (<c>fw_versions.io_map_path</c>) плюс общая папка «Карта ВВ» рядом с
/// папкой контроллера, а вся работа с картой на компьютере — это «открыть файл тем, чем система
/// открывает» (SearchView.OpenMap). То есть в отличие от параметров ПЧ/УПП здесь нечего показывать
/// «как в программе»: в программе показывает Excel.
///
/// <b>Что из этого следует для телефона.</b> Отдать файл как есть — честно, но на объекте чаще всего
/// бесполезно: <c>.xlsx</c> браузер телефона не рисует, его подхватывает Excel/Таблицы, если они
/// стоят и если человек согласится ждать загрузку и переключение приложений одной рукой. Поэтому
/// развилка по типу файла, и обе ветки доступны всегда:
/// <list type="bullet">
/// <item><description><b>книга Excel</b> (xlsx/xlsm) — читается здесь и показывается страницей:
///       ничего скачивать и ничем открывать не надо, работает поиск по странице в браузере, и это
///       подавляющее большинство карт ВВ;</description></item>
/// <item><description><b>всё остальное</b> (pdf, docx, картинка) — страницей показывается только
///       карточка файла, а сам он отдаётся по ссылке тем же сервером. PDF телефон рисует сам,
///       остальное отдаст своему приложению — хуже, но это ровно то же самое, что человек получает
///       на компьютере, и молчать вместо этого было бы хуже.</description></item>
/// </list>
///
/// <b>Старый .xls (BIFF) сюда не входит.</b> ClosedXML читает только OpenXML; такой файл попадает во
/// вторую ветку и отдаётся как файл. Отдельной библиотеки ради него не заводим.</summary>
public static class IoMapDocs
{
    /// <summary>Сколько листов книги показывать. Карта ВВ — это один-два листа («Входы», «Выходы»);
    /// книга с десятком листов почти наверняка не карта, и грузить телефон её целиком незачем.</summary>
    public const int MaxSheets = 4;

    /// <summary>Сколько строк листа показывать. Карта на 300 строк — это уже весь шкаф целиком;
    /// дальше страница становится длиннее, чем её кто-либо пролистает, а памяти ест столько же.</summary>
    public const int MaxRows = 300;

    /// <summary>Сколько столбцов показывать. У карты их обычно 4–6 (клемма, сигнал, тип, описание);
    /// всё, что правее, — пояснения на полях, ради которых не стоит растягивать таблицу.</summary>
    public const int MaxColumns = 10;

    /// <summary>Обрезка длинной ячейки. Длинный текст в ячейке карты — это комментарий, а не
    /// значение; целиком он есть в исходном файле, ссылка на который на странице рядом.</summary>
    public const int MaxCellChars = 200;

    /// <summary>Файл карты ВВ этой версии — ТОТ ЖЕ, который открывает карточка на компьютере.
    ///
    /// Правило выбора («самый свежий из пути у версии и общей папки документа») живёт в
    /// <see cref="DocFileResolver"/>, поправка на запись-ссылку и на перестроенную раскладку — в
    /// <see cref="VersionDocFolders.ReadFolderFor"/>. Здесь не решается ничего: иначе телефон и
    /// компьютер однажды показали бы разные карты одной версии, и спор у шкафа было бы нечем
    /// разрешить.</summary>
    public static string? FileFor(Database db, int fwVersionId)
    {
        var row = db.GetFwVersionById(fwVersionId);
        if (row is null) return null;

        var names = db.GetFwVersionNames(fwVersionId);
        var versionDir = FirmwareDiskPresence.ResolveVersionDir(row.DiskPath, row.VersionRaw) ?? row.DiskPath;
        var folder = VersionDocFolders.ReadFolderFor(versionDir, names?.GroupName, names?.SubtypeName,
            names?.ControllerName, HierarchyFolders.IoMap);
        return DocFileResolver.Resolve(row.IoMapPath, folder);
    }

    /// <summary>Файл карты, разобранный настолько, насколько получилось. null — карты у версии нет
    /// вовсе (ни файла у версии, ни в общей папке).</summary>
    public static IoMapDoc? For(Database db, int fwVersionId) =>
        FileFor(db, fwVersionId) is { } path ? Read(path) : null;

    /// <summary>Описание файла и, для книги Excel, его содержимое сеткой. Недоступный файл (шару
    /// отключили между поиском и чтением) — null: показывать карточку файла, которого уже нет, и
    /// давать на него ссылку значит обещать то, чего не будет.</summary>
    public static IoMapDoc? Read(string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return null;
        }
        catch (Exception) { return null; }

        var doc = new IoMapDoc(path, info.Name, info.Length, SafeWriteTime(info), Array.Empty<IoMapSheet>());
        if (!IsWorkbook(path)) return doc;

        try
        {
            // FileShare.ReadWrite — карта лежит на общем диске, и её вполне может сейчас держать
            // открытой кто-то в Excel. Отказать телефону из-за этого нельзя: смотрим то, что на
            // диске, не мешая тому, кто правит.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var wb = new XLWorkbook(stream);
            var sheets = wb.Worksheets
                .Where(ws => ws.Visibility == XLWorksheetVisibility.Visible)
                .Take(MaxSheets)
                .Select(ReadSheet)
                .Where(s => s.Rows.Count > 0)
                .ToList();
            return doc with { Sheets = sheets };
        }
        catch (Exception)
        {
            // Битая или нечитаемая книга — не повод ронять страницу: остаётся карточка файла со
            // ссылкой, то есть ровно то же, что и у pdf. Человек у шкафа получит файл, а не ошибку.
            return doc;
        }
    }

    private static IoMapSheet ReadSheet(IXLWorksheet ws)
    {
        var used = ws.RangeUsed();
        if (used is null) return new IoMapSheet(ws.Name, Array.Empty<string[]>(), false);

        var firstRow = used.RangeAddress.FirstAddress.RowNumber;
        var lastRow = used.RangeAddress.LastAddress.RowNumber;
        var firstCol = used.RangeAddress.FirstAddress.ColumnNumber;
        var lastCol = Math.Min(used.RangeAddress.LastAddress.ColumnNumber, firstCol + MaxColumns - 1);
        var colsTruncated = used.RangeAddress.LastAddress.ColumnNumber > lastCol;

        var rows = new List<string[]>();
        var rowsTruncated = false;
        for (var r = firstRow; r <= lastRow; r++)
        {
            if (rows.Count >= MaxRows) { rowsTruncated = true; break; }

            var cells = new string[lastCol - firstCol + 1];
            var empty = true;
            for (var c = firstCol; c <= lastCol; c++)
            {
                // GetFormattedString, а не Value: в карте ВВ важно то, что ЧЕЛОВЕК видит в Excel
                // (формат, округление, дата), а не внутреннее значение ячейки.
                var text = Clip(ws.Cell(r, c).GetFormattedString());
                cells[c - firstCol] = text;
                if (text.Length > 0) empty = false;
            }
            // Пустые строки в карте — разделители между блоками в Excel; на телефоне они съели бы
            // пол-экрана, ничего не сказав.
            if (!empty) rows.Add(cells);
        }

        return new IoMapSheet(ws.Name, rows, rowsTruncated || colsTruncated);
    }

    private static string Clip(string? s)
    {
        var text = (s ?? "").Trim();
        return text.Length <= MaxCellChars ? text : text[..MaxCellChars] + "…";
    }

    /// <summary>Книга OpenXML, которую имеет смысл разбирать. Расширение, а не содержимое: имя файла
    /// здесь единственный источник правды и на диске, и в ссылке.</summary>
    public static bool IsWorkbook(string path)
    {
        var ext = Path.GetExtension(path);
        return string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".xlsm", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime SafeWriteTime(FileInfo info)
    {
        try { return info.LastWriteTime; }
        catch (Exception) { return DateTime.MinValue; }
    }
}

/// <summary>Карта ВВ одной версии в том виде, в каком её показывают телефону.</summary>
/// <param name="FullPath">Путь на диске — нужен только тому, кто отдаёт файл по ссылке; в разметку
/// он не попадает никогда: полный путь к конторской шаре телефону знать незачем.</param>
/// <param name="Sheets">Пусто — файл не книга (или книга не прочиталась): страница показывает
/// карточку файла и ссылку на него.</param>
public sealed record IoMapDoc(
    string FullPath, string FileName, long SizeBytes, DateTime Modified, IReadOnlyList<IoMapSheet> Sheets)
{
    public bool HasGrid => Sheets.Count > 0;
}

/// <param name="Truncated">Лист показан не целиком (упёрлись в <see cref="IoMapDocs.MaxRows"/> или
/// <see cref="IoMapDocs.MaxColumns"/>) — об этом обязана сказать сама страница, иначе человек решит,
/// что входов больше нет, и будет искать несуществующую ошибку в шкафу.</param>
public sealed record IoMapSheet(string Name, IReadOnlyList<string[]> Rows, bool Truncated);
