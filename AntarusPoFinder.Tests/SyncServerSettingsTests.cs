using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Настройка канала до службы обмена (tools/sync-server) и проверка связи с ней.
///
/// До этого захода в интерфейсе висела заглушка: переключатель «папка/сервер», который ничего не
/// переключал, поле адреса, которое никуда не шло, и ни одного поля для ключа. Здесь закрепляется
/// то, что пришло ей на смену.</summary>
public class SyncServerSettingsTests
{
    [Fact]
    public void КлючСохраняетсяИЧитаетсяОбратно()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        Assert.Equal("", cfg.ServerKey());

        cfg.SetServerKey("  a1b2c3d4e5f6  ");
        Assert.Equal("a1b2c3d4e5f6", cfg.ServerKey());

        cfg.SetServerKey("");
        Assert.Equal("", cfg.ServerKey());
    }

    /// <summary>Ключ не должен лежать в базе открытым текстом: файл базы попадает и на скриншоты,
    /// и в чужие руки при разборе жалоб.</summary>
    [Fact]
    public void КлючЛежитВБазеЗашифрованным()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        cfg.SetServerKey("очень-секретный-ключ");

        var raw = cfg.Get("server_key");
        Assert.StartsWith("enc:", raw);
        Assert.DoesNotContain("очень-секретный-ключ", raw);
        Assert.Equal("очень-секретный-ключ", cfg.ServerKey());
    }

    /// <summary>Ключ у каждой машины свой — в этом весь смысл опознания на стороне службы. Если бы
    /// он уезжал в общий конфиг, все машины ходили бы под одним именем, и отозвать доступ у одной
    /// стало бы невозможно.</summary>
    [Fact]
    public void КлючНеУезжаетНаДругиеМашины()
    {
        var skipped = ConfigSyncSkipKeys.Read();
        Assert.Contains("server_key", skipped);
        Assert.Contains("server_url", skipped);
    }

    [Fact]
    public void АдресИКлючОтдаютсяОднойПарой()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        cfg.SetServerUrl("https://ant-srv:8443/");
        cfg.SetServerKey("kluch");

        var settings = cfg.SyncServer();
        Assert.True(settings.IsConfigured);
        Assert.Equal("https://ant-srv:8443", settings.Root);
        Assert.Equal("kluch", settings.AccessKey);
    }

    // ── Проверка связи ───────────────────────────────────────────────────────

    private sealed class Answer : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public string? SeenKey { get; private set; }
        public string? SeenUrl { get; private set; }

        public Answer(HttpStatusCode status, string body = "")
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            SeenUrl = request.RequestUri!.AbsoluteUri;
            if (request.Headers.TryGetValues(HttpSyncTransport.KeyHeader, out IEnumerable<string>? keys))
                SeenKey = System.Linq.Enumerable.FirstOrDefault(keys);
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private const string GoodPing =
        """
        {"service":"antarus-sync","version":"1.0.0","server":"ANT-SRV",
         "client":"naladchik-1","can_write":false,"revision":42,"has_config":true}
        """;

    [Fact]
    public async Task ПроверкаСтучитсяВPingСКлючом()
    {
        var handler = new Answer(HttpStatusCode.OK, GoodPing);
        var result = await SyncServerProbe.CheckAsync(
            new SyncServerSettings("https://ant-srv:8443", "kluch-1"), new HttpClient(handler));

        Assert.Equal("https://ant-srv:8443/ping", handler.SeenUrl);
        Assert.Equal("kluch-1", handler.SeenKey);
        Assert.True(result.Ok);
    }

    /// <summary>Ответ обязан сказать не «сервер жив», а кем нас признали и что разрешено — иначе
    /// проверка не отвечает на вопрос, ради которого её нажимают.</summary>
    [Fact]
    public async Task УспехНазываетМашинуИПрава()
    {
        var result = await SyncServerProbe.CheckAsync(
            new SyncServerSettings("https://ant-srv:8443", "kluch-1"),
            new HttpClient(new Answer(HttpStatusCode.OK, GoodPing)));

        Assert.True(result.Ok);
        Assert.Contains("ANT-SRV", result.Message);
        Assert.Contains("naladchik-1", result.Message);
        Assert.Contains("только чтение", result.Message);
        Assert.Contains("42", result.Message);
    }

    [Fact]
    public async Task ПраваНаЗаписьПоказываютсяОтдельно()
    {
        const string writer =
            """
            {"service":"antarus-sync","version":"1.0.0","server":"ANT-SRV",
             "client":"admin-1","can_write":true,"revision":7,"has_config":true}
            """;
        var result = await SyncServerProbe.CheckAsync(
            new SyncServerSettings("https://ant-srv:8443", "k"),
            new HttpClient(new Answer(HttpStatusCode.OK, writer)));

        Assert.Contains("чтение и отправка", result.Message);
    }

    /// <summary>401 и 403 лечатся по-разному: «вставили не тот ключ» и «доступ отобрали на сервере».
    /// Сообщение обязано различать их, иначе разбор упрётся в гадание.</summary>
    [Fact]
    public async Task НеопознанныйКлючИОтключённыйДоступРазличаются()
    {
        var wrongKey = await SyncServerProbe.CheckAsync(
            new SyncServerSettings("https://ant-srv:8443", "k"),
            new HttpClient(new Answer(HttpStatusCode.Unauthorized)));
        Assert.False(wrongKey.Ok);
        Assert.Contains("ключ не опознан", wrongKey.Message);

        var disabled = await SyncServerProbe.CheckAsync(
            new SyncServerSettings("https://ant-srv:8443", "k"),
            new HttpClient(new Answer(HttpStatusCode.Forbidden)));
        Assert.False(disabled.Ok);
        Assert.Contains("отключён", disabled.Message);
    }

    [Fact]
    public async Task ЧужойСервисПоАдресуНеСчитаетсяСвязью()
    {
        var result = await SyncServerProbe.CheckAsync(
            new SyncServerSettings("https://ant-srv:8443", "k"),
            new HttpClient(new Answer(HttpStatusCode.OK, """{"service":"nginx","version":"1.2"}""")));

        Assert.False(result.Ok);
        Assert.Contains("не служба обмена", result.Message);
    }

    [Fact]
    public async Task НезаполненныеПоляНеШлютЗапрос()
    {
        var noUrl = await SyncServerProbe.CheckAsync(new SyncServerSettings("", "k"));
        Assert.False(noUrl.Ok);
        Assert.Contains("Адрес", noUrl.Message);

        var noKey = await SyncServerProbe.CheckAsync(new SyncServerSettings("https://ant-srv:8443", ""));
        Assert.False(noKey.Ok);
        Assert.Contains("Ключ", noKey.Message);
    }

    [Fact]
    public async Task ЧетырестаЧетыреГоворитПроАдресИПорт()
    {
        var result = await SyncServerProbe.CheckAsync(
            new SyncServerSettings("https://ant-srv:8443", "k"),
            new HttpClient(new Answer(HttpStatusCode.NotFound)));

        Assert.False(result.Ok);
        Assert.Contains("8443", result.Message);
    }
}
