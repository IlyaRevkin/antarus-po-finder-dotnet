using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Хранение таблиц параметров ПЧ/УПП: справочник групп, документы, ревизии и их строки
/// (Database.ParamTables.cs).</summary>
public class ParamTableDbTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly Database _db;

    public ParamTableDbTests() => _db = new Database(_dbFile.Path);

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
    }

    private int NewTable(string name = "Задание Modbus", string path = @"D:\ПО\ESQ", string file = "ESQ-230.par") =>
        _db.AddParamTable(new ParamTable { DiskPath = path, Filename = file, Name = name, Manufacturer = "ESQ" });

    private int NewRevision(int tableId, params ParamTableRow[] rows) =>
        _db.AddParamTableRevision(new ParamTableRevision
        {
            TableId = tableId,
            Number = _db.NextParamTableRevisionNumber(tableId),
            Reason = "первая заливка",
            Author = "Ilia",
            Rows = rows.ToList(),
        });

    private static ParamTableRow P(string code, string value, string group = ParamGroupCatalog.Main) => new()
    {
        Kind = ParamRowKind.Param, Code = code, Title = "Параметр " + code, Value = value,
        ValueState = ParamValueState.Set, GroupName = group,
    };

    // ── Справочник групп ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Groups_AreSeededInWorkOrder_NotAlphabetically()
    {
        var groups = _db.GetParamGroups();

        Assert.Equal(ParamGroupCatalog.Main, groups.First());
        // Сброс до заводских ПОСЛЕДНИМ: окажись он в середине, человек, идущий по таблице сверху
        // вниз, в какой-то момент обнулил бы всё, что уже выставил.
        Assert.Equal(ParamGroupCatalog.FactoryReset, groups.Last());
        Assert.Equal(ParamGroupCatalog.Defaults.Length, groups.Count);
    }

    [Fact]
    public void DeletedGroup_IsNotResurrectedByRestart()
    {
        _db.DeleteParamGroup(ParamGroupCatalog.Protections);
        _db.Dispose();

        using var reopened = new Database(_dbFile.Path);

        // Сид разовый: он применяется к базе, а не к каждому запуску. Иначе осознанно убранная
        // группа возвращалась бы каждое утро.
        Assert.DoesNotContain(ParamGroupCatalog.Protections, reopened.GetParamGroups());
    }

    [Fact]
    public void OwnGroup_FitsBetweenTheSeededOnes()
    {
        _db.AddParamGroup("ПИД-регулятор", 25);

        var groups = _db.GetParamGroups();

        Assert.Equal(ParamGroupCatalog.Communication, groups[1]);
        Assert.Equal("ПИД-регулятор", groups[2]);
        Assert.Equal(ParamGroupCatalog.InputsOutputs, groups[3]);
    }

    [Fact]
    public void GroupWrittenInAnotherCase_IsTheSameGroup()
    {
        // ⚠️ Для SQLite «Двигатель» и «двигатель» — РАЗНЫЕ строки: COLLATE NOCASE у него сворачивает
        // только латиницу. Значит свернуть регистр обязан .NET, иначе в справочнике заведётся вторая
        // «та же» группа, и половина строк уедет под неё.
        _db.AddParamGroup("двигатель");

        var groups = _db.GetParamGroups();
        Assert.Equal(ParamGroupCatalog.Defaults.Length, groups.Count);
        Assert.Contains(ParamGroupCatalog.Motor, groups);
        Assert.DoesNotContain("двигатель", groups);

        var order = _db.GetParamGroupOrder();

        Assert.Equal(50, order[ParamGroupCatalog.Motor]);
        Assert.Equal(50, ParamGroupCatalog.OrderOf("ДВИГАТЕЛЬ", order));
        // Группа, которой в справочнике уже нет, читается и уходит в конец — но выше сброса.
        Assert.True(ParamGroupCatalog.OrderOf("Своя давняя группа", order) < order[ParamGroupCatalog.FactoryReset]);
    }

    [Fact]
    public void UsedGroups_AreCollectedFromLiveRevisionsOnly()
    {
        var live = NewTable();
        NewRevision(live, P("P1-01", "15", ParamGroupCatalog.Motor));

        var dropped = NewTable("Старый документ");
        NewRevision(dropped, P("P0-02", "2", ParamGroupCatalog.Communication));
        _db.TombstoneParamTable(dropped);

        var used = _db.CollectUsedParamGroups();

        Assert.Contains(ParamGroupCatalog.Motor, used);
        Assert.DoesNotContain(ParamGroupCatalog.Communication, used);
    }

    // ── Документ ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NewTable_GetsSyncIdAndStampsImmediately()
    {
        var table = new ParamTable { DiskPath = @"D:\ПО\ESQ", Filename = "ESQ-230.par", Name = "Задание Modbus" };

        _db.AddParamTable(table);

        // Не «проставим при следующем запуске»: документ может уехать в общий конфиг в ту же минуту,
        // а без sync_id получатель не соотнесёт его ни с чем.
        Assert.NotEqual("", table.SyncId);
        Assert.NotEqual("", table.CreatedAt);
        Assert.NotEqual("", table.UpdatedAt);
    }

    [Fact]
    public void TableIsFoundByTheFileItself_NotByTheRowInParamFiles()
    {
        NewTable(path: @"D:\ПО\ESQ", file: "ESQ-230.par");

        // Один и тот же файл записан по-разному на разных машинах: хвостовой слэш, регистр имени.
        // Ключ — сам файл (Database.FileKey), поэтому все три записи означают одно.
        Assert.Single(_db.GetParamTablesForFile(@"D:\ПО\ESQ", "ESQ-230.par"));
        Assert.Single(_db.GetParamTablesForFile(@"D:\ПО\ESQ\", "esq-230.PAR"));
        Assert.Empty(_db.GetParamTablesForFile(@"D:\ПО\Другая", "ESQ-230.par"));
    }

    [Fact]
    public void SeveralDocumentsPerFile_LiveSideBySide()
    {
        NewTable("Задание Modbus");
        NewTable("Пуск по месту");

        Assert.Equal(2, _db.GetParamTablesForFile(@"D:\ПО\ESQ", "ESQ-230.par").Count);
    }

    [Fact]
    public void TombstonedTable_DisappearsFromLists_ButKeepsItsRevisions()
    {
        var id = NewTable();
        NewRevision(id, P("P0-02", "2"));

        _db.TombstoneParamTable(id);

        Assert.Empty(_db.GetParamTables());
        Assert.Empty(_db.GetParamTablesForFile(@"D:\ПО\ESQ", "ESQ-230.par"));
        // Снятие — отметкой, а не DELETE: строка обязана и дальше ездить по машинам как сигнал
        // «это удалили», а содержимое остаётся читаемым, если удаление окажется ошибкой.
        Assert.NotEqual("", _db.GetParamTable(id)!.DeletedAt);
        Assert.Single(_db.GetParamTableRevisions(id));
    }

    [Fact]
    public void MovedFile_TakesItsDocumentAlong()
    {
        var id = NewTable();

        _db.UpdateParamTableFile(id, @"Z:\Software\ESQ", "ESQ-230.par");

        Assert.Single(_db.GetParamTablesForFile(@"Z:\Software\ESQ", "ESQ-230.par"));
        Assert.Empty(_db.GetParamTablesForFile(@"D:\ПО\ESQ", "ESQ-230.par"));
    }

    // ── Ревизии ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Revisions_AreNumberedFromOne_AndListedFreshestFirst()
    {
        var id = NewTable();
        NewRevision(id, P("P0-02", "2"));
        NewRevision(id, P("P0-02", "3"));

        var revisions = _db.GetParamTableRevisions(id);

        Assert.Equal(new[] { 2, 1 }, revisions.Select(r => r.Number));
    }

    [Fact]
    public void DeletedRevisionNumber_IsNeverReused()
    {
        var id = NewTable();
        NewRevision(id, P("P0-02", "2"));
        var second = NewRevision(id, P("P0-02", "3"));

        _db.TombstoneParamTableRevision(second);

        // Номер — то, чем ревизию называют вслух («вернулись к третьей»). Переиспользовать номер
        // снятой значит завести в разговоре двух разных «третьих».
        Assert.Equal(3, _db.NextParamTableRevisionNumber(id));
        Assert.Single(_db.GetParamTableRevisions(id));
    }

    [Fact]
    public void RevisionRows_ComeBackExactlyAsSaved_InSourceOrder()
    {
        var id = NewTable();
        var rows = new[]
        {
            new ParamTableRow
            {
                Kind = ParamRowKind.Param, GroupName = ParamGroupCatalog.Main, Code = "P0-10", Title = "Максимальная частота",
                ValueState = ParamValueState.Ask, Factory = "50", Unit = "Гц", Description = "уточнить по ПЛК",
                Applicability = "Только для ПЧ №1", AppliesWhen = "Для 55 ГЦ", Extra = "{\"Диапазон\":\"0-600\"}",
            },
            new ParamTableRow { Kind = ParamRowKind.Note, GroupName = ParamGroupCatalog.Main, Title = "В ПЛК выставить частоту 55Гц" },
            new ParamTableRow { Kind = ParamRowKind.Param, GroupName = ParamGroupCatalog.Motor, Code = "P1-01", Title = "Мощность", ValueState = ParamValueState.OnSite },
        };

        var revisionId = NewRevision(id, rows);
        var back = _db.GetParamTableRows(revisionId);

        Assert.Equal(new[] { "P0-10", "", "P1-01" }, back.Select(r => r.Code));
        Assert.Equal(new[] { 0, 1, 2 }, back.Select(r => r.SortOrder));
        Assert.Equal(ParamValueState.Ask, back[0].ValueState);
        Assert.Equal("Только для ПЧ №1", back[0].Applicability);
        Assert.Equal("Для 55 ГЦ", back[0].AppliesWhen);
        Assert.Equal("{\"Диапазон\":\"0-600\"}", back[0].Extra);
        Assert.Equal(ParamRowKind.Note, back[1].Kind);
        Assert.Equal(ParamValueState.OnSite, back[2].ValueState);
    }

    [Fact]
    public void ValueStateIsNormalizedOnTheWayIn()
    {
        var id = NewTable();
        var revisionId = NewRevision(id, new ParamTableRow { Code = "P0-02", Value = "2", ValueState = "" });

        // Пустое состояние — «значение задано»: иначе ревизия, приехавшая со старой машины,
        // показала бы каждую строку как «снимается по месту».
        Assert.Equal(ParamValueState.Set, _db.GetParamTableRows(revisionId)[0].ValueState);
    }

    [Fact]
    public void RevisionAndItsRows_ArriveTogetherOrNotAtAll()
    {
        var id = NewTable();
        var revision = new ParamTableRevision
        {
            TableId = id, Number = 1, Author = "Ilia",
            Rows = new List<ParamTableRow> { P("P0-02", "2"), null! },
        };

        // Полуприехавшая ревизия (строка есть, строк таблицы нет) читалась бы как «в этой редакции
        // стёрли всё», и разбор изменений честно бы это и показал.
        Assert.ThrowsAny<Exception>(() => _db.AddParamTableRevision(revision));
        Assert.Empty(_db.GetParamTableRevisions(id));
    }

    [Fact]
    public void ReasonIsEditable_RowsAreNot()
    {
        var id = NewTable();
        var revisionId = NewRevision(id, P("P0-02", "2"));

        _db.UpdateParamTableRevisionReason(revisionId, "уточнили после выезда");

        Assert.Equal("уточнили после выезда", _db.GetParamTableRevision(revisionId)!.Reason);
        Assert.Equal("2", _db.GetParamTableRows(revisionId)[0].Value);
    }

    [Fact]
    public void SummaryIsComputed_ReasonIsWrittenByHand()
    {
        var id = NewTable();
        var first = NewRevision(id, P("P0-10", "50"));
        var newRows = new[] { P("P0-10", "55") };
        var summary = ParamTableDiff.Describe(ParamTableDiff.Compare(_db.GetParamTableRows(first), newRows));

        var second = _db.AddParamTableRevision(new ParamTableRevision
        {
            TableId = id, Number = _db.NextParamTableRevisionNumber(id),
            Reason = "объект попросил 55 Гц", Summary = summary, Author = "Ilia", Rows = newRows.ToList(),
        });

        var stored = _db.GetParamTableRevision(second)!;
        Assert.Equal("объект попросил 55 Гц", stored.Reason);
        Assert.Contains("P0-10: 50 → 55", stored.Summary);
    }

    // ── Свои столбцы ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OwnColumns_AreAddedOnceEvenIfAskedTwice()
    {
        var id = NewTable();

        var first = _db.AddParamTableColumn(id, "Диапазон");
        var again = _db.AddParamTableColumn(id, "ДИАПАЗОН");

        // Два столбца «Диапазон» не различить ни глазами, ни по содержимому: ключ в
        // ParamTableRow.Extra — как раз название.
        Assert.Equal(first, again);
        Assert.Single(_db.GetParamTableColumns(id));
        Assert.Equal(-1, _db.AddParamTableColumn(id, "   "));
    }

    [Fact]
    public void RemovingAColumn_DoesNotRewriteSavedRevisions()
    {
        var id = NewTable();
        var columnId = _db.AddParamTableColumn(id, "Диапазон");
        var revisionId = NewRevision(id, new ParamTableRow { Code = "P0-02", Value = "2", Extra = "{\"Диапазон\":\"0-600\"}" });

        _db.DeleteParamTableColumn(columnId);

        // Ревизия — снимок. Переписывать её задним числом значит рассказывать про прошлое неправду.
        Assert.Empty(_db.GetParamTableColumns(id));
        Assert.Equal("{\"Диапазон\":\"0-600\"}", _db.GetParamTableRows(revisionId)[0].Extra);
    }
}
