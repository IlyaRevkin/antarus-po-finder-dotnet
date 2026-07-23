using System;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Mandatory AD login gate shown by App.OnStartup before MainWindow ever exists, when
/// Настройки → Общие → «Требовать вход по AD при запуске» is on and there's no still-valid cached
/// session (see AdSessionService) for whichever login last authenticated on this machine. A
/// deliberately separate window from RoleSwitchDialog (the optional in-app "switch role" action)
/// rather than a mode flag on it: this one runs before AppServices' usual wiring is fully in place,
/// has no local role-password picker (identity here must come from AD), and its only way out
/// besides a successful AD login is the administrator escape hatch below — closing the window
/// (Cancel/Alt+F4/the X button) is treated by App.OnStartup as declining to log in, and the whole
/// application exits rather than opening MainWindow half-authenticated.</summary>
public partial class AdStartupLoginDialog : Window
{
    private readonly AppServices _services;
    private readonly ConfigService _cfg;
    private readonly IAdCredentialValidator _adValidator;

    public string? SelectedRole { get; private set; }

    public AdStartupLoginDialog(AppServices services, IAdCredentialValidator? adValidator = null)
    {
        InitializeComponent();
        _services = services;
        _cfg = services.Cfg;
        _adValidator = adValidator ?? AdCredentialValidatorFactory.Create(_cfg);

        AdDomainInput.Text = _cfg.Get("ad_domain");

        RememberCombo.ItemsSource = RememberOptions.All(_cfg.AdRequireLoginDefaultDays());
        RememberCombo.SelectedValuePath = "Key";
        RememberCombo.SelectedValue = RememberOptions.DefaultKey;
    }

    /// <summary>Same personalization as RoleSwitchDialog.AdLoginInput_LostFocus — pre-selects
    /// whatever duration this login chose last time it authenticated on this machine.</summary>
    private void AdLoginInput_LostFocus(object sender, RoutedEventArgs e)
    {
        var login = AdLoginInput.Text.Trim();
        if (login.Length == 0) return;

        var session = _services.Db.GetAdLoginSession(AppUserAuthService.NormalizeAdLogin(login));
        RememberCombo.SelectedValue = session?.Mode switch
        {
            AdSessionMode.Always => RememberOptions.AlwaysKey,
            AdSessionMode.Custom => session.CustomDays.ToString(),
            _ => RememberOptions.DefaultKey,
        };
    }

    private void RememberCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RememberAlwaysWarning.Visibility = (string?)RememberCombo.SelectedValue == RememberOptions.AlwaysKey
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RecordRememberChoice(string normalizedLogin)
    {
        var key = (string?)RememberCombo.SelectedValue ?? RememberOptions.DefaultKey;
        var mode = key switch
        {
            RememberOptions.AlwaysKey => AdSessionMode.Always,
            RememberOptions.DefaultKey => AdSessionMode.Default,
            _ => AdSessionMode.Custom,
        };
        var customDays = mode == AdSessionMode.Custom && int.TryParse(key, out var d) ? d : 0;
        AdSessionService.RecordLogin(_services.Db, normalizedLogin, mode, customDays, _cfg.AdRequireLoginDefaultDays(), DateTime.Now);
        _cfg.SetAdLastLogin(normalizedLogin);
    }

    /// <summary>Mirrors RoleSwitchDialog.AdAuth_Click's group-then-roster resolution (see that
    /// method's doc for why there are two paths) — duplicated rather than shared because this dialog
    /// additionally has to persist the resolved role itself (SetRole) before returning, since there
    /// is no MainWindowViewModel yet to hand it to at this point in startup.</summary>
    private void AdAuth_Click(object sender, RoutedEventArgs e)
    {
        var domain = AdDomainInput.Text.Trim();
        var login = AdLoginInput.Text.Trim();
        var password = AdPasswordInput.Password;

        if (string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(login))
        {
            ShowError("Укажите домен и логин.");
            return;
        }

        if (!_adValidator.Validate(domain, login, password, out var authError))
        {
            ShowError(authError ?? "Не удалось войти — проверьте логин и пароль.");
            return;
        }

        var normalized = AppUserAuthService.NormalizeAdLogin(login);
        var groupRole = WindowsGroupAuth.DetectRoleForUser(_cfg, domain, login, password);

        string role;
        var isNewUser = false;
        if (groupRole is not null)
        {
            role = groupRole;
        }
        else
        {
            isNewUser = _services.Db.FindAppUserByLogin(normalized) is null;
            var user = _services.Db.TouchOrCreateAppUser(normalized);
            role = user.Role;

            // Best-effort, same reasoning as RoleSwitchDialog.AdAuth_Click — never blocks login.
            try { ConfigSyncService.PushAppUsersOnly(_services, _cfg.RootPath(), $"{login} ({RolesConfig.RoleLabel(role)})"); }
            catch { /* share unreachable — next successful login or manual retry will catch it up */ }
        }

        RecordRememberChoice(normalized);
        _services.CurrentAdLogin = normalized;
        _cfg.SetRole(role);
        SelectedRole = role;

        if (isNewUser)
            AppMessageBox.Show(
                $"Первый вход «{login}» — назначена роль «{RolesConfig.RoleLabel(role)}».\n" +
                "Администратор может изменить её в Настройки → Пользователи.",
                "Новый пользователь", MessageBoxButton.OK, MessageBoxImage.Information);

        DialogResult = true;
    }

    /// <summary>The "бутылочное горлышко" escape: even with mandatory AD login on, the shared
    /// administrator password (Настройки → Общие → «Пароли доступа») always still works here,
    /// specifically so a fresh deployment (or a domain outage) never locks everyone out — the
    /// administrator logs in this way once and assigns the right AD login the "administrator" role
    /// in Настройки → Пользователи, after which that person can log in via AD directly. Deliberately
    /// never cached: AdLastLogin/ad_login_sessions are untouched here, so the gate asks again next
    /// launch rather than this becoming a routine bypass.</summary>
    private void AdminEscape_Click(object sender, RoutedEventArgs e)
    {
        // Сравнение через VerifyAdminPassword, не строковое: AdminPassword() с этого раунда всегда
        // хранит хеш (см. ConfigService/PasswordHasher), прямое сравнение с введённым открытым
        // текстом больше никогда бы не совпало — эта правка не входила в исходную зону файлов
        // (см. отчёт), но без неё запасной вход администратора был бы полностью сломан.
        if (!_cfg.VerifyAdminPassword(AdminEscapePasswordInput.Password))
        {
            ShowError("Неверный пароль администратора.");
            return;
        }

        _cfg.SetRole("administrator");
        SelectedRole = "administrator";
        DialogResult = true;
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }
}
