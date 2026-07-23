using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Infrastructure;
using AntarusPoFinder.Core.Services;

using AntarusPoFinder.App;

namespace AntarusPoFinder.App.Views;

public partial class SettingsView : UserControl
{
    private readonly AppServices _services;
    private readonly IAppHost _host;
    private List<FwVersionRecord> _fwVersionsData = new();

    /// <summary>One row per subtype — the unified ТИПЫ/ПОДТИПЫ table. Normally a group has at least
    /// one subtype row (Database.EnsureEveryGroupHasSubtype backfills a «—» placeholder on startup),
    /// but a group CAN temporarily end up with zero: the config import mirrors subtype deletions from
    /// another machine, and the backfill only runs at startup. Such a group used to vanish from this
    /// table entirely while still being offered in Загрузка ПО's «Тип шкафа» combo — a junk type the
    /// operator could see everywhere except where it can be deleted (his exact report). It now shows
    /// as a row with Subtype == null, so it can be selected, renamed and deleted like any other.</summary>
    private class HierarchyRow
    {
        public EquipmentGroup Group { get; init; } = null!;
        public EquipmentSubType? Subtype { get; init; }
        public string GroupName => Group.Name;
        public int GroupPrefix => Group.Prefix;
        public string SubtypeName => Subtype?.Name ?? "(нет подтипов)";
        public string SubtypePrefix => Subtype is null ? "—" : Subtype.Prefix.ToString();
        public string FolderName => Subtype?.FolderName ?? Group.Name;
    }

    /// <summary>Flattens controller types + their modifications into one grid: one row per modification,
    /// or a single placeholder row (ModificationId null) for a type that has none yet.</summary>
    private class ControllerModRow
    {
        public int ControllerId { get; init; }
        public string ControllerName { get; init; } = "";
        public int SortOrder { get; init; }
        public int? ModificationId { get; init; }
        public string DisplayName { get; init; } = "";
        public int HwVersion { get; init; }
        public string Description { get; init; } = "";
        public string HwVersionText => ModificationId.HasValue ? HwVersion.ToString() : "—";
    }

    private class FwRow
    {
        public FwVersionRecord Record { get; init; } = null!;
        public string GroupName => Record.GroupName;
        public string SubtypeName => Record.SubtypeName;
        public string CtrlName => Record.CtrlName;
        public string VersionRaw => Record.VersionRaw;
        public string Tags => Record.Tags;
        public string DateOnly => Record.UploadDate.Length >= 10 ? Record.UploadDate[..10] : Record.UploadDate;
        public bool IsRolledBack => Record.Status == "rolled_back";
        public string StatusLabel => IsRolledBack ? "Откатана" : "Активна";
    }

    private class ReservationRow
    {
        public FwVersionReservation Record { get; init; } = null!;
        public string GroupName => Record.GroupName;
        public string SubtypeName => Record.SubtypeName;
        public string CtrlName => Record.CtrlName;
        public string DateOnly => Record.ReservedAt.Length >= 10 ? Record.ReservedAt[..10] : Record.ReservedAt;

        /// <summary>Human-readable countdown instead of just the raw expires_at timestamp — computed
        /// live from DateTime.Now each time this is read, so re-evaluating the binding (see
        /// SettingsView's reservation countdown timer, which calls ReservationsGrid.Items.Refresh()
        /// every 30s while this tab is visible) is enough to make the number tick down without a
        /// full DB reload. Same "yyyy-MM-dd HH:mm:ss" shape as Database.NowIso()/IsoPlusHours.</summary>
        public string ExpiresLabel
        {
            get
            {
                if (string.IsNullOrEmpty(Record.ExpiresAt)) return "не истекает";
                if (!DateTime.TryParseExact(Record.ExpiresAt, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var expiry))
                    return Record.ExpiresAt; // unexpected format — fall back to showing it raw

                var remaining = expiry - DateTime.Now;
                return remaining <= TimeSpan.Zero ? "истёк" : $"истечёт через {HumanizeRemaining(remaining)}";
            }
        }

        private static string HumanizeRemaining(TimeSpan ts)
        {
            if (ts.TotalDays >= 1)
            {
                var days = (int)ts.TotalDays;
                var hours = ts.Hours;
                return hours > 0 ? $"{days} дн {hours} ч" : $"{days} дн";
            }
            if (ts.TotalHours >= 1)
            {
                var hours = (int)ts.TotalHours;
                var minutes = ts.Minutes;
                return minutes > 0 ? $"{hours} ч {minutes} мин" : $"{hours} ч";
            }
            // Under an hour: show minutes, rounding up so "59 sec left" doesn't read as "0 мин".
            var totalMinutes = Math.Max(1, (int)Math.Ceiling(ts.TotalMinutes));
            return $"{totalMinutes} мин";
        }
    }

    private class AppRow
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
    }

    private class AppVersionOption
    {
        public UpdateRelease Release { get; init; } = null!;
        public string Label => Release.Version == AppUpdateService.CurrentVersion
            ? $"{Release.Version} (текущая)"
            : Release.Version.ToString();
    }

    private UpdateRelease? _latestAppRelease;

    /// <summary>See SearchView.OnboardingTarget for why this exists — same reasoning.</summary>
    public FrameworkElement? OnboardingTarget(string key) => key switch
    {
        "tabbar" => TabBar,
        _ => null,
    };

    /// <summary>Ticks the "истечёт через …" reservation labels down without a DB round-trip — see
    /// ReservationRow.ExpiresLabel, which recomputes from DateTime.Now on every read, so a plain
    /// Items.Refresh() is enough. Only runs while the Reservations tab is actually visible.</summary>
    private readonly System.Windows.Threading.DispatcherTimer _reservationCountdownTimer =
        new() { Interval = TimeSpan.FromSeconds(30) };

    public SettingsView(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        Loaded += (_, _) =>
        {
            LoadGeneral();
            LoadHierarchy();
            LoadFirmwareTab();
            LoadQuickApps();
            ApplyRoleVisibility();
            _reservationCountdownTimer.Start();
        };
        Unloaded += (_, _) => _reservationCountdownTimer.Stop();
        _reservationCountdownTimer.Tick += (_, _) =>
        {
            if (ReservationsTab.Visibility == Visibility.Visible)
                ReservationsGrid.Items.Refresh();
        };
    }

    // ── Tab switching ─────────────────────────────────────────────────────────

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        foreach (var btn in new[] { TabBtnGeneral, TabBtnHierarchy, TabBtnFirmware, TabBtnModeration, TabBtnReservations, TabBtnTags, TabBtnQuickApps, TabBtnUsers })
            btn.Tag = null;
        ((Button)sender).Tag = "Active";

        GeneralTab.Visibility = Visibility.Collapsed;
        HierarchyTab.Visibility = Visibility.Collapsed;
        FirmwareTab.Visibility = Visibility.Collapsed;
        ModerationTab.Visibility = Visibility.Collapsed;
        ReservationsTab.Visibility = Visibility.Collapsed;
        TagsTab.Visibility = Visibility.Collapsed;
        QuickAppsTab.Visibility = Visibility.Collapsed;
        UsersTab.Visibility = Visibility.Collapsed;

        if (sender == TabBtnGeneral) GeneralTab.Visibility = Visibility.Visible;
        else if (sender == TabBtnHierarchy) HierarchyTab.Visibility = Visibility.Visible;
        else if (sender == TabBtnFirmware) FirmwareTab.Visibility = Visibility.Visible;
        else if (sender == TabBtnModeration) { ModerationTab.Visibility = Visibility.Visible; LoadModerationTab(); }
        else if (sender == TabBtnReservations) { ReservationsTab.Visibility = Visibility.Visible; LoadReservationsTab(); }
        else if (sender == TabBtnTags) { TagsTab.Visibility = Visibility.Visible; LoadTagsTab(); }
        else if (sender == TabBtnQuickApps) QuickAppsTab.Visibility = Visibility.Visible;
        else if (sender == TabBtnUsers) { UsersTab.Visibility = Visibility.Visible; LoadUsersTab(); }
    }

    /// <summary>Naladchik/programmer now have access to Настройки at all (previously administrator-
    /// only), but with a narrowed set of tabs and, within "Общие", a narrowed set of fields — see the
    /// XAML comment next to AdminRoleAndPasswordsSection (role switch/passwords/full AD config) for
    /// exactly what's admin-only and why. Administrator keeps seeing everything, unchanged. Called once from Loaded (this
    /// view is created fresh the first time its role can already see it) AND from
    /// MainWindowViewModel.ApplyRole every time the role changes while this page instance is already
    /// alive in the page cache — switching e.g. administrator -> naladchik mid-session must hide
    /// Иерархия/Прошивки/Пользователи immediately, not just on the next fresh navigation.</summary>
    public void ApplyRoleVisibility()
    {
        var role = _services.Cfg.CurrentRole();
        var isAdmin = role == "administrator";

        AdminRoleAndPasswordsSection.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;

        TabBtnHierarchy.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        TabBtnFirmware.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        TabBtnUsers.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        TabBtnModeration.Visibility = isAdmin || role == "naladchik" ? Visibility.Visible : Visibility.Collapsed;
        TabBtnTags.Visibility = isAdmin || role == "naladchik" ? Visibility.Visible : Visibility.Collapsed;
        TabBtnReservations.Visibility = isAdmin || role == "programmer" ? Visibility.Visible : Visibility.Collapsed;
        // TabBtnGeneral/TabBtnQuickApps: no role restriction — everyone who can reach Настройки at all sees them.

        var allTabs = new[] { TabBtnGeneral, TabBtnHierarchy, TabBtnFirmware, TabBtnModeration, TabBtnReservations, TabBtnTags, TabBtnQuickApps, TabBtnUsers };
        var activeTab = allTabs.FirstOrDefault(b => (string?)b.Tag == "Active");
        if (activeTab is null || activeTab.Visibility != Visibility.Visible)
            Tab_Click(allTabs.First(b => b.Visibility == Visibility.Visible), new RoutedEventArgs());
    }

    // ── Nested-scroll bubbling ───────────────────────────────────────────────
    // Only the "free-flowing fields" tabs (Общие/Иерархия/Модерация/Теги) still live inside
    // MainScrollViewer — see the big comment above it in the XAML. Their grids/lists have their own
    // internal ScrollViewer, and WPF's default behavior marks a mouse-wheel event as handled the
    // moment the inner ScrollViewer touches it — even once it's already at the top/bottom — so the
    // wheel never reaches MainScrollViewer and scrolling the page while hovering a table gets stuck.
    // This forwards the wheel to MainScrollViewer once the inner one has nowhere left to scroll.
    // The other 4 tabs (Прошивки/Резервация/Быстрый доступ/Пользователи) aren't wired to this at
    // all anymore — their DataGrid is the only scrollable thing on the tab, so the default WPF
    // behavior (mouse wheel scrolls the grid, full stop) is exactly right there.

    private void ScrollableChild_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject d) return;
        var inner = FindVisualChild<ScrollViewer>(d);
        bool atLimit = inner is null || inner.ScrollableHeight <= 0
            || (e.Delta > 0 && inner.VerticalOffset <= 0)
            || (e.Delta < 0 && inner.VerticalOffset >= inner.ScrollableHeight);
        if (!atLimit) return;

        e.Handled = true;
        MainScrollViewer.ScrollToVerticalOffset(MainScrollViewer.VerticalOffset - e.Delta);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    // ── Резервация номеров ───────────────────────────────────────────────────

    private void LoadReservationsTab()
    {
        ReservationsGrid.ItemsSource = _services.Db.GetAllOpenReservations().Select(r => new ReservationRow { Record = r }).ToList();
        ReservationTtlInput.Text = _services.Cfg.ReservationTtlHours().ToString();
    }

    private void RefreshReservations_Click(object sender, RoutedEventArgs e) => LoadReservationsTab();

    private void SaveReservationTtl_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ReservationTtlInput.Text.Trim(), out var hours) || hours < 0)
        {
            AppMessageBox.Show("Введите целое число часов (0 — без ограничения).", "Резервация номеров", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _services.Cfg.SetReservationTtlHours(hours);
        _host.ShowStatus(hours == 0 ? "Резервация номеров больше не истекает по умолчанию" : $"Срок резерва по умолчанию: {hours} ч", category: NotificationCategory.FirmwareAndParams);
    }

    private void CancelReservation_Click(object sender, RoutedEventArgs e)
    {
        if (ReservationsGrid.SelectedItem is not ReservationRow row)
        {
            AppMessageBox.Show("Выберите резерв в таблице.", "Резервация номеров", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var reply = AppMessageBox.Show(
            $"Отменить резерв номера {row.Record.VersionRaw}?\n\nНомер не будет использован повторно — следующая загрузка получит следующий свободный номер.",
            "Отменить резерв", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        _services.Db.CancelReservation(row.Record.Id!.Value);
        _host.ShowStatus($"Резерв отменён: {row.Record.VersionRaw}", category: NotificationCategory.FirmwareAndParams);
        LoadReservationsTab();
    }

    // ── Теги ──────────────────────────────────────────────────────────────────
    // Баблы вместо таблицы (как TagBubbleEditor, но с другой семантикой: тут это глобальный
    // список тегов — двойной клик переименовывает тег ВЕЗДЕ, "×" удаляет его из системы совсем,
    // а не просто отвязывает от одной записи, как в TagBubbleEditor).

    private string? _renamingTag;
    private bool _addingTag;

    private void LoadTagsTab()
    {
        _renamingTag = null;
        _addingTag = false;
        RenderTagsTab();
    }

    /// <summary>Клиентский фильтр по подстроке (см. TagsFilterInput) — тегов обычно немного, поэтому
    /// отдельного индекса/запроса к БД не требуется, достаточно отфильтровать уже загруженный список
    /// перед отрисовкой баблов. Кнопка добавления тега показывается всегда, даже если фильтр ничего
    /// не нашёл — иначе непонятно, как добавить тег, когда список пуст из-за фильтра.</summary>
    private void RenderTagsTab()
    {
        TagsBubblesPanel.Children.Clear();
        var filter = TagsFilterInput.Text.Trim();
        var tags = filter.Length == 0
            ? _services.Db.GetAllTags()
            : _services.Db.GetAllTags().Where(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var tag in tags)
            TagsBubblesPanel.Children.Add(tag == _renamingTag ? MakeTagRenameBubble(tag) : MakeTagBubble(tag));
        TagsBubblesPanel.Children.Add(_addingTag ? MakeTagAddInputBubble() : MakeTagAddButtonBubble());
    }

    private void TagsFilter_Changed(object sender, TextChangedEventArgs e) => RenderTagsTab();

    private Border MakeTagBubble(string tag)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var text = new TextBlock { Text = tag, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
        text.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            _renamingTag = tag;
            RenderTagsTab();
        };
        panel.Children.Add(text);

        var removeBtn = new Button { Content = "×", Style = (Style)FindResource("TagRemoveButton"), Margin = new Thickness(6, 0, 0, 0) };
        removeBtn.Click += (_, _) =>
        {
            var reply = AppMessageBox.Show($"Удалить тег «{tag}»? Он будет снят со всех прошивок.", "Удалить тег",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (reply != MessageBoxResult.Yes) return;
            _services.Db.DeleteTag(tag);
            LoadTagsTab();
        };
        panel.Children.Add(removeBtn);

        return new Border { Style = (Style)FindResource("TagBubbleBorder"), Child = panel, Margin = new Thickness(0, 0, 6, 6) };
    }

    private Border MakeTagRenameBubble(string tag)
    {
        var input = new TextBox
        {
            Text = tag, Width = 100, Height = 24, VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
        };
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitTagRename(tag, input.Text); e.Handled = true; }
            else if (e.Key == Key.Escape) { _renamingTag = null; RenderTagsTab(); e.Handled = true; }
        };
        input.LostFocus += (_, _) => CommitTagRename(tag, input.Text);
        input.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return new Border { Style = (Style)FindResource("TagBubbleBorder"), Child = input, Margin = new Thickness(0, 0, 6, 6) };
    }

    private void CommitTagRename(string oldTag, string newTextRaw)
    {
        _renamingTag = null;
        var newText = newTextRaw.Trim();
        if (newText.Length == 0 || newText.Equals(oldTag, StringComparison.OrdinalIgnoreCase)) { RenderTagsTab(); return; }
        _services.Db.RenameTag(oldTag, newText);
        LoadTagsTab();
        _host.ShowStatus($"Тег переименован: «{oldTag}» → «{newText}»", category: NotificationCategory.FirmwareAndParams);
    }

    private Border MakeTagAddButtonBubble()
    {
        var btn = new Button { Content = "+ тег", Style = (Style)FindResource("TagAddButton") };
        btn.Click += (_, _) => { _addingTag = true; RenderTagsTab(); };
        return new Border { Child = btn, Margin = new Thickness(0, 0, 6, 6) };
    }

    private Border MakeTagAddInputBubble()
    {
        var input = new TextBox
        {
            Width = 100, Height = 24, VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
        };
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitTagAdd(input.Text); e.Handled = true; }
            else if (e.Key == Key.Escape) { _addingTag = false; RenderTagsTab(); e.Handled = true; }
        };
        input.LostFocus += (_, _) => CommitTagAdd(input.Text);
        input.Loaded += (_, _) => input.Focus();
        return new Border { Style = (Style)FindResource("TagBubbleBorder"), Child = input, Margin = new Thickness(0, 0, 6, 6) };
    }

    private void CommitTagAdd(string rawText)
    {
        _addingTag = false;
        var name = rawText.Trim();
        if (name.Length == 0) { RenderTagsTab(); return; }
        _services.Db.AddTag(name);
        LoadTagsTab();
    }

    // ── Общие ─────────────────────────────────────────────────────────────────

    private void LoadGeneral()
    {
        // Пароли хранятся хешированными (см. ConfigService.SetAdminPassword/SetProgrammerPassword) —
        // хеш нельзя развернуть обратно в исходный пароль, поэтому поля больше не подставляют
        // «текущий пароль», как раньше (когда там реально лежал открытый текст). SavePasswords_Click
        // ниже трактует пустое поле как «не менять» именно из-за этого — иначе первое же открытие
        // Настроек и нажатие «Сохранить пароли» без единого изменения тихо обнулило бы оба пароля.
        AdminPwdInput.Password = "";
        ProgPwdInput.Password = "";

        AdDomainInput.Text = _services.Cfg.Get("ad_domain");
        AdGroupAdminInput.Text = _services.Cfg.Get("ad_group_administrator");
        AdGroupProgInput.Text = _services.Cfg.Get("ad_group_programmer");
        AdGroupNaladchikInput.Text = _services.Cfg.Get("ad_group_naladchik");
        AdHttpUrlInput.Text = _services.Cfg.AdHttpUrl();
        (_services.Cfg.AdAuthMode() switch
        {
            "http" => AdModeHttpRadio,
            "both" => AdModeBothRadio,
            _ => AdModeLdapRadio,
        }).IsChecked = true;

        AdRequireLoginCheck.IsChecked = _services.Cfg.AdRequireLogin();
        AdRequireLoginDaysInput.Text = _services.Cfg.AdRequireLoginDefaultDays().ToString();

        KeepArchivesCheck.IsChecked = _services.Cfg.KeepArchives();

        var tray = _services.Cfg.CloseAction() == "tray";
        CloseActionCloseRadio.IsChecked = !tray;
        CloseActionTrayRadio.IsChecked = tray;

        AutostartCheck.IsChecked = AutostartService.IsEnabled();
        StartMinimizedCheck.IsChecked = _services.Cfg.AppStartMinimized();

        AppUpdatePathInput.Text = _services.Cfg.AppUpdatePath();
        AppAutoUpdateCheck.IsChecked = _services.Cfg.AppAutoUpdate();
        AppVersionText.Text = $"Текущая версия: {AppUpdateService.CurrentVersion}";

        SearchAutoSyncCheck.IsChecked = _services.Cfg.SearchAutoSync();
        LoaderExePathInput.Text = _services.Cfg.LoaderExePath();

        LayoutFallbackCheck.IsChecked = _services.Cfg.LayoutFallbackEnabled();
        LayoutFallbackThresholdInput.Text = _services.Cfg.LayoutFallbackThreshold().ToString();
        RefreshLayoutFallbackGrid();
    }

    // ── Поиск и лоадер ─────────────────────────────────────────────────────

    private void SearchAutoSync_Changed(object sender, RoutedEventArgs e) =>
        _services.Cfg.SetSearchAutoSync(SearchAutoSyncCheck.IsChecked == true);

    private void BrowseLoaderExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Исполняемый файл лоадера",
            Filter = "Программы (*.exe)|*.exe|Все файлы (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true) LoaderExePathInput.Text = dlg.FileName;
    }

    private void SaveLoaderExePath_Click(object sender, RoutedEventArgs e)
    {
        _services.Cfg.SetLoaderExePath(LoaderExePathInput.Text.Trim());
        _host.ShowStatus("Путь к лоадеру сохранён");
    }

    // ── Раскладка клавиатуры (обучение подсказки поиска) ────────────────────

    private class LayoutFallbackRow
    {
        public string QueryKey { get; init; } = "";
        public int YesCount { get; init; }
        public int NoCount { get; init; }
        public string DecisionLabel { get; init; } = "";
    }

    private void RefreshLayoutFallbackGrid()
    {
        LayoutFallbackGrid.ItemsSource = _services.Db.GetAllLayoutFallbackLearning()
            .Select(r => new LayoutFallbackRow
            {
                QueryKey = r.QueryKey,
                YesCount = r.YesCount,
                NoCount = r.NoCount,
                DecisionLabel = r.Decision switch
                {
                    LayoutFallbackDecision.Always => "Всегда подставлять",
                    LayoutFallbackDecision.Never => "Никогда не пробовать",
                    _ => "Спрашивать",
                },
            })
            .ToList();
    }

    private void LayoutFallback_Changed(object sender, RoutedEventArgs e) =>
        _services.Cfg.SetLayoutFallbackEnabled(LayoutFallbackCheck.IsChecked == true);

    private void LayoutFallbackThreshold_Changed(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(LayoutFallbackThresholdInput.Text.Trim(), out var v) && v > 0)
            _services.Cfg.SetLayoutFallbackThreshold(v);
        LayoutFallbackThresholdInput.Text = _services.Cfg.LayoutFallbackThreshold().ToString();
    }

    private void ResetLayoutFallbackSelected_Click(object sender, RoutedEventArgs e)
    {
        if (LayoutFallbackGrid.SelectedItem is not LayoutFallbackRow row) return;
        _services.Db.ResetLayoutFallbackLearning(row.QueryKey);
        RefreshLayoutFallbackGrid();
    }

    private void ResetLayoutFallbackAll_Click(object sender, RoutedEventArgs e)
    {
        var reply = AppMessageBox.Show("Сбросить всю накопленную статистику по раскладке клавиатуры?",
            "Сброс обучения", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        _services.Db.ResetAllLayoutFallbackLearning();
        RefreshLayoutFallbackGrid();
    }

    /// <summary>Reads both radios' current IsChecked rather than trusting which one raised the
    /// event — LoadGeneral sets both in sequence when populating the tab, so only the one flipped
    /// to true actually fires Checked (WPF radio-group auto-uncheck doesn't raise Checked on the
    /// other), and by the time it does both controls already reflect the final desired state.</summary>
    private void CloseAction_Changed(object sender, RoutedEventArgs e) =>
        _services.Cfg.SetCloseAction(CloseActionTrayRadio.IsChecked == true ? "tray" : "close");

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        try
        {
            AutostartService.SetEnabled(AutostartCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось изменить автозапуск:\n{ex.Message}", "Автозапуск", MessageBoxButton.OK, MessageBoxImage.Warning);
            AutostartCheck.IsChecked = AutostartService.IsEnabled(); // reflect whatever actually happened
        }
    }

    private void StartMinimized_Changed(object sender, RoutedEventArgs e) =>
        _services.Cfg.SetAppStartMinimized(StartMinimizedCheck.IsChecked == true);

    private void BrowseAppUpdatePath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Папка обновлений" };
        if (dlg.ShowDialog() == true) AppUpdatePathInput.Text = dlg.FolderName;
    }

    private void SaveAppUpdatePath_Click(object sender, RoutedEventArgs e)
    {
        _services.Cfg.SetAppUpdatePath(AppUpdatePathInput.Text.Trim());
        _services.Cfg.SetAppAutoUpdate(AppAutoUpdateCheck.IsChecked == true);
        _host.ShowStatus("Настройки обновлений сохранены", category: NotificationCategory.AppUpdates);
    }

    private async void CheckAppUpdates_Click(object sender, RoutedEventArgs e)
    {
        AppUpdateStatusText.Text = "Проверка обновлений…";
        InstallLatestBtn.IsEnabled = false;

        UpdateCheckResult result;
        try
        {
            result = await AppUpdateService.CheckForUpdatesAsync(_services.Cfg.AppUpdatePath());
        }
        catch (Exception ex)
        {
            AppUpdateStatusText.Text = $"Не удалось проверить обновления: {AppUpdateService.DescribeError(ex)}";
            _latestAppRelease = null;
            AppVersionsCombo.ItemsSource = null;
            return;
        }

        AppVersionsCombo.ItemsSource = result.Releases.Select(r => new AppVersionOption { Release = r }).ToList();
        if (result.Releases.Count > 0) AppVersionsCombo.SelectedIndex = 0;

        if (result.Releases.Count == 0)
        {
            AppUpdateStatusText.Text = $"Источник: {result.SourceLabel}. Релизов не найдено.";
            _latestAppRelease = null;
            return;
        }

        _latestAppRelease = result.Releases[0];
        var current = AppUpdateService.CurrentVersion;
        if (_latestAppRelease.Version > current)
        {
            AppUpdateStatusText.Text = $"Источник: {result.SourceLabel}. Доступна новая версия: {_latestAppRelease.Version} (текущая {current}).";
            InstallLatestBtn.IsEnabled = true;
        }
        else
        {
            AppUpdateStatusText.Text = $"Источник: {result.SourceLabel}. Установлена актуальная версия ({current}). Найдено версий: {result.Releases.Count}.";
        }
    }

    private void InstallLatestUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_latestAppRelease == null) return;
        InstallAppVersion(_latestAppRelease);
    }

    private void InstallSelectedVersion_Click(object sender, RoutedEventArgs e)
    {
        if (AppVersionsCombo.SelectedItem is not AppVersionOption option)
        {
            AppMessageBox.Show("Сначала нажмите «Проверить обновления» и выберите версию.", "Установка версии",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        InstallAppVersion(option.Release);
    }

    private async void InstallAppVersion(UpdateRelease release)
    {
        var current = AppUpdateService.CurrentVersion;
        var direction = release.Version > current ? "Обновить" : release.Version < current ? "Откатить" : "Переустановить";
        var reply = AppMessageBox.Show(
            $"{direction} приложение до версии {release.Version}?\n\nПриложение закроется и перезапустится автоматически.",
            "Установка версии", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (reply != MessageBoxResult.Yes) return;

        try
        {
            await AppUpdateService.InstallAndRestartAsync(release);
        }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось установить версию:\n{AppUpdateService.DescribeError(ex)}", "Установка версии",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Откат — программист/администраторское действие (как в Upload); прячем кнопку
    /// в Прошивках от всех кроме administrator (единственная роль с доступом и к Настройкам,
    /// и к Загрузке).</summary>
    private void UpdateRollbackAccess() =>
        RollbackFirmwareBtn.Visibility = _services.Cfg.CurrentRole() == "administrator" ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Поля не подставляются текущим паролем при загрузке (см. LoadGeneral — хеш нельзя
    /// развернуть обратно), поэтому пустое поле здесь трактуется как «не менять этот пароль», а не
    /// «очистить его» — иначе открыть Настройки и нажать «Сохранить пароли», не тронув оба поля,
    /// тихо обнулило бы пароль администратора до пустой строки (для программиста пустой пароль —
    /// уже осмысленное состояние «не задан», но для администратора пустой пароль означает «войти
    /// может кто угодно», а это не то, что должно происходить по умолчанию от простого открытия
    /// вкладки). Известное ограничение такого решения: этой кнопкой больше нельзя вернуть пароль
    /// программиста обратно в «не задан» пустым полем — для этого рядом отдельная кнопка «Очистить
    /// пароль программиста» (см. ClearProgrammerPassword_Click).</summary>
    private void SavePasswords_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(AdminPwdInput.Password))
            _services.Cfg.SetAdminPassword(AdminPwdInput.Password);
        if (!string.IsNullOrEmpty(ProgPwdInput.Password))
            _services.Cfg.SetProgrammerPassword(ProgPwdInput.Password);

        AdminPwdInput.Password = "";
        ProgPwdInput.Password = "";
        _host.ShowStatus("Пароли сохранены");
    }

    /// <summary>Единственный способ вернуть пароль программиста в состояние «не задан» — пустое
    /// поле в SavePasswords_Click означает «не менять», поэтому оттуда это сделать нельзя (см. её
    /// комментарий). ConfigService.SetProgrammerPassword("") хранит пустую строку как есть, не
    /// хешируя её, — тот же смысл «пароль не требуется», что и у изначально незаданного пароля
    /// (см. ConfigService.VerifyProgrammerPassword).</summary>
    private void ClearProgrammerPassword_Click(object sender, RoutedEventArgs e)
    {
        _services.Cfg.SetProgrammerPassword("");
        ProgPwdInput.Password = "";
        _host.ShowStatus("Пароль программиста сброшен — роль «Программист» больше не требует пароля");
    }

    private void SaveAdGroups_Click(object sender, RoutedEventArgs e)
    {
        _services.Cfg.Set("ad_domain", AdDomainInput.Text.Trim());
        _services.Cfg.Set("ad_group_administrator", AdGroupAdminInput.Text.Trim());
        _services.Cfg.Set("ad_group_programmer", AdGroupProgInput.Text.Trim());
        _services.Cfg.Set("ad_group_naladchik", AdGroupNaladchikInput.Text.Trim());
        _services.Cfg.SetAdHttpUrl(AdHttpUrlInput.Text);
        _services.Cfg.SetAdAuthMode(AdModeHttpRadio.IsChecked == true ? "http" : AdModeBothRadio.IsChecked == true ? "both" : "ldap");

        _services.Cfg.SetAdRequireLogin(AdRequireLoginCheck.IsChecked == true);
        if (int.TryParse(AdRequireLoginDaysInput.Text.Trim(), out var days) && days > 0)
            _services.Cfg.SetAdRequireLoginDefaultDays(days);

        _host.ShowStatus("Группы и способ проверки пароля AD сохранены");
    }

    /// <summary>The one AD-related field naladchik/programmer can see (see ApplyRoleVisibility) —
    /// saves only the "TTL days" value, without touching domain/groups/mode/URL/the require-login
    /// switch itself, since those controls aren't even in the visual tree for those two roles.
    /// Administrator has this same button too (redundant with "Сохранить группы и способ" above,
    /// which also writes this field) — harmless, just an extra way to save the one value.</summary>
    private void SaveAdRequireLoginDays_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(AdRequireLoginDaysInput.Text.Trim(), out var days) || days <= 0)
        {
            AppMessageBox.Show("Введите целое число дней больше нуля.", "Срок повторного входа по AD", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _services.Cfg.SetAdRequireLoginDefaultDays(days);
        _host.ShowStatus($"Срок повторного входа по AD: {days} дн.");
    }

    // ── Пользователи (собственный AD-ростер, Часть 2/3) ────────────────────────

    private class UserRow
    {
        public AppUser Record { get; init; } = null!;
        public string AdLogin => Record.AdLogin;
        public string RoleLabel => RolesConfig.RoleLabel(Record.Role);
        public string FirstLoginAt => Record.FirstLoginAt;
        public string LastLoginAt => Record.LastLoginAt;
    }

    /// <summary>Полный ростер, загруженный из БД — источник для клиентского фильтра (см.
    /// UsersFilterInput/ApplyUsersFilter). Пользователей обычно не так много, чтобы городить
    /// запрос к БД под каждое нажатие клавиши — фильтруем уже загруженный список.</summary>
    private List<UserRow> _allUsersData = new();

    private void LoadUsersTab()
    {
        _allUsersData = _services.Db.GetAppUsers().Select(u => new UserRow { Record = u }).ToList();
        ApplyUsersFilter();

        UserRoleCombo.ItemsSource = RolesConfig.Roles.Select(r => new RoleOption(r.RoleId, r.Label)).ToList();
        UserRoleCombo.SelectedValuePath = "RoleId";
    }

    private void ApplyUsersFilter()
    {
        var filter = UsersFilterInput.Text.Trim();
        UsersGrid.ItemsSource = filter.Length == 0
            ? _allUsersData
            : _allUsersData.Where(u =>
                    u.AdLogin.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    u.RoleLabel.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    private void UsersFilter_Changed(object sender, TextChangedEventArgs e) => ApplyUsersFilter();

    private void RefreshUsers_Click(object sender, RoutedEventArgs e) => LoadUsersTab();

    private void SetUserRole_Click(object sender, RoutedEventArgs e)
    {
        if (UsersGrid.SelectedItem is not UserRow row)
        {
            AppMessageBox.Show("Выберите пользователя в таблице.", "Пользователи", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (UserRoleCombo.SelectedItem is not RoleOption selected)
        {
            AppMessageBox.Show("Выберите роль в списке.", "Пользователи", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _services.Db.SetAppUserRole(row.Record.Id!.Value, selected.RoleId);
        LoadUsersTab();
        _host.ShowStatus($"Роль «{row.AdLogin}» изменена на «{selected.Label}»");
    }

    private void DeleteUser_Click(object sender, RoutedEventArgs e)
    {
        if (UsersGrid.SelectedItem is not UserRow row)
        {
            AppMessageBox.Show("Выберите пользователя в таблице.", "Пользователи", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = AppMessageBox.Show(
            $"Удалить пользователя «{row.AdLogin}» из ростера?\n\nПри следующем входе по AD он будет создан заново с ролью «Наладчик».",
            "Удаление пользователя", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        _services.Db.DeleteAppUser(row.Record.Id!.Value);
        LoadUsersTab();
        _host.ShowStatus($"Пользователь «{row.AdLogin}» удалён");
    }

    private void SaveMisc_Click(object sender, RoutedEventArgs e)
    {
        _services.Cfg.Set("keep_archives", KeepArchivesCheck.IsChecked == true ? "true" : "false");
        _host.ShowStatus("Настройки сохранены");
    }

    // ── Иерархия ──────────────────────────────────────────────────────────────

    private void LoadHierarchy()
    {
        var groups = _services.Db.GetAllEquipmentGroups();
        var hierarchyRows = new List<HierarchyRow>();
        foreach (var g in groups)
        {
            var subtypes = _services.Db.GetSubtypesForGroup(g.Id!.Value);
            // Тип без подтипов — тоже строка (см. HierarchyRow): иначе он невидим здесь, но виден в
            // «Загрузке ПО», и удалить его из интерфейса нечем.
            if (subtypes.Count == 0)
                hierarchyRows.Add(new HierarchyRow { Group = g, Subtype = null });
            else
                hierarchyRows.AddRange(subtypes.Select(s => new HierarchyRow { Group = g, Subtype = s }));
        }
        HierarchyGrid.ItemsSource = hierarchyRows;

        var prevSelection = ControllersGrid.SelectedItem is ControllerModRow prevRow
            ? (prevRow.ControllerId, prevRow.ModificationId)
            : ((int, int?)?)null;

        var controllers = _services.Db.GetAllControllerModels();
        var ctrlRows = new List<ControllerModRow>();
        foreach (var c in controllers)
        {
            var mods = _services.Db.GetModificationsForController(c.Id!.Value);
            if (mods.Count == 0)
            {
                ctrlRows.Add(new ControllerModRow
                {
                    ControllerId = c.Id!.Value, ControllerName = c.Name, SortOrder = c.SortOrder,
                    DisplayName = "(нет модификаций)",
                });
            }
            else
            {
                foreach (var m in mods)
                {
                    ctrlRows.Add(new ControllerModRow
                    {
                        ControllerId = c.Id!.Value, ControllerName = c.Name, SortOrder = c.SortOrder,
                        ModificationId = m.Id, DisplayName = m.DisplayName, HwVersion = m.HwVersion, Description = m.Description,
                    });
                }
            }
        }
        ControllersGrid.ItemsSource = ctrlRows;
        if (prevSelection is not null)
        {
            var idx = ctrlRows.FindIndex(r => r.ControllerId == prevSelection.Value.Item1 && r.ModificationId == prevSelection.Value.Item2);
            if (idx >= 0) ControllersGrid.SelectedIndex = idx;
        }

        ManufList.ItemsSource = _services.Db.GetParamManufacturers();

        ExtList.Items.Clear();
        foreach (var ext in _services.Db.GetAllowedExtensions())
            ExtList.Items.Add(new ListBoxItem { Content = $".{ext}", Tag = ext });

        ExtHmiList.Items.Clear();
        foreach (var ext in _services.Db.GetAllowedExtensionsHmi())
            ExtHmiList.Items.Add(new ListBoxItem { Content = $".{ext}", Tag = ext });
    }

    /// <summary>A cabinet type can never exist without a subtype (see Database.EnsureEveryGroupHasSubtype),
    /// so creating a type always creates its first subtype in the same flow — there's no way to end
    /// up with an orphaned type via the UI. Use subtype name «—» for a type with no real subtype
    /// division.</summary>
    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptDialog.Prompt(Window.GetWindow(this), "Добавить тип шкафа", "Название типа шкафа:");
        if (string.IsNullOrWhiteSpace(name)) return;
        var trimmedGroupName = name.Trim();
        if (_services.Db.GetAllEquipmentGroups().Any(g => string.Equals(g.Name, trimmedGroupName, StringComparison.OrdinalIgnoreCase)))
        {
            AppMessageBox.Show($"Тип шкафа «{trimmedGroupName}» уже существует.", "Тип шкафа", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var groupPrefixStr = TextPromptDialog.Prompt(Window.GetWindow(this), "Добавить тип шкафа", "Префикс типа (число):");
        if (!int.TryParse(groupPrefixStr, out var groupPrefix))
        {
            AppMessageBox.Show("Префикс должен быть числом.", "Тип шкафа", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_services.Db.GroupPrefixTaken(groupPrefix))
        {
            AppMessageBox.Show($"Префикс {groupPrefix} уже используется другим типом шкафа.", "Тип шкафа", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var subName = TextPromptDialog.Prompt(Window.GetWindow(this), "Первый подтип", "Название подтипа (напр. КПЧ, или — если подтипов нет):", "—");
        if (string.IsNullOrWhiteSpace(subName)) return;
        var subPrefixStr = TextPromptDialog.Prompt(Window.GetWindow(this), "Первый подтип", "Префикс подтипа (0 — если подтипов нет):", "0");
        if (!int.TryParse(subPrefixStr, out var subPrefix))
        {
            AppMessageBox.Show("Префикс должен быть числом.", "Подтип", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var groupId = _services.Db.UpsertEquipmentGroup(new EquipmentGroup { Name = trimmedGroupName, Prefix = groupPrefix, SortOrder = _services.Db.GetAllEquipmentGroups().Count + 1 });
        var trimmedSubName = subName.Trim();
        var folderName = trimmedSubName == "—" ? trimmedGroupName : $"{trimmedGroupName}-{trimmedSubName}";
        _services.Db.UpsertEquipmentSubtype(new EquipmentSubType { GroupId = groupId, Name = trimmedSubName, Prefix = subPrefix, FolderName = folderName, SortOrder = 1 });

        LoadHierarchy();
        AutoRebuild();
        _host.PushCatalogChange($"Тип шкафа добавлен: {trimmedGroupName} ({folderName})");
    }

    /// <summary>"Переименовать тип/подтип"/"Изменить префикс типа/подтипа" used to be four separate
    /// buttons — they no longer fit the toolbar next to Добавить/Удалить, so double-clicking the
    /// relevant cell now does the same thing instead (Explorer/spreadsheet-style "double-click to
    /// edit this field"), routed by which column was actually clicked. Double-clicking any other
    /// column (Папка) falls back to renaming the subtype, the single most common edit.</summary>
    private void HierarchyGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!DataGridClickGuard.IsOverDataRow(e)) return;
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        switch (cell?.Column?.Header as string)
        {
            case "Тип шкафа": RenameGroup_Click(sender, e); break;
            case "Префикс типа": EditGroupPrefix_Click(sender, e); break;
            case "Подтип": RenameSubtype_Click(sender, e); break;
            case "Префикс подтипа": EditSubtypePrefix_Click(sender, e); break;
            default: RenameSubtype_Click(sender, e); break;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void EditGroupPrefix_Click(object sender, RoutedEventArgs e)
    {
        if (HierarchyGrid.SelectedItem is not HierarchyRow row)
        {
            AppMessageBox.Show("Выберите строку с типом шкафа.", "Тип шкафа", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var group = row.Group;
        var prefixStr = TextPromptDialog.Prompt(Window.GetWindow(this), "Изменить префикс типа",
            $"Префикс типа «{group.Name}» (число):", group.Prefix.ToString());
        if (prefixStr is null) return;
        if (!int.TryParse(prefixStr, out var prefix))
        {
            AppMessageBox.Show("Префикс должен быть числом.", "Тип шкафа", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (prefix != group.Prefix && _services.Db.GroupPrefixTaken(prefix, group.Id))
        {
            AppMessageBox.Show($"Префикс {prefix} уже используется другим типом шкафа.", "Тип шкафа", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _services.Db.UpsertEquipmentGroup(new EquipmentGroup { Name = group.Name, Prefix = prefix, SortOrder = group.SortOrder });
        LoadHierarchy();
        _host.PushCatalogChange($"Префикс типа «{group.Name}» изменён на {prefix}");
    }

    /// <summary>Unlike the prefix (a DB-only value), the group's Name is also its on-disk folder
    /// name — read live by HierarchyService every time it builds a path — so this moves the real
    /// folder (both ПО and Параметры trees) and remaps every already-uploaded firmware/param file's
    /// stored path before touching the DB row, and refuses the rename entirely if either move fails.</summary>
    private void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        if (HierarchyGrid.SelectedItem is not HierarchyRow row)
        {
            AppMessageBox.Show("Выберите строку с типом шкафа.", "Тип шкафа", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var group = row.Group;
        var name = TextPromptDialog.Prompt(Window.GetWindow(this), "Переименовать тип шкафа",
            $"Новое название для «{group.Name}»:", group.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        var trimmed = name.Trim();
        if (trimmed == group.Name) return;

        if (_services.Db.GroupNameTaken(trimmed, group.Id))
        {
            AppMessageBox.Show($"Тип шкафа «{trimmed}» уже существует.", "Тип шкафа", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var root = _services.Cfg.RootPath();
        if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
        {
            var result = _services.Hierarchy.RenameGroupFolder(root, group.Name, trimmed);
            if (!result.Ok)
            {
                AppMessageBox.Show($"Не удалось переименовать папку на диске:\n{result.Error}\n\nПереименование отменено.",
                    "Тип шкафа", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        _services.Db.RenameEquipmentGroup(group.Id!.Value, trimmed);
        LoadHierarchy();
        _host.PushCatalogChange($"Тип шкафа переименован: «{group.Name}» → «{trimmed}»");
    }

    private void AddSubtype_Click(object sender, RoutedEventArgs e)
    {
        if (HierarchyGrid.SelectedItem is not HierarchyRow selected)
        {
            AppMessageBox.Show("Сначала выберите строку с типом шкафа, к которому нужно добавить подтип.", "Подтип", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var group = selected.Group;
        var name = TextPromptDialog.Prompt(Window.GetWindow(this), "Добавить подтип", $"Название подтипа для «{group.Name}» (напр. КПЧ):");
        if (string.IsNullOrWhiteSpace(name)) return;
        var trimmedName = name.Trim();
        if (_services.Db.GetSubtypesForGroup(group.Id!.Value).Any(s => string.Equals(s.Name, trimmedName, StringComparison.OrdinalIgnoreCase)))
        {
            AppMessageBox.Show($"Подтип «{trimmedName}» уже есть у типа «{group.Name}».", "Подтип", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var prefixStr = TextPromptDialog.Prompt(Window.GetWindow(this), "Добавить подтип", "Префикс подтипа (число):");
        if (!int.TryParse(prefixStr, out var prefix))
        {
            AppMessageBox.Show("Префикс должен быть числом.", "Подтип", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_services.Db.SubtypePrefixTakenInGroup(group.Id!.Value, prefix))
        {
            AppMessageBox.Show($"Префикс {prefix} уже используется другим подтипом типа «{group.Name}».", "Подтип", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var folderName = trimmedName == "—" ? group.Name : $"{group.Name}-{trimmedName}";
        _services.Db.UpsertEquipmentSubtype(new EquipmentSubType
        {
            GroupId = group.Id!.Value,
            Name = trimmedName,
            Prefix = prefix,
            FolderName = folderName,
            SortOrder = _services.Db.GetSubtypesForGroup(group.Id!.Value).Count + 1,
        });
        LoadHierarchy();
        AutoRebuild();
        _host.PushCatalogChange($"Подтип добавлен: {folderName}");
    }

    private void EditSubtypePrefix_Click(object sender, RoutedEventArgs e)
    {
        if (HierarchyGrid.SelectedItem is not HierarchyRow row || row.Subtype is null)
        {
            AppMessageBox.Show("Выберите подтип.", "Подтип", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var prefixStr = TextPromptDialog.Prompt(Window.GetWindow(this), "Изменить префикс подтипа",
            $"Префикс для «{row.FolderName}» (0 — если подтипов нет):", row.Subtype.Prefix.ToString());
        if (prefixStr is null) return;
        if (!int.TryParse(prefixStr, out var prefix))
        {
            AppMessageBox.Show("Префикс должен быть числом.", "Подтип", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (prefix != row.Subtype.Prefix && _services.Db.SubtypePrefixTakenInGroup(row.Subtype.GroupId, prefix, row.Subtype.Id))
        {
            AppMessageBox.Show($"Префикс {prefix} уже используется другим подтипом типа «{row.GroupName}».", "Подтип", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _services.Db.UpsertEquipmentSubtype(new EquipmentSubType
        {
            GroupId = row.Subtype.GroupId,
            Name = row.Subtype.Name,
            Prefix = prefix,
            FolderName = row.Subtype.FolderName,
            SortOrder = row.Subtype.SortOrder,
        });
        LoadHierarchy();
        _host.PushCatalogChange($"Префикс подтипа «{row.FolderName}» изменён на {prefix}");
    }

    /// <summary>Same disk-folder-move reasoning as RenameGroup_Click. Not offered for the "—"
    /// placeholder subtype (Database.EnsureEveryGroupHasSubtype) — it has no folder segment of its
    /// own, so there's nothing meaningful to rename.</summary>
    private void RenameSubtype_Click(object sender, RoutedEventArgs e)
    {
        if (HierarchyGrid.SelectedItem is not HierarchyRow row || row.Subtype is null)
        {
            AppMessageBox.Show("Выберите подтип.", "Подтип", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (row.Subtype.Name == "—")
        {
            AppMessageBox.Show("У этого типа шкафа нет подтипов — переименовывать нечего.", "Подтип", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = TextPromptDialog.Prompt(Window.GetWindow(this), "Переименовать подтип",
            $"Новое название подтипа для «{row.GroupName}»:", row.Subtype.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        var trimmed = name.Trim();
        if (trimmed == row.Subtype.Name) return;

        if (_services.Db.SubtypeNameTakenInGroup(row.Subtype.GroupId, trimmed, row.Subtype.Id))
        {
            AppMessageBox.Show($"Подтип «{trimmed}» уже есть у типа «{row.GroupName}».", "Подтип", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var root = _services.Cfg.RootPath();
        if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
        {
            var result = _services.Hierarchy.RenameSubtypeFolder(root, row.GroupName, row.Subtype.Name, trimmed);
            if (!result.Ok)
            {
                AppMessageBox.Show($"Не удалось переименовать папку на диске:\n{result.Error}\n\nПереименование отменено.",
                    "Подтип", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var newFolderName = $"{row.GroupName}-{trimmed}";
        _services.Db.RenameEquipmentSubtype(row.Subtype.Id!.Value, trimmed, newFolderName);
        LoadHierarchy();
        _host.PushCatalogChange($"Подтип переименован: «{row.Subtype.Name}» → «{trimmed}»");
    }

    /// <summary>Deletes the subtype in the selected row. A group can't be left without any subtype
    /// (see Database.EnsureEveryGroupHasSubtype), so deleting the last remaining subtype of a group
    /// asks to delete the whole type instead of silently leaving/recreating an orphaned one.</summary>
    private void DeleteSubtype_Click(object sender, RoutedEventArgs e)
    {
        if (HierarchyGrid.SelectedItem is not HierarchyRow row)
        {
            AppMessageBox.Show("Выберите строку для удаления.", "Удаление", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Тип без подтипов (см. HierarchyRow) — удалять нечего, кроме самого типа.
        if (row.Subtype is null)
        {
            var replyGroup = AppMessageBox.Show(
                $"У типа «{row.GroupName}» нет ни одного подтипа. Удалить сам тип шкафа?",
                "Удалить тип шкафа", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (replyGroup != MessageBoxResult.Yes) return;

            _services.Db.DeleteEquipmentGroup(row.Group.Id!.Value);
            LoadHierarchy();
            MoveDeletedFolder(row.GroupName);
            _host.PushCatalogChange($"Тип шкафа «{row.GroupName}» удалён");
            return;
        }

        var isLastSubtype = _services.Db.CountSubtypesForGroup(row.Subtype.GroupId) <= 1;
        if (isLastSubtype)
        {
            var reply = AppMessageBox.Show(
                $"«{row.SubtypeName}» — последний подтип типа «{row.GroupName}». Тип шкафа не может остаться без подтипа.\n\nУдалить весь тип «{row.GroupName}» вместе с ним?",
                "Удалить тип шкафа", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (reply != MessageBoxResult.Yes) return;

            _services.Db.DeleteEquipmentGroup(row.Subtype.GroupId);
            LoadHierarchy();
            MoveDeletedFolder(row.GroupName);
            _host.PushCatalogChange($"Тип шкафа «{row.GroupName}» удалён");
            return;
        }

        var replySub = AppMessageBox.Show($"Удалить подтип «{row.FolderName}»?", "Удалить подтип",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (replySub != MessageBoxResult.Yes) return;

        _services.Db.DeleteEquipmentSubtype(row.Subtype.Id!.Value);
        LoadHierarchy();
        MoveDeletedFolder(row.FolderName);
        _host.PushCatalogChange($"Подтип «{row.FolderName}» удалён");
    }

    private void AddController_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptDialog.Prompt(Window.GetWindow(this), "Добавить контроллер", "Название (напр. SMH6):");
        if (string.IsNullOrWhiteSpace(name)) return;
        var upper = name.Trim().ToUpperInvariant();
        _services.Db.UpsertControllerModel(new ControllerModel { Name = upper, SortOrder = ControllersGrid.Items.Count + 1 });
        LoadHierarchy();
        AutoRebuild();
        _host.PushCatalogChange($"Контроллер добавлен: {upper}");
    }

    private void AddModification_Click(object sender, RoutedEventArgs e)
    {
        if (ControllersGrid.SelectedItem is not ControllerModRow row)
        {
            AppMessageBox.Show("Выберите контроллер в таблице выше.", "Модификация", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new AddModificationDialog(row.ControllerName) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        _services.Db.AddControllerModification(row.ControllerId, dlg.ModName, dlg.HwVersion, dlg.Description);
        LoadHierarchy();
        _host.PushCatalogChange($"Модификация добавлена: {dlg.ModName}");
        _host.ShowStatus($"Модификация добавлена: {dlg.ModName} (hw{dlg.HwVersion})", category: NotificationCategory.Hierarchy);
    }

    private void DeleteControllerRow_Click(object sender, RoutedEventArgs e)
    {
        if (ControllersGrid.SelectedItem is not ControllerModRow row)
        {
            AppMessageBox.Show("Выберите строку в таблице.", "Удаление", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (row.ModificationId is int modId)
        {
            var reply = AppMessageBox.Show($"Удалить модификацию «{row.DisplayName}» (hw{row.HwVersion})?", "Удалить модификацию",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (reply != MessageBoxResult.Yes) return;

            _services.Db.DeleteControllerModification(modId);
            LoadHierarchy();
            _host.PushCatalogChange($"Модификация «{row.DisplayName}» удалена");
            return;
        }

        var replyCtrl = AppMessageBox.Show($"Удалить тип контроллера «{row.ControllerName}»?", "Удалить контроллер",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (replyCtrl != MessageBoxResult.Yes) return;

        _services.Db.DeleteControllerModel(row.ControllerId);
        LoadHierarchy();
        MoveDeletedFolder(row.ControllerName);
        _host.PushCatalogChange($"Контроллер «{row.ControllerName}» удалён");
    }

    private void AddManufacturer_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptDialog.Prompt(Window.GetWindow(this), "Добавить производителя", "Название:");
        if (string.IsNullOrWhiteSpace(name)) return;
        _services.Db.AddParamManufacturer(name.Trim());
        LoadHierarchy();
        AutoRebuild();
        _host.PushCatalogChange($"Производитель ПЧ/УПП добавлен: {name.Trim()}");
    }

    private void DeleteManufacturer_Click(object sender, RoutedEventArgs e)
    {
        if (ManufList.SelectedItem is not string name)
        {
            AppMessageBox.Show("Выберите производителя.", "Производитель", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var reply = AppMessageBox.Show($"Удалить производителя «{name}»?", "Удалить производителя",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        _services.Db.DeleteParamManufacturer(name);
        LoadHierarchy();
        MoveDeletedFolder(name);
        _host.PushCatalogChange($"Производитель ПЧ/УПП «{name}» удалён");
    }

    private void AddExtension_Click(object sender, RoutedEventArgs e)
    {
        var ext = ExtInput.Text.Trim();
        if (string.IsNullOrEmpty(ext)) return;
        _services.Db.AddAllowedExtension(ext);
        ExtInput.Text = "";
        LoadHierarchy();
        _host.PushCatalogChange($"Расширение добавлено: .{ext.ToLowerInvariant().TrimStart('.')}");
    }

    private void DeleteExtension_Click(object sender, RoutedEventArgs e)
    {
        if (ExtList.SelectedItem is not ListBoxItem item || item.Tag is not string ext)
        {
            AppMessageBox.Show("Выберите расширение.", "Расширение", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var reply = AppMessageBox.Show($"Удалить расширение «.{ext}» из списка?", "Удалить расширение",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        _services.Db.RemoveAllowedExtension(ext);
        LoadHierarchy();
        _host.PushCatalogChange($"Расширение «.{ext}» удалено");
    }

    private void AddExtensionHmi_Click(object sender, RoutedEventArgs e)
    {
        var ext = ExtHmiInput.Text.Trim();
        if (string.IsNullOrEmpty(ext)) return;
        _services.Db.AddAllowedExtensionHmi(ext);
        ExtHmiInput.Text = "";
        LoadHierarchy();
        _host.PushCatalogChange($"Расширение HMI добавлено: .{ext.ToLowerInvariant().TrimStart('.')}");
    }

    private void DeleteExtensionHmi_Click(object sender, RoutedEventArgs e)
    {
        if (ExtHmiList.SelectedItem is not ListBoxItem item || item.Tag is not string ext)
        {
            AppMessageBox.Show("Выберите расширение.", "Расширение HMI", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var reply = AppMessageBox.Show($"Удалить расширение HMI «.{ext}» из списка?", "Удалить расширение HMI",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        _services.Db.RemoveAllowedExtensionHmi(ext);
        LoadHierarchy();
        _host.PushCatalogChange($"Расширение HMI «.{ext}» удалено");
    }

    private async void RebuildHierarchy_Click(object sender, RoutedEventArgs e)
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show("Сетевой диск недоступен.", "Иерархия", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // План по БД здесь, создание сотен папок на сетевом диске — в фоне (см. HierarchyService,
        // блок про двухфазные операции): окно во время этого больше не «висит».
        var plan = _services.Hierarchy.PlanStructure(root);
        EnsureStructureResult result;
        using (_host.BeginBusy("Проверка структуры диска…"))
            result = await Task.Run(() => HierarchyService.ApplyStructurePlan(plan));
        if (result.Errors.Count > 0)
            AppMessageBox.Show(string.Join("\n", result.Errors.Take(10)), "Ошибки", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            AppMessageBox.Show($"Создано папок: {result.CreatedCount}", "Структура диска", MessageBoxButton.OK, MessageBoxImage.Information);
        _host.ShowStatus($"Структура обновлена: {result.CreatedCount} папок", category: NotificationCategory.Sync);
    }

    private async void SyncFwFromDisk_Click(object sender, RoutedEventArgs e)
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show("Сетевой диск недоступен.", "Синхронизация", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Обход всех папок версий — единственная долгая часть, и она уходит в фоновый поток; записи
        // в БД по найденному делаются здесь же, на потоке интерфейса.
        var plan = _services.Hierarchy.PlanFwSync(root);
        FwDiskScan scan;
        using (_host.BeginBusy("Синхронизация прошивок с диском…"))
            scan = await Task.Run(() => HierarchyService.ScanFwDisk(plan));
        var result = _services.Hierarchy.ImportFwCandidates(scan);
        if (!result.Ok)
        {
            var msg = result.Errors.Count > 0 ? string.Join("\n", result.Errors.Take(5)) : "Неизвестная ошибка";
            AppMessageBox.Show(msg, "Синхронизация", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            var summary = $"Добавлено версий: {result.Added}\nПропущено (уже есть): {result.Skipped}";
            var details = result.AddedItems.Count == 0
                ? ""
                : "\n\nЧто добавлено:\n" + string.Join("\n", result.AddedItems.Take(50))
                  + (result.AddedItems.Count > 50 ? $"\n… и ещё {result.AddedItems.Count - 50}" : "");
            AppMessageBox.Show(summary + details, "Синхронизация", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        LoadFirmwareTab();
        _host.ShowStatus($"Синхронизация завершена: +{result.Added} версий" + (result.AddedItems.Count > 0 ? " (" + string.Join(", ", result.AddedItems.Take(3)) + (result.AddedItems.Count > 3 ? "…" : "") + ")" : ""), category: NotificationCategory.Sync);
    }

    private async void ScanUnknown_Click(object sender, RoutedEventArgs e)
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show("Сетевой диск недоступен.", "Сканирование", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var names = _services.Hierarchy.SnapshotNames();
        List<UnknownEntry> unknown;
        using (_host.BeginBusy("Проверка диска на неизвестные файлы…"))
            unknown = await Task.Run(() => HierarchyService.ScanUnknownFiles(root, names));
        if (unknown.Count == 0)
        {
            AppMessageBox.Show("Неизвестных файлов/папок не найдено.", "Сканирование", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new UnknownFilesDialog(_services, root, unknown) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        _host.ShowStatus($"Перенесено: {dlg.Moved}, перемещено в раздел: {dlg.Reassigned}, удалено: {dlg.Deleted}", category: NotificationCategory.Sync);
    }

    private void MoveDeletedFolder(string folderName)
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            _host.ShowStatus("Папка не перенесена — нажмите «Пересоздать структуру диска» позже", category: NotificationCategory.Sync);
            return;
        }
        _services.Hierarchy.EnsureStructure(root);
        var result = _services.Hierarchy.MoveNamedFolders(root, folderName);
        if (result.Moved > 0)
            _host.ShowStatus($"Папки «{folderName}» перенесены в Неизвестное ({result.Moved} шт.)", category: NotificationCategory.Sync);
        else if (result.Errors.Count > 0)
            _host.ShowStatus(result.Errors[0], category: NotificationCategory.Sync);
        else
            _host.ShowStatus($"Папка «{folderName}» не найдена на диске или уже удалена", category: NotificationCategory.Sync);
    }

    private void AutoRebuild()
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        var result = _services.Hierarchy.EnsureStructure(root);
        if (result.CreatedCount > 0)
            _host.ShowStatus($"Папки на диске обновлены: +{result.CreatedCount}", category: NotificationCategory.Sync);
    }

    // ── Прошивки ──────────────────────────────────────────────────────────────

    private void LoadFirmwareTab()
    {
        _fwVersionsData = _services.Db.GetAllFwVersionsWithNames();
        PopulateFwFilterCombos();
        ApplyFwFilter();
        UpdateRollbackAccess();
        UpdateModerationCount();
    }

    /// <summary>Dropdown values are built from what's actually in _fwVersionsData (not the full
    /// hierarchy) so a Группа/Контроллер with zero uploaded firmware never shows up as a selectable,
    /// always-empty filter. Index 0 in each combo is the "no filter on this field" sentinel.</summary>
    private void PopulateFwFilterCombos()
    {
        void Fill(ComboBox combo, string allLabel, IEnumerable<string> values)
        {
            var prev = combo.SelectedItem as string;
            var items = new List<string> { allLabel };
            items.AddRange(values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct()
                .OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase));
            combo.ItemsSource = items;
            combo.SelectedIndex = prev is not null ? Math.Max(0, items.IndexOf(prev)) : 0;
        }

        Fill(FwGroupFilterCombo, "Группа: все", _fwVersionsData.Select(v => v.GroupName));
        Fill(FwSubtypeFilterCombo, "Подтип: все", _fwVersionsData.Select(v => v.SubtypeName));
        Fill(FwControllerFilterCombo, "Контроллер: все", _fwVersionsData.Select(v => v.CtrlName));
        Fill(FwStatusFilterCombo, "Статус: все", new[] { "Активна", "Откатана" });
        Fill(FwTagFilterCombo, "Тег: все", _fwVersionsData.SelectMany(v => TagString.Parse(v.Tags)));
    }

    private void FwFilterCombo_Changed(object sender, SelectionChangedEventArgs e) => ApplyFwFilter();

    private void UpdateModerationCount()
    {
        var count = _services.Db.GetUnreleasedFwVersionsCount();
        TabBtnModeration.Content = count > 0 ? $"Модерация ({count})" : "Модерация";
    }

    private void LoadModerationTab()
    {
        var data = _services.Db.GetUnreleasedFwVersionsWithNames();
        ModGrid.ItemsSource = data.Select(v => new FwRow { Record = v }).ToList();
        ModerationCountText.Text = data.Count > 0
            ? $"Версии, ожидающие модерации — {data.Count}"
            : "Версии, ожидающие модерации — все загруженные версии уже выведены из модерации";
        UpdateModerationCount();
    }

    private void RefreshModeration_Click(object sender, RoutedEventArgs e) => LoadModerationTab();

    private void ModGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataGridClickGuard.IsOverDataRow(e)) ModerateFirmware_Click(sender, e);
    }

    private void ModerateFirmware_Click(object sender, RoutedEventArgs e)
    {
        if (ModGrid.SelectedItem is not FwRow row)
        {
            AppMessageBox.Show("Выберите версию в таблице.", "Модерация", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var v = row.Record;
        var title = $"{v.GroupName} {v.SubtypeName} {v.CtrlName} {v.VersionRaw}";
        var dlg = new EditFirmwareDialog(_services, v, title) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        _services.Db.UpdateFwVersion(v.Id!.Value, dlg.ResultDescription, dlg.ResultTags, dlg.ResultLaunchTypes,
            dlg.ResultHmiExecutableHint, dlg.ResultExecutableHint);
        EditFirmwareDialog.ReportAttachments(dlg.AttachmentsResult, _host);

        var release = AppMessageBox.Show(
            "Вывести версию из модерации и сделать релизной?",
            "Модерация", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
        if (release) _services.Db.MarkFwVersionReleased(v.Id!.Value);

        _host.ShowStatus(release ? $"Версия выведена из модерации: {v.VersionRaw}" : $"Теги обновлены: {v.VersionRaw}", category: NotificationCategory.FirmwareAndParams);
        LoadModerationTab();
    }

    private void PopulateFirmwareTable(List<FwVersionRecord> data) =>
        FwGrid.ItemsSource = data.Select(v => new FwRow { Record = v }).ToList();

    private void FwFilter_Changed(object sender, TextChangedEventArgs e) => ApplyFwFilter();

    private void FwModerationFilter_Changed(object sender, RoutedEventArgs e) => ApplyFwFilter();

    private void ApplyFwFilter()
    {
        IEnumerable<FwVersionRecord> rows = _fwVersionsData;
        if (FwNeedsModerationCheck.IsChecked == true)
            rows = rows.Where(v => !v.Released);

        if (FwGroupFilterCombo.SelectedIndex > 0 && FwGroupFilterCombo.SelectedItem is string group)
            rows = rows.Where(v => v.GroupName == group);
        if (FwSubtypeFilterCombo.SelectedIndex > 0 && FwSubtypeFilterCombo.SelectedItem is string subtype)
            rows = rows.Where(v => v.SubtypeName == subtype);
        if (FwControllerFilterCombo.SelectedIndex > 0 && FwControllerFilterCombo.SelectedItem is string ctrl)
            rows = rows.Where(v => v.CtrlName == ctrl);
        if (FwStatusFilterCombo.SelectedIndex > 0 && FwStatusFilterCombo.SelectedItem is string status)
            rows = rows.Where(v => (status == "Откатана") == (v.Status == "rolled_back"));
        if (FwTagFilterCombo.SelectedIndex > 0 && FwTagFilterCombo.SelectedItem is string tag)
            rows = rows.Where(v => TagString.Contains(v.Tags, tag));

        var filter = FwFilterInput.Text.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(filter))
            rows = rows.Where(v =>
                (v.GroupName + v.SubtypeName + v.CtrlName + v.VersionRaw + v.Tags + v.Status).ToUpperInvariant().Contains(filter));

        PopulateFirmwareTable(rows.ToList());
    }

    private void RefreshFirmware_Click(object sender, RoutedEventArgs e) => LoadFirmwareTab();

    private FwVersionRecord? GetSelectedFwVersion()
    {
        if (FwGrid.SelectedItem is not FwRow row)
        {
            AppMessageBox.Show("Выберите прошивку в таблице.", "Прошивки", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        return row.Record;
    }

    private void FwGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataGridClickGuard.IsOverDataRow(e)) EditFirmware_Click(sender, e);
    }

    private void EditFirmware_Click(object sender, RoutedEventArgs e)
    {
        var v = GetSelectedFwVersion();
        if (v is null) return;
        var title = $"{v.GroupName} {v.SubtypeName} {v.CtrlName} {v.VersionRaw}";
        var dlg = new EditFirmwareDialog(_services, v, title) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        // "Прошивка обновлена" is misleading when the only thing that changed is tags (no new
        // firmware version, nothing re-uploaded) — same distinction ModerateFirmware_Click already
        // makes below. Compare tags as an unordered set (space-joined, order isn't meaningful).
        bool tagsChanged = !new HashSet<string>(TagString.Parse(v.Tags), StringComparer.OrdinalIgnoreCase)
            .SetEquals(TagString.Parse(dlg.ResultTags));
        bool otherChanged = v.Description != dlg.ResultDescription ||
            !new HashSet<string>(v.LaunchTypes, StringComparer.OrdinalIgnoreCase).SetEquals(dlg.ResultLaunchTypes);

        _services.Db.UpdateFwVersion(v.Id!.Value, dlg.ResultDescription, dlg.ResultTags, dlg.ResultLaunchTypes,
            dlg.ResultHmiExecutableHint, dlg.ResultExecutableHint);
        EditFirmwareDialog.ReportAttachments(dlg.AttachmentsResult, _host);
        _host.ShowStatus(otherChanged ? $"Прошивка обновлена: {v.VersionRaw}"
            : tagsChanged ? $"Теги обновлены: {v.VersionRaw}"
            : $"Без изменений: {v.VersionRaw}", category: NotificationCategory.FirmwareAndParams);
        LoadFirmwareTab();
    }

    private void DuplicateFirmware_Click(object sender, RoutedEventArgs e)
    {
        var v = GetSelectedFwVersion();
        if (v is null) return;
        var title = $"{v.GroupName} {v.SubtypeName} {v.CtrlName} {v.VersionRaw}";
        var reply = AppMessageBox.Show($"Создать копию записи:\n{title}?", "Дублировать",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        var newId = _services.Db.DuplicateFwVersion(v.Id!.Value);
        if (newId > 0)
        {
            _host.ShowStatus($"Дублировано: {v.VersionRaw}", category: NotificationCategory.FirmwareAndParams);
            LoadFirmwareTab();
        }
    }

    private void RollbackFirmware_Click(object sender, RoutedEventArgs e)
    {
        var v = GetSelectedFwVersion();
        if (v is null) return;
        if (v.Status == "rolled_back")
        {
            AppMessageBox.Show("Эта версия уже откатана.", "Откат версии", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var reply = AppMessageBox.Show(
            $"Откатить версию {v.VersionRaw}?\n\nЗапись в базе будет помечена как откатанная.\nСледующая загрузка получит тот же SW-номер заново.\nФайлы на диске останутся нетронутыми.",
            "Откат версии", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        _services.Db.RollbackFwVersion(v.Id!.Value);
        _host.ShowStatus($"Откатано: {v.VersionRaw}", category: NotificationCategory.FirmwareAndParams);
        LoadFirmwareTab();
    }

    /// <summary>Permanently removes a firmware version from view — both this machine's database row
    /// (via a deletion tombstone, see Database.TombstoneFwVersion) and its files on disk (Round 43,
    /// analogous to DeleteUser_Click above / Database.DeleteAppUser, Round 38). Unlike "Откатить",
    /// which only flips status and keeps everything, this is destructive and cannot be undone from
    /// within the app. Only the version's own folder (DiskPath) and, if it looks like it belongs to
    /// this exact version (name contains VersionRaw), its versioned HMI subfolder are removed — the
    /// shared Карта ВВ/Карта modbus/Инструкция attachment files are deliberately left alone, since
    /// those live in a folder shared across ALL versions of the same subtype/controller (see
    /// UploadView.OfferCarryOver — several versions can point at literally the same file) and deleting
    /// them here would be collateral damage unrelated to this one version.
    /// Задача 3: this used to be a bare DB DELETE, which meant the deletion never left this machine —
    /// any other machine that hadn't synced since would resurrect the "missing" row on its next
    /// export. TombstoneFwVersion instead marks the row deleted and keeps it flowing through hierarchy
    /// config sync as a tombstone, so every other machine that syncs afterwards mirrors the deletion
    /// (see the fw_versions block in ImportHierarchyDataCore) instead of bringing it back.</summary>
    private void DeleteFirmware_Click(object sender, RoutedEventArgs e)
    {
        var v = GetSelectedFwVersion();
        if (v is null) return;
        var title = $"{v.GroupName} {v.SubtypeName} {v.CtrlName} {v.VersionRaw}";

        var confirm = AppMessageBox.Show(
            $"Удалить прошивку «{title}» безвозвратно?\n\n" +
            "Будут удалены запись в базе и файлы на диске (папка версии" +
            (string.IsNullOrEmpty(v.HmiPath) ? "" : ", включая приложенный HMI-проект") + ").\n" +
            "Это НЕЛЬЗЯ отменить из приложения (не «Откатить» — история не остаётся).\n\n" +
            "Удаление перенесётся на другие машины при следующей синхронизации конфига (Настройки → " +
            "Сетевые диски) — включая попытку убрать файлы и там. До тех пор, пока хотя бы одна другая " +
            "машина не синхронизируется, прошивка на ней ещё будет видна.",
            "Удаление прошивки", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        var warnings = new List<string>();
        try
        {
            if (!string.IsNullOrEmpty(v.DiskPath) && Directory.Exists(v.DiskPath))
                FileSystemHelpers.RmtreeSafe(v.DiskPath);
        }
        catch (Exception ex) { warnings.Add($"Папка версии: {ex.Message}"); }

        try
        {
            if (!string.IsNullOrEmpty(v.HmiPath) && v.HmiPath.Contains(v.VersionRaw, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(v.HmiPath)) FileSystemHelpers.RmtreeSafe(v.HmiPath);
                else if (File.Exists(v.HmiPath)) File.Delete(v.HmiPath);
            }
        }
        catch (Exception ex) { warnings.Add($"HMI-проект: {ex.Message}"); }

        _services.Db.TombstoneFwVersion(v.Id!.Value);
        _host.ShowStatus($"Удалено: {v.VersionRaw}", category: NotificationCategory.FirmwareAndParams);
        if (warnings.Count > 0)
            AppMessageBox.Show("Запись удалена из базы, но не все файлы удалось убрать с диска:\n" + string.Join("\n", warnings),
                "Удаление прошивки", MessageBoxButton.OK, MessageBoxImage.Warning);
        LoadFirmwareTab();
    }

    /// <summary>Экспортирует таблицу нумерации версий (типы шкафов, подтипы, контроллеры) в
    /// отдельный Excel-файл, формируя её из текущих данных БД (см. FwVersionTableExportService) —
    /// то есть ровно то, что сейчас настроено в Иерархии на этой машине.</summary>
    private void ExportVersionTable_Click(object sender, RoutedEventArgs e)
    {
        var initialDir = _services.Cfg.RootPath();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить таблицу версий",
            Filter = "Excel файлы (*.xlsx)|*.xlsx",
            FileName = $"Antarus_версии_нумерация_{DateTime.Now:yyyyMMdd}.xlsx",
            InitialDirectory = !string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir) ? initialDir : "",
        };

        bool? shown;
        try
        {
            shown = dlg.ShowDialog();
        }
        catch (ArgumentException)
        {
            // Reproduced live: the native Save dialog's Shell API (IFileDialog) can throw
            // "Value does not fall within the expected range" resolving InitialDirectory into a
            // shell item, even when Directory.Exists() on that exact path returns true — observed
            // with a Cyrillic root_path folder (D:\...\Новая папка\тест). Without this, the button
            // silently did nothing (the exception went uncaught past this handler, past this method
            // entirely). Retry once with no InitialDirectory instead of leaving the operator with a
            // button that appears to do nothing.
            dlg.InitialDirectory = "";
            try { shown = dlg.ShowDialog(); }
            catch (Exception ex2)
            {
                AppMessageBox.Show($"Не удалось открыть диалог сохранения:\n{ex2.Message}", "Таблица версий",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        if (shown != true) return;

        try
        {
            FwVersionTableExportService.Generate(dlg.FileName, _services.Db);
            _host.ShowStatus($"Таблица версий сохранена: {Path.GetFileName(dlg.FileName)}", category: NotificationCategory.Hierarchy);
            Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось сохранить таблицу:\n{ex.Message}", "Таблица версий",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── Быстрый доступ ────────────────────────────────────────────────────────

    private void LoadQuickApps()
    {
        var apps = _services.Cfg.QuickApps();
        AppsGrid.ItemsSource = new ObservableCollection<AppRow>(apps.Select(a => new AppRow { Name = a.Name, Path = a.Path }));

        QuickAppsModeSidebarRadio.Checked -= QuickAppsMode_Changed;
        QuickAppsModeTopRadio.Checked -= QuickAppsMode_Changed;
        QuickAppsModeTopLabeledRadio.Checked -= QuickAppsMode_Changed;
        var mode = _services.Cfg.QuickAppsDisplayMode();
        QuickAppsModeTopRadio.IsChecked = mode == "top";
        QuickAppsModeTopLabeledRadio.IsChecked = mode == "top_labeled";
        QuickAppsModeSidebarRadio.IsChecked = mode is not ("top" or "top_labeled");
        QuickAppsModeSidebarRadio.Checked += QuickAppsMode_Changed;
        QuickAppsModeTopRadio.Checked += QuickAppsMode_Changed;
        QuickAppsModeTopLabeledRadio.Checked += QuickAppsMode_Changed;
    }

    private void QuickAppsMode_Changed(object sender, RoutedEventArgs e)
    {
        var mode = QuickAppsModeTopLabeledRadio.IsChecked == true ? "top_labeled"
            : QuickAppsModeTopRadio.IsChecked == true ? "top" : "sidebar";
        _services.Cfg.SetQuickAppsDisplayMode(mode);
        _host.ReloadSidebarApps();
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбрать приложение",
            Filter = "Исполняемые файлы (*.exe;*.bat;*.lnk)|*.exe;*.bat;*.lnk|Все файлы (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        if (AppsGrid.ItemsSource is not ObservableCollection<AppRow> apps) return;

        apps.Add(new AppRow { Name = Path.GetFileNameWithoutExtension(dlg.FileName), Path = dlg.FileName });
    }

    private void DeleteApp_Click(object sender, RoutedEventArgs e)
    {
        if (AppsGrid.SelectedItem is not AppRow row) return;
        (AppsGrid.ItemsSource as ObservableCollection<AppRow>)?.Remove(row);
    }

    private void SaveApps_Click(object sender, RoutedEventArgs e)
    {
        AppsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        AppsGrid.CommitEdit(DataGridEditingUnit.Row, true);

        if (AppsGrid.ItemsSource is not ObservableCollection<AppRow> apps) return;
        var list = apps
            .Where(a => !string.IsNullOrWhiteSpace(a.Name) || !string.IsNullOrWhiteSpace(a.Path))
            .Select(a => new QuickApp { Name = a.Name, Path = a.Path })
            .ToList();
        _services.Cfg.SetQuickApps(list);
        _host.ReloadSidebarApps();
        _host.ShowStatus("Быстрые приложения сохранены");
    }
}
