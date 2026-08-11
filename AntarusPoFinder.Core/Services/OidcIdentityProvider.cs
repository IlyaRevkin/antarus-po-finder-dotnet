using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что рассказал о себе OpenID-провайдер. Пустые адреса означают «сервер ответил, но
/// описание неполное» — тогда входить нечем и об этом надо сказать прямо, а не падать в браузере.</summary>
public sealed record OidcDiscovery(string Issuer, string AuthorizationEndpoint, string TokenEndpoint,
    string? UserInfoEndpoint, string? EndSessionEndpoint)
{
    public bool Usable => !string.IsNullOrWhiteSpace(AuthorizationEndpoint) && !string.IsNullOrWhiteSpace(TokenEndpoint);
}

/// <summary>Итог кнопки «Проверить адрес» в настройках подключения.</summary>
public sealed record OidcCheckResult(bool Ok, string Message, OidcDiscovery? Discovery);

/// <summary>Вход через корпоративный SSO (Keycloak и любой другой OpenID Connect) — третья
/// реализация <see cref="IIdentityProvider"/> рядом с <see cref="AdIdentityProvider"/>. Точка
/// расширения была подготовлена заранее и описана в самом интерфейсе; здесь она заполнена ровно так,
/// как там и планировалось.
///
/// Схема — authorization code + PKCE с возвратом на loopback:
///   1. у провайдера спрашивается его описание (<c>.well-known/openid-configuration</c>) — адреса
///      не зашиваются, потому что у Keycloak они зависят от realm;
///   2. поднимается временный слушатель на <c>http://127.0.0.1:{свободный порт}/</c>;
///   3. открывается СИСТЕМНЫЙ браузер (встроенный WebView многие провайдеры блокируют намеренно, да
///      и корпоративный SSO обычно уже залогинен именно в системном браузере — вход происходит в
///      один клик);
///   4. пришедший код меняется на токены прямым запросом к token_endpoint.
///
/// Пароля приложение не видит НИ РАЗУ — в этом и весь смысл перехода на SSO.
///
/// Подпись id_token отдельно не проверяется намеренно: токен получен не через браузер, а прямым
/// TLS-запросом к token_endpoint того самого сервера, чей адрес взят из его же описания, — это тот
/// случай, который спецификация (OIDC Core, 3.1.3.7) прямо разрешает не проверять. Появится
/// требование безопасности проверять подпись — сюда добавится загрузка JWKS, форма вызова не
/// изменится.</summary>
public sealed class OidcIdentityProvider : IIdentityProvider
{
    private readonly string _authority;
    private readonly string _clientId;
    private readonly string _groupsClaim;
    private readonly HttpClient _http;
    private readonly Action<string> _openBrowser;

    /// <summary>Сколько ждём, пока человек введёт логин в браузере. Час — не «на всякий случай»:
    /// корпоративный вход часто требует второго фактора с телефона, а окно входа в это время просто
    /// ждёт и не должно объявить отказ раньше пользователя.</summary>
    private static readonly TimeSpan BrowserWait = TimeSpan.FromMinutes(10);

    public OidcIdentityProvider(string authority, string clientId, string? groupsClaim = null,
        HttpClient? http = null, Action<string>? openBrowser = null)
    {
        _authority = (authority ?? "").Trim().TrimEnd('/');
        _clientId = (clientId ?? "").Trim();
        _groupsClaim = string.IsNullOrWhiteSpace(groupsClaim) ? "groups" : groupsClaim!.Trim();
        _http = http ?? SharedHttp;
        _openBrowser = openBrowser ?? OpenInSystemBrowser;
    }

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Спросить у сервера его описание. Отдельно от входа — это и кнопка «Проверить адрес»
    /// в настройках, и первый шаг самого входа.</summary>
    public static async Task<OidcCheckResult> DiscoverAsync(string authority, HttpClient? http = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(authority))
            return new OidcCheckResult(false, "Адрес realm не задан.", null);

        var url = authority.Trim().TrimEnd('/') + "/.well-known/openid-configuration";
        try
        {
            var client = http ?? SharedHttp;
            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return new OidcCheckResult(false,
                    $"Сервер ответил {(int)response.StatusCode} на {url} — проверьте адрес realm.", null);

            var json = await response.Content.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(json) as JsonObject;
            if (node is null) return new OidcCheckResult(false, "Сервер вернул не то, чего ждали от описания OpenID.", null);

            var discovery = new OidcDiscovery(
                Str(node, "issuer") ?? "",
                Str(node, "authorization_endpoint") ?? "",
                Str(node, "token_endpoint") ?? "",
                Str(node, "userinfo_endpoint"),
                Str(node, "end_session_endpoint"));

            return discovery.Usable
                ? new OidcCheckResult(true, $"Сервер отвечает. Realm: {discovery.Issuer}", discovery)
                : new OidcCheckResult(false, "Сервер ответил, но не сообщил адреса входа — это не похоже на OpenID-провайдера.", discovery);
        }
        catch (Exception ex)
        {
            return new OidcCheckResult(false, $"Не удалось связаться с {url}: {ex.Message}", null);
        }
    }

    public async Task<IdentityResult> SignInAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_authority) || string.IsNullOrWhiteSpace(_clientId))
            return IdentityResult.Fail(IdentityFailureKind.Unavailable,
                "Корпоративный вход не настроен: укажите адрес realm и клиент в Настройки → Подключение.");

        var check = await DiscoverAsync(_authority, _http, ct);
        if (!check.Ok || check.Discovery is null)
            return IdentityResult.Fail(IdentityFailureKind.Unavailable, check.Message);

        var discovery = check.Discovery;

        HttpListener listener;
        string redirectUri;
        try
        {
            (listener, redirectUri) = StartLoopbackListener();
        }
        catch (Exception ex)
        {
            return IdentityResult.Fail(IdentityFailureKind.Unavailable,
                $"Не удалось открыть локальный порт для ответа сервера входа: {ex.Message}");
        }

        try
        {
            var verifier = RandomUrlSafe(64);
            var challenge = Sha256Base64Url(verifier);
            var state = RandomUrlSafe(24);

            var authUrl = discovery.AuthorizationEndpoint
                + (discovery.AuthorizationEndpoint.Contains('?') ? "&" : "?")
                + string.Join("&", new[]
                {
                    "response_type=code",
                    "client_id=" + Uri.EscapeDataString(_clientId),
                    "redirect_uri=" + Uri.EscapeDataString(redirectUri),
                    "scope=" + Uri.EscapeDataString("openid profile email"),
                    "state=" + state,
                    "code_challenge=" + challenge,
                    "code_challenge_method=S256",
                });

            try { _openBrowser(authUrl); }
            catch (Exception ex)
            {
                return IdentityResult.Fail(IdentityFailureKind.Unavailable,
                    $"Не удалось открыть браузер для входа: {ex.Message}");
            }

            var callback = await WaitForCallbackAsync(listener, state, ct);
            if (callback.Error is not null)
                return IdentityResult.Fail(IdentityFailureKind.Rejected, callback.Error);
            if (callback.Code is null)
                return IdentityResult.Fail(IdentityFailureKind.Unavailable, "Сервер входа не вернул код авторизации.");

            return await ExchangeCodeAsync(discovery, callback.Code, verifier, redirectUri, ct);
        }
        finally
        {
            try { listener.Stop(); } catch (Exception) { /* закрытие слушателя не должно ломать вход */ }
            try { listener.Close(); } catch (Exception) { /* и закрытие тоже — см. строку выше */ }
        }
    }

    private async Task<IdentityResult> ExchangeCodeAsync(OidcDiscovery discovery, string code, string verifier,
        string redirectUri, CancellationToken ct)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = _clientId,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
            });
            using var response = await _http.PostAsync(discovery.TokenEndpoint, content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return IdentityResult.Fail(IdentityFailureKind.Rejected,
                    $"Сервер входа отказал при обмене кода ({(int)response.StatusCode}): {Shorten(body)}");

            var token = JsonNode.Parse(body) as JsonObject;
            var idToken = Str(token, "id_token");
            if (string.IsNullOrWhiteSpace(idToken))
                return IdentityResult.Fail(IdentityFailureKind.Unavailable, "Сервер входа не вернул id_token.");

            var claims = DecodeJwtPayload(idToken!);
            if (claims is null)
                return IdentityResult.Fail(IdentityFailureKind.Unavailable, "Не удалось разобрать токен от сервера входа.");

            var login = Str(claims, "preferred_username") ?? Str(claims, "upn") ?? Str(claims, "email")
                        ?? Str(claims, "sub") ?? "";
            var name = Str(claims, "name") ?? login;
            var email = Str(claims, "email") ?? "";
            var groups = ReadGroups(claims, _groupsClaim);
            var expires = ReadExpiry(claims);

            return IdentityResult.Ok(login, name, email, groups, expires);
        }
        catch (Exception ex)
        {
            return IdentityResult.Fail(IdentityFailureKind.Unavailable, $"Обмен кода не удался: {ex.Message}");
        }
    }

    /// <summary>Срок жизни подтверждения — claim exp, секунды эпохи. Нет или не число — null:
    /// это не ошибка входа, просто гейту нечего запоминать про срок.</summary>
    private static DateTime? ReadExpiry(JsonObject claims)
    {
        try
        {
            if (claims["exp"] is JsonValue v && v.TryGetValue<long>(out var seconds))
                return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        catch (Exception)
        {
            // Нестандартный тип claim'а — считаем, что срока нет.
        }
        return null;
    }

    /// <summary>Группы могут прийти и списком, и одной строкой через пробел/запятую — оба варианта
    /// встречаются у разных провайдеров, поэтому разбираем оба, а не «как у нас в тесте».</summary>
    private static IReadOnlyList<string> ReadGroups(JsonObject claims, string claimName)
    {
        var node = claims[claimName];
        if (node is JsonArray array)
            return array.Select(x => x?.ToString() ?? "").Where(s => s.Length > 0).ToList();
        if (node is JsonValue value && value.TryGetValue<string>(out var raw))
            return raw.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Array.Empty<string>();
    }

    private sealed record Callback(string? Code, string? Error);

    private async Task<Callback> WaitForCallbackAsync(HttpListener listener, string expectedState, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(BrowserWait);

        var contextTask = listener.GetContextAsync();
        var finished = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, timeout.Token));
        if (finished != contextTask)
            return new Callback(null, "Вход не завершён: браузер так и не вернулся с ответом.");

        var context = await contextTask;
        var query = context.Request.QueryString;
        var code = query["code"];
        var error = query["error_description"] ?? query["error"];
        var state = query["state"];

        var ok = error is null && code is not null && string.Equals(state, expectedState, StringComparison.Ordinal);
        await RespondAsync(context, ok
            ? "Вход выполнен. Можно закрыть эту вкладку и вернуться в программу."
            : "Вход не выполнен. Вернитесь в программу — там написано, что пошло не так.");

        if (error is not null) return new Callback(null, $"Сервер входа отказал: {error}");
        if (!string.Equals(state, expectedState, StringComparison.Ordinal))
            return new Callback(null, "Ответ сервера входа не совпал с запросом — вход отменён из соображений безопасности.");
        return new Callback(code, null);
    }

    private static async Task RespondAsync(HttpListenerContext context, string message)
    {
        try
        {
            var html = "<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\">" +
                       "<title>Antarus ПО Finder</title></head><body style=\"font-family:Segoe UI,Arial;padding:40px\">" +
                       "<p>" + WebUtility.HtmlEncode(message) + "</p></body></html>";
            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        catch (Exception)
        {
            // Браузер мог уже уйти — на результат входа это не влияет.
        }
    }

    /// <summary>Слушатель на свободном порту loopback. Порт занимаем сначала обычным сокетом, чтобы
    /// узнать свободный номер, и только потом отдаём его HttpListener — заранее известного
    /// «нашего» порта у настольного приложения быть не может, а зашитый занят у кого-нибудь всегда.</summary>
    private static (HttpListener Listener, string RedirectUri) StartLoopbackListener()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var prefix = $"http://127.0.0.1:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        return (listener, prefix);
    }

    private static void OpenInSystemBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static string RandomUrlSafe(int bytes)
    {
        var buffer = RandomNumberGenerator.GetBytes(bytes);
        return Base64Url(buffer);
    }

    private static string Sha256Base64Url(string value) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(value)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Полезная нагрузка JWT без проверки подписи — см. комментарий класса о том, почему
    /// это здесь допустимо.</summary>
    internal static JsonObject? DecodeJwtPayload(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Str(JsonObject? obj, string key) =>
        obj?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Shorten(string text) =>
        text.Length <= 300 ? text : text[..300] + "…";
}
