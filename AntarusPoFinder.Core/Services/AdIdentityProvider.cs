using System.Threading;
using System.Threading.Tasks;

namespace AntarusPoFinder.Core.Services;

/// <summary>Домен + логин + пароль ровно в том виде, в каком их ввели в окне входа. Отдельный тип
/// (а не три параметра SignInAsync) потому, что <see cref="IIdentityProvider.SignInAsync"/> по
/// контракту параметров не принимает: у будущего OIDC-провайдера учётных данных нет вовсе —
/// он сам открывает браузер. Здесь их поставляет вызывающая сторона через делегат, см.
/// <see cref="AdIdentityProvider"/>.</summary>
public record AdCredentials(string Domain, string Login, string Password);

/// <summary>AD-реализация <see cref="IIdentityProvider"/>: внутри зовёт нынешний
/// <see cref="IAdCredentialValidator"/> — тот же самый, который до появления этого слоя вызывался
/// из окна входа напрямую. Ничего в поведении входа не меняет, это чистый рефакторинг формы вызова
/// (см. docs/corporate-auth-and-network.md, п.7.3).
///
/// Два делегата вместо готовых значений в конструкторе — это НЕ «архитектура ради архитектуры», а
/// сохранение двух уже существовавших особенностей окна входа, которые иначе бы сломались:
/// • <paramref name="credentialsSource"/> читает поля формы (и попутно сохраняет правки домена/URL
///   из «Дополнительных параметров») в момент нажатия «Войти», а не в момент создания провайдера;
/// • <paramref name="validatorFactory"/> пересобирает валидатор ПРИ КАЖДОЙ попытке по текущим
///   настройкам — иначе правка адреса HTTP-сервера в том же окне не подхватилась бы до перезапуска
///   приложения (ровно то, что раньше обеспечивал вызов AdCredentialValidatorFactory.Create прямо
///   в обработчике кнопки).</summary>
public sealed class AdIdentityProvider : IIdentityProvider
{
    private readonly Func<AdCredentials?> _credentialsSource;
    private readonly Func<AdCredentials, IAdCredentialValidator> _validatorFactory;

    public AdIdentityProvider(
        Func<AdCredentials?> credentialsSource,
        Func<AdCredentials, IAdCredentialValidator> validatorFactory)
    {
        _credentialsSource = credentialsSource;
        _validatorFactory = validatorFactory;
    }

    /// <summary>Выполняется синхронно и возвращает уже завершённый Task — намеренно: проверка пароля
    /// (LDAP-бинд/HTTP-запрос) как была блокирующей, так и осталась, поэтому await над этим
    /// результатом продолжается в том же кадре, без возврата в цикл сообщений. Так порядок действий
    /// в окне входа остаётся ровно прежним. Асинхронной сигнатура сделана ради будущего
    /// OIDC-провайдера, которому ожидание браузера без async не выразить.</summary>
    public Task<IdentityResult> SignInAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var credentials = _credentialsSource();
        if (credentials is null)
            return Task.FromResult(IdentityResult.Fail(IdentityFailureKind.Rejected, null));

        var validator = _validatorFactory(credentials);

        // ValidateWithStatus, а не Validate: обе реальные реализации (LdapAdCredentialValidator,
        // HttpAdCredentialValidator) и их комбинация определяют Validate КАК ValidateWithStatus(...)
        // == Success с тем же текстом ошибки, так что результат и сообщение совпадают бит в бит; а
        // статус дополнительно позволяет отличить «пароль неверный» от «до домена не достучались» —
        // это нужно кнопке «Проверить снова» в окне входа (туннель IPsec мог ещё не подняться).
        // Реализация, у которой ValidateWithStatus не переопределён, получает статус из значения по
        // умолчанию интерфейса (обёртка над Validate) — то есть тоже без изменения поведения.
        var status = validator.ValidateWithStatus(credentials.Domain, credentials.Login, credentials.Password, out var error);
        if (status == AdValidationStatus.Success)
            return Task.FromResult(IdentityResult.Ok(credentials.Login));

        // Groups у AD-провайдера намеренно НЕ заполняются: роль по группам AD определяется тем же
        // вызовом WindowsGroupAuth.DetectRoleForUser в окне входа, что и раньше (переносить его
        // сюда — значит менять поведение входа, чего этот рефакторинг делать не должен). У будущего
        // OIDC-провайдера всё наоборот: группы придут claim'ом в IdentityResult.Groups, а сопоставление
        // «claim → роль» встанет на место того самого вызова.
        return Task.FromResult(IdentityResult.Fail(
            status == AdValidationStatus.Unavailable ? IdentityFailureKind.Unavailable : IdentityFailureKind.Rejected,
            error,
            credentials.Login));
    }
}
