using System.Windows;

namespace AntarusPoFinder.App.Views;

/// <summary>Что именно уедет в архив тикетов (см. TicketsView.ExportTickets_Click и
/// Core/Services/TicketExportService). Отдельным окном, а не одной кнопкой «выгрузить всё»: обычно
/// нужно отдать то, что ещё не починено, изредка — один конкретный тикет, а вложения решают, весит
/// архив десять килобайт или сто мегабайт, и это заметно, когда его отправляют почтой.</summary>
public partial class TicketExportDialog : Window
{
    public enum Scope
    {
        /// <summary>Открытые и взятые в работу — то, что ещё чинить.</summary>
        Active,
        /// <summary>Всё, что видно на странице (у наладчика/программиста — только свои тикеты).</summary>
        AllVisible,
        Selected,
    }

    public Scope SelectedScope { get; private set; } = Scope.Active;
    public bool WithAttachments { get; private set; } = true;

    /// <param name="visibleCount">Сколько тикетов видно на странице сейчас.</param>
    /// <param name="activeCount">Из них открытых и в работе.</param>
    /// <param name="hasSelection">Выделен ли тикет в списке.</param>
    /// <param name="shareAvailable">Доступен ли сетевой диск — вложения лежат только там.</param>
    public TicketExportDialog(int visibleCount, int activeCount, bool hasSelection, bool shareAvailable)
    {
        InitializeComponent();

        ScopeActive.Content = $"Только открытые и взятые в работу ({activeCount})";
        ScopeAll.Content = $"Все, что видны на странице ({visibleCount})";
        ScopeSelected.IsEnabled = hasSelection;

        // Ни одного незакрытого тикета — переключатель по умолчанию выгрузил бы пустой архив.
        if (activeCount == 0 && visibleCount > 0) ScopeAll.IsChecked = true;
        if (hasSelection && visibleCount == 0) ScopeSelected.IsChecked = true;

        if (shareAvailable)
        {
            AttachmentsHint.Text = "Архив вырастет на размер вложений — если отправлять его почтой, это стоит держать в уме.";
        }
        else
        {
            // Сетевой диск недоступен: вложения физически негде взять (TicketSyncService.
            // AttachmentsDir), и галочка бы врала. Тексты тикетов при этом выгружаются как обычно —
            // они лежат в локальной базе.
            WithAttachmentsCheck.IsChecked = false;
            WithAttachmentsCheck.IsEnabled = false;
            AttachmentsHint.Text = "Сетевой диск сейчас недоступен, поэтому вложения взять неоткуда — уедут только тексты тикетов.";
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedScope = ScopeSelected.IsChecked == true ? Scope.Selected
            : ScopeAll.IsChecked == true ? Scope.AllVisible
            : Scope.Active;
        WithAttachments = WithAttachmentsCheck.IsChecked == true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
