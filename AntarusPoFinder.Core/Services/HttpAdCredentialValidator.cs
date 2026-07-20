using System;
using System.Net;
using System.Net.Http;
using System.Threading;

namespace AntarusPoFinder.Core.Services;

/// <summary>Способ №2 (см. <see cref="IAdCredentialValidator"/>) — проверяет AD логин/пароль не
/// прямым LDAP-биндом, а HTTP-запросом к внутреннему веб-серверу компании (реальный адрес на момент
/// написания — disk.antarus.su, IIS 10.0, авторизация только Negotiate/NTLM, порт 443 доступен из
/// интернета без VPN — см. заметки сессии) — для рабочих мест, у которых нет прямого сетевого
/// доступа к контроллеру домена (LDAP, способ №1), но есть доступ к этому сайту.
///
/// Живёт в Core, а не в App (в отличие от LdapAdCredentialValidator) — не тянет ничего Windows-
/// специфичного (просто HttpClient), так что кроссплатформенно тестируется вместе с остальным Core
/// без ссылки тестового проекта на App.
///
/// baseUrl приходит из Настроек (Настройки → Общие → «URL веб-сервера для проверки пароля») и
/// НЕ хардкодится здесь — точный адрес/формат подтвердит IT, администратор впишет его в момент
/// активации способа.</summary>
public class HttpAdCredentialValidator : IAdCredentialValidator
{
    private readonly string _baseUrl;
    private readonly TimeSpan _timeout;

    public HttpAdCredentialValidator(string baseUrl, TimeSpan? timeout = null)
    {
        _baseUrl = baseUrl ?? "";
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public bool Validate(string domain, string login, string password, out string? error) =>
        ValidateWithStatus(domain, login, password, out error) == AdValidationStatus.Success;

    /// <summary>domain приходит по общему контракту (тот же ввод, что и для LDAP-способа), но
    /// намеренно игнорируется здесь — поле логина уже несёт тот формат, который реально работает
    /// против домена (UPN "login@domain", "ДОМЕН\login" или голый "login" — см. пояснение в
    /// AppUserAuthService.NormalizeAdLogin о том, почему ручное разделение домен+логин раньше не
    /// сработало при тестировании NTLM). Логин/пароль передаются в NetworkCredential как есть, без
    /// домена — по явному решению из этого раунда, не пытаемся угадывать/парсить формат сами.</summary>
    public AdValidationStatus ValidateWithStatus(string domain, string login, string password, out string? error)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
        {
            error = "URL веб-сервера для проверки пароля не настроен (Настройки → Общие → «Способ проверки»).";
            return AdValidationStatus.Unavailable;
        }

        if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri))
        {
            error = $"Некорректный URL веб-сервера для проверки пароля: «{_baseUrl}».";
            return AdValidationStatus.Unavailable;
        }

        try
        {
            return CheckSync(uri, login, password, out error);
        }
        catch (Exception ex)
        {
            // Любая сетевая ошибка (сервер недоступен, DNS не резолвится, таймаут, TLS-сбой) — это
            // НЕ "неверный пароль", это отдельный статус: мы просто не смогли спросить. Разделение
            // важно для UX (см. AdValidationStatus) и для способа="оба" (LDAP + HTTP-фоллбэк).
            error = $"Не удалось проверить пароль через {uri.Host} — сервер недоступен: {ex.Message}";
            return AdValidationStatus.Unavailable;
        }
    }

    /// <summary>HEAD first (cheapest — no body to transfer); a 405 from a server that refuses HEAD
    /// entirely falls back to GET once, so a wrong choice of method here doesn't masquerade as
    /// "unavailable". CredentialCache scopes the credential explicitly to NTLM/Negotiate on this URI
    /// only (never Basic/Digest) — matches how this was manually verified against disk.antarus.su
    /// via PowerShell earlier, and never sends the password anywhere the server didn't ask for it via
    /// exactly one of those two challenge types.</summary>
    private static AdValidationStatus CheckSync(Uri uri, string login, string password, out string? error)
    {
        var credentials = new CredentialCache
        {
            { uri, "NTLM", new NetworkCredential(login, password) },
            { uri, "Negotiate", new NetworkCredential(login, password) },
        };
        using var handler = new HttpClientHandler
        {
            Credentials = credentials,
            AllowAutoRedirect = false,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        var response = client.Send(new HttpRequestMessage(HttpMethod.Head, uri), CancellationToken.None);
        if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
        {
            response.Dispose();
            response = client.Send(new HttpRequestMessage(HttpMethod.Get, uri), CancellationToken.None);
        }

        using (response)
        {
            var code = (int)response.StatusCode;
            if (code is >= 200 and < 300)
            {
                error = null;
                return AdValidationStatus.Success;
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                error = "Неверный логин или пароль (проверено через веб-сервер).";
                return AdValidationStatus.InvalidCredentials;
            }
            error = $"Веб-сервер вернул статус {code} {response.ReasonPhrase} — не удалось проверить пароль.";
            return AdValidationStatus.Unavailable;
        }
    }
}
