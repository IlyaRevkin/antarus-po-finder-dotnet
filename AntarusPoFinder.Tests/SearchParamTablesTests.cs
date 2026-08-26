using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Поиск по документам-таблицам параметров (Database.SearchParamTablesByTokens).
///
/// Жалоба владельца: «В поиске не найти». Таблицы не участвовали в поиске вообще — ни по названию,
/// ни по тегам, ни по содержимому. Содержимое здесь главное: наладчик на объекте ищет «P0-10» или
/// «максимальная частота», а названия документа он не помнит.</summary>
public class SearchParamTablesTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly Database _db;

    public SearchParamTablesTests() => _db = new Database(_dbFile.Path);

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
    }

    private static ParamTableRow P(string code, string title, string value = "", string description = "") => new()
    {
        Kind = ParamRowKind.Param, Code = code, Title = title, Value = value,
        ValueState = value.Length > 0 ? ParamValueState.Set : ParamValueState.OnSite,
        Description = description, GroupName = ParamGroupCatalog.Main,
    };

    private int SeedTable(string name, string tags = "", string file = "ESQ-230.par", params ParamTableRow[] rows)
    {
        var id = _db.AddParamTable(new ParamTable
        {
            DiskPath = @"Z:\ПО\Параметры\ESQ", Filename = file, Name = name, Manufacturer = "ESQ", Tags = tags,
        });
        ParamTableEditing.SaveRevision(_db, id, rows, "первая", "Ilia");
        return id;
    }

    private List<Database.ParamTableHit> Find(string query, bool exact = false) =>
        _db.SearchParamTablesByTokens(
            SearchService.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries), exact);

    [Fact]
    public void FoundByDocumentName()
    {
        SeedTable("Задание Modbus", rows: P("P0-02", "Выбор канала"));

        Assert.Equal("Задание Modbus", Assert.Single(Find("модбас модбус задание")).Table.Name);
    }

    [Fact]
    public void FoundByParameterCode_AndTheMatchingRowComesBack()
    {
        // Это и есть главный случай: наладчик помнит код, а не название документа. И ответ без
        // строки бесполезен — открывать документ и искать глазами заново он не должен.
        SeedTable("Задание Modbus", rows: new[]
        {
            P("P0-02", "Выбор канала команды запуска", "2"),
            P("P0-10", "Максимальная частота", "55"),
        });

        var hit = Assert.Single(Find("P0-10"));
        Assert.Equal("P0-10", Assert.Single(hit.Rows).Code);
    }

    [Fact]
    public void FoundByParameterTitle()
    {
        SeedTable("Задание Modbus", rows: P("P0-10", "Максимальная частота", "55"));

        var hit = Assert.Single(Find("максимальная частота"));
        Assert.Equal("P0-10", hit.Rows[0].Code);
    }

    [Fact]
    public void FoundByDescription()
    {
        SeedTable("Задание Modbus", rows: P("PD-01", "Формат данных", "3", "восьмибитный без чётности"));

        Assert.Single(Find("восьмибитный"));
    }

    [Fact]
    public void FoundByTags()
    {
        SeedTable("Пуск по месту", tags: "пожарный, дробилка", rows: P("P0-02", "Выбор канала"));

        Assert.Single(Find("дробилка"));
    }

    [Fact]
    public void NameMatch_OutranksAScatterOfRowMatches()
    {
        // Документ, названный искомым словом, обязан стоять выше документа, где это слово просто
        // встречается в сорока строках. Поэтому счёт по строкам считается ОДИН РАЗ НА СЛОВО, а не
        // по числу совпавших строк.
        SeedTable("Пуск по месту", file: "a.par", rows: P("P0-02", "Выбор канала"));
        SeedTable("Задание Modbus", file: "b.par", rows: Enumerable.Range(1, 8)
            .Select(i => P($"P9-0{i}", $"Пуск ступени {i}")).ToArray());

        var hits = Find("пуск");

        Assert.Equal(2, hits.Count);
        Assert.Equal("Пуск по месту", hits[0].Table.Name);
    }

    [Fact]
    public void OnlyTheLatestLiveRevisionIsSearched()
    {
        // Прежние редакции — прошлое. Находка в значении, снятом два года назад, сбивала бы с толку.
        var id = SeedTable("Задание Modbus", rows: P("P0-10", "Максимальная частота", "50"));
        // ⚠️ Время заведения ставится РУКАМИ. created_at хранится с точностью до секунды, и две
        // редакции, заведённые в одну секунду, ParamTableNumbering разводит по sync_id — то есть по
        // случайному GUID. В жизни правки так не идут, а тест на этом флачил через раз.
        _db.AddParamTableRevision(new ParamTableRevision
        {
            TableId = id,
            Number = _db.NextParamTableRevisionNumber(id),
            Reason = "поправили",
            Author = "Ilia",
            CreatedAt = DateTime.Now.AddMinutes(1).ToString("yyyy-MM-dd HH:mm:ss"),
            Rows = new() { P("P0-10", "Верхний предел частоты", "55") },
        });

        Assert.Empty(Find("максимальная"));
        Assert.Single(Find("верхний предел"));
    }

    [Fact]
    public void RemovedDocument_IsNotFound()
    {
        var id = SeedTable("Задание Modbus", rows: P("P0-10", "Максимальная частота"));
        _db.TombstoneParamTable(id);

        Assert.Empty(Find("максимальная"));
    }

    [Fact]
    public void ExactWord_DoesNotMatchAPartOfAWord()
    {
        SeedTable("Задание Modbus", rows: P("P0-10", "Максимальная частота"));

        Assert.Single(Find("частот"));
        Assert.Empty(Find("частот", exact: true));
        Assert.Single(Find("частота", exact: true));
    }

    [Fact]
    public void SubtypesComeWithTheHit()
    {
        // Одинаковых по названию «Заданий Modbus» на разные шкафы бывает несколько — без типа и
        // подтипа выдача не отвечает, какое из них нужное.
        var subtype = _db.GetAllEquipmentSubtypes().First();
        var group = _db.GetAllEquipmentGroups().First(g => g.Id == subtype.GroupId);
        _db.AddParamFile(new ParamFile
        {
            SubtypeId = subtype.Id, Manufacturer = "ESQ", Filename = "ESQ-230.par",
            DiskPath = @"Z:\ПО\Параметры\ESQ", UploadDate = "2026-08-26 10:00:00",
        });
        SeedTable("Задание Modbus", rows: P("P0-10", "Максимальная частота"));

        Assert.Contains(group.Name, Assert.Single(Find("максимальная")).Subtypes);
    }

    [Fact]
    public void NothingMatches_IsAnEmptyList_NotAnError()
    {
        SeedTable("Задание Modbus", rows: P("P0-10", "Максимальная частота"));

        Assert.Empty(Find("бетономешалка"));
        Assert.Empty(Find(""));
    }
}
