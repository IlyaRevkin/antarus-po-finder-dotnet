using System.Windows;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

public record RoleOption(string RoleId, string Label);

public partial class RoleSwitchDialog : Window
{
    private readonly ConfigService _cfg;

    public string? SelectedRole { get; private set; }

    public RoleSwitchDialog(ConfigService cfg, string currentRole)
    {
        InitializeComponent();
        _cfg = cfg;

        foreach (var (roleId, label) in RolesConfig.Roles)
            RoleCombo.Items.Add(new RoleOption(roleId, label));
        RoleCombo.SelectedValue = currentRole;

        AdDomainInput.Text = _cfg.Get("ad_domain");
    }

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

        if (!WindowsGroupAuth.ValidateAdCredentials(domain, login, password, out var authError))
        {
            ErrorText.Text = authError ?? "Не удалось войти — проверьте логин и пароль.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        var role = WindowsGroupAuth.DetectRoleForUser(_cfg, domain, login, password);
        if (role is null)
        {
            ErrorText.Text = $"Логин и пароль верны, но «{login}» не входит ни в одну из настроенных AD-групп ролей.\n" +
                              "Настройте группы в Настройки → Общие, либо войдите паролем роли.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        SelectedRole = role;
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
