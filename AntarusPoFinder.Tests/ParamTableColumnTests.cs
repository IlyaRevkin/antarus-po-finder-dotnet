using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Свои столбцы документа: содержимое ячейки (ParamRowExtra), правила показа и
/// переименования (ParamTableColumnEditing) и хранение с тумбстоуном (Database.ParamTables.cs).
///
/// Главное, ради чего эти тесты вообще есть: <b>переименование столбца не должно опустошать уже
/// сохранённые ревизии</b>. Ревизия — снимок, переписать её задним числом нельзя, поэтому
/// содержимое помечено ключом, а переименовывается заголовок.</summary>
public class ParamTableColumnTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly Database _db;

    public ParamTableColumnTests() => _db = new Database(_dbFile.Path);

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
    }

    private int NewTable() => _db.AddParamTable(new ParamTable
    {
        DiskPath = @"D:\ПО\ESQ", Filename = "ESQ-230.par", Name = "Задание Modbus", Manufacturer = "ESQ",
    });

    private static ParamTableRow P(string code, string extra = "") => new()
    {
        Kind = ParamRowKind.Param, Code = code, Title = "Параметр " + code, Value = "1",
        GroupName = ParamGroupCatalog.Main, Extra = extra,
    };

    // ── Содержимое ячейки ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Extra_RoundTripsThroughJson()
    {
        var text = ParamRowExtra.Format(new Dictionary<string, string>
        {
            ["Диапазон"] = "0…600",
            ["Кем проверено"] = "Иванов",
        });

        var back = ParamRowExtra.Parse(text);
        Assert.Equal("0…600", back["Диапазон"]);
        Assert.Equal("Иванов", back["Кем проверено"]);
    }

    [Fact]
    public void Extra_OfARowWithoutOwnColumns_IsAnEmptyString_NotBraces()
    {
        // Пустой набор обязан давать ровно то, что кладёт разбор txt, иначе первая же правка
        // документа выглядела бы как изменение всех строк сразу.
        Assert.Equal("", ParamRowExtra.Format(new Dictionary<string, string>()));
        Assert.Equal("", ParamRowExtra.Format(new Dictionary<string, string> { ["Диапазон"] = "   " }));
    }

    [Fact]
    public void Extra_GarbageInsteadOfJson_DoesNotThrow()
    {
        // Ячейка могла приехать с чужой машины или из чиненной руками базы. Уронить на ней ПОКАЗ
        // документа нельзя — человек тогда не увидит вообще ничего.
        Assert.Empty(ParamRowExtra.Parse("это не json"));
        Assert.Empty(ParamRowExtra.Parse("[1,2,3]"));
        Assert.Empty(ParamRowExtra.Parse(null));
    }

    [Fact]
    public void Extra_IsReadCaseInsensitively()
    {
        // «Диапазон» и «ДИАПАЗОН» с двух машин обязаны сойтись: ключ когда-то был заголовком,
        // а COLLATE NOCASE у SQLite кириллицу не сворачивает (см. CLAUDE.md).
        Assert.Equal("0…600", ParamRowExtra.Get("{\"ДИАПАЗОН\":\"0…600\"}", "Диапазон"));
    }

    [Fact]
    public void Extra_WithReplacesOneColumnAndLeavesTheRest()
    {
        var text = ParamRowExtra.With("{\"Диапазон\":\"0…600\",\"Кем проверено\":\"Иванов\"}", "Диапазон", "0…50");

        var back = ParamRowExtra.Parse(text);
        Assert.Equal("0…50", back["Диапазон"]);
        Assert.Equal("Иванов", back["Кем проверено"]);
    }

    [Fact]
    public void Extra_ClearingAValueRemovesTheKeyEntirely()
    {
        var text = ParamRowExtra.With("{\"Диапазон\":\"0…600\"}", "Диапазон", "  ");

        Assert.Equal("", text);
    }

    // ── Правила названий и порядка ───────────────────────────────────────────────────────────

    [Fact]
    public void ATitleThatRepeatsABuiltInColumn_IsRefused()
    {
        var why = ParamTableColumnEditing.WhyTitleWontDo(Array.Empty<ParamTableColumn>(), "значение");

        Assert.NotNull(why);
        Assert.Contains("встроенный", why);
    }

    [Fact]
    public void ATitleThatRepeatsAnExistingColumn_IsRefused_EvenInAnotherCase()
    {
        var existing = new[] { new ParamTableColumn { Id = 1, Key = "Диапазон", Title = "Диапазон" } };

        Assert.NotNull(ParamTableColumnEditing.WhyTitleWontDo(existing, "ДИАПАЗОН"));
        // Себе самому столбец не помеха: иначе его нельзя было бы переименовать «в тот же регистр».
        Assert.Null(ParamTableColumnEditing.WhyTitleWontDo(existing, "ДИАПАЗОН", exceptId: 1));
    }

    [Fact]
    public void ASnattedColumnDoesNotBlockTheName()
    {
        // Столбец убрали и заводят заново под тем же именем — обычное дело, и запрещать это
        // нечего: ключ у него тот же, содержимое старых ревизий вернётся вместе с ним.
        var existing = new[] { new ParamTableColumn { Id = 1, Key = "Диапазон", Title = "Диапазон", DeletedAt = "2026-08-25 10:00:00" } };

        Assert.Null(ParamTableColumnEditing.WhyTitleWontDo(existing, "Диапазон"));
    }

    [Fact]
    public void MovingAColumnStaysInsideTheList()
    {
        var columns = new[]
        {
            new ParamTableColumn { Id = 1, Title = "А" },
            new ParamTableColumn { Id = 2, Title = "Б" },
            new ParamTableColumn { Id = 3, Title = "В" },
        };

        Assert.Equal(new[] { "Б", "А", "В" }, ParamTableColumnEditing.Moved(columns, 1, -1).Select(c => c.Title));
        Assert.Equal(new[] { "А", "Б", "В" }, ParamTableColumnEditing.Moved(columns, 0, -1).Select(c => c.Title));
        Assert.Equal(new[] { "А", "Б", "В" }, ParamTableColumnEditing.Moved(columns, 2, +1).Select(c => c.Title));
    }

    // ── Что показывается у выбранной ревизии ─────────────────────────────────────────────────

    [Fact]
    public void ASnattedColumn_StillShowsUpWhereItHasContent()
    {
        var columns = new[]
        {
            new ParamTableColumn { Id = 1, Key = "Диапазон", Title = "Диапазон", SortOrder = 1, DeletedAt = "2026-08-25 10:00:00" },
            new ParamTableColumn { Id = 2, Key = "Кем проверено", Title = "Кем проверено", SortOrder = 2 },
        };
        var rows = new[] { P("P0-02", "{\"Диапазон\":\"0…600\"}") };

        // Столбец убирают «на будущее», а ревизия — снимок прошлого: спрячь её содержимое вместе
        // со столбцом, и человек увидит меньше, чем в редакции было записано, и не узнает об этом.
        Assert.Equal(new[] { "Диапазон", "Кем проверено" },
            ParamTableColumnEditing.Visible(columns, rows).Select(c => c.Title));

        // А там, где содержимого нет, снятого столбца и не видно.
        Assert.Equal(new[] { "Кем проверено" },
            ParamTableColumnEditing.Visible(columns, new[] { P("P0-02") }).Select(c => c.Title));
    }

    [Fact]
    public void AColumnThatHasNotArrivedYet_IsShownByItsKey()
    {
        // Секции конфига приезжают порознь: строки с содержимым уже здесь, а список столбцов ещё
        // нет. Показать содержимое всё равно надо — ключ и был когда-то заголовком.
        var visible = ParamTableColumnEditing.Visible(Array.Empty<ParamTableColumn>(),
            new[] { P("P0-02", "{\"Диапазон\":\"0…600\"}") });

        Assert.Equal("Диапазон", Assert.Single(visible).Title);
    }

    // ── Хранение ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RenamingAColumn_KeepsTheContentOfSavedRevisions()
    {
        var id = NewTable();
        var columnId = _db.AddParamTableColumn(id, "Диапазон");
        var revisionId = _db.AddParamTableRevision(new ParamTableRevision
        {
            TableId = id, Number = 1, Author = "Ilia",
            Rows = new List<ParamTableRow> { P("P0-02", "{\"Диапазон\":\"0…600\"}") },
        });

        _db.UpdateParamTableColumn(columnId, "Пределы", sortOrder: 1);

        // Ровно то, ради чего ключ разведён с заголовком: строки ревизии неизменяемы, и будь
        // содержимое помечено заголовком, столбец после переименования оказался бы пустым.
        var column = Assert.Single(_db.GetParamTableColumns(id));
        Assert.Equal("Пределы", column.Title);
        Assert.Equal("Диапазон", column.Key);
        Assert.Equal("0…600", ParamRowExtra.Get(_db.GetParamTableRows(revisionId)[0].Extra, column.Key));
    }

    [Fact]
    public void ASnattedColumn_ComesBackWithItsContentWhenAddedAgain()
    {
        var id = NewTable();
        var columnId = _db.AddParamTableColumn(id, "Диапазон");
        var revisionId = _db.AddParamTableRevision(new ParamTableRevision
        {
            TableId = id, Number = 1, Author = "Ilia",
            Rows = new List<ParamTableRow> { P("P0-02", "{\"Диапазон\":\"0…600\"}") },
        });

        _db.TombstoneParamTableColumn(columnId);
        var again = _db.AddParamTableColumn(id, "Диапазон");

        // Второй строки с тем же ключом быть не должно: содержимое в extra у них было бы общим.
        Assert.Equal(columnId, again);
        Assert.Single(_db.GetParamTableColumns(id));
        Assert.Equal("0…600", ParamRowExtra.Get(_db.GetParamTableRows(revisionId)[0].Extra, "Диапазон"));
    }

    [Fact]
    public void ASnattedColumnKeepsItsTitleForOldRevisions()
    {
        var id = NewTable();
        _db.TombstoneParamTableColumn(_db.AddParamTableColumn(id, "Диапазон"));

        // Тумбстоун, а не DELETE: по нему у старой ревизии и берётся заголовок снятого столбца.
        Assert.Empty(_db.GetParamTableColumns(id));
        Assert.Equal("Диапазон", Assert.Single(_db.AllParamTableColumnsIncludingDeleted(id)).Title);
    }

    [Fact]
    public void ApplyOrderRenumbersFromOne_AndLeavesUntouchedColumnsAlone()
    {
        var id = NewTable();
        _db.AddParamTableColumn(id, "А");
        _db.AddParamTableColumn(id, "Б");
        _db.AddParamTableColumn(id, "В");

        var columns = _db.GetParamTableColumns(id);
        ParamTableColumnEditing.ApplyOrder(_db, ParamTableColumnEditing.Moved(columns, 2, -2));

        Assert.Equal(new[] { "В", "А", "Б" }, _db.GetParamTableColumns(id).Select(c => c.Title));
    }

    [Fact]
    public void TidyNormalizesExtra_SoReorderedKeysAreNotAnEdit()
    {
        var one = ParamTableEditing.Tidy(new[] { P("P0-02", "{\"Б\":\"2\",\"А\":\"1\"}") })[0];
        var two = ParamTableEditing.Tidy(new[] { P("P0-02", "{\"А\":\"1\",\"Б\":\"2\"}") })[0];

        Assert.Equal(one.Extra, two.Extra);
    }
}
