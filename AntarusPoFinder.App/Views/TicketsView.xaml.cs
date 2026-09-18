using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

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
        /// <summary>По чему столбец «Статус» на самом деле сортируется — см. TicketStatus.SortOrder.</summary>
        public int StatusOrder => TicketStatus.SortOrder(Ticket.Status);
        public string CreatedBy => Ticket.CreatedBy;
        /// <summary>У автоотчёта роль — «system», и RolesConfig её не знает: в столбце «Роль»
        /// стояло бы английское слово. Пишем по-русски, как и все остальные роли.</summary>
        public string CreatedByRoleLabel =>
            string.Equals(Ticket.CreatedByRole, TicketAutoReports.SystemRole, StringComparison.OrdinalIgnoreCase)
                ? "программа"
                : RolesConfig.RoleLabel(Ticket.CreatedByRole);
        public string CreatedAtLabel => DateTime.TryParse(Ticket.CreatedAt, out var dt) ? dt.ToString("dd.MM.yyyy HH:mm") : Ticket.CreatedAt;

        /// <summary>По чему столбец «Создан» сортируется на самом деле.
        ///
        /// Показываем дату по-русски («дд.ММ.гггг чч:мм»), а сортировка по этой же строке идёт
        /// посимвольно — то есть по ДНЮ МЕСЯЦА: 01.09 оказывается выше 30.08, и порядок выглядит
        /// случайным. Именно это и назвали багом сортировки по дате создания. Сортируем по самой
        /// дате; неразобранное значение уезжает в конец (DateTime.MinValue), а не притворяется
        /// сегодняшним.</summary>
        public DateTime CreatedAtSort => DateTime.TryParse(Ticket.CreatedAt, out var dt) ? dt : DateTime.MinValue;
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
        // Показали актуальный список (в т.ч. только что подтянутые с диска тикеты) — просим шелл
        // сдвинуть watermark «просмотрено» и погасить бейдж на пункте меню.
        _host.OnTicketsViewed();

        // Вторая половина синхронизации — обмен с хранилищем на хостинге (TicketStorageSync). Он
        // ходит в сеть, поэтому не задерживает показ списка: страница уже нарисована местными
        // тикетами, а приехавшее из бакета досыпается в неё, когда придёт. Не await'им намеренно —
        // Activate зовут из Loaded и из «Обновить», обоим нельзя вставать на время похода в сеть.
        _ = SyncWithStorageAsync();
    }

    /// <summary>Сходить в хранилище и показать то, что пришло. Замок и обработка ошибок — в
    /// оболочке (MainWindowViewModel.SyncTicketsWithStorageAsync): обмен идёт ещё и фоном, и два
    /// прохода наперегонки выкладывали бы каждый своё состояние.</summary>
    private async Task SyncWithStorageAsync()
    {
        try
        {
            await _host.SyncTicketsWithStorageAsync(force: true);
        }
        catch { /* обмен сам сообщает о своих бедах; страница из-за них не должна падать */ }

        if (!IsLoaded) return; // страницу успели закрыть, пока ходили в сеть
        ReloadRows();
        _host.OnTicketsViewed();
    }

    /// <summary>Перечитать строки списка БЕЗ синхронизации. Нужно фоновому обмену: он сам только
    /// что сходил в сеть, и полный Activate() запустил бы с открытой страницы второй проход.</summary>
    public void ReloadRows()
    {
        if (!IsLoaded) return;
        ReloadGrid();
    }

    /// <summary>Список: сперва отбор по роли (свои/все), затем автоотчёты о сбоях —
    /// см. TicketAutoReports. Порядок именно такой: число у галки должно считаться по тому, что
    /// человек в принципе может увидеть, иначе наладчику обещали бы чужие спрятанные отчёты.</summary>
    private void ReloadGrid()
    {
        var all = _services.Db.GetTickets();
        var mine = IsAdmin
            ? all
            : all.Where(t => string.Equals(t.CreatedBy, _services.CurrentUserName, StringComparison.OrdinalIgnoreCase)).ToList();

        var autoCount = TicketAutoReports.Count(mine);
        ShowAutoReportsCheck.Content = autoCount > 0
            ? $"Показывать автоматические отчёты о сбоях ({autoCount})"
            : "Автоматических отчётов о сбоях нет";
        ShowAutoReportsCheck.IsEnabled = autoCount > 0;

        var visible = TicketAutoReports.Visible(mine, ShowAutoReportsCheck.IsChecked == true);

        // Порядок сортировки запоминается и восстанавливается вокруг подмены источника.
        // Новый ItemsSource обнуляет и стрелку в заголовке, и сам порядок — и тикет, открытый из
        // отсортированного по статусу списка, после закрытия окна оказывался «в середине, между
        // закрытыми». Раньше это не было заметно, потому что список не перечитывался после
        // открытия тикета; перечитывать его понадобилось из-за переписки — реплика меняет время
        // правки, а по нему строится порядок.
        var sort = TicketsGrid.Items.SortDescriptions.ToList();
        var sortedColumns = TicketsGrid.Columns
            .Where(c => c.SortDirection is not null)
            .Select(c => (Column: c, Direction: c.SortDirection))
            .ToList();

        TicketsGrid.ItemsSource = visible.Select(t => new TicketRow { Ticket = t }).ToList();

        if (sort.Count > 0)
        {
            TicketsGrid.Items.SortDescriptions.Clear();
            foreach (var d in sort) TicketsGrid.Items.SortDescriptions.Add(d);
            // Стрелку в заголовке WPF сам не вернёт: она живёт на колонке, а не в описании
            // сортировки. Без этого порядок правильный, а столбец выглядит несортированным.
            foreach (var (column, direction) in sortedColumns) column.SortDirection = direction;
            TicketsGrid.Items.Refresh();
        }

        UpdateActionButtons();
    }

    private void ShowAutoReports_Changed(object sender, RoutedEventArgs e)
    {
        // Загружается страница — обработчик срабатывает до того, как построен сам список.
        if (TicketsGrid is null) return;
        ReloadGrid();
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

        // Заведение тикета (строка в базе + событие в очередь + попытка отправки) живёт одним
        // методом на всех заводчиков — см. TicketSyncService.CreateTicket: тикеты создаёт ещё и
        // «Проверка компьютера», и разъехавшиеся копии этой последовательности рано или поздно
        // разошлись бы в мелочах.
        var ticket = TicketSyncService.CreateTicket(_services, type, text);

        CopyPendingAttachments(ticket.Id);

        TicketTextInput.Clear();
        _host.ShowStatus("Тикет создан", category: NotificationCategory.General);
        ReloadGrid();
        // И сразу в хранилище — иначе тикет, заведённый вне офисной сети (сетевого диска нет),
        // ждал бы ближайшего фонового тика, а его причина ждала бы вместе с ним.
        _ = SyncWithStorageAsync();
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

    // ── Выгрузка в архив ─────────────────────────────────────────────────────

    /// <summary>Складывает тикеты в один файл, который можно унести с рабочей машины. Тикеты живут
    /// на сетевом диске конторы, и тот, кто их чинит, до него не достаёт — до этой кнопки текст
    /// приходилось пересказывать своими словами, а скриншоты пересобирать руками.</summary>
    private async void ExportTickets_Click(object sender, RoutedEventArgs e)
    {
        var visible = (TicketsGrid.ItemsSource as IEnumerable<TicketRow>)?.Select(r => r.Ticket).ToList() ?? new List<Ticket>();
        var active = visible.Where(t => t.Status != TicketStatus.Closed).ToList();
        var selected = (TicketsGrid.SelectedItem as TicketRow)?.Ticket;

        if (visible.Count == 0)
        {
            AppMessageBox.Show("Выгружать нечего — тикетов в списке нет.", "Выгрузка тикетов",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var root = _services.Cfg.RootPath();
        var shareAvailable = !string.IsNullOrEmpty(root) && Directory.Exists(root);

        // Хранилище настроено (адрес, бакет, ключи) — тогда доступна отправка прямо туда; без него
        // остаётся прежний путь «сохранить файлом».
        var s3 = _services.Cfg.S3();
        var options = new TicketExportDialog(visible.Count, active.Count, selected is not null, shareAvailable,
            s3.CanPublish)
        { Owner = Window.GetWindow(this) };
        if (options.ShowDialog() != true) return;

        var (tickets, scopeLabel) = options.SelectedScope switch
        {
            TicketExportDialog.Scope.Selected => (new List<Ticket> { selected! }, "один выбранный тикет"),
            TicketExportDialog.Scope.AllVisible => (visible, IsAdmin ? "все тикеты" : "тикеты этого пользователя"),
            _ => (active, "открытые и в работе"),
        };
        if (tickets.Count == 0)
        {
            AppMessageBox.Show("По этому отбору тикетов не нашлось.", "Выгрузка тикетов",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var at = DateTime.Now;
        var toStorage = options.SelectedDestination == TicketExportDialog.Destination.Storage;
        string path;
        if (toStorage)
        {
            // Архив собирается во временную папку: в хранилище уезжает содержимое, а на рабочей
            // машине после отправки оставаться нечему.
            path = Path.Combine(Path.GetTempPath(), TicketExportService.SuggestedFileName(at));
        }
        else
        {
            var save = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Куда сохранить выгрузку тикетов",
                FileName = TicketExportService.SuggestedFileName(at),
                Filter = "Архив (*.zip)|*.zip",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (save.ShowDialog() != true) return;
            path = save.FileName;
        }

        var meta = new TicketExportService.Meta(
            AppUpdateService.CurrentVersionText, Environment.MachineName, _services.CurrentUserName,
            RolesConfig.RoleLabel(_services.Cfg.CurrentRole()), scopeLabel, at);
        var withAttachments = options.WithAttachments && shareAvailable;

        // Вложения читаются с сетевой шары — на десятке скриншотов это заметные секунды, а окно всё
        // это время не должно висеть замороженным.
        ExportBtn.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => TicketExportService.Write(
                path, meta, tickets,
                withAttachments ? id => TicketSyncService.AttachmentsDir(root, id) : null));

            var summary = $"Выгружено тикетов: {result.Tickets}" +
                          (result.Attachments > 0 ? $", вложений: {result.Attachments}" : "");
            if (result.Warnings.Count > 0)
                summary += "\n\nНе попало в архив:\n" + string.Join("\n", result.Warnings);

            if (toStorage)
            {
                await SendArchiveToStorageAsync(s3, path, at, result, summary);
                return;
            }

            summary += $"\nФайл: {path} ({TicketExportService.SizeLabel(result.Bytes)})";
            _host.ShowStatus($"Тикеты выгружены: {path}", category: NotificationCategory.General);
            if (AppMessageBox.Show(summary + "\n\nПоказать файл в папке?", "Выгрузка тикетов",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
                catch { /* проводник не открылся — путь к файлу человеку уже показан выше */ }
            }
        }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось выгрузить: {ex.Message}", "Выгрузка тикетов",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ExportBtn.IsEnabled = true;
            // Временный архив нужен был только чтобы его отправить. Не удалился — не беда, папку
            // временных файлов Windows чистит сама.
            if (toStorage)
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Кладёт собранный архив в бакет и показывает, куда именно он лёг.
    ///
    /// Ключ содержит случайный хвост (см. <see cref="TicketExportService.StorageObjectName"/>):
    /// бакет отдаётся наружу по публичному веб-адресу, а тикеты — это внутренняя переписка о
    /// поломках со скриншотами рабочих экранов, и предсказуемый адрес открыл бы кто угодно.</summary>
    private async System.Threading.Tasks.Task SendArchiveToStorageAsync(S3Settings s3, string path,
        DateTime at, TicketExportService.Result result, string summary)
    {
        var key = TicketExportService.StorageKey(s3, at, TicketExportService.NewToken());
        var sent = await new S3Client().PutFileAsync(s3, key, path);

        if (!sent.Ok)
        {
            AppMessageBox.Show($"{summary}\n\nВ хранилище не отправлено: {sent.Error}\n\n" +
                               "Архив можно собрать заново кнопкой «Сохранить…» и передать иначе.",
                "Выгрузка тикетов", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _host.ShowStatus($"Тикеты отправлены в хранилище: {key}", category: NotificationCategory.General);
        AppMessageBox.Show(
            summary + $"\nРазмер: {TicketExportService.SizeLabel(result.Bytes)}" +
            $"\n\nЛежит в хранилище: {key}\n\n" +
            "Адрес неугадываемый — в имени случайный хвост, но бакет общий, так что ссылку не публиковать.",
            "Выгрузка тикетов", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── Detail view (full text + attachments) ────────────────────────────────

    private void ShowDetail_Click(object sender, RoutedEventArgs e) => OpenDetailForSelected();
    private void TicketsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenDetailForSelected();

    private void OpenDetailForSelected()
    {
        if (TicketsGrid.SelectedItem is not TicketRow row) return;
        // Службы передаются ради обсуждения: реплики читаются и пишутся в базу, а после закрытия
        // окна список перечитывается — реплика меняет и время правки тикета, а по нему строится
        // порядок и решается, отдавать ли тикет в хранилище.
        new TicketDetailDialog(row.Ticket, _services.Cfg.RootPath(), _services) { Owner = Window.GetWindow(this) }.ShowDialog();
        ReloadGrid();
        _ = SyncWithStorageAsync();
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
        // Смена статуса — это ровно то, что должно доехать до остальных быстро: и до коллег в
        // конторе (событие на сетевом диске, TryFlush выше), и до того, кто чинит, — а он видит
        // только хранилище.
        _ = SyncWithStorageAsync();
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
