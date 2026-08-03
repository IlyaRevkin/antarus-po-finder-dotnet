using System;
using System.Threading;
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
    /// <summary>Ненулевой только в тестах: валидатор передали снаружи, тогда он и используется. В
    /// боевом пути валидатор строится в AdAuth_Click по текущим полям домена/сервера (их могли
    /// поправить в «Доп. параметрах»), а не фиксируется в конструкторе — иначе правка адреса сервера
    /// не подхватилась бы до перезапуска.</summary>
    private readonly IAdCredentialValidator? _injectedValidator;

    public string? SelectedRole { get; private set; }

    public AdStartupLoginDialog(AppServices services, IAdCredentialValidator? adValidator = null)
    {
        InitializeComponent();
        _services = services;
        _cfg = services.Cfg;
        _injectedValidator = adValidator;

        AdDomainInput.Text = _cfg.Get("ad_domain");
        AdHttpUrlInput.Text = _cfg.AdHttpUrl();

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
    private async void AdAuth_Click(object sender, RoutedEventArgs e)
    {
        var domain = AdDomainInput.Text.Trim();
        var login = AdLoginInput.Text.Trim();
        var password = AdPasswordInput.Password;
        var httpUrl = AdHttpUrlInput.Text.Trim();

        if (string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(login))
        {
            ShowError("Укажите домен и логин.");
            return;
        }

        // Вход идёт через IIdentityProvider (см. AdIdentityProvider) — слой «получи подтверждённую
        // личность» вместо прямого «проверь логин+пароль». Поведение то же самое: провайдер внутри
        // зовёт тот же самый IAdCredentialValidator, что вызывался здесь раньше. Смысл перехода —
        // будущий OIDC, при котором приложение вообще не увидит пароль (docs/corporate-auth-and-network.md).
        // Оба делегата ниже — это буквально те два действия, что стояли здесь до рефакторинга, и в том
        // же порядке: сначала сохранить правки домена/сервера, потом собрать валидатор по текущим полям.
        var provider = new AdIdentityProvider(
            () =>
            {
                // Правки домена/сервера (из «Доп. параметров») сохраняем ДО проверки: во-первых, их
                // подхватит фабрика валидатора ниже (иначе новый адрес HTTP-сервера заработал бы только
                // после перезапуска), во-вторых, они закрепляются на этой машине — оператору не придётся
                // вписывать их заново при следующем входе, если дефолт не подошёл. Per-machine, как весь AD-блок.
                _cfg.Set("ad_domain", domain);
                _cfg.SetAdHttpUrl(httpUrl);
                return new AdCredentials(domain, login, password);
            },
            // Валидатор пересобирается на КАЖДОЕ нажатие по текущим настройкам — как и раньше
            // (см. комментарий к _injectedValidator), а не фиксируется в конструкторе окна.
            _ => _injectedValidator ?? AdCredentialValidatorFactory.Create(_cfg));

        // SignInAsync у AD-провайдера возвращает уже завершённый Task (проверка пароля осталась
        // синхронной), поэтому await продолжается в том же кадре — порядок действий ниже не меняется.
        var identity = await provider.SignInAsync(CancellationToken.None);
        if (!identity.Success)
        {
            ShowError(identity.FailureReason ?? "Не удалось войти — проверьте логин и пароль.");
            // Домен/сервер не ответили вовсе (не «неверный пароль») — под IPsec это штатная ситуация
            // сразу после включения компьютера: туннель поднимается ПОЗЖЕ старта приложения. Даём явный
            // повтор и диагностику вместо единственного исхода «не вошёл».
            RetryPanel.Visibility = identity.Failure == IdentityFailureKind.Unavailable
                ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        RetryPanel.Visibility = Visibility.Collapsed;

        var normalized = AppUserAuthService.NormalizeAdLogin(login);
        // ТОЧКА РАСШИРЕНИЯ ПОД OIDC (сейчас НЕ реализована, см. IIdentityProvider): у AD-провайдера
        // identity.Groups пуст, и роль по-прежнему определяется прямым запросом групп в AD — ровно тем
        // же вызовом, что и до появления слоя провайдеров. Когда появится OIDC-провайдер, группы
        // придут в identity.Groups claim'ом, и здесь встанет ветка «если Groups непусты — сопоставить
        // claim с ролью приложения по настраиваемой таблице», а этот вызов останется только для AD.
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

    /// <summary>Диагностика прямо из окна входа — до главного окна, где живут Настройки, здесь ещё
    /// не дошли. Показывает, что именно недоступно (диск, домен/сервер входа, источник обновлений),
    /// и даёт скопировать результат.</summary>
    private void ShowConnectionStatus_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ConnectionStatusDialog(_cfg) { Owner = this };
        dlg.ShowDialog();
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }
}
