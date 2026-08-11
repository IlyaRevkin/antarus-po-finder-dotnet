using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AntarusPoFinder.Core.Services;

/// <summary>«Этот адрес вообще открывается?» — проверка веб-адреса диска инструкций из настроек
/// печати. Отдельно от <see cref="ServerEndpointCheck"/>: тот ищет ИМЕННО наш сервер (стучится в
/// /healthz, рассказывает про WebSocket) и для обычной шары по HTTP отвечал бы бессмыслицей.
///
/// Сначала HEAD (не тянуть тело — за адресом может лежать каталог с тяжёлыми PDF), при отказе —
/// GET: не каждый веб-сервер отвечает на HEAD, и «405 Method Not Allowed» не означает, что адрес
/// неверный. Код 401/403 считается УСПЕХОМ адреса: сервер на месте и отвечает, просто требует
/// вход — для QR это нормально, ссылку откроют из браузера, где вход уже есть.</summary>
public static class UrlReachabilityCheck
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public sealed record Result(bool Ok, string Message);

    public static async Task<Result> CheckAsync(string url, HttpClient? http = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new Result(false, "Адрес не задан.");

        var target = url.Trim().TrimEnd('/');
        if (!target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            target = "https://" + target;

        var client = http ?? Http;
        foreach (var method in new[] { HttpMethod.Head, HttpMethod.Get })
        {
            try
            {
                using var request = new HttpRequestMessage(method, target);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                var code = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                    return new Result(true, $"Адрес отвечает ({code}). Ссылки в QR будут начинаться с {target}");
                if (code is 401 or 403)
                    return new Result(true, $"Адрес отвечает, но требует вход ({code}) — для ссылки в QR это нормально.");
                if (method == HttpMethod.Get)
                    return new Result(false, $"Адрес ответил {code} — проверьте, что это корень диска инструкций.");
            }
            catch (Exception ex) when (method == HttpMethod.Get)
            {
                return new Result(false, $"Адрес не открывается: {ex.Message}");
            }
            catch (Exception)
            {
                // HEAD не прошёл — пробуем GET, некоторые серверы HEAD не поддерживают вовсе.
            }
        }

        return new Result(false, "Адрес не открывается.");
    }
}
