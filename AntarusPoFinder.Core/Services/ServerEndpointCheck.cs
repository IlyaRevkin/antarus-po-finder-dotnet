using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что ответил сервер приложения на кнопку «Проверить сервер».</summary>
public sealed record ServerCheckResult(bool Ok, string Message, string? WebSocketUrl);

/// <summary>Проверка адреса будущего сервера приложения (docs/client-server-plan.md, этап 1) — и
/// заодно единственное место, где строится адрес живых уведомлений: та же машина и путь <c>/ws</c>,
/// схема http→ws, https→wss. Правило построения адреса должно быть ОДНО (иначе кириллица и порты
/// разъезжаются между кнопкой проверки и реальным подключением) — поэтому оно здесь, а не в
/// обработчике кнопки.
///
/// Сервера сегодня нет, и это нормально: проверка честно скажет, что по адресу никого нет. Смысл в
/// том, чтобы в день, когда сервер поднимут, настройку можно было проверить одной кнопкой, а не
/// заливкой конфига «наугад».</summary>
public static class ServerEndpointCheck
{
    /// <summary>Куда стучимся, чтобы понять, что там наш сервер, а не чужой сайт. Порядок — сверху
    /// вниз, первый ответивший выигрывает.</summary>
    private static readonly string[] Probes = { "/healthz", "/api/health", "/" };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static string WebSocketUrlFor(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return "";
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "wss://" + trimmed["https://".Length..] + "/ws";
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return "ws://" + trimmed["http://".Length..] + "/ws";
        // Схему не указали — считаем https: внутрисетевой сервер без TLS сегодня уже исключение,
        // а угадать «наверное http» тем более нельзя.
        return "wss://" + trimmed + "/ws";
    }

    public static async Task<ServerCheckResult> CheckAsync(string baseUrl, HttpClient? http = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return new ServerCheckResult(false, "Адрес сервера не задан.", null);

        var root = baseUrl.Trim().TrimEnd('/');
        if (!root.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !root.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            root = "https://" + root;

        var ws = WebSocketUrlFor(root);
        var client = http ?? Http;
        string? lastError = null;

        foreach (var probe in Probes)
        {
            try
            {
                using var response = await client.GetAsync(root + probe, ct);
                if (response.IsSuccessStatusCode)
                    return new ServerCheckResult(true,
                        $"Сервер отвечает ({root}{probe}, {(int)response.StatusCode}). Живые уведомления пойдут на {ws}.", ws);
                lastError = $"{root}{probe} ответил {(int)response.StatusCode}";
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        return new ServerCheckResult(false,
            $"Сервер по адресу {root} не отвечает: {lastError}. Пока он не поднят, оставьте обмен через сетевую папку.", ws);
    }
}
