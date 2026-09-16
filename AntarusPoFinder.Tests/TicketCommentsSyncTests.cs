using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Переписка по тикету ездит между машинами.
///
/// Тикет: «добавить возможность добавлять комментарии к тикетам». Смысл в том, чтобы обсуждение
/// доезжало до всех — иначе оно ничем не лучше разговора мимо программы. Реплики лежат ВНУТРИ
/// объекта тикета, а не отдельными объектами: их всегда читают вместе с тикетом, и видеть тикет без
/// половины обсуждения нельзя.
///
/// Реплики только добавляются, поэтому слияние — объединение по идентификатору, без разбора «чья
/// новее». Проверяется здесь именно это: встречные реплики двух машин обязаны сойтись в обе
/// стороны, а не вытеснить друг друга.</summary>
public class TicketCommentsSyncTests
{
    private sealed class FakeStorage : ITicketStorage
    {
        private readonly Dictionary<string, string> _objects = new(StringComparer.Ordinal);
        private int _version;

        public bool CanSync => true;

        public Task<TicketStorageListing> ListAsync(string prefix, CancellationToken ct = default) =>
            Task.FromResult(TicketStorageListing.Success(_objects.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Select(k => new TicketStorageEntry(k, _objects[k].Length, null, _objects[k].GetHashCode().ToString()))
                .ToList()));

        public Task<(string? Json, string? Error)> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<(string?, string?)>(_objects.TryGetValue(key, out var v) ? (v, null) : (null, null));

        public Task<string?> PutAsync(string key, string json, CancellationToken ct = default)
        {
            _objects[key] = json;
            _version++;
            return Task.FromResult<string?>(null);
        }
    }

    private static Ticket NewTicket(string id, string at = "2026-09-16T10:00:00.000") => new()
    {
        Id = id, Type = TicketType.Bug, Text = "не печатается наклейка", Status = TicketStatus.Open,
        CreatedBy = "ivanov", CreatedByRole = "naladchik", CreatedAt = at, UpdatedAt = at,
    };

    [Fact]
    public async Task Comment_TravelsToTheOtherMachine()
    {
        using var fileA = new TempDb();
        using var fileB = new TempDb();
        using var dbA = new Database(fileA.Path);
        using var dbB = new Database(fileB.Path);
        var storage = new FakeStorage();

        dbA.InsertTicketIfMissing(NewTicket("t1"));
        dbA.AddTicketComment("t1", "ilia", "administrator", "починил, проверь");

        await TicketStorageSync.RunAsync(dbA, storage);
        await TicketStorageSync.RunAsync(dbB, storage);

        var got = dbB.GetTicketComments("t1");
        Assert.Equal("починил, проверь", Assert.Single(got).Text);
        Assert.Equal("ilia", got[0].Author);
    }

    /// <summary>Главное свойство: встречные реплики СЛИВАЮТСЯ. Каждая машина написала своё, не зная
    /// о другой, — и после обмена обе реплики должны быть у обеих. Затирание тут недопустимо: это
    /// переписка, потерянная реплика означает потерянную договорённость.</summary>
    [Fact]
    public async Task CommentsFromBothMachines_AreMerged()
    {
        using var fileA = new TempDb();
        using var fileB = new TempDb();
        using var dbA = new Database(fileA.Path);
        using var dbB = new Database(fileB.Path);
        var storage = new FakeStorage();

        dbA.InsertTicketIfMissing(NewTicket("t1"));
        await TicketStorageSync.RunAsync(dbA, storage);
        await TicketStorageSync.RunAsync(dbB, storage);

        dbA.AddTicketComment("t1", "ilia", "administrator", "с моей стороны готово");
        dbB.AddTicketComment("t1", "kiselyov.a", "naladchik", "у меня всё ещё падает");

        // Каждая сходила в хранилище по разу, потом ещё раз — чтобы забрать то, что положил сосед.
        await TicketStorageSync.RunAsync(dbA, storage);
        await TicketStorageSync.RunAsync(dbB, storage);
        await TicketStorageSync.RunAsync(dbA, storage);

        var onA = dbA.GetTicketComments("t1").Select(c => c.Text).ToList();
        var onB = dbB.GetTicketComments("t1").Select(c => c.Text).ToList();

        Assert.Contains("с моей стороны готово", onA);
        Assert.Contains("у меня всё ещё падает", onA);
        Assert.Contains("с моей стороны готово", onB);
        Assert.Contains("у меня всё ещё падает", onB);
    }

    /// <summary>Повторный обмен не задваивает реплику: она приезжает по тому же объекту снова и
    /// снова, и узнаваться должна по идентификатору.</summary>
    [Fact]
    public async Task RepeatedSync_DoesNotDuplicateComments()
    {
        using var fileA = new TempDb();
        using var fileB = new TempDb();
        using var dbA = new Database(fileA.Path);
        using var dbB = new Database(fileB.Path);
        var storage = new FakeStorage();

        dbA.InsertTicketIfMissing(NewTicket("t1"));
        dbA.AddTicketComment("t1", "ilia", "administrator", "одна реплика");
        await TicketStorageSync.RunAsync(dbA, storage);

        for (var i = 0; i < 3; i++) await TicketStorageSync.RunAsync(dbB, storage);

        Assert.Single(dbB.GetTicketComments("t1"));
    }

    /// <summary>Реплика двигает время правки тикета. На этом держится вся отдача: обмен решает,
    /// выкладывать ли объект, сравнивая время правки с тем, что видел в хранилище. Не двигай мы
    /// его — реплика осталась бы только на своей машине.</summary>
    [Fact]
    public void WritingAComment_TouchesTheTicket()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);

        db.InsertTicketIfMissing(NewTicket("t1", at: "2026-09-16T10:00:00.000"));
        db.AddTicketComment("t1", "ilia", "administrator", "реплика");

        var t = db.GetTickets().Single(x => x.Id == "t1");
        Assert.True(string.CompareOrdinal(t.UpdatedAt, "2026-09-16T10:00:00.000") > 0,
            "время правки тикета не сдвинулось: " + t.UpdatedAt);
    }
}
