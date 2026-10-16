using System;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ТАБЛИЦА ПАРАМЕТРОВ БЕЗ ФАЙЛА (бета 2.0).
///
/// Решение Ильи 16.10.2026: «в бете мы делаем её как будто бы продукт рабочий и не зависим от
/// старых структур, соответственно параметры ПЧ УПП всегда таблица, не зависим от файлов, чиста
/// старого диска и тп не нужна».
///
/// Раньше документ существовал только «рядом с .par-файлом»: пара диск+имя была его адресом. Это
/// наследство файлового порядка — наладчику нужны ЗНАЧЕНИЯ, а не то, из какого файла их когда-то
/// вычитали.
///
/// Здесь проверяется, что свободная таблица — полноправная запись: она находится, у неё работают
/// ревизии и теги, её можно отвязать от файла, не потеряв, и удаление по-прежнему разъезжается
/// тумбстоуном (без этого «у меня две записи, у коллеги четыре» вернулось бы в прежнем виде).</summary>
public class StandaloneParamTableTests
{
    private static int AddFree(Database db, string name, string manufacturer = "Danfoss") =>
        db.AddParamTable(new ParamTable { Name = name, Manufacturer = manufacturer });

    [Fact]
    public void Таблица_без_файла_заводится_и_находится()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);

        var id = AddFree(db, "Пуск по месту");

        var free = db.GetStandaloneParamTables();
        Assert.Contains(free, t => t.Id == id && t.Name == "Пуск по месту");
        // И в общем списке тоже: это обычный документ, просто без привязки.
        Assert.Contains(db.GetParamTables(), t => t.Id == id);
    }

    /// <summary>Привязанные к файлу документы в список свободных не попадают: иначе «таблицы без
    /// файла» означало бы «все таблицы подряд» и отбор терял бы смысл.</summary>
    [Fact]
    public void Привязанные_к_файлу_в_свободные_не_попадают()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);

        db.AddParamTable(new ParamTable
        {
            Name = "Задание Modbus",
            DiskPath = @"Z:\Software\Параметры\Danfoss",
            Filename = "fc51.par",
        });

        Assert.Empty(db.GetStandaloneParamTables());
    }

    /// <summary>Отвязка оставляет САМ документ: файл могли удалить или переложить, а значения
    /// параметров нужны по-прежнему. Удалять документ ради этого — потерять работу наладчика.</summary>
    [Fact]
    public void Отвязка_от_файла_сохраняет_документ()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);
        var id = db.AddParamTable(new ParamTable
        {
            Name = "Пуск по месту",
            DiskPath = @"Z:\Software\Параметры\Danfoss",
            Filename = "fc51.par",
        });

        db.DetachParamTableFromFile(id);

        var table = db.GetParamTable(id);
        Assert.NotNull(table);
        Assert.Equal("Пуск по месту", table!.Name);
        Assert.Equal("", table.DiskPath);
        Assert.Equal("", table.Filename);
        Assert.Contains(db.GetStandaloneParamTables(), t => t.Id == id);
    }

    /// <summary>Теги у свободной таблицы работают так же: по ним документ и находят, привязка к
    /// файлу в этом не участвует вовсе.</summary>
    [Fact]
    public void Теги_работают_и_без_файла()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);
        var id = AddFree(db, "Пуск по месту");

        db.UpdateParamTableTags(id, TagString.Join(new[] { "насос", "ПЧ" }));

        var tags = TagString.Parse(db.GetParamTable(id)!.Tags);
        Assert.Contains("насос", tags);
        Assert.Contains("ПЧ", tags);
    }

    /// <summary>Удаление разъезжается тумбстоуном, а не исчезает молча: иначе удаление жило бы на
    /// одной машине, а у коллег документ висел бы вечно — ровно жалоба «у меня 2 записи, у коллеги
    /// 4», ради которой тумбстоуны и заводились.</summary>
    [Fact]
    public void Удаление_свободной_таблицы_остаётся_тумбстоуном()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);
        var id = AddFree(db, "Пуск по месту");

        db.TombstoneParamTable(id);

        Assert.DoesNotContain(db.GetStandaloneParamTables(), t => t.Id == id);
        Assert.DoesNotContain(db.GetParamTables(), t => t.Id == id);
    }
}
