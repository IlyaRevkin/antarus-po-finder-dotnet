using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    public TicketDetailDialog(Ticket ticket, string? root)
    {
        InitializeComponent();

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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
