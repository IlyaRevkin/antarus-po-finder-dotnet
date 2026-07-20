using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App;

/// <summary>Способ №1 (см. IAdCredentialValidator) — прямой LDAP-бинд. Просто оборачивает уже
/// рабочий WindowsGroupAuth.ValidateAdCredentials в тестируемый контракт, так что
/// AppUserAuthService.Login (AntarusPoFinder.Core) можно покрыть юнит-тестами с фейковой
/// реализацией, не завися от System.DirectoryServices.AccountManagement/реального AD.</summary>
public class LdapAdCredentialValidator : IAdCredentialValidator
{
    public bool Validate(string domain, string login, string password, out string? error) =>
        WindowsGroupAuth.ValidateAdCredentials(domain, login, password, out error);

    public AdValidationStatus ValidateWithStatus(string domain, string login, string password, out string? error)
    {
        var ok = WindowsGroupAuth.ValidateAdCredentials(domain, login, password, out error, out var unavailable);
        if (ok) return AdValidationStatus.Success;
        return unavailable ? AdValidationStatus.Unavailable : AdValidationStatus.InvalidCredentials;
    }
}
