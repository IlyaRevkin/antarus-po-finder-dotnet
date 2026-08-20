using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что ответила служба обмена на «постучались».</summary>
public sealed record SyncServerProbeResult(bool Ok, string Message);

/// <summary>Ответ GET /ping службы antarus-sync (tools/sync-server).</summary>
public sealed class SyncServerPing
{
    [JsonPropertyName("service")] public string Service { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("server")] public string Server { get; set; } = "";
    [JsonPropertyName("client")] public string Client { get; set; } = "";
    [JsonPropertyName("can_write")] public bool CanWrite { get; set; }
    [JsonPropertyName("revision")] public int Revision { get; set; }
    [JsonPropertyName("has_config")] public bool HasConfig { get; set; }
}

/// <summary>Проверка связи со службой обмена — то, что стоит за кнопкой «Проверить связь».
///
/// Отдельно от <see cref="UrlReachabilityCheck"/>: тот отвечает «адрес вообще откликается»,
/// а здесь нужно другое — точно ли по адресу НАША служба, признала ли она эту машину и что ей
/// разрешено. Без этого «проверка» сводилась бы к «сервер жив», а жалоба «не синхронизируется»
/// всё равно требовала бы разбора вручную.
///
/// Возвращает готовую фразу для показа человеку, а не код ответа: коды HTTP тут ничего не говорят
/// тому, кто настраивает, а различать 401 «ключ не тот» и 403 «доступ отобрали» ему нужно.</summary>
public static class SyncServerProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<SyncServerProbeResult> CheckAsync(SyncServerSettings settings, HttpClient? http = null)
    {
        if (!settings.IsConfigured)
            return new SyncServerProbeResult(false, "Адрес службы не задан или не похож на адрес (нужен http:// или https://).");
        if (string.IsNullOrWhiteSpace(settings.AccessKey))
            return new SyncServerProbeResult(false, "Ключ доступа не задан — служба не пустит без него.");

        var client = http ?? new HttpClient { Timeout = Timeout };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{settings.Root}/ping");
            request.Headers.TryAddWithoutValidation(HttpSyncTransport.KeyHeader, settings.AccessKey.Trim());

            using var response = await client.SendAsync(request).ConfigureAwait(false);

            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    return new SyncServerProbeResult(false,
                        "Служба отвечает, но ключ не опознан. Проверьте, что вставлен ключ именно этой машины.");
                case HttpStatusCode.Forbidden:
                    return new SyncServerProbeResult(false,
                        "Ключ опознан, но доступ для этой машины отключён на сервере.");
                case HttpStatusCode.NotFound:
                    return new SyncServerProbeResult(false,
                        "По этому адресу службы обмена нет (ответ 404). Проверьте адрес и порт — по умолчанию 8443.");
            }

            if (!response.IsSuccessStatusCode)
                return new SyncServerProbeResult(false,
                    $"Служба ответила {(int)response.StatusCode} {response.ReasonPhrase}.");

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            SyncServerPing? ping = null;
            try
            {
                ping = JsonSerializer.Deserialize<SyncServerPing>(body);
            }
            catch (JsonException)
            {
                // Не наша служба, а что-то другое по тому же адресу: связь есть, но это не она.
            }

            if (ping is null || ping.Service != "antarus-sync")
                return new SyncServerProbeResult(false,
                    "Адрес отвечает, но это не служба обмена — проверьте адрес и порт.");

            var rights = ping.CanWrite ? "чтение и отправка" : "только чтение";
            var catalog = ping.HasConfig
                ? $"Каталог на сервере есть, ревизия {ping.Revision}"
                : "Каталог на сервер ещё ни разу не отправляли";
            return new SyncServerProbeResult(true,
                $"Связь есть. Служба «{ping.Server}» версии {ping.Version}. " +
                $"Эта машина опознана как «{ping.Client}», права: {rights}. {catalog}.");
        }
        catch (TaskCanceledException)
        {
            return new SyncServerProbeResult(false, "Служба не ответила за 10 секунд. Проверьте адрес, порт и брандмауэр.");
        }
        catch (HttpRequestException ex)
        {
            return new SyncServerProbeResult(false, $"Не удалось связаться со службой: {ex.Message}");
        }
        finally
        {
            if (http is null) client.Dispose();
        }
    }
}
