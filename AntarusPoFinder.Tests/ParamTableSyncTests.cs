using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Таблицы параметров ПЧ/УПП между двумя машинами (секция param_tables в
/// Database.ConfigExchange.cs) и справочник групп рядом с ней.
///
/// Секция аддитивная и с тумбстоуном, как fw_versions/param_files/fw_attachments, и проверяется то
/// же, на чём этот класс задач уже обжигался: новая ревизия доезжает, удаление доезжает, снятое не
/// воскресает, а снимок СО СТАРОЙ версии приложения (секции нет вовсе) ничего не стирает.</summary>
public class ParamTableSyncTests : IDisposable
{
    private readonly TempDb _fileA = new();
    private readonly TempDb _fileB = new();
    private readonly Database _a;
    private readonly Database _b;

    public ParamTableSyncTests()
    {
        _a = new Database(_fileA.Path);
        _b = new Database(_fileB.Path);
        // Рукопожатие, пока базы ещё совпадают по именам (см. док ConfigSyncTests).
        _b.ImportHierarchyData(_a.ExportHierarchyData());
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
        _fileA.Dispose();
        _fileB.Dispose();
    }

    private const string Folder = @"Z:\Software\Параметры\ESQ";
    private const string File = "ESQ-230.par";

    private static ParamTableRow P(string code, string value, string group = ParamGroupCatalog.Main) => new()
    {
        Kind = ParamRowKind.Param, Code = code, Title = "Параметр " + code, Value = value,
        ValueState = ParamValueState.Set, GroupName = group,
    };

    private static int NewTable(Database db, string name = "Задание Modbus") =>
        db.AddParamTable(new ParamTable { DiskPath = Folder, Filename = File, Name = name, Manufacturer = "ESQ" });

    private void Sync() => _b.ImportHierarchyData(_a.ExportHierarchyData());

    private ParamTable OnB(string name = "Задание Modbus") =>
        _b.GetParamTablesForFile(Folder, File).Single(t => t.Name == name);

    // ── Документ и его ревизии доезжают целиком ──────────────────────────────────────────────

    [Fact]
    public void DocumentTravelsWithItsRevisionsAndRows()
    {
        var id = NewTable(_a);
        ParamTableEditing.SaveRevision(_a, id, new[] { P("P0-02", "2"), P("P1-01", "15", ParamGroupCatalog.Motor) },
            "перенёс из txt", "Ilia");

        Sync();

        var table = OnB();
        var revision = Assert.Single(_b.GetParamTableRevisions(table.Id!.Value));
        Assert.Equal("перенёс из txt", revision.Reason);
        var rows = _b.GetParamTableRows(revision.Id!.Value);
        Assert.Equal(new[] { "P0-02", "P1-01" }, rows.Select(r => r.Code));
        Assert.Equal(ParamGroupCatalog.Motor, rows[1].GroupName);
    }

    [Fact]
    public void ValueStatesSurviveTheTrip()
    {
        var id = NewTable(_a);
        ParamTableEditing.SaveRevision(_a, id, new[]
        {
            new ParamTableRow { Code = "P0-10", Title = "Максимальная частота", ValueState = ParamValueState.Ask },
            new ParamTableRow { Code = "P1-01", Title = "Мощность", ValueState = ParamValueState.OnSite },
        }, "первая", "Ilia");

        Sync();

        var rows = _b.GetParamTableRows(_b.GetParamTableRevisions(OnB().Id!.Value)[0].Id!.Value);
        // «Уточнить по ПЛК» и «снимается с шильдика» доехали как есть. Схлопнись они в пустую
        // ячейку — на объекте это читалось бы как «здесь ноль».
        Assert.Equal(ParamValueState.Ask, rows[0].ValueState);
        Assert.Equal(ParamValueState.OnSite, rows[1].ValueState);
    }

    [Fact]
    public void NewRevisionArrivesWithoutTouchingTheOldOne()
    {
        var id = NewTable(_a);
        ParamTableEditing.SaveRevision(_a, id, new[] { P("P0-10", "50") }, "первая", "Ilia");
        Sync();

        ParamTableEditing.SaveRevision(_a, id, new[] { P("P0-10", "55") }, "объект попросил 55 Гц", "Ilia");
        Sync();

        var revisions = _b.GetParamTableRevisions(OnB().Id!.Value);
        Assert.Equal(new[] { 2, 1 }, revisions.Select(r => r.Number));
        Assert.Equal("50", _b.GetParamTableRows(revisions[1].Id!.Value)[0].Value);
        Assert.Equal("55", _b.GetParamTableRows(revisions[0].Id!.Value)[0].Value);
        // Разбор изменений посчитан ОДИН раз, на машине автора, и приехал готовым: пересчитывать
        // его у получателя незачем, у него те же строки.
        Assert.Contains("P0-10: 50 → 55", revisions[0].Summary);
    }

    [Fact]
    public void SecondSyncChangesNothing()
    {
        var id = NewTable(_a);
        ParamTableEditing.SaveRevision(_a, id, new[] { P("P0-02", "2") }, "первая", "Ilia");
        Sync();
        Sync();

        Assert.Single(_b.GetParamTablesForFile(Folder, File));
        Assert.Single(_b.GetParamTableRevisions(OnB().Id!.Value));
    }

    // ── Удаление ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DeletionTravels()
    {
        var id = NewTable(_a);
        ParamTableEditing.SaveRevision(_a, id, new[] { P("P0-02", "2") }, "первая", "Ilia");
        Sync();

        _a.TombstoneParamTable(id);
        Sync();

        Assert.Empty(_b.GetParamTablesForFile(Folder, File));
    }

    [Fact]
    public void LocalDeletionIsPermanent()
    {
        var id = NewTable(_a);
        ParamTableEditing.SaveRevision(_a, id, new[] { P("P0-02", "2") }, "первая", "Ilia");
        Sync();

        // На B документ убрали, а A об этом ещё не знает и присылает свою живую копию. Воскреснуть
        // документ не должен — ровно та жалоба «у меня 2 записи, у коллеги 4».
        _b.TombstoneParamTable(OnB().Id!.Value);
        Sync();

        Assert.Empty(_b.GetParamTablesForFile(Folder, File));
    }

    [Fact]
    public void DeletedRevisionDoesNotComeBack()
    {
        var id = NewTable(_a);
        ParamTableEditing.SaveRevision(_a, id, new[] { P("P0-02", "2") }, "первая", "Ilia");
        var (second, _) = ParamTableEditing.SaveRevision(_a, id, new[] { P("P0-02", "3") }, "вторая", "Ilia");
        Sync();

        _a.TombstoneParamTableRevision(second);
        Sync();

        var revisions = _b.GetParamTableRevisions(OnB().Id!.Value);
        Assert.Equal(new[] { 1 }, revisions.Select(r => r.Number));
    }

    [Fact]
    public void AlreadyDeletedDocumentIsNotMaterializedAsAGhost()
    {
        var id = NewTable(_a);
        ParamTableEditing.SaveRevision(_a, id, new[] { P("P0-02", "2") }, "первая", "Ilia");
        _a.TombstoneParamTable(id);

        // B об этом документе не слышал вовсе. Завести его только затем, чтобы тут же спрятать под
        // тумбстоуном, значит показать фантом в списке «всех документов».
        Sync();

        Assert.Empty(_b.GetParamTables());
    }

    // ── Правки шапки и «зачем» ───────────────────────────────────────────────────────────────

    [Fact]
    public void RenamedDocumentTravels()
    {
        var id = NewTable(_a);
        ParamTableEditing.SaveRevision(_a, id, new[] { P("P0-02", "2") }, "первая", "Ilia");
        Sync();

        _a.UpdateParamTable(id, "Задание Modbus (новая серия)", "ESQ");
        Sync();

        Assert.Equal("Задание Modbus (новая серия)", _b.GetParamTablesForFile(Folder, File).Single().Name);
    }

    [Fact]
    public void EditedReasonTravels_ButRowsStayAsTheyWere()
    {
        var id = NewTable(_a);
        var (revisionId, _) = ParamTableEditing.SaveRevision(_a, id, new[] { P("P0-02", "2") }, "первая", "Ilia");
        Sync();

        _a.UpdateParamTableRevisionReason(revisionId, "уточнили после выезда");
        Sync();

        var revision = _b.GetParamTableRevisions(OnB().Id!.Value).Single();
        Assert.Equal("уточнили после выезда", revision.Reason);
        Assert.Equal("2", _b.GetParamTableRows(revision.Id!.Value)[0].Value);
    }

    [Fact]
    public void OlderEditDoesNotOverwriteAFresherOne()
    {
        var id = NewTable(_a);
        ParamTableEditing.SaveRevision(_a, id, new[] { P("P0-02", "2") }, "первая", "Ilia");
        Sync();

        // На B поправили позже — приехавшая старая правка с A побеждать не должна: сводим по времени
        // правки, а не по тому, кто позже нажал импорт.
        var localRevision = _b.GetParamTableRevisions(OnB().Id!.Value).Single();
        _b.UpdateParamTableRevisionReason(localRevision.Id!.Value, "правка на B", "2030-01-01 00:00:00.000");
        Sync();

        Assert.Equal("правка на B", _b.GetParamTableRevisions(OnB().Id!.Value).Single().Reason);
    }

    // ── Старый клиент ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SnapshotFromAnOlderAppVersionErasesNothing()
    {
        var id = NewTable(_a);
        ParamTableEditing.SaveRevision(_a, id, new[] { P("P0-02", "2") }, "первая", "Ilia");
        Sync();

        // Снимок без секции вовсе — так выглядит выгрузка со старой версии программы. «Секции нет»
        // означает «отправитель о ней не знает», а не «у отправителя таблиц ноль».
        var snapshot = _a.ExportHierarchyData();
        snapshot.ParamTables = null;
        snapshot.ParamGroups = null;
        _b.ImportHierarchyData(snapshot);

        Assert.Single(_b.GetParamTablesForFile(Folder, File));
        Assert.NotEmpty(_b.GetParamGroups());
    }

    // ── Справочник групп ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void NewGroupTravelsWithItsPlaceInTheOrder()
    {
        _a.AddParamGroup("ПИД-регулятор", 25);

        Sync();

        var groups = _b.GetParamGroups();
        // Порядок и есть главное содержимое этого справочника: приехав одними именами, группа встала
        // бы в конец, и «сперва основные значения, сброс до заводских в конце» перестало бы
        // выполняться у всех, кроме автора.
        Assert.Equal(ParamGroupCatalog.Communication, groups[1]);
        Assert.Equal("ПИД-регулятор", groups[2]);
    }

    [Fact]
    public void DeletedGroupTravels_AndDoesNotComeBack()
    {
        _a.DeleteParamGroup(ParamGroupCatalog.Protections);

        Sync();
        Sync();

        Assert.DoesNotContain(ParamGroupCatalog.Protections, _b.GetParamGroups());
    }

    [Fact]
    public void GroupInUseSurvivesAnAuthoritativeSnapshot()
    {
        // Эталонный снимок присылает ПОЛНЫЙ список, и группы «ПИД-регулятор» в нём нет. Но локальная
        // строка таблицы ею помечена — убрав группу, мы отняли бы у полусотни строк единственную
        // подпись, к чему они относятся (тот же мягкий предохранитель, что у видов доп. материалов).
        var id = NewTable(_b);
        ParamTableEditing.SaveRevision(_b, id, new[] { P("P8-01", "1", "ПИД-регулятор") }, "первая", "Ilia");

        var snapshot = _a.ExportHierarchyData();
        var counts = _b.ImportHierarchyData(snapshot, authoritative: true);

        Assert.Contains("ПИД-регулятор", _b.GetParamGroups());
        Assert.Equal(1, counts.ParamGroupsSkippedDelete);
    }

    [Fact]
    public void ImportCountsSayWhatArrived()
    {
        var id = NewTable(_a);
        ParamTableEditing.SaveRevision(_a, id, new[] { P("P0-02", "2") }, "первая", "Ilia");

        var counts = _b.ImportHierarchyData(_a.ExportHierarchyData());

        // Сводка приёма обязана называть самое частое изменение вслух: обычный случай — «документ у
        // всех давно есть, приехала его новая редакция».
        Assert.Equal(1, counts.ParamTablesAdded);
        Assert.Equal(1, counts.ParamTableRevisionsAdded);
    }

    [Fact]
    public void DryRunPromisesExactlyWhatApplyingWouldDo()
    {
        var id = NewTable(_a);
        ParamTableEditing.SaveRevision(_a, id, new[] { P("P0-02", "2") }, "первая", "Ilia");
        ParamTableEditing.SaveRevision(_a, id, new[] { P("P0-02", "3") }, "вторая", "Ilia");
        var snapshot = _a.ExportHierarchyData();

        var promised = _b.PreviewImportHierarchyData(snapshot);
        var applied = _b.ImportHierarchyData(snapshot);

        // Плашка приёма считается сухим прогоном. Разойдись она с применением — она обещала бы «один
        // документ», а приезжал бы документ с восемью редакциями.
        Assert.Equal(applied.ParamTablesAdded, promised.ParamTablesAdded);
        Assert.Equal(applied.ParamTableRevisionsAdded, promised.ParamTableRevisionsAdded);
        Assert.Equal(2, promised.ParamTableRevisionsAdded);
    }

    // ── Свои столбцы документа ───────────────────────────────────────────────────────────────

    [Fact]
    public void OwnColumnsTravelWithTheDocument()
    {
        var id = NewTable(_a);
        _a.AddParamTableColumn(id, "Диапазон");
        ParamTableEditing.SaveRevision(_a, id, new[] { P("P0-02", "2") }, "перенёс из txt", "Ilia");

        Sync();

        var column = Assert.Single(_b.GetParamTableColumns(OnB().Id!.Value));
        Assert.Equal("Диапазон", column.Title);
        Assert.Equal("Диапазон", column.Key);
    }

    [Fact]
    public void RenamingAColumnTravels_AndKeepsTheContentOfTheRevisionsAlreadyThere()
    {
        var id = NewTable(_a);
        var columnId = _a.AddParamTableColumn(id, "Диапазон");
        var rows = new[] { WithExtra(P("P0-02", "2"), "Диапазон", "0…600") };
        ParamTableEditing.SaveRevision(_a, id, rows, "перенёс из txt", "Ilia");
        Sync();

        _a.UpdateParamTableColumn(columnId, "Пределы", sortOrder: 1);
        Sync();

        var tableId = OnB().Id!.Value;
        var column = Assert.Single(_b.GetParamTableColumns(tableId));
        Assert.Equal("Пределы", column.Title);
        // Ключ тот же, поэтому содержимое уже приехавшей ревизии никуда не делось.
        var revision = _b.GetParamTableRevisions(tableId).Single();
        Assert.Equal("0…600", ParamRowExtra.Get(_b.GetParamTableRows(revision.Id!.Value)[0].Extra, column.Key));
    }

    [Fact]
    public void ARemovedColumnStaysRemoved_AndDoesNotComeBackWithTheNextSnapshot()
    {
        var id = NewTable(_a);
        var columnId = _a.AddParamTableColumn(id, "Диапазон");
        ParamTableEditing.SaveRevision(_a, id, new[] { P("P0-02", "2") }, "перенёс из txt", "Ilia");
        Sync();

        _a.TombstoneParamTableColumn(columnId);
        Sync();
        Assert.Empty(_b.GetParamTableColumns(OnB().Id!.Value));

        // И местное снятие тоже постоянно: чужая живая копия его не отменяет.
        Sync();
        Assert.Empty(_b.GetParamTableColumns(OnB().Id!.Value));
    }

    [Fact]
    public void ASnapshotFromAnOlderVersion_StillBringsColumnsButNeverRemovesThem()
    {
        var id = NewTable(_a);
        _a.AddParamTableColumn(id, "Диапазон");
        ParamTableEditing.SaveRevision(_a, id, new[] { P("P0-02", "2") }, "перенёс из txt", "Ilia");

        // Снимок с 1.74.2: нового поля нет, есть только список заголовков.
        var data = _a.ExportHierarchyData();
        foreach (var table in data.ParamTables!) table.ParamColumns = null;
        _b.ImportHierarchyData(data);

        Assert.Equal("Диапазон", Assert.Single(_b.GetParamTableColumns(OnB().Id!.Value)).Title);

        // Тот же снимок, но столбца в нём уже нет: в одних заголовках нельзя отличить «его у
        // отправителя нет» от «он его убрал», поэтому не убираем ничего.
        var later = _a.ExportHierarchyData();
        foreach (var table in later.ParamTables!)
        {
            table.ParamColumns = null;
            table.Columns.Clear();
        }
        _b.ImportHierarchyData(later);

        Assert.Single(_b.GetParamTableColumns(OnB().Id!.Value));
    }

    [Fact]
    public void ValuesOfOwnColumnsSurviveTheTrip()
    {
        var id = NewTable(_a);
        _a.AddParamTableColumn(id, "Диапазон");
        ParamTableEditing.SaveRevision(_a, id,
            new[] { WithExtra(P("P0-02", "2"), "Диапазон", "0…600") }, "перенёс из txt", "Ilia");

        Sync();

        var revision = _b.GetParamTableRevisions(OnB().Id!.Value).Single();
        Assert.Equal("0…600", ParamRowExtra.Get(_b.GetParamTableRows(revision.Id!.Value)[0].Extra, "Диапазон"));
    }

    private static ParamTableRow WithExtra(ParamTableRow row, string key, string value)
    {
        row.Extra = ParamRowExtra.With(row.Extra, key, value);
        return row;
    }
}
