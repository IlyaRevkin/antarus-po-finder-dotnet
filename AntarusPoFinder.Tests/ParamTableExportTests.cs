using System.Collections.Generic;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Выгрузка таблицы параметров в файл для переноса.
///
/// В бете параметры живут таблицей, а файл остался только способом донести её туда, куда программа
/// не дотянется. Значит файл обязан быть читаемым чем угодно и не врать: разъехавшийся по столбцам
/// текст на объекте означает настройку не того параметра.</summary>
public class ParamTableExportTests
{
    private static ParamTable Table() => new() { Id = 1, Name = "Задание Modbus", Manufacturer = "Innovert" };

    private static ParamTableRow Param(string code, string title, string value) => new()
    {
        Kind = ParamRowKind.Param,
        GroupName = "Основные настройки",
        Code = code,
        Title = title,
        Value = value,
        ValueState = ParamValueState.Set,
    };

    [Fact]
    public void Точка_с_запятой_в_описании_не_разваливает_строку()
    {
        var row = Param("P0-02", "Канал команд", "2");
        row.Description = "Сначала P0-03; затем P0-02";

        var csv = ParamTableExport.Build(Table(), new List<ParamTableRow> { row });

        // Ячейка с разделителем обязана уехать в кавычки — иначе описание перетекает в соседний
        // столбец, и таблица читается неверно ровно там, где по ней работают.
        Assert.Contains("\"Сначала P0-03; затем P0-02\"", csv);
    }

    [Fact]
    public void Кавычки_внутри_ячейки_удваиваются()
    {
        var row = Param("P0-10", "Режим", "1");
        row.Description = "Режим \"Авто\"";
        var csv = ParamTableExport.Build(Table(), new List<ParamTableRow> { row });
        Assert.Contains("\"Режим \"\"Авто\"\"\"", csv);
    }

    [Fact]
    public void Состояние_значения_выгружается_словами_а_не_кодом()
    {
        // «снимается по месту» — это ответ на вопрос «что сюда писать». Уехав кодом onsite, он
        // превратился бы в мусор для всех, кроме самой программы.
        var row = Param("P1-01", "Номинальный ток", "");
        row.ValueState = ParamValueState.OnSite;

        var csv = ParamTableExport.Build(Table(), new List<ParamTableRow> { row });
        Assert.Contains("снимается по месту", csv);
        Assert.DoesNotContain("onsite", csv);
    }

    [Fact]
    public void Название_документа_попадает_в_файл_и_в_его_имя()
    {
        var csv = ParamTableExport.Build(Table(), new List<ParamTableRow>());
        Assert.Contains("Задание Modbus", csv);
        Assert.Equal("Задание Modbus.csv", ParamTableExport.FileNameFor(Table()));
    }

    [Fact]
    public void Запрещённые_в_имени_знаки_не_ломают_выгрузку()
    {
        var table = new ParamTable { Name = "Насос 1/2: задание" };
        var name = ParamTableExport.FileNameFor(table);
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain(":", name);
        Assert.EndsWith(".csv", name);
    }

    [Fact]
    public void Пояснение_не_притворяется_параметром()
    {
        // В исходниках попадаются строки вроде «В ПЛК выставить 55 Гц» — это указание наладчику.
        // В выгрузке у него не должно быть кода и значения, иначе его выставят как параметр.
        var note = new ParamTableRow
        {
            Kind = ParamRowKind.Note,
            GroupName = "Основные настройки",
            Description = "В ПЛК выставить частоту 55 Гц",
        };

        var csv = ParamTableExport.Build(Table(), new List<ParamTableRow> { note });
        Assert.Contains("В ПЛК выставить частоту 55 Гц", csv);
        var line = csv.Split('\n')[^2];
        Assert.StartsWith("Основные настройки;;;;;", line);
    }
}
