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

        /// <summary>«Инструкции на этот шкаф не будет» (см. EquipmentSubType.NoInstruction). В отличие
        /// от соседей — обычное поле, а не вычисление по Subtype: галочку переключают прямо в таблице,
        /// и строка обязана показать новое положение, не перечитывая всю иерархию из базы.</summary>
        public bool NoInstruction { get; set; }
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
        public bool IsRolledBack => FwStatus.IsRolledBack(Record.Status);
        /// <summary>Выключенная строка притушена так же, как откатанная: обе не выдаются. Разделяет
        /// их столбец «Статус» («Откатана» / «Выключена») — цвет лишь говорит «эта в работе не
        /// участвует».</summary>
        public bool IsDisabled => FwStatus.IsDisabled(Record.Status);
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
            BuildSidebar();
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
        TabBtnTags, TabBtnTickets, TabBtnHosting, TabBtnEmail, TabBtnQuickApps, TabBtnLoader, TabBtnConnection, TabBtnPrinting, TabBtnUsers,
        TabBtnCleanup,
    };

    /// <summary>РАЗДЕЛЫ НАСТРОЕК ПО СМЫСЛУ — порядок и группировка бокового списка в бете.
    ///
    /// Группы ровно те же, по которым о настройках и говорят вслух: «работа» — то, во что лазят по
    /// делу; «сервис» — тикеты и хранилище, которые сюда и переехали из бокового меню («тикеты это
    /// сервисное это убрать надо в настройки»); «оборудование» — лоадер, подключение, печать;
    /// «администрирование» — то, что трогает один человек на контору и считанные разы.
    ///
    /// Порядок внутри групп — от частого к редкому, а не алфавитный: алфавит ставит «Чистку диска»
    /// рядом с «Общими», и человек каждый раз перечитывает весь список.</summary>
    private (string Group, Button Button)[] SidebarLayout() => new[]
    {
        ("", TabBtnGeneral),
        ("РАБОТА", TabBtnHierarchy),
        ("", TabBtnFirmware),
        ("", TabBtnModeration),
        ("", TabBtnTags),
        ("", TabBtnEmail),
        ("", TabBtnQuickApps),
        ("СЕРВИС", TabBtnTickets),
        ("", TabBtnHosting),
        ("ОБОРУДОВАНИЕ", TabBtnLoader),
        ("", TabBtnConnection),
        ("", TabBtnPrinting),
        ("АДМИНИСТРИРОВАНИЕ", TabBtnReservations),
        ("", TabBtnUsers),
        ("", TabBtnCleanup),
    };

    /// <summary>Какие разделы боковой список пропустил. Пусто — значит ни один не потерян.
    ///
    /// Нужно ПРОВЕРКОЙ, а не внимательностью: боковой список перечисляет кнопки вручную
    /// (SidebarLayout), и вкладка, заведённая в разметке, но забытая там, в бете просто не
    /// нарисовалась бы — экран настроек молча лишился бы раздела, и заметил бы это пользователь.
    /// Отдаётся наружу тестам (ViewsRenderTests), а не прячется за #if BETA: проверять надо сборку,
    /// которую тесты и собирают.</summary>
    internal string[] TabsMissingFromSidebar()
    {
        var listed = SidebarLayout().Select(x => x.Button).ToList();
        return AllTabButtons()
            .Where(b => !listed.Any(x => ReferenceEquals(x, b)))
            .Select(b => b.Name)
            .ToArray();
    }

    /// <summary>БОКОВОЙ СПИСОК РАЗДЕЛОВ вместо ленты пилюль (бета 2.0).
    ///
    /// Жалоба была дважды: «пелюль сверху стало уже слишком много» и потом, уже по сделанному,
    /// «поле настроек все ещё плохо выглядит». Первый заход прятал редкие разделы под «Ещё» —
    /// пилюль стало меньше, но разделы, которые человек ищет глазами, уехали в выпадающее меню, то
    /// есть стало не лучше, а по-другому плохо.
    ///
    /// Здесь разделы стоят столбиком слева, видны все разом и разбиты подзаголовками по смыслу —
    /// так устроены настройки везде, где их больше горсти: Windows 11, macOS, телефон. Кнопки — те
    /// же самые объекты, что и в ленте: переключение, подсветка активного, права роли
    /// (ApplyRoleVisibility) работают ровно как работали, меняется только то, где они нарисованы.
    ///
    /// Зовётся ПОСЛЕ ApplyRoleVisibility: кнопки, закрытые ролью, уже скрыты, и заголовок группы,
    /// в которой не осталось ни одного видимого раздела, не показывается — иначе у наладчика висел
    /// бы пустой «АДМИНИСТРИРОВАНИЕ».</summary>
    private void BuildSidebar()
    {
        if (!AppIdentity.IsBeta) return;

        // Лента переезжает из своей строки в левую колонку основной строки — и становится
        // вертикальной. Отдельного контрола под это не заводим: два списка одних и тех же разделов
        // разъехались бы при первой же добавленной вкладке.
        Grid.SetRow(TabBarScroll, 2);
        Grid.SetColumn(TabBarScroll, 0);
        Grid.SetColumnSpan(TabBarScroll, 1);
        TabBarScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        TabBarScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        TabBarScroll.Width = 224;
        TabBarScroll.Margin = new Thickness(0, 0, 20, 0);
        TabBar.Orientation = Orientation.Vertical;

        Grid.SetColumn(SettingsBody, 1);
        Grid.SetColumnSpan(SettingsBody, 1);

        TabBtnMore.Visibility = Visibility.Collapsed;

        TabBar.Children.Clear();
        foreach (var (group, button) in SidebarLayout())
        {
            if (group.Length > 0 && GroupHasVisible(group))
                TabBar.Children.Add(new TextBlock
                {
                    Text = group,
                    Style = (Style)FindResource("MutedText"),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(10, 12, 0, 3),
                });

            // Плотнее, чем в ленте: столбиком разделов полтора десятка, и с прежними отступами
            // последние («Пользователи», «Чистка диска») уходили за нижний край окна 1280×800 —
            // видно на снимке первой сборки бокового списка.
            button.Margin = new Thickness(0, 0, 0, 1);
            button.Padding = new Thickness(12, 6, 12, 6);
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            TabBar.Children.Add(button);
        }
        TabBar.Children.Add(TabBtnMore);
    }

    /// <summary>Есть ли в группе хоть один раздел, доступный текущей роли. Нужен ровно затем, чтобы
    /// не рисовать заголовок над пустотой.</summary>
    private bool GroupHasVisible(string group)
    {
        var layout = SidebarLayout();
        var start = Array.FindIndex(layout, x => x.Group == group);
        if (start < 0) return false;
        for (var i = start; i < layout.Length; i++)
        {
            if (i > start && layout[i].Group.Length > 0) break;
            if (layout[i].Button.Visibility == Visibility.Visible) return true;
        }
        return false;
    }

    /// <summary>Вкладки, которые в бете уезжают под «Ещё»: нужны считанные разы в месяц, а ряд
    /// занимали наравне с ежедневными («пелюль сверху стало уже слишком много»). Это НЕ урезание
    /// прав: что из них вообще доступно роли, по-прежнему решает ApplyRoleVisibility, а «Ещё»
    /// показывает только разрешённое.</summary>
    private (Button Button, string Label)[] RareTabs() => new[]
    {
        (TabBtnReservations, "Резервация номеров"),
        (TabBtnQuickApps, "Быстрый доступ"),
        (TabBtnLoader, "Лоадер"),
        (TabBtnConnection, "Подключение"),
        (TabBtnPrinting, "Печать"),
        (TabBtnUsers, "Пользователи"),
        (TabBtnCleanup, "Чистка диска"),
    };

    /// <summary>Свернуть редкие вкладки под «Ещё». Зовётся ПОСЛЕ ApplyRoleVisibility: прячем только
    /// то, что этой роли и так было видно, иначе «Ещё» предлагало бы страницы, куда человеку
    /// нельзя.</summary>
    private void CollapseRareTabs()
    {
        if (!AppIdentity.IsBeta) return;

        var any = false;
        foreach (var (button, _) in RareTabs())
        {
            if (button.Visibility != Visibility.Visible) continue;
            button.Visibility = Visibility.Collapsed;
            button.Tag = "Hidden";      // пометка «спрятана, но роли доступна» — читается в меню
            any = true;
        }
        TabBtnMore.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MoreTabs_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = TabBtnMore, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (button, label) in RareTabs())
        {
            // В меню попадает только то, что роли доступно: пометку поставил CollapseRareTabs.
            if (!Equals(button.Tag, "Hidden")) continue;
            var item = new MenuItem { Header = label };
            var captured = button;
            item.Click += (_, _) => Tab_Click(captured, new RoutedEventArgs());
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) return;
        menu.IsOpen = true;
    }

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

    /// <summary>Открыть вкладку настроек по имени — ТОЛЬКО для самоснимка (UiSnapshotService).
    /// Снаружи вкладки переключаются нажатием, а обход снимает страницы без нажатий; без этой
    /// щёлочки «Настройки» всегда снимались бы на первой вкладке, и ни «Прошивки», ни «Модерация»
    /// глазами перед релизом не проверялись бы вовсе.</summary>
    public void ShowTabForSnapshot(string tab)
    {
        var button = tab switch
        {
            "firmware" => TabBtnFirmware,
            "moderation" => TabBtnModeration,
            "hierarchy" => TabBtnHierarchy,
            "loader" => TabBtnLoader,
            _ => TabBtnGeneral,
        };
        Tab_Click(button, new RoutedEventArgs());
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
        TicketsTab.Visibility = Visibility.Collapsed;
        HostingTab.Visibility = Visibility.Collapsed;
        EmailTab.Visibility = Visibility.Collapsed;
        QuickAppsTab.Visibility = Visibility.Collapsed;
        ConnectionsTab.Visibility = Visibility.Collapsed;
        UsersTab.Visibility = Visibility.Collapsed;
        LoaderTab.Visibility = Visibility.Collapsed;
        ConnectionTab.Visibility = Visibility.Collapsed;
        PrintingTab.Visibility = Visibility.Collapsed;
        CleanupTab.Visibility = Visibility.Collapsed;

        if (sender == TabBtnCleanup) { CleanupTab.Visibility = Visibility.Visible; OpenCleanupTab(); }
        else if (sender == TabBtnLoader) { LoaderTab.Visibility = Visibility.Visible; LoadLoaderTab(); }
        else if (sender == TabBtnConnection) { ConnectionTab.Visibility = Visibility.Visible; LoadConnectionTab(); }
        else if (sender == TabBtnPrinting) { PrintingTab.Visibility = Visibility.Visible; LoadPrintingTab(); }
        else if (sender == TabBtnGeneral) GeneralTab.Visibility = Visibility.Visible;
        // Исполнения теперь живут ВНУТРИ «Иерархии» (бета 2.0), поэтому и перерисовываются вместе
        // с ней: отдельной вкладки, по заходу на которую это делалось, больше нет.
        else if (sender == TabBtnHierarchy) { HierarchyTab.Visibility = Visibility.Visible; RenderExecutions(); }
        else if (sender == TabBtnFirmware) FirmwareTab.Visibility = Visibility.Visible;
        else if (sender == TabBtnModeration) { ModerationTab.Visibility = Visibility.Visible; LoadModerationTab(); }
        else if (sender == TabBtnReservations) { ReservationsTab.Visibility = Visibility.Visible; LoadReservationsTab(); }
        else if (sender == TabBtnTags) { TagsTab.Visibility = Visibility.Visible; LoadTagsTab(); }
        else if (sender == TabBtnTickets) { TicketsTab.Visibility = Visibility.Visible; ShowServicePage(TicketsTab, "tickets"); }
        else if (sender == TabBtnHosting) { HostingTab.Visibility = Visibility.Visible; ShowServicePage(HostingTab, "hosting"); }
        else if (sender == TabBtnEmail) { EmailTab.Visibility = Visibility.Visible; LoadEmailTab(); }
        else if (sender == TabBtnQuickApps) QuickAppsTab.Visibility = Visibility.Visible;
        else if (sender == TabBtnConnections) { ConnectionsTab.Visibility = Visibility.Visible; LoadConnectionsTab(); }
        else if (sender == TabBtnUsers) { UsersTab.Visibility = Visibility.Visible; LoadUsersTab(); }
    }

    /// <summary>«Чистка диска» строится при первом открытии вкладки, а не вместе с Настройками:
    /// заходят туда раз в месяц, а страница тянет за собой таблицу находок и обход диска. При КАЖДОМ
    /// открытии список находок сбрасывается — ровно то, что раньше делал возврат на страницу
    /// (см. DiskCleanupView.RefreshIfActive): пока человек был на другой вкладке, коллега мог залить
    /// прошивку в ту же папку, и «применить отмеченное» по устаревшему списку удаляло бы уже не то,
    /// что человек видел глазами.</summary>
    private void OpenCleanupTab()
    {
        if (CleanupTab.Content is not DiskCleanupView view)
        {
            view = new DiskCleanupView(_services, _host);
            CleanupTab.Content = view;
        }
        view.RefreshIfActive();
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
        // Список подключений — переделка 2.0 и инструмент администратора: в рабочей версии вкладки
        // нет вовсе, иначе наладчик получил бы экран, на котором ему нечего решать.
        TabBtnConnections.Visibility =
            isAdmin && Core.Domain.AppIdentity.IsBeta ? Visibility.Visible : Visibility.Collapsed;
        TabBtnUsers.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        // «Чистка диска» переименовывает, переносит и безвозвратно удаляет файлы на общем диске, то
        // есть чужую работу. Была единственной администраторской СТРАНИЦЕЙ (RolesConfig.RoleAccess) —
        // став вкладкой, осталась администраторской ровно так же.
        TabBtnCleanup.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        TabBtnModeration.Visibility = isAdmin || role == "naladchik" ? Visibility.Visible : Visibility.Collapsed;
        // ТЕГИ: в бете — кнопкой в «Прошивках», а не отдельной пилюлей сверху («пелюль сверху стало
        // уже слишком много»). Панель при этом одна и та же, просто показывается окном. В стабильной
        // программе всё остаётся как было: обе сборки идут из одного дерева, и менять привычное
        // расположение людям посреди рабочего дня незачем.
        // Сервисные вкладки — только в бете и только тем ролям, которым эти страницы и так были
        // доступны (RolesConfig.RoleAccess): место поменялось, права нет.
        TabBtnTickets.Visibility = AppIdentity.IsBeta ? Visibility.Visible : Visibility.Collapsed;
        TabBtnHosting.Visibility = AppIdentity.IsBeta && (isAdmin || role == "programmer")
            ? Visibility.Visible : Visibility.Collapsed;

        var tagsAllowed = isAdmin || role == "naladchik";
        var tagsAsButton = AppIdentity.IsBeta && tagsAllowed;
        TabBtnTags.Visibility = tagsAllowed && !tagsAsButton ? Visibility.Visible : Visibility.Collapsed;
        OpenTagsBtn.Visibility = tagsAsButton ? Visibility.Visible : Visibility.Collapsed;
        // «Исполнения» ведёт тот, кто собирает прошивки: администратор и программист.
        TabBtnReservations.Visibility = isAdmin || role == "programmer" ? Visibility.Visible : Visibility.Collapsed;
        // «Почта» — только администратору: здесь реквизиты почтового ящика конторы и список
        // адресатов, общий на всех. Настройка одного человека здесь меняет почту всем остальным.
        // ВКЛАДКА ДОСТУПНА ВСЕМ. Жалоба: «наладчик который заведен не может настроить себе рассылку
        // и опять весь спам получает». Настройки сервера и общий список адресов внутри остаются
        // администраторскими — они касаются всей конторы; своя рассылка и категории уведомлений
        // нужны каждому.
        TabBtnEmail.Visibility = Visibility.Visible;
        EmailServerCard.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        EmailRulesCard.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        // TabBtnGeneral/TabBtnQuickApps: no role restriction — everyone who can reach Настройки at all sees them.

        // «Лоадер» видят наладчик и программист (они и заливают), администратор — как и всё
        // остальное. «Подключение» — только администратор: способ входа и адрес сервера общие для
        // машины, а не личная настройка того, кто сейчас за ней сидит.
        TabBtnLoader.Visibility = Visibility.Visible;
        TabBtnConnection.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;

        // «Печать» видят все — этикетку и наклейки печатает наладчик. Но внутри вкладки общие
        // настройки (адрес диска инструкций, тексты этикетки, папка наклеек) уезжают по сети на все
        // машины, поэтому редактировать их может только администратор. Принтер, размер наклейки и
        // сами кнопки печати остаются всем: это железо конкретного компьютера, по сети оно не
        // разъезжается (ConfigSyncService.SkipSettingsKeys).
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

        // Боковой список пересобирается ПОСЛЕ правки видимости: заголовки групп считаются по тому,
        // что роли видно, а роль меняют и на лету (MainWindowViewModel зовёт этот метод у уже
        // созданного экрана). Без этого у вчерашнего наладчика, ставшего администратором, новые
        // разделы были бы видны, а заголовка над ними — нет.
        BuildSidebar();
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

    /// <summary>Теги отдельным окном (бета 2.0). Панель НЕ копируется: настоящая уезжает в окно и
    /// возвращается на место при закрытии — см. DetachedPanelWindow. Поэтому переименование двойным
    /// щелчком, фильтр и слова-исключения работают там ровно так же, без единой правки.</summary>
    private void OpenTags_Click(object sender, RoutedEventArgs e)
    {
        LoadTagsTab();
        if (TagsTab.Parent is not Panel origin) return;
        DetachedPanelWindow.Show(Window.GetWindow(this), "Теги", origin, TagsTab);
    }

    /// <summary>Страница-вкладка настроек: тикеты и хранилище. Создаётся при первом заходе и дальше
    /// живёт, пока открыты настройки.
    ///
    /// Почему они здесь. «Тикеты это сервисное это убрать надо в настройки», «хранилище не должно
    /// быть у программиста» — в боковом меню остаётся только работа, а сервисное собрано в одном
    /// месте. Страницы при этом те же самые, со всеми своими правами и обновлением: ничего не
    /// переписано, изменилось лишь место.</summary>
    private void ShowServicePage(ContentControl host, string pageId)
    {
        if (host.Content is null)
        {
            host.Content = pageId switch
            {
                "tickets" => new TicketsView(_services, _host),
                // HostingView просит именно модель главного окна, а не интерфейс хоста: ей нужны
                // команды перехода. В настройках хост — он же и есть, просто типизирован шире.
                "hosting" => _host as ViewModels.MainWindowViewModel is { } vm ? new HostingView(_services, vm) : null,
                _ => null,
            };
            return;
        }

        // Страница уже построена — просто просим её перечитать данные, как это делает переход по
        // меню: тикеты приезжают синхронизацией, и показывать вчерашний список нельзя.
        switch (host.Content)
        {
            case TicketsView tickets: tickets.RefreshIfActive(); break;
            case HostingView hosting: hosting.RefreshIfActive(); break;
        }
    }

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
    /// <summary>Список слов-исключений (см. Database.SearchWords.cs). Рисуется теми же «пузырьками»,
    /// что и теги рядом: это такой же справочник коротких слов, и вести себя он должен так же.</summary>
    private void RenderOnDemandWords()
    {
        OnDemandWordsPanel.Children.Clear();
        var words = _services.Db.GetOnDemandWords();
        OnDemandWordsEmptyText.Visibility = words.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var word in words)
        {
            var captured = word;
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 6) };
            panel.Children.Add(new TextBlock { Text = captured, VerticalAlignment = VerticalAlignment.Center });

            var remove = new Button
            {
                Content = "×",
                Style = (Style)FindResource("SecondaryButton"),
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(6, 0, 6, 0),
                ToolTip = "Убрать слово. Прошивки не трогаются — они снова начнут находиться как раньше.",
            };
            remove.Click += (_, _) =>
            {
                _services.Db.DeleteOnDemandWord(captured);
                RenderOnDemandWords();
            };
            panel.Children.Add(remove);

            var bubble = new Border
            {
                Style = (Style)FindResource("CardBorder"),
                Padding = new Thickness(10, 4, 6, 4),
                Margin = new Thickness(0, 0, 6, 6),
                Child = panel,
            };
            OnDemandWordsPanel.Children.Add(bubble);
        }
    }

    /// <summary>СПРАВОЧНИК ИСПОЛНЕНИЙ — таблицей, внутри «Иерархии».
    ///
    /// Переехал сюда из отдельной вкладки настроек (бета 2.0): это часть справочника оборудования,
    /// ровно как контроллеры и производители. Просьба Ильи: «исполнения вкладку перенеси в иерархия»
    /// и «отображение таблицей уже удобней будет» — у исполнения появились состояние и дата, а
    /// пузырьки их показать не могут.</summary>
    private void RenderExecutions()
    {
        if (ExecutionsGrid is null) return;
        ExecutionsGrid.ItemsSource = _services.Db.GetExecutionRows()
            .Select(r => new ExecutionRowView(r.Name, r.Active, r.CreatedAt))
            .ToList();
    }

    /// <summary>Строка таблицы исполнений. Отдельный тип, а не запись из БД напрямую: таблица
    /// показывает «Да/Нет» и прочерк вместо пустой даты, и превращать одно в другое должен тот, кто
    /// рисует, а не тот, кто хранит.</summary>
    private sealed record ExecutionRowView(string Name, bool Active, string CreatedAt)
    {
        public string ActiveText => Active ? "Да" : "Нет";

        /// <summary>Прочерк, а не сегодняшнее число: исполнения, заведённые до того, как дату начали
        /// записывать, честно показывают, что даты у них нет.</summary>
        public string CreatedText => string.IsNullOrEmpty(CreatedAt) ? "—" : CreatedAt;
    }

    private ExecutionRowView? SelectedExecution => ExecutionsGrid?.SelectedItem as ExecutionRowView;

    private void AddExecution_Click(object sender, RoutedEventArgs e) => CommitExecution();

    private void ExecutionInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        CommitExecution();
    }

    private void CommitExecution()
    {
        var name = AntarusPoFinder.Core.Domain.FwExecution.Normalize(ExecutionInput.Text);
        if (name.Length == 0) return;
        _services.Db.AddExecutionToCatalog(name);
        ExecutionInput.Clear();
        RenderExecutions();
    }

    private void ExecutionsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        RenameExecution_Click(sender, e);

    /// <summary>Переименование идёт через Database.RenameExecution, а не «удалить и завести заново»:
    /// оно обязано перенести пометку и у самих прошивок. Исполнения сравниваются точно, строка в
    /// строку (см. FwExecution), и без переноса линейка разорвалась бы надвое — версии со старым
    /// написанием перестали бы заменять новые и повисли бы в модерации все разом.</summary>
    private void RenameExecution_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedExecution is not { } row) return;

        var next = TextPromptDialog.Prompt(Window.GetWindow(this), "Переименовать исполнение",
            "Новое название:", row.Name);
        if (string.IsNullOrWhiteSpace(next) || next.Trim() == row.Name) return;

        if (!_services.Db.RenameExecution(row.Name, next))
        {
            AppMessageBox.Show(
                "Такое исполнение уже есть. Склеивать два исполнения переименованием нельзя — "
                + "прошивки разных линеек перемешались бы молча.",
                "Исполнения", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RenderExecutions();
        _host.ShowStatus($"Исполнение переименовано: {row.Name} → {next.Trim()}",
            category: NotificationCategory.Hierarchy);
    }

    /// <summary>Выключить — не то же самое, что удалить: пометка у загруженных прошивок остаётся,
    /// исполнение просто перестаёт предлагаться при новой загрузке. Линейку закрыли, история по ней
    /// читается.</summary>
    private void ToggleExecution_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedExecution is not { } row) return;
        _services.Db.SetExecutionActive(row.Name, !row.Active);
        RenderExecutions();
        _host.ShowStatus(row.Active
            ? $"Исполнение «{row.Name}» выключено — при загрузке больше не предлагается"
            : $"Исполнение «{row.Name}» включено",
            category: NotificationCategory.Hierarchy);
    }

    private void DeleteExecution_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedExecution is not { } row) return;
        if (AppMessageBox.Show(
                $"Убрать исполнение «{row.Name}» из справочника?" + Environment.NewLine + Environment.NewLine
                + "У уже загруженных прошивок пометка сохранится — перестанет только предлагаться "
                + "при новой загрузке.",
                "Исполнения", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            != MessageBoxResult.Yes) return;

        _services.Db.DeleteExecutionFromCatalog(row.Name);
        RenderExecutions();
    }

    private void AddOnDemandWord_Click(object sender, RoutedEventArgs e) => CommitOnDemandWord();

    private void OnDemandWordInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        CommitOnDemandWord();
    }

    private void CommitOnDemandWord()
    {
        var word = AntarusPoFinder.Core.Domain.FwOnDemandTerms.Normalize(OnDemandWordInput.Text);
        if (word.Length == 0) return;
        _services.Db.AddOnDemandWord(word);
        OnDemandWordInput.Clear();
        RenderOnDemandWords();
    }

    private void RenderTagsTab()
    {
        RenderOnDemandWords();
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
        // Enter пересобирает панель, поле теряет фокус — и LostFocus заходил бы на переименование
        // второй раз. Один пузырь = одна фиксация.
        var committed = false;
        void CommitOnce()
        {
            if (committed) return;
            committed = true;
            CommitTagRename(tag, input.Text);
        }
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitOnce(); e.Handled = true; }
            else if (e.Key == Key.Escape) { committed = true; _renamingTag = null; RenderTagsTab(); e.Handled = true; }
        };
        input.LostFocus += (_, _) => CommitOnce();
        input.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return new Border { Style = (Style)FindResource("TagBubbleBorder"), Child = input, Margin = new Thickness(0, 0, 6, 6) };
    }

    private void CommitTagRename(string oldTag, string newTextRaw)
    {
        _renamingTag = null;
        var newText = newTextRaw.Trim();
        // Сравнение точное, а не без учёта регистра: «пи» → «ПИ» — осмысленная правка, и раньше
        // она молча отваливалась здесь, а интерфейс всё равно рапортовал об успехе.
        if (newText.Length == 0 || newText.Equals(oldTag, StringComparison.Ordinal)) { RenderTagsTab(); return; }

        var result = _services.Db.RenameTag(oldTag, newText);
        LoadTagsTab();
        var message = result.Outcome switch
        {
            Database.TagRenameOutcome.Renamed => $"Тег переименован: «{oldTag}» → «{result.Name}»",
            Database.TagRenameOutcome.Merged => $"Тег «{oldTag}» объединён с существующим «{result.Name}»",
            _ => null,
        };
        if (message is not null)
            _host.ShowStatus(message, category: NotificationCategory.FirmwareAndParams);
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
        try { LoadGeneralCore(); LoadThemePicker(); }
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
        // ⚠️ InitThemeUi обязателен здесь. Он существовал, но не вызывался ниоткуда — и переключатель
        // темы всегда рисовался в положении «светлая», какой бы тема ни была. Со стороны это выглядит
        // так: «у меня тёмная тема, а ползунок в светлой; нажимаю — ползунок встаёт правильно, но
        // больше ничего не меняется». Ничего и не могло: нажатие ставило ту тему, которая и так уже
        // стояла.
        InitThemeUi();
        InitAccentUi();
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

    // ── Цвет оформления ──────────────────────────────────────────────────────
    //
    // Цвет читается и пишется прямо в настройки и применяется через ThemeManager, минуя
    // MainWindowViewModel: тема и цвет — разные настройки, и заводить ради цвета второй источник
    // правды о теме не нужно. Текущая тема берётся у самого ThemeManager, он её и так помнит.

    /// <summary>Тема переехала сюда из сайдбара по просьбе владельца. Раньше её здесь не было из
    /// опасения завести второй источник правды: страница настроек — отдельный UserControl без доступа
    /// к MainWindowViewModel.IsDarkTheme. Но опасение снимается тем, что переключатель в сайдбаре
    /// убран совсем: правда теперь одна — сохранённая настройка, а её текущее значение помнит сам
    /// ThemeManager. Дублирования не осталось.</summary>
    private void InitThemeUi()
    {
        _loadingGeneral = true;
        DarkThemeCheck.IsChecked = ThemeManager.Current == "dark";
        ThemeLabelText.Text = ThemeManager.Current == "dark" ? "Тёмная тема" : "Светлая тема";
        _loadingGeneral = false;
    }

    /// <summary>«Пройти обучение» — тот же тур, что показывается при первом запуске. Живёт в
    /// настройках, потому что нужен редко и осознанно; в меню кнопка остаётся только у новичка и
    /// уходит сама (см. MainWindow.SetupOnboardingButton).</summary>
    private void RunOnboarding_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main) main.RunOnboardingAgain();
    }

    /// <summary>Выбор темы списком. Появляется, только когда тем больше двух: тумблер
    /// «светлая/тёмная» их показать не может, а дублировать его списком из двух пунктов незачем.
    /// Просьба Ильи (бета 2.0): «добавь темы типа матиреал дизайн, ликвид Гласс и тп».</summary>
    private void LoadThemePicker()
    {
        var themes = ThemeCatalog.Available;
        if (themes.Count <= 2) return;

        ThemePickerPanel.Visibility = Visibility.Visible;
        // Тумблер прячем: две ручки на одну настройку — верный способ получить вопрос «а почему
        // переключатель показывает не то».
        DarkThemeCheck.Visibility = Visibility.Collapsed;
        ThemeLabelText.Visibility = Visibility.Collapsed;

        ThemeCombo.ItemsSource = themes;
        ThemeCombo.SelectedItem = themes.FirstOrDefault(t => t.Id == _services.Cfg.Theme()) ?? themes[0];
        ShowGlassOpacity();
    }

    /// <summary>Показать ползунок прозрачности, если выбрана тема со стеклом, и увести его с глаз,
    /// если нет. Значение при этом не сбрасывается: вернувшись на «Стекло», человек находит своё.</summary>
    private void ShowGlassOpacity()
    {
        var glass = ThemeCatalog.Resolve(_services.Cfg.Theme()).Backdrop;
        GlassOpacityPanel.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        if (!glass) return;

        var saved = _services.Cfg.GlassOpacity();
        var percent = saved > 0 ? GlassSurface.Clamp(saved) : GlassSurface.Default;
        // Под флагом: присваивание Value поднимает ValueChanged, и без него открытие страницы
        // пересобирало бы тему заново на каждом заходе в настройки.
        _loadingGlass = true;
        try { GlassOpacitySlider.Value = percent; }
        finally { _loadingGlass = false; }
        GlassOpacityValue.Text = percent + " %";
    }

    private bool _loadingGlass;

    private void GlassOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingGeneral || _loadingGlass) return;
        var percent = GlassSurface.Clamp((int)Math.Round(e.NewValue));
        GlassOpacityValue.Text = percent + " %";
        _services.Cfg.SetGlassOpacity(percent);
        ThemeManager.GlassOpacity = percent;
        // Применяем тему целиком, а не подменяем кисти руками: пересчёт поверхностей живёт в одном
        // месте (ThemeManager.Apply), и вторая его копия здесь разошлась бы с первой при первой же правке.
        ThemeManager.Apply(ThemeManager.Current, ThemeManager.CurrentAccent);
    }

    private void GlassOpacityReset_Click(object sender, RoutedEventArgs e)
    {
        _services.Cfg.SetGlassOpacity(GlassSurface.Default);
        ThemeManager.GlassOpacity = GlassSurface.Default;
        ThemeManager.Apply(ThemeManager.Current, ThemeManager.CurrentAccent);
        ShowGlassOpacity();
    }

    private void ThemeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingGeneral) return;
        if (ThemeCombo.SelectedItem is not AppTheme theme) return;

        _services.Cfg.SetTheme(theme.Id);
        ThemeManager.Apply(theme.Id, ThemeManager.CurrentAccent);
        ShowGlassOpacity();
        // Образец цвета перерисовываем по той же причине, что и у тумблера: оттенок под курсором и
        // подсветка строки считаются с оглядкой на тему.
        ShowAccent(AccentPalette.NormalizeStored(_services.Cfg.Accent()));
    }

    private void DarkTheme_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingGeneral) return;
        var theme = DarkThemeCheck.IsChecked == true ? "dark" : "light";
        // Подпись рядом со слайдером показывает ТЕКУЩЕЕ состояние, как и в прежней боковой панели:
        // у переключателя без подписи не понять, «тёмная сейчас» или «включить тёмную».
        ThemeLabelText.Text = theme == "dark" ? "Тёмная тема" : "Светлая тема";
        _services.Cfg.SetTheme(theme);
        ThemeManager.Apply(theme, ThemeManager.CurrentAccent);
        // Образец цвета перерисовываем: подпись «надписи белые/тёмные» зависит от темы не напрямую,
        // но оттенок под курсором и подсветка строки считаются с оглядкой на неё.
        ShowAccent(AccentPalette.NormalizeStored(_services.Cfg.Accent()));
    }

    private void InitAccentUi()
    {
        AccentSamples.ItemsSource = AccentPalette.Samples
            .Select(x => new { x.Hex, x.Name }).ToList();
        ShowAccent(AccentPalette.NormalizeStored(_services.Cfg.Accent()));
    }

    /// <summary>Цвет выбрали мышью на спектре. Применяется сразу, как и щелчок по образцу палитры:
    /// подбор на глаз без немедленного показа результата смысла не имеет.</summary>
    private void AccentSpectrum_ColorPicked(object? sender, System.Windows.Media.Color color) =>
        ApplyAccent(AccentPalette.ToHex(color));

    private void ShowAccent(string hex)
    {
        var color = AccentPalette.Parse(hex);
        AccentHexBox.Text = AccentPalette.ToHex(color);
        // Спектр показывает то же, что и поле кода: выбрали образец или вписали код — маркеры
        // переезжают туда же. Присваивание не зациклится: пока цвет меняем мы, элемент своё
        // событие не шлёт (см. ColorSpectrumPicker).
        AccentSpectrum.SelectedColor = color;
        AccentPreview.Background = new SolidColorBrush(color);
        // Подпись рядом с образцом — не украшение: цвет надписи на кнопках подбирается сам, и человек
        // должен видеть, какой именно выбран, чтобы не гадать, почему текст вдруг стал тёмным.
        var onLight = AccentPalette.TextOn(color) == Colors.White;
        AccentPreviewText.Text = onLight ? "надписи белые" : "надписи тёмные";
    }

    private void ApplyAccent(string hex)
    {
        var color = AccentPalette.Parse(hex);
        var normalized = AccentPalette.ToHex(color);
        _services.Cfg.SetAccent(normalized);
        ThemeManager.Apply(ThemeManager.Current, normalized);
        ShowAccent(normalized);
    }

    private void AccentSample_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex }) ApplyAccent(hex);
    }

    private void AccentHexApply_Click(object sender, RoutedEventArgs e) => ApplyAccent(AccentHexBox.Text);

    private void AccentHex_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) ApplyAccent(AccentHexBox.Text);
    }

    private void AccentReset_Click(object sender, RoutedEventArgs e) => ApplyAccent(AccentPalette.DefaultHex);

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
        var dlg = new SelfCheckDialog(_services) { Owner = Window.GetWindow(this) };
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

            ServerUrlInput.Text = _services.Cfg.ServerUrl();
            ServerKeyInput.Password = _services.Cfg.ServerKey();
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
        // Служба обмена: пока адрес не задан, раздел приглушён — но остаётся заполнимым, потому что
        // именно в этот момент его и заполняют.
        ServerSection.Opacity = string.IsNullOrWhiteSpace(ServerUrlInput.Text) ? 0.6 : 1.0;
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

    private void ServerUrl_LostFocus(object sender, RoutedEventArgs e)
    {
        var url = ServerUrlInput.Text.Trim().TrimEnd('/');
        if (url == _services.Cfg.ServerUrl()) return;
        _services.Cfg.SetServerUrl(url);
        UpdateConnectionSections();
        _host.ShowStatus("Адрес службы обмена сохранён");
    }

    /// <summary>Ключ сохраняется по мере ввода, а не по потере фокуса: его вставляют из буфера и
    /// сразу жмут «Проверить связь», не уводя фокус, — и проверка уходила бы со старым ключом.</summary>
    private void ServerKey_Changed(object sender, RoutedEventArgs e)
    {
        if (_connectionTabFilling) return;
        var key = ServerKeyInput.Password.Trim();
        if (key == _services.Cfg.ServerKey()) return;
        _services.Cfg.SetServerKey(key);
    }

    private void ServerUrl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ServerUrl_LostFocus(sender, e);
    }

    /// <summary>Проверка идёт по протоколу самой службы (GET /ping с ключом), а не «отвечает ли
    /// адрес»: ответ обязан сказать, признала ли служба эту машину и что ей разрешено — иначе
    /// «проверено» означало бы только «что-то по адресу есть».</summary>
    private async void CheckServer_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SyncServerSettings(
            ServerUrlInput.Text.Trim().TrimEnd('/'),
            ServerKeyInput.Password.Trim());

        ServerCheckResultText.Text = "Проверяем связь…";
        var result = await SyncServerProbe.CheckAsync(settings);
        ServerCheckResultText.Text = result.Message;
    }

    // ── Вкладка «Печать» ──────────────────────────────────────────────────────
    // Этикетка с QR на инструкцию + папка с шаблонами наклеек. Разделение настроек здесь важнее
    // обычного: адрес диска инструкций, тексты этикетки и папка наклеек — ОБЩИЕ (уезжают на все
    // машины с конфигом, задаёт администратор), а имя принтера и размер наклейки — своё у каждой
    // машины (ConfigSyncService.SkipSettingsKeys). Поэтому поля общих настроек скрыты от наладчика с
    // программистом, а принтер, размер и сама печать доступны всем — печатает как раз наладчик.

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
            $"{layout.SizeCaption()} мм, поля {layout.MarginsCaption()} мм",
            "Если что-то срезано по краю — увеличьте поля или подвиньте макет: кнопка «QR инструкции» на карточке версии.",
            // Подпись в центре кода берётся настоящая, а не «ТЕСТ»: образец должен показывать ровно ту
            // этикетку, которая пойдёт на шкаф, вместе с подписью назначения над кодом.
            layout.EffectiveHoleText());
        var outcome = LabelPrinter.Print(label, _services.Cfg.LabelPrinter(), "Пробная этикетка", layout);
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
    /// <summary>Подключить бета-тестирование.
    ///
    /// Бета — ОТДЕЛЬНАЯ программа: ставится рядом, со своей папкой, своим ярлыком и своей базой
    /// настроек. Поэтому здесь ничего не скачивается и не запускается само — открывается страница
    /// релиза, откуда человек сам берёт установщик. Это не лень: подменять работающую программу
    /// нажатием кнопки в настройках нельзя, а установка рядом — осознанное действие, после которого
    /// рабочая версия остаётся ровно такой, какой была.
    ///
    /// Сама эта (рабочая) программа бету как обновление НЕ ВИДИТ и увидеть не может — предрелизы
    /// отсекаются тремя независимыми барьерами в AppUpdateService.</summary>
    private async void JoinBeta_Click(object sender, RoutedEventArgs e)
    {
        BetaJoinBtn.IsEnabled = false;
        BetaStatusText.Text = "Ищу бета-версию…";
        try
        {
            var beta = await AppUpdateService.FindLatestBetaAsync();
            if (beta is null)
            {
                BetaStatusText.Text = "Бета-версии сейчас нет — либо её ещё не выпускали, либо не вышло связаться с GitHub.";
                return;
            }

            var answer = AppMessageBox.Show(
                $"Бета-версия {beta.Value.Version} ставится ОТДЕЛЬНОЙ программой, рядом с этой."
                + Environment.NewLine + Environment.NewLine
                + "Рабочая версия останется на месте: свой ярлык, свои настройки, своя база. "
                + "Открыть страницу загрузки?",
                "Бета-тестирование", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes)
            {
                BetaStatusText.Text = $"Доступна бета {beta.Value.Version}.";
                return;
            }

            BetaStatusText.Text = $"Открыл страницу беты {beta.Value.Version}. Там лежит установщик "
                                + "AntarusPoFinderBeta-…-setup.msi — он ставится рядом с рабочей программой.";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(beta.Value.PageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            BetaStatusText.Text = $"Не вышло: {ex.Message}";
        }
        finally
        {
            BetaJoinBtn.IsEnabled = true;
        }
    }

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
                hierarchyRows.AddRange(subtypes.Select(s => new HierarchyRow { Group = g, Subtype = s, NoInstruction = s.NoInstruction }));
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
        LoadParamGroups(null);

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
            // Галочка обрабатывает свои клики сама. Без этой строки двойной клик по ней попадал бы в
            // default и поверх двух переключений открывал окно переименования подтипа.
            case "Инструкции не будет": break;
            default: RenameSubtype_Click(sender, e); break;
        }
    }

    /// <summary>Отметка «на этот шкаф инструкции не будет» — рациональные шкафы (см.
    /// EquipmentSubType.NoInstruction, StubKind.NotPlanned). Пишется сразу по клику: это переключатель
    /// на два положения, диалог с «Сохранить» был бы лишним шагом.
    ///
    /// ⚠️ Вместе с отметкой заводится ОДНА-НА-ВСЕХ страница в корне диска и уходит на хостинг. Иначе
    /// признак наполовину мёртв: наклейка такого шкафа ведёт именно на неё, а окно QR подставляет её
    /// только когда файл РЕАЛЬНО лежит (SearchView.NotPlannedPageFor) — нет файла, и наклейка повела бы
    /// в пустоту. Сама по себе страница появлялась лишь при обходе диска или по «Перезалить всё», то
    /// есть неизвестно когда, а галочку ставят ровно затем, чтобы напечатать наклейку.
    ///
    /// Снятие отметки страницу НЕ удаляет: на других подтипах она может быть ещё нужна, а её адрес уже
    /// напечатан на наклейках.</summary>
    private void SubtypeNoInstruction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || box.DataContext is not HierarchyRow row) return;

        if (row.Subtype?.Id is not int subtypeId)
        {
            box.IsChecked = false;
            AppMessageBox.Show("У этого типа шкафа ещё нет подтипов — отмечать нечего.",
                "Инструкции не будет", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var value = box.IsChecked == true;
        _services.Db.SetSubtypeNoInstruction(subtypeId, value);
        row.NoInstruction = value;
        row.Subtype.NoInstruction = value;

        _host.PushCatalogChange(value
            ? $"«{row.SubtypeName}»: инструкции не будет, наклейки ведут на страницу сервиса"
            : $"«{row.SubtypeName}»: отметка «инструкции не будет» снята");

        if (!value) return;

        var root = _services.Cfg.RootPath();
        var stubs = _services.StubWriter();
        var publisher = _services.Publisher();
        _ = Task.Run(() =>
        {
            var warnings = new List<string>();
            string message;
            try
            {
                var action = InstructionStub.EnsureShared(root, stubs, warnings, publisher);
                message = warnings.Count > 0
                    ? warnings[0]
                    : action == StubAction.None
                        ? "Страница «Инструкции не будет» уже на месте"
                        : "Страница «Инструкции не будет» готова — на диске и на хостинге";
            }
            catch (Exception ex)
            {
                message = $"Страницу «Инструкции не будет» подготовить не удалось: {ex.Message}";
            }
            Dispatcher.BeginInvoke(new Action(() => _host.ShowStatus(message)));
        });
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

    // ── Группы параметров ПЧ/УПП ─────────────────────────────────────────────
    //
    // Справочник разделов таблицы параметров. До этого его правила только миграция: интерфейса не
    // было вообще, и «поля в выпадающих списках» документа были неизменяемы. Вся логика — в
    // Core/Services/ParamGroupEditing.cs под тестами; здесь только показ и вопросы человеку.

    /// <summary>Перечитать список групп, сохранив выделение по НАЗВАНИЮ (а не по номеру строки):
    /// перестановка меняет как раз номер, и по нему выделение уезжало бы на соседнюю группу.</summary>
    private void LoadParamGroups(string? select)
    {
        var groups = _services.Db.GetParamGroups();
        var keep = select ?? ParamGroupList.SelectedItem as string;
        ParamGroupList.ItemsSource = groups;
        if (keep is not null)
            ParamGroupList.SelectedItem = groups.FirstOrDefault(g =>
                string.Equals(g, keep, StringComparison.OrdinalIgnoreCase));

        ParamGroupHint.Text = $"Групп: {groups.Count}. Порядок сверху вниз — порядок разделов в таблице параметров.";
    }

    /// <summary>Показать итог правки справочника: отказ — окном, удача — строкой состояния и
    /// толчком отправки конфига (справочник ездит в общем конфиге, как производители и теги).</summary>
    private bool ShowParamGroupResult(ParamGroupEditing.Result result)
    {
        if (!result.Ok)
        {
            AppMessageBox.Show(result.Message, "Группы параметров", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        _host.PushCatalogChange(result.Message);
        return true;
    }

    private void AddParamGroup_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptDialog.Prompt(Window.GetWindow(this), "Добавить группу параметров",
            "Название раздела таблицы: «Насос», «Пожарный режим»:");
        if (name is null) return;

        var result = ParamGroupEditing.Add(_services.Db, name);
        if (!ShowParamGroupResult(result)) return;
        LoadParamGroups(name.Trim());
    }

    private void RenameParamGroup_Click(object sender, RoutedEventArgs e)
    {
        if (ParamGroupList.SelectedItem is not string source)
        {
            AppMessageBox.Show("Выберите группу.", "Группы параметров", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var used = ParamGroupEditing.UsedBy(_services.Db, source);
        var hint = used == 0
            ? "Этой группой пока не помечена ни одна строка."
            : $"Этой группой помечено строк: {used}. Подпись поправится и в них — но только на этой машине: "
              + "строки уже сохранённой редакции по машинам не переписываются, и у коллег в их копиях останется прежнее имя.";

        var name = TextPromptDialog.Prompt(Window.GetWindow(this), "Переименовать группу",
            $"Новое название вместо «{source}».\n{hint}", source);
        if (name is null) return;

        var result = ParamGroupEditing.Rename(_services.Db, source, name);
        if (!ShowParamGroupResult(result)) return;
        LoadParamGroups(name.Trim());
    }

    private void MoveParamGroupUp_Click(object sender, RoutedEventArgs e) => MoveParamGroup(-1);

    private void MoveParamGroupDown_Click(object sender, RoutedEventArgs e) => MoveParamGroup(+1);

    private void MoveParamGroup(int delta)
    {
        if (ParamGroupList.SelectedItem is not string source)
        {
            AppMessageBox.Show("Выберите группу.", "Группы параметров", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = ParamGroupEditing.Move(_services.Db, source, delta);
        if (!ShowParamGroupResult(result)) return;
        LoadParamGroups(source);
    }

    private void RemoveParamGroup_Click(object sender, RoutedEventArgs e)
    {
        if (ParamGroupList.SelectedItem is not string source)
        {
            AppMessageBox.Show("Выберите группу.", "Группы параметров", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var used = ParamGroupEditing.UsedBy(_services.Db, source);
        var tail = used == 0
            ? "Ни одна строка таблиц ею не помечена."
            : $"Ею помечено строк: {used}. Подпись у них останется — группа лежит в строке текстом, а не ссылкой, "
              + "но своего места в порядке показа эти строки лишатся.";

        var reply = AppMessageBox.Show($"Убрать группу «{source}» из справочника?\n\n{tail}",
            "Группы параметров", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        var result = ParamGroupEditing.Remove(_services.Db, source);
        if (!ShowParamGroupResult(result)) return;
        LoadParamGroups(null);
    }

    private void ResetParamGroupOrder_Click(object sender, RoutedEventArgs e)
    {
        var reply = AppMessageBox.Show(
            "Вернуть заводской порядок групп?\n\nНазвания не изменятся: переставятся только места. "
            + "Свои группы останутся, но уедут в конец — перед «Сбросом до заводских».",
            "Группы параметров", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        var result = ParamGroupEditing.ResetToDefaults(_services.Db);
        if (!ShowParamGroupResult(result)) return;
        LoadParamGroups(null);
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
    /// <summary>Открытое окно перестройки. Оно больше не модальное, поэтому «нажали кнопку
    /// второй раз» перестало быть невозможным: без этой ссылки на экране оказались бы два окна
    /// с двумя планами, и второе из них — заведомо устаревшим.</summary>
    private DiskMigrationDialog? _migrationWindow;

    /// <summary>Открытое окно разбора старого диска — по той же причине, что и _migrationWindow
    /// выше: окно немодальное, а в нём лежит ручная разметка найденного, которую нельзя
    /// продублировать вторым окном.</summary>
    private LegacyImportDialog? _legacyWindow;

    private void DiskMigration_Click(object sender, RoutedEventArgs e)
    {
        // Немодально: перестройка идёт минутами, и всё это время программой надо пользоваться.
        // Второй раз её не запустить — окно живёт в одном экземпляре, а самой работе право
        // выдаёт LongOperationRegistry (см. DiskMigrationDialog).
        if (_migrationWindow is { IsLoaded: true } opened)
        {
            if (opened.WindowState == WindowState.Minimized) opened.WindowState = WindowState.Normal;
            opened.Activate();
            return;
        }
        var dlg = new DiskMigrationDialog(_services, _host) { Owner = Window.GetWindow(this) };
        _migrationWindow = dlg;
        // Перечитывать вкладку «Прошивки» надо ПОСЛЕ работы окна, а не после его открытия: имена
        // файлов у записей меняет сама перестройка. Немодальное Show() возвращает управление сразу,
        // поэтому обновление переехало в Closed — иначе список так и остался бы со старыми именами.
        dlg.Closed += (_, _) =>
        {
            _migrationWindow = null;
            LoadFirmwareTab();
        };
        dlg.Show();
    }

    /// <summary>Разбор «дофайндеровского» диска (LegacyImportDialog): обход выбранной папки, поиск
    /// прошивок и построчный перенос отмеченного в структуру Финдера. Отдельно от перестройки выше:
    /// та приводит к правилам СВОЙ диск, а эта забирает чужое накопленное, ничего в нём не меняя.
    /// После закрытия перечитываем вкладку «Прошивки» — версий могло прибавиться.</summary>
    private void LegacyImport_Click(object sender, RoutedEventArgs e)
    {
        // Немодально — по той же причине, что и перестройка диска: разбор чужого дерева и перенос
        // идут десятками минут. Второе окно не заводим: в открытом лежит ручная разметка.
        if (_legacyWindow is { IsLoaded: true } opened)
        {
            if (opened.WindowState == WindowState.Minimized) opened.WindowState = WindowState.Normal;
            opened.Activate();
            return;
        }
        var dlg = new LegacyImportDialog(_services, _host) { Owner = Window.GetWindow(this) };
        _legacyWindow = dlg;
        // Тоже ПОСЛЕ работы окна: версии, которые перенесёт разбор, до его закрытия ещё не заведены.
        dlg.Closed += (_, _) =>
        {
            _legacyWindow = null;
            LoadFirmwareTab();
        };
        dlg.Show();
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
            new[] { FwHistoryStatus.Current, FwHistoryStatus.Superseded, FwHistoryStatus.RolledBack, FwHistoryStatus.Disabled });
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
        var dlg = new EditFirmwareDialog(_services, v, title, _host) { Owner = Window.GetWindow(this) };
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
            // администраторской (см. ConfigSyncService.PushFirmwareAndModerationOnly).
            delivered = ConfigSyncService.RecordAndPushModeration(_services,
                _services.Db.GetFwVersionIdsSharingFiles(v.Id!.Value), _services.CurrentUserName);
        }

        _host.ShowStatus(release
            // Названием прошивки, а не голым номером версии: по «2.0.0042.0003» невозможно понять,
            // к чему относится сообщение (тикет коллеги — «не номер прошивки, а название её»).
            ? $"Версия выведена из модерации: {title}" + (delivered ? " (отправлено коллегам)" : "")
            : $"Теги обновлены: {title}", category: NotificationCategory.FirmwareAndParams);
        LoadModerationTab();
    }

    private void PopulateFirmwareTable(List<FwVersionRecord> data) =>
        FwGrid.ItemsSource = data.Select(v => new FwRow { Record = v, StatusLabel = LabelFor(v) }).ToList();

    /// <summary>Вычисленная метка статуса записи — ровно та, что показана в столбце «Статус» таблицы.
    /// Общая для отрисовки и для фильтра по статусу, чтобы они не разъезжались.</summary>
    private string LabelFor(FwVersionRecord v) =>
        v.Id is int id && _fwStatusLabels.TryGetValue(id, out var label)
            ? label
            : FwStatus.IsRolledBack(v.Status) ? FwHistoryStatus.RolledBack
            : FwStatus.IsDisabled(v.Status) ? FwHistoryStatus.Disabled
            : FwHistoryStatus.Current;

    /// <summary>Схлопывает «Текущая (HW n)» и «Текущая (исполнение «…»)» до «Текущая», чтобы одна
    /// опция фильтра ловила их все. Остальные метки возвращаются как есть.</summary>
    private static string StatusCategory(string label) =>
        FwHistoryStatus.IsCurrent(label) ? FwHistoryStatus.Current : label;

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

    // ── Подключения ───────────────────────────────────────────────────────

    /// <summary>Строка списка подключений. Состояние считается здесь, а не хранится: минимальная
    /// версия меняется в этом же окне, и запомненный ответ разошёлся бы с ней в ту же секунду.</summary>
    private sealed class ConnectionRow
    {
        public string MachineName { get; init; } = "";
        public string UserName { get; init; } = "";
        public string AppVersion { get; init; } = "";
        public string Channel { get; init; } = "";
        public string LastSeen { get; init; } = "";
        public string State { get; init; } = "";
        public bool Outdated { get; init; }
        public string OriginId { get; init; } = "";
    }

    private void LoadConnectionsTab()
    {
        MinVersionInput.Text = _services.Cfg.MinAppVersion();

        var minimum = _services.Cfg.MinAppVersion();
        var ownOrigin = _services.Db.UsageOriginId();
        ConnectionsGrid.ItemsSource =
            _services.Db
                .GetParticipants()
                .Select(p =>
                {
                    var trusted = Core.Services.VersionGate.IsTrusted(p.AppVersion, minimum);
                    return new ConnectionRow
                    {
                        MachineName = p.MachineName.Length > 0 ? p.MachineName : "(не представилась)",
                        UserName = p.UserName,
                        AppVersion = p.AppVersion.Length > 0 ? p.AppVersion : "неизвестна",
                        Channel = p.Channel,
                        LastSeen = p.LastSeenAt.Length >= 16 ? p.LastSeenAt[..16].Replace('T', ' ') : p.LastSeenAt,
                        Outdated = !trusted,
                        OriginId = p.OriginId,
                        State =
                            p.OriginId == ownOrigin ? "это эта машина"
                            : trusted ? "в обмене"
                            : "правки не принимаются — нужна " + minimum,
                    };
                })
                .ToList();
    }

    private void RefreshConnections_Click(object sender, RoutedEventArgs e) => LoadConnectionsTab();

    private void SaveMinVersion_Click(object sender, RoutedEventArgs e)
    {
        var text = MinVersionInput.Text.Trim();
        if (text.Length > 0 && Core.Services.VersionGate.Parse(text) is null)
        {
            AppMessageBox.Show("Версия пишется числами через точку: 1.75.9 или 2.0.2.1.",
                "Минимальная версия", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Запретить самому себе участвовать в обмене — это способ остаться без синхронизации и не
        // понять почему. Проверка стоит до записи, потому что настройка уезжает всем сразу.
        if (text.Length > 0 && !Core.Services.VersionGate.IsTrusted(AppUpdateService.CurrentVersionText, text))
        {
            AppMessageBox.Show(
                "Эта версия сама ниже указанной — после сохранения обмен отрезало бы и у вас." + Environment.NewLine +
                "Сначала обновитесь, потом поднимайте планку.",
                "Минимальная версия", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _services.Cfg.SetMinAppVersion(text);
        _host.PushCatalogChange(text.Length > 0 ? $"Минимальная версия для обмена: {text}" : "Ограничение по версии снято",
            "min_app_version", NotificationCategory.Sync);
        _host.ShowStatus(text.Length > 0
                ? $"Минимальная версия: {text}. Машины ниже неё в обмене не участвуют."
                : "Ограничение по версии снято.",
            category: NotificationCategory.Sync);
        LoadConnectionsTab();
    }

    private void ClearMinVersion_Click(object sender, RoutedEventArgs e)
    {
        MinVersionInput.Text = "";
        SaveMinVersion_Click(sender, e);
    }

    private void ForgetConnection_Click(object sender, RoutedEventArgs e)
    {
        if (ConnectionsGrid.SelectedItem is not ConnectionRow row)
        {
            AppMessageBox.Show("Выберите машину в списке.", "Подключения", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _services.Db.ForgetParticipant(row.OriginId);
        LoadConnectionsTab();
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
        // ⚠️ ВЫКЛЮЧЕНИЕ ВЕРСИЙ — ТОЛЬКО В БЕТЕ (тот же довод, что и в FirmwareCard): в рабочей
        // версии у прошивки по-прежнему есть лишь откат. Видимость ставится ДО проверки выбора:
        // без выбранной строки метод выходит раньше, и кнопки оставались на экране.
        // Роль здесь на тех же правах, что и у кнопок отката (UpdateRollbackAccess): правка
        // состояния прошивки — дело администратора.
        var allowed = Core.Domain.AppIdentity.IsBeta && _services.Cfg.CurrentRole() == "administrator";
        DisableFirmwareBtn.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        KeepOnlyFirmwareBtn.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;

        var v = FwGrid.SelectedItem is FwRow row ? row.Record : null;
        if (v is null)
        {
            SetCurrentFirmwareBtn.IsEnabled = false;
            RollbackFirmwareBtn.IsEnabled = false;
            UnrollbackFirmwareBtn.IsEnabled = false;
            DisableFirmwareBtn.IsEnabled = false;
            DisableFirmwareBtn.Content = "Выключить";
            KeepOnlyFirmwareBtn.IsEnabled = false;
            return;
        }

        var isRolledBack = FwStatus.IsRolledBack(v.Status);
        var isDisabled = FwStatus.IsDisabled(v.Status);
        var label = v.Id is int id && _fwStatusLabels.TryGetValue(id, out var l) ? l : "";
        // Любая разновидность «Текущей» — и общая, и по HW, и по исполнению (FwHistoryStatus.IsCurrent):
        // версия, уже актуальная в своей группе, повторной отметки не требует.
        var isCurrent = FwHistoryStatus.IsCurrent(label);

        RollbackFirmwareBtn.IsEnabled = !isRolledBack;
        UnrollbackFirmwareBtn.IsEnabled = isRolledBack;
        SetCurrentFirmwareBtn.IsEnabled = !isRolledBack && !isDisabled && !isCurrent;

        // Выключение и откат — разные вещи (см. FwStatus), поэтому у откатанной версии кнопка
        // выключения недоступна: её сначала возвращают в активные, а уже потом решают, выдавать её
        // или нет. Подпись переключается, чтобы одна кнопка работала в обе стороны.
        DisableFirmwareBtn.IsEnabled = !isRolledBack;
        DisableFirmwareBtn.Content = isDisabled ? "Включить" : "Выключить";
        KeepOnlyFirmwareBtn.IsEnabled = !isRolledBack;
    }

    private void EditFirmware_Click(object sender, RoutedEventArgs e)
    {
        var v = GetSelectedFwVersion();
        if (v is null) return;
        var title = $"{v.GroupName} {v.SubtypeName} {v.CtrlName} {v.VersionRaw}";
        var dlg = new EditFirmwareDialog(_services, v, title, _host) { Owner = Window.GetWindow(this) };
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

        // Пока в папку версии пишет долгая операция (сборка LFS, заливка), менять каталог
        // нельзя: на диске остался бы файл, которому в базе больше ничего не отвечает.
        if (BusySubjectGuard.Blocked(_services, v.DiskPath, "Откат версии")) return;

        var reply = AppMessageBox.Show(
            $"Откатить версию {v.VersionRaw}?\n\nЗапись в базе будет помечена как откатанная.\nСледующая загрузка получит тот же SW-номер заново.\nФайлы на диске останутся нетронутыми.",
            "Откат версии", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        _services.Db.RollbackFwVersion(v.Id!.Value);
        // Правка состояния прошивки — такая же неотправленная правка, как правка справочника: без
        // регистрации в накопителе плашка «изменений не отправлено» молчит, и у коллег версия
        // остаётся прежней (см. UploadView, случай Success).
        _host.PushCatalogChange($"Откатана версия {v.VersionRaw}",
            v.Id?.ToString() ?? "", NotificationCategory.FirmwareAndParams);
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
        if (FwHistoryStatus.IsCurrent(currentLabel))
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
        _host.PushCatalogChange($"Отмечена текущей версия {v.VersionRaw}",
            v.Id?.ToString() ?? "", NotificationCategory.FirmwareAndParams);
        LoadFirmwareTab();
    }

    /// <summary>Выключить версию или включить её обратно — одна кнопка в обе стороны (см.
    /// FwStatus: «во-вторых отключение, опять же залили не то случайно раздвоили прошивку»).
    /// В отличие от отката SW-номер остаётся занятым, папка на диске не переименовывается, и
    /// выключать можно любую версию по отдельности, не трогая соседние по цепочке.</summary>
    private void DisableFirmware_Click(object sender, RoutedEventArgs e)
    {
        var v = GetSelectedFwVersion();
        if (v is null) return;
        if (FwStatus.IsRolledBack(v.Status))
        {
            AppMessageBox.Show("Версия откатана. Сначала верните её в активные, потом решайте, выдавать её или нет.",
                "Выключение версии", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (FwStatus.IsDisabled(v.Status))
        {
            if (!_services.Db.EnableFwVersion(v.Id!.Value)) return;
            _host.PushCatalogChange($"Включена версия {v.VersionRaw}",
                v.Id?.ToString() ?? "", NotificationCategory.FirmwareAndParams);
            _host.InvalidateSearchResults();
            _host.ShowStatus($"Версия включена: {v.VersionRaw}", category: NotificationCategory.FirmwareAndParams);
            LoadFirmwareTab();
            return;
        }

        var reply = AppMessageBox.Show(
            $"Выключить версию {v.VersionRaw}?\n\n" +
            "Версия перестанет показываться в поиске и выдаче, но останется в истории.\n" +
            "SW-номер за ней сохраняется, файлы и папка на диске не трогаются.\n" +
            "Включить обратно можно этой же кнопкой.",
            "Выключение версии", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        if (!_services.Db.DisableFwVersion(v.Id!.Value)) return;
        _host.PushCatalogChange($"Выключена версия {v.VersionRaw}",
            v.Id?.ToString() ?? "", NotificationCategory.FirmwareAndParams);
        _host.InvalidateSearchResults();
        _host.ShowStatus($"Версия выключена: {v.VersionRaw}", category: NotificationCategory.FirmwareAndParams);
        LoadFirmwareTab();
    }

    /// <summary>«Оставить только эту» — третий случай из заказанных: «намеренно сделали 3 копии
    /// прошивки под разные исполнения и их можно включать и выключать… включить новую версию с
    /// каким-то исполнением и оставить только её». Включает выбранную и выключает все остальные
    /// живые версии того же шкафа; откатанные не трогает (см. Database.KeepOnlyFwVersionActive).</summary>
    private void KeepOnlyFirmware_Click(object sender, RoutedEventArgs e)
    {
        var v = GetSelectedFwVersion();
        if (v is null) return;
        if (FwStatus.IsRolledBack(v.Status))
        {
            AppMessageBox.Show("Версия откатана — сначала верните её в активные.",
                "Оставить только эту", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var reply = AppMessageBox.Show(
            $"Оставить в работе только версию {v.VersionRaw}?\n\n" +
            $"Все остальные версии шкафа «{v.SubtypeName} / {v.CtrlName}» будут выключены — включая копии " +
            "под другие исполнения и под другое железо.\nОткатанные версии не затрагиваются.\n" +
            "Любую из выключенных можно вернуть кнопкой «Включить».",
            "Оставить только эту", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        var disabled = _services.Db.KeepOnlyFwVersionActive(v.Id!.Value);
        if (disabled < 0) return;
        _host.PushCatalogChange($"Оставлена в работе версия {v.VersionRaw}",
            v.Id?.ToString() ?? "", NotificationCategory.FirmwareAndParams);
        _host.InvalidateSearchResults();
        _host.ShowStatus(disabled == 0
                ? $"Версия {v.VersionRaw} и так была единственной выдаваемой"
                : $"Оставлена только {v.VersionRaw}, выключено версий: {disabled}",
            category: NotificationCategory.FirmwareAndParams);
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
        _host.PushCatalogChange($"Возвращена в активные версия {v.VersionRaw}",
            v.Id?.ToString() ?? "", NotificationCategory.FirmwareAndParams);
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

        // Пока в папку версии пишет долгая операция (сборка LFS, заливка), менять каталог
        // нельзя: на диске остался бы файл, которому в базе больше ничего не отвечает.
        if (BusySubjectGuard.Blocked(_services, v.DiskPath, "Удаление прошивки")) return;

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
        // коллег с любой машины: узкий канал (ConfigSyncService.PushFirmwareAndModerationOnly) дописывает
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

    // ═════════════════════════ ПОЧТА (БЕТА) ════════════════════════

    /// <summary>МОЯ ПОЧТА и КАТЕГОРИИ — на одной вкладке с почтой.
    ///
    /// Две жалобы Ильи 16.10.2026 разом: «сделай настройку уведомлений совмещенно с почтой как-то а
    /// то не очень удобно настраивать» и «наладчик который заведен не может настроить себе рассылку
    /// и опять весь спам получает». Раньше категории жили в окне истории уведомлений, а почта — в
    /// настройках, и для одной задачи приходилось ходить в два места; настроить же себе рассылку
    /// наладчик не мог вовсе — вкладка была администраторской.</summary>
    private void LoadMyEmailSection()
    {
        MyEmailInput.Text = _services.Cfg.MyEmail();

        var mine = _services.Cfg.MyEmailCategories();
        MyEmailCategoriesPanel.Children.Clear();
        foreach (var (category, label) in NotificationCategoryInfo.All)
        {
            var id = category.ToString();
            var box = new CheckBox
            {
                Content = label,
                Margin = new Thickness(0, 0, 0, 4),
                // Пусто — «всё подряд»: при первом заходе отмечено всё, иначе человек увидел бы
                // пустой список и решил, что ему ничего не приходит.
                IsChecked = mine.Count == 0 || mine.Any(c => string.Equals(c, id, StringComparison.OrdinalIgnoreCase)),
            };
            box.Checked += (_, _) => SaveMyEmailCategories();
            box.Unchecked += (_, _) => SaveMyEmailCategories();
            box.Tag = id;
            MyEmailCategoriesPanel.Children.Add(box);
        }

        RefreshMyEmailHint();
    }

    private void SaveMyEmailCategories()
    {
        if (_loadingGeneral) return;

        var picked = MyEmailCategoriesPanel.Children.OfType<CheckBox>()
            .Where(b => b.IsChecked == true)
            .Select(b => (string)b.Tag!)
            .ToList();

        // Отмечено всё — значит ограничений нет: храним пусто, чтобы новая категория в будущей
        // версии приходила сама, а не молча оказалась «не отмеченной».
        var all = NotificationCategoryInfo.All.Length;
        _services.Cfg.SetMyEmailCategories(picked.Count == all ? Array.Empty<string>() : picked);
        RefreshMyEmailHint();
    }

    private void MyEmail_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loadingGeneral) return;
        _services.Cfg.SetMyEmail(MyEmailInput.Text);
        RefreshMyEmailHint();
    }

    private void RefreshMyEmailHint()
    {
        var mine = _services.Cfg.MyEmail();
        if (mine.Length == 0)
        {
            MyEmailHint.Text = "Адрес не указан — письма приходят по общему списку без ограничений.";
            return;
        }

        var inRules = EmailRouting.ToSubscriptions(_services.Db.GetEmailRules())
            .Any(sub => string.Equals(sub.Recipient, mine, StringComparison.OrdinalIgnoreCase));

        // Честно говорим, если адреса нет в общем списке: личная настройка рассылку не РАСШИРЯЕТ,
        // и человек иначе ждал бы писем, которых никто ему не назначал.
        MyEmailHint.Text = inRules
            ? "Адрес есть в общем списке — отмеченное выше и будет приходить."
            : "Этого адреса нет в общем списке рассылки, поэтому письма на него не идут. "
              + "Попросите администратора добавить адрес — ограничения отсюда применятся сразу.";
    }

    /// <summary>Категории уведомлений — те же, что были в окне истории. Настройка машинная и к почте
    /// отношения не имеет, но стоит рядом намеренно: человек приходит сюда с вопросом «что мне
    /// приходит», а не «где хранится эта галка».</summary>
    private void LoadNotificationCategories()
    {
        NotificationCategoriesPanel.Children.Clear();
        foreach (var (category, label) in NotificationCategoryInfo.All)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });

            var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 0);

            var shown = new CheckBox
            {
                Content = "Показывать",
                IsChecked = _services.Cfg.IsNotificationCategoryEnabled(category),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var counted = new CheckBox
            {
                Content = "Непрочитанным",
                IsChecked = _services.Cfg.IsNotificationCategoryCountedUnread(category),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var captured = category;
            shown.Checked += (_, _) => _services.Cfg.SetNotificationCategoryEnabled(captured, true);
            shown.Unchecked += (_, _) => _services.Cfg.SetNotificationCategoryEnabled(captured, false);
            counted.Checked += (_, _) => _services.Cfg.SetNotificationCategoryCountedUnread(captured, true);
            counted.Unchecked += (_, _) => _services.Cfg.SetNotificationCategoryCountedUnread(captured, false);
            Grid.SetColumn(shown, 1);
            Grid.SetColumn(counted, 2);

            row.Children.Add(name);
            row.Children.Add(shown);
            row.Children.Add(counted);
            NotificationCategoriesPanel.Children.Add(row);
        }
    }

    private void LoadEmailTab()
    {
        LoadMyEmailSection();
        LoadNotificationCategories();

        // Списка категорий здесь больше нет: что присылать на адрес, отмечается галочками в окне
        // рассылки (EmailSubscriptionDialog) — выпадающий список позволял выбрать ровно одно.

        EmailEnabledCheck.IsChecked = _services.Cfg.EmailEnabled();
        SmtpHostInput.Text = _services.Cfg.SmtpHost();
        SmtpPortInput.Text = _services.Cfg.SmtpPort().ToString();
        SmtpSslCheck.IsChecked = _services.Cfg.SmtpSsl();
        SmtpUserInput.Text = _services.Cfg.SmtpUser();
        SmtpFromInput.Text = _services.Cfg.SmtpFrom();
        // Пароль обратно В ПОЛЕ НЕ подставляется — и не потому, что его нельзя прочитать, а чтобы
        // сохранение без правки пароля не затирало его тем, что показалось в поле. Пустое поле при
        // сохранении означает «оставить как было» — см. SaveSmtp_Click.
        SmtpPasswordInput.Password = "";
        SmtpStatusText.Text = _services.Cfg.SmtpPassword().Length > 0 ? "Пароль сохранён." : "";

        LoadEmailSchedule();
        RenderEmailRules();
        RefreshEmailReadiness();
    }

    // ── Когда и что слать ─────────────────────────────────────────────
    //
    // Жалоба Ильи: «более обширная настройка — что, как, когда, время и т.п. отправки по почте».
    // Вся арифметика («пора или ещё нет», переход тихих часов через полночь) живёт в
    // Core.Domain.EmailSchedule и проверена тестами; здесь только поля и их сохранение.

    /// <summary>Пока идёт заполнение полей, обработчик переключателей обязан молчать. Без этого
    /// флага возврат на вкладку «Почта» (страница живёт в кэше, LoadEmailTab зовётся снова при
    /// уже IsLoaded == true) запускал бы сохранение ПОСРЕДИ заполнения: режим уже выставлен, а поля
    /// времени и флажки категорий — ещё старые/пустые, и в конфиг уехал бы пустой список категорий.
    /// То есть простой заход на вкладку молча сбрасывал бы настройку.</summary>
    private bool _loadingEmailSchedule;

    private void LoadEmailSchedule()
    {
        _loadingEmailSchedule = true;
        try { FillEmailScheduleFields(); }
        finally { _loadingEmailSchedule = false; }

        DescribeEmailSchedule();
    }

    /// <summary>Флажки категорий строятся кодом по тому же списку, что и выпадашка правил
    /// (NotificationCategoryInfo.All): два списка в XAML разъехались бы при первой же новой
    /// категории, и разъехались бы молча.</summary>
    private void FillEmailScheduleFields()
    {
        var s = _services.Cfg.EmailDelivery();

        EmailModeImmediate.IsChecked = s.Mode == EmailDeliveryMode.Immediate;
        EmailModeEveryN.IsChecked = s.Mode == EmailDeliveryMode.EveryNHours;
        EmailModeDaily.IsChecked = s.Mode == EmailDeliveryMode.DailyAtTime;
        EmailDigestHoursInput.Text = s.DigestHours.ToString();
        EmailDailyAtInput.Text = EmailSchedule.FormatTime(s.DailyAt);
        EmailQuietCheck.IsChecked = s.QuietHoursEnabled;
        EmailQuietFromInput.Text = EmailSchedule.FormatTime(s.QuietFrom);
        EmailQuietToInput.Text = EmailSchedule.FormatTime(s.QuietTo);

        if (EmailCategoriesPanel.Children.Count == 0)
            foreach (var (category, label) in NotificationCategoryInfo.All)
            {
                var box = new CheckBox
                {
                    Content = label,
                    Tag = category.ToString(),
                    Margin = new Thickness(0, 0, 0, 4),
                };
                // Галочка сохраняется сразу, как и остальные переключатели: ждать кнопки от флажка
                // человек не станет — он уже «сделал выбор» и ушёл со страницы.
                box.Checked += EmailSchedule_Changed;
                box.Unchecked += EmailSchedule_Changed;
                EmailCategoriesPanel.Children.Add(box);
            }

        var chosen = s.AllowedCategories;
        foreach (var box in EmailCategoriesPanel.Children.OfType<CheckBox>())
            box.IsChecked = chosen.Contains((string)box.Tag!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Переключили режим или тихие часы — сохраняем сразу, как и галочка «дублировать».
    /// Кнопка «Сохранить» рядом нужна полям ВВОДА (часы, время): их человек дописывает по символу,
    /// и сохранять «2» из недопечатанного «22:00» нельзя.</summary>
    private void EmailSchedule_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _loadingEmailSchedule) return;
        SaveEmailSchedule_Click(sender, e);
    }

    private void SaveEmailSchedule_Click(object sender, RoutedEventArgs e)
    {
        var mode = EmailModeDaily.IsChecked == true ? EmailDeliveryMode.DailyAtTime
            : EmailModeEveryN.IsChecked == true ? EmailDeliveryMode.EveryNHours
            : EmailDeliveryMode.Immediate;
        _services.Cfg.SetEmailMode(mode);

        var complaints = new List<string>();

        if (int.TryParse(EmailDigestHoursInput.Text.Trim(), out var hours) && hours > 0)
            _services.Cfg.SetEmailDigestHours(hours);
        else if (EmailDigestHoursInput.Text.Trim().Length > 0)
            complaints.Add("шаг сводки — целое число часов");

        // Время, которое не разобралось, НЕ сохраняется и о нём говорится вслух. Молча подставить
        // «09:00» вместо непонятого «9 утра» значит получить письма не тогда, когда просили, и
        // никакого следа почему.
        if (EmailSchedule.ParseTime(EmailDailyAtInput.Text) is { } daily) _services.Cfg.SetEmailDailyAt(daily);
        else complaints.Add("время суточной сводки — в виде 09:00");

        _services.Cfg.SetEmailQuietEnabled(EmailQuietCheck.IsChecked == true);
        if (EmailSchedule.ParseTime(EmailQuietFromInput.Text) is { } from) _services.Cfg.SetEmailQuietFrom(from);
        else complaints.Add("начало тихих часов — в виде 22:00");
        if (EmailSchedule.ParseTime(EmailQuietToInput.Text) is { } to) _services.Cfg.SetEmailQuietTo(to);
        else complaints.Add("конец тихих часов — в виде 08:00");

        _services.Cfg.SetEmailCategories(EmailCategoriesPanel.Children.OfType<CheckBox>()
            .Where(b => b.IsChecked == true).Select(b => (string)b.Tag!));

        DescribeEmailSchedule(complaints);
    }

    /// <summary>Одной строкой — что из этого следует на практике. Настройка расписания проверяется
    /// только ожиданием, поэтому сказать словами, когда ждать письма, здесь важнее обычного.</summary>
    private void DescribeEmailSchedule(List<string>? complaints = null)
    {
        var s = _services.Cfg.EmailDelivery();
        var parts = new List<string>
        {
            s.Mode switch
            {
                EmailDeliveryMode.EveryNHours => $"Сводка раз в {s.DigestHours} ч",
                EmailDeliveryMode.DailyAtTime => $"Сводка раз в сутки в {EmailSchedule.FormatTime(s.DailyAt)}",
                _ => "Каждое уведомление уходит сразу",
            },
        };
        if (s.QuietHoursEnabled)
            parts.Add($"тихо с {EmailSchedule.FormatTime(s.QuietFrom)} по {EmailSchedule.FormatTime(s.QuietTo)}");
        parts.Add(s.AllowedCategories.Count == 0
            ? "уходят уведомления всех видов"
            : $"уходят только выбранные виды ({s.AllowedCategories.Count})");

        if (s.Mode != EmailDeliveryMode.Immediate)
            parts.Add($"ближайшая отправка — не раньше {EmailSchedule.NextDue(s, DateTime.Now, DateTime.Now):dd.MM HH:mm}");

        var text = string.Join(", ", parts) + ".";
        if (complaints is { Count: > 0 })
            text += " НЕ сохранено: " + string.Join("; ", complaints) + ".";

        EmailScheduleStatusText.Text = text;
        EmailScheduleStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
            complaints is { Count: > 0 } ? "WarningBrush" : "TextMutedBrush");
    }

    // ── Реквизиты почты файлом ────────────────────────────────────────
    //
    // Второй путь к тому же самому, а не замена полям: просьба была «подключать как конфигом,
    // так и вручную». Файл только заполняет поля — сохранение всё равно идёт через общий путь,
    // чтобы не завестись второй способ записывать настройки, который потом разойдётся с первым.

    private void MailSecrets_Click(object sender, MouseButtonEventArgs e) => PickMailSecretsFile();

    private void MailSecrets_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void MailSecrets_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            ApplyMailSecretsFile(paths[0]);
    }

    private void PickMailSecretsFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбрать файл с реквизитами почты",
            // «Все файлы» первым фильтром — как и у ключей хранилища: имя и расширение задаём не мы.
            Filter = "Все файлы (*.*)|*.*|Файлы настроек (*.txt;*.json;*.env;*.ini;*.cfg)|*.txt;*.json;*.env;*.ini;*.cfg",
        };
        if (dlg.ShowDialog() == true) ApplyMailSecretsFile(dlg.FileName);
    }

    /// <summary>Читает файл и раскладывает его по полям. Всё, что может пойти не так (папку перетащили,
    /// файл занят, внутри не то), заканчивается понятной строкой в состоянии, а не исключением:
    /// эту настройку делает не программист.
    ///
    /// В поля кладётся только то, что В ФАЙЛЕ ДЕЙСТВИТЕЛЬНО ЕСТЬ. Половинчатый файл не должен
    /// вычищать уже заполненное: человек добавляет настройки, а не начинает с нуля.</summary>
    private void ApplyMailSecretsFile(string path)
    {
        string content;
        try
        {
            if (Directory.Exists(path))
            {
                SmtpStatusText.Text = "Это папка. Нужен сам файл с настройками почты.";
                return;
            }
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                SmtpStatusText.Text = "Файл не найден.";
                return;
            }
            if (info.Length > MailSecretsFile.MaxReasonableBytes)
            {
                SmtpStatusText.Text = "Файл слишком большой для настроек почты — похоже, это не он.";
                return;
            }
            content = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            SmtpStatusText.Text = $"Не удалось прочитать файл: {ex.Message}";
            return;
        }

        var parsed = MailSecretsFile.Parse(content);
        if (!parsed.Ok)
        {
            SmtpStatusText.Text = parsed.Error!;
            return;
        }

        var filled = new List<string>();
        SmtpHostInput.Text = parsed.Host;
        filled.Add("сервер");
        if (parsed.Port > 0) { SmtpPortInput.Text = parsed.Port.ToString(); filled.Add("порт"); }
        if (parsed.UseSsl is { } ssl) { SmtpSslCheck.IsChecked = ssl; filled.Add("SSL"); }
        if (parsed.User.Length > 0) { SmtpUserInput.Text = parsed.User; filled.Add("логин"); }
        if (parsed.From.Length > 0) { SmtpFromInput.Text = parsed.From; filled.Add("«от кого»"); }
        if (parsed.Password.Length > 0) { SmtpPasswordInput.Password = parsed.Password; filled.Add("пароль"); }

        // Сохраняем сразу и тем же путём, что и кнопка «Сохранить»: иначе человек, перетащивший
        // файл и ушедший с вкладки, остался бы без настроек и с полной уверенностью, что всё готово.
        SaveSmtp_Click(this, new RoutedEventArgs());

        SmtpStatusText.Text = $"Из файла взято: {string.Join(", ", filled)}. Сохранено.";
    }

    /// <summary>Одной строкой — уйдёт ли письмо на самом деле. См. EmailReadiness.</summary>
    private void RefreshEmailReadiness()
    {
        var rules = _services.Db.GetEmailRules();
        var smtp = _services.Cfg.Smtp();
        EmailReadyText.Text = EmailReadiness.Describe(smtp, rules);
        EmailReadyText.Foreground = (System.Windows.Media.Brush)FindResource(
            EmailReadiness.Ready(smtp, rules) ? "SuccessBrush" : "WarningBrush");
    }

    /// <summary>Опрос почтового сервера — то же, что делают руками через консоль: открыть соединение,
    /// прочитать баннер, сказать EHLO и посмотреть, что сервер умеет.
    ///
    /// Перебирается вписанный адрес И обычные имена по домену «От кого» — см.
    /// SmtpProbe.BuildCandidates. Ровно на этом была жалоба: «нажимаю определить сервер, он находил,
    /// но автоматически не подставляет». Вписанный по памяти неверный адрес останавливал поиск на
    /// себе, и кнопка не подставляла НИЧЕГО.
    ///
    /// Пароль здесь НЕ нужен и письмо НЕ отправляется: иначе непонятно, в чём беда — в адресе,
    /// в порте или в пароле.</summary>
    private async void DetectSmtp_Click(object sender, RoutedEventArgs e)
    {
        var typed = SmtpHostInput.Text.Trim();
        var candidates = SmtpProbe.BuildCandidates(typed, SmtpFromInput.Text.Trim());

        if (candidates.Count == 0)
        {
            SmtpStatusText.Text = "Укажите адрес сервера или адрес «От кого» — по домену из него сервер можно найти самому.";
            return;
        }

        DetectSmtpBtn.IsEnabled = false;
        SmtpStatusText.Text = $"Опрашиваем {string.Join(", ", candidates)}…";

        var found = await Task.Run(() =>
        {
            var last = SmtpProbe.Probe("", 0);
            foreach (var host in candidates)
            {
                last = SmtpProbe.ProbeCommonPorts(host, 4000);
                if (last.Ok) return last;
            }
            return last;
        });

        DetectSmtpBtn.IsEnabled = true;

        if (!found.Ok)
        {
            SmtpStatusText.Text = found.Error!;
            return;
        }

        var replacedTyped = typed.Length > 0 && !string.Equals(typed, found.Host, StringComparison.OrdinalIgnoreCase);

        SmtpHostInput.Text = found.Host;
        SmtpPortInput.Text = found.Port.ToString();
        // Порт 465 — это TLS с первой секунды, 587/25 — STARTTLS поверх открытого соединения. SmtpClient
        // в обоих случаях хочет EnableSsl, если сервер шифрование умеет.
        SmtpSslCheck.IsChecked = found.StartTls || found.Port == 465;

        // Логин подставляем только в ПУСТОЕ поле и только когда сервер прямо сказал, что вход
        // нужен: у большинства серверов логин и есть адрес «От кого», и человек, которому не
        // объяснили, что требуется пароль, уходит с настройкой, которая не отправит ни одного
        // письма. Затирать уже вписанный логин догадкой нельзя — он мог отличаться от адреса
        // (DOMAIN\user на Exchange).
        var filledUser = false;
        if (found.NeedsAuth && SmtpUserInput.Text.Trim().Length == 0 && SmtpFromInput.Text.Trim().Length > 0)
        {
            SmtpUserInput.Text = SmtpFromInput.Text.Trim();
            filledUser = true;
        }

        SaveSmtp_Click(this, new RoutedEventArgs());

        var parts = new List<string>();
        if (replacedTyped)
            // Молча подменить вписанный человеком адрес нельзя: он потом будет искать, почему в поле
            // не то, что он набирал, и решит, что программа «сама переписывает настройки».
            parts.Add($"Вписанный {typed} не ответил — подставлен {found.Host}, порт {found.Port}");
        else
            parts.Add($"Нашёлся: {found.Host}, порт {found.Port}");
        parts.Add(SmtpSslCheck.IsChecked == true ? "SSL включён" : "SSL выключен");
        if (found.Banner.Length > 0) parts.Add($"сервер представился: {found.Banner}");
        if (found.NeedsAuth)
        {
            parts.Add(filledUser
                ? $"сервер ТРЕБУЕТ логин и пароль — логин подставлен ({SmtpUserInput.Text}), пароль впишите сами"
                : "сервер ТРЕБУЕТ логин и пароль");
        }
        else
        {
            parts.Add("вход не требуется");
        }
        if (found.CertificateSubject.Length > 0) parts.Add($"сертификат: {found.CertificateSubject}");
        parts.Add("всё подставленное сохранено");
        SmtpStatusText.Text = string.Join(". ", parts) + ".";

        RefreshEmailReadiness();
    }

    private void EmailEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _services.Cfg.SetEmailEnabled(EmailEnabledCheck.IsChecked == true);
        RefreshEmailReadiness();
    }

    private void SaveSmtp_Click(object sender, RoutedEventArgs e)
    {
        _services.Cfg.SetSmtpHost(SmtpHostInput.Text);
        if (int.TryParse(SmtpPortInput.Text.Trim(), out var port) && port > 0)
            _services.Cfg.SetSmtpPort(port);
        _services.Cfg.SetSmtpSsl(SmtpSslCheck.IsChecked == true);
        _services.Cfg.SetSmtpUser(SmtpUserInput.Text);
        _services.Cfg.SetSmtpFrom(SmtpFromInput.Text);
        // Пустое поле — «не трогать», а не «стереть»: иначе правка порта молча роняла бы почту
        // целиком. Чтобы убрать пароль, очищают логин.
        if (SmtpPasswordInput.Password.Length > 0)
            _services.Cfg.SetSmtpPassword(SmtpPasswordInput.Password);
        else if (SmtpUserInput.Text.Trim().Length == 0)
            _services.Cfg.SetSmtpPassword("");

        SmtpPasswordInput.Password = "";
        SmtpStatusText.Text = "Сохранено.";
    }

    private void TestEmailToInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TestSmtp_Click(sender, e);
    }

    /// <summary>Пробное письмо — себе или по вписанному адресу. Жалоба Ильи: «не могу тестовое письмо
    /// отправить конкретному email». Пустое поле оставлено прежним поведением (письмо на «От кого»):
    /// привычная кнопка не должна вдруг начать требовать ввода.
    ///
    /// Здесь ошибка почты говорится вслух целиком; в бою о ней сообщается один раз на переходе в
    /// «не работает» (см. NotificationCenter.ReportMailResult), а не на каждое событие.</summary>
    private async void TestSmtp_Click(object sender, RoutedEventArgs e)
    {
        SaveSmtp_Click(sender, e);

        // Галочка «дублировать» здесь принудительно включена: проверка связи с сервером не должна
        // зависеть от того, включено ли дублирование, — его как раз и настраивают после проверки.
        var smtp = _services.Cfg.Smtp() with { Enabled = true };

        var target = EmailTestTarget.Resolve(TestEmailToInput.Text, smtp.From);
        if (!target.Ok)
        {
            SmtpStatusText.Text = target.Error;
            return;
        }
        var to = target.Address;

        SmtpStatusText.Text = $"Отправляем на {to}…";
        var error = await Task.Run(() => EmailNotifier.Send(smtp, new[] { to },
            "Antarus ПО Finder: проверка почты",
            $"Это пробное письмо из настроек Antarus ПО Finder, отправлено на {to}. "
            + "Если оно пришло — почтовый сервер настроен верно."));

        if (error.Length > 0)
        {
            SmtpStatusText.Text = $"Письмо на {to} НЕ отправлено. {error}";
            return;
        }

        // Главное здесь — НЕ сказать «всё работает». Пробное письмо идёт в ОБХОД и галочки
        // дублирования, и списка получателей — оно проверяет только связь с сервером. Именно на этом
        // и обожглись: «тестовое письмо дошло, опубликовал прошивку — и письмо не отправилось».
        var rules = _services.Db.GetEmailRules();
        var problems = EmailReadiness.Problems(_services.Cfg.Smtp(), rules);
        var where = target.ToSelf ? $"на {to} (адрес «От кого»)" : $"на {to}";
        SmtpStatusText.Text = problems.Count == 0
            ? $"Письмо отправлено {where}. Дублирование настроено и работает."
            : $"Письмо отправлено {where} — значит, сервер доступен. НО уведомления по-прежнему НЕ будут дублироваться: "
              + string.Join("; ", problems) + ".";

        RefreshEmailReadiness();
    }


    /// <summary>Список рассылок — ПО СТРОКЕ НА АДРЕС, а не по строке на «категория+адрес».
    ///
    /// Жалобы Ильи 15.10.2026: «надо сделать чтобы выбор что получать на почту не выпадающим списком
    /// было где можно что-то одно выбрать, а что-то конкретное» и «нужно добавить для почт что
    /// заведены редакт рассылки что получать и почты в целом».
    ///
    /// В хранилище правила так и остаются по одной категории на строку (они ездят в общем конфиге, и
    /// менять формат ради экрана нельзя) — свернуть их в подписки умеет EmailRouting.</summary>
    private void RenderEmailRules()
    {
        EmailRulesPanel.Children.Clear();
        var subscriptions = EmailRouting.ToSubscriptions(_services.Db.GetEmailRules());
        EmailRulesEmptyText.Visibility = subscriptions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var sub in subscriptions)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var who = new TextBlock { Text = sub.Recipient, VerticalAlignment = VerticalAlignment.Center };
            // Группа AD подписана явно: строка без собачки легко читается как опечатка, а она
            // намеренная — и раскрывает её почтовый сервер, а не мы.
            if (!EmailRouting.LooksLikeMailbox(sub.Recipient)) who.Text += "  — группа AD";

            var what = new TextBlock
            {
                Text = sub.Everything
                    ? "всё подряд"
                    : string.Join(", ", sub.Categories.Select(LabelForCategoryId)),
                Style = (Style)FindResource("MutedText"),
                TextWrapping = TextWrapping.Wrap,
            };

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(who);
            text.Children.Add(what);
            Grid.SetColumn(text, 0);

            var edit = new Button
            {
                Content = "Изменить",
                Style = (Style)FindResource("SecondaryButton"),
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(8, 0, 6, 0),
                ToolTip = "Поправить адрес и то, что на него присылать",
            };
            var captured = sub;
            edit.Click += (_, _) => EditSubscription(captured);
            Grid.SetColumn(edit, 1);

            var del = new Button
            {
                Content = "×",
                Style = (Style)FindResource("SecondaryButton"),
                Padding = new Thickness(10, 2, 10, 2),
                ToolTip = "Убрать адрес из рассылки целиком",
            };
            del.Click += (_, _) =>
            {
                if (AppMessageBox.Show($"Убрать «{captured.Recipient}» из рассылки?", "Почта",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
                ReplaceSubscription(captured.Recipient, null);
            };
            Grid.SetColumn(del, 2);

            row.Children.Add(text);
            row.Children.Add(edit);
            row.Children.Add(del);
            EmailRulesPanel.Children.Add(row);
        }
    }

    private void AddEmailRule_Click(object sender, RoutedEventArgs e) => EditSubscription(null);

    private void EditSubscription(EmailRouting.EmailSubscription? existing)
    {
        var next = EmailSubscriptionDialog.Edit(Window.GetWindow(this), existing);
        if (next is null) return;
        ReplaceSubscription(existing?.Recipient, next);
    }

    /// <summary>Заменить всё, что заведено на адрес, новым набором. Сначала сносим старые строки
    /// этого адреса, потом пишем новые: иначе снятая галочка осталась бы в хранилище и человек
    /// продолжал бы получать то, от чего отписался.
    ///
    /// <paramref name="next"/> = null — адрес убирают целиком.</summary>
    private void ReplaceSubscription(string? oldRecipient, EmailRouting.EmailSubscription? next)
    {
        var rules = _services.Db.GetEmailRules();
        if (!string.IsNullOrWhiteSpace(oldRecipient))
            foreach (var r in rules.Where(r => string.Equals(r.Recipient, oldRecipient, StringComparison.OrdinalIgnoreCase)))
                _services.Db.DeleteEmailRule(r.Id);

        if (next is not null)
        {
            if (next.Everything) _services.Db.AddEmailRule("", next.Recipient);
            else foreach (var category in next.Categories) _services.Db.AddEmailRule(category, next.Recipient);
        }

        RenderEmailRules();
        RefreshEmailReadiness();
        _host.ShowStatus(next is null
            ? $"Адрес убран из рассылки: {oldRecipient}"
            : $"Рассылка сохранена: {next.Recipient}", category: NotificationCategory.General);
    }

    private static string LabelForCategoryId(string id) =>
        Enum.TryParse<NotificationCategory>(id, out var parsed)
            ? NotificationCategoryInfo.Label(parsed)
            // Неизвестная категория — это правило из более новой версии программы, приехавшее
            // синхронизацией. Показываем как есть и НЕ удаляем: чужая настройка не наше дело.
            : id;
}
