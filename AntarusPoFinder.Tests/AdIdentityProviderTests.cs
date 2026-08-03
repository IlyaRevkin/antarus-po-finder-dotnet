using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.App;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Гарантия того, что переход окна входа с «проверь логин+пароль» (IAdCredentialValidator)
/// на «получи подтверждённую личность» (IIdentityProvider) НИЧЕГО не изменил в поведении входа.
/// Способ доказательства: для каждого из трёх режимов проверки (ldap / http / both) один и тот же
/// набор входных данных прогоняется двумя путями — напрямую через валидатор (как было до
/// рефакторинга) и через AdIdentityProvider (как стало) — и результаты сверяются: и «пустил/не
/// пустил», и текст ошибки.</summary>
public class AdIdentityProviderTests
{
    /// <summary>Валидатор со сценарием: отвечает заранее заданным статусом и текстом. Заменяет и
    /// LDAP-бинд, и HTTP-проверку — оба в тестах недопустимы (реальный домен/сервер).</summary>
    private sealed class ScriptedValidator : IAdCredentialValidator
    {
        private readonly AdValidationStatus _status;
        private readonly string? _error;
        public int Calls { get; private set; }

        public ScriptedValidator(AdValidationStatus status, string? error)
        {
            _status = status;
            _error = error;
        }

        public bool Validate(string domain, string login, string password, out string? error) =>
            ValidateWithStatus(domain, login, password, out error) == AdValidationStatus.Success;

        public AdValidationStatus ValidateWithStatus(string domain, string login, string password, out string? error)
        {
            Calls++;
            error = _status == AdValidationStatus.Success ? null : _error;
            return _status;
        }
    }

    /// <summary>Собирает валидатор ровно так же, как это делает боевая AdCredentialValidatorFactory
    /// для каждого режима (см. её код), но из подставных «способов» — иначе тест полез бы в реальный
    /// домен и на реальный веб-сервер.</summary>
    private static IAdCredentialValidator BuildForMode(string mode, IAdCredentialValidator ldap, IAdCredentialValidator http) =>
        mode switch
        {
            "http" => http,
            "both" => new CombinedAdCredentialValidator(ldap, http),
            _ => ldap,
        };

    private static readonly AdCredentials Creds = new("Elita", "ivanov.i", "секрет");

    private static AdIdentityProvider ProviderOver(IAdCredentialValidator validator, Action? beforeValidate = null) =>
        new(() => { beforeValidate?.Invoke(); return Creds; }, _ => validator);

    public static IEnumerable<object[]> AllModes() => new[]
    {
        new object[] { "ldap" },
        new object[] { "http" },
        new object[] { "both" },
    };

    // ── Совпадение со «старым» путём во всех трёх режимах ────────────────────

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task SignInAsync_SuccessfulLogin_MatchesDirectValidatorResult(string mode)
    {
        var direct = BuildForMode(mode,
            new ScriptedValidator(AdValidationStatus.Success, null),
            new ScriptedValidator(AdValidationStatus.Success, null));
        var oldPathOk = direct.Validate(Creds.Domain, Creds.Login, Creds.Password, out var oldPathError);

        var provider = ProviderOver(BuildForMode(mode,
            new ScriptedValidator(AdValidationStatus.Success, null),
            new ScriptedValidator(AdValidationStatus.Success, null)));
        var identity = await provider.SignInAsync(CancellationToken.None);

        Assert.True(oldPathOk);
        Assert.Equal(oldPathOk, identity.Success);
        Assert.Equal(oldPathError, identity.FailureReason);
        Assert.Equal(IdentityFailureKind.None, identity.Failure);
        Assert.Equal(Creds.Login, identity.UserName);
        // Группы у AD-провайдера намеренно пусты — роль по-прежнему определяет WindowsGroupAuth в
        // окне входа (см. AdIdentityProvider). Если это когда-нибудь изменится, изменится и поведение.
        Assert.Empty(identity.Groups);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task SignInAsync_WrongPassword_MatchesDirectValidatorResultAndErrorText(string mode)
    {
        const string rejected = "Неверный логин или пароль.";
        var direct = BuildForMode(mode,
            new ScriptedValidator(AdValidationStatus.InvalidCredentials, rejected),
            new ScriptedValidator(AdValidationStatus.InvalidCredentials, rejected));
        var oldPathOk = direct.Validate(Creds.Domain, Creds.Login, Creds.Password, out var oldPathError);

        var provider = ProviderOver(BuildForMode(mode,
            new ScriptedValidator(AdValidationStatus.InvalidCredentials, rejected),
            new ScriptedValidator(AdValidationStatus.InvalidCredentials, rejected)));
        var identity = await provider.SignInAsync(CancellationToken.None);

        Assert.False(oldPathOk);
        Assert.Equal(oldPathOk, identity.Success);
        Assert.Equal(oldPathError, identity.FailureReason);
        // Неверный пароль — это НЕ повод предлагать «Проверить снова»: повторять нечего.
        Assert.Equal(IdentityFailureKind.Rejected, identity.Failure);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task SignInAsync_TargetUnreachable_MatchesDirectValidatorAndIsClassifiedAsUnavailable(string mode)
    {
        const string down = "Не удалось связаться с доменом «Elita» — проверьте сеть/имя домена.";
        var direct = BuildForMode(mode,
            new ScriptedValidator(AdValidationStatus.Unavailable, down),
            new ScriptedValidator(AdValidationStatus.Unavailable, down));
        var oldPathOk = direct.Validate(Creds.Domain, Creds.Login, Creds.Password, out var oldPathError);

        var provider = ProviderOver(BuildForMode(mode,
            new ScriptedValidator(AdValidationStatus.Unavailable, down),
            new ScriptedValidator(AdValidationStatus.Unavailable, down)));
        var identity = await provider.SignInAsync(CancellationToken.None);

        Assert.False(oldPathOk);
        Assert.Equal(oldPathOk, identity.Success);
        Assert.Equal(oldPathError, identity.FailureReason);
        // Именно этот случай включает в окне входа кнопку «Проверить снова» (туннель IPsec мог ещё
        // не подняться) — и только он.
        Assert.Equal(IdentityFailureKind.Unavailable, identity.Failure);
    }

    [Fact]
    public async Task SignInAsync_BothMode_FallsBackToHttpExactlyLikeBefore()
    {
        // Способ «оба»: LDAP не достучался — идём на HTTP; неверный пароль от LDAP на HTTP НЕ уводит.
        var ldapDown = new ScriptedValidator(AdValidationStatus.Unavailable, "домен недоступен");
        var httpOk = new ScriptedValidator(AdValidationStatus.Success, null);
        var identity = await ProviderOver(new CombinedAdCredentialValidator(ldapDown, httpOk)).SignInAsync(CancellationToken.None);
        Assert.True(identity.Success);
        Assert.Equal(1, ldapDown.Calls);
        Assert.Equal(1, httpOk.Calls);

        var ldapRejects = new ScriptedValidator(AdValidationStatus.InvalidCredentials, "Неверный логин или пароль.");
        var httpNeverCalled = new ScriptedValidator(AdValidationStatus.Success, null);
        var rejected = await ProviderOver(new CombinedAdCredentialValidator(ldapRejects, httpNeverCalled)).SignInAsync(CancellationToken.None);
        Assert.False(rejected.Success);
        Assert.Equal(0, httpNeverCalled.Calls);
    }

    // ── Сохранённые особенности окна входа ───────────────────────────────────

    [Fact]
    public async Task SignInAsync_SavesEditedSettingsBeforeValidating()
    {
        // В окне входа правки домена/адреса сервера сохраняются ДО проверки — иначе новый адрес
        // HTTP-сервера не подхватился бы фабрикой валидатора. Порядок обязан сохраниться.
        var saved = false;
        var validatorBuiltAfterSave = false;
        var provider = new AdIdentityProvider(
            () => { saved = true; return Creds; },
            _ =>
            {
                validatorBuiltAfterSave = saved;
                return new ScriptedValidator(AdValidationStatus.Success, null);
            });

        await provider.SignInAsync(CancellationToken.None);

        Assert.True(saved);
        Assert.True(validatorBuiltAfterSave);
    }

    [Fact]
    public async Task SignInAsync_RebuildsValidatorOnEveryAttempt()
    {
        // Валидатор не фиксируется один раз: если между попытками поправили адрес сервера в
        // «Дополнительных параметрах», вторая попытка обязана пойти уже по новому адресу.
        var builds = 0;
        var provider = new AdIdentityProvider(
            () => Creds,
            _ => { builds++; return new ScriptedValidator(AdValidationStatus.InvalidCredentials, "нет"); });

        await provider.SignInAsync(CancellationToken.None);
        await provider.SignInAsync(CancellationToken.None);

        Assert.Equal(2, builds);
    }

    [Fact]
    public async Task SignInAsync_NoCredentialsSupplied_FailsWithoutTouchingValidator()
    {
        var validator = new ScriptedValidator(AdValidationStatus.Success, null);
        var provider = new AdIdentityProvider(() => null, _ => validator);

        var identity = await provider.SignInAsync(CancellationToken.None);

        Assert.False(identity.Success);
        Assert.Equal(0, validator.Calls);
    }

    // ── Фабрика режимов не изменилась ────────────────────────────────────────

    [Theory]
    [InlineData("ldap", typeof(LdapAdCredentialValidator))]
    [InlineData("http", typeof(HttpAdCredentialValidator))]
    [InlineData("both", typeof(CombinedAdCredentialValidator))]
    [InlineData("что-то непонятное", typeof(LdapAdCredentialValidator))]
    public void AdCredentialValidatorFactory_StillMapsModeToTheSameImplementation(string mode, Type expected)
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);
        cfg.Set("ad_auth_mode", mode);

        // Только тип — сам валидатор здесь не вызывается, в сеть/домен тест не ходит.
        Assert.IsType(expected, AdCredentialValidatorFactory.Create(cfg));
    }
}
