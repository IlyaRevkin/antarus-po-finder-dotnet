using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
using AntarusPoFinder.Core.Loader;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

public partial class SettingsView : UserControl
{
    private readonly AppServices _services;
    private readonly IAppHost _host;
    private List<FwVersionRecord> _fwVersionsData = new();
    /// <summary>Вычисленный статус (id → метка, см. FwHistoryStatus.LabelsByGroup) для ВСЕХ версий из
    /// _fwVersionsData — считается один раз на весь (неотфильтрованный) набор, а не на то, что
    /// осталось после фильтра таблицы. Иначе фильтр по тегу/статусу мог вырезать из вида реальную
    /// «текущую» версию шкафа, и оставшаяся в выдаче заменённая версия ошибочно посчиталась бы
    /// текущей — статус версии не должен зависеть от того, что сейчас показано на экране.</summary>
    private Dictionary<int, string> _fwStatusLabels = new();

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
        /// <summary>Вычисленный статус («Текущая»/«Заменена»/«Откатана» — см. FwHistoryStatus), как в
        /// Истории версий, а не сырое поле status (которое знает только active/rolled_back и потому
        /// показывало «Активна» у ВСЕХ незаменённых версий разом — жалоба «5 версий одного шкафа, у
        /// всех Активна»). Задаётся снаружи (см. PopulateFirmwareTable), т.к. вычисляется по всей
        /// группе версий шкафа, а не по одной этой записи. Грубый запасной вариант на случай, если
        /// конкретную запись почему-то не посчитали (не должно происходить в обычной работе).</summary>
        public string StatusLabel { get; init; } = "";
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

    /// <summary>Все кнопки вкладок в порядке показа — один список на переключение, сброс подсветки
    /// и выбор запасной вкладки при смене роли (раньше он был выписан трижды и при добавлении
    /// вкладки его забывали в одном из трёх мест).</summary>
    private Button[] AllTabButtons() => new[]
    {
        TabBtnGeneral, TabBtnHierarchy, TabBtnFirmware, TabBtnModeration, TabBtnReservations,
        TabBtnTags, TabBtnQuickApps, TabBtnLoader, TabBtnConnection, TabBtnPrinting, TabBtnUsers,
    };

    /// <summary>Колесо мыши над полосой вкладок крутит саму полосу. В минимальном размере окна
    /// вкладки в строку не влезают, а горизонтальный скроллбар мышью с колесом не связан вовсе —
    /// без этого до крайних вкладок было не добраться (жалоба «не все вкладки видны и их не
    /// проскроллить»). Перехват на Preview: до того, как событие уйдёт наверх в MainScrollViewer и
    /// прокрутит страницу вместо полосы.</summary>
    private void TabBar_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabBarScroll.ScrollToHorizontalOffset(TabBarScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        foreach (var btn in AllTabButtons())
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
        LoaderTab.Visibility = Visibility.Collapsed;
        ConnectionTab.Visibility = Visibility.Collapsed;
        PrintingTab.Visibility = Visibility.Collapsed;

        if (sender == TabBtnLoader) { LoaderTab.Visibility = Visibility.Visible; LoadLoaderTab(); }
        else if (sender == TabBtnConnection) { ConnectionTab.Visibility = Visibility.Visible; LoadConnectionTab(); }
        else if (sender == TabBtnPrinting) { PrintingTab.Visibility = Visibility.Visible; LoadPrintingTab(); }
        else if (sender == TabBtnGeneral) GeneralTab.Visibility = Visibility.Visible;
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
        // Общая папка обновлений пишется в общий конфиг и уезжает на все машины — задавать её может
        // только администратор. Локальное поле выше остаётся доступно всем: это настройка своей машины.
        SharedUpdatePathSection.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        // Единая зона ПЛК+HMI — бета-функция, программист/администратор осознанно включают её себе;
        // наладчику не показываем, чтобы не столкнулся с экспериментальным поведением, не понимая, что это опция.
        UnifiedZoneSection.Visibility = isAdmin || role == "programmer" ? Visibility.Visible : Visibility.Collapsed;

        TabBtnHierarchy.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        TabBtnFirmware.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        TabBtnUsers.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        TabBtnModeration.Visibility = isAdmin || role == "naladchik" ? Visibility.Visible : Visibility.Collapsed;
        TabBtnTags.Visibility = isAdmin || role == "naladchik" ? Visibility.Visible : Visibility.Collapsed;
        TabBtnReservations.Visibility = isAdmin || role == "programmer" ? Visibility.Visible : Visibility.Collapsed;
        // TabBtnGeneral/TabBtnQuickApps: no role restriction — everyone who can reach Настройки at all sees them.

        // «Лоадер» видят наладчик и программист (они и заливают), администратор — как и всё
        // остальное. «Подключение» — только администратор: способ входа и адрес сервера общие для
        // машины, а не личная настройка того, кто сейчас за ней сидит.
        TabBtnLoader.Visibility = Visibility.Visible;
        TabBtnConnection.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;

        // «Печать» видят все — этикетку и наклейки печатает наладчик. Но внутри вкладки общие
        // настройки (адрес диска инструкций, размер этикетки, папка наклеек) уезжают по сети на все
        // машины, поэтому редактировать их может только администратор; выбор принтера и сами кнопки
        // печати остаются всем.
        TabBtnPrinting.Visibility = Visibility.Visible;
        PrintingSharedSection.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        StickersFolderSection.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        // Папка шаблонов паспортов и метка подстановки — та же общая политика предприятия, что и
        // папка наклеек рядом: правит администратор, печатают все.
        PassportTemplatesSection.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;

        var allTabs = AllTabButtons();
        var activeTab = allTabs.FirstOrDefault(b => (string?)b.Tag == "Active");
        if (activeTab is null || activeTab.Visibility != Visibility.Visible)
            Tab_Click(allTabs.First(b => b.Visibility == Visibility.Visible), new RoutedEventArgs());

        // Кнопки статуса на вкладке «Прошивки» (Сделать текущей/Откатить/Вернуть в активные) видимы
        // только администратору — их видимость выставляется в UpdateRollbackAccess, но раньше она
        // вызывалась лишь из LoadFirmwareTab (один раз при создании SettingsView). Экземпляр
        // кэшируется (MainWindowViewModel), а при смене роли вызывается только этот метод — без этого
        // вызова наладчик, открывший Настройки и затем ставший администратором, видел вкладку
        // «Прошивки» вообще без этих трёх кнопок, пока не нажмёт «Обновить».
        UpdateRollbackAccess();
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

    private void ReservationTtl_LostFocus(object sender, RoutedEventArgs e) => SaveReservationTtl();

    private void ReservationTtl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveReservationTtl();
    }

    /// <summary>Автосохранение вместо кнопки «Сохранить» (см. SettingsAutoSave): мусорный ввод не
    /// открывает модальное окно — поле возвращается к сохранённому значению, причина уходит в нижнюю
    /// строку состояния.</summary>
    private void SaveReservationTtl()
    {
        var edit = SettingsAutoSave.ParseNumber(ReservationTtlInput.Text, _services.Cfg.ReservationTtlHours(), min: 0,
            "Срок резерва: нужно целое число часов (0 — без ограничения)");
        if (edit.Invalid)
        {
            ReservationTtlInput.Text = edit.Value.ToString();
            _host.ShowStatus(edit.Message, category: NotificationCategory.FirmwareAndParams);
            return;
        }
        if (!edit.Save) return;

        _services.Cfg.SetReservationTtlHours(edit.Value);
        _host.ShowStatus(edit.Value == 0 ? "Резервация номеров больше не истекает по умолчанию" : $"Срок резерва по умолчанию: {edit.Value} ч", category: NotificationCategory.FirmwareAndParams);
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

    /// <summary>true, пока LoadGeneral раскладывает сохранённые значения по контролам. Все поля
    /// вкладки «Общие» теперь автосохраняются по изменению, и без этого флага само наполнение формы
    /// поднимало бы Checked/SelectionChanged и писало в конфиг то, что только что из него прочитало.
    /// Сами обработчики и так не сохраняют неизменившееся значение (SettingsAutoSave), флаг — второй
    /// рубеж и защита от лишней записи в общий конфиг.</summary>
    private bool _loadingGeneral;

    private void LoadGeneral()
    {
        _loadingGeneral = true;
        try { LoadGeneralCore(); }
        finally { _loadingGeneral = false; }
    }

    private void LoadGeneralCore()
    {
        // Пароли хранятся хешированными (см. ConfigService.SetAdminPassword/SetProgrammerPassword) —
        // хеш нельзя развернуть обратно в исходный пароль, поэтому поля не подставляют «текущий
        // пароль». Именно поэтому SavePassword ниже игнорирует пустое поле («не трогать»), а убрать
        // пароль программиста можно только явной кнопкой (см. ClearProgrammerPassword_Click).
        AdminPwdInput.Password = "";
        ProgPwdInput.Password = "";

        // Поля AD (домен, группы, адрес веб-проверки, «требовать вход», срок) заполняются не здесь, а
        // в LoadConnectionTab — они переехали на вкладку «Подключение».

        KeepArchivesCheck.IsChecked = _services.Cfg.KeepArchives();

        var tray = _services.Cfg.CloseAction() == "tray";
        CloseActionCloseRadio.IsChecked = !tray;
        CloseActionTrayRadio.IsChecked = tray;

        AutostartCheck.IsChecked = AutostartService.IsEnabled();
        StartMinimizedCheck.IsChecked = _services.Cfg.AppStartMinimized();

        AppUpdatePathInput.Text = _services.Cfg.AppUpdatePath();
        AppUpdatePathSharedInput.Text = _services.Cfg.AppUpdatePathShared();
        AppAutoUpdateCheck.IsChecked = _services.Cfg.AppAutoUpdate();
        AppVersionText.Text = $"Текущая версия: {AppUpdateService.CurrentVersionText}";

        SearchAutoSyncCheck.IsChecked = _services.Cfg.SearchAutoSync();
        LoaderExePathInput.Text = _services.Cfg.LoaderExePath();
        UnifiedPlcHmiZoneCheck.IsChecked = _services.Cfg.UnifiedPlcHmiZoneEnabled();

        LayoutFallbackCheck.IsChecked = _services.Cfg.LayoutFallbackEnabled();
        LayoutFallbackThresholdInput.Text = _services.Cfg.LayoutFallbackThreshold().ToString();
        RefreshLayoutFallbackGrid();
        FwUsageThresholdInput.Text = _services.Cfg.FwUsageThreshold().ToString();
        RefreshUsageMultiplierUi();
        RefreshUsageStats();
    }

    // ── Поиск и лоадер ─────────────────────────────────────────────────────

    private void SearchAutoSync_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingGeneral) return;
        var on = SearchAutoSyncCheck.IsChecked == true;
        if (on == _services.Cfg.SearchAutoSync()) return;

        _services.Cfg.SetSearchAutoSync(on);
        _host.ShowStatus(on
            ? "Найденные прошивки будут подтягиваться в локальную копию автоматически"
            : "Автоподтягивание найденных прошивок выключено");
    }

    private void BrowseLoaderExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Segnetics Loader",
            Filter = "Программы (*.exe)|*.exe|Все файлы (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        LoaderExePathInput.Text = dlg.FileName;
        SaveLoaderExePath();
    }

    private void LoaderExePathInput_LostFocus(object sender, RoutedEventArgs e) => SaveLoaderExePath();

    private void LoaderExePathInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveLoaderExePath();
    }

    /// <summary>Сообщение в статус-строку выводится всегда, а не только после «Обзор…»: сохранение
    /// происходит лишь когда путь реально изменился (SettingsAutoSave.PathChanged), так что молчание
    /// при обычном проходе фокусом по форме обеспечивается само собой.</summary>
    private void SaveLoaderExePath()
    {
        var path = LoaderExePathInput.Text.Trim();
        if (!SettingsAutoSave.PathChanged(path, _services.Cfg.LoaderExePath())) return;

        _services.Cfg.SetLoaderExePath(path);
        _host.ShowStatus(path.Length == 0 ? "Будет использован встроенный Segnetics Loader" : "Путь к лоадеру сохранён");
        RefreshLoaderStatus();
    }

    // ── Вкладка «Лоадер» ──────────────────────────────────────────────────────
    // Всё, что раньше было размазано между «Общими» и окном загрузки: где Loader, чем подключаемся
    // к ПЛК и надо ли проверять связь заранее. Настройки машинные (свой шнурок, свой переходник),
    // в общий конфиг не уезжают — см. ConfigSyncService.SkipSettingsKeys.

    /// <summary>Пункт списка «Способ подключения»: показываем по-человечески, храним значением.</summary>
    private sealed record ModeOption(PlcConnectionMode Mode)
    {
        public override string ToString() => LoaderConnectionSettings.ModeCaption(Mode);
    }

    /// <summary>Заполняется один раз при первом открытии вкладки: список адаптеров ходит в систему,
    /// а SelectionChanged у ComboBox срабатывает и при программном заполнении — флаг не даёт
    /// заполнению сохранить «выбор», которого человек не делал (та же защита, что _fillingFilters
    /// в SearchView).</summary>
    private bool _loaderTabFilling;
    private bool _loaderTabLoaded;

    private void LoadLoaderTab()
    {
        _loaderTabFilling = true;
        try
        {
            LoaderExePathInput.Text = _services.Cfg.LoaderExePath();

            if (LoaderModeCombo.Items.Count == 0)
                foreach (var mode in new[] { PlcConnectionMode.Unspecified, PlcConnectionMode.Usb, PlcConnectionMode.Ethernet })
                    LoaderModeCombo.Items.Add(new ModeOption(mode));
            var current = LoaderConnectionSettings.ParseMode(_services.Cfg.LoaderConnectionMode());
            LoaderModeCombo.SelectedItem = LoaderModeCombo.Items.Cast<ModeOption>().First(o => o.Mode == current);

            LoaderIpInput.Text = _services.Cfg.LoaderPlcIp();

            // Список адаптеров перечитывается при каждом заходе: переходник USB-Ethernet наладчик
            // втыкает уже после запуска программы, и «его нет в списке» было бы враньём.
            var saved = _services.Cfg.LoaderNetworkAdapter();
            LoaderAdapterCombo.Items.Clear();
            LoaderAdapterCombo.Items.Add("Как в Loader");
            foreach (var name in PlcLinkCheck.Adapters()) LoaderAdapterCombo.Items.Add(name);
            // Сохранённого адаптера может уже не быть в системе (переходник вынут) — показываем его
            // отдельной строкой, иначе выбор молча слетел бы на «Как в Loader».
            if (saved.Length > 0 && !LoaderAdapterCombo.Items.Cast<string>().Any(s => string.Equals(s, saved, StringComparison.CurrentCultureIgnoreCase)))
                LoaderAdapterCombo.Items.Add(saved);
            LoaderAdapterCombo.SelectedItem = saved.Length == 0
                ? LoaderAdapterCombo.Items[0]
                : LoaderAdapterCombo.Items.Cast<string>().First(s => string.Equals(s, saved, StringComparison.CurrentCultureIgnoreCase));

            LoaderCheckLinkCheck.IsChecked = _services.Cfg.LoaderCheckLink();
            LoaderLinkTimeoutInput.Text = _services.Cfg.LoaderLinkTimeoutMs().ToString();
            LoaderPrepareDefaultCheck.IsChecked = _services.Cfg.LoaderFormatAndUpdateDefault();
        }
        finally
        {
            _loaderTabFilling = false;
        }

        _loaderTabLoaded = true;
        RefreshLoaderStatus();
        RefreshAdapterHint();
    }

    /// <summary>Строка под путём: найден ли Loader и что именно нашлось. Без неё «пустой путь =
    /// встроенная копия» проверяется только заливкой, то есть в поле и в самый неподходящий момент.</summary>
    private void RefreshLoaderStatus()
    {
        if (!_loaderTabLoaded) return;
        var backend = FirmwareLoaderFactory.Create(_services.Cfg.LoaderExePath());
        LoaderStatusText.Text = backend.IsAvailable
            ? $"Loader найден: {backend.Name}{(string.IsNullOrEmpty(backend.DisplayVersion) ? "" : $", версия {backend.DisplayVersion}")}"
            : $"Loader не найден: {backend.UnavailableReason}";
    }

    private void RefreshAdapterHint()
    {
        var adapter = _services.Cfg.LoaderNetworkAdapter();
        if (adapter.Length == 0)
        {
            LoaderAdapterHint.Text = "Адаптер выбирает сам Loader.";
            return;
        }
        var address = PlcLinkCheck.AdapterAddress(adapter);
        LoaderAdapterHint.Text = address.Length > 0
            ? $"Адрес этого адаптера сейчас: {address}"
            : "У этого адаптера сейчас нет адреса — он выключен или кабель не воткнут.";
    }

    private void LoaderMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loaderTabFilling || LoaderModeCombo.SelectedItem is not ModeOption option) return;
        _services.Cfg.SetLoaderConnectionMode(LoaderConnectionSettings.ModeToConfig(option.Mode));
        _host.ShowStatus($"Подключение к ПЛК: {LoaderConnectionSettings.ModeCaption(option.Mode)}");
    }

    private void LoaderAdapter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loaderTabFilling || LoaderAdapterCombo.SelectedItem is not string name) return;
        // Первая строка списка — «как в Loader», то есть пустое значение настройки.
        _services.Cfg.SetLoaderNetworkAdapter(LoaderAdapterCombo.SelectedIndex == 0 ? "" : name);
        RefreshAdapterHint();
        _host.ShowStatus("Сетевой адаптер для ПЛК сохранён");
    }

    private void LoaderIp_LostFocus(object sender, RoutedEventArgs e) => SaveLoaderIp();

    private void LoaderIp_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveLoaderIp();
    }

    private void SaveLoaderIp()
    {
        var ip = LoaderIpInput.Text.Trim();
        if (ip == _services.Cfg.LoaderPlcIp()) return;
        _services.Cfg.SetLoaderPlcIp(ip);
        _host.ShowStatus(ip.Length == 0 ? "Адрес ПЛК очищен" : $"Адрес ПЛК сохранён: {ip}");
    }

    private void LoaderCheckLink_Click(object sender, RoutedEventArgs e)
    {
        if (_loaderTabFilling) return;
        _services.Cfg.SetLoaderCheckLink(LoaderCheckLinkCheck.IsChecked == true);
        _host.ShowStatus(LoaderCheckLinkCheck.IsChecked == true
            ? "Связь с ПЛК будет проверяться перед заливкой"
            : "Проверка связи перед заливкой выключена");
    }

    private void LoaderPrepareDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_loaderTabFilling) return;
        _services.Cfg.SetLoaderFormatAndUpdateDefault(LoaderPrepareDefaultCheck.IsChecked == true);
        _host.ShowStatus("Значение по умолчанию для окна загрузки сохранено");
    }

    private void LoaderLinkTimeout_LostFocus(object sender, RoutedEventArgs e) => SaveLoaderLinkTimeout();

    private void LoaderLinkTimeout_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveLoaderLinkTimeout();
    }

    private void SaveLoaderLinkTimeout()
    {
        if (!int.TryParse(LoaderLinkTimeoutInput.Text.Trim(), out var ms) || ms <= 0)
        {
            // Возвращаем показанное значение к сохранённому, а не ругаемся окном: поле числовое,
            // и молча принять «абв» хуже, чем показать, что осталось прежним.
            LoaderLinkTimeoutInput.Text = _services.Cfg.LoaderLinkTimeoutMs().ToString();
            return;
        }
        if (ms == _services.Cfg.LoaderLinkTimeoutMs()) return;
        _services.Cfg.SetLoaderLinkTimeoutMs(ms);
        _host.ShowStatus($"Ожидание ответа ПЛК: {ms} мс");
    }

    private async void CheckPlcLink_Click(object sender, RoutedEventArgs e)
    {
        var ip = LoaderIpInput.Text.Trim();
        LoaderLinkResultText.Text = "Проверяем связь…";
        var result = await PlcLinkCheck.CheckAsync(ip, _services.Cfg.LoaderLinkTimeoutMs());
        LoaderLinkResultText.Text = $"{result.Message} ({result.ElapsedMs} мс)";
    }

    /// <summary>Как SearchAutoSync_Changed выше — сохраняется сразу по щелчку, без отдельной кнопки
    /// «Сохранить». UploadView перечитывает значение через ConfigService.UnifiedPlcHmiZoneEnabled()
    /// при каждом переходе на страницу «Загрузка прошивки» (см. UploadView.ReloadCombos), так что
    /// уже открытая где-то в фоне вкладка Загрузки подхватит новое значение не мгновенно, а при
    /// следующем возврате на неё — см. комментарий у самой галочки в XAML.</summary>
    private void UnifiedPlcHmiZone_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingGeneral) return;
        var on = UnifiedPlcHmiZoneCheck.IsChecked == true;
        if (on == _services.Cfg.UnifiedPlcHmiZoneEnabled()) return;

        _services.Cfg.SetUnifiedPlcHmiZoneEnabled(on);
        _host.ShowStatus(on
            ? "Единая зона ПЛК+HMI включена — применится при следующем открытии «Загрузки прошивки»"
            : "Вернулись к раздельным зонам ПЛК и HMI");
    }

    // ── Раскладка клавиатуры (обучение подсказки поиска) ────────────────────

    private class LayoutFallbackRow
    {
        public string QueryKey { get; init; } = "";
        public int YesCount { get; set; }
        public int NoCount { get; set; }
        public string DecisionLabel { get; init; } = "";
    }

    /// <summary>Правка «да»/«нет» прямо в таблице — задаёт накопленные ответы напрямую и пересчитывает
    /// решение по тем же числам, что и обычный ответ. WPF сначала пишет отредактированное значение в
    /// свойство строки, поэтому читаем из строки (после отложенного применения edit'а).</summary>
    private void LayoutFallbackGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not LayoutFallbackRow row) return;
        var edited = (e.EditingElement as System.Windows.Controls.TextBox)?.Text;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _services.Db.SetLayoutFallbackCounts(row.QueryKey, row.YesCount, row.NoCount,
                _services.Cfg.LayoutFallbackThreshold());
            RefreshLayoutFallbackGrid();
        }), System.Windows.Threading.DispatcherPriority.Background);
        _ = edited;
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

    // ── Статистика выборов прошивки (Database.FwUsage.cs) ────────────────────

    private void UsageConfirm_Changed(object sender, RoutedEventArgs e) =>
        _services.Cfg.SetUsageConfirmEnabled(UsageConfirmCheck.IsChecked == true);

    /// <summary>Сброс — намеренно для всех машин сразу, а не только у себя: статистика общая, и
    /// сброшенная в одиночку она вернулась бы с первым же чужим снимком. Отметка времени сброса
    /// уезжает в общем конфиге, и каждая машина, увидев отметку новее своей, чистит статистику у
    /// себя (ConfigSyncService.ApplyUsageResetMark). Как и любая правка справочника, отметка ждёт
    /// кнопки «Отправить всё» на плашке — до отправки сброс останется локальным.</summary>
    private void ResetFwUsage_Click(object sender, RoutedEventArgs e)
    {
        var total = _services.Db.TotalFwUsageCount();
        var reply = AppMessageBox.Show(
            $"Сбросить статистику выборов прошивок (учтено выборов: {total})?\n\n" +
            "Сброс распространяется на все машины — он уедет вместе со следующей отправкой конфига на сетевой диск.",
            "Сброс статистики", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        _services.Db.ResetAllFwUsage();
        _services.Cfg.SetFwUsageResetAt(now);
        _services.Cfg.SetFwUsageResetAppliedAt(now);
        RefreshUsageStats();
        _host?.PushCatalogChange("сброшена статистика выборов прошивок");
    }

    private void ResetUsageConfirmLearning_Click(object sender, RoutedEventArgs e)
    {
        _services.Db.ResetFwUsageConfirmLearning();
        RefreshUsageStats();
    }

    private void RefreshUsageStats()
    {
        UsageConfirmCheck.IsChecked = _services.Cfg.UsageConfirmEnabled();
        var decision = _services.Db.GetFwUsageConfirmDecision() switch
        {
            UsageConfirmDecision.Always => "выборы засчитываются без вопроса",
            UsageConfirmDecision.Never => "выборы не засчитываются",
            _ => "спрашивает при каждом выборе",
        };
        UsageStatsLabel.Text = $"Сейчас учтено выборов: {_services.Db.TotalFwUsageCount()} · {decision}";
        RefreshFwUsageGrid();
    }

    private class FwUsageRow
    {
        public string QueryKey { get; init; } = "";
        public string SubtypeName { get; init; } = "";
        public string ControllerName { get; init; } = "";
        public string VersionRaw { get; init; } = "";
        public int Uses { get; set; }
        /// <summary>Ручной вес под этот запрос. Правится прямо в таблице (двойной клик) и адресно в
        /// модерации прошивки — обе правки пишут одну строку (SetLocalFwWeight).</summary>
        public int Weight { get; set; }
        /// <summary>Доля чужого снимка в показанных Uses/Weight — её правкой не сдвинуть (синхронизация
        /// вернёт назад), поэтому при правке своего вклада мы вычитаем именно её из введённого числа.</summary>
        public int SharedUses { get; init; }
        public int SharedWeight { get; init; }
        /// <summary>Локальная строка fw_versions, к которой относится эта пара запрос→версия — нужна
        /// для записи ручной правки веса. Null, если строка целиком чужая и локальной версии под неё
        /// нет (тогда править нечего — правка идёт у машины-источника).</summary>
        public int? LocalVersionId { get; init; }
    }

    /// <summary>Ручная правка числа выборов или веса прямо в таблице — задаёт вклад ЭТОЙ машины по паре
    /// запрос→версия (SetLocalFwUsage / SetLocalFwWeight). Значение берём напрямую из редактируемого
    /// TextBox, а не из row.*: на момент CellEditEnding двусторонняя привязка ещё не записала введённое
    /// в источник, поэтому опора на row дала бы старое число («не сохраняется»). Показанное — сумма
    /// своего и чужого снимка, правим только свой вклад: вычитаем чужую долю, ниже неё не опускаем
    /// (синхронизация всё равно вернёт её). Пусто = обнулить свой вклад; не-целое и отрицательное —
    /// откат к тому, что в базе.</summary>
    private void FwUsageGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not FwUsageRow row) return;
        if (row.LocalVersionId is not int id) return;

        var text = (e.EditingElement as System.Windows.Controls.TextBox)?.Text?.Trim() ?? "";
        var isWeight = (e.Column?.Header as string) == "Вес";
        int? typed = text.Length == 0 ? 0
            : (int.TryParse(text, out var v) && v >= 0 ? v : (int?)null);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (typed is int t)
            {
                var local = Math.Max(0, t - (isWeight ? row.SharedWeight : row.SharedUses));
                if (isWeight) _services.Db.SetLocalFwWeight(row.QueryKey, id, local);
                else _services.Db.SetLocalFwUsage(row.QueryKey, id, local);
            }
            RefreshUsageStats();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Таблица накопленной статистики выборов: столбцы «Выборов» и «Вес» правятся на месте
    /// (FwUsageGrid_CellEditEnding), остальные — идентификаторы. Сортировка по убыванию числа выборов
    /// уже сделана в Database.GetAllFwUsage. Порог ранжирования здесь не фильтрует строки: таблица
    /// показывает всё, что накопилось, а не только то, что уже двигает выдачу (см. её doc-комментарий).</summary>
    private void RefreshFwUsageGrid()
    {
        FwUsageGrid.ItemsSource = _services.Db.GetAllFwUsage()
            .Select(r => new FwUsageRow
            {
                QueryKey = r.QueryKey,
                SubtypeName = r.SubtypeName,
                ControllerName = r.ControllerName,
                VersionRaw = r.VersionRaw,
                Uses = r.Uses,
                Weight = r.Weight,
                SharedUses = r.Uses - r.LocalUses,
                SharedWeight = r.Weight - r.LocalWeight,
                LocalVersionId = r.LocalVersionId,
            })
            .ToList();
    }

    private void FwUsageThreshold_Changed(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(FwUsageThresholdInput.Text.Trim(), out var v) && v > 0)
            _services.Cfg.SetFwUsageThreshold(v);
        FwUsageThresholdInput.Text = _services.Cfg.FwUsageThreshold().ToString();
    }

    /// <summary>Множитель популярности — синхронизируемый (уезжает на все машины). Принимаем и дробное
    /// («1.5»), инвариантной культурой, чтобы одинаково читалось при любой локали; отрицательное и
    /// мусор откатываются к текущему значению. После правки обновляем подсказку с актуальным потолком
    /// авто-вклада — ориентиром для ручного веса.</summary>
    private void FwUsageMultiplier_Changed(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(FwUsageMultiplierInput.Text.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0)
            _services.Cfg.SetFwUsageMultiplier(v);
        RefreshUsageMultiplierUi();
    }

    private void FwWeightShared_Changed(object sender, RoutedEventArgs e) =>
        _services.Cfg.SetFwWeightShared(FwWeightSharedCheck.IsChecked == true);

    /// <summary>Подтягивает поле множителя, галочку «делиться весом» и подсказку с текущим потолком
    /// авто-вклада (FwUsageMaxAutoBonus) — то самое число, выше которого ставят ручной вес, чтобы
    /// обойти популярность.</summary>
    private void RefreshUsageMultiplierUi()
    {
        FwUsageMultiplierInput.Text = _services.Cfg.FwUsageMultiplier()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        FwWeightSharedCheck.IsChecked = _services.Cfg.FwWeightShared();
        FwUsageMultiplierHint.Text =
            $"На сколько частота выбора двигает выдачу. 1 — как обычно, больше — сильнее, 0 — популярность не влияет вовсе. " +
            $"Сейчас счётчик открытий добавляет к версии не больше {_services.Cfg.FwUsageMaxAutoBonus()} баллов — " +
            $"задавайте ручной вес выше этого числа, чтобы поднять версию над самой популярной.";
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

    /// <summary>Автосохранение вместо кнопки «Сохранить»: путь закрепляется сразу после выбора папки
    /// и при уходе фокуса из поля, о факте сохранения сообщает нижняя строка состояния.</summary>
    private void BrowseAppUpdatePath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Папка обновлений" };
        if (dlg.ShowDialog() != true) return;
        AppUpdatePathInput.Text = dlg.FolderName;
        SaveAppUpdatePath();
    }

    private void AppUpdatePathInput_LostFocus(object sender, RoutedEventArgs e) => SaveAppUpdatePath();

    private void AppUpdatePath_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveAppUpdatePath();
    }

    private void SaveAppUpdatePath()
    {
        if (_loadingGeneral) return;
        var path = AppUpdatePathInput.Text.Trim();
        if (!SettingsAutoSave.PathChanged(path, _services.Cfg.AppUpdatePath())) return;

        _services.Cfg.SetAppUpdatePath(path);
        // Формулировка учитывает общую папку: очистка личного пути не означает «теперь GitHub» —
        // сначала пробуется общая папка (см. UpdateFolderResolver).
        _host.ShowStatus(path.Length == 0
                ? "Папка обновлений этой машины очищена — будет использована общая папка или GitHub"
                : $"Папка обновлений сохранена: {path}",
            category: NotificationCategory.AppUpdates);
    }

    private void AppAutoUpdate_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingGeneral) return;
        var on = AppAutoUpdateCheck.IsChecked == true;
        if (on == _services.Cfg.AppAutoUpdate()) return;

        _services.Cfg.SetAppAutoUpdate(on);
        _host.ShowStatus(on ? "Обновления будут ставиться автоматически при запуске" : "Автоустановка обновлений выключена",
            category: NotificationCategory.AppUpdates);
    }

    private void AppUpdatePathSharedInput_LostFocus(object sender, RoutedEventArgs e)
    {
        var path = AppUpdatePathSharedInput.Text.Trim();
        if (string.Equals(path, _services.Cfg.AppUpdatePathShared(), StringComparison.OrdinalIgnoreCase)) return;

        _services.Cfg.SetAppUpdatePathShared(path);
        _host.ShowStatus(path.Length == 0
                ? "Общая папка обновлений очищена"
                : $"Общая папка обновлений сохранена: {path} (уедет на другие машины со следующей отправкой конфига)",
            category: NotificationCategory.AppUpdates);
    }

    private void ShowConnectionStatus_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ConnectionStatusDialog(_services.Cfg) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }

    // ── Вкладка «Подключение» ─────────────────────────────────────────────────
    // Два независимых вопроса на одном экране: чем подтверждается вход и как ходят общие данные.
    // Оба переключателя намеренно НЕ переключают ничего сами по себе задним числом: смена способа
    // входа действует со следующего запуска (гейт показывается до главного окна), а серверный
    // обмен вообще нельзя включать раньше, чем сервер поднят — см. docs/client-server-plan.md.

    private sealed record AuthOption(string Value, string Caption)
    {
        public override string ToString() => Caption;
    }

    private sealed record TransportOption(string Value, string Caption)
    {
        public override string ToString() => Caption;
    }

    private bool _connectionTabFilling;

    private void LoadConnectionTab()
    {
        _connectionTabFilling = true;
        try
        {
            if (AuthKindCombo.Items.Count == 0)
            {
                AuthKindCombo.Items.Add(new AuthOption("ldap", "Домен напрямую (LDAP)"));
                AuthKindCombo.Items.Add(new AuthOption("http", "Веб-проверка пароля"));
                AuthKindCombo.Items.Add(new AuthOption("both", "Домен, при недоступности — веб-проверка"));
                AuthKindCombo.Items.Add(new AuthOption("oidc", "Корпоративный вход (Keycloak / OpenID Connect)"));
            }
            var mode = _services.Cfg.AdAuthMode();
            AuthKindCombo.SelectedItem = AuthKindCombo.Items.Cast<AuthOption>().First(o => o.Value == mode);

            // Поля AD переехали сюда из «Общих» вместе со всей темой входа — заполняются там же, где
            // и способ входа, под тем же флагом «идёт заполнение» (иначе LostFocus/Checked при
            // подстановке значений тут же посчитали бы их правкой оператора).
            AdDomainInput.Text = _services.Cfg.Get("ad_domain");
            AdGroupAdminInput.Text = _services.Cfg.Get("ad_group_administrator");
            AdGroupProgInput.Text = _services.Cfg.Get("ad_group_programmer");
            AdGroupNaladchikInput.Text = _services.Cfg.Get("ad_group_naladchik");
            AdHttpUrlInput.Text = _services.Cfg.AdHttpUrl();
            AdRequireLoginCheck.IsChecked = _services.Cfg.AdRequireLogin();
            AdRequireLoginDaysInput.Text = _services.Cfg.AdRequireLoginDefaultDays().ToString();

            OidcAuthorityInput.Text = _services.Cfg.OidcAuthority();
            OidcClientIdInput.Text = _services.Cfg.OidcClientId();
            OidcGroupsClaimInput.Text = _services.Cfg.OidcGroupsClaim();

            if (TransportCombo.Items.Count == 0)
            {
                TransportCombo.Items.Add(new TransportOption("fileshare", "Сетевая папка (как сейчас)"));
                // Подпись честно говорит, что сервера ещё нет: выбрать пункт можно (адрес и проверка
                // пригодятся в день запуска), но обмен от этого сегодня не меняется — см. текст
                // раздела в SettingsView.xaml и предупреждение в Transport_Changed.
                TransportCombo.Items.Add(new TransportOption("server", "Сервер: HTTP + WebSocket (сервера ещё нет)"));
            }
            var transport = _services.Cfg.SyncTransport();
            TransportCombo.SelectedItem = TransportCombo.Items.Cast<TransportOption>().First(o => o.Value == transport);
            ServerUrlInput.Text = _services.Cfg.ServerUrl();
        }
        finally
        {
            _connectionTabFilling = false;
        }

        UpdateConnectionSections();
    }

    /// <summary>Поля SSO и сервера показываются всегда, но приглушаются, когда соответствующий
    /// способ не выбран: заполнить их ДО переключения — нормальный порядок действий (сначала ИТ
    /// прислал параметры, потом переключаем), поэтому прятать их совсем было бы неудобно.</summary>
    private void UpdateConnectionSections()
    {
        var oidc = (AuthKindCombo.SelectedItem as AuthOption)?.Value == "oidc";
        OidcSection.Opacity = oidc ? 1.0 : 0.6;
        // Домен и группы при корпоративном входе не используются (роль считается по claim из токена),
        // но и там остаются заполнимыми — приглушаем так же, как SSO при доменном входе.
        AdSection.Opacity = oidc ? 0.6 : 1.0;
        var server = (TransportCombo.SelectedItem as TransportOption)?.Value == "server";
        ServerSection.Opacity = server ? 1.0 : 0.6;
    }

    private void AuthKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_connectionTabFilling || AuthKindCombo.SelectedItem is not AuthOption option) return;

        if (option.Value == "oidc" && !_services.Cfg.OidcConfigured())
        {
            AppMessageBox.Show(
                "Сначала заполните адрес realm и клиент — без них корпоративный вход выполнить нечем.\n" +
                "Параметры выдаёт ИТ, когда поднимет Keycloak.",
                "Корпоративный вход", MessageBoxButton.OK, MessageBoxImage.Information);
            LoadConnectionTab();
            return;
        }

        _services.Cfg.SetAdAuthMode(option.Value);
        UpdateConnectionSections();
        _host.ShowStatus($"Способ входа сохранён: {option.Caption}. Подействует при следующем запуске программы.");
    }

    private void OidcAuthority_LostFocus(object sender, RoutedEventArgs e)
    {
        var url = OidcAuthorityInput.Text.Trim().TrimEnd('/');
        if (url == _services.Cfg.OidcAuthority()) return;
        _services.Cfg.SetOidcAuthority(url);
        _host.ShowStatus("Адрес сервера входа сохранён");
    }

    private void OidcClientId_LostFocus(object sender, RoutedEventArgs e)
    {
        var id = OidcClientIdInput.Text.Trim();
        if (id == _services.Cfg.OidcClientId()) return;
        _services.Cfg.SetOidcClientId(id);
        _host.ShowStatus("Клиент сервера входа сохранён");
    }

    private void OidcGroupsClaim_LostFocus(object sender, RoutedEventArgs e)
    {
        var claim = OidcGroupsClaimInput.Text.Trim();
        if (claim.Length == 0 || claim == _services.Cfg.OidcGroupsClaim()) return;
        _services.Cfg.SetOidcGroupsClaim(claim);
        _host.ShowStatus("Поле с ролями сохранено");
    }

    /// <summary>Enter в любом поле SSO = «сохранить», как и уход фокусом: три поля с одинаковым
    /// поведением не заслуживают трёх одинаковых обработчиков.</summary>
    private void OidcField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender == OidcAuthorityInput) OidcAuthority_LostFocus(sender, e);
        else if (sender == OidcClientIdInput) OidcClientId_LostFocus(sender, e);
        else if (sender == OidcGroupsClaimInput) OidcGroupsClaim_LostFocus(sender, e);
    }

    private async void CheckOidc_Click(object sender, RoutedEventArgs e)
    {
        var authority = OidcAuthorityInput.Text.Trim().TrimEnd('/');
        OidcCheckResultText.Text = "Спрашиваем сервер…";
        var result = await OidcIdentityProvider.DiscoverAsync(authority);
        OidcCheckResultText.Text = result.Message;
    }

    private void Transport_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_connectionTabFilling || TransportCombo.SelectedItem is not TransportOption option) return;

        if (option.Value == "server")
        {
            if (string.IsNullOrWhiteSpace(ServerUrlInput.Text))
            {
                AppMessageBox.Show("Сначала укажите адрес сервера.", "Обмен данными",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                LoadConnectionTab();
                return;
            }
            // Клиента к серверу ещё нет (docs/client-server-plan.md — план, а не реализация), поэтому
            // предупреждение говорит ровно две вещи: сегодня выбор ничего не меняет, а в день запуска
            // сервера он отрежет от общих данных машины со старой версией.
            var answer = AppMessageBox.Show(
                "Сервера пока нет, и обмен с ним в программе ещё не написан: данные и после переключения\n" +
                "продолжат ездить через сетевую папку. Настройка запомнится на будущее.\n\n" +
                "Когда сервер поднимут, этот выбор отрежет от общих данных машины со старой версией —\n" +
                "они умеют только папку.\n\nЗапомнить выбор?",
                "Обмен данными", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                LoadConnectionTab();
                return;
            }
        }

        _services.Cfg.SetSyncTransport(option.Value);
        UpdateConnectionSections();
        _host.ShowStatus($"Обмен данными: {option.Caption}", category: NotificationCategory.Sync);
    }

    private void ServerUrl_LostFocus(object sender, RoutedEventArgs e)
    {
        var url = ServerUrlInput.Text.Trim().TrimEnd('/');
        if (url == _services.Cfg.ServerUrl()) return;
        _services.Cfg.SetServerUrl(url);
        _host.ShowStatus("Адрес сервера сохранён");
    }

    private void ServerUrl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ServerUrl_LostFocus(sender, e);
    }

    private async void CheckServer_Click(object sender, RoutedEventArgs e)
    {
        var url = ServerUrlInput.Text.Trim().TrimEnd('/');
        if (url.Length == 0)
        {
            ServerCheckResultText.Text = "Адрес сервера не задан.";
            return;
        }
        ServerCheckResultText.Text = "Проверяем сервер…";
        var result = await ServerEndpointCheck.CheckAsync(url);
        ServerCheckResultText.Text = result.Message;
    }

    // ── Вкладка «Печать» ──────────────────────────────────────────────────────
    // Этикетка с QR на инструкцию + папка с шаблонами наклеек. Разделение настроек здесь важнее
    // обычного: адрес диска инструкций, размер этикетки и папка наклеек — ОБЩИЕ (уезжают на все
    // машины с конфигом, задаёт администратор), а имя принтера — своё у каждой машины
    // (ConfigSyncService.SkipSettingsKeys). Поэтому поля общих настроек скрыты от наладчика с
    // программистом, а выбор принтера и сама печать доступны всем — печатает как раз наладчик.

    private bool _printingTabFilling;

    /// <summary>Подпись пустого выбора в списке принтеров. Не имя принтера — печать на «принтер
    /// Windows по умолчанию», то есть в настройках хранится пустая строка.</summary>
    private const string DefaultPrinterCaption = "Принтер по умолчанию";

    private void LoadPrintingTab()
    {
        _printingTabFilling = true;
        try
        {
            InstructionBaseUrlInput.Text = _services.Cfg.InstructionBaseUrl();
            LabelWidthInput.Text = _services.Cfg.LabelWidthMm().ToString("0.##", CultureInfo.CurrentCulture);
            LabelHeightInput.Text = _services.Cfg.LabelHeightMm().ToString("0.##", CultureInfo.CurrentCulture);

            var labelLayout = LabelLayout.FromConfig(_services.Cfg);
            LabelHeadlineInput.Text = labelLayout.HeadlineText;
            LabelShowHeadlineCheck.IsChecked = labelLayout.ShowHeadline;
            LabelHoleTextInput.Text = labelLayout.HoleText;

            // Список принтеров перечитывается на каждый заход: принтер могли добавить, пока
            // программа открыта. Сохранённое имя добавляем в список, даже если такого принтера
            // сейчас нет (сеть/принтер отвалились) — иначе выбор молча сбросился бы на «по умолчанию».
            var saved = _services.Cfg.LabelPrinter();
            var names = new List<string> { DefaultPrinterCaption };
            names.AddRange(LabelPrinter.InstalledPrinters().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));
            if (saved.Length > 0 && !names.Contains(saved, StringComparer.OrdinalIgnoreCase))
                names.Add(saved);
            LabelPrinterCombo.ItemsSource = names;
            LabelPrinterCombo.SelectedItem = saved.Length == 0
                ? DefaultPrinterCaption
                : names.FirstOrDefault(n => n.Equals(saved, StringComparison.OrdinalIgnoreCase)) ?? DefaultPrinterCaption;

            StickersFolderInput.Text = _services.Cfg.StickersFolder();
            ShowStickersFolderStatus();

            PassportTemplatesFolderInput.Text = _services.Cfg.PassportTemplatesFolder();
            PassportPlaceholderInput.Text = _services.Cfg.PassportNamePlaceholder();
            PassportDuplexCheck.IsChecked = _services.Cfg.PassportDuplexShortEdge();
            ShowPassportTemplatesStatus();
        }
        finally
        {
            _printingTabFilling = false;
        }
    }

    /// <summary>Куда программа будет смотреть за наклейками с текущими настройками — то же
    /// вычисление, что и в самом окне наклеек, чтобы «настроил одно, открылось другое» было
    /// невозможно.</summary>
    private void ShowStickersFolderStatus()
    {
        var folder = StickerTemplates.FolderFor(_services.Cfg.RootPath(), _services.Cfg.StickersFolder());
        if (folder is null)
        {
            StickersFolderStatus.Text = "Сетевой диск не настроен — папку наклеек взять неоткуда.";
            return;
        }
        var count = StickerTemplates.List(folder).Count;
        StickersFolderStatus.Text = count > 0
            ? $"{folder} — шаблонов: {count}"
            : $"{folder} — пусто или недоступно.";
    }

    private void PrintingField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender == InstructionBaseUrlInput) InstructionBaseUrl_LostFocus(sender, e);
        else if (sender == LabelWidthInput || sender == LabelHeightInput) LabelSize_LostFocus(sender, e);
        else if (sender == LabelHeadlineInput) LabelHeadline_LostFocus(sender, e);
        else if (sender == LabelHoleTextInput) LabelHoleText_LostFocus(sender, e);
        else if (sender == StickersFolderInput) StickersFolder_LostFocus(sender, e);
    }

    /// <summary>Подпись назначения. Пустое поле — ЗНАЧЕНИЕ («подписи не надо»), а не «настройку не
    /// трогали»: поэтому оно и сохраняется как пустая строка, а не откатывается к умолчанию (см.
    /// ConfigService.LabelText / Database.HasSetting — без этого различия стёртая подпись
    /// возвращалась бы обратно при каждом чтении).</summary>
    private void LabelHeadline_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_printingTabFilling) return;
        var current = LabelLayout.FromConfig(_services.Cfg);
        var value = (LabelHeadlineInput.Text ?? "").Trim();
        if (string.Equals(value, current.HeadlineText, StringComparison.Ordinal)) return;

        (current with { HeadlineText = value }).SaveTo(_services.Cfg);
        LabelHeadlineInput.Text = LabelLayout.FromConfig(_services.Cfg).HeadlineText;
        _host.ShowStatus(value.Length == 0
            ? "Подпись назначения на этикетке очищена"
            : $"Подпись назначения на этикетке: {value}");
    }

    private void LabelShowHeadline_Click(object sender, RoutedEventArgs e)
    {
        if (_printingTabFilling) return;
        var show = LabelShowHeadlineCheck.IsChecked == true;
        (LabelLayout.FromConfig(_services.Cfg) with { ShowHeadline = show }).SaveTo(_services.Cfg);
        _host.ShowStatus(show ? "Подпись назначения печатается" : "Подпись назначения выключена");
    }

    private void LabelHoleText_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_printingTabFilling) return;
        var current = LabelLayout.FromConfig(_services.Cfg);
        var value = (LabelHoleTextInput.Text ?? "").Trim();
        if (string.Equals(value, current.HoleText, StringComparison.Ordinal)) return;

        (current with { HoleText = value }).SaveTo(_services.Cfg);
        LabelHoleTextInput.Text = LabelLayout.FromConfig(_services.Cfg).HoleText;
        _host.ShowStatus(value.Length == 0
            ? "Код печатается без окошка в центре"
            : $"Подпись в центре кода: {value}");
    }

    private void InstructionBaseUrl_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_printingTabFilling) return;
        var url = InstructionBaseUrlInput.Text.Trim().TrimEnd('/');
        if (url == _services.Cfg.InstructionBaseUrl()) return;

        _services.Cfg.SetInstructionBaseUrl(url);
        // Пустой адрес возвращает предустановку компании (ConfigService.PresetKeys) — поле
        // перечитывается, чтобы человек видел сохранённое значение, а не пустоту.
        InstructionBaseUrlInput.Text = _services.Cfg.InstructionBaseUrl();
        _host.ShowStatus(url.Length == 0
            ? $"Поле очищено — вернулся предустановленный адрес: {_services.Cfg.InstructionBaseUrl()}"
            : $"Веб-адрес диска инструкций сохранён: {url}");
    }

    /// <summary>Макет страницы-заглушки — отдельным окном с живым предпросмотром, а не полями здесь:
    /// подобрать текст и размеры вслепую нельзя, а страницу эту видит заказчик (см. StubLayoutWindow).</summary>
    private void EditStubLayout_Click(object sender, RoutedEventArgs e)
    {
        var win = new StubLayoutWindow(_services, _host) { Owner = Window.GetWindow(this) };
        win.ShowDialog();
    }

    /// <summary>Проверка ссылки — ровно то, чего не хватает при настройке: собранный адрес открывается
    /// или нет. Спрашивается САМ базовый адрес (что за ним лежит конкретный файл — уже видно в окне
    /// этикетки): промахнуться можно в схеме, хосте или корне, а не в хвосте.</summary>
    private async void CheckInstructionUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = InstructionBaseUrlInput.Text.Trim().TrimEnd('/');
        if (url.Length == 0)
        {
            InstructionUrlCheckText.Text = "Адрес не задан — в QR уйдёт сетевой путь к файлу.";
            return;
        }

        InstructionUrlCheckText.Text = "Спрашиваем сервер…";
        var result = await UrlReachabilityCheck.CheckAsync(url);
        InstructionUrlCheckText.Text = result.Message;
    }

    private void LabelSize_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_printingTabFilling) return;

        var width = ParseMm(LabelWidthInput);
        var height = ParseMm(LabelHeightInput);
        if (width is null || height is null)
        {
            _host.ShowStatus("Размер этикетки: нужны миллиметры больше нуля (например 97,5 × 72)");
            LoadPrintingTab();
            return;
        }

        var changed = false;
        if (Math.Abs(width.Value - _services.Cfg.LabelWidthMm()) > 0.001)
        {
            _services.Cfg.SetLabelWidthMm(width.Value);
            changed = true;
        }
        if (Math.Abs(height.Value - _services.Cfg.LabelHeightMm()) > 0.001)
        {
            _services.Cfg.SetLabelHeightMm(height.Value);
            changed = true;
        }
        if (changed)
            _host.ShowStatus($"Размер этикетки: {width.Value:0.##} × {height.Value:0.##} мм");
    }

    /// <summary>Размер вводят и «97,5», и «97.5» — принимаем оба независимо от локали машины
    /// (хранится он всегда через точку, см. ConfigService).</summary>
    private static double? ParseMm(TextBox box)
    {
        var raw = box.Text.Trim().Replace(',', '.');
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
    }

    private void LabelPrinter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_printingTabFilling) return;
        var name = LabelPrinterCombo.SelectedItem as string ?? DefaultPrinterCaption;
        var value = name == DefaultPrinterCaption ? "" : name;
        if (value == _services.Cfg.LabelPrinter()) return;

        _services.Cfg.SetLabelPrinter(value);
        _host.ShowStatus(value.Length == 0 ? "Этикетки печатаются на принтер Windows по умолчанию" : $"Принтер этикеток: {value}");
    }

    /// <summary>Образец этикетки того же размера и тем же кодом, что и настоящая (LabelPrinter.
    /// BuildLabel) — иначе проверка полей ничего не проверяет.</summary>
    private void TestPrintLabel_Click(object sender, RoutedEventArgs e)
    {
        var layout = LabelLayout.FromConfig(_services.Cfg);
        var label = LabelPrinter.BuildLabel(layout, "https://example.org/проверка", "Пробная этикетка",
            $"{layout.SizeCaption()} мм, поля {layout.MarginMm:0.##} мм",
            "Если что-то срезано по краю — увеличьте поля или подвиньте макет: кнопка «QR инструкции» на карточке версии.",
            // Подпись в центре кода берётся настоящая, а не «ТЕСТ»: образец должен показывать ровно ту
            // этикетку, которая пойдёт на шкаф, вместе с подписью назначения над кодом.
            layout.EffectiveHoleText());
        var outcome = LabelPrinter.Print(label, _services.Cfg.LabelPrinter(), "Пробная этикетка");
        _host.ShowStatus(outcome.Message);
        if (!outcome.Ok)
            AppMessageBox.Show(outcome.Message, "Пробная печать", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void StickersFolder_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_printingTabFilling) return;
        var path = StickersFolderInput.Text.Trim();
        if (string.Equals(path, _services.Cfg.StickersFolder(), StringComparison.OrdinalIgnoreCase)) return;

        _services.Cfg.SetStickersFolder(path);
        ShowStickersFolderStatus();
        _host.ShowStatus(path.Length == 0 ? "Наклейки берутся из Конфиг\\Наклейки на общем диске" : $"Папка наклеек: {path}");
    }

    private void BrowseStickersFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Папка с шаблонами наклеек" };
        if (dlg.ShowDialog() != true) return;

        // Обзор всегда отдаёт абсолютный путь с буквой ЭТОЙ машины, а настройка синхронизируется:
        // папка наклеек общая, «только буква разная» — записав «Z:\…», мы прятали бы её от всех, у
        // кого тот же диск подключён под другой буквой. Внутри общего диска — сохраняем хвост.
        StickersFolderInput.Text = SharedFolderPath.ToPortable(
            _services.Cfg.RootPath(), dlg.FolderName, StickerTemplates.DefaultSubfolder);
        StickersFolder_LostFocus(sender, e);
    }

    private void OpenStickers_Click(object sender, RoutedEventArgs e) =>
        StickersWindow.ShowFor(Window.GetWindow(this), _services, _host);

    private void OpenStickersFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = StickerTemplates.FolderFor(_services.Cfg.RootPath(), _services.Cfg.StickersFolder());
        if (folder is null)
        {
            AppMessageBox.Show("Сетевой диск не настроен — открывать нечего.", "Наклейки",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // Папку создаём по требованию: пока в неё ничего не положили, её на диске может и не быть, а
        // «открыть папку» должно открывать папку, а не ошибку проводника.
        try { Directory.CreateDirectory(folder); } catch (Exception) { /* сеть недоступна — покажем как есть */ }
        PrintableDocActions.Open(folder);
        ShowStickersFolderStatus();
    }

    // ── Шаблоны паспортов ──────────────────────────────────────────────────────

    /// <summary>Куда программа будет смотреть за шаблонами паспортов с текущими настройками — тем же
    /// вычислением, что и само окно печати. Шаблоны — это просто файлы в общей папке (записей в базе
    /// у них нет), поэтому и считаем именно файлы.</summary>
    private void ShowPassportTemplatesStatus()
    {
        var folder = PassportService.TemplatesFolder(_services.Cfg.RootPath(), _services.Cfg.PassportTemplatesFolder());
        if (folder is null)
        {
            PassportTemplatesStatus.Text = "Сетевой диск не настроен — папку шаблонов взять неоткуда.";
            return;
        }
        var count = CountPassportTemplateFiles(folder);
        PassportTemplatesStatus.Text = count > 0
            ? $"{folder} — шаблонов: {count}"
            : $"{folder} — шаблонов пока нет. Нажмите «Загрузить…» или положите файлы в эту папку.";
    }

    /// <summary>Сколько файлов-шаблонов лежит в папке (верхний уровень, без ярлыков). Недоступная
    /// папка — ноль, а не исключение: шара отваливается регулярно.</summary>
    private static int CountPassportTemplateFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Count(f => !DocFileResolver.IsShortcut(f));
        }
        catch (Exception) { return 0; }
    }

    /// <summary>Загрузка шаблона паспорта — то же, что «Загрузить…» в окне наклеек: копирование файла
    /// в общую папку, без всякой записи в базу. Несколько шаблонов / разные редакции просто лежат
    /// файлами рядом.</summary>
    private void UploadPassportTemplate_Click(object sender, RoutedEventArgs e)
    {
        var folder = PassportService.TemplatesFolder(_services.Cfg.RootPath(), _services.Cfg.PassportTemplatesFolder());
        if (folder is null)
        {
            AppMessageBox.Show("Сетевой диск не настроен — класть шаблон некуда.", "Шаблоны паспортов",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите файлы шаблонов паспортов",
            Multiselect = true,
            Filter = "Документы (*.docx;*.doc;*.pdf)|*.docx;*.doc;*.pdf|Все файлы (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        var copied = 0;
        var errors = new List<string>();
        foreach (var source in dlg.FileNames)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var dst = Path.Combine(folder, Path.GetFileName(source));
                if (File.Exists(dst))
                {
                    var answer = AppMessageBox.Show(
                        $"«{Path.GetFileName(source)}» в папке уже есть. Заменить?",
                        "Шаблоны паспортов", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                    if (answer != MessageBoxResult.Yes) continue;
                }
                File.Copy(source, dst, overwrite: true);
                copied++;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(source)}: {ex.Message}");
            }
        }

        ShowPassportTemplatesStatus();
        if (errors.Count > 0)
            AppMessageBox.Show(string.Join("\n", errors), "Шаблоны паспортов", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (copied > 0)
            _host.ShowStatus(copied == 1
                ? "Шаблон паспорта загружен — он уже виден коллегам"
                : $"Загружено шаблонов: {copied} — они уже видны коллегам");
    }

    private void PassportTemplatesFolder_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_printingTabFilling) return;
        var path = PassportTemplatesFolderInput.Text.Trim();
        if (string.Equals(path, _services.Cfg.PassportTemplatesFolder(), StringComparison.OrdinalIgnoreCase)) return;

        _services.Cfg.SetPassportTemplatesFolder(path);
        ShowPassportTemplatesStatus();
        _host.ShowStatus(path.Length == 0
            ? "Шаблоны паспортов берутся из Конфиг\\Паспорта на общем диске"
            : $"Папка шаблонов паспортов: {path}");
    }

    private void BrowsePassportTemplatesFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Папка с шаблонами паспортов" };
        if (dlg.ShowDialog() != true) return;

        // Хвост от корня диска, а не буква этой машины — см. BrowseStickersFolder_Click рядом.
        PassportTemplatesFolderInput.Text = SharedFolderPath.ToPortable(
            _services.Cfg.RootPath(), dlg.FolderName, PassportService.DefaultTemplatesSubfolder);
        PassportTemplatesFolder_LostFocus(sender, e);
    }

    /// <summary>Метка подстановки. Пустое поле — не «подставлять везде», а возврат к значению по
    /// умолчанию: пустая метка нашлась бы в каждой точке текста и испортила бы весь шаблон.</summary>
    private void PassportPlaceholder_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_printingTabFilling) return;
        var value = PassportPlaceholderInput.Text.Trim();
        if (string.Equals(value, _services.Cfg.PassportNamePlaceholder(), StringComparison.Ordinal)) return;

        _services.Cfg.SetPassportNamePlaceholder(value);
        PassportPlaceholderInput.Text = _services.Cfg.PassportNamePlaceholder();
        _host.ShowStatus($"Метка названия в шаблоне: {_services.Cfg.PassportNamePlaceholder()}");
    }

    /// <summary>Двусторонняя печать паспорта. Переворот всегда по короткой стороне — по длинной
    /// оборот разворота встаёт вверх ногами, и другого осмысленного варианта для шаблона нет,
    /// поэтому переключатель один, а не выбор из двух видов дуплекса.</summary>
    private void PassportDuplex_Click(object sender, RoutedEventArgs e)
    {
        if (_printingTabFilling) return;
        _services.Cfg.SetPassportDuplexShortEdge(PassportDuplexCheck.IsChecked == true);
        _host.ShowStatus(PassportDuplexCheck.IsChecked == true
            ? "Паспорт печатается с двух сторон, переворот по короткой стороне"
            : "Паспорт печатается односторонним");
    }

    private void OpenPassportPrint_Click(object sender, RoutedEventArgs e) =>
        PassportPrintWindow.ShowFor(Window.GetWindow(this), _services, _host);

    private void OpenPassportTemplatesFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = PassportService.TemplatesFolder(_services.Cfg.RootPath(), _services.Cfg.PassportTemplatesFolder());
        if (folder is null)
        {
            AppMessageBox.Show("Сетевой диск не настроен — открывать нечего.", "Шаблоны паспортов",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Directory.CreateDirectory(folder); } catch (Exception) { /* сеть недоступна — покажем как есть */ }
        PrintableDocActions.Open(folder);
        ShowPassportTemplatesStatus();
    }

    /// <summary>Опрашивает ОБА источника (папка и GitHub) и показывает всё сразу: что нашлось в
    /// папке, что на GitHub, что установлено и какой источник будет использован. Раньше здесь
    /// спрашивался только тот источник, который сработал первым, и «папка отвалилась, работаем с
    /// GitHub» выглядело точно так же, как «папка не настроена» — то есть недоступность источника
    /// была не видна. Список версий для отката по-прежнему берётся из ТОГО источника, который будет
    /// использован (устанавливать можно только оттуда, откуда действительно можно скачать).</summary>
    private async void CheckAppUpdates_Click(object sender, RoutedEventArgs e)
    {
        AppUpdateStatusText.Text = "Проверка обновлений…";
        InstallLatestBtn.IsEnabled = false;

        var report = await AppUpdateService.ProbeSourcesAsync(_services.Cfg.EffectiveAppUpdatePath(),
            Services.ConnectionStatusService.DefaultTimeout);
        AppUpdateStatusText.Text = AppUpdateService.DescribeSources(report);

        if (report.EffectiveSource is null)
        {
            _latestAppRelease = null;
            AppVersionsCombo.ItemsSource = null;
            return;
        }

        UpdateCheckResult result;
        try
        {
            result = await AppUpdateService.CheckForUpdatesAsync(_services.Cfg.EffectiveAppUpdatePath());
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
            _latestAppRelease = null;
            return;
        }

        _latestAppRelease = result.Releases[0];
        if (_latestAppRelease.Version > AppUpdateService.CurrentVersion)
        {
            AppUpdateStatusText.Text += $"\nДоступна новая версия: {_latestAppRelease.Version}.";
            InstallLatestBtn.IsEnabled = true;
        }
    }

    private void InstallLatestUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_latestAppRelease == null) return;
        _ = InstallAppVersionAsync(_latestAppRelease);
    }

    /// <summary>Открыть постоянный журнал «что менялось по версиям программы» — то же, что разовое
    /// окно «Что нового» после обновления, но сохранённое (ConfigService.AppChangelogHistory), чтобы
    /// вернуться в любой момент.</summary>
    private void ShowAppChangelog_Click(object sender, RoutedEventArgs e)
    {
        var win = new AppChangelogWindow(_services.Cfg.AppChangelogHistory()) { Owner = Window.GetWindow(this) };
        win.ShowDialog();
    }

    private void InstallSelectedVersion_Click(object sender, RoutedEventArgs e)
    {
        if (AppVersionsCombo.SelectedItem is not AppVersionOption option)
        {
            AppMessageBox.Show("Сначала нажмите «Проверить обновления» и выберите версию.", "Установка версии",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _ = InstallAppVersionAsync(option.Release);
    }

    /// <summary>async Task, а не async void: обработчик — это кнопка выше, а не сам метод, и падение
    /// внутри async void ушло бы мимо try/catch прямо в необработанное исключение приложения.
    /// Вызывается как «пустил и не жду» тем же `_ = …`, что и остальные такие места в проекте.</summary>
    private async Task InstallAppVersionAsync(UpdateRelease release)
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

    /// <summary>Откат/возврат из отката/ручная отметка «текущей» — программист/администраторское
    /// действие (как в Upload); прячем кнопки в Прошивках от всех кроме administrator (единственная
    /// роль с доступом и к Настройкам, и к Загрузке).</summary>
    private void UpdateRollbackAccess()
    {
        var visible = _services.Cfg.CurrentRole() == "administrator" ? Visibility.Visible : Visibility.Collapsed;
        RollbackFirmwareBtn.Visibility = visible;
        SetCurrentFirmwareBtn.Visibility = visible;
        UnrollbackFirmwareBtn.Visibility = visible;
        UpdateFirmwareActionState();
    }

    private void Password_LostFocus(object sender, RoutedEventArgs e) => SavePassword(sender as PasswordBox);

    private void Password_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SavePassword(sender as PasswordBox);
    }

    /// <summary>Поля не подставляются текущим паролем при загрузке (см. LoadGeneral — хеш нельзя
    /// развернуть обратно), поэтому пустое поле здесь означает «не трогать этот пароль», а не
    /// «сделать пароль пустым»: иначе обычный проход фокусом по вкладке молча снёс бы пароль
    /// администратора — единственный аварийный вход при проблемах с доменом.
    ///
    /// Кнопки «Сохранить пароли» больше нет — набранный пароль уезжает в конфиг по уходу из поля и
    /// по Enter. Убрать пароль программиста совсем можно только явным действием, кнопкой
    /// «Убрать пароль программиста» (ConfigService.SetProgrammerPassword("") хранит пустую строку как
    /// есть, не хешируя её, — VerifyProgrammerPassword трактует это как «пароль не требуется»).
    /// Само поле после сохранения очищается: показывать набранный пароль дальше незачем.</summary>
    private void SavePassword(PasswordBox? box)
    {
        if (box is null || box.Password.Length == 0) return;

        if (ReferenceEquals(box, AdminPwdInput))
        {
            _services.Cfg.SetAdminPassword(box.Password);
            _host.ShowStatus("Пароль администратора сохранён");
        }
        else
        {
            _services.Cfg.SetProgrammerPassword(box.Password);
            _host.ShowStatus("Пароль программиста сохранён");
        }
        box.Password = "";
    }

    private void ClearProgrammerPassword_Click(object sender, RoutedEventArgs e)
    {
        var reply = AppMessageBox.Show(
            "Убрать пароль программиста?\n\nРоль «Программист» перестанет его спрашивать — переключиться на неё сможет любой, кто открыл приложение.",
            "Пароли доступа", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        _services.Cfg.SetProgrammerPassword("");
        ProgPwdInput.Password = "";
        _host.ShowStatus("Пароль программиста убран — роль больше его не спрашивает");
    }

    private void AdSettings_LostFocus(object sender, RoutedEventArgs e) => SaveAdSettings();

    private void AdSettings_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveAdSettings();
    }

    private void AdSettings_Changed(object sender, RoutedEventArgs e) => SaveAdSettings();

    /// <summary>Весь AD-блок сохраняется сам, без кнопки «Сохранить группы и способ»: текстовые поля —
    /// по уходу фокуса и по Enter, переключатель способа и галочка «требовать вход» — сразу по выбору.
    /// Пишем только те ключи, что реально изменились, и сообщаем в статус-строку одним сообщением —
    /// иначе каждое открытие вкладки и каждый переход между полями сыпали бы уведомления.
    ///
    /// Срок повторного входа (AdRequireLoginDaysInput) здесь НЕ трогается — у него своё поле и своё
    /// автосохранение (SaveAdRequireLoginDays), видимое всем ролям, а этот блок целиком скрыт от
    /// наладчика/программиста (см. ApplyRoleVisibility).</summary>
    private void SaveAdSettings()
    {
        // Флаг вкладки «Подключение», а не «Общих»: блок AD живёт теперь там и заполняется оттуда.
        if (_connectionTabFilling) return;

        var changed = new List<string>();
        void SaveKey(string key, string value, string label)
        {
            if (!SettingsAutoSave.TextChanged(value, _services.Cfg.Get(key))) return;
            _services.Cfg.Set(key, value.Trim());
            changed.Add(label);
        }

        SaveKey("ad_domain", AdDomainInput.Text, "домен");
        SaveKey("ad_group_administrator", AdGroupAdminInput.Text, "группа администратора");
        SaveKey("ad_group_programmer", AdGroupProgInput.Text, "группа программиста");
        SaveKey("ad_group_naladchik", AdGroupNaladchikInput.Text, "группа наладчика");

        var url = AdHttpUrlInput.Text.Trim();
        if (SettingsAutoSave.TextChanged(url, _services.Cfg.AdHttpUrl()))
        {
            _services.Cfg.SetAdHttpUrl(url);
            changed.Add("URL веб-сервера");
        }

        // Способ проверки пароля здесь больше не сохраняется: он задаётся комбобоксом «Чем
        // подтверждаем личность» на этой же вкладке (AuthKind_Changed) — один ключ, одно место.

        var requireLogin = AdRequireLoginCheck.IsChecked == true;
        if (requireLogin != _services.Cfg.AdRequireLogin())
        {
            _services.Cfg.SetAdRequireLogin(requireLogin);
            changed.Add(requireLogin ? "вход по AD включён" : "вход по AD выключен");
        }

        if (changed.Count == 0) return;
        _host.ShowStatus("Настройки AD сохранены: " + string.Join(", ", changed));
    }

    private void AdRequireLoginDays_LostFocus(object sender, RoutedEventArgs e) => SaveAdRequireLoginDays();

    private void AdRequireLoginDays_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveAdRequireLoginDays();
    }

    /// <summary>The one AD-related field naladchik/programmer can see (see ApplyRoleVisibility) —
    /// saves only the "TTL days" value, without touching domain/groups/mode/URL/the require-login
    /// switch itself, since those controls aren't even in the visual tree for those two roles.
    /// Сохраняется само, по уходу фокуса и по Enter (кнопки «Сохранить» больше нет).</summary>
    private void SaveAdRequireLoginDays()
    {
        if (_connectionTabFilling) return;
        var edit = SettingsAutoSave.ParseNumber(AdRequireLoginDaysInput.Text, _services.Cfg.AdRequireLoginDefaultDays(), min: 1,
            "Срок повторного входа по AD: нужно целое число дней больше нуля");
        if (edit.Invalid)
        {
            AdRequireLoginDaysInput.Text = edit.Value.ToString();
            _host.ShowStatus(edit.Message);
            return;
        }
        if (!edit.Save) return;

        _services.Cfg.SetAdRequireLoginDefaultDays(edit.Value);
        _host.ShowStatus($"Срок повторного входа по AD: {edit.Value} дн.");
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

    /// <summary>Быстрый путь к смене роли по образцу двойного клика в других таблицах Настроек
    /// (Иерархия — HierarchyGrid_MouseDoubleClick). Смена роли — не текстовое поле вроде описания/
    /// тегов, которое можно один клик и сразу отредактировать: тут нужно ЕЩЁ И выбрать новую роль,
    /// поэтому двойной клик не меняет роль сам по себе (это было бы неожиданно и рискованно —
    /// случайный двойной клик реально сменил бы права доступа), а только подставляет текущую роль
    /// пользователя в UserRoleCombo и сразу раскрывает список — остаётся выбрать новую роль и
    /// нажать «Сменить роль», без отдельного клика по самому комбобоксу.</summary>
    private void UsersGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!DataGridClickGuard.IsOverDataRow(e)) return;
        if (UsersGrid.SelectedItem is not UserRow row) return;

        UserRoleCombo.SelectedValue = row.Record.Role;
        UserRoleCombo.Focus();
        UserRoleCombo.IsDropDownOpen = true;
    }

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

    private void KeepArchives_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingGeneral) return;
        var on = KeepArchivesCheck.IsChecked == true;
        if (on == _services.Cfg.KeepArchives()) return;

        _services.Cfg.Set("keep_archives", on ? "true" : "false");
        _host.ShowStatus(on ? "Архивы будут храниться после извлечения" : "Архивы будут удаляться после извлечения");
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

        ExtSchematicList.Items.Clear();
        foreach (var ext in _services.Db.GetAllowedExtensionsSchematic())
            ExtSchematicList.Items.Add(new ListBoxItem { Content = $".{ext}", Tag = ext });
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

    private void ControllersGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!DataGridClickGuard.IsOverDataRow(e)) return;
        if (ControllersGrid.SelectedItem is not ControllerModRow row) return;

        // Строка-контроллер без модификаций — правится только имя типа.
        if (row.ModificationId is not int modId)
        {
            var newName = TextPromptDialog.Prompt(Window.GetWindow(this), "Переименовать контроллер", "Название:", row.ControllerName);
            if (string.IsNullOrWhiteSpace(newName)) return;
            var upperName = newName.Trim().ToUpperInvariant();
            if (upperName == row.ControllerName) return;

            var root0 = _services.Cfg.RootPath();
            if (!string.IsNullOrEmpty(root0) && Directory.Exists(root0))
            {
                var moved = _services.Hierarchy.RenameControllerFolders(root0, row.ControllerName, upperName);
                if (!moved.Ok)
                {
                    AppMessageBox.Show($"Не удалось переименовать папки контроллера на диске:\n{moved.Error}\n\nПереименование отменено.",
                        "Контроллер", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            _services.Db.UpdateControllerModelName(row.ControllerId, upperName);
            LoadHierarchy();
            AutoRebuild();
            _host.PushCatalogChange($"Контроллер переименован: «{row.ControllerName}» → «{upperName}»");
            _host.ShowStatus($"Контроллер переименован: {upperName}", category: NotificationCategory.Hierarchy);
            return;
        }

        // Строка-модификация — полноценная правка (тип / название / hw / описание).
        var controllers = _services.Db.GetAllControllerModels();
        var loadedCount = _services.Db.GetFwVersionsByControllerAndHw(row.ControllerId, row.HwVersion).Count;
        var hint = loadedCount > 0
            ? $"Уже загружено прошивок с hw{row.HwVersion}: {loadedCount}. При смене hw будет предложено переписать их (с переименованием папок на диске)."
            : null;

        var dlg = new AddModificationDialog(controllers, row.ControllerId, row.DisplayName, row.HwVersion, row.Description, hint)
            { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        var newCtrlId = dlg.SelectedControllerId ?? row.ControllerId;
        var hwChanged = dlg.HwVersion != row.HwVersion;
        var ctrlChanged = newCtrlId != row.ControllerId;

        _services.Db.UpdateControllerModification(modId, newCtrlId, dlg.ModName, dlg.HwVersion, dlg.Description);

        // Переписывание уже загруженных прошивок предлагаем только когда сменился именно hw и контроллер
        // остался прежним — перенос модификации к другому типу это отдельная редкая правка справочника,
        // её файлы на диске трогать не станем без явного сценария.
        if (hwChanged && !ctrlChanged && loadedCount > 0)
        {
            var ask = AppMessageBox.Show(
                $"Найдено уже загруженных прошивок с hw{row.HwVersion} для «{row.ControllerName}»: {loadedCount}.\n\n" +
                $"Переписать их на hw{dlg.HwVersion} (переименовать папки версий на диске)?",
                "Переписать hw прошивок", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (ask == MessageBoxResult.Yes)
            {
                var root = _services.Cfg.RootPath();
                var res = _services.Hierarchy.RewriteControllerHwVersion(root, row.ControllerId, row.HwVersion, dlg.HwVersion);
                if (res.Errors.Count > 0)
                    AppMessageBox.Show(
                        $"Переписано версий: {res.UpdatedRows}.\nНе удалось: {res.Errors.Count}.\n\n" + string.Join("\n", res.Errors),
                        "Переписать hw", MessageBoxButton.OK, MessageBoxImage.Warning);
                _host.ShowStatus($"hw прошивок переписан: {res.UpdatedRows} версий (hw{row.HwVersion} → hw{dlg.HwVersion})",
                    category: NotificationCategory.Hierarchy);

                // Рассылаем переписывание hw как ЯВНУЮ операцию-переименование: без этого смена hw
                // едет как обычный дифф строк fw_versions (hw зашит в version_raw = натуральный ключ),
                // и у коллег старая строка остаётся фантомом «нет папки на диске», а новая вставляется
                // дублем (ровно то, на что жаловались). Событие проигрывают остальные машины у себя
                // (ConfigSyncService.ReplayHwRewrites), переименовывая свои строки/папки, а не плодя
                // дубли. Отметку времени тут же ставим и своим watermark — чтобы не проиграть своё же.
                var ctrlSyncId = _services.Db.GetControllerSyncId(row.ControllerId);
                if (!string.IsNullOrEmpty(ctrlSyncId))
                {
                    var ts = Database.NowIsoPreciseTs();
                    _services.Db.RecordHwRewrite(ctrlSyncId, row.ControllerName, row.HwVersion, dlg.HwVersion, ts, _services.CurrentUserName);
                    _services.Cfg.SetHwRewriteAppliedAt(ts);
                    _host.PushCatalogChange($"Переписан hw прошивок: {row.ControllerName} hw{row.HwVersion} → hw{dlg.HwVersion} ({res.UpdatedRows} версий)");
                }
            }
        }

        LoadHierarchy();
        _host.PushCatalogChange($"Модификация изменена: {dlg.ModName} (hw{dlg.HwVersion})");
        _host.ShowStatus($"Модификация обновлена: {dlg.ModName} (hw{dlg.HwVersion})", category: NotificationCategory.Hierarchy);
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

    private void AddExtensionSchematic_Click(object sender, RoutedEventArgs e)
    {
        var ext = ExtSchematicInput.Text.Trim();
        if (string.IsNullOrEmpty(ext)) return;
        _services.Db.AddAllowedExtensionSchematic(ext);
        ExtSchematicInput.Text = "";
        LoadHierarchy();
        _host.PushCatalogChange($"Расширение поиска схем добавлено: .{ext.ToLowerInvariant().TrimStart('.')}");
    }

    private void DeleteExtensionSchematic_Click(object sender, RoutedEventArgs e)
    {
        if (ExtSchematicList.SelectedItem is not ListBoxItem item || item.Tag is not string ext)
        {
            AppMessageBox.Show("Выберите расширение.", "Расширение поиска схем", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var reply = AppMessageBox.Show($"Удалить расширение поиска схем «.{ext}» из списка?", "Удалить расширение",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        _services.Db.RemoveAllowedExtensionSchematic(ext);
        LoadHierarchy();
        _host.PushCatalogChange($"Расширение поиска схем «.{ext}» удалено");
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
        // Заглушка «Инструкция в разработке» — там же, где создаются папки: см. InstructionStub и
        // ту же связку в MainWindowViewModel.EnsureHierarchyAsync.
        var stubs = _services.StubWriter();
        using (_host.BeginBusy("Проверка структуры диска…"))
            result = await Task.Run(() => HierarchyService.ApplyStructurePlan(plan, stubs));
        if (result.Errors.Count > 0)
            AppMessageBox.Show(string.Join("\n", result.Errors.Take(10)), "Ошибки", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            AppMessageBox.Show($"Создано папок: {result.CreatedCount}", "Структура диска", MessageBoxButton.OK, MessageBoxImage.Information);
        _host.ShowStatus($"Структура обновлена: {result.CreatedCount} папок", category: NotificationCategory.Sync);
    }

    /// <summary>Разовая перестройка уже накопленного диска (DiskMigrationDialog). Отдельно от
    /// «Пересоздать структуру диска» выше: та только СОЗДАЁТ недостающие папки и ничего не двигает,
    /// а эта переименовывает файлы и переносит инструкции — то есть меняет то, что уже лежит.
    /// Список версий окно берёт из БД само; после закрытия перечитываем вкладку «Прошивки», потому
    /// что имена файлов у записей могли поменяться.</summary>
    private void DiskMigration_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new DiskMigrationDialog(_services, _host) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        LoadFirmwareTab();
    }

    /// <summary>Локальная починка путей ОПЦ после переноса их внутрь контроллера
    /// (docs/hierarchy-rework-plan.md, этап 5 — единственный переезд, меняющий disk_path). Нужна на
    /// каждой машине отдельно, потому что путь у совпавшей записи импортом общего конфига не
    /// обновляется никогда: у всех, кроме запускавшего перестройку, ОПЦ-прошивки иначе молча стали бы
    /// «⚠ на диске не найдена». Обход диска — в фоне: у ОПЦ-записей путей сотни.</summary>
    private async void RepairOpcPaths_Click(object sender, RoutedEventArgs e)
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            AppMessageBox.Show("Сетевой диск недоступен.", "ОПЦ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        HierarchyService.OpcRepairResult result;
        using (_host.BeginBusy("Ищем переехавшие ОПЦ…"))
            result = await Task.Run(() => _services.Hierarchy.RepairOpcDiskPaths(root));

        var text = result.Repaired == 0 && result.Unresolved == 0
            ? "Все ОПЦ-прошивки на месте — чинить нечего."
            : $"Путь исправлен у {result.Repaired} ОПЦ-прошивок." +
              (result.Unresolved > 0 ? $"\nНе нашлось новое место у {result.Unresolved} — они показаны ниже." : "") +
              (result.Details.Count > 0 ? "\n\n" + string.Join("\n", result.Details.Take(20)) : "");
        AppMessageBox.Show(text, "ОПЦ", MessageBoxButton.OK, MessageBoxImage.Information);
        _host.ShowStatus($"ОПЦ: путь исправлен у {result.Repaired}", category: NotificationCategory.Sync);
        LoadFirmwareTab();
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
        using (_host.BeginSync("поиск прошивок на диске"))
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
        // Заглушки здесь СОЗНАТЕЛЬНО не кладутся, хотя папки «Инструкция» создаются и тут. Эта
        // достройка идёт на потоке интерфейса (её зовут прямо из обработчиков правки справочника), а
        // заглушка требует обойти содержимое каждой папки на сетевом диске — окно настроек начало бы
        // подвисать на ровном месте. Папки, созданные здесь, получат заглушки при следующем запуске
        // (MainWindowViewModel.EnsureHierarchyAsync) или по кнопке «Пересоздать структуру диска» —
        // обе операции идут в фоне. См. InstructionStub.
        var result = _services.Hierarchy.EnsureStructure(root);
        if (result.CreatedCount > 0)
            _host.ShowStatus($"Папки на диске обновлены: +{result.CreatedCount}", category: NotificationCategory.Sync);
    }

    // ── Прошивки ──────────────────────────────────────────────────────────────

    private void LoadFirmwareTab()
    {
        _fwVersionsData = _services.Db.GetAllFwVersionsWithNames();
        _fwStatusLabels = FwHistoryStatus.LabelsByGroup(_fwVersionsData);
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
        // Значения совпадают с тем, что реально показано в столбце «Статус» таблицы (вычисленная
        // метка FwHistoryStatus), а не сырое поле status. Раньше здесь были «Активна»/«Откатана», а в
        // таблице значились «Текущая»/«Заменена»/«Откатана» — фильтр не совпадал ни с одной строкой на
        // экране и выглядел неработающим. «Текущая» покрывает и «Текущая (HW n)» — см. StatusCategory.
        Fill(FwStatusFilterCombo, "Статус: все",
            new[] { FwHistoryStatus.Current, FwHistoryStatus.Superseded, FwHistoryStatus.RolledBack });
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

        EditFirmwareDialog.ApplyResult(dlg, _services, _host, v.Id!.Value);

        var release = AppMessageBox.Show(
            "Вывести версию из модерации и сделать релизной?",
            "Модерация", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
        // Вместе с записями-копиями под другими подтипами — см. Database.MarkFwVersionReleasedWithLinked.
        var delivered = false;
        if (release)
        {
            _services.Db.MarkFwVersionReleasedWithLinked(v.Id!.Value);
            // Узкий канал доставки решения модерации — работает с любой машины, не только с
            // администраторской (см. ConfigSyncService.PushModerationOnly).
            delivered = ConfigSyncService.RecordAndPushModeration(_services,
                _services.Db.GetFwVersionIdsSharingFiles(v.Id!.Value), _services.CurrentUserName);
        }

        _host.ShowStatus(release
            ? $"Версия выведена из модерации: {v.VersionRaw}" + (delivered ? " (отправлено коллегам)" : "")
            : $"Теги обновлены: {v.VersionRaw}", category: NotificationCategory.FirmwareAndParams);
        LoadModerationTab();
    }

    private void PopulateFirmwareTable(List<FwVersionRecord> data) =>
        FwGrid.ItemsSource = data.Select(v => new FwRow { Record = v, StatusLabel = LabelFor(v) }).ToList();

    /// <summary>Вычисленная метка статуса записи — ровно та, что показана в столбце «Статус» таблицы.
    /// Общая для отрисовки и для фильтра по статусу, чтобы они не разъезжались.</summary>
    private string LabelFor(FwVersionRecord v) =>
        v.Id is int id && _fwStatusLabels.TryGetValue(id, out var label)
            ? label
            : (v.Status == "rolled_back" ? FwHistoryStatus.RolledBack : FwHistoryStatus.Current);

    /// <summary>Схлопывает «Текущая (HW n)» до «Текущая», чтобы одна опция фильтра ловила и общую
    /// текущую, и текущие по каждому hw. Остальные метки возвращаются как есть.</summary>
    private static string StatusCategory(string label) =>
        label.StartsWith(FwHistoryStatus.Current, StringComparison.Ordinal) ? FwHistoryStatus.Current : label;

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
            rows = rows.Where(v => StatusCategory(LabelFor(v)) == status);
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

    /// <summary>Двойной клик ОТКРЫВАЕТ файл прошивки — как двойной клик по строке в «Параметрах
    /// ПЧ/УПП» открывает файл параметров. Раньше он открывал окно модерации, и это расходилось и с
    /// проводником, и с соседней страницей приложения: «двойной клик = открыть то, на что смотрю».
    /// Модерация осталась кнопкой «Редактировать» рядом с таблицей.</summary>
    private void FwGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataGridClickGuard.IsOverDataRow(e)) OpenSelectedFirmwareFile();
    }

    private void OpenFirmwareFile_Click(object sender, RoutedEventArgs e) => OpenSelectedFirmwareFile();

    /// <summary>Тот же выбор файла, что и у кнопки «Открыть прошивку ПЛК» на карточке поиска
    /// (PlcOpenResolver): уважает подсказку «чем открывать», предпочитает .psl у проектов Segnetics и
    /// не подсовывает файл панели вместо программы ПЛК. Папку версии на диске ищем через
    /// FirmwareDiskPresence — записанный disk_path мог устареть после переименования папки.
    /// Открывать нечего — показываем хотя бы папку, это полезнее сообщения «не найдено».</summary>
    private void OpenSelectedFirmwareFile()
    {
        // «Выберите прошивку в таблице» показывает сам GetSelectedFwVersion — второго такого же
        // сообщения здесь быть не должно.
        var v = GetSelectedFwVersion();
        if (v is null) return;

        var dir = FirmwareDiskPresence.ResolveVersionDir(v.DiskPath, v.VersionRaw);
        if (string.IsNullOrEmpty(dir))
        {
            AppMessageBox.Show($"Папка версии на диске не найдена:\n{v.DiskPath}", "Прошивки",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var folders = new[] { dir };
        var target = PlcOpenResolver.Resolve(new PlcOpenSources
        {
            CandidateFolders = folders,
            VersionFolders = folders,
            FilteredFolders = folders,
            ExecutableHint = v.ExecutableHint,
            NetworkFolder = dir,
        }) ?? dir;

        PrintableDocActions.Open(target);
        _host.ShowStatus($"Открыто: {Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar))}",
            category: NotificationCategory.FirmwareAndParams);
    }

    private void FwGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFirmwareActionState();

    /// <summary>Доступность кнопок статуса зависит от выбранной версии — раньше они были включены
    /// всегда, и «Вернуть в активные» у активной прошивки лишь ругалось диалогом после нажатия.
    /// Теперь недоступное действие сразу серое: откатанную можно только вернуть в активные, активную —
    /// откатить или (если она не текущая) сделать текущей.</summary>
    private void UpdateFirmwareActionState()
    {
        var v = FwGrid.SelectedItem is FwRow row ? row.Record : null;
        if (v is null)
        {
            SetCurrentFirmwareBtn.IsEnabled = false;
            RollbackFirmwareBtn.IsEnabled = false;
            UnrollbackFirmwareBtn.IsEnabled = false;
            return;
        }

        var isRolledBack = v.Status == "rolled_back";
        var label = v.Id is int id && _fwStatusLabels.TryGetValue(id, out var l) ? l : "";
        var isCurrent = label == FwHistoryStatus.Current || label == FwHistoryStatus.CurrentForHw(v.HwVersion);

        RollbackFirmwareBtn.IsEnabled = !isRolledBack;
        UnrollbackFirmwareBtn.IsEnabled = isRolledBack;
        SetCurrentFirmwareBtn.IsEnabled = !isRolledBack && !isCurrent;
    }

    private void EditFirmware_Click(object sender, RoutedEventArgs e)
    {
        var v = GetSelectedFwVersion();
        if (v is null) return;
        var title = $"{v.GroupName} {v.SubtypeName} {v.CtrlName} {v.VersionRaw}";
        var dlg = new EditFirmwareDialog(_services, v, title) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        // Изменилось что-то или нет — считаем здесь только ради случая «ничего»: обо всём остальном
        // (что именно за теги и у какой прошивки — человекопонятным именем) сообщает ReportChanges.
        bool tagsChanged = !new HashSet<string>(TagString.Parse(v.Tags), StringComparer.OrdinalIgnoreCase)
            .SetEquals(TagString.Parse(dlg.ResultTags));
        bool otherChanged = v.Description != dlg.ResultDescription ||
            !new HashSet<string>(v.LaunchTypes, StringComparer.OrdinalIgnoreCase).SetEquals(dlg.ResultLaunchTypes);

        EditFirmwareDialog.ApplyResult(dlg, _services, _host, v.Id!.Value);
        if (!tagsChanged && !otherChanged)
            _host.ShowStatus($"Без изменений: {title}", category: NotificationCategory.FirmwareAndParams);
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

    /// <summary>Ручной оверрайд «текущей» версии в её hw-группе (см. Database.
    /// SetFwVersionManualCurrent / FwHistoryStatus.Labels) — например, когда более новую по номеру
    /// версию на практике забраковали и вернулись к прежней, не откатывая её формально (откат убрал
    /// бы версию из истории/поиска совсем, а тут нужно лишь поменять, какая из активных версий
    /// считается текущей).</summary>
    private void SetCurrentFirmware_Click(object sender, RoutedEventArgs e)
    {
        var v = GetSelectedFwVersion();
        if (v is null) return;
        if (v.Status == "rolled_back")
        {
            AppMessageBox.Show("Откатанную версию нельзя сделать текущей — сначала верните её в активные.",
                "Сделать текущей", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var currentLabel = v.Id is int id && _fwStatusLabels.TryGetValue(id, out var label) ? label : "";
        if (currentLabel == FwHistoryStatus.Current || currentLabel == FwHistoryStatus.CurrentForHw(v.HwVersion))
        {
            AppMessageBox.Show("Эта версия уже текущая.", "Сделать текущей", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var reply = AppMessageBox.Show(
            $"Сделать версию {v.VersionRaw} текущей для этого шкафа (HW {v.HwVersion})?\n\n" +
            "Версия с бо́льшим SW-номером в той же группе перестанет считаться текущей и будет показана " +
            "как «Заменена», хотя формально останется активной — вычисление статуса учтёт эту ручную отметку.",
            "Сделать текущей", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        _services.Db.SetFwVersionManualCurrent(v.Id!.Value);
        _host.ShowStatus($"Отмечена текущей: {v.VersionRaw}", category: NotificationCategory.FirmwareAndParams);
        LoadFirmwareTab();
    }

    /// <summary>Обратное действие RollbackFirmware_Click — снимает отметку «Откатана» (см. Database.
    /// UnrollbackFwVersion).</summary>
    private void UnrollbackFirmware_Click(object sender, RoutedEventArgs e)
    {
        var v = GetSelectedFwVersion();
        if (v is null) return;
        if (v.Status != "rolled_back")
        {
            AppMessageBox.Show("Эта версия не откатана.", "Вернуть в активные", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var reply = AppMessageBox.Show(
            $"Вернуть версию {v.VersionRaw} в активные?\n\n" +
            "Статус в базе снова станет обычным, версия будет учитываться при вычислении текущей/заменённой.\n" +
            "Папку на диске (переименованную при откате, с маркером «_ОТКАТАНО» в имени) это не переименует " +
            "обратно — имя придётся поправить вручную при необходимости.",
            "Вернуть в активные", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        _services.Db.UnrollbackFwVersion(v.Id!.Value);
        _host.ShowStatus($"Возвращена в активные: {v.VersionRaw}", category: NotificationCategory.FirmwareAndParams);
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

        // Та же прошивка может быть заведена под несколькими подтипами шкафов — файлы на диске при
        // этом ОДНИ (см. FirmwareSubtypeLinkService). Тогда это удаление ссылки, а не прошивки:
        // файлы остаются другим записям, и говорим об этом прямо, чтобы «безвозвратно» не пугало зря.
        var filesShared = v.Id is not null && _services.Db.IsDiskPathSharedByOtherVersions(v.DiskPath, v.Id.Value);

        var confirm = AppMessageBox.Show(
            $"Удалить прошивку «{title}» безвозвратно?\n\n" +
            (filesShared
                ? "Файлы на диске останутся: эта же прошивка заведена ещё под другим подтипом шкафа " +
                  "и лежит на диске одна на всех. Удалится только эта запись.\n"
                : "Будут удалены запись в базе и файлы на диске (папка версии" +
                  (string.IsNullOrEmpty(v.HmiPath) ? "" : ", включая приложенный HMI-проект") + ").\n") +
            "Это НЕЛЬЗЯ отменить из приложения (не «Откатить» — история не остаётся).\n\n" +
            "Удаление перенесётся на другие машины при следующей синхронизации конфига (Настройки → " +
            "Сетевые диски) — включая попытку убрать файлы и там. До тех пор, пока хотя бы одна другая " +
            "машина не синхронизируется, прошивка на ней ещё будет видна.",
            "Удаление прошивки", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        var warnings = new List<string>();
        try
        {
            if (!filesShared && !string.IsNullOrEmpty(v.DiskPath) && Directory.Exists(v.DiskPath))
                FileSystemHelpers.RmtreeSafe(v.DiskPath);
        }
        catch (Exception ex) { warnings.Add($"Папка версии: {ex.Message}"); }

        try
        {
            if (!filesShared && !string.IsNullOrEmpty(v.HmiPath) && v.HmiPath.Contains(v.VersionRaw, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(v.HmiPath)) FileSystemHelpers.RmtreeSafe(v.HmiPath);
                else if (File.Exists(v.HmiPath)) File.Delete(v.HmiPath);
            }
        }
        catch (Exception ex) { warnings.Add($"HMI-проект: {ex.Message}"); }

        _services.Db.TombstoneFwVersion(v.Id!.Value);
        // Удаление — такое же решение модерации, как «выпустить», и точно так же обязано доехать до
        // коллег с любой машины: узкий канал (ConfigSyncService.PushModerationOnly) дописывает
        // tombstone в общий конфиг, не дожидаясь полного экспорта администратора.
        ConfigSyncService.RecordAndPushModeration(_services, v.Id!.Value, _services.CurrentUserName);
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

        var name = Path.GetFileNameWithoutExtension(dlg.FileName);
        apps.Add(new AppRow { Name = name, Path = dlg.FileName });
        SaveApps($"Добавлено в быстрый доступ: {name}");
    }

    private void DeleteApp_Click(object sender, RoutedEventArgs e)
    {
        if (AppsGrid.SelectedItem is not AppRow row) return;
        (AppsGrid.ItemsSource as ObservableCollection<AppRow>)?.Remove(row);
        SaveApps($"Убрано из быстрого доступа: {row.Name}");
    }

    /// <summary>Правка названия/пути прямо в таблице сохраняется по окончании строки. WPF записывает
    /// отредактированное значение в объект строки уже ПОСЛЕ этого события, поэтому сохранение
    /// откладывается на следующий проход диспетчера — иначе в конфиг ушло бы предыдущее значение
    /// (тот же приём, что в LayoutFallbackGrid_CellEditEnding выше).</summary>
    private void AppsGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        Dispatcher.BeginInvoke(new Action(() => SaveApps("Быстрый доступ сохранён")),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void SaveApps(string statusMessage)
    {
        if (AppsGrid.ItemsSource is not ObservableCollection<AppRow> apps) return;
        var list = apps
            .Where(a => !string.IsNullOrWhiteSpace(a.Name) || !string.IsNullOrWhiteSpace(a.Path))
            .Select(a => new QuickApp { Name = a.Name, Path = a.Path })
            .ToList();
        _services.Cfg.SetQuickApps(list);
        _host.ReloadSidebarApps();
        _host.ShowStatus(statusMessage);
    }
}
