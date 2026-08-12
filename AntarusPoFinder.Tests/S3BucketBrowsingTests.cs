using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Обзор бакета на странице «Хранилище» — просьба Ильи от 12.08.2026: «сделать
/// взаимодействие с файловой системой во вкладке хранилище, типа чтобы можно было посмотреть, может
/// удалить что-то — допустим, тот же мусор вручную».
///
/// До этого страница знала только то, что программа СОБИРАЛАСЬ выложить, и умела спросить про
/// каждую такую строку, лежит ли она. То, что лежит в бакете сверх этого — остатки после
/// переименований папок, выкладки с другой машины, ручные опыты, — было не видно ниоткуда.
///
/// Проверяется здесь ровно то, что нельзя проверить глазами в живом прогоне:
///   • разбор ответа хостинга — у разных провайдеров (AWS, Ceph у Timeweb) отличаются пространство
///     имён и то, приходит ли метка продолжения в последнем ответе; пойти по ней ещё раз — это
///     бесконечный обход;
///   • совпадение адреса запроса с тем, от чего посчитана подпись: с первым же префиксом с
///     кириллицей расхождение даёт «SignatureDoesNotMatch», по которому ничего не отладить;
///   • удаление: метод, ключ в адресе и то, что «объекта уже нет» — это успех, а не ошибка.</summary>
public class S3BucketBrowsingTests
{
    private static S3Settings Settings(string prefix = "") =>
        new("https://s3.twcstorage.ru", "amperus", "ru-1", prefix, "AK", "SK",
            "https://fs.elitacompany.ru", true);

    private const string Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    private static string Listing(string contents, string prefixes = "", bool truncated = false,
        string token = "") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <ListBucketResult xmlns="{Ns}">
          <Name>amperus</Name>
          <IsTruncated>{(truncated ? "true" : "false")}</IsTruncated>
          {contents}
          {prefixes}
          <NextContinuationToken>{token}</NextContinuationToken>
        </ListBucketResult>
        """;

    // ── Разбор ответа ─────────────────────────────────────────────────────────

    [Fact]
    public void ParseListing_ReadsObjectsAndFolders()
    {
        var page = S3Client.ParseListing(Listing(
            contents: """
                <Contents>
                  <Key>PO/PZH/SMH5/Instrukciya/i.pdf</Key>
                  <LastModified>2026-08-12T09:30:00.000Z</LastModified>
                  <Size>1048576</Size>
                </Contents>
                """,
            prefixes: "<CommonPrefixes><Prefix>PO/PZH/SMH5/</Prefix></CommonPrefixes>"));

        Assert.True(page.Ok);
        var obj = Assert.Single(page.Objects);
        Assert.Equal("PO/PZH/SMH5/Instrukciya/i.pdf", obj.Key);
        Assert.Equal(1048576, obj.Size);
        Assert.Equal(new DateTime(2026, 8, 12, 9, 30, 0, DateTimeKind.Utc), obj.Modified!.Value.ToUniversalTime());
        Assert.Equal("PO/PZH/SMH5/", Assert.Single(page.Folders));
    }

    /// <summary>Метка продолжения берётся ТОЛЬКО когда список правда обрезан. Часть провайдеров
    /// присылает её и в последнем ответе — обход по ней запрашивал бы одну и ту же страницу без
    /// конца, а страница показывала бы удваивающийся список.</summary>
    [Fact]
    public void ParseListing_IgnoresTheContinuationToken_WhenTheListIsComplete()
    {
        var page = S3Client.ParseListing(Listing("", truncated: false, token: "1/xxx="));

        Assert.True(page.Ok);
        Assert.Null(page.NextToken);
    }

    [Fact]
    public void ParseListing_KeepsTheToken_WhenTruncated()
    {
        var page = S3Client.ParseListing(Listing("", truncated: true, token: "1/xxx="));

        Assert.Equal("1/xxx=", page.NextToken);
    }

    /// <summary>Ответ не тем, чего ждали (страница входа хостинга, обрезанное тело), — это сообщение
    /// человеку, а не исключение посреди обхода.</summary>
    [Fact]
    public void ParseListing_OnGarbage_SaysSoInsteadOfThrowing()
    {
        var page = S3Client.ParseListing("<html>вход в панель</html");

        Assert.False(page.Ok);
        Assert.NotNull(page.Error);
        Assert.Empty(page.Objects);
    }

    // ── Запрос ────────────────────────────────────────────────────────────────

    /// <summary>Адрес запроса и строка, от которой считается подпись, — это ОДНА строка: параметры
    /// закодированы по правилам схемы и отсортированы по имени. Раньше в адрес уходил исходный вид, а
    /// подписывался закодированный; пока параметрами были «list-type=2» и «max-keys=1», это было одно
    /// и то же, но слеш в префиксе обязан стать %2F, а кириллица — процентами, иначе сервер посчитает
    /// подпись от своего варианта и не сойдётся.</summary>
    [Fact]
    public void BuildRequest_PutsTheSignedQueryIntoTheAddress()
    {
        var request = S3Client.BuildRequest(Settings(), HttpMethod.Get, "", Array.Empty<byte>(), null,
            new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero),
            new[] { ("prefix", "ПО/ПЖ/"), ("list-type", "2"), ("delimiter", "/") });

        Assert.Equal(
            "https://s3.twcstorage.ru/amperus?delimiter=%2F&list-type=2&prefix=%D0%9F%D0%9E%2F%D0%9F%D0%96%2F",
            request.RequestUri!.AbsoluteUri);
    }

    // ── Перечисление и удаление через подставной хостинг ──────────────────────

    /// <summary>Подставной хостинг: отвечает заготовленными телами по очереди и запоминает запросы.
    /// Живой сети в тестах нет — проверяется поведение программы, а не доступность Timeweb.</summary>
    private sealed class FakeStorage : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _answers;

        public FakeStorage(params (HttpStatusCode Status, string Body)[] answers) =>
            _answers = new Queue<(HttpStatusCode, string)>(answers);

        public List<(string Method, string Url)> Seen { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Seen.Add((request.Method.Method, request.RequestUri!.AbsoluteUri));
            var (status, body) = _answers.Count > 0 ? _answers.Dequeue() : (HttpStatusCode.OK, Listing(""));
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    /// <summary>Хостинг отдаёт список порциями, и продолжение — единственный способ узнать
    /// остальное. Обход обязан пройти его до конца сам: показать первую тысячу ключей и молча
    /// остановиться — это «мусора нет» там, где он есть.</summary>
    [Fact]
    public async Task ListAsync_WalksThroughEveryPage()
    {
        var storage = new FakeStorage(
            (HttpStatusCode.OK, Listing("<Contents><Key>a.pdf</Key><Size>1</Size></Contents>",
                truncated: true, token: "next=")),
            (HttpStatusCode.OK, Listing("<Contents><Key>b.pdf</Key><Size>2</Size></Contents>")));
        var client = new S3Client(new HttpClient(storage));

        var first = await client.ListAsync(Settings(), "PO/", grouped: true);
        Assert.Equal("next=", first.NextToken);
        var second = await client.ListAsync(Settings(), "PO/", grouped: true, continuationToken: first.NextToken);

        Assert.Equal("a.pdf", Assert.Single(first.Objects).Key);
        Assert.Equal("b.pdf", Assert.Single(second.Objects).Key);
        Assert.Null(second.NextToken);

        Assert.All(storage.Seen, r => Assert.Equal("GET", r.Method));
        Assert.Contains("list-type=2", storage.Seen[0].Url);
        Assert.Contains("delimiter=%2F", storage.Seen[0].Url);
        Assert.Contains("continuation-token=next%3D", storage.Seen[1].Url);
    }

    /// <summary>«Показать всё вложенное списком» и подсчёт того, что уйдёт при удалении папки, — это
    /// запрос БЕЗ разделителя: папок у S3 нет, и удалять приходится по ключу каждый объект.</summary>
    [Fact]
    public async Task ListAsync_Flat_AsksWithoutADelimiter()
    {
        var storage = new FakeStorage();
        var client = new S3Client(new HttpClient(storage));

        await client.ListAsync(Settings(), "PO/", grouped: false);

        Assert.DoesNotContain("delimiter", Assert.Single(storage.Seen).Url);
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheObject()
    {
        var storage = new FakeStorage((HttpStatusCode.NoContent, ""));
        var client = new S3Client(new HttpClient(storage));

        var result = await client.DeleteAsync(Settings(), "PO/PZH/i.pdf");

        Assert.True(result.Ok);
        var (method, url) = Assert.Single(storage.Seen);
        Assert.Equal("DELETE", method);
        Assert.Equal("https://s3.twcstorage.ru/amperus/PO/PZH/i.pdf", url);
    }

    /// <summary>Объекта уже нет — цель достигнута. Иначе повторное удаление той же строки (список на
    /// экране устарел, коллега убрал её раньше) выглядело бы для человека поломкой.</summary>
    [Fact]
    public async Task DeleteAsync_TreatsMissingObjectAsDone()
    {
        var client = new S3Client(new HttpClient(new FakeStorage((HttpStatusCode.NotFound, ""))));

        Assert.True((await client.DeleteAsync(Settings(), "PO/нет.pdf")).Ok);
    }

    /// <summary>Без ключей ни перечисления, ни удаления не происходит — и это внятная строка, а не
    /// запрос без подписи, на который хостинг ответит «AccessDenied».</summary>
    [Fact]
    public async Task WithoutKeys_NeitherListNorDeleteGoesToTheNetwork()
    {
        var storage = new FakeStorage();
        var client = new S3Client(new HttpClient(storage));
        var noKeys = Settings() with { AccessKey = "", SecretKey = "" };

        Assert.False((await client.ListAsync(noKeys)).Ok);
        Assert.False((await client.DeleteAsync(noKeys, "a.pdf")).Ok);
        Assert.Empty(storage.Seen);
    }
}
