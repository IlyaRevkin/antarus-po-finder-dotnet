using System;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Правка справочника групп параметров из Настроек (ParamGroupEditing).
///
/// Жалоба владельца была дословно про это: «поля к примеру в выпадающих списках не настраиваемые,
/// надо добавить в настройки возможность редактировать их». Справочник до этого засевался
/// миграцией, а править его было нечем.
///
/// ⚠️ Половина тестов ниже — про кириллицу: у SQLite COLLATE NOCASE сворачивает только латиницу, и
/// «Двигатель»/«двигатель» для базы РАЗНЫЕ строки, а для .NET OrdinalIgnoreCase — одна и та же.</summary>
public class ParamGroupEditingTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly Database _db;

    public ParamGroupEditingTests() => _db = new Database(_dbFile.Path);

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
    }

    private int RowInGroup(string group)
    {
        var tableId = _db.AddParamTable(new ParamTable { DiskPath = @"D:\ПО", Filename = "x.par", Name = "Док" });
        return _db.AddParamTableRevision(new ParamTableRevision
        {
            TableId = tableId,
            Number = 1,
            Author = "Ilia",
            Rows = new()
            {
                new ParamTableRow { Kind = ParamRowKind.Param, Code = "P0-02", Title = "Канал", GroupName = group },
            },
        });
    }

    // ── Завести ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Add_PutsTheGroupBeforeFactoryReset()
    {
        var result = ParamGroupEditing.Add(_db, "Пожарный режим");

        Assert.True(result.Ok);
        // Предпоследней: новая группа встаёт в конец, но ВЫШЕ «Сброса до заводских».
        var groups = _db.GetParamGroups();
        Assert.Equal(groups.Count - 2, groups.IndexOf("Пожарный режим"));
        Assert.Equal(ParamGroupCatalog.FactoryReset, groups[^1]);
    }

    [Fact]
    public void Add_DoesNotMakeASecondGroupDifferingOnlyInCase()
    {
        // Ровно та ловушка, из-за которой сравнение идёт в .NET: INSERT прошёл бы, и в списке
        // оказались бы «Двигатель» и «двигатель» — две группы про одно и то же, с разными местами.
        var result = ParamGroupEditing.Add(_db, "двигатель");

        Assert.False(result.Ok);
        Assert.Contains(ParamGroupCatalog.Motor, result.Message);
        Assert.Single(_db.GetParamGroups().Where(g =>
            string.Equals(g, ParamGroupCatalog.Motor, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Add_RefusesEmptyName()
    {
        Assert.False(ParamGroupEditing.Add(_db, "   ").Ok);
    }

    // ── Переименовать ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rename_KeepsThePlaceInTheOrder()
    {
        var before = _db.GetParamGroups().IndexOf(ParamGroupCatalog.Motor);

        Assert.True(ParamGroupEditing.Rename(_db, ParamGroupCatalog.Motor, "Электродвигатель").Ok);

        var groups = _db.GetParamGroups();
        Assert.Equal(before, groups.IndexOf("Электродвигатель"));
        Assert.DoesNotContain(ParamGroupCatalog.Motor, groups);
    }

    [Fact]
    public void Rename_AlsoFixesTheLabelOnAlreadySavedRows()
    {
        // Группа лежит в строке ТЕКСТОМ. Не поправь её здесь — полсотни строк остались бы с именем,
        // которого в справочнике больше нет, то есть выпали бы из порядка показа (см.
        // ParamGroupCatalog.OrderOf: незнакомая группа уходит в конец).
        var revisionId = RowInGroup(ParamGroupCatalog.Motor);

        var result = ParamGroupEditing.Rename(_db, ParamGroupCatalog.Motor, "Электродвигатель");

        Assert.True(result.Ok);
        Assert.Equal("Электродвигатель", _db.GetParamTableRows(revisionId).Single().GroupName);
        Assert.Contains("строк", result.Message);
    }

    [Fact]
    public void Rename_ToAnExistingName_IsRefused()
    {
        var result = ParamGroupEditing.Rename(_db, ParamGroupCatalog.Motor, ParamGroupCatalog.Protections);

        Assert.False(result.Ok);
        Assert.Contains(ParamGroupCatalog.Protections, _db.GetParamGroups());
        Assert.Contains(ParamGroupCatalog.Motor, _db.GetParamGroups());
    }

    [Fact]
    public void Rename_ChangingOnlyTheCase_IsAllowed()
    {
        // «двигатель» → «Двигатель» — это не столкновение с самим собой, а ровно то, ради чего
        // правка и нужна: приехавшее с чужой машины написание приводят к своему.
        _db.AddParamGroup("насос");
        var revisionId = RowInGroup("насос");

        var result = ParamGroupEditing.Rename(_db, "насос", "Насос");

        Assert.True(result.Ok);
        Assert.Contains("Насос", _db.GetParamGroups());
        Assert.Equal("Насос", _db.GetParamTableRows(revisionId).Single().GroupName);
    }

    // ── Порядок ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Move_SwapsWithTheNeighbour()
    {
        var before = _db.GetParamGroups();
        var at = before.IndexOf(ParamGroupCatalog.Communication);

        Assert.True(ParamGroupEditing.Move(_db, ParamGroupCatalog.Communication, -1).Ok);

        var after = _db.GetParamGroups();
        Assert.Equal(at - 1, after.IndexOf(ParamGroupCatalog.Communication));
        Assert.Equal(at, after.IndexOf(before[at - 1]));
    }

    [Fact]
    public void Move_OfTheFirstGroupUp_SaysSoInsteadOfDoingNothing()
    {
        var first = _db.GetParamGroups()[0];

        var result = ParamGroupEditing.Move(_db, first, -1);

        Assert.False(result.Ok);
        Assert.Equal(first, _db.GetParamGroups()[0]);
    }

    [Fact]
    public void Move_WorksEvenWhenTwoGroupsShareTheSamePlace()
    {
        // Порядок правят руками, и совпавшие числа — обычное дело. Поэтому перестановка
        // ПЕРЕНУМЕРОВЫВАЕТ весь список, а не меняет местами два sort_order: на равных числах обмен
        // не делал бы ничего, и кнопка выглядела бы сломанной.
        _db.AddParamGroup("Насос", 50);
        _db.AddParamGroup("Клапан", 50);

        Assert.True(ParamGroupEditing.Move(_db, "Клапан", -1).Ok);

        var groups = _db.GetParamGroups();
        Assert.True(groups.IndexOf("Клапан") < groups.IndexOf("Насос"));
        Assert.Equal(groups.Count, groups.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void FactoryReset_StaysLast_WhenNewGroupsAppearAfterReordering()
    {
        // Перестановка перенумеровывает список десятками, и прежняя метка «последней» (1000)
        // перестаёт существовать. Новая группа всё равно обязана встать ВЫШЕ сброса: человек идёт
        // по таблице сверху вниз, и сброс в середине обнулит всё, что он уже выставил.
        ParamGroupEditing.Move(_db, ParamGroupCatalog.Communication, -1);
        ParamGroupEditing.Add(_db, "Пожарный режим");
        ParamGroupEditing.Add(_db, "Насос");

        Assert.Equal(ParamGroupCatalog.FactoryReset, _db.GetParamGroups()[^1]);
    }

    [Fact]
    public void ResetToDefaults_BringsBackTheWorkOrder_AndKeepsOwnGroups()
    {
        ParamGroupEditing.Add(_db, "Пожарный режим");
        ParamGroupEditing.Move(_db, ParamGroupCatalog.FactoryReset, -1);
        ParamGroupEditing.Move(_db, ParamGroupCatalog.FactoryReset, -1);

        Assert.True(ParamGroupEditing.ResetToDefaults(_db).Ok);

        var groups = _db.GetParamGroups();
        Assert.Equal(ParamGroupCatalog.Main, groups[0]);
        Assert.Equal(ParamGroupCatalog.FactoryReset, groups[^1]);
        Assert.Contains("Пожарный режим", groups);
        Assert.True(groups.IndexOf("Пожарный режим") < groups.IndexOf(ParamGroupCatalog.FactoryReset));
    }

    // ── Убрать ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Remove_LeavesTheLabelOnRowsThatAlreadyUseIt()
    {
        var revisionId = RowInGroup(ParamGroupCatalog.Motor);

        Assert.True(ParamGroupEditing.Remove(_db, ParamGroupCatalog.Motor).Ok);

        Assert.DoesNotContain(ParamGroupCatalog.Motor, _db.GetParamGroups());
        Assert.Equal(ParamGroupCatalog.Motor, _db.GetParamTableRows(revisionId).Single().GroupName);
    }

    [Fact]
    public void UsedBy_CountsLiveRowsOnly()
    {
        RowInGroup(ParamGroupCatalog.Motor);

        Assert.Equal(1, ParamGroupEditing.UsedBy(_db, ParamGroupCatalog.Motor));
        // Регистр не должен мешать ответить на вопрос «на этой группе что-нибудь держится?».
        Assert.Equal(1, ParamGroupEditing.UsedBy(_db, "двигатель"));
        Assert.Equal(0, ParamGroupEditing.UsedBy(_db, ParamGroupCatalog.Protections));
    }

    [Fact]
    public void EditingIsForTheSamePeopleWhoEditTables()
    {
        Assert.True(ParamGroupEditing.CanEdit("administrator"));
        Assert.True(ParamGroupEditing.CanEdit("programmer"));
        Assert.False(ParamGroupEditing.CanEdit("naladchik"));
    }
}
