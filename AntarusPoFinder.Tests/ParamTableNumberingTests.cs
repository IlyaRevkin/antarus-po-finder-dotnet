using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Номер ревизии, который видит человек (ParamTableNumbering).
///
/// Задача, ради которой это заведено: хранимый номер присваивает заведшая машина, между машинами он
/// не уникален, и две машины, не видевшие правок друг друга, обе заводят «ревизию 3». После обмена
/// конфигом в одном документе оказываются две третьих — а список ревизий читают глазами как
/// историю. Показываемый номер поэтому считается от порядка по времени заведения.</summary>
public class ParamTableNumberingTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly Database _db;

    public ParamTableNumberingTests() => _db = new Database(_dbFile.Path);

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
    }

    private static ParamTableRevision R(int number, string createdAt, string syncId = "", string deletedAt = "") => new()
    {
        Number = number, CreatedAt = createdAt, DeletedAt = deletedAt,
        SyncId = syncId.Length > 0 ? syncId : "sync-" + number + "-" + createdAt,
    };

    [Fact]
    public void TwoIndependentThirds_BecomeAnHonestSequence()
    {
        // Ровно тот случай, из-за которого всё и затевалось: у каждой машины была своя «третья».
        var mine = R(3, "2026-08-25 09:00:00", "a");
        var theirs = R(3, "2026-08-25 11:00:00", "b");

        ParamTableNumbering.Apply(new[] { R(1, "2026-08-24 08:00:00", "z1"), R(2, "2026-08-24 09:00:00", "z2"), theirs, mine });

        Assert.Equal(3, mine.DisplayNumber);
        Assert.Equal(4, theirs.DisplayNumber);
    }

    [Fact]
    public void TheOrderIsTheSameOnBothMachines_EvenWhenTimestampsCollide()
    {
        // created_at хранится с точностью до секунды, и две ревизии в одну секунду (импорт, быстрая
        // правка) без второго ключа разложились бы на разных машинах по-разному. sync_id одинаков
        // везде — с ним одинаков и порядок.
        var one = R(1, "2026-08-25 09:00:00", "b-second");
        var two = R(7, "2026-08-25 09:00:00", "a-first");

        ParamTableNumbering.Apply(new[] { one, two });
        Assert.Equal(1, two.DisplayNumber);
        Assert.Equal(2, one.DisplayNumber);

        // Тот же набор, поданный в другом порядке, — тот же результат.
        ParamTableNumbering.Apply(new[] { two, one });
        Assert.Equal(1, two.DisplayNumber);
        Assert.Equal(2, one.DisplayNumber);
    }

    [Fact]
    public void ASnattedRevision_LeavesAGapInsteadOfShiftingTheRest()
    {
        // Тумбстоун ездит в общем конфиге, то есть виден всем. Выкинь его из счёта — и номера
        // уцелевших поехали бы у того, до кого снятие ещё не доехало.
        var first = R(1, "2026-08-24 08:00:00", "a");
        var gone = R(2, "2026-08-24 09:00:00", "b", deletedAt: "2026-08-25 10:00:00");
        var last = R(3, "2026-08-24 10:00:00", "c");

        ParamTableNumbering.Apply(new[] { first, gone, last });

        Assert.Equal(1, first.DisplayNumber);
        Assert.Equal(3, last.DisplayNumber);
    }

    [Fact]
    public void TheLabelMentionsTheAuthorsNumberOnlyWhenItDiffers()
    {
        Assert.Equal("3", ParamTableNumbering.Label(new ParamTableRevision { Number = 3, DisplayNumber = 3 }));
        // Под старым номером ревизия уже названа в чужих Summary и в разговоре до обмена —
        // оборвать эту ниточку молча нельзя.
        Assert.Equal("4 (заведена как 3)", ParamTableNumbering.Label(new ParamTableRevision { Number = 3, DisplayNumber = 4 }));
    }

    [Fact]
    public void LiveRevisionsComeBackNewestFirst_NumberedFromTheDatabase()
    {
        var tableId = _db.AddParamTable(new ParamTable { DiskPath = @"D:\ПО", Filename = "x.par", Name = "Задание" });
        var first = _db.AddParamTableRevision(new ParamTableRevision
        {
            TableId = tableId, Number = 1, CreatedAt = "2026-08-24 08:00:00", SyncId = "a",
        });
        // Ревизия с чужой машины: номер у неё тоже первый, а завели её позже.
        var second = _db.AddParamTableRevision(new ParamTableRevision
        {
            TableId = tableId, Number = 1, CreatedAt = "2026-08-25 08:00:00", SyncId = "b",
        });

        var live = ParamTableNumbering.LiveRevisions(_db, tableId);

        Assert.Equal(new[] { second, first }, live.Select(r => r.Id!.Value));
        Assert.Equal(new[] { 2, 1 }, live.Select(r => r.DisplayNumber));
    }

    [Fact]
    public void ANewRevisionIsComparedWithTheLatestOne_ByTimeNotByStoredNumber()
    {
        var tableId = _db.AddParamTable(new ParamTable { DiskPath = @"D:\ПО", Filename = "x.par", Name = "Задание" });
        _db.AddParamTableRevision(new ParamTableRevision
        {
            TableId = tableId, Number = 9, CreatedAt = "2026-08-20 08:00:00", SyncId = "old",
            Rows = new List<ParamTableRow> { new() { Code = "P0-10", Value = "50", GroupName = ParamGroupCatalog.Main } },
        });
        _db.AddParamTableRevision(new ParamTableRevision
        {
            TableId = tableId, Number = 2, CreatedAt = "2026-08-24 08:00:00", SyncId = "fresh",
            Rows = new List<ParamTableRow> { new() { Code = "P0-10", Value = "55", GroupName = ParamGroupCatalog.Main } },
        });

        var (_, diff) = ParamTableEditing.SaveRevision(_db, tableId,
            new[] { new ParamTableRow { Code = "P0-10", Value = "60", GroupName = ParamGroupCatalog.Main } },
            "объект попросил 60", "Ilia");

        // Сравнивать надо со СВЕЖЕЙ (55 → 60), а не с той, у которой номер больше (50 → 60):
        // номера приезжают с чужих машин и упорядочить историю не могут.
        var change = Assert.Single(diff.Changes);
        Assert.Equal("55", change.Before!.Value);
    }
}
