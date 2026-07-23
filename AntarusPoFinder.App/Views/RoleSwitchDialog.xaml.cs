using System.Windows;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;

using AntarusPoFinder.App;

namespace AntarusPoFinder.App.Views;

public record RoleOption(string RoleId, string Label);

public partial class RoleSwitchDialog : Window
{
    private readonly AppServices _services;
    private readonly ConfigService _cfg;
    private readonly Database _db;
    private readonly IAdCredentialValidator _adValidator;

    public string? SelectedRole { get; private set; }

    /// <summary>adValidator defaults to whatever Настройки → Общие → «Способ проверки» currently
    /// selects (see AdCredentialValidatorFactory — LDAP, HTTP or both) — tests pass a fake instead,
    /// since the app has no access to a real AD domain/web server in a sandbox (see
    /// AppUserAuthServiceTests in AntarusPoFinder.Tests for the login-logic coverage this enables;
    /// this dialog itself is only exercised live, with the default validator, against a real or
    /// unreachable domain/server).</summary>
    public RoleSwitchDialog(AppServices services, string currentRole, IAdCredentialValidator? adValidator = null)
    {
        InitializeComponent();
        _services = services;
        _cfg = services.Cfg;
        _db = services.Db;
        _adValidator = adValidator ?? AdCredentialValidatorFactory.Create(_cfg);

        foreach (var (roleId, label) in RolesConfig.Roles)
            RoleCombo.Items.Add(new RoleOption(roleId, label));
        RoleCombo.SelectedValue = currentRole;

        AdDomainInput.Text = _cfg.Get("ad_domain");

        RememberCombo.ItemsSource = RememberOptions.All(_cfg.AdRequireLoginDefaultDays());
        RememberCombo.SelectedValuePath = "Key";
        RememberCombo.SelectedValue = RememberOptions.DefaultKey;
    }

    /// <summary>Best-effort personalization, not a security check: if this login already chose a
    /// "remember me" duration on this machine before, pre-select it instead of always defaulting
    /// back to "как задано администратором" — see RecordRememberChoice for where it's saved.</summary>
    private void AdLoginInput_LostFocus(object sender, RoutedEventArgs e)
    {
        var login = AdLoginInput.Text.Trim();
        if (login.Length == 0) return;

        var session = _db.GetAdLoginSession(AppUserAuthService.NormalizeAdLogin(login));
        RememberCombo.SelectedValue = session?.Mode switch
        {
            AntarusPoFinder.Core.Services.AdSessionMode.Always => RememberOptions.AlwaysKey,
            AntarusPoFinder.Core.Services.AdSessionMode.Custom => session.CustomDays.ToString(),
            _ => RememberOptions.DefaultKey,
        };
    }

    private void RememberCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RememberAlwaysWarning.Visibility = (string?)RememberCombo.SelectedValue == RememberOptions.AlwaysKey
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Persists whatever the "Запомнить вход" combo is currently set to for this login on
    /// this machine — called right after a successful AD auth, both from the AD-group branch and the
    /// app-roster branch of <see cref="AdAuth_Click"/>, and also mirrored in AdStartupLoginDialog for
    /// the mandatory-gate path (see AdSessionService for the shared expiry logic).</summary>
    private void RecordRememberChoice(string normalizedLogin)
    {
        var key = (string?)RememberCombo.SelectedValue ?? RememberOptions.DefaultKey;
        var mode = key switch
        {
            RememberOptions.AlwaysKey => AntarusPoFinder.Core.Services.AdSessionMode.Always,
            RememberOptions.DefaultKey => AntarusPoFinder.Core.Services.AdSessionMode.Default,
            _ => AntarusPoFinder.Core.Services.AdSessionMode.Custom,
        };
        var customDays = mode == AntarusPoFinder.Core.Services.AdSessionMode.Custom && int.TryParse(key, out var d) ? d : 0;
        AntarusPoFinder.Core.Services.AdSessionService.RecordLogin(
            _db, normalizedLogin, mode, customDays, _cfg.AdRequireLoginDefaultDays(), System.DateTime.Now);
        _cfg.SetAdLastLogin(normalizedLogin);
    }

    /// <summary>Способ 1 (Часть 1) — AD-группа, если настроена и логин в неё входит, побеждает: это
    /// то, что видит и поддерживает IT, если вообще поддерживает. Способ 2 (Часть 2) — собственный
    /// ростер приложения — подхватывает всех остальных, включая самый первый вход этого логина, без
    /// участия IT и без блокировки/ожидания одобрения администратора. Оба способа используют один и
    /// тот же ввод (домен/логин/пароль) и одну и ту же проверку пароля — отличается только то,
    /// откуда берётся роль после успешной проверки.</summary>
    private void AdAuth_Click(object sender, RoutedEventArgs e)
    {
        var domain = AdDomainInput.Text.Trim();
        var login = AdLoginInput.Text.Trim();
        var password = AdPasswordInput.Password;

        if (string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(login))
        {
            ErrorText.Text = "Укажите домен и логин.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (!_adValidator.Validate(domain, login, password, out var authError))
        {
            ErrorText.Text = authError ?? "Не удалось войти — проверьте логин и пароль.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        var normalized = AppUserAuthService.NormalizeAdLogin(login);

        var groupRole = WindowsGroupAuth.DetectRoleForUser(_cfg, domain, login, password);
        if (groupRole is not null)
        {
            RecordRememberChoice(normalized);
            _services.CurrentAdLogin = normalized;
            SelectedRole = groupRole;
            DialogResult = true;
            return;
        }

        var isNewUser = _db.FindAppUserByLogin(normalized) is null;
        var user = _db.TouchOrCreateAppUser(normalized);

        // Best-effort — see ConfigSyncService.PushAppUsersOnly: this machine may not be an
        // administrator (the only role with a full config push), so without this, a brand new AD
        // login here would sit in this machine's local roster forever and never reach any other
        // machine, including the administrator's own "Пользователи" list. Never blocks login on a
        // slow/unreachable share.
        try { ConfigSyncService.PushAppUsersOnly(_services, _cfg.RootPath(), $"{login} ({RolesConfig.RoleLabel(user.Role)})"); }
        catch { /* share unreachable — next successful login or manual retry will catch it up */ }

        if (isNewUser)
            AppMessageBox.Show(
                $"Первый вход «{login}» — назначена роль «{RolesConfig.RoleLabel(user.Role)}».\n" +
                "Администратор может изменить её в Настройки → Пользователи.",
                "Новый пользователь", MessageBoxButton.OK, MessageBoxImage.Information);

        RecordRememberChoice(normalized);
        _services.CurrentAdLogin = normalized;
        SelectedRole = user.Role;
        DialogResult = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (RoleCombo.SelectedItem is not RoleOption selected)
            return;

        var password = PasswordInput.Password;
        string? error = selected.RoleId switch
        {
            "administrator" when !_cfg.VerifyAdminPassword(password) => "Неверный пароль администратора.",
            "programmer" when !_cfg.VerifyProgrammerPassword(password) => "Неверный пароль программиста.",
            _ => null,
        };

        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        SelectedRole = selected.RoleId;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
