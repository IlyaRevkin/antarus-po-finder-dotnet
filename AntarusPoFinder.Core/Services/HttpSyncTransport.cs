using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AntarusPoFinder.Core.Services;

/// <summary>Адрес сервера обмена и ключ доступа к нему. Отдельным типом, а не парой строк по месту,
/// ровно из-за одного требования: канал должен сниматься и заменяться в пару действий, без правки
/// кода. Поэтому в программе НЕТ и не должно появиться ни одного зашитого имени хоста — адрес это
/// настройка, и что за сервер за ней стоит (арендованный, корпоративный, чей-то временный), код
/// знать не обязан.
///
/// Пустой адрес — штатное состояние «сервер не настроен»: <see cref="ConfigSyncService"/> в этом
/// случае работает через сетевой диск, как работал всегда. Это то же правило, что у выкладки на
/// хостинг (<see cref="S3Settings.CanPublish"/>), и заведено оно по той же причине: канал, которого
/// нет, не должен ломать программу.</summary>
public sealed record SyncServerSettings(string BaseUrl, string AccessKey)
{
    /// <summary>Адрес задан и похож на адрес. Проверяем схему, а не только непустоту: строка «сервер
    /// конторы» в поле адреса — это не «настроено», и падать на ней внутри HttpClient незачем.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>Адрес без хвостового слеша — чтобы склейка с путём не давала двойной.</summary>
    public string Root => (BaseUrl ?? "").Trim().TrimEnd('/');
}

/// <summary>Обмен общим конфигом через HTTP — вторая реализация <see cref="ISyncTransport"/> рядом с
/// файловой шарой (<see cref="FileShareTransport"/>).
///
/// <b>Зачем.</b> Сетевой диск виден только внутри корпоративной сети, а работать с прошивками нужно и
/// снаружи. Файловая шара до тех, кто снаружи, не дотягивается никак — и дело не в файлах: без общего
/// конфига у такой машины просто НЕ ОБНОВЛЯЕТСЯ каталог, то есть новых прошивок она не видит вовсе,
/// сколько файлов ей ни дай. Поэтому первым по HTTP уезжает именно конфиг, а не файлы.
///
/// <b>Семантика ровно та же, что у шары</b>, — два объекта: маркер ревизии и зашифрованный конфиг.
/// Ни шифрование, ни склейка журнала изменений сюда не переезжают, они остаются в
/// <see cref="ConfigSyncService"/>: транспорт возит байты и больше ничего не знает.
///
/// <b>Только GET и POST.</b> Ни PUT, ни PROPFIND, ни прочих глаголов WebDAV — их корпоративные прокси
/// режут регулярно, и отладить такое с рабочей машины почти невозможно. По той же причине не
/// используется и подключение сетевым диском средствами Windows: редиректор WebDAV требует служб и
/// правок реестра, которых на рабочей машине не будет.</summary>
public sealed class HttpSyncTransport : ISyncTransport
{
    /// <summary>Заголовок с ключом доступа. Именно заголовок, а не Basic-авторизация: Basic по дороге
    /// перехватывает Windows-редиректор и часть прокси, начиная спрашивать учётные данные у человека.</summary>
    public const string KeyHeader = "X-Antarus-Key";

    /// <summary>Сколько ждём ответа на проверку доступности. Коротко и намеренно: этот вызов стоит на
    /// пути каждого тика синхронизации, и висеть на нём полминуты, когда сервер лежит, нельзя.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly SyncServerSettings _settings;
    private readonly HttpClient _http;

    public HttpSyncTransport(SyncServerSettings settings, HttpClient? http = null)
    {
        _settings = settings;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    }

    private string Url(string path) => $"{_settings.Root}/{path}";

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, Url(path));
        if (!string.IsNullOrWhiteSpace(_settings.AccessKey))
            request.Headers.TryAddWithoutValidation(KeyHeader, _settings.AccessKey.Trim());
        return request;
    }

    public async Task<bool> IsAvailableAsync()
    {
        if (!_settings.IsConfigured) return false;
        try
        {
            using var cts = new CancellationTokenSource(ProbeTimeout);
            using var response = await _http.SendAsync(Request(HttpMethod.Get, "ping"), cts.Token)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            // Сервер не отвечает — это «канала нет», а не ошибка программы: ровно так же файловая
            // шара отвечает «папки нет», когда диск отвалился.
            return false;
        }
    }

    public async Task<SyncRevisionMarker?> ReadRevisionAsync()
    {
        var bytes = await GetAsync("revision").ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            return JsonSerializer.Deserialize<SyncRevisionMarker>(Encoding.UTF8.GetString(bytes));
        }
        catch (JsonException)
        {
            // Битый маркер трактуется как «маркера нет» — то же самое правило, что и у файловой
            // реализации: надёжных сведений о ревизии нет, клиент откатывается на сравнение по конфигу.
            return null;
        }
    }

    public Task WriteRevisionAsync(SyncRevisionMarker marker) =>
        PostAsync("revision", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(marker)), "application/json");

    public Task<byte[]?> ReadConfigAsync() => GetAsync("config");

    public Task WriteConfigAsync(byte[] bytes) =>
        PostAsync("config", bytes, "application/octet-stream");

    /// <summary>Чтение объекта. null — объекта ещё нет (404) либо канал недоступен; вызывающий обязан
    /// трактовать оба случая одинаково, и в интерфейсе это записано прямо.</summary>
    private async Task<byte[]?> GetAsync(string path)
    {
        if (!_settings.IsConfigured) return null;
        try
        {
            using var response = await _http.SendAsync(Request(HttpMethod.Get, path)).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Запись объекта. В отличие от чтения ошибку НЕ глотает: «конфиг не уехал» обязано
    /// дойти до вызывающего — молча потерянная отправка означает, что изменения не увидит никто, а
    /// человек будет уверен, что отправил.</summary>
    private async Task PostAsync(string path, byte[] bytes, string contentType)
    {
        if (!_settings.IsConfigured)
            throw new InvalidOperationException("Сервер обмена не настроен — адрес не задан.");

        using var request = Request(HttpMethod.Post, path);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        using var response = await _http.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Сервер обмена не принял «{path}»: {(int)response.StatusCode} {response.ReasonPhrase}");
    }
}
