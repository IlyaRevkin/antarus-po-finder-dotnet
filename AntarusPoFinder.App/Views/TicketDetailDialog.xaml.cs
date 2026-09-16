using System.Diagnostics;
using System.IO;
using System.Windows;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App.Views;

/// <summary>Full-text read view for one ticket, opened by double-clicking a row in TicketsView —
/// the grid itself only shows a single trimmed line per ticket (a very long report otherwise
/// dominated/overflowed the row, see TicketsGrid's ElementStyle), this is where the whole text is
/// actually readable, plus any attached files.</summary>
public partial class TicketDetailDialog : Window
{
    private readonly string? _attachmentsDir;
    private readonly AppServices? _services;
    private readonly Ticket _ticket;

    /// <summary>Одна реплика так, как её видно в окне. Отдельный тип, а не привязка к доменной
    /// TicketComment: в шапке склеиваются автор, роль и время, и собирать эту строку в разметке
    /// значило бы разложить её по трём привязкам с конвертерами.</summary>
    private sealed record CommentRow(string Head, string Body);

    public TicketDetailDialog(Ticket ticket, string? root, AppServices? services = null)
    {
        InitializeComponent();
        _ticket = ticket;
        _services = services;

        HeaderText.Text = $"{TicketType.Label(ticket.Type)} — {TicketStatus.Label(ticket.Status)}";
        var created = DateTime.TryParse(ticket.CreatedAt, out var dt) ? dt.ToString("dd.MM.yyyy HH:mm") : ticket.CreatedAt;
        MetaText.Text = $"Автор: {ticket.CreatedBy} ({RolesConfig.RoleLabel(ticket.CreatedByRole)}) · Создан: {created}";
        BodyText.Text = ticket.Text;

        if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
        {
            _attachmentsDir = TicketSyncService.AttachmentsDir(root, ticket.Id);
            if (Directory.Exists(_attachmentsDir))
            {
                var files = Directory.GetFiles(_attachmentsDir).Select(Path.GetFileName).ToList();
                if (files.Count > 0)
                {
                    AttachmentsList.ItemsSource = files;
                    AttachmentsList.Visibility = Visibility.Visible;
                    NoAttachmentsText.Visibility = Visibility.Collapsed;
                }
            }
        }

        // Без доступа к базе переписку ни показать, ни написать — окно тогда открывается как
        // раньше, только для чтения. Так бывает у вызовов, которым база не нужна вовсе.
        if (_services is null)
        {
            CommentsPanelEnabled(false);
        }
        else
        {
            ReloadComments();
        }

        // Фокус в текст сразу при открытии: колесо мыши работает и без этого, но PageUp/PageDown,
        // стрелки и Ctrl+End — только у контрола с фокусом, а длинный тикет чаще всего листают
        // именно клавиатурой. Каретка ставится в начало, чтобы окно открывалось на первой строке.
        Loaded += (_, _) =>
        {
            BodyText.Focus();
            BodyText.CaretIndex = 0;
        };
    }

    private void OpenAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (AttachmentsList.SelectedItem is not string name || _attachmentsDir is null) return;
        var path = Path.Combine(_attachmentsDir, name);
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void AttachmentsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenAttachment_Click(sender, e);

    private void OpenAttachmentsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_attachmentsDir is null || !Directory.Exists(_attachmentsDir))
        {
            AppMessageBox.Show("Нет вложений — папка не создавалась.", "Вложения", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(_attachmentsDir) { UseShellExecute = true });
    }

    private void CommentsPanelEnabled(bool on)
    {
        NewCommentInput.IsEnabled = on;
        AddCommentBtn.IsEnabled = on;
        if (!on) NoCommentsText.Text = "Обсуждение недоступно";
    }

    private void ReloadComments()
    {
        if (_services is null) return;
        var rows = _services.Db.GetTicketComments(_ticket.Id)
            .Select(c => new CommentRow(
                $"{(string.IsNullOrWhiteSpace(c.Author) ? "—" : c.Author)}" +
                $" ({RolesConfig.RoleLabel(c.AuthorRole)})" +
                $" · {(DateTime.TryParse(c.CreatedAt, out var at) ? at.ToString("dd.MM.yyyy HH:mm") : c.CreatedAt)}",
                c.Text))
            .ToList();

        CommentsList.ItemsSource = rows;
        CommentsList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoCommentsText.Visibility = rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Ctrl+Enter отправляет, просто Enter переносит строку. Наоборот было бы нельзя:
    /// многострочную реплику иначе не написать, а она здесь обычное дело.</summary>
    private void NewCommentInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) return;
        e.Handled = true;
        AddComment_Click(sender, new RoutedEventArgs());
    }

    private void AddComment_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;
        var text = NewCommentInput.Text.Trim();
        if (text.Length == 0) return;

        // Имя берём из CurrentUserName — тем же источником подписывается и сам тикет
        // (TicketSyncService). CurrentAdLogin у входа аварийным администратором пуст, и реплика
        // тогда подписывалась прочерком, хотя рядом в шапке стояло имя.
        _services.Db.AddTicketComment(_ticket.Id, _services.CurrentUserName, _services.Cfg.CurrentRole(), text);
        NewCommentInput.Clear();
        ReloadComments();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
