using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App.Views;

/// <summary>Тикеты (баг-репорты/предложения) — видна всем ролям, но набор действий зависит от роли:
/// наладчик/программист видят и создают только СВОИ тикеты (см. Ticket.CreatedBy — Windows-логин,
/// у ролей нет персональных учёток), администратор видит все и может менять статус. Синхронизация
/// между машинами — TicketSyncService (event-log на сетевом диске), не связана с ConfigSyncService.</summary>
public partial class TicketsView : UserControl
{
    private class TicketRow
    {
        public required Ticket Ticket { get; init; }
        public string TypeLabel => TicketType.Label(Ticket.Type);
        public string Text => Ticket.Text;
        public string StatusLabel => TicketStatus.Label(Ticket.Status);
        public string CreatedBy => Ticket.CreatedBy;
        public string CreatedByRoleLabel => RolesConfig.RoleLabel(Ticket.CreatedByRole);
        public string CreatedAtLabel => DateTime.TryParse(Ticket.CreatedAt, out var dt) ? dt.ToString("dd.MM.yyyy HH:mm") : Ticket.CreatedAt;
    }

    private readonly AppServices _services;
    private readonly IAppHost _host;
    private readonly List<string> _pendingAttachmentPaths = new();

    /// <summary>Tracks whether the LAST Activate() sync had a failure — same "only notify on the
    /// transition" rule as MainWindowViewModel.PushConfigNow, so a share that stays unreachable for a
    /// while doesn't produce a fresh toast literally every time the operator opens this page, but a
    /// tickets/status change that's stuck NOT reaching other machines is no longer invisible forever
    /// the way it used to be (root cause of the same "молча не синхронизируется" bug class as the
    /// config auto-push and app auto-update had — see PushConfigNow/AppUpdateService.TakeLastUpdateError).</summary>
    private bool _syncLastFailed;

    public TicketsView(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;

        foreach (var (id, label) in TicketType.All)
            TicketTypeCombo.Items.Add(new TicketTypeOption(id, label));
        TicketTypeCombo.SelectedIndex = 0;

        Loaded += (_, _) => Activate();
    }

    private record TicketTypeOption(string Id, string Label);

    public void RefreshIfActive()
    {
        if (IsLoaded) Activate();
    }

    private bool IsAdmin => _services.Cfg.CurrentRole() == "administrator";

    private void Activate()
    {
        ScopeHintText.Text = IsAdmin
            ? "Видны все тикеты всех пользователей. Можно менять статус."
            : "Видны только тикеты, созданные вами на любом из компьютеров (по имени пользователя Windows).";

        var root = _services.Cfg.RootPath();
        if (!string.IsNullOrEmpty(root) && System.IO.Directory.Exists(root))
        {
            try
            {
                TicketSyncService.FlushOutbox(_services, root, out var flushFailed);
                TicketSyncService.PullNewEvents(_services, root, out var pullFailed);
                var failedThisPass = flushFailed + pullFailed;
                if (failedThisPass > 0)
                {
                    if (!_syncLastFailed)
                    {
                        _syncLastFailed = true;
                        _host.ShowStatus($"Синхронизация тикетов: не удалось обработать файлов: {failedThisPass} — повторится при следующем открытии страницы",
                            8000, NotificationCategory.Sync);
                    }
                }
                else if (_syncLastFailed)
                {
                    _syncLastFailed = false;
                    _host.ShowStatus("Синхронизация тикетов восстановлена", 6000, NotificationCategory.Sync);
                }
            }
            catch { /* best effort — local tickets still show, sync retried next time the page opens */ }
        }

        ReloadGrid();
    }

    private void ReloadGrid()
    {
        var all = _services.Db.GetTickets();
        var visible = IsAdmin ? all : all.Where(t => string.Equals(t.CreatedBy, _services.CurrentUserName, StringComparison.OrdinalIgnoreCase));
        TicketsGrid.ItemsSource = visible.Select(t => new TicketRow { Ticket = t }).ToList();
        UpdateActionButtons();
    }

    private void TicketsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActionButtons();

    private void UpdateActionButtons()
    {
        var selected = (TicketsGrid.SelectedItem as TicketRow)?.Ticket;
        var canModerate = IsAdmin && selected is not null;

        TakeInProgressBtn.Visibility = canModerate && selected!.Status != TicketStatus.InProgress ? Visibility.Visible : Visibility.Collapsed;
        CloseTicketBtn.Visibility = canModerate && selected!.Status != TicketStatus.Closed ? Visibility.Visible : Visibility.Collapsed;
        ReopenTicketBtn.Visibility = canModerate && selected!.Status == TicketStatus.Closed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CreateTicket_Click(object sender, RoutedEventArgs e)
    {
        var text = TicketTextInput.Text.Trim();
        if (text.Length == 0)
        {
            AppMessageBox.Show("Введите текст тикета.", "Тикеты", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var type = (TicketTypeCombo.SelectedItem as TicketTypeOption)?.Id ?? TicketType.Other;
        var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff");

        var ticket = new Ticket
        {
            Id = Guid.NewGuid().ToString(),
            Type = type,
            Text = text,
            Status = TicketStatus.Open,
            CreatedBy = _services.CurrentUserName,
            CreatedByRole = _services.Cfg.CurrentRole(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _services.Db.InsertTicketIfMissing(ticket);

        var (filename, payload) = TicketSyncService.BuildCreateEvent(ticket);
        _services.Db.EnqueueTicketOutbox(filename, payload);
        TryFlush();

        CopyPendingAttachments(ticket.Id);

        TicketTextInput.Clear();
        _host.ShowStatus("Тикет создан", category: NotificationCategory.General);
        ReloadGrid();
    }

    // ── Attachments (staged before creation, then copied straight onto the shared drive —
    //    see TicketSyncService.AttachmentsDir for why these aren't tracked in the DB/event log) ──

    private void AttachFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Прикрепить файлы к тикету", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        _pendingAttachmentPaths.AddRange(dlg.FileNames);
        UpdateAttachmentsSummary();
    }

    private void ClearAttachments_Click(object sender, RoutedEventArgs e)
    {
        _pendingAttachmentPaths.Clear();
        UpdateAttachmentsSummary();
    }

    private void UpdateAttachmentsSummary()
    {
        if (_pendingAttachmentPaths.Count == 0)
        {
            AttachmentsSummaryText.Text = "";
            ClearAttachmentsBtn.Visibility = Visibility.Collapsed;
            return;
        }
        AttachmentsSummaryText.Text = "Прикреплено: " + string.Join(", ", _pendingAttachmentPaths.Select(Path.GetFileName));
        ClearAttachmentsBtn.Visibility = Visibility.Visible;
    }

    /// <summary>Copies whatever was staged via "Прикрепить файлы…" onto the shared drive under this
    /// new ticket's id, right after the ticket itself is created. If the share isn't reachable right
    /// now, the ticket is still created (attachments just don't block it) — the operator is told so
    /// explicitly rather than the files silently vanishing.</summary>
    private void CopyPendingAttachments(string ticketId)
    {
        if (_pendingAttachmentPaths.Count == 0) return;

        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show(
                "Тикет создан, но вложения не сохранены — сетевой диск недоступен. Прикрепите их позже, пересоздав тикет, когда диск будет доступен.",
                "Вложения", MessageBoxButton.OK, MessageBoxImage.Warning);
            _pendingAttachmentPaths.Clear();
            UpdateAttachmentsSummary();
            return;
        }

        var dir = TicketSyncService.AttachmentsDir(root, ticketId);
        Directory.CreateDirectory(dir);
        var failed = new List<string>();
        foreach (var src in _pendingAttachmentPaths)
        {
            try { File.Copy(src, Path.Combine(dir, Path.GetFileName(src)), overwrite: true); }
            catch (Exception ex) { failed.Add($"{Path.GetFileName(src)}: {ex.Message}"); }
        }
        if (failed.Count > 0)
            AppMessageBox.Show($"Не удалось приложить:\n{string.Join("\n", failed)}", "Вложения", MessageBoxButton.OK, MessageBoxImage.Warning);

        _pendingAttachmentPaths.Clear();
        UpdateAttachmentsSummary();
    }

    // ── Detail view (full text + attachments) ────────────────────────────────

    private void ShowDetail_Click(object sender, RoutedEventArgs e) => OpenDetailForSelected();
    private void TicketsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenDetailForSelected();

    private void OpenDetailForSelected()
    {
        if (TicketsGrid.SelectedItem is not TicketRow row) return;
        new TicketDetailDialog(row.Ticket, _services.Cfg.RootPath()) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void SetStatus_Click(object sender, RoutedEventArgs e)
    {
        if (!IsAdmin) return;
        if (TicketsGrid.SelectedItem is not TicketRow row) return;
        if ((sender as Button)?.Tag is not string newStatus) return;

        var at = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff");
        _services.Db.ApplyTicketStatusIfNewer(row.Ticket.Id, newStatus, at);

        var (filename, payload) = TicketSyncService.BuildStatusEvent(row.Ticket.Id, newStatus, _services.CurrentUserName, _services.Cfg.CurrentRole(), at);
        _services.Db.EnqueueTicketOutbox(filename, payload);
        TryFlush();

        _host.ShowStatus($"Статус тикета: {TicketStatus.Label(newStatus)}", category: NotificationCategory.General);
        ReloadGrid();
    }

    private void TryFlush()
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root)) return;
        try { TicketSyncService.FlushOutbox(_services, root); }
        catch { /* stays queued — retried on next Activate() */ }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Activate();
}
