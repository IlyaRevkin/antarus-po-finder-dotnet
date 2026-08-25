using System.Windows;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App.Views;

/// <summary>Предпросмотр тикета, который собрала «Проверка компьютера». Существует ровно ради одного
/// правила: тикет не заводится молча. Человек видит весь текст целиком, может дописать своими
/// словами («не открывалась карточка такой-то прошивки») и может просто закрыть окно.
///
/// Тип по умолчанию — «Баг»: проверка предлагает тикет только тогда, когда нашла настоящую
/// проблему, а не предупреждение.</summary>
public partial class SelfCheckTicketDialog : Window
{
    private record TypeOption(string Id, string Label);

    public SelfCheckTicketDialog(string draftText)
    {
        InitializeComponent();

        foreach (var (id, label) in Core.Domain.TicketType.All)
            TicketTypeCombo.Items.Add(new TypeOption(id, label));
        TicketTypeCombo.SelectedIndex = 0;

        BodyInput.Text = draftText;
        // Курсор в начало, а не в конец: первым делом человек должен УВИДЕТЬ, что именно уйдёт, а
        // не оказаться в хвосте двухэкранного текста.
        Loaded += (_, _) =>
        {
            BodyInput.Focus();
            BodyInput.CaretIndex = 0;
        };
    }

    public string TicketText => BodyInput.Text.Trim();

    public string SelectedType => (TicketTypeCombo.SelectedItem as TypeOption)?.Id ?? Core.Domain.TicketType.Bug;

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (TicketText.Length == 0)
        {
            AppMessageBox.Show("Текст тикета пуст.", "Тикет", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
