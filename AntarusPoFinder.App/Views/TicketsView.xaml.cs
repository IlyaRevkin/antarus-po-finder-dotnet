using System;
using System.Collections.Generic;
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
                TicketSyncService.FlushOutbox(_services, root);
                TicketSyncService.PullNewEvents(_services, root);
            }
            catch { /* best effort — local tickets still show, sync retried next time the page opens */ }
        }

        ReloadGrid();
    }

    private void ReloadGrid()
    {
        var all = _services.Db.GetTickets();
        var visible = IsAdmin ? all : all.Where(t => string.Equals(t.CreatedBy, Environment.UserName, StringComparison.OrdinalIgnoreCase));
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
            CreatedBy = Environment.UserName,
            CreatedByRole = _services.Cfg.CurrentRole(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _services.Db.InsertTicketIfMissing(ticket);

        var (filename, payload) = TicketSyncService.BuildCreateEvent(ticket);
        _services.Db.EnqueueTicketOutbox(filename, payload);
        TryFlush();

        TicketTextInput.Clear();
        _host.ShowStatus("Тикет создан", category: NotificationCategory.General);
        ReloadGrid();
    }

    private void SetStatus_Click(object sender, RoutedEventArgs e)
    {
        if (!IsAdmin) return;
        if (TicketsGrid.SelectedItem is not TicketRow row) return;
        if ((sender as Button)?.Tag is not string newStatus) return;

        var at = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff");
        _services.Db.ApplyTicketStatusIfNewer(row.Ticket.Id, newStatus, at);

        var (filename, payload) = TicketSyncService.BuildStatusEvent(row.Ticket.Id, newStatus, Environment.UserName, _services.Cfg.CurrentRole(), at);
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
