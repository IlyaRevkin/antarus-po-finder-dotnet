using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ТРАНСПОРТ МОБИЛЬНОГО ДОСТУПА. Проверяется не разметка (она в MobileSiteTests), а ровно
/// то, что отделяет «свой» телефон от любого другого устройства в той же Wi-Fi сети, и то, что
/// сервер остаётся читающим.
///
/// Порт 0 — ОС выдаёт свободный сама: прибивать тест к 9877 значило бы ронять его каждый раз, когда
/// на машине открыта сама программа.</summary>
public class MobileAccessServerTests
{
    private static MobileAccessServer Start(string token = "tok") =>
        new(0, token, (path, query) => new MobileSite.Page(200, $"путь=[{path}] запрос=[{query}]"));

    private static HttpClient Client() => new() { Timeout = System.TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task CorrectToken_GetsThePage_AndPassesThePathThrough()
    {
        using var server = Start();
        using var http = Client();

        var body = await http.GetStringAsync($"http://127.0.0.1:{server.Port}/tok/v/7?q=%D0%9A%D0%9D%D0%A1");

        Assert.Contains("путь=[v/7]", body);
        Assert.Contains("запрос=[q=%D0%9A%D0%9D%D0%A1]", body);
    }

    /// <summary>Корень без хвоста — то, что открывает закладка телефона. Хвост обязан быть пустым, а
    /// не «/» и не «tok».</summary>
    [Theory]
    [InlineData("/tok")]
    [InlineData("/tok/")]
    public async Task Root_WithAndWithoutTrailingSlash_GivesAnEmptyTail(string path)
    {
        using var server = Start();
        using var http = Client();

        var body = await http.GetStringAsync($"http://127.0.0.1:{server.Port}{path}");

        Assert.Contains("путь=[]", body);
    }

    /// <summary>Главное: без токена — отказ. Токен в пути здесь единственный пропуск, и его проверка
    /// не должна зависеть ни от чего больше.</summary>
    [Fact]
    public async Task WrongToken_IsRefused()
    {
        using var server = Start();
        using var http = Client();

        var response = await http.GetAsync($"http://127.0.0.1:{server.Port}/чужой/v/7");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Первый слой только читает. Запись сюда не просто не реализована — она отклоняется,
    /// чтобы «а он уже умеет принимать» не стало случайным выводом из того, что POST не падает.</summary>
    [Fact]
    public async Task Post_IsRefused_EvenWithTheRightToken()
    {
        using var server = Start();
        using var http = Client();

        var response = await http.PostAsync($"http://127.0.0.1:{server.Port}/tok/",
            new StringContent("что-нибудь"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    /// <summary>Остановленный сервер больше не отвечает — галку «выключить» надо уметь выключать
    /// по-настоящему, а не только прятать адрес.</summary>
    [Fact]
    public async Task Disposed_StopsAnswering()
    {
        var server = Start();
        var port = server.Port;
        server.Dispose();

        using var http = Client();
        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => http.GetStringAsync($"http://127.0.0.1:{port}/tok/"));
    }

    [Theory]
    [InlineData("/tok", "tok", "")]
    [InlineData("/tok/", "tok", "")]
    [InlineData("/tok/v/7?q=1", "tok/v/7", "q=1")]
    [InlineData("/", "", "")]
    public void SplitPath_SeparatesPathAndQuery(string raw, string path, string query)
    {
        var (actualPath, actualQuery) = MobileAccessServer.SplitPath(raw);
        Assert.Equal(path, actualPath);
        Assert.Equal(query, actualQuery);
    }
}
