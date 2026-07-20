namespace AntarusPoFinder.Core.Services;

/// <summary>Способ="оба" (Настройки → Общие → «Способ проверки») — пробует <paramref name="primary"/>
/// (обычно LDAP, способ №1) и переходит на <paramref name="fallback"/> (обычно HTTP, способ №2)
/// ТОЛЬКО когда primary не смог даже достучаться до цели (<see cref="AdValidationStatus.Unavailable"/>),
/// а не когда пароль был просто неверным — иначе "я ошибся в пароле" превращалось бы в запутанный
/// повторный запрос ко второму способу вместо понятной ошибки. Работает с любой парой
/// IAdCredentialValidator, не только LDAP+HTTP — так и тестируется (с двумя фейками).</summary>
public class CombinedAdCredentialValidator : IAdCredentialValidator
{
    private readonly IAdCredentialValidator _primary;
    private readonly IAdCredentialValidator _fallback;

    public CombinedAdCredentialValidator(IAdCredentialValidator primary, IAdCredentialValidator fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public bool Validate(string domain, string login, string password, out string? error) =>
        ValidateWithStatus(domain, login, password, out error) == AdValidationStatus.Success;

    public AdValidationStatus ValidateWithStatus(string domain, string login, string password, out string? error)
    {
        var primaryStatus = _primary.ValidateWithStatus(domain, login, password, out var primaryError);
        if (primaryStatus != AdValidationStatus.Unavailable)
        {
            error = primaryError;
            return primaryStatus;
        }

        var fallbackStatus = _fallback.ValidateWithStatus(domain, login, password, out var fallbackError);
        if (fallbackStatus == AdValidationStatus.Unavailable)
        {
            // Neither способ could even be reached — report both, so the operator (or whoever reads
            // the logs) doesn't have to guess which one failed and how.
            error = $"LDAP: {primaryError}\nHTTP: {fallbackError}";
            return AdValidationStatus.Unavailable;
        }

        error = fallbackError;
        return fallbackStatus;
    }
}
