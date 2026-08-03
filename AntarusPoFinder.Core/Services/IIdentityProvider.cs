using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AntarusPoFinder.Core.Services;

/// <summary>Почему вход не состоялся — ровно то же разделение, что и у
/// <see cref="AdValidationStatus"/> («отказали» против «не смогли спросить»), но на уровне
/// провайдера личности, а не проверки пароля: у будущего OIDC-провайдера «не смогли спросить» —
/// это недоступный IdP, а не недоступный контроллер домена, при этом гейт входа обязан вести себя
/// одинаково в обоих случаях (предложить повтор, а не объявить пароль неверным).</summary>
public enum IdentityFailureKind
{
    /// <summary>Вход состоялся — причины отказа нет.</summary>
    None,
    /// <summary>Личность подтвердить не удалось: пароль/учётка отвергнуты источником. Повторять
    /// «как есть» бессмысленно — надо вводить другие данные.</summary>
    Rejected,
    /// <summary>До источника личности не достучались вовсе (домен/IdP/веб-сервер недоступны, сеть
    /// не поднялась, таймаут). Именно этот случай оправдывает кнопку «Проверить снова» в гейте:
    /// под IPsec туннель поднимается ПОЗЖЕ старта приложения, и первая попытка входа штатно может
    /// прийтись на момент, когда сети ещё нет.</summary>
    Unavailable,
}

/// <summary>Результат попытки входа — то, что гейту нужно знать о вошедшем, независимо от того,
/// чем его подтвердили (LDAP-бинд, HTTP-проверка пароля, а в будущем — токен OIDC).</summary>
/// <param name="Success">Личность подтверждена.</param>
/// <param name="UserName">Логин в том виде, в каком его ввёл/вернул источник (нормализация —
/// задача вызывающей стороны, см. AppUserAuthService.NormalizeAdLogin).</param>
/// <param name="DisplayName">Человеческое имя, если источник его знает. Для AD сегодня совпадает
/// с логином — LDAP-проверка пароля отдельно за displayName не ходит (это был бы лишний запрос к
/// домену на каждый вход). У OIDC сюда ляжет claim <c>name</c>.</param>
/// <param name="Email">Почта, если источник её знает. Для AD сегодня пусто, у OIDC — claim
/// <c>email</c>.</param>
/// <param name="Groups">Группы/роли, пришедшие от источника. Для AD сегодня ПУСТО и это
/// намеренно: роль по группам определяет WindowsGroupAuth.DetectRoleForUser тем же вызовом, что и
/// раньше (см. AdIdentityProvider). У OIDC сюда ляжет claim групп, и тогда появится настраиваемое
/// соответствие «claim → роль приложения».</param>
/// <param name="ExpiresAt">Когда подтверждение перестаёт быть действительным (для токена OIDC —
/// его exp). Для AD — null: срок «не переспрашивать» задаёт сам пользователь в окне входа и
/// хранится отдельно (AdSessionService), к сроку жизни подтверждения он отношения не имеет.</param>
/// <param name="FailureReason">Человекочитаемая причина отказа — ровно тот текст, который
/// показывается в окне входа.</param>
/// <param name="Failure">Классификация отказа, см. <see cref="IdentityFailureKind"/>.</param>
public record IdentityResult(
    bool Success,
    string UserName,
    string DisplayName,
    string Email,
    IReadOnlyList<string> Groups,
    DateTime? ExpiresAt,
    string? FailureReason,
    IdentityFailureKind Failure)
{
    public static IdentityResult Ok(string userName, string? displayName = null, string email = "",
        IReadOnlyList<string>? groups = null, DateTime? expiresAt = null) =>
        new(true, userName, displayName ?? userName, email, groups ?? Array.Empty<string>(), expiresAt, null, IdentityFailureKind.None);

    public static IdentityResult Fail(IdentityFailureKind kind, string? reason, string userName = "") =>
        new(false, userName, userName, "", Array.Empty<string>(), null, reason, kind);
}

/// <summary>Слой над проверкой пароля: «получи подтверждённую личность», а не «проверь логин с
/// паролем». Введён специально под будущий корпоративный SSO (см.
/// docs/corporate-auth-and-network.md): при OpenID Connect приложение НЕ должно видеть пароль
/// вовсе, поэтому <see cref="IAdCredentialValidator"/> (метод «проверь логин+пароль») для него не
/// подходит по форме — а этот интерфейс подходит для обоих случаев одинаково.
///
/// Сегодня существует ровно одна реализация — <see cref="AdIdentityProvider"/>, которая внутри
/// зовёт нынешний <see cref="IAdCredentialValidator"/> и ничего в поведении входа не меняет.
///
/// ТОЧКА РАСШИРЕНИЯ ПОД OIDC (сознательно НЕ реализована — нет поднятого IdP и выданного
/// client_id, см. п.8 docs/corporate-auth-and-network.md):
/// рядом появится <c>OidcIdentityProvider : IIdentityProvider</c>, который в SignInAsync
///   1) поднимает временный HTTP-слушатель на <c>http://127.0.0.1:{свободный порт}/</c>
///      (loopback-redirect — стандарт для настольных приложений; встроенный WebView2 многие IdP
///      блокируют намеренно, поэтому именно системный браузер);
///   2) открывает системный браузер на authorization endpoint (authorization code + PKCE);
///   3) меняет код на токены, читает claims и возвращает <see cref="IdentityResult"/> с
///      заполненными Groups/Email/ExpiresAt.
/// Гейт входа при этом не переписывается: он уже зовёт SignInAsync и работает с результатом.
/// Единственное, что нужно будет добавить рядом, — настраиваемое соответствие «claim групп → роль
/// приложения» (по образцу нынешних настроек ad_group_administrator/_programmer/_naladchik, только
/// сопоставляются не имена групп AD, а значения claim'а): без него после переезда на SSO все
/// пользователи получат роль по умолчанию. Место, где это соответствие применится, — та же ветка
/// гейта, где сейчас вызывается WindowsGroupAuth.DetectRoleForUser (см. AdStartupLoginDialog).</summary>
public interface IIdentityProvider
{
    /// <summary>Никогда не бросает: любая неудача — это результат с Success=false и заполненными
    /// FailureReason/Failure (тот же контракт, что у <see cref="IAdCredentialValidator.Validate"/>,
    /// который эта обёртка сохраняет один в один).</summary>
    Task<IdentityResult> SignInAsync(CancellationToken ct);
}
