using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ТИКЕТ ДОЛЖЕН ДОЙТИ САМ.
///
/// Выгрузка тикетов была ручной кнопкой, то есть существовала ровно настолько, насколько про неё
/// помнили. Тот, кто чинит программу, сидит вне конторской сети и до тикетов не достаёт: не нажали
/// — жалобы для него нет. Канал тот же, что у журналов заливок (LoaderDiagnostics), и проверяется
/// здесь то же самое, из-за чего такие каналы обычно оказываются пустыми, когда нужны: очередь
/// переживает отсутствие сети, отправленное удаляется, а НЕотправленное — нет.
///
/// В сеть тесты не ходят: страница приёма подставная (см. FakeDrop).</summary>
public class TicketAutoDropTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "antarus-tdrop-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Подставная страница приёма: отвечает заданным кодом и запоминает, что присылали.</summary>
    private sealed class FakeDrop : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public List<string> Bodies { get; } = new();

        public FakeDrop(HttpStatusCode status = HttpStatusCode.OK) => _status = status;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await (request.Content?.ReadAsStringAsync(ct) ?? Task.FromResult("")));
            return new HttpResponseMessage(_status);
        }
    }

    private static Ticket Sample(string text = "не печатается наклейка") => new()
    {
        Id = Guid.NewGuid().ToString(),
        Type = TicketType.Bug,
        Text = text,
        Status = TicketStatus.Open,
        CreatedBy = "kiselyov.a",
        CreatedByRole = "naladchik",
        CreatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        UpdatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
    };

    private static TicketAutoDrop.Entry Entry(Ticket? t = null, string eventType = "created", string? comment = null) =>
        TicketAutoDrop.EntryFor(t ?? Sample(), eventType, "stend-1", "kiselyov.a", "naladchik", "2.0.0.0", comment);

    [Fact]
    public void Запись_ложится_в_очередь()
    {
        using var dir = new TempDir();

        var path = TicketAutoDrop.Enqueue(dir.Path, Entry());

        Assert.NotNull(path);
        var pending = TicketAutoDrop.Pending(TicketAutoDrop.QueueFolder(dir.Path));
        Assert.Single(pending);
        Assert.Contains("не печатается наклейка", File.ReadAllText(pending[0]));
    }

    /// <summary>Очередь не растёт бесконечно, если сети нет месяцами: старое вытесняется новым.</summary>
    [Fact]
    public void Очередь_ограничена_сверху()
    {
        using var dir = new TempDir();
        var queue = TicketAutoDrop.QueueFolder(dir.Path);
        Directory.CreateDirectory(queue);

        for (var i = 0; i < TicketAutoDrop.MaxPending + 7; i++)
            File.WriteAllText(System.IO.Path.Combine(queue, $"2026102{i % 10}-{i:0000}-x.json"), "{}");
        TicketAutoDrop.Trim(queue);

        Assert.Equal(TicketAutoDrop.MaxPending, TicketAutoDrop.Pending(queue).Count);
    }

    [Fact]
    public async Task Отправленное_удаляется_из_очереди()
    {
        using var dir = new TempDir();
        TicketAutoDrop.Enqueue(dir.Path, Entry());
        TicketAutoDrop.Enqueue(dir.Path, Entry(Sample("шкаф не стартует"), TicketAutoDrop.Event.Status));

        var drop = new FakeDrop();
        var result = await TicketAutoDrop.FlushAsync(dir.Path, "https://example.test/drop", "код",
            new HttpClient(drop));

        Assert.True(result.Ok);
        Assert.Equal(2, result.Sent);
        Assert.Empty(TicketAutoDrop.Pending(TicketAutoDrop.QueueFolder(dir.Path)));
        Assert.All(drop.Bodies, body => Assert.Contains("код", body));
    }

    /// <summary>Отправка обязана быть МОЛЧАЛИВОЙ: поле silent=1 — это то, из-за чего страница
    /// приёма не дёргает телеграм и кладёт файл в подпапку diag/. Без него каждый заведённый тикет
    /// превращался бы в уведомление, которого никто не просил.</summary>
    [Fact]
    public async Task Отправка_идёт_молча()
    {
        using var dir = new TempDir();
        TicketAutoDrop.Enqueue(dir.Path, Entry());

        var drop = new FakeDrop();
        await TicketAutoDrop.FlushAsync(dir.Path, "https://example.test/drop", "код", new HttpClient(drop));

        Assert.Contains("name=silent", drop.Bodies[0].Replace("\"", ""));
        Assert.Contains("1", drop.Bodies[0]);
    }

    /// <summary>САМОЕ ВАЖНОЕ. Страница не ответила — записи обязаны остаться: иначе первая же
    /// отключённая сеть стирала бы ровно ту жалобу, ради которой всё и заведено.</summary>
    [Fact]
    public async Task Неотправленное_остаётся_в_очереди()
    {
        using var dir = new TempDir();
        TicketAutoDrop.Enqueue(dir.Path, Entry());

        var result = await TicketAutoDrop.FlushAsync(dir.Path, "https://example.test/drop", "код",
            new HttpClient(new FakeDrop(HttpStatusCode.ServiceUnavailable)));

        Assert.False(result.Ok);
        Assert.Equal(0, result.Sent);
        Assert.Single(TicketAutoDrop.Pending(TicketAutoDrop.QueueFolder(dir.Path)));
    }

    /// <summary>Канал не настроен — молчим и ничего не трогаем. Пустой адрес это не ошибка, а
    /// выключенная отправка.</summary>
    [Fact]
    public async Task Без_адреса_ничего_не_происходит()
    {
        using var dir = new TempDir();
        TicketAutoDrop.Enqueue(dir.Path, Entry());

        var result = await TicketAutoDrop.FlushAsync(dir.Path, "", "код", new HttpClient(new FakeDrop()));

        Assert.True(result.Ok);
        Assert.Equal(0, result.Sent);
        Assert.Single(TicketAutoDrop.Pending(TicketAutoDrop.QueueFolder(dir.Path)));
    }

    /// <summary>Очередь тикетов и очередь журналов заливок — РАЗНЫЕ папки: это разные потоки с
    /// разной судьбой, и смешав их, нельзя было бы вычистить один, не тронув второй.</summary>
    [Fact]
    public void Очередь_не_пересекается_с_журналами_заливок()
    {
        using var dir = new TempDir();

        Assert.NotEqual(TicketAutoDrop.QueueFolder(dir.Path), LoaderDiagnostics.QueueFolder(dir.Path));
    }

    /// <summary>Реплика едет вместе с тикетом: обсуждение без ответа той стороны — половина
    /// разговора.</summary>
    [Fact]
    public void Реплика_попадает_в_запись()
    {
        using var dir = new TempDir();

        TicketAutoDrop.Enqueue(dir.Path, Entry(Sample(), TicketAutoDrop.Event.Comment, "проверь на второй машине"));

        var text = File.ReadAllText(TicketAutoDrop.Pending(TicketAutoDrop.QueueFolder(dir.Path)).Single());
        Assert.Contains("проверь на второй машине", text);
        Assert.Contains(TicketAutoDrop.Event.Comment, text);
    }
}
