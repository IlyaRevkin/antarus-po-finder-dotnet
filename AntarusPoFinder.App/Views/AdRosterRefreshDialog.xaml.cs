using System.Windows;

namespace AntarusPoFinder.App.Views;

/// <summary>Спрашивает, ОТ ЧЬЕГО ИМЕНИ ходить в домен за составом групп — перед «Обновить из
/// домена» на вкладке «Пользователи» (см. SettingsView.RefreshUsersFromDomain_Click).
///
/// Зачем вообще спрашивать. По умолчанию состав групп читается учётной записью текущего сеанса
/// Windows, и в обычном случае этого достаточно — администратор сидит под своей доменной учёткой.
/// Но ровно там же, где это приложение и живёт, машина нередко работает под общей или локальной
/// учётной записью (см. комментарий к WindowsGroupAuth: оператор может быть залогинен в Windows не
/// своей AD-личностью). Тогда запрос в домен уйдёт от аккаунта, которого в домене нет, и ответ
/// будет «недостаточно прав» — а человек подумает, что опять сломался список. Поэтому галочка
/// снимается и вводятся доменные логин с паролем.
///
/// Пароль никуда не сохраняется: он живёт только на время одного обхода групп.</summary>
public partial class AdRosterRefreshDialog : Window
{
    public string Domain { get; private set; } = "";

    /// <summary>Пусто — ходить от текущего сеанса Windows.</summary>
    public string Login { get; private set; } = "";
    public string Password { get; private set; } = "";

    public AdRosterRefreshDialog(string domain, string suggestedLogin)
    {
        InitializeComponent();
        DomainInput.Text = domain;
        LoginInput.Text = suggestedLogin;
    }

    private void UseCurrentSession_Changed(object sender, RoutedEventArgs e)
    {
        // Конструктор ещё не отработал — InitializeComponent поднимает Checked до того, как поля
        // существуют. Без этой проверки окно падало бы на разборе разметки (та самая XAML-гонка
        // инициализации, о которой предупреждает CLAUDE.md).
        if (CredentialsPanel is null) return;
        CredentialsPanel.Visibility = UseCurrentSessionCheck.IsChecked == true
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var domain = DomainInput.Text.Trim();
        if (domain.Length == 0)
        {
            ErrorText.Text = "Укажите домен.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (UseCurrentSessionCheck.IsChecked == true)
        {
            Login = "";
            Password = "";
        }
        else
        {
            var login = LoginInput.Text.Trim();
            if (login.Length == 0)
            {
                ErrorText.Text = "Укажите доменный логин — или верните галочку «под учётной записью текущего сеанса».";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
            Login = login;
            Password = PasswordInput.Password;
        }

        Domain = domain;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
