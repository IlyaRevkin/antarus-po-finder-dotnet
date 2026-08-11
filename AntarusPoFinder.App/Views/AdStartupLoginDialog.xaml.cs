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

        ApplyAuthMode();
    }

    /// <summary>Какой способ входа показывать. «oidc» с заполненными параметрами — корпоративный вход
    /// как основной способ: панель Keycloak сверху, а вход по доменной учётной записи свёрнут в
    /// запасной (раскрывается кнопкой или сам при недоступности сервера входа — см. SsoAuth_Click и
    /// RevealAdFallback). Ни в одном режиме вход по AD не убирается совсем: переключение всех машин
    /// на Keycloak не должно запирать тех, у кого он в этот момент недоступен (см.
    /// StartupLoginOptions). Способ oidc выбран, но не настроен — молча оставляем обычный вход по AD:
    /// запереть человека окном, в котором нечем войти, хуже любой непоследовательности.</summary>
    private void ApplyAuthMode()
    {
        var mode = _cfg.AdAuthMode();
        var oidcConfigured = _cfg.OidcConfigured();
        var sso = StartupLoginOptions.ShowCorporateLogin(mode, oidcConfigured);

        SsoPanel.Visibility = sso ? Visibility.Visible : Visibility.Collapsed;

        // Вход по AD доступен всегда (StartupLoginOptions.AdFallbackAvailable) — в режиме oidc лишь
        // свёрнут по умолчанию, а раскрывается кнопкой ShowAdFallbackButton или автоматически, если
        // сервер корпоративного входа не ответил.
        var adCollapsed = StartupLoginOptions.AdFallbackCollapsedInitially(mode, oidcConfigured);
        AdPanel.Visibility = adCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ShowAdFallbackButton.Visibility = adCollapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Раскрыть запасной вход по доменной учётной записи, не покидая окно. Вызывается по
    /// кнопке «Войти по доменной учётной записи» и автоматически, когда сервер корпоративного входа
    /// не ответил (<paramref name="offerMessage"/>=true — тогда над панелью появляется явная
    /// подсказка, что делать дальше, вместо тупика «не вошёл»).</summary>
    private void RevealAdFallback(bool offerMessage)
    {
        AdPanel.Visibility = Visibility.Visible;
        ShowAdFallbackButton.Visibility = Visibility.Collapsed;
        AdLoginInput.Focus();
        if (offerMessage)
        {
            SsoStatus.Visibility = Visibility.Visible;
            SsoStatus.Text = "Сервер корпоративного входа не отвечает. Войдите по доменной учётной записи ниже " +
                             "или, если это тоже недоступно, паролем администратора внизу окна.";
        }
    }

    private void ShowAdFallback_Click(object sender, RoutedEventArgs e) => RevealAdFallback(offerMessage: false);

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
        // У AD-провайдера identity.Groups пуст намеренно: роль определяется прямым запросом групп в
        // AD — тем же вызовом, что и до появления слоя провайдеров. Ветка «группы пришли claim'ом»
        // живёт в SsoAuth_Click ниже и считает роль по тем же настройкам через RoleFromGroups.
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

    /// <summary>Корпоративный вход (Keycloak / OpenID Connect): браузер, PKCE, токен — см.
    /// <see cref="OidcIdentityProvider"/>. Пароля здесь нет вообще, поэтому и роль определяется иначе:
    /// группы приходят claim'ом в самом токене (<see cref="RoleFromGroups"/>), а не спрашиваются у
    /// домена — это ровно та ветка, которую предусматривал комментарий в AdAuth_Click.
    ///
    /// Всё остальное — как у AD-входа: запомнить вход на этой машине, ростер приложения при
    /// несовпадении групп (новому человеку достаётся минимальная роль), сообщение о первом входе.</summary>
    private async void SsoAuth_Click(object sender, RoutedEventArgs e)
    {
        SsoButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        SsoStatus.Visibility = Visibility.Visible;
        SsoStatus.Text = "Открыт браузер — завершите вход на странице компании. Это окно ждёт ответа.";
        try
        {
            var provider = new OidcIdentityProvider(_cfg.OidcAuthority(), _cfg.OidcClientId(), _cfg.OidcGroupsClaim());
            var identity = await provider.SignInAsync(CancellationToken.None);
            if (!identity.Success)
            {
                ShowError(identity.FailureReason ?? "Корпоративный вход не выполнен.");
                // Сервер входа не ответил (не «отказал в доступе») — это тупик, если не предложить
                // запасной путь: раскрываем вход по доменной учётной записи прямо здесь и говорим,
                // что делать. Пользователь мог сам отменить/ошибиться (Rejected) — тогда просто
                // показываем причину, панель AD остаётся свёрнутой (её всё равно видно по кнопке).
                if (identity.Failure == IdentityFailureKind.Unavailable)
                    RevealAdFallback(offerMessage: true);
                else
                    SsoStatus.Visibility = Visibility.Collapsed;
                return;
            }

            var normalized = AppUserAuthService.NormalizeAdLogin(identity.UserName);
            var role = RoleFromGroups.Detect(_cfg, identity.Groups);
            var isNewUser = false;
            if (role is null)
            {
                isNewUser = _services.Db.FindAppUserByLogin(normalized) is null;
                var user = _services.Db.TouchOrCreateAppUser(normalized);
                role = user.Role;

                // Best-effort, как и в AD-ветке: недоступная шара вход не отменяет.
                try { ConfigSyncService.PushAppUsersOnly(_services, _cfg.RootPath(), $"{identity.UserName} ({RolesConfig.RoleLabel(role)})"); }
                catch { /* поедет со следующим удачным входом или ручной отправкой */ }
            }

            RecordRememberChoice(normalized);
            _services.CurrentAdLogin = normalized;
            _cfg.SetRole(role);
            SelectedRole = role;

            if (isNewUser)
                AppMessageBox.Show(
                    $"Первый вход «{identity.UserName}» — назначена роль «{RolesConfig.RoleLabel(role)}».\n" +
                    "Администратор может изменить её в Настройки → Пользователи.",
                    "Новый пользователь", MessageBoxButton.OK, MessageBoxImage.Information);

            DialogResult = true;
        }
        finally
        {
            SsoButton.IsEnabled = true;
        }
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
