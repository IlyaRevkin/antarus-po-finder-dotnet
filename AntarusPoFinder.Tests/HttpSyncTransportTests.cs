using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Обмен общим конфигом по HTTP — второй канал рядом с сетевым диском.
///
/// Нужен он не ради красоты архитектуры: сетевой диск виден только внутри корпоративной сети, а
/// наладчики работают снаружи. Без общего конфига у машины снаружи не обновляется КАТАЛОГ — она не
/// видит новых прошивок вовсе, сколько файлов ей ни дай. Поэтому первым по HTTP уезжает конфиг.
///
/// Проверяется здесь то, что нельзя увидеть глазами и что ломается тихо:
///   • ненастроенный адрес — это «канала нет», а не исключение на ровном месте;
///   • чтение прощает всё (нет объекта, лежит сервер, битый JSON) — ровно как отвалившаяся шара;
///   • запись НЕ прощает ничего: молча потерянная отправка means «изменений не увидит никто», а
///     человек уверен, что отправил;
///   • наружу уходят только GET и POST — глаголы WebDAV корпоративные прокси режут.</summary>
public class HttpSyncTransportTests
{
    private static SyncServerSettings Settings(string url = "https://obmen.example.test/api") =>
        new(url, "kluch-123");

    /// <summary>Подставной сервер: отвечает заготовленным и запоминает, что у него спросили.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, byte[] Body)> _answers;

        public FakeServer(params (HttpStatusCode Status, byte[] Body)[] answers) =>
            _answers = new Queue<(HttpStatusCode, byte[])>(answers);

        public List<(string Method, string Url, string? Key, byte[] Body)> Seen { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(ct);
            request.Headers.TryGetValues(HttpSyncTransport.KeyHeader, out var keys);

            Seen.Add((request.Method.Method, request.RequestUri!.AbsoluteUri, keys?.FirstOrDefault(), body));

            var (status, answer) = _answers.Count > 0
                ? _answers.Dequeue()
                : (HttpStatusCode.OK, Array.Empty<byte>());
            return new HttpResponseMessage(status) { Content = new ByteArrayContent(answer) };
        }
    }

    private static HttpSyncTransport Transport(FakeServer server, SyncServerSettings? settings = null) =>
        new(settings ?? Settings(), new HttpClient(server));

    private static byte[] Json(string s) => Encoding.UTF8.GetBytes(s);

    // ── Ненастроенный канал ───────────────────────────────────────────────────

    /// <summary>Пустой адрес — штатное состояние машины, которая работает через сетевой диск. Никаких
    /// исключений и никаких запросов: канала просто нет.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("сервер конторы")]      // человек вписал не адрес
    [InlineData("ftp://obmen.example")] // схема, которой мы не умеем
    public async Task NotConfigured_IsSilentlyUnavailable(string url)
    {
        var server = new FakeServer();
        var transport = Transport(server, new SyncServerSettings(url, "kluch"));

        Assert.False(new SyncServerSettings(url, "kluch").IsConfigured);
        Assert.False(await transport.IsAvailableAsync());
        Assert.Null(await transport.ReadRevisionAsync());
        Assert.Null(await transport.ReadConfigAsync());
        Assert.Empty(server.Seen);
    }

    /// <summary>А вот ЗАПИСЬ в ненастроенный канал молчать не имеет права: «отправил, и ничего не
    /// уехало» — худший из возможных исходов.</summary>
    [Fact]
    public async Task NotConfigured_WriteThrows()
    {
        var transport = Transport(new FakeServer(), new SyncServerSettings("", ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.WriteConfigAsync(new byte[] { 1 }));
    }

    // ── Чтение ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadRevision_ParsesMarker_AndSendsTheKey()
    {
        var server = new FakeServer((HttpStatusCode.OK, Json(
            """{"Revision":42,"ExportedAt":"2026-08-13T12:00:00","ExportedBy":"revkin.i","Changes":[]}""")));

        var marker = await Transport(server).ReadRevisionAsync();

        Assert.Equal(42, marker!.Revision);
        Assert.Equal("revkin.i", marker.ExportedBy);
        var (method, url, key, _) = Assert.Single(server.Seen);
        Assert.Equal("GET", method);
        Assert.Equal("https://obmen.example.test/api/revision", url);
        Assert.Equal("kluch-123", key);
    }

    /// <summary>Объекта ещё нет (никто не экспортировал), сервер лёг, ответ битый — для клиента это
    /// одно и то же: «надёжных сведений нет». Так же ведёт себя и файловая шара.</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.OK, "не json вовсе")]
    public async Task ReadRevision_ForgivesEverything(HttpStatusCode status, string body)
    {
        var server = new FakeServer((status, Json(body)));
        Assert.Null(await Transport(server).ReadRevisionAsync());
    }

    [Fact]
    public async Task ReadConfig_ReturnsRawBytes_Undecrypted()
    {
        // Транспорт про шифрование не знает и знать не должен — возит байты как есть.
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var server = new FakeServer((HttpStatusCode.OK, payload));

        Assert.Equal(payload, await Transport(server).ReadConfigAsync());
        Assert.Equal("https://obmen.example.test/api/config", server.Seen.Single().Url);
    }

    [Fact]
    public async Task ReadConfig_MissingObject_IsNull_NotAnError()
    {
        var server = new FakeServer((HttpStatusCode.NotFound, Array.Empty<byte>()));
        Assert.Null(await Transport(server).ReadConfigAsync());
    }

    // ── Запись ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteConfig_PostsBytesAsIs()
    {
        var server = new FakeServer((HttpStatusCode.OK, Array.Empty<byte>()));
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        await Transport(server).WriteConfigAsync(payload);

        var (method, url, key, body) = Assert.Single(server.Seen);
        Assert.Equal("POST", method);
        Assert.Equal("https://obmen.example.test/api/config", url);
        Assert.Equal("kluch-123", key);
        Assert.Equal(payload, body);
    }

    /// <summary>Сервер не принял — вызывающий обязан узнать. Это главное отличие записи от чтения.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Write_Failure_Throws(HttpStatusCode status)
    {
        var server = new FakeServer((status, Array.Empty<byte>()), (status, Array.Empty<byte>()));
        var transport = Transport(server);

        await Assert.ThrowsAsync<HttpRequestException>(() => transport.WriteConfigAsync(new byte[] { 1 }));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            transport.WriteRevisionAsync(new SyncRevisionMarker { Revision = 1 }));
    }

    [Fact]
    public async Task WriteRevision_SendsMarkerAsJson()
    {
        var server = new FakeServer((HttpStatusCode.OK, Array.Empty<byte>()));

        await Transport(server).WriteRevisionAsync(new SyncRevisionMarker
        {
            Revision = 7,
            ExportedBy = "revkin.i",
            Changes = { new SyncChangeEntry { Description = "залита прошивка", Revision = 7 } },
        });

        // Сверяем не текст, а СМЫСЛ: System.Text.Json по умолчанию экранирует кириллицу в \uXXXX
        // (ровно так же пишет маркер и файловый транспорт), поэтому искать подстроку в теле бесполезно
        // — важно, что принимающая сторона прочитает то же самое, что мы отправили.
        var sent = System.Text.Json.JsonSerializer.Deserialize<SyncRevisionMarker>(
            Encoding.UTF8.GetString(server.Seen.Single().Body));
        Assert.Equal(7, sent!.Revision);
        Assert.Equal("залита прошивка", sent.Changes.Single().Description);
    }

    // ── Совместимость с прокси ────────────────────────────────────────────────

    /// <summary>Наружу уходят ТОЛЬКО GET и POST. Корпоративные прокси регулярно режут PUT, DELETE и
    /// глаголы WebDAV (PROPFIND, MKCOL), а отладить это с рабочей машины почти невозможно — поэтому
    /// правило проверяется тестом, а не держится на памяти.</summary>
    [Fact]
    public async Task OnlyGetAndPost_EverLeaveTheMachine()
    {
        var server = new FakeServer(
            (HttpStatusCode.OK, Json("""{"Revision":1}""")),
            (HttpStatusCode.OK, Array.Empty<byte>()),
            (HttpStatusCode.OK, Array.Empty<byte>()),
            (HttpStatusCode.OK, Array.Empty<byte>()),
            (HttpStatusCode.OK, Array.Empty<byte>()));
        var transport = Transport(server);

        await transport.IsAvailableAsync();
        await transport.ReadRevisionAsync();
        await transport.ReadConfigAsync();
        await transport.WriteConfigAsync(new byte[] { 1 });
        await transport.WriteRevisionAsync(new SyncRevisionMarker());

        Assert.All(server.Seen, r => Assert.Contains(r.Method, new[] { "GET", "POST" }));
    }

    /// <summary>Хвостовой слеш в адресе — самая частая опечатка в поле настройки, и удваивать из-за
    /// неё слеш в пути нельзя: часть серверов на «//config» отвечает 404.</summary>
    [Fact]
    public async Task TrailingSlashInAddress_DoesNotDoubleUp()
    {
        var server = new FakeServer((HttpStatusCode.OK, Array.Empty<byte>()));
        await Transport(server, new SyncServerSettings("https://obmen.example.test/api/", "k")).ReadConfigAsync();
        Assert.Equal("https://obmen.example.test/api/config", server.Seen.Single().Url);
    }

    /// <summary>Сервер лежит целиком (соединение не устанавливается) — это «канала нет», и падать
    /// программа не должна: она обязана продолжить работать через сетевой диск.</summary>
    [Fact]
    public async Task DeadServer_IsUnavailable_NotACrash()
    {
        var transport = new HttpSyncTransport(Settings(), new HttpClient(new ThrowingHandler()));

        Assert.False(await transport.IsAvailableAsync());
        Assert.Null(await transport.ReadRevisionAsync());
        Assert.Null(await transport.ReadConfigAsync());
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("соединение не установлено");
    }
}
