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
            "administrator" when password != _cfg.AdminPassword() => "Неверный пароль администратора.",
            "programmer" when !string.IsNullOrEmpty(_cfg.ProgrammerPassword()) && password != _cfg.ProgrammerPassword()
                => "Неверный пароль программиста.",
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
