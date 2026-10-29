using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Выложить таблицу параметров файлом — для переноса, а не для хранения.
///
/// <b>Зачем.</b> В бете параметры живут таблицей, а не файлом: «мы отказываемся от файлов, в бете
/// только таблицы, файлы создаются временно для переноса в папку». То есть файл здесь — не способ
/// хранить настройки, а способ донести их туда, куда программа сама не дотянется: отдать коллеге,
/// положить на флешку, вложить в письмо. Поэтому он собирается по требованию и ложится, куда
/// сказали, а в базе ничего не меняется.
///
/// <b>Почему CSV.</b> Формат выбран за то, что его открывает всё: Excel, Блокнот, конфигуратор
/// вендора через импорт, и человек на объекте, у которого вообще ничего не установлено. Двоичная
/// выгрузка конфигуратора (.par) для этого не годится — её не прочитать глазами, а именно глазами
/// таблицу и читают. Разделитель «точка с запятой», а не запятая: в значениях и описаниях запятая
/// встречается постоянно (десятичная, перечисление), и русский Excel ждёт именно её.
///
/// Кодировка — UTF-8 С BOM. Без BOM тот же русский Excel открывает кириллицу кракозябрами, и это
/// первое, на что жалуются; лишние три байта того не стоят.</summary>
public static class ParamTableExport
{
    /// <summary>Разделитель столбцов. Вынесен константой, чтобы в тесте не повторять догадку.</summary>
    public const char Separator = ';';

    public const string Extension = ".csv";

    /// <summary>Имя файла для таблицы: её название, очищенное от того, что Windows в именах не
    /// разрешает. Пустое название — не повод отказать: человек всё равно поймёт, что выгрузил.</summary>
    public static string FileNameFor(ParamTable table)
    {
        var name = (table?.Name ?? "").Trim();
        if (name.Length == 0) name = "Таблица параметров";
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, ' ');
        name = name.Trim();
        return (name.Length == 0 ? "Таблица параметров" : name) + Extension;
    }

    /// <summary>Собрать содержимое файла. Отдельно от записи на диск — чтобы это можно было
    /// проверить тестом, не трогая файловую систему.</summary>
    public static string Build(ParamTable table, IEnumerable<ParamTableRow> rows)
    {
        var sb = new StringBuilder();

        // Шапка с названием документа: лист, вырванный из контекста, без неё превращается в
        // «какие-то числа», а переносят таблицу именно поодиночке.
        if (!string.IsNullOrWhiteSpace(table?.Name)) sb.Append("# ").Append(table!.Name).Append('\n');
        if (!string.IsNullOrWhiteSpace(table?.Manufacturer)) sb.Append("# ").Append(table!.Manufacturer).Append('\n');
        sb.Append("# Выгружено ").Append(DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)).Append('\n');

        Line(sb, "Группа", "Код", "Параметр", "Значение", "Заводское", "Ед.", "Назначение", "Применимость", "Условие", "Описание");

        foreach (var row in rows ?? Enumerable.Empty<ParamTableRow>())
        {
            // Пояснение — не параметр: у него нет кода и значения, и выгружать его строкой таблицы
            // значит сбить тому, кто читает, ритм столбцов. Отдаём как есть, в столбце описания.
            if (string.Equals(row.Kind, ParamRowKind.Note, StringComparison.Ordinal))
            {
                Line(sb, row.GroupName, "", "", "", "", "", "", row.Applicability, row.AppliesWhen, row.Description);
                continue;
            }

            Line(sb, row.GroupName, row.Code, row.Title, ValueOf(row), row.Factory, row.Unit,
                ParamPurposes.Label(row.Purpose), row.Applicability, row.AppliesWhen, row.Description);
        }

        return sb.ToString();
    }

    /// <summary>Записать файл в выбранную папку и вернуть полный путь.</summary>
    public static string Write(string folder, ParamTable table, IEnumerable<ParamTableRow> rows)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileNameFor(table));
        // UTF-8 именно с BOM — см. пояснение к классу.
        File.WriteAllText(path, Build(table, rows), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    /// <summary>Значение так, как его читают: «снимается по месту» и прочие состояния — это ответ
    /// на вопрос «что сюда писать», и потерять его при выгрузке значит отдать таблицу, по которой
    /// нельзя работать.</summary>
    private static string ValueOf(ParamTableRow row) =>
        string.Equals(row.ValueState, ParamValueState.Set, StringComparison.Ordinal) ? row.Value
            : string.Equals(row.ValueState, ParamValueState.OnSite, StringComparison.Ordinal) ? "снимается по месту"
            : string.Equals(row.ValueState, ParamValueState.Ask, StringComparison.Ordinal) ? "уточнить"
            : row.Value;

    private static void Line(StringBuilder sb, params string[] cells)
    {
        sb.Append(string.Join(Separator, cells.Select(Escape))).Append('\n');
    }

    /// <summary>Экранирование по правилам CSV: кавычки удваиваются, а ячейка берётся в кавычки, если
    /// внутри есть разделитель, кавычка или перенос строки. Без этого описание с точкой с запятой
    /// разъезжается по соседним столбцам — и замечают это не сразу, а когда по таблице уже работают.</summary>
    private static string Escape(string? cell)
    {
        var value = cell ?? "";
        var needsQuotes = value.IndexOf(Separator) >= 0 || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        if (!needsQuotes) return value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}
