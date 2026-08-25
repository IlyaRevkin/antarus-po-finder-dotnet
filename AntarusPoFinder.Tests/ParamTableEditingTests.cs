using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Импорт с предпросмотром, правка и новая ревизия таблицы параметров
/// (ParamTableEditing) — то, что происходит между разбором текста и записью в базу.</summary>
public class ParamTableEditingTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly Database _db;

    public ParamTableEditingTests() => _db = new Database(_dbFile.Path);

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
    }

    private const string Sample = """
        ESQ-230 - КПЧ(Задание Modbus) Новая серия ПЧ 2025г

        =================[Настройка ШУ]
        P0-02(2) - Выбор канала команды запуска - Протокол связи
        P0-10(?) - Максимальная частота

        ================[Двигатель]
        P1-01 - Мощность
        """;

    private int NewTable() => _db.AddParamTable(new ParamTable
    {
        DiskPath = @"D:\ПО\ESQ", Filename = "ESQ-230.par", Name = "Задание Modbus", Manufacturer = "ESQ",
    });

    private static ParamTableRow P(string code, string value, string group = ParamGroupCatalog.Main) => new()
    {
        Kind = ParamRowKind.Param, Code = code, Title = "Параметр " + code, Value = value,
        ValueState = ParamValueState.Set, GroupName = group,
    };

    // ── Предпросмотр импорта ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Preview_ShowsBothTheTableAndTheTextItCameFrom()
    {
        var bytes = TextFileEncoding.Cp1251.GetBytes(Sample);

        var preview = ParamTableEditing.Preview(bytes, "ESQ-230 2025 - КПЧ(Задание Modbus).txt");

        Assert.Equal("Windows-1251", preview.EncodingName);
        Assert.Equal(3, preview.Rows.Count);
        // Исходный текст отдаётся целиком: когда разбор промахнулся, человек смотрит на строки
        // рядом с таблицей, а не гадает.
        Assert.Contains("P0-02(2)", preview.Text);
    }

    [Fact]
    public void Preview_SaysOutLoudWhenNothingWasFound()
    {
        // Выбрали соседний Readme вместо файла параметров. Разбор прощает всё и кладёт непонятные
        // строки пояснениями — то есть таблица будет НЕ пустой, и без прямой оговорки человек
        // спокойно сохранил бы документ из трёх абзацев текста.
        var bytes = TextFileEncoding.Cp1251.GetBytes("Прошивка собрана 12.05.2025.\nПеред заливкой сделать резервную копию.");

        var preview = ParamTableEditing.Preview(bytes, "Readme.txt");

        Assert.DoesNotContain(preview.Rows, r => r.Kind == ParamRowKind.Param);
        Assert.Contains(preview.Warnings, w => w.Contains("ни одного параметра"));
    }

    [Fact]
    public void Preview_KeepsTheParsersOwnWarnings()
    {
        var preview = ParamTableEditing.Preview(TextFileEncoding.Cp1251.GetBytes(Sample), "f.txt");

        Assert.Contains(preview.Warnings, w => w.Contains("P0-10"));
    }

    [Theory]
    // Назначение документа записано в скобках — всё остальное в имени повторяет иерархию.
    [InlineData("ESQ-230 2025 - КПЧ(Задание Modbus).txt", "Задание Modbus")]
    [InlineData("Веспер - КПЧ(Пуск по месту).txt", "Пуск по месту")]
    // Скобок нет — берём имя файла без расширения, угадывать не по чему.
    [InlineData("Параметры ПЧ.txt", "Параметры ПЧ")]
    // «(2)» — след копирования файла рядом, а не название документа.
    [InlineData("Параметры (2).txt", "Параметры (2)")]
    public void SuggestName_TakesTheBracketedPurpose(string fileName, string expected)
    {
        Assert.Equal(expected, ParamTableEditing.SuggestName(fileName, ""));
    }

    [Fact]
    public void SuggestName_FallsBackToTheTitleInsideTheFile()
    {
        Assert.Equal("Задание Modbus",
            ParamTableEditing.SuggestName("copy.txt", "ESQ-230 - КПЧ(Задание Modbus) Новая серия"));
    }

    // ── Приведение строк в порядок ───────────────────────────────────────────────────────────

    [Fact]
    public void Tidy_TrimsRenumbersAndDropsEmptyRows()
    {
        var rows = new List<ParamTableRow>
        {
            new() { Code = "  P0-02 ", Title = " Выбор канала  ", Value = " 2 ", GroupName = " Двигатель " },
            new() { Code = "", Title = "" },
            new() { Code = "P1-01", Title = "Мощность" },
        };

        var tidy = ParamTableEditing.Tidy(rows, _db.GetParamGroups());

        // «Добавил строку, передумал» не должно доехать до наладчика пустой строкой без кода и
        // названия, а до разбора изменений — как «добавлена строка ""».
        Assert.Equal(2, tidy.Count);
        Assert.Equal("P0-02", tidy[0].Code);
        Assert.Equal("Выбор канала", tidy[0].Title);
        Assert.Equal(new[] { 0, 1 }, tidy.Select(r => r.SortOrder));
    }

    [Fact]
    public void Tidy_NormalizesGroupSpellingToTheCatalog()
    {
        var rows = new List<ParamTableRow> { new() { Code = "P1-01", Title = "Мощность", GroupName = "двигатель" } };

        var tidy = ParamTableEditing.Tidy(rows, _db.GetParamGroups());

        // ⚠️ Свести написание обязан .NET: для SQLite «Двигатель» и «двигатель» — разные строки, и
        // в таблице завелись бы две одинаковые с виду группы с разным местом в порядке показа.
        Assert.Equal(ParamGroupCatalog.Motor, tidy[0].GroupName);
    }

    [Fact]
    public void Tidy_RowWithoutCodeBecomesANote()
    {
        var rows = new List<ParamTableRow>
        {
            new() { Kind = ParamRowKind.Param, Code = "", Title = "В ПЛК выставить частоту 55Гц" },
            new() { Kind = ParamRowKind.Note, Code = "P1-01", Title = "Мощность" },
        };

        var tidy = ParamTableEditing.Tidy(rows, _db.GetParamGroups());

        // Вид строки не спрашивают отдельной колонкой — он виден по тому, есть ли код.
        Assert.Equal(ParamRowKind.Note, tidy[0].Kind);
        Assert.Equal(ParamRowKind.Param, tidy[1].Kind);
    }

    [Fact]
    public void Ordered_ShowsGroupsInWorkOrder_NotInSourceOrder()
    {
        var rows = new List<ParamTableRow>
        {
            P("P9-01", "1", ParamGroupCatalog.FactoryReset),
            P("P1-01", "15", ParamGroupCatalog.Motor),
            P("P0-02", "2", ParamGroupCatalog.Main),
        };

        var shown = ParamTableEditing.Ordered(rows, _db.GetParamGroupOrder());

        Assert.Equal(new[] { "P0-02", "P1-01", "P9-01" }, shown.Select(r => r.Code));
    }

    [Fact]
    public void Ordered_KeepsSourceOrderInsideAGroup()
    {
        var rows = new List<ParamTableRow>
        {
            new() { Code = "P0-10", SortOrder = 5, GroupName = ParamGroupCatalog.Main },
            new() { Code = "P0-02", SortOrder = 1, GroupName = ParamGroupCatalog.Main },
        };

        Assert.Equal(new[] { "P0-02", "P0-10" },
            ParamTableEditing.Ordered(rows, _db.GetParamGroupOrder()).Select(r => r.Code));
    }

    // ── Отбор по применимости (это и есть работа наладчика) ──────────────────────────────────

    [Fact]
    public void Applicabilities_ListsEveryDeviceMentioned()
    {
        var rows = new List<ParamTableRow>
        {
            new() { Code = "P4-00", Applicability = "Только для ПЧ №1" },
            new() { Code = "P4-39", Applicability = "Для ПЧ №1 и ПЧ №2" },
            new() { Code = "P5-00" },
            new() { Code = "P5-01", Applicability = "только для пч №1" },
        };

        var choices = ParamTableEditing.Applicabilities(rows);

        Assert.Equal(ParamTableEditing.AnyApplicability, choices[0]);
        Assert.Equal(3, choices.Count);
    }

    [Fact]
    public void Filter_KeepsRowsThatFitEveryone()
    {
        var rows = new List<ParamTableRow>
        {
            new() { Code = "P0-02" },
            new() { Code = "P4-00", Applicability = "Только для ПЧ №1" },
            new() { Code = "P4-01", Applicability = "Только для ПЧ №2" },
        };

        var shown = ParamTableEditing.FilterByApplicability(rows, "Только для ПЧ №1");

        // Строка без пометки годится всем, и её большинство. Спрятав её, отбор показал бы наладчику
        // три строки из ста, и частотник он выставил бы по ним одним.
        Assert.Equal(new[] { "P0-02", "P4-00" }, shown.Select(r => r.Code));
        Assert.Equal(3, ParamTableEditing.FilterByApplicability(rows, ParamTableEditing.AnyApplicability).Count);
    }

    // ── Кто вправе править ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("administrator", true)]
    [InlineData("programmer", true)]
    [InlineData("naladchik", false)]
    [InlineData(null, false)]
    public void OnlyProgrammerAndAdministratorEdit(string? role, bool canEdit)
    {
        Assert.Equal(canEdit, ParamTableEditing.CanEdit(role));
    }

    // ── Сохранение ревизии ───────────────────────────────────────────────────────────────────

    [Fact]
    public void FirstRevision_SaysHowManyRows_NotWhatChanged()
    {
        var id = NewTable();

        var (revisionId, _) = ParamTableEditing.SaveRevision(_db, id, new[] { P("P0-02", "2") }, "первая заливка", "Ilia");

        // Сравнивать первую ревизию не с чем, и «Добавлено (1): P0-02» было бы честно, но бесполезно.
        Assert.Equal("Первая редакция: строк 1.", _db.GetParamTableRevision(revisionId)!.Summary);
    }

    [Fact]
    public void NextRevision_GetsItsSummaryComputed_AndKeepsTheHumanReason()
    {
        var id = NewTable();
        ParamTableEditing.SaveRevision(_db, id, new[] { P("P0-10", "50") }, "первая заливка", "Ilia");

        var (revisionId, diff) = ParamTableEditing.SaveRevision(_db, id, new[] { P("P0-10", "55") },
            "объект попросил 55 Гц", "Ilia");

        var stored = _db.GetParamTableRevision(revisionId)!;
        Assert.Equal(2, stored.Number);
        Assert.Equal("объект попросил 55 Гц", stored.Reason);
        Assert.Contains("P0-10: 50 → 55", stored.Summary);
        Assert.Equal(1, diff.ValueChanged);
    }

    [Fact]
    public void PreviousRevisionStaysAsItWas()
    {
        var id = NewTable();
        var (first, _) = ParamTableEditing.SaveRevision(_db, id, new[] { P("P0-10", "50") }, "первая", "Ilia");

        ParamTableEditing.SaveRevision(_db, id, new[] { P("P0-10", "55") }, "вторая", "Ilia");

        // Иначе «переключение между ревизиями» показывало бы одно и то же.
        Assert.Equal("50", _db.GetParamTableRows(first)[0].Value);
    }

    [Fact]
    public void NewGroupTypedByHand_JoinsTheCatalog()
    {
        var id = NewTable();

        ParamTableEditing.SaveRevision(_db, id,
            new[] { P("P8-01", "1", "ПИД-регулятор") }, "добавили ПИД", "Ilia");

        // Иначе группа осталась бы только в строках: показ разложил бы её «в конец», а в списке
        // групп её никто бы не увидел и второй раз выбрать не смог.
        Assert.Contains("ПИД-регулятор", _db.GetParamGroups());
    }

    [Fact]
    public void GroupTypedInAnotherCase_DoesNotSplitInTwo()
    {
        var id = NewTable();

        ParamTableEditing.SaveRevision(_db, id, new[] { P("P1-01", "15", "ДВИГАТЕЛЬ") }, "первая", "Ilia");

        Assert.Equal(ParamGroupCatalog.Defaults.Length, _db.GetParamGroups().Count);
        Assert.Equal(ParamGroupCatalog.Motor, _db.GetParamTableRows(_db.GetParamTableRevisions(id)[0].Id!.Value)[0].GroupName);
    }

    [Fact]
    public void ImportCreatesDocumentAndItsFirstRevisionTogether()
    {
        var preview = ParamTableEditing.Preview(TextFileEncoding.Cp1251.GetBytes(Sample),
            "ESQ-230 2025 - КПЧ(Задание Modbus).txt");

        var (tableId, revisionId) = ParamTableEditing.CreateFromImport(_db, new ParamTable
        {
            DiskPath = @"D:\ПО\ESQ", Filename = "ESQ-230.par",
            Name = preview.SuggestedName, Manufacturer = "ESQ",
        }, preview.Rows, "перенёс из txt", "Ilia");

        // Документ без единой ревизии — пустая строка в списке: показать в нём нечего, а уехать к
        // коллегам он бы успел.
        var table = _db.GetParamTable(tableId)!;
        Assert.Equal("Задание Modbus", table.Name);
        Assert.Single(_db.GetParamTableRevisions(tableId));
        Assert.Equal(3, _db.GetParamTableRows(revisionId).Count);
        Assert.Equal(ParamGroupCatalog.Motor, _db.GetParamTableRows(revisionId)[2].GroupName);
    }

    [Fact]
    public void EditingIsSavedAsANewRevision_NotOnTopOfTheOldOne()
    {
        var id = NewTable();
        ParamTableEditing.SaveRevision(_db, id, new[] { P("P0-02", "2"), P("P0-03", "9") }, "первая", "Ilia");

        var edited = _db.GetParamTableRows(_db.GetParamTableRevisions(id)[0].Id!.Value);
        edited[0].Value = "7";
        edited.RemoveAt(1);
        edited.Add(P("P5-00", "1"));
        ParamTableEditing.SaveRevision(_db, id, edited, "поправил после выезда", "Ilia");

        var revisions = _db.GetParamTableRevisions(id);
        Assert.Equal(2, revisions.Count);
        var summary = revisions[0].Summary;
        Assert.Contains("P0-02: 2 → 7", summary);
        Assert.Contains("Добавлено (1): P5-00", summary);
        Assert.Contains("Убрано (1): P0-03", summary);
    }
}
