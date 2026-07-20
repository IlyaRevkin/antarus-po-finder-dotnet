using System;
using System.Collections.Generic;
using ClosedXML.Excel;

namespace AntarusPoFinder.Core.Services;

/// <summary>Generates the standard firmware-version numbering reference spreadsheet (5 equipment
/// types, their subtypes, and controller hw codes) — layout copied cell-for-cell from the
/// "Antarus_версии_нумерация.xlsx" reference kept on the admin's desktop. This is a fixed catalogue,
/// independent of whatever equipment_groups/subtypes/controllers rows currently exist in the live
/// DB (those can drift via the Иерархия admin UI) — the button always exports the standard.</summary>
public static class FwVersionTableExportService
{
    public sealed record StandardSubtype(int Prefix, string Code);
    public sealed record StandardGroup(int Prefix, string Name, StandardSubtype[] Subtypes);
    public sealed record StandardController(string Model, string Modification, int Hw, string Description);

    public static readonly StandardGroup[] Groups =
    [
        new(1, "ПЖ", [new(0, "2.0"), new(1, "FD"), new(2, "КПЧ"), new(3, "ХП"), new(4, "ПИ"), new(5, "ПКР"), new(6, "ПКР ПИ")]),
        new(2, "НГР", [new(0, "2.0"), new(1, "КНС"), new(2, "УПД"), new(3, "КР")]),
        new(3, "ТГР", [new(0, "-")]),
        new(4, "ВЗУ", [new(0, "-"), new(1, "ПИ")]),
        new(5, "ШУЗ", [new(0, "-")]),
    ];

    public static readonly StandardController[] Controllers =
    [
        new("SMH4", "SMH4", 4, "HMI 4.3\", 5DI / 3DO / 2AI / 2AO, RS-485, Ethernet"),
        new("SMH5", "SMH5", 5, "HMI 5\", 8DI / 8DO / 4AI / 2AO, RS-485, Ethernet"),
        new("KINCO", "KINCO", 20, "ПЛК KINCO K-серия, модульный, высокоскоростной I/O"),
        new("PIXEL2", "PIXEL2", 40, "универсальный"),
        new("PIXEL2", "PIXEL2-1020", 41, "8DI / 6DO / 8AI / 2AO"),
        new("PIXEL2", "PIXEL2-1021", 42, "8DI / 5DO / 8AI / 4AO"),
        new("PIXEL2", "PIXEL2-1320", 43, "8DI / 6DO / 6AI / 2AO"),
        new("PIXEL2", "PIXEL2-1321", 44, "8DI / 5DO / 6AI / 4AO"),
        new("PIXEL2", "PIXEL2-3022", 45, "16DI / 12DO"),
        new("PIXEL2", "PIXEL2-3322", 46, "16DI / 8DO / 4AI"),
        new("PIXEL2", "PIXEL2-3422", 47, "16DI / 8DO / 4AO"),
        new("PIXEL", "PIXEL-2511", 48, "первое поколение Pixel, требует уточнения характеристик"),
        new("PIXEL", "PIXEL-2512", 49, "первое поколение Pixel, требует уточнения характеристик"),
        new("PIXEL", "PIXEL-2514", 50, "первое поколение Pixel, требует уточнения характеристик"),
        new("PIXEL", "PIXEL-2515", 51, "первое поколение Pixel, требует уточнения характеристик"),
    ];

    private static string Hw(int hw) => hw.ToString("D3");

    public static void Generate(string filePath)
    {
        using var wb = new XLWorkbook();
        BuildOverviewSheet(wb.Worksheets.Add("Обзор"));
        BuildTableSheet(wb.Worksheets.Add("Таблица"));
        wb.SaveAs(filePath);
    }

    private static void BuildOverviewSheet(IXLWorksheet ws)
    {
        ws.Cell(1, 1).Value = "Схема нумерации версий прошивок — Antarus ПО Finder";
        ws.Cell(1, 1).Style.Font.Bold = true;

        ws.Cell(3, 1).Value = "Формат номера версии:";
        ws.Cell(4, 1).Value = "Цифра1.Цифра2.hw.sw[.ДАТА_ВРЕМЯ]";
        ws.Cell(5, 1).Value = "Цифра1 = группа оборудования (лист «Обзор», таблица ниже)";
        ws.Cell(6, 1).Value = "Цифра2 = подтип внутри группы — свой для каждой группы (см. отдельный лист группы)";
        ws.Cell(7, 1).Value = "hw = версия контроллера/модификации (таблица «Контроллеры» ниже, общая для всех групп)";
        ws.Cell(8, 1).Value = "sw = версия прошивки — растёт автоматически при каждой новой загрузке";
        ws.Cell(9, 1).Value = "ОПЦ = номер заявки — опционально (чекбокс «ОПЦ Режим» в Загрузке)";
        ws.Cell(10, 1).Value = "ДАТА_ВРЕМЯ = когда собрана прошивка — опционально (чекбокс «Добавлять дату/время» в Загрузке)";

        int row = 12;
        ws.Cell(row, 1).Value = "Цифра 1 — Группа оборудования";
        ws.Cell(row, 1).Style.Font.Bold = true;
        row++;
        ws.Cell(row, 1).Value = "Группа";
        ws.Cell(row, 2).Value = "Цифра 1 (prefix)";
        ws.Range(row, 1, row, 2).Style.Font.Bold = true;
        row++;
        foreach (var g in Groups)
        {
            ws.Cell(row, 1).Value = g.Name;
            ws.Cell(row, 2).Value = g.Prefix;
            row++;
        }

        row++;
        ws.Cell(row, 1).Value = "Контроллеры / модификации (hw — общий список для всех групп)";
        ws.Cell(row, 1).Style.Font.Bold = true;
        row++;
        ws.Cell(row, 1).Value = "Модель";
        ws.Cell(row, 2).Value = "Модификация";
        ws.Cell(row, 3).Value = "hw";
        ws.Cell(row, 4).Value = "Описание";
        ws.Range(row, 1, row, 4).Style.Font.Bold = true;
        row++;
        foreach (var c in Controllers)
        {
            ws.Cell(row, 1).Value = c.Model;
            ws.Cell(row, 2).Value = c.Modification;
            ws.Cell(row, 3).Value = Hw(c.Hw);
            ws.Cell(row, 4).Value = c.Description;
            row++;
        }

        ws.Columns(1, 4).AdjustToContents();
    }

    private static void BuildTableSheet(IXLWorksheet ws)
    {
        const int typeRow = 1;
        const int prefixRow = 2;
        const int subtypeRow = 3;
        const int indexRow = 4;
        const int firstDataRow = 5;
        const int modelCol = 1;
        const int hwCol = 2;
        const int firstDataCol = 3;

        ws.Cell(prefixRow, modelCol).Value = "1. Тип шкафа →";
        ws.Range(prefixRow, modelCol, prefixRow, hwCol).Merge();
        ws.Cell(subtypeRow, hwCol).Value = "2. Подтип шкафа →";
        ws.Cell(indexRow, hwCol).Value = "3. Контроллеры↓";

        var subtypeColumns = new List<(StandardGroup Group, StandardSubtype Subtype, int Col)>();
        int col = firstDataCol;
        foreach (var g in Groups)
        {
            int groupFirstCol = col;
            foreach (var s in g.Subtypes)
            {
                ws.Cell(subtypeRow, col).Value = s.Code;
                ws.Cell(indexRow, col).Value = s.Prefix;
                subtypeColumns.Add((g, s, col));
                col++;
            }
            int groupLastCol = col - 1;

            ws.Cell(typeRow, groupFirstCol).Value = g.Name;
            ws.Cell(prefixRow, groupFirstCol).Value = g.Prefix;
            if (groupLastCol > groupFirstCol)
            {
                ws.Range(typeRow, groupFirstCol, typeRow, groupLastCol).Merge();
                ws.Range(prefixRow, groupFirstCol, prefixRow, groupLastCol).Merge();
            }
        }

        int row = firstDataRow;
        foreach (var c in Controllers)
        {
            ws.Cell(row, modelCol).Value = c.Modification;
            ws.Cell(row, hwCol).Value = Hw(c.Hw);
            foreach (var (g, s, dataCol) in subtypeColumns)
                ws.Cell(row, dataCol).Value = $"{g.Prefix}.{s.Prefix}.{Hw(c.Hw)}";
            row++;
        }

        ws.Range(typeRow, modelCol, typeRow, col - 1).Style.Font.Bold = true;
        ws.Range(prefixRow, modelCol, prefixRow, col - 1).Style.Font.Bold = true;
        ws.Range(subtypeRow, modelCol, subtypeRow, col - 1).Style.Font.Bold = true;
        ws.Range(indexRow, modelCol, indexRow, col - 1).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(indexRow);
        ws.SheetView.FreezeColumns(hwCol);
        ws.Columns(1, col - 1).AdjustToContents();
    }
}
