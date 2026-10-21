using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ЖУРНАЛ ЗАЛИВКИ ДОЛЖЕН ДОЙТИ САМ.
///
/// Просьба Ильи 21.10.2026: «сделай чтобы логи через хранилище тебе дошли и от меня ничего не
/// требовалось». До этого причину окирпичивания SMH5 искали дважды и дважды неверно — ровно потому,
/// что следов заливки не оставалось нигде: окно показывало журнал и закрывалось вместе с ним.
///
/// Здесь проверяется то, из-за чего такой канал обычно и оказывается пустым, когда он нужен:
/// очередь переживает отсутствие сети, отправленное удаляется, а НЕотправленное — нет.</summary>
public class LoaderDiagnosticsTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "antarus-diag-" + Guid.NewGuid().ToString("N"));

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

    private static LoaderDiagnostics.Entry Entry(string controller = "SMH5") => new()
    {
        At = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        Machine = "stend-1",
        Operation = "deploy",
        Controller = controller,
        Version = "НГР-КНС 2.0.0004.0007",
        Artifact = @"Z:\ПО\НГР\КНС\SMH5\2.0.0004.0007\Прошивка\проект.lfs",
        ArtifactType = "lfs",
        Ok = true,
        Outcome = "Succeeded",
        Log = "План загрузки: Тип файла: LFS. Этапы: загрузка LFS.",
    };

    [Fact]
    public void Запись_ложится_в_очередь_и_читается_обратно()
    {
        using var dir = new TempDir();

        var path = LoaderDiagnostics.Enqueue(dir.Path, Entry());

        Assert.NotNull(path);
        var pending = LoaderDiagnostics.Pending(LoaderDiagnostics.QueueFolder(dir.Path));
        var single = Assert.Single(pending);
        var text = File.ReadAllText(single);
        Assert.Contains("SMH5", text);
        Assert.Contains("Этапы: загрузка LFS", text);
    }

    /// <summary>Очередь не растёт бесконечно: в цеху сети может не быть неделями, а место на диске
    /// кончается молча. Вытесняются САМЫЕ СТАРЫЕ — свежая заливка ближе к беде.</summary>
    [Fact]
    public void Очередь_ограничена_сверху()
    {
        using var dir = new TempDir();
        var queue = LoaderDiagnostics.QueueFolder(dir.Path);
        Directory.CreateDirectory(queue);

        for (var i = 0; i < LoaderDiagnostics.MaxPending + 7; i++)
            File.WriteAllText(Path.Combine(queue, $"2026010{i / 10}-0000{i % 10}-{i:000}.json"), "{}");

        LoaderDiagnostics.Trim(queue);

        Assert.Equal(LoaderDiagnostics.MaxPending, LoaderDiagnostics.Pending(queue).Count);
    }

    [Fact]
    public async Task Отправленное_удаляется_из_очереди()
    {
        using var dir = new TempDir();
        LoaderDiagnostics.Enqueue(dir.Path, Entry());
        LoaderDiagnostics.Enqueue(dir.Path, Entry("SMH4"));

        var drop = new FakeDrop();
        var result = await LoaderDiagnostics.FlushAsync(dir.Path, "https://example.test/drop", "код",
            new HttpClient(drop));

        Assert.True(result.Ok);
        Assert.Equal(2, result.Sent);
        Assert.Empty(LoaderDiagnostics.Pending(LoaderDiagnostics.QueueFolder(dir.Path)));
        Assert.All(drop.Bodies, body => Assert.Contains("код", body));
    }

    /// <summary>САМОЕ ВАЖНОЕ. Страница не ответила — записи обязаны остаться: иначе первая же
    /// отключённая сеть стирала бы ровно тот журнал, ради которого всё и заведено.</summary>
    [Fact]
    public async Task Неотправленное_остаётся_в_очереди()
    {
        using var dir = new TempDir();
        LoaderDiagnostics.Enqueue(dir.Path, Entry());

        var result = await LoaderDiagnostics.FlushAsync(dir.Path, "https://example.test/drop", "код",
            new HttpClient(new FakeDrop(HttpStatusCode.ServiceUnavailable)));

        Assert.False(result.Ok);
        Assert.Equal(0, result.Sent);
        Assert.Single(LoaderDiagnostics.Pending(LoaderDiagnostics.QueueFolder(dir.Path)));
    }

    /// <summary>Канал не настроен — молчим и ничего не трогаем. Пустой адрес это не ошибка, а
    /// выключенная отправка.</summary>
    [Fact]
    public async Task Без_адреса_ничего_не_происходит()
    {
        using var dir = new TempDir();
        LoaderDiagnostics.Enqueue(dir.Path, Entry());

        var result = await LoaderDiagnostics.FlushAsync(dir.Path, "", "код", new HttpClient(new FakeDrop()));

        Assert.True(result.Ok);
        Assert.Equal(0, result.Sent);
        Assert.Single(LoaderDiagnostics.Pending(LoaderDiagnostics.QueueFolder(dir.Path)));
    }

    /// <summary>Имя объекта раскладывается по дню — чтобы «что было на той неделе» читалось одним
    /// запросом с префиксом, а не выборкой из всего, что накопилось.</summary>
    [Fact]
    public void Ключ_раскладывается_по_дню()
    {
        var key = LoaderDiagnostics.KeyFor("20261021-134501-abc.json");

        Assert.StartsWith(LoaderDiagnostics.StorageFolder + "/20261021/", key);
    }
}
