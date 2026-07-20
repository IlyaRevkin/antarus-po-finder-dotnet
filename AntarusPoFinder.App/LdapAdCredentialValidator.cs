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
}

// Способ №2 (заготовка, НЕ реализован): HTTP-проверка логина/пароля через внутренний веб-сервер
// компании (напр. disk.antarus.su) вместо прямого LDAP-бинда — понадобится, если у части рабочих
// мест наладчиков нет прямого сетевого доступа к контроллеру домена, но есть доступ к внутреннему
// сайту. Формат запроса/ответа НЕ подтверждён IT на момент написания этого кода (реальный AD-домен
// был недоступен из песочницы разработки — см. заметки сессии) — реализовать здесь
// HttpAdCredentialValidator : IAdCredentialValidator, когда появится рабочий контракт, и передать
// его вместо LdapAdCredentialValidator туда, где он сейчас конструируется (RoleSwitchDialog) —
// остальную логику входа (AppUserAuthService, ростер, UI) менять не придётся.
