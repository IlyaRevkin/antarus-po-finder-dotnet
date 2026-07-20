using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using ClosedXML.Excel;

namespace AntarusPoFinder.Core.Services;

/// <summary>Generates the firmware-version numbering reference spreadsheet (equipment types, their
/// subtypes, and controller hw codes) straight off the live database — whatever equipment_groups/
/// equipment_subtypes/controller_models/controller_modifications rows exist in the DB at the moment
/// the button is clicked (see SettingsView.ExportVersionTable_Click), including anything an admin
/// added/renamed/removed via Настройки → Иерархия. Layout (two sheets, merged header cells) is
/// unchanged from the original hand-authored "Antarus_версии_нумерация.xlsx" reference — only the
/// data source changed, from a hardcoded catalogue to live DB reads.</summary>
public static class FwVersionTableExportService
{
    private sealed record TableGroup(EquipmentGroup Group, List<EquipmentSubType> Subtypes);
    private sealed record TableRow(ControllerModel Controller, ControllerModification Mod);

    private static string Hw(int hw) => hw.ToString("D3");

    public static void Generate(string filePath, Database db)
    {
        var groups = db.GetAllEquipmentGroups()
            .Select(g => new TableGroup(g, db.GetSubtypesForGroup(g.Id!.Value)))
            .Where(tg => tg.Subtypes.Count > 0)
            .ToList();

        var rows = db.GetAllControllerModels()
            .SelectMany(c => db.GetModificationsForController(c.Id!.Value).Select(m => new TableRow(c, m)))
            .ToList();

        using var wb = new XLWorkbook();
        BuildOverviewSheet(wb.Worksheets.Add("Обзор"), groups, rows);
        BuildTableSheet(wb.Worksheets.Add("Таблица"), groups, rows);
        wb.SaveAs(filePath);
    }

    private static void BuildOverviewSheet(IXLWorksheet ws, List<TableGroup> groups, List<TableRow> rows)
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
        foreach (var g in groups)
        {
            ws.Cell(row, 1).Value = g.Group.Name;
            ws.Cell(row, 2).Value = g.Group.Prefix;
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
        foreach (var r in rows)
        {
            ws.Cell(row, 1).Value = r.Controller.Name;
            ws.Cell(row, 2).Value = r.Mod.DisplayName;
            ws.Cell(row, 3).Value = Hw(r.Mod.HwVersion);
            ws.Cell(row, 4).Value = r.Mod.Description;
            row++;
        }

        ws.Columns(1, 4).AdjustToContents();
    }

    private static void BuildTableSheet(IXLWorksheet ws, List<TableGroup> groups, List<TableRow> rows)
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

        var subtypeColumns = new List<(EquipmentGroup Group, EquipmentSubType Subtype, int Col)>();
        int col = firstDataCol;
        foreach (var g in groups)
        {
            int groupFirstCol = col;
            foreach (var s in g.Subtypes)
            {
                ws.Cell(subtypeRow, col).Value = s.Name;
                ws.Cell(indexRow, col).Value = s.Prefix;
                subtypeColumns.Add((g.Group, s, col));
                col++;
            }
            int groupLastCol = col - 1;

            ws.Cell(typeRow, groupFirstCol).Value = g.Group.Name;
            ws.Cell(prefixRow, groupFirstCol).Value = g.Group.Prefix;
            if (groupLastCol > groupFirstCol)
            {
                ws.Range(typeRow, groupFirstCol, typeRow, groupLastCol).Merge();
                ws.Range(prefixRow, groupFirstCol, prefixRow, groupLastCol).Merge();
            }
        }

        int row = firstDataRow;
        foreach (var r in rows)
        {
            ws.Cell(row, modelCol).Value = r.Mod.DisplayName;
            ws.Cell(row, hwCol).Value = Hw(r.Mod.HwVersion);
            foreach (var (g, s, dataCol) in subtypeColumns)
                ws.Cell(row, dataCol).Value = $"{g.Prefix}.{s.Prefix}.{Hw(r.Mod.HwVersion)}";
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
