using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App.Views;

/// <summary>Сообщение о баге В ПРОШИВКЕ — то, что открывается жучком на карточке выдачи.
///
/// Отдельное окно, а не общая форма тикета, ради двух вещей, которых у обычного тикета нет:
/// прошивка уже подставлена (её не надо называть словами, и её нельзя назвать неправильно) и
/// критичность выбирается явно. Критичность спрашивается именно здесь, у наладчика на объекте:
/// он единственный, кто видит, стоит ли шкаф. Программисту, читающему список через неделю,
/// восстановить это уже неоткуда.</summary>
public partial class FwBugDialog : Window
{
    private string _severity = FwBugSeverity.Major;

    public FwBugDialog(string fwLabel)
    {
        InitializeComponent();
        FwLabelText.Text = string.IsNullOrWhiteSpace(fwLabel) ? "не указана" : fwLabel;

        foreach (var (id, label) in FwBugSeverity.All)
        {
            var rb = new RadioButton
            {
                Content = label,
                Tag = id,
                Margin = new Thickness(0, 0, 16, 0),
                IsChecked = id == _severity,
            };
            rb.Checked += (s, _) => _severity = (string)((RadioButton)s).Tag;
            SeverityPanel.Children.Add(rb);
        }

        Loaded += (_, _) => BodyInput.Focus();
    }

    /// <summary>По умолчанию «Серьёзный», а не «Критичный»: предлагать самый громкий уровень значит
    /// получить его во всех тикетах подряд, и шкала перестанет что-либо означать.</summary>
    public string Severity => _severity;

    public string TicketText => BodyInput.Text.Trim();

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (TicketText.Length == 0)
        {
            AppMessageBox.Show("Опишите, что именно не так с прошивкой.", "Баг в прошивке",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
