using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.App.Views;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IAppHost
{
    private readonly AppServices _services;
    private readonly Dictionary<string, object> _pageCache = new();
    private DispatcherTimer? _sync1500msTimer;
    private DispatcherTimer? _syncRepeatTimer;
    private DispatcherTimer? _hierarchy2sTimer;
    private DispatcherTimer? _statusClearTimer;
    private DispatcherTimer? _updateCheckTimer;
    private DispatcherTimer? _periodicUpdateCheckTimer;
    /// <summary>Разовый таймер показа окна «Что нового» после автообновления — см. CheckWhatsNewAsync.</summary>
    private DispatcherTimer? _whatsNewCheckTimer;
    private DispatcherTimer? _fwUpdateCheckTimer;
    private DispatcherTimer? _configCheckTimer;
    private DispatcherTimer? _configPullRepeatTimer;
    private DispatcherTimer? _configPushTimer;
    /// <summary>Задача 3: лёгкий fallback-опрос маркера ревизии — работает НЕЗАВИСИМО от
    /// sync_interval_min (который может быть равен 0, т.е. отключён пользователем) на фиксированном
    /// малом интервале, потому что сама проверка дешёвая (см. ConfigSyncService.ReadShared — читает
    /// только Конфиг\revision.json, пока ревизия не выросла).</summary>
    private DispatcherTimer? _revisionPollTimer;
    /// <summary>Best-effort триггер по изменению файла (Задача 3) — ДОПОЛНЕНИЕ к опросу выше, не
    /// замена: по SMB FileSystemWatcher ненадёжен (событие может не долететь/задвоиться/опоздать).
    /// См. SetupConfigWatcher.</summary>
    private System.IO.FileSystemWatcher? _configWatcher;
    private UpdateRelease? _pendingUpdate;
    private int? _lastModerationCount;
    private List<FirmwareUpdateInfo> _pendingFwUpdates = new();
    private List<UnknownEntry> _pendingUnknownItems = new();
    private bool _configPushLastFailed;
    private bool _fwAutoUpdateLastFailed;
    private bool _appUpdateCheckLastFailed;
    /// <summary>Папка обновлений задана, но недоступна — работаем с GitHub. Отдельный флаг от
    /// _appUpdateCheckLastFailed: проверка при этом формально удалась, но настроенный источник
    /// молча подменился запасным, и об этом надо сказать один раз на переходе (а не каждые 30 минут).</summary>
    private bool _appUpdateFolderLastFailed;
    private Version? _lastNotifiedUpdateVersion;

    /// <summary>Тикеты приходят с других машин в любой момент, а PullNewEvents раньше срабатывал
    /// только при открытии страницы «Тикеты» — о новом тикете оператор не узнавал, пока сам туда не
    /// заходил. Теперь тянем их тем же фоном, что и конфиг; _ticketSyncRunning гасит наложение тиков,
    /// _lastUnseenTickets — прежнее число непросмотренных (всплывашку показываем только на РОСТ, как
    /// у бейджа модерации), чтобы каждый тик не гудел о том же.</summary>
    private bool _ticketSyncRunning;
    private int? _lastUnseenTickets;

    /// <summary>Сколько операций синхронизации (приём/отправка конфига, тикеты) идёт прямо сейчас —
    /// СВОЙ счётчик, отдельный от Busy (тот общий с поиском и обходом диска). По просьбе из тикета
    /// коллеги индикатор синхронизации крутится ровно на синхре, а не на любой фоновой работе.</summary>
    private int _syncActivity;

    /// <summary>Текст последней ошибки синхронизации и когда она случилась — сбрасывается любой
    /// успешной синхрой. Держит пилюлю статуса в состоянии «ошибка» (оранжевый треугольник), пока не
    /// пройдёт удачный тик.</summary>
    private string? _syncLastError;

    /// <summary>Тик синхронизации теперь асинхронный, значит следующий может прийти, пока предыдущий
    /// ещё ждёт сетевой диск (диск отвечает медленнее, чем sync_interval_min). Раньше такого быть не
    /// могло — всё выполнялось внутри одного Tick на потоке интерфейса. Наложение прогонов не даёт
    /// ничего, кроме второй порции нагрузки на тот же диск, поэтому лишний тик просто пропускается.</summary>
    private bool _syncRunning;
    private bool _configSyncRunning;

    private bool _suppressThemeToggleHandler;

    [ObservableProperty] private string _roleLabel = "";
    /// <summary>Кто сейчас залогинен — показывается слева сверху вместо роли (по просьбе оператора):
    /// AD-логин, если вход был по AD, иначе имя учётной записи Windows (см. AppServices.CurrentUserName,
    /// тот же источник, что и автор тикетов). Обновляется в ApplyRole (старт + смена роли) и после
    /// повторного входа через кнопку «Выход».</summary>
    [ObservableProperty] private string _currentUserLabel = "";
    [ObservableProperty] private bool _isDarkTheme;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _diskStatusText = "Диск: …";
    [ObservableProperty] private object? _currentPageContent;
    [ObservableProperty] private bool _settingsVisible;
    [ObservableProperty] private bool _isSettingsActive;
    [ObservableProperty] private bool _updateBannerVisible;
    [ObservableProperty] private string _updateBannerText = "";
    [ObservableProperty] private bool _updateActionEnabled = true;
    [ObservableProperty] private bool _fwUpdateBannerVisible;
    [ObservableProperty] private string _fwUpdateBannerText = "";
    [ObservableProperty] private bool _unknownItemsBannerVisible;
    [ObservableProperty] private string _unknownItemsBannerText = "";
    [ObservableProperty] private bool _hierarchyConflictBannerVisible;
    [ObservableProperty] private string _hierarchyConflictBannerText = "";
    /// <summary>Задача 4 (отправитель) — «Изменений готово к отправке: N», см. RefreshPendingChangesBanner.
    /// В отличие от остальных баннеров эта плашка не самоскрывается по таймеру: пока накопитель не
    /// пуст, ей положено оставаться на виду (по дизайну — «неисчезающая плашка»).</summary>
    [ObservableProperty] private bool _pendingChangesBannerVisible;
    [ObservableProperty] private string _pendingChangesSummary = "";
    [ObservableProperty] private int _pendingChangesCount;
    /// <summary>Задача 4 (приёмник) — «Поступили изменения (N): […]», см. ShowIncomingChangesBanner.</summary>
    [ObservableProperty] private bool _incomingChangesBannerVisible;
    [ObservableProperty] private string _incomingChangesBannerText = "";
    [ObservableProperty] private int _unseenNotificationsCount;
    /// <summary>Сумма бейджей компактных пунктов (Тикеты/Сетевые диски), спрятанных в свёрнутой по
    /// умолчанию секции «ДОПОЛНИТЕЛЬНО». Всплывает на её заголовок, пока секция не раскрыта — иначе
    /// пришедший тикет ставит бейдж на пункт, которого на экране НЕТ, и оператор его не видит (ровно
    /// жалоба «не вижу, когда прилетают новые тикеты»). Раскрыл секцию — виден сам бейдж пункта, тогда
    /// заголовочный прячется (см. MainWindow.xaml).</summary>
    [ObservableProperty] private int _moreSectionBadgeCount;

    /// <summary>То же для секции «ДЛЯ НАЛАДЧИКА»: сумма бейджей спрятанных в ней пунктов. Сейчас
    /// бейдж там один — очередь модерации прошивок, и она как раз тот случай, ради которого
    /// заголовочный счётчик и нужен: пункт «Модерация прошивок» переехал из основного списка в
    /// свёрнутую секцию, и без этого числа администратор перестал бы видеть, что версии ждут
    /// разметки.</summary>
    [ObservableProperty] private int _setupSectionBadgeCount;

    /// <summary>Развёрнута ли секция «ДЛЯ НАЛАДЧИКА». Пишется в настройки при каждом переключении —
    /// см. ConfigService.SidebarSetupExpanded.</summary>
    [ObservableProperty] private bool _setupSectionExpanded;

    /// <summary>Показывать ли секцию «ДЛЯ НАЛАДЧИКА» вообще. Пустая секция — это заголовок, который
    /// ничего не открывает: у роли, которой не досталось ни одного её пункта, он был бы просто
    /// обманом. Сегодня такой роли нет (наклейки и «Сформировать паспорт» доступны всем), но
    /// достаточно закрыть кому-то параметры в RolesConfig.RoleAccess, чтобы это стало правдой, и
    /// проверка обязана быть на месте раньше, чем это случится.</summary>
    [ObservableProperty] private bool _setupSectionVisible = true;

    /// <summary>Состояние пилюли синхронизации в статус-строке (отдельный от поиска индикатор — см.
    /// _syncActivity и тикет коллеги «отделить анимацию синхронизации от анимации поиска»):
    /// "syncing" — идёт синхра (стрелки крутятся), "error" — последний тик упал (оранжевый ⚠),
    /// "pending" — есть неотправленное (свои правки/теги в накопителе или тикеты в очереди),
    /// "synced" — всё синхронизировано (приглушённые стрелки). XAML разбирает строку в глиф/цвет/
    /// анимацию через DataTrigger — так одно поле правит и вид, и подсказку.</summary>
    [ObservableProperty] private string _syncStatusState = "synced";
    [ObservableProperty] private string _syncStatusTooltip = "Синхронизация";
    /// <summary>Быстрый доступ display mode — see ConfigService.QuickAppsDisplayMode. Two separate
    /// Visibility-driving flags (rather than one enum bound with a converter) because MainWindow.xaml
    /// needs to combine this with "QuickApps.Count > 0" (an empty list never shows a bar/strip either
    /// way, in EITHER mode) and XAML has no clean way to AND two bindings without a multi-converter.</summary>
    [ObservableProperty] private bool _quickAppsSidebarVisible;
    [ObservableProperty] private bool _quickAppsTopVisible;
    /// <summary>Only meaningful while QuickAppsTopVisible is true — whether each bubble in the top
    /// row also shows its shortcut name underneath ("top_labeled" mode) or is icon-only ("top").</summary>
    [ObservableProperty] private bool _quickAppsTopShowLabels;

    /// <summary>Индикатор фоновой работы в статус-строке (рядом с «Диск: …»). Всё, что ходит на
    /// сетевой диск, обязано открывать здесь область на время работы — иначе для пользователя это
    /// выглядит как зависшая программа, ровно на что и жаловались.</summary>
    public BusyTracker Busy { get; } = new();

    public string CurrentRole { get; private set; } = "naladchik";
    public string CurrentTheme { get; private set; } = "light";
    public string CurrentPageId { get; private set; } = "search";

    public ObservableCollection<NavItem> NavItems { get; } = new();
    public ObservableCollection<QuickAppItem> QuickApps { get; } = new();
    public ObservableCollection<NotificationEntry> NotificationHistory { get; } = new();

    private const int NotificationHistoryLimit = 100;
    private const int BannerAutoHideMs = 10000;
    /// <summary>Задача 3 — интервал fallback-опроса маркера ревизии, см. _revisionPollTimer.</summary>
    private static readonly TimeSpan RevisionPollInterval = TimeSpan.FromSeconds(75);

    /// <summary>How often the app re-checks for a new self-update after the one-time startup check —
    /// see StartTimers/_periodicUpdateCheckTimer. Deliberately not a user-facing setting (unlike
    /// sync_interval_min) — 30 minutes is frequent enough to notice a fresh release same-day without
    /// hammering GitHub/the update folder. Exposed as internal so a live UI test can temporarily swap
    /// in a shorter interval without touching the timer wiring itself.</summary>
    internal static TimeSpan PeriodicUpdateCheckInterval { get; set; } = TimeSpan.FromMinutes(30);

    public MainWindowViewModel(AppServices services)
    {
        _services = services;

        foreach (var (pageId, label, section) in RolesConfig.NavItems)
            NavItems.Add(new NavItem(pageId, label, section));

        // Свёрнута/развёрнута секция «ДЛЯ НАЛАДЧИКА» — запоминается между запусками (в отличие от
        // «ДОПОЛНИТЕЛЬНО», которая всегда открывается свёрнутой): туда ходят работать, а не смотреть
        // настройку раз в месяц. Ставится ДО первого показа окна, чтобы секция не «моргала».
        _setupSectionExpanded = _services.Cfg.SidebarSetupExpanded();

        CurrentRole = _services.Cfg.CurrentRole();
        CurrentTheme = _services.Cfg.Theme();
        _suppressThemeToggleHandler = true;
        IsDarkTheme = CurrentTheme == "dark";
        _suppressThemeToggleHandler = false;

        ApplyRole(CurrentRole);
        ThemeManager.Apply(CurrentTheme);
        ReloadSidebarApps();
        Navigate(FirstAllowedPageId(CurrentRole));

        // Пилюля статуса синхры и накопитель — показать актуальное состояние сразу, не дожидаясь
        // первого фонового тика (RefreshPendingChangesBanner сам зовёт RefreshSyncStatus).
        RefreshPendingChangesBanner();

        StartTimers();
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Navigate(string pageId)
    {
        if (!_pageCache.ContainsKey(pageId) && !TryCreatePage(pageId, out _))
            return;

        CurrentPageId = pageId;
        CurrentPageContent = _pageCache[pageId];
        if (pageId == "search" && _pageCache[pageId] is SearchView searchView)
            searchView.RefreshIfActive();
        if (pageId == "newversions" && _pageCache[pageId] is NewVersionsView newVersionsView)
            newVersionsView.RefreshIfActive();
        if (pageId == "inspection" && _pageCache[pageId] is InspectionView inspectionView)
            inspectionView.RefreshIfActive();
        if (pageId == "hosting" && _pageCache[pageId] is HostingView hostingView)
            hostingView.RefreshIfActive();
        if (pageId == "network" && _pageCache[pageId] is NetworkSyncView networkView)
            networkView.RefreshIfActive();
        if (pageId == "tickets" && _pageCache[pageId] is TicketsView ticketsView)
        {
            ticketsView.RefreshIfActive();
            // Открыли страницу «Тикеты» — всё, что на ней сейчас видно, считается просмотренным:
            // сдвигаем watermark на самый свежий тикет и гасим бейдж (без всплывашки — это не новое
            // событие, оператор сам сюда пришёл).
            MarkTicketsSeen();
        }
        // Загрузка ПО / Параметры перечитывают справочники (типы шкафов, подтипы, контроллеры,
        // производители) — иначе в комбобоксах остаётся состояние на момент первой отрисовки
        // страницы, см. UploadView.RefreshIfActive.
        if (pageId == "upload" && _pageCache[pageId] is UploadView uploadView)
            uploadView.RefreshIfActive();
        if (pageId == "params" && _pageCache[pageId] is ParamsView paramsView)
            paramsView.RefreshIfActive();
        // «Чистка диска» переехала в Настройки: сбросом её списка находок при каждом заходе на
        // вкладку занимается сам SettingsView (см. SettingsView.Tab_Click).

        foreach (var item in NavItems)
            item.IsActive = item.PageId == pageId;
        IsSettingsActive = pageId == "settings";
        RefreshModerationBadge();
        RefreshTicketsBadge(notify: false);
    }

    /// <summary>Keeps the "Модерация прошивок" sidebar badge in sync with Settings→Прошивки→Модерация's
    /// own counter — refreshed on every navigation (cheap COUNT query) so it never goes stale after
    /// moderating a version and switching tabs, without needing a dedicated changed-event. Also
    /// notifies the administrator specifically (not just a passive badge) the moment the count goes
    /// up — i.e. a new firmware actually started needing moderation, not just "some still do".</summary>
    private void RefreshModerationBadge()
    {
        var item = NavItems.FirstOrDefault(n => n.PageId == "newversions");
        if (item is null) return;
        try
        {
            var count = _services.Db.GetUnreleasedFwVersionsCount();
            item.BadgeCount = count;
            // «Модерация прошивок» переехала в свёрнутую секцию «ДЛЯ НАЛАДЧИКА» — её бейдж теперь
            // обязан всплывать на заголовок секции, иначе очередь модерации не видна вовсе, пока
            // секция закрыта.
            RecomputeSectionBadges();

            if (CurrentRole == "administrator" && _lastModerationCount.HasValue && count > _lastModerationCount.Value)
                ShowStatus($"Новая прошивка ожидает модерации (всего в очереди: {count})", 8000, NotificationCategory.FirmwareAndParams);
            _lastModerationCount = count;
        }
        catch { /* best effort — badge just won't update this time */ }
    }

    // ── Тикеты: фоновый приём + бейдж/уведомление ───────────────────────────────
    // PullNewEvents раньше срабатывал ТОЛЬКО при открытии страницы «Тикеты», поэтому о тикете,
    // оставленном коллегой, оператор не узнавал, пока сам туда не заходил. Теперь тянем их тем же
    // фоном, что и конфиг (см. вызов в CheckForConfigUpdateAsync), и показываем счётчик на пункте
    // меню + всплывашку на КАЖДЫЙ прирост непросмотренных.

    /// <summary>Тянет новые события тикетов с диска и обновляет бейдж. DB-часть (InsertTicketIfMissing/
    /// ApplyTicketStatusIfNewer внутри PullNewEvents) идёт на потоке интерфейса — соединение SQLite одно
    /// и не потокобезопасно (см. HierarchyService), ровно так же тикеты синхронизирует TicketsView при
    /// открытии страницы. Тикеты малы и их немного, поэтому короткий поход на шару здесь допустим;
    /// _ticketSyncRunning гасит наложение тиков.</summary>
    private void SyncTicketsNow()
    {
        if (_ticketSyncRunning) return;
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root)) return;

        _ticketSyncRunning = true;
        using var activity = BeginSyncActivity("тикеты");
        try
        {
            TicketSyncService.FlushOutbox(_services, root, out var flushFailed);
            var applied = TicketSyncService.PullNewEvents(_services, root, out var pullFailed);
            if (flushFailed + pullFailed > 0)
                NoteSyncOutcome($"Тикеты: не удалось обработать файлов: {flushFailed + pullFailed}", isError: true);
            else
                NoteSyncOutcome(applied > 0 ? $"Тикеты: получено событий: {applied}" : null, isError: false);
            RefreshTicketsBadge(notify: true);
            // Тикет мог сменить статус на «в очереди на отправке нечего» — освежаем и пилюлю.
            RefreshSyncStatus();
        }
        catch { /* best effort — локальные тикеты всё равно видны, повтор на следующем тике */ }
        finally { _ticketSyncRunning = false; }
    }

    /// <summary>Тикеты, видимые ТЕКУЩЕЙ роли (администратор — все, остальные — только свои, по имени
    /// Windows/AD, тот же фильтр, что в TicketsView), у которых что-то поменялось (UpdatedAt) после
    /// последнего просмотра страницы. Ставит бейдж на пункт «Тикеты» и, если непросмотренных стало
    /// БОЛЬШЕ (пришло новое), негромко уведомляет — на каждый тик подряд об одном и том же не гудит.</summary>
    private void RefreshTicketsBadge(bool notify)
    {
        var item = NavItems.FirstOrDefault(n => n.PageId == "tickets");
        if (item is null) return;
        try
        {
            var lastSeen = _services.Cfg.TicketsLastSeenAt();
            var me = _services.CurrentUserName;
            var isAdmin = CurrentRole == "administrator";
            var unseen = _services.Db.GetTickets()
                .Where(t => isAdmin || string.Equals(t.CreatedBy, me, StringComparison.OrdinalIgnoreCase))
                .Count(t => string.CompareOrdinal(t.UpdatedAt, lastSeen) > 0);

            item.BadgeCount = unseen;
            RecomputeSectionBadges();

            if (notify && _lastUnseenTickets.HasValue && unseen > _lastUnseenTickets.Value && unseen > 0)
                ShowStatus(unseen == 1 ? "Новый тикет — нажмите «Показать»" : $"Новые тикеты/изменения: {unseen} — нажмите «Показать»",
                    8000, NotificationCategory.General, reopen: () => Navigate("tickets"));
            _lastUnseenTickets = unseen;
        }
        catch { /* best effort — бейдж просто не обновится в этот раз */ }
    }

    /// <summary>«Наклейки» и «Сформировать паспорт» в секции «ДЛЯ НАЛАДЧИКА» — окна, а не страницы,
    /// и роль их не ограничивает: печатает наладчик, а шаблон заводит программист или администратор.
    /// Отдельным свойством, а не просто константой в разметке, чтобы «секция пуста — секции нет»
    /// (см. SetupSectionVisible) считалось по тому же списку, что и рисуется.</summary>
    public bool SetupToolsVisible => true;

    /// <summary>Сумма непросмотренных по всем пунктам свёрнутых секций — заголовок секции показывает
    /// её, пока та свёрнута. Иначе пришедшее событие ставит бейдж на пункт, которого на экране нет.</summary>
    private void RecomputeSectionBadges()
    {
        MoreSectionBadgeCount = NavItems.Where(n => n.Section == NavSection.More && n.IsVisible).Sum(n => n.BadgeCount);
        SetupSectionBadgeCount = NavItems.Where(n => n.Section == NavSection.Setup && n.IsVisible).Sum(n => n.BadgeCount);
        SetupSectionVisible = NavItems.Any(n => n.Section == NavSection.Setup && n.IsVisible) || SetupToolsVisible;
    }

    /// <summary>Секцию раскрыли/свернули — запомнить. Единственное состояние сайдбара, которое
    /// переживает перезапуск: см. ConfigService.SidebarSetupExpanded.</summary>
    partial void OnSetupSectionExpandedChanged(bool value)
    {
        try { _services.Cfg.SetSidebarSetupExpanded(value); }
        catch (Exception) { /* не смогли записать настройку — на работу секции это не влияет */ }
    }

    /// <summary>IAppHost — TicketsView зовёт после того, как показал страницу (и подтянул новые
    /// события с диска), чтобы пометка «просмотрено» ставилась по актуальному списку.</summary>
    public void OnTicketsViewed() => MarkTicketsSeen();

    /// <summary>Открыли страницу «Тикеты» — сдвигаем watermark на самый свежий видимый тикет (всё, что
    /// на ней сейчас показано, считается просмотренным) и гасим бейдж без всплывашки.</summary>
    private void MarkTicketsSeen()
    {
        try
        {
            var me = _services.CurrentUserName;
            var isAdmin = CurrentRole == "administrator";
            var newest = _services.Db.GetTickets()
                .Where(t => isAdmin || string.Equals(t.CreatedBy, me, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.UpdatedAt)
                .OrderBy(s => s, StringComparer.Ordinal)
                .LastOrDefault() ?? "";
            if (!string.IsNullOrEmpty(newest)) _services.Cfg.SetTicketsLastSeenAt(newest);
        }
        catch { /* не смогли — бейдж просто не сбросится до следующего открытия */ }
        RefreshTicketsBadge(notify: false);
    }

    // ── Пилюля статуса синхронизации (статус-строка) ────────────────────────────
    // Отдельный от Busy индикатор именно про синхру (тикет коллеги: «отделить анимацию синхронизации
    // конфига от анимации поиска, добавить иконку в виде скруглённых стрелок, ошибку — красными
    // стрелками / восклицательным знаком в оранжевом треугольнике»). Приоритет состояний:
    // идёт синхра → ошибка → есть неотправленное → всё синхронизировано.

    /// <summary>Отмечает начало операции синхронизации: пилюля переходит в «крутящиеся стрелки», пока
    /// scope не освобождён. Считает вложенно (несколько синхр разом — нормально), поэтому int-счётчик,
    /// а не bool.
    ///
    /// <paramref name="stage"/> — что именно синхронизируется («Приём конфига с диска», «Тикеты»…);
    /// попадает в подсказку пилюли. Синхронизация НЕ занимает индикатор фоновой работы (Busy) внизу:
    /// он остался за видимой работой страницы — поиском, копированием, сборкой PDF. Раньше обе
    /// работы делили одну полоску, и во время поиска снизу мог висеть текст про синхру — ровно
    /// жалоба «отделить анимацию синхронизации от анимации поиска».</summary>
    private IDisposable BeginSyncActivity(string? stage = null)
    {
        _syncActivity++;
        if (!string.IsNullOrEmpty(stage)) _syncStages.Add(stage!);
        RefreshSyncStatus();
        return new SyncActivityScope(this, stage);
    }

    /// <summary>IAppHost — то же самое для страниц (ручная синхронизация с «Сетевых дисков» и из
    /// Настроек): крутит пилюлю, а не полоску поиска.</summary>
    public IDisposable BeginSync(string stage) => BeginSyncActivity(stage);

    /// <summary>IAppHost — итог ручной синхронизации со страницы: ошибка красит пилюлю (и держится до
    /// следующего удачного тика), успех её гасит.</summary>
    public void NoteSyncResult(string? message, bool isError) => NoteSyncOutcome(message, isError);

    /// <summary>Что синхронизируется прямо сейчас — по строке на каждый живой scope; в подсказке
    /// показывается последняя начатая (как и в BusyTracker).</summary>
    private readonly List<string> _syncStages = new();

    private sealed class SyncActivityScope(MainWindowViewModel owner, string? stage) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner._syncActivity--;
            if (!string.IsNullOrEmpty(stage)) owner._syncStages.Remove(stage!);
            owner.RefreshSyncStatus();
        }
    }

    /// <summary>Итог одного синхро-тика: запомнить ошибку (пилюля станет «ошибка») либо стереть её
    /// (успех). Плюс в подробном режиме синхронизации (Сетевые диски → «Подробный режим») пишет в
    /// статус-строку, ЧТО именно синхронизировалось — обычно всё это происходит молча.</summary>
    private void NoteSyncOutcome(string? message, bool isError)
    {
        if (isError) _syncLastError = message;
        else _syncLastError = null;

        if (_services.Cfg.SyncVerbose() && !string.IsNullOrEmpty(message))
            ShowStatus((isError ? "⚠ " : "⟳ ") + message, isError ? 8000 : 4000, NotificationCategory.Sync);

        RefreshSyncStatus();
    }

    /// <summary>Пересчитывает состояние пилюли по приоритету и собирает подсказку. Дёшево (пара
    /// запросов COUNT + чтение настроек) — зовётся из каждой точки, где синхро-состояние могло
    /// поменяться.</summary>
    private void RefreshSyncStatus()
    {
        int outbox = 0;
        try { outbox = _services.Db.GetTicketOutbox().Count; } catch { /* БД занята — ноль, поправится на след. тике */ }
        var pendingLocal = PendingChangesCount + outbox;

        string state;
        string tip;
        if (_syncActivity > 0)
        {
            state = "syncing";
            tip = _syncStages.Count > 0
                ? $"Идёт синхронизация с сетевым диском: {_syncStages[^1]}"
                : "Идёт синхронизация с сетевым диском…";
        }
        else if (_syncLastError is not null)
        {
            state = "error";
            tip = "Ошибка синхронизации: " + _syncLastError + "\nПовторится автоматически. Открыть «Сетевые диски» →";
        }
        else if (pendingLocal > 0)
        {
            state = "pending";
            var parts = new List<string>();
            if (PendingChangesCount > 0) parts.Add($"правок справочника/тегов: {PendingChangesCount}");
            if (outbox > 0) parts.Add($"событий тикетов: {outbox}");
            tip = "Ожидает отправки на диск (" + string.Join(", ", parts) + ").\nОткрыть «Сетевые диски» →";
        }
        else
        {
            state = "synced";
            var last = _services.Cfg.ConfigLastSyncedAt();
            var when = last.Length >= 16 ? last[..16].Replace('T', ' ') : last;
            tip = string.IsNullOrEmpty(when) ? "Синхронизировано с сетевым диском" : $"Синхронизировано · последний приём: {when}";
            tip += "\nОткрыть «Сетевые диски» →";
        }

        SyncStatusState = state;
        SyncStatusTooltip = tip;
    }

    /// <summary>Клик по пилюле статуса — открывает «Сетевые диски» (там таймстемпы приёма/отправки,
    /// ревизия, конфликты и переключатель подробного режима синхронизации).</summary>
    [RelayCommand]
    private void ShowSyncDetails() => Navigate("network");

    private bool TryCreatePage(string pageId, out object? page)
    {
        page = pageId switch
        {
            "search" => new SearchView(_services, this),
            "inspection" => new InspectionView(_services, this),
            "newversions" => new NewVersionsView(_services, this),
            "upload" => new UploadView(_services, this),
            "params" => new ParamsView(_services, this),
            "settings" => new SettingsView(_services, this),
            "network" => new NetworkSyncView(_services, this),
            "hosting" => new HostingView(_services, this),
            "tickets" => new TicketsView(_services, this),
            _ => null,
        };
        if (page is null) return false;
        _pageCache[pageId] = page;
        return true;
    }

    // ── Roles ─────────────────────────────────────────────────────────────────

    public void ApplyRole(string role)
    {
        CurrentRole = role;
        RoleLabel = RolesConfig.RoleLabel(role);
        CurrentUserLabel = _services.CurrentUserName;
        var allowed = RolesConfig.RoleAccess.GetValueOrDefault(role, new HashSet<string>());
        foreach (var item in NavItems)
            item.IsVisible = allowed.Contains(item.PageId);
        SettingsVisible = allowed.Contains("settings");
        // Роль сменилась — пункты секций появились/исчезли: пересчитать и заголовочные бейджи, и
        // «показывать ли секцию вообще».
        RecomputeSectionBadges();

        // Redirect to the first page this role actually has access to if the current page is no
        // longer allowed for the new role — landing on "search" regardless (the old behavior) sent
        // "programmer" (allowed = upload, params only) to a page it can't see, with no active nav
        // button to show for it.
        if (!allowed.Contains(CurrentPageId))
            Navigate(FirstAllowedPageId(role));

        // Settings' own tab/field visibility (see SettingsView.ApplyRoleVisibility) is role-dependent
        // TOO, on top of the whole page being allowed/not — and unlike other pages that just get
        // re-rendered wholesale on next Navigate, this one can still be the CURRENT page across a role
        // switch (both administrator and naladchik/programmer can reach "settings" now), so it needs
        // an explicit refresh here rather than relying on Navigate to have re-created it.
        if (_pageCache.TryGetValue("settings", out var settingsPage) && settingsPage is SettingsView settingsView)
            settingsView.ApplyRoleVisibility();

        RefreshConfigSync();
    }

    private static string FirstAllowedPageId(string role)
    {
        var allowed = RolesConfig.RoleAccess.GetValueOrDefault(role, new HashSet<string>());
        foreach (var (pageId, _, _) in RolesConfig.NavItems)
            if (allowed.Contains(pageId)) return pageId;
        return "search";
    }

    public void SwitchRole(string role)
    {
        CurrentRole = role;
        _services.Cfg.SetRole(role);
        ApplyRole(role);
    }

    // ── Theme ─────────────────────────────────────────────────────────────────

    public string ThemeLabel => IsDarkTheme ? "Тёмная тема" : "Светлая тема";

    partial void OnIsDarkThemeChanged(bool value)
    {
        OnPropertyChanged(nameof(ThemeLabel));
        if (_suppressThemeToggleHandler) return;
        CurrentTheme = value ? "dark" : "light";
        _services.Cfg.SetTheme(CurrentTheme);
        ThemeManager.Apply(CurrentTheme);
    }

    // ── Status bar ────────────────────────────────────────────────────────────

    /// <summary>If the category is disabled in Настройки → Уведомления, the message is fully
    /// suppressed — no status-bar flash, no history entry — per the user's explicit request that a
    /// muted category shouldn't show up anywhere, not just skip the history.</summary>
    public void ShowStatus(string message, int ms = 4000, NotificationCategory category = NotificationCategory.General, Action? reopen = null)
    {
        if (!_services.Cfg.IsNotificationCategoryEnabled(category)) return;

        StatusMessage = message;
        if (!string.IsNullOrEmpty(message)) AddNotification(message, category, reopen);
        _statusClearTimer?.Stop();
        _statusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        _statusClearTimer.Tick += (_, _) =>
        {
            StatusMessage = "";
            _statusClearTimer!.Stop();
        };
        _statusClearTimer.Start();
    }

    // ── Notification center ───────────────────────────────────────────────────
    // Every ShowStatus() call and every banner appearance lands here — a status-bar message that
    // flashed for 4-8 seconds, or a banner that auto-hides after BannerAutoHideMs, is still visible
    // afterwards via the "Уведомления" sidebar button, not gone the moment nobody was looking.

    /// <summary>Callers that raise a banner directly (app/firmware update) rather than going through
    /// ShowStatus must check IsNotificationCategoryEnabled themselves before calling this AND before
    /// setting their *BannerVisible flag — this only guards the history entry, not the banner.</summary>
    private void AddNotification(string text, NotificationCategory category, Action? reopen = null, bool reopenIsModal = false)
    {
        // Повтор уже показанного сообщения не заводит новую строку — поднимает существующую наверх
        // со счётчиком (см. NotificationHistoryOps.CollapseRepeat). UnseenNotificationsCount при
        // этом не растёт: это не новая информация.
        if (NotificationHistoryOps.CollapseRepeat(NotificationHistory, text, reopen, DateTime.Now)) return;

        NotificationHistory.Insert(0, new NotificationEntry(text, DateTime.Now, category, reopen) { ReopenIsModal = reopenIsModal });
        while (NotificationHistory.Count > NotificationHistoryLimit)
            NotificationHistory.RemoveAt(NotificationHistory.Count - 1);
        if (_services.Cfg.IsNotificationCategoryCountedUnread(category))
            UnseenNotificationsCount++;
    }

    /// <summary>Interactive banners (update available, firmware update available) get 10 seconds
    /// before hiding themselves — long enough to read, short enough not to sit there forever if the
    /// user just doesn't act on it. Reopening from history brings the same interactive banner back
    /// rather than just repeating its text, so "Обновить сейчас" is still one click away.</summary>
    private void ScheduleBannerAutoHide(Action hide)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(BannerAutoHideMs) };
        timer.Tick += (_, _) => { timer.Stop(); hide(); };
        timer.Start();
    }

    [RelayCommand]
    private void ShowNotificationHistory()
    {
        UnseenNotificationsCount = 0;
        var win = new NotificationHistoryWindow(NotificationHistory, _services.Cfg) { Owner = Application.Current.MainWindow };
        win.ShowDialog();
    }

    // ── Startup timers (mirrors app.py's exact sequence) ─────────────────────

    private void StartTimers()
    {
        // 1000ms once: ensure disk folder structure exists. Deliberately BEFORE the 1500ms sync tick
        // below (was 2000ms/after, until live-testing Task 3 exposed the race that ordering caused —
        // see EnsureHierarchy's own doc): EnsureStructure silently auto-moves top-level unrecognised
        // names into «Неизвестное» as a side effect, so if CheckForUnknownItems' scan (part of
        // RunSync) ran first, its list could reference a path that got moved out from under it a
        // moment later, and the operator's very first unknown-items banner would already be stale
        // before they ever clicked "Показать". Running structure-and-cleanup first means the first
        // scan the operator sees reflects reality: only genuinely still-unresolved items.
        _hierarchy2sTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _hierarchy2sTimer.Tick += async (_, _) =>
        {
            _hierarchy2sTimer!.Stop();
            await EnsureHierarchyAsync();
        };
        _hierarchy2sTimer.Start();

        // 1500ms once (always), then every sync_interval_min minutes — unless it's 0, which means
        // "periodic auto-sync disabled on this machine" (see ConfigService.SyncIntervalMin); the
        // one-time startup sync above still runs regardless, only the repeat is skipped.
        _sync1500msTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _sync1500msTimer.Tick += async (_, _) =>
        {
            _sync1500msTimer!.Stop();
            await RunSyncAsync();
            var minutes = _services.Cfg.SyncIntervalMin();
            if (minutes <= 0) return;
            _syncRepeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
            _syncRepeatTimer.Tick += async (_, _) => await RunSyncAsync();
            _syncRepeatTimer.Start();
        };
        _sync1500msTimer.Start();

        // A self-update from a previous run may have failed silently after this process had already
        // closed (network share denied the file move, AV briefly locked the staged .exe, etc.) — see
        // AppUpdateService.InstallAndRestart. Surface it now instead of leaving it invisible.
        var lastUpdateError = AppUpdateService.TakeLastUpdateError();
        if (lastUpdateError is not null)
            AddNotification($"Автообновление не удалось: {lastUpdateError}", NotificationCategory.AppUpdates);

        // 2500ms once: check for app updates (folder if configured, else GitHub — see AppUpdateService).
        // Then, while the app stays open, re-check every PeriodicUpdateCheckInterval — a release that
        // ships after the app was already running used to only surface the next time someone
        // restarted it. One timer, reused for every tick (not a new thread/timer per check).
        _updateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        _updateCheckTimer.Tick += async (_, _) =>
        {
            _updateCheckTimer!.Stop();
            await CheckForAppUpdatesAsync();

            _periodicUpdateCheckTimer = new DispatcherTimer { Interval = PeriodicUpdateCheckInterval };
            _periodicUpdateCheckTimer.Tick += async (_, _) => await CheckForAppUpdatesAsync();
            _periodicUpdateCheckTimer.Start();
        };
        _updateCheckTimer.Start();

        // 2200ms once: показ окна «Что нового» после автообновления — сравнивает версию, которую эта
        // машина уже "видела" (ConfigService.LastWhatsNewShownVersion), с текущей CurrentVersionText.
        // Специально ДО проверки обновлений выше (2500ms) — это два независимых действия (одно про
        // версию, на которой приложение только что запустилось, другое про версию, которая ещё
        // только появится), незачем ставить одно в зависимость от таймингов другого. Не требует
        // сети/диска обновлений — GetReleaseNotesAsync внутри сама переживает недоступность GitHub.
        _whatsNewCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
        _whatsNewCheckTimer.Tick += async (_, _) =>
        {
            _whatsNewCheckTimer!.Stop();
            await CheckWhatsNewAsync();
        };
        _whatsNewCheckTimer.Start();

        // 3000ms once: check whether any locally cached firmware has a newer version on the server.
        _fwUpdateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3000) };
        _fwUpdateCheckTimer.Tick += async (_, _) =>
        {
            _fwUpdateCheckTimer!.Stop();
            await CheckForFirmwareUpdatesAsync();
        };
        _fwUpdateCheckTimer.Start();

        // 3500ms once, then every sync_interval_min minutes: pull+apply a newer shared config
        // automatically — settings are 100% local-only by design (see ConfigSyncService.
        // SkipSettingsKeys), so applying without a confirmation click is safe.
        _configCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3500) };
        _configCheckTimer.Tick += async (_, _) =>
        {
            _configCheckTimer!.Stop();
            await CheckForConfigUpdateAsync();
            var minutes = _services.Cfg.SyncIntervalMin();
            if (minutes <= 0) return;
            _configPullRepeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
            _configPullRepeatTimer.Tick += async (_, _) => await CheckForConfigUpdateAsync();
            _configPullRepeatTimer.Start();
        };
        _configCheckTimer.Start();

        // Задача 3: лёгкий fallback-опрос маркера ревизии — работает НЕЗАВИСИМО от sync_interval_min
        // (может быть 0, т.е. периодика приёма выключена пользователем) на собственном фиксированном
        // малом интервале, потому что сама проверка дешёвая, пока ревизия не выросла (см.
        // ConfigSyncService.ReadShared — читает только маленький незашифрованный маркер, не весь
        // конфиг). Переиспользует тот же CheckForConfigUpdateAsync, что и остальные тики приёма —
        // _configSyncRunning guard внутри него не даёт им наложиться друг на друга.
        _revisionPollTimer = new DispatcherTimer { Interval = RevisionPollInterval };
        _revisionPollTimer.Tick += async (_, _) => await CheckForConfigUpdateAsync();
        _revisionPollTimer.Start();

        // FileSystemWatcher на Конфиг\revision.json — best-effort триггер «по изменению» (Задача 3),
        // ДОПОЛНЕНИЕ к опросу выше, а не замена: по SMB он ненадёжен (событие может не долететь,
        // задвоиться или прийти с задержкой) — если событие не пришло, следующий тик
        // _revisionPollTimer/_configPullRepeatTimer всё равно всё увидит.
        SetupConfigWatcher();

        RefreshPendingChangesBanner();
    }

    /// <summary>Пересоздаёт наблюдатель за Конфиг\revision.json на текущем root_path. Best-effort:
    /// сетевая шара может не поддерживать уведомления вовсе, отсутствовать в момент вызова или
    /// отвалиться посреди работы приложения — во всех случаях наблюдатель просто не работает, и
    /// синхронизация продолжает жить на fallback-опросе (_revisionPollTimer/_configPullRepeatTimer).
    /// Вызывается из StartTimers и повторно при смене пути диска (см. OnRootPathChangedAsync).</summary>
    private void SetupConfigWatcher()
    {
        _configWatcher?.Dispose();
        _configWatcher = null;

        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root)) return;

        try
        {
            var configDir = System.IO.Path.Combine(root, "Конфиг");
            if (!System.IO.Directory.Exists(configDir)) return;

            var watcher = new System.IO.FileSystemWatcher(configDir, "revision.json")
            {
                NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.Size | System.IO.NotifyFilters.CreationTime,
            };
            watcher.Changed += OnConfigWatcherEvent;
            watcher.Created += OnConfigWatcherEvent;
            watcher.Renamed += OnConfigWatcherEvent;
            watcher.EnableRaisingEvents = true;
            _configWatcher = watcher;
        }
        catch
        {
            // Сеть/права/драйвер шары не поддерживают уведомления о файле — тихо остаёмся на
            // fallback-опросе, ничего критичного не сломалось.
            _configWatcher = null;
        }
    }

    /// <summary>Событие FileSystemWatcher приходит на СВОЁМ потоке (не UI) — через Dispatcher, иначе
    /// CheckForConfigUpdateAsync (трогает ObservableProperty-поля) упадёт с ошибкой доступа к
    /// объекту с другого потока. Отдельного debounce-таймера не нужно: _configSyncRunning guard
    /// внутри CheckForConfigUpdateAsync уже гасит наложение тиков, а несколько событий подряд
    /// (Changed+Created на некоторых реализациях шар прилетают парой на одну и ту же запись) просто
    /// сольются в один (максимум два подряд) фактических тика.</summary>
    private void OnConfigWatcherEvent(object sender, System.IO.FileSystemEventArgs e)
    {
        try { Application.Current?.Dispatcher.BeginInvoke(async () => await CheckForConfigUpdateAsync()); }
        catch { /* приложение закрывается / диспетчер недоступен — событию просто некуда деться */ }
    }

    // ── App updates ───────────────────────────────────────────────────────────

    private async Task CheckForAppUpdatesAsync()
    {
        UpdateCheckResult result;
        try
        {
            // EffectiveAppUpdatePath, а не AppUpdatePath: путь может быть задан не на этой машине, а
            // администратором в общей (синхронизируемой) настройке — см. UpdateFolderResolver.
            result = await AppUpdateService.CheckForUpdatesAsync(_services.Cfg.EffectiveAppUpdatePath());
        }
        catch (Exception ex)
        {
            // В журнал — ВСЕГДА, а не только на переходе в «не работает» ниже: жалоба «обновления не
            // приходят» разбирается по журналу, и там должна быть видна каждая неудачная попытка,
            // включая случай «недоступны оба источника сразу» (UpdateSourcesUnavailableException).
            AppUpdateService.LogSourceFailure($"Проверка обновлений не удалась: {AppUpdateService.DescribeError(ex)}");

            // Здесь стоял голый `catch { return; }` с обоснованием «пользователь всегда может
            // проверить вручную в Настройках» — на практике это означало, что сломавшаяся проверка
            // (GitHub недоступен из заводской сети, прокси режет TLS, сетевая папка обновлений
            // отвалилась) не оставляла ВООБЩЕ никакого следа: ни плашки, ни записи в истории. Никто
            // не ходит проверять вручную то, о поломке чего ему не сообщили, — приложение просто
            // тихо оставалось на старой версии сколько угодно долго. Правило то же, что у
            // PushConfigNow и автообновления прошивок: сообщаем один раз на переходе в «не
            // работает», а не на каждом тике раз в 30 минут.
            if (!_appUpdateCheckLastFailed)
            {
                _appUpdateCheckLastFailed = true;
                AddNotification($"Проверка обновлений приложения не удалась: {AppUpdateService.DescribeError(ex)}",
                    NotificationCategory.AppUpdates);
            }
            return;
        }
        _appUpdateCheckLastFailed = false;

        // Настроенная папка обновлений отвалилась, а обновления поехали с GitHub. Само по себе это
        // ещё работает — но именно так выглядит начало проблемы, ради которой папку и заводили:
        // если/когда GitHub закроют по IP, обновления после этого исчезнут молча. Говорим сразу.
        if (result.FolderProblem is not null)
        {
            AppUpdateService.LogSourceFailure($"Папка обновлений недоступна ({result.FolderProblem}) — использован запасной источник: {result.SourceLabel}.");
            if (!_appUpdateFolderLastFailed)
            {
                _appUpdateFolderLastFailed = true;
                AddNotification($"Папка обновлений недоступна — {result.FolderProblem}. Обновления пока берутся из запасного источника: {result.SourceLabel}.",
                    NotificationCategory.AppUpdates);
            }
        }
        else
        {
            _appUpdateFolderLastFailed = false;
        }

        if (result.Releases.Count == 0) return;
        var latest = result.Releases[0];
        if (latest.Version <= AppUpdateService.CurrentVersion) return;

        _pendingUpdate = latest;
        var notifyEnabled = _services.Cfg.IsNotificationCategoryEnabled(NotificationCategory.AppUpdates);
        if (_services.Cfg.AppAutoUpdate())
        {
            if (notifyEnabled)
            {
                UpdateBannerText = $"Устанавливается версия {latest.Version} (источник: {result.SourceLabel})…";
                UpdateBannerVisible = true;
            }
            await InstallUpdate();
        }
        else
        {
            // Запись в историю уведомлений — БЕЗУСЛОВНО, в т.ч. при выключенной категории
            // «Обновления приложения». Раньше выключенная категория вместе с выключенным
            // автообновлением означала, что найденная новая версия молча выбрасывалась: ветка
            // `else if (notifyEnabled)` не делала вообще ничего, и приложение оставалось на старой
            // версии без единого следа где бы то ни было. Галочка категории глушит всплывающую
            // плашку — это её работа, — но не должна делать существование новой версии
            // ненаблюдаемым: историю пользователь открывает сам, когда идёт разбираться.
            var text = $"Доступна новая версия {latest.Version} (текущая {AppUpdateService.CurrentVersion}). Источник: {result.SourceLabel}.";
            // Дедупликация по версии: проверка повторяется каждые 30 минут, и без этого одна и та же
            // версия сыпала бы в историю по записи за полчаса, пока её не поставят.
            if (_lastNotifiedUpdateVersion != latest.Version)
            {
                _lastNotifiedUpdateVersion = latest.Version;
                AddNotification(text, NotificationCategory.AppUpdates, reopen: () => UpdateBannerVisible = true);
            }

            if (notifyEnabled)
            {
                UpdateBannerText = text;
                UpdateBannerVisible = true;
                // Единственная плашка, которая НЕ прячется сама: у всех остальных текст — уведомление
                // о том, что уже случилось, а здесь на плашке живёт кнопка «Установить», и автоскрытие
                // уносило вместе с текстом единственный способ начать установку. Сюда попадают только
                // те, кто осознанно выключил автообновление у себя в Настройках (по умолчанию оно
                // теперь включено — см. ConfigService.Defaults["app_auto_update"]), и для них эта
                // плашка по-прежнему единственная дорога к новой версии.
            }
        }
    }

    // Same class of bug as the config-push one fixed earlier: when AppAutoUpdate is on but the
    // AppUpdates notification category is off, CheckForAppUpdatesAsync below never turns
    // UpdateBannerVisible on before calling this — so a failure here (download dropped mid-stream,
    // staged .exe briefly locked by AV, network share denied) used to update only UpdateBannerText,
    // which nothing was displaying, and never touched the notification history either. Always
    // forcing the banner visible and always logging to history here means an install failure is
    // never quieter than the "new version available" notice was, regardless of that toggle.
    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (_pendingUpdate is null) return;
        UpdateActionEnabled = false;
        UpdateBannerText = $"Установка версии {_pendingUpdate.Version}…";
        UpdateBannerVisible = true;
        try
        {
            await AppUpdateService.InstallAndRestartAsync(_pendingUpdate);
        }
        catch (Exception ex)
        {
            UpdateBannerText = $"Не удалось установить обновление: {AppUpdateService.DescribeError(ex)}";
            UpdateBannerVisible = true;
            UpdateActionEnabled = true;
            AddNotification(UpdateBannerText, NotificationCategory.AppUpdates, reopen: () => UpdateBannerVisible = true);
        }
    }

    [RelayCommand]
    private void DismissUpdateBanner() => UpdateBannerVisible = false;

    // ── "Что нового" после автообновления ────────────────────────────────────

    /// <summary>Показывает окно «Что нового в vX.Y.Z» ровно один раз на этой машине — на первом
    /// запуске КАЖДОЙ версии, отличной от той, что уже была отмечена как показанная (решение —
    /// чистая функция AppUpdateService.ShouldShowWhatsNew, покрыта тестами без WPF/сети). Три случая:
    /// • ключ ещё пуст (самая первая установка / первый запуск после появления самой этой фичи на
    ///   уже существующей установке) — окну показываться не с чем сравнивать ("что нового" относительно
    ///   ничего не имеет смысла), молча запоминаем текущую версию и выходим;
    /// • ключ отличается от текущей версии — приложение только что обновилось: тянем release notes
    ///   этой версии с GitHub и показываем их;
    /// • ключ уже равен текущей версии — уже показывали (или зачли) в этом запуске приложения, либо
    ///   в одном из прошлых, ничего не делаем.
    /// Версия в ключ пишется СРАЗУ по принятии решения показывать — даже если сами notes не удалось
    /// получить (сеть недоступна, GitHub лежит, релиза с таким тегом нет) — иначе при каждом
    /// следующем запуске приложение снова и снова пыталось бы показать то же самое окно.</summary>
    private async Task CheckWhatsNewAsync()
    {
        var current = AppUpdateService.CurrentVersionText;
        var lastShown = _services.Cfg.LastWhatsNewShownVersion();

        if (!AppUpdateService.ShouldShowWhatsNew(lastShown, current))
        {
            if (string.IsNullOrEmpty(lastShown))
                _services.Cfg.SetLastWhatsNewShownVersion(current);
            return;
        }

        string? notes;
        try { notes = await AppUpdateService.GetReleaseNotesAsync(current); }
        catch { notes = null; } // GetReleaseNotesAsync уже не бросает — двойная страховка на случай будущих правок

        _services.Cfg.SetLastWhatsNewShownVersion(current);

        // Сохраняем «что нового» СРАЗУ по факту обновления — и в постоянный журнал изменений (его
        // открывают потом, когда разовое окно давно закрыто), и строкой в историю уведомлений. Делаем
        // это ДО проверки свёрнутого старта: журнал и уведомление человек смотрит сам, поэтому они
        // должны наполниться независимо от того, показалось ли разовое модальное окно. Строка истории
        // не дублирует всё тело релиза, а ведёт назад в то же окно «Что нового» кнопкой «Показать».
        if (!string.IsNullOrWhiteSpace(notes))
        {
            var body = notes!;
            _services.Cfg.AddAppChangelogEntry(current, body, DateTime.Now);
            AddNotification($"Обновление до версии {current}. Нажмите «Показать», чтобы посмотреть, что нового.",
                NotificationCategory.AppUpdates,
                reopen: () => Views.TextViewDialog.Show(Application.Current?.MainWindow, $"Что нового в v{current}", body),
                reopenIsModal: true);
        }

        // Свёрнутый в трей старт (Настройки → Общие → «Запускать свёрнутым») — модальное окно не
        // показываем, чтобы оно не выскакивало поверх того, что для пользователя выглядит как «программа
        // тихо сидит в трее»: запись в журнал/историю уже сделана выше, этого достаточно.
        if (_services.Cfg.AppStartMinimized()) return;

        if (string.IsNullOrWhiteSpace(notes)) return; // сети нет / релиза с таким тегом нет — не критично, просто не показываем

        Views.TextViewDialog.Show(Application.Current?.MainWindow, $"Что нового в v{current}", notes);
    }

    // ── Firmware updates ─────────────────────────────────────────────────────

    private async Task CheckForFirmwareUpdatesAsync()
    {
        List<FirmwareUpdateInfo> updates;
        try
        {
            updates = FirmwareUpdateService.GetAvailableUpdates(_services.Db);
        }
        catch
        {
            // Same reasoning as the app-update check: a background scan shouldn't ever surface
            // an error dialog on startup — the user can still see everything via Search/История.
            return;
        }
        if (updates.Count == 0) return;

        var autoOnes = updates.Where(u => _services.Cfg.IsFwAutoUpdate(u.LocalDir)).ToList();
        var manualOnes = updates.Except(autoOnes).ToList();

        // A firmware marked for auto-update (Настройки → Прошивки → «Обновлять автоматически»)
        // failing here used to vanish completely: it's not in manualOnes (so no banner either), the
        // count below only reports successes, and this whole scan already runs silently on success
        // by design — same shape as the app auto-update Round 35 bug (a background auto-действие
        // failing with zero trace anywhere). Same "only notify on the transition" rule as
        // PushConfigNow: a share that's briefly unreachable doesn't spam a toast on every tick, but a
        // firmware that's been silently stuck out of date for hours/days is no longer invisible.
        var autoUpdated = 0;
        var autoFailed = new List<string>();
        if (autoOnes.Count > 0)
        {
            // Копирование с сетевого диска — в фоновом потоке и с прогрессом снизу: раньше это
            // молча вешало окно на всё время, пока тянулись все автообновляемые прошивки.
            using var busy = Busy.Begin("Обновление прошивок…");
            for (int i = 0; i < autoOnes.Count; i++)
            {
                var u = autoOnes[i];
                busy.Text = $"Обновление прошивки: {u.Name}";
                busy.Report(i, autoOnes.Count);
                try
                {
                    var source = SearchService.ToHierarchyResult(u.Latest, localRoot: _services.Cfg.RootPath());
                    await Task.Run(() => FirmwareSync.CopyToLocal(source));
                    autoUpdated++;
                }
                catch (Exception ex) { autoFailed.Add($"{u.Name}: {ex.Message}"); }
            }
        }
        if (autoUpdated > 0)
            ShowStatus($"Автоматически обновлено прошивок: {autoUpdated}", 6000, NotificationCategory.FirmwareAndParams);
        if (autoFailed.Count > 0)
        {
            if (!_fwAutoUpdateLastFailed)
            {
                _fwAutoUpdateLastFailed = true;
                AddNotification($"Автообновление прошивок не удалось ({autoFailed.Count}): {string.Join("; ", autoFailed.Take(3))}", NotificationCategory.FirmwareAndParams);
            }
        }
        else if (_fwAutoUpdateLastFailed)
        {
            _fwAutoUpdateLastFailed = false;
        }

        _pendingFwUpdates = manualOnes;
        if (manualOnes.Count == 0) return;
        if (!_services.Cfg.IsNotificationCategoryEnabled(NotificationCategory.FirmwareAndParams)) return;

        FwUpdateBannerText = $"Доступно обновление прошивок: {manualOnes.Count}";
        FwUpdateBannerVisible = true;
        AddNotification(FwUpdateBannerText, NotificationCategory.FirmwareAndParams, reopen: () => FwUpdateBannerVisible = true);
        ScheduleBannerAutoHide(() => FwUpdateBannerVisible = false);
    }

    [RelayCommand]
    private async Task UpdateAllFw()
    {
        // A manual, explicitly-clicked action — same per-item error surfacing as
        // FirmwareUpdatesWindow.ApplyUpdate (the "Показать"/details path for the same banner), which
        // already tells the operator exactly which firmware and why instead of just under-counting.
        var count = 0;
        var stillPending = new List<FirmwareUpdateInfo>();
        var failedMessages = new List<string>();
        var pending = _pendingFwUpdates.ToList();
        using (var busy = Busy.Begin("Обновление прошивок…"))
        {
            for (int i = 0; i < pending.Count; i++)
            {
                var u = pending[i];
                busy.Text = $"Обновление прошивки: {u.Name}";
                busy.Report(i, pending.Count);
                try
                {
                    var source = SearchService.ToHierarchyResult(u.Latest, localRoot: _services.Cfg.RootPath());
                    await Task.Run(() => FirmwareSync.CopyToLocal(source));
                    count++;
                }
                catch (Exception ex)
                {
                    stillPending.Add(u);
                    failedMessages.Add($"{u.Name}: {ex.Message}");
                }
            }
        }
        _pendingFwUpdates = stillPending;
        FwUpdateBannerText = stillPending.Count > 0 ? $"Доступно обновление прошивок: {stillPending.Count}" : FwUpdateBannerText;
        FwUpdateBannerVisible = stillPending.Count > 0;
        ShowStatus($"Обновлено прошивок: {count}", 6000, NotificationCategory.FirmwareAndParams);
        if (failedMessages.Count > 0)
            AddNotification($"Не удалось обновить ({failedMessages.Count}): {string.Join("; ", failedMessages.Take(3))}", NotificationCategory.FirmwareAndParams);
        RefreshSearchIfActive();
    }

    [RelayCommand]
    private void ShowFwUpdatesDetails()
    {
        // Немодально: обновление тянет с шары сотни мегабайт на версию, и всё это время программой
        // надо пользоваться. Второе окно не заводим — в открытом уже идёт своя пачка.
        if (_fwUpdatesWindow is { IsLoaded: true } opened)
        {
            if (opened.WindowState == WindowState.Minimized) opened.WindowState = WindowState.Normal;
            opened.Activate();
            return;
        }

        var win = new FirmwareUpdatesWindow(_services, _pendingFwUpdates) { Owner = Application.Current.MainWindow };
        _fwUpdatesWindow = win;
        win.Closed += (_, _) =>
        {
            _fwUpdatesWindow = null;
            OnFwUpdatesWindowClosed(win);
        };
        win.Show();
    }

    /// <summary>Открытое окно обновления прошивок. Появилось вместе с немодальностью: раньше «нажали
    /// второй раз» было невозможно, пока окно держало программу.</summary>
    private Views.FirmwareUpdatesWindow? _fwUpdatesWindow;

    /// <summary>Пересчёт после закрытия окна обновлений. Раньше это шло сразу за ShowDialog(); теперь
    /// окно возвращает управление немедленно, и пересчитывать надо по его закрытию.</summary>
    private void OnFwUpdatesWindowClosed(Views.FirmwareUpdatesWindow win)
    {
        if (win.UpdatedCount > 0)
            RefreshSearchIfActive();

        // Recompute — the window may have updated some rows and/or flipped auto-update flags.
        _pendingFwUpdates = FirmwareUpdateService.GetAvailableUpdates(_services.Db)
            .Where(u => !_services.Cfg.IsFwAutoUpdate(u.LocalDir))
            .ToList();
        if (_pendingFwUpdates.Count == 0)
        {
            FwUpdateBannerVisible = false;
        }
        else
        {
            FwUpdateBannerText = $"Доступно обновление прошивок: {_pendingFwUpdates.Count}";
        }
    }

    [RelayCommand]
    private void DismissFwUpdateBanner() => FwUpdateBannerVisible = false;

    // ── Unknown files/folders (Task 3 — see HierarchyService.ScanUnknownFiles) ──────────────────

    /// <summary>Piggybacks on the same periodic tick as the rest of RunSync (startup + every
    /// sync_interval_min) — same reasoning as CleanupInspectionFolder/EnsureHierarchy above.
    /// ScanUnknownFiles is read-only (nothing gets moved/deleted here, unlike EnsureHierarchy's own
    /// top-level auto-move) — the operator decides what happens to each item via
    /// ShowUnknownItemsDetails, one at a time or in bulk.</summary>
    private async Task CheckForUnknownItemsAsync()
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root)) return;

        List<UnknownEntry> unknown;
        try
        {
            // Имена справочников — из БД здесь, обход диска — в фоне: см. HierarchyService, блок
            // про двухфазные операции (соединение SQLite одно и не потокобезопасно).
            var names = _services.Hierarchy.SnapshotNames();
            using var busy = Busy.Begin("Проверка диска на неизвестные файлы…");
            unknown = await Task.Run(() =>
                System.IO.Directory.Exists(root) ? HierarchyService.ScanUnknownFiles(root, names) : null!);
            if (unknown is null) return;
        }
        catch { return; } // best effort — flaky network mount, next tick retries

        _pendingUnknownItems = unknown;
        if (unknown.Count == 0)
        {
            UnknownItemsBannerVisible = false;
            return;
        }
        if (!_services.Cfg.IsNotificationCategoryEnabled(NotificationCategory.Hierarchy)) return;

        UnknownItemsBannerText = $"Обнаружены неизвестные файлы/папки на диске: {unknown.Count}";
        UnknownItemsBannerVisible = true;
        // Same text as last time (nothing changed since) — AddNotification's own dedup just bumps
        // the timestamp instead of piling up identical history rows.
        // reopen из истории уведомлений открывает подробности (ShowUnknownItemsDetails) МОДАЛЬНО поверх
        // окна уведомлений (reopenIsModal: true) — оно не закрывается, после закрытия подробностей
        // оператор остаётся в списке уведомлений. ShowUnknownItemsDetails сам ПЕРЕ-СКАНИРУЕТ диск перед
        // показом и молча выходит, если неизвестных уже нет (пользователь мог их разрешить), поэтому
        // «Показать» не воскрешает устаревшую плашку — как и прежде.
        AddNotification(UnknownItemsBannerText, NotificationCategory.Hierarchy, reopen: () => _ = ShowUnknownItemsDetails(), reopenIsModal: true);
    }

    [RelayCommand]
    private async Task ShowUnknownItemsDetails()
    {
        // Re-scan right before showing, rather than trust whatever _pendingUnknownItems still holds
        // from the last periodic tick (up to sync_interval_min minutes old, default 5) — on a shared
        // network drive another machine (or this app's own auto-cleanup, see EnsureHierarchy) could
        // have already moved/removed an item by the time the operator gets around to clicking
        // "Показать". A live rescan is what the manual Настройки → Иерархия scan button already did;
        // the notification path deserves the same freshness guarantee.
        await CheckForUnknownItemsAsync();
        if (_pendingUnknownItems.Count == 0) return;

        var dlg = new UnknownFilesDialog(_services, _services.Cfg.RootPath(), _pendingUnknownItems) { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();

        // Re-scan again afterwards rather than trust the dialog's own bookkeeping — a reassign/move/
        // delete can fail partway through (see UnknownFilesDialog's per-item error handling), and the
        // disk is the single source of truth for what's still actually unresolved.
        await CheckForUnknownItemsAsync();
    }

    [RelayCommand]
    private void DismissUnknownItemsBanner() => UnknownItemsBannerVisible = false;

    /// <summary>Реальные данные поиска изменились (применён общий конфиг, обновлены прошивки) — метим
    /// выдачу устаревшей и, если вкладка активна, тут же перезапускаем. Пометка нужна, чтобы обычный
    /// возврат на вкладку выдачу НЕ трогал (см. SearchView.RefreshIfActive), а вот после настоящих
    /// правок она всё же обновилась.</summary>
    private void RefreshSearchIfActive()
    {
        if (_pageCache.TryGetValue("search", out var page) && page is SearchView searchView)
        {
            searchView.MarkResultsDirty();
            searchView.RefreshIfActive();
        }
    }

    // ── Shared config sync (Настройки → Общие → Экспорт/Импорт) ─────────────

    /// <summary>Every role auto-pulls the shared config on this interval — no confirmation click,
    /// per the operator's decision that naladchik/programmer should just stay in sync in the
    /// background. Applied silently: no banner, no notification-history entry (previously fired on
    /// every tick that found a real diff, which — with several people pushing firmware/config changes
    /// throughout the day — read as constant spam). The result is only visible as a passive "last
    /// synced" timestamp on the Сетевые диски page (NetworkSyncView.RefreshIfActive), which the user
    /// can check whenever they care, rather than being interrupted by it.</summary>
    private async Task CheckForConfigUpdateAsync()
    {
        // Тикеты тянем тем же фоном (независимо от гейта конфига ниже — у SyncTicketsNow свой
        // guard): раньше о новом тикете узнавали, только зайдя на страницу «Тикеты».
        SyncTicketsNow();

        if (_configSyncRunning) return; // тик пришёл, пока предыдущий ещё тянет диск — просто пропускаем
        _configSyncRunning = true;
        using var activity = BeginSyncActivity("проверка обновлений на диске");
        try
        {
            SharedConfigSnapshot? snapshot;
            ConfigUpdateInfo? info;
            string? error;
            (info, error, snapshot) = await ConfigSyncService.CheckForUpdateAsync(_services);

            if (error is not null)
            {
                // Root reachable but reading/parsing the shared config itself failed — worth telling
                // the user, unlike an unreachable share (already covered by DiskStatusText) or "no
                // update yet", which stay silent. The app keeps running on the local copy regardless.
                NoteSyncOutcome($"Не удалось проверить обновление конфига: {error}", isError: true);
                ShowStatus($"Не удалось проверить обновление конфига: {error}", 8000, NotificationCategory.Sync);
                return;
            }
            if (info is null || snapshot is null) { NoteSyncOutcome(null, isError: false); return; }

            var root = _services.Cfg.RootPath();
            using (BeginSyncActivity("приём справочника и прошивок"))
                await ConfigSyncService.ApplyAsync(_services, snapshot, root);

            ReloadSidebarApps();
            RefreshSearchIfActive();
            CheckForHierarchyConflicts();
            ShowIncomingChangesBanner(info);
            // Другая машина могла тем временем отправить экспорт (и тем самым очистить СВОЙ
            // накопитель) — здесь же дешёвый повод перечитать и наш собственный счётчик, а не только
            // после PushCatalogChange/PushConfigNow.
            RefreshPendingChangesBanner();
            NoteSyncOutcome($"Применён конфиг с диска (изменений: {info.Changes?.Count ?? 0})", isError: false);
        }
        catch (Exception e)
        {
            NoteSyncOutcome($"Сбой синхронизации конфига: {e.Message}", isError: true);
        }
        finally
        {
            _configSyncRunning = false;
        }
    }

    /// <summary>Задача 4 (приёмник) / 5 (уровни применения): аддитивные изменения и удаления к этому
    /// моменту УЖЕ применены (см. Apply выше) — не спрашиваем, применять ли их (п.5: "где спрашивать
    /// бессмысленно, не спрашиваем" — удаления обязаны применяться всегда, иначе «мусор воскресает»,
    /// аддитивные безопасны сами по себе), но и не молчим о них: эта плашка перечисляет, ЧТО именно
    /// применилось — источник текста — журнал изменений маркера ревизии (ConfigUpdateInfo.Changes,
    /// человекочитаемые описания, см. SyncRevisionMarker), а не сырой ImportCounts diff. Конфликты
    /// правки одного поля на двух машинах сюда не попадают — они уже обработаны отдельно, через
    /// ConflictResolutionDialog (см. CheckForHierarchyConflicts выше, которая вызывается раньше).
    /// Критическое расхождение версии схемы конфига — отдельное громкое уведомление (тоже
    /// неблокирующее, п.5: "применяется принудительно с уведомлением").</summary>
    private void ShowIncomingChangesBanner(ConfigUpdateInfo info)
    {
        if (info.Changes is { Count: > 0 } changes && _services.Cfg.IsNotificationCategoryEnabled(NotificationCategory.Sync))
        {
            // Повторы схлопываются: одна и та же правка справочника попадает в журнал столько раз,
            // сколько экспортов её унесло, и перечислять её пять раз подряд бессмысленно.
            var unique = changes
                .GroupBy(c => c.Description, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Count() > 1 ? $"{g.Key} (×{g.Count()})" : g.Key)
                .ToList();
            var preview = string.Join("; ", unique.Take(3));
            var more = unique.Count > 3 ? $" и ещё {unique.Count - 3}" : "";
            IncomingChangesBannerText = $"Поступили изменения с общего диска ({unique.Count}): {preview}{more}";
            // Полный список — по кнопке «Показать»: раньше в плашку влезало пять описаний, а остальные
            // существовали только числом «и ещё 45», посмотреть их было негде вообще.
            _incomingChangesDetails = string.Join(Environment.NewLine, changes.Select(FormatChangeEntry));
            IncomingChangesBannerVisible = true;
            // reopen из истории открывает полный список изменений МОДАЛЬНО поверх окна уведомлений
            // (ShowIncomingChangesDetails → TextViewDialog), а не показывает плашку на главном окне за
            // модальным окном истории: окно уведомлений при этом НЕ закрывается, после закрытия
            // подробностей оператор остаётся в списке и может открыть следующее уведомление.
            AddNotification(IncomingChangesBannerText, NotificationCategory.Sync, reopen: ShowIncomingChangesDetails, reopenIsModal: true);
            // НЕ автоскрывать: пользователь жаловался, что плашка исчезала раньше, чем он успевал
            // посмотреть, ЧТО именно поменялось. Теперь висит, пока он сам не нажмёт «Показать»
            // (полный список, ShowIncomingChangesDetails) и/или не закроет её (DismissIncomingChangesBanner).
        }

        if (info.CriticalSchemaMismatch)
            ShowStatus("Критическое расхождение версии схемы общего конфига — применено принудительно. Проверьте, что у всех коллег установлена одна версия приложения.",
                15000, NotificationCategory.Sync);
    }

    /// <summary>Полный список поступивших изменений — то, что показывает кнопка «Показать» на плашке.
    /// Хранится строкой, а не коллекцией: показывается он одним читаемым текстом (см. TextViewDialog).</summary>
    private string _incomingChangesDetails = "";

    private static string FormatChangeEntry(SyncChangeEntry c)
    {
        var when = c.Ts.Length >= 16 ? c.Ts[..16].Replace('T', ' ') : c.Ts;
        var who = string.IsNullOrWhiteSpace(c.Author) ? "" : $"  ·  {c.Author}";
        return $"{when}{who}\n    {c.Description}";
    }

    [RelayCommand]
    private void ShowIncomingChangesDetails() =>
        Views.TextViewDialog.Show(System.Windows.Application.Current?.MainWindow,
            "Поступившие изменения",
            string.IsNullOrEmpty(_incomingChangesDetails) ? "Список изменений пуст." : _incomingChangesDetails);

    [RelayCommand]
    private void DismissIncomingChangesBanner() => IncomingChangesBannerVisible = false;

    // ── Плашка-накопитель изменений, готовых к отправке (Задача 4, отправитель) ─────────────────

    private void RefreshPendingChangesBanner()
    {
        var pending = _services.Db.GetSyncPendingChanges();

        // «Добавил тип шкафа и тут же удалил» (или переименовал/сменил префикс и вернул обратно) —
        // накопитель хранит по строке-описанию на каждую правку, поэтому показывал бы «добавлен» И
        // «удалён», намекая на 2 исходящих изменения, хотя справочник вернулся ровно к тому состоянию,
        // что уже на диске. Сверяем сигнатуру синхронизируемого содержимого с базовой (последняя
        // отправка/приём): совпала — отправлять реально нечего, чистим накопитель. База может
        // отсутствовать (машина ещё ни разу не отправляла/не принимала конфиг) — тогда не трогаем.
        if (pending.Count > 0)
        {
            var baseline = _services.Cfg.Get(Services.ConfigSyncService.ContentSignatureKey);
            if (!string.IsNullOrEmpty(baseline) &&
                Services.ConfigSyncService.ComputeContentSignature(_services) == baseline)
            {
                _services.Db.ClearSyncPendingChanges();
                pending = _services.Db.GetSyncPendingChanges();
            }
        }

        PendingChangesCount = pending.Count;
        PendingChangesBannerVisible = pending.Count > 0;
        PendingChangesSummary = pending.Count == 0 ? "" : $"Изменений готово к отправке: {pending.Count}";

        // Накопитель (в т.ч. правки тегов) — это и есть «неотправленное» для пилюли статуса синхры.
        RefreshSyncStatus();
    }

    /// <summary>Полный список готовых к отправке правок — открывается отдельным окном (TextViewDialog),
    /// как и «Показать» у входящих изменений. Раньше кнопка разворачивала список ВНУТРИ самой плашки
    /// (ещё одной плашкой ниже) — пользователь просил вынести в отдельное окно.</summary>
    [RelayCommand]
    private void ShowPendingChangesDetails()
    {
        var pending = _services.Db.GetSyncPendingChanges();
        var text = pending.Count == 0
            ? "Изменений нет."
            : string.Join(Environment.NewLine, pending.Select(c =>
            {
                var when = c.Ts.Length >= 16 ? c.Ts[..16].Replace('T', ' ') : c.Ts;
                var who = string.IsNullOrWhiteSpace(c.Author) ? "" : $"  ·  {c.Author}";
                return $"{when}{who}\n    {c.Description}";
            }));
        Views.TextViewDialog.Show(System.Windows.Application.Current?.MainWindow, "Готово к отправке", text);
    }

    /// <summary>Неисчезающая по дизайну (п.4) — в отличие от остальных баннеров скрытие здесь не
    /// очищает накопитель, только прячет саму плашку до следующего изменения (следующий
    /// PushCatalogChange или входящий тик снова покажет её, пока в таблице что-то есть).</summary>
    [RelayCommand]
    private void DismissPendingChangesBanner() => PendingChangesBannerVisible = false;

    [RelayCommand]
    private async Task SendPendingChangesNow()
    {
        if (CurrentRole != "administrator")
        {
            // Полный экспорт — привилегия администратора (см. PushCatalogChangeAsync ниже за тем же
            // объяснением) — у остальных ролей накопитель просто ждёт, пока администратор отправит
            // свой собственный экспорт (тот подхватит всё текущее состояние локальной БД целиком).
            ShowStatus("Отправка на диск доступна только администратору — изменения останутся в очереди", 8000, NotificationCategory.Sync);
            return;
        }

        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root))
        {
            ShowStatus("Сетевой диск недоступен — изменения останутся в очереди", 8000, NotificationCategory.Sync);
            return;
        }

        try
        {
            using var activity = BeginSyncActivity("отправка изменений на диск");
            // Забрать чужое перед тем как отдать своё — тот же порядок, что PushCatalogChangeAsync
            // (Export перезаписывает ВЕСЬ общий снимок, отправка без предварительного приёма рискует
            // затереть чужие изменения).
            await CheckForConfigUpdateAsync();
            var descriptions = _services.Db.GetSyncPendingChanges().Select(c => c.Description).ToList();
            await ConfigSyncService.ExportAsync(_services, root, $"{_services.CurrentUserName} ({RoleLabel})", descriptions);
            RefreshPendingChangesBanner();
            ShowStatus("Изменения отправлены на диск", category: NotificationCategory.Sync);
        }
        catch (Exception ex)
        {
            ShowStatus($"Не удалось отправить изменения: {ex.Message}", 12000, NotificationCategory.Sync);
        }
    }

    /// <summary>Surfaces held-back hierarchy conflicts (see Database.ClassifyHierarchyChange) the same
    /// way the unknown-items/firmware-update banners work: a passive banner with a "Показать" button,
    /// not a forced modal popup that interrupts whatever the operator is doing mid-tick. Called after
    /// every silent auto-pull (CheckForConfigUpdate) — NetworkSyncView's manual "Синхронизировать
    /// сейчас" instead opens the resolution dialog directly, since that's already a deliberate,
    /// blocking action.</summary>
    private void CheckForHierarchyConflicts()
    {
        var count = _services.Db.PendingHierarchyConflictCount();
        if (count == 0)
        {
            HierarchyConflictBannerVisible = false;
            return;
        }
        if (!_services.Cfg.IsNotificationCategoryEnabled(NotificationCategory.Sync)) return;

        HierarchyConflictBannerText = $"Конфликты синхронизации, требуют решения: {count}";
        HierarchyConflictBannerVisible = true;
        // reopen из истории открывает диалог разрешения конфликтов МОДАЛЬНО поверх окна уведомлений
        // (ShowHierarchyConflictsDetails → ConflictResolutionDialog), а не показывает плашку за модальным
        // окном истории: окно уведомлений НЕ закрывается, после закрытия диалога оператор остаётся в списке.
        AddNotification(HierarchyConflictBannerText, NotificationCategory.Sync, reopen: ShowHierarchyConflictsDetails, reopenIsModal: true);
    }

    [RelayCommand]
    private void ShowHierarchyConflictsDetails()
    {
        var pending = _services.Db.GetPendingHierarchyConflicts();
        if (pending.Count == 0)
        {
            HierarchyConflictBannerVisible = false;
            return;
        }

        var dlg = new ConflictResolutionDialog(_services, pending) { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();

        if (dlg.ResolvedCount > 0)
        {
            ShowStatus($"Разрешено конфликтов синхронизации: {dlg.ResolvedCount}", 6000, NotificationCategory.Sync);
            ReloadSidebarApps();
            RefreshSearchIfActive();
        }
        CheckForHierarchyConflicts();
    }

    [RelayCommand]
    private void DismissHierarchyConflictsBanner() => HierarchyConflictBannerVisible = false;

    private async Task RunSyncAsync()
    {
        if (_syncRunning) return;
        _syncRunning = true;
        try
        {
            await RefreshDiskStatusAsync();
            RefreshModerationBadge();

            try
            {
                var expired = _services.Db.ExpireStaleReservations();
                if (expired > 0) ShowStatus($"Просрочено резервов номеров: {expired} (номера пропущены навсегда)", 8000, NotificationCategory.FirmwareAndParams);
            }
            catch { /* best effort — next tick will retry */ }

            await ScanDiskForNewFirmwareAsync();
            await CleanupInspectionFolderAsync();
            await CheckForUnknownItemsAsync();
        }
        finally
        {
            _syncRunning = false;
        }
    }

    /// <summary>Досмотр сетевого диска на предмет версий, которых нет в локальной базе.
    ///
    /// Почему это здесь появилось: прошивки, загруженные коллегой, физически лежат на общем диске, но
    /// в базу этой машины попадали ровно одним путём — через общий конфиг, который ОТПРАВЛЯЕТ только
    /// администратор и по умолчанию не отправляет вовсе (config_push_interval_min = 0). Сам обход
    /// диска (HierarchyService.SyncFwFromDisk) вызывался только внутри применения нового конфига —
    /// то есть если конфиг никто не выкладывал, он не запускался НИКОГДА. Отсюда и жалоба: «позагружал
    /// прошивки на компе коллеги, у себя не вижу» — файлы на диске были, показать их было некому.
    /// Теперь диск досматривается сам, тем же периодическим тиком, что и остальная синхронизация, и
    /// от чужих настроек отправки не зависит.
    ///
    /// Фазы те же, что везде: план по БД → обход диска в фоновом потоке → запись результата по БД
    /// (см. блок про двухфазные операции в HierarchyService).</summary>
    private async Task ScanDiskForNewFirmwareAsync()
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root)) return;

        try
        {
            // Заодно тем же тиком чистим осиротевшие ярлыки версий: файлы прошивки удалили прямо на
            // диске (мимо программы), а ярлык доп.подтипа на них так и висел (жалоба «корневой файл
            // исчез, а ярлык не исчезает»). Обход диска best-effort и не мешает поиску новых версий —
            // выполняется до него и в фоне; недоступный корень PruneOrphanedFirmwareShortcuts просто
            // пропускает, чтобы offline-шара не выглядела как «всё пропало».
            var pruned = await Task.Run(() => _services.Hierarchy.PruneOrphanedFirmwareShortcuts(root));
            if (pruned.Removed > 0) RefreshSearchIfActive();

            var plan = _services.Hierarchy.PlanFwSync(root);
            FwDiskScan scan;
            using (Busy.Begin("Поиск новых прошивок на диске…"))
                scan = await Task.Run(() => HierarchyService.ScanFwDisk(plan));

            if (scan.Candidates.Count == 0) return;

            var result = _services.Hierarchy.ImportFwCandidates(scan);
            if (result.Added <= 0) return;

            var preview = string.Join(", ", result.AddedItems.Take(3));
            var more = result.AddedItems.Count > 3 ? $" и ещё {result.AddedItems.Count - 3}" : "";
            ShowStatus($"Найдено новых прошивок на диске: {result.Added} ({preview}{more})",
                10000, NotificationCategory.FirmwareAndParams);
            RefreshSearchIfActive();
        }
        catch { /* best effort — повторится на следующем тике, как и остальные шаги RunSync */ }
    }

    /// <summary>Auto-deletes files older than ConfigService.InspectionAutoCleanupMinutes() from the
    /// Осмотр folder — 0 (default) means disabled, see ConfigService for why. Piggybacks on the same
    /// timer as the rest of RunSync (startup + every sync_interval_min) instead of a dedicated timer,
    /// same reasoning as EnsureHierarchy/ExpireStaleReservations above: one more periodic background
    /// check, not a whole new schedule.</summary>
    private async Task CleanupInspectionFolderAsync()
    {
        var minutes = _services.Cfg.InspectionAutoCleanupMinutes();
        if (minutes <= 0) return;

        var folder = _services.Cfg.Get("inspection_folder");
        if (string.IsNullOrEmpty(folder)) return;

        try
        {
            var now = DateTime.Now;
            // Журнал «когда файл впервые увидели в этой папке» — без него возраст считался по дате
            // изменения файла, и снимок, скопированный в папку со стороны (у него дата своя, старая),
            // сносило первым же тиком (см. InspectionSeenLedger). Журнал машинно-локальный, лежит
            // рядом с базой, а не в самой папке осмотра — там ему делать нечего.
            var ledgerPath = System.IO.Path.Combine(ConfigService.AppData, "inspection_seen.json");
            var result = await Task.Run(() =>
                System.IO.Directory.Exists(folder)
                    ? InspectionCleanupService.Cleanup(folder, minutes, now, InspectionSeenLedger.Load(ledgerPath))
                    : null);
            if (result is null || result.DeletedCount == 0) return;

            var preview = string.Join(", ", result.DeletedNames.Take(3));
            var more = result.DeletedNames.Count > 3 ? $" и ещё {result.DeletedNames.Count - 3}" : "";
            ShowStatus($"Автоочистка папки осмотра: удалено файлов старше {InspectionCleanupService.FormatAge(minutes)} — {result.DeletedCount} ({preview}{more})",
                8000, NotificationCategory.Inspection);
        }
        catch { /* best effort — next tick will retry */ }
    }

    private async Task EnsureHierarchyAsync()
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root)) return;

        // План — по БД (быстро, здесь), создание папок на сетевом диске — в фоне. Сотни CreateDirectory
        // по медленной шаре и были одной из тех «программа не отвечает при запуске» пауз.
        var plan = _services.Hierarchy.PlanStructure(root);
        EnsureStructureResult result;
        // Это главная точка, где папки «Инструкция» вообще появляются на диске (проверка структуры
        // идёт при каждом запуске), поэтому и заглушка «Инструкция в разработке» кладётся здесь:
        // пустая папка неотличима от «инструкцию потеряли». Настоящий документ не затирается — см.
        // InstructionStub.
        var stubs = _services.StubWriter();
        using (Busy.Begin("Проверка структуры диска…"))
            result = await Task.Run(() => HierarchyService.ApplyStructurePlan(plan, stubs));

        if (result.CreatedCount > 0)
            ShowStatus($"Структура диска создана: {result.CreatedCount} папок", 6000, NotificationCategory.Sync);
        // EnsureStructure also auto-moves top-level unrecognised names into «Неизвестное» — this used
        // to happen completely silently (MovedCount was computed but never surfaced anywhere), which
        // meant a folder could vanish from where the operator expected it with zero explanation. The
        // moved items themselves are then picked up by the very next CheckForUnknownItems tick (they
        // live in «Неизвестное», which ScanUnknownFiles treats as a known/skip name — but this status
        // line is the only place their *disappearance* from the original spot gets explained at all).
        if (result.MovedCount > 0)
            ShowStatus($"Перенесено в «Неизвестное» при проверке структуры диска: {result.MovedCount}", 8000, NotificationCategory.Hierarchy);
    }

    // ── IAppHost ──────────────────────────────────────────────────────────────

    /// <summary>Recomputes only the footer disk indicator. Extracted from RunSync so callers that
    /// change the root path or write to the disk can refresh it on demand instead of leaving it
    /// stale until the next periodic RunSync tick — which, with sync_interval_min=0, never comes.</summary>
    public void RefreshDiskStatus() => _ = RefreshDiskStatusAsync();

    /// <summary>Пересчёт индикатора — это рекурсивный обход ВСЕГО сетевого диска (EnumerateFiles по
    /// всем подпапкам), самая заметная из «программа зависла» пауз: на общей шаре он занимает
    /// секунды. Считаем в фоновом потоке, в БД при этом не ходим вообще.</summary>
    public async Task RefreshDiskStatusAsync()
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root))
        {
            DiskStatusText = "Диск: ✗ недоступен";
            return;
        }

        using var busy = Busy.Begin("Проверка диска…");
        DiskStatusText = await Task.Run(() =>
        {
            try
            {
                if (!System.IO.Directory.Exists(root)) return "Диск: ✗ недоступен";
                var fileCount = System.Linq.Enumerable.Count(
                    System.IO.Directory.EnumerateFiles(root, "*", System.IO.SearchOption.AllDirectories));
                return $"Диск: ✓  ({fileCount} файлов)";
            }
            catch
            {
                // Шара отвалилась посреди обхода — для индикатора это то же самое, что «недоступен».
                return "Диск: ✗ недоступен";
            }
        });
    }

    public void OnRootPathChanged() => _ = OnRootPathChangedAsync();

    private async Task OnRootPathChangedAsync()
    {
        // Same order StartTimers uses (structure first, then status) — EnsureHierarchy may create the
        // tree, which changes the file count RefreshDiskStatus reports.
        await EnsureHierarchyAsync();
        await RefreshDiskStatusAsync();
        // Наблюдатель за revision.json (Задача 3) смотрит на конкретную папку конкретного root —
        // путь сменился, пересоздаём его на новом месте (или гасим, если новый путь пуст).
        SetupConfigWatcher();
    }

    public void ReloadSidebarApps()
    {
        QuickApps.Clear();
        foreach (var app in _services.Cfg.QuickApps())
            QuickApps.Add(new QuickAppItem(app.Name, app.Path));

        var mode = _services.Cfg.QuickAppsDisplayMode();
        var onTop = mode is "top" or "top_labeled";
        var hasApps = QuickApps.Count > 0;
        QuickAppsSidebarVisible = hasApps && !onTop;
        QuickAppsTopVisible = hasApps && onTop;
        QuickAppsTopShowLabels = mode == "top_labeled";
    }

    void IAppHost.Navigate(string pageId, string? section)
    {
        Navigate(pageId);
        if (section is null) return;
        if (_pageCache.TryGetValue(pageId, out var page) && page is HostingView hosting)
            hosting.ShowSection(section);
    }

    void IAppHost.InvalidateSearchResults()
    {
        if (_pageCache.TryGetValue("search", out var page) && page is SearchView searchView)
            searchView.MarkResultsDirty();
    }

    public IBusyScope BeginBusy(string text) => Busy.Begin(text);

    /// <summary>Only the administrator gets an auto-push timer — everyone else just pulls (see
    /// StartTimers/_configPullRepeatTimer above). Safe to call any time (role switch, or right
    /// after NetworkSyncView saves config_push_interval_min) since it always stops any previous
    /// timer before deciding whether to start a new one. No separate on/off checkbox — the interval
    /// alone carries that (0 = disabled), same pattern as sync_interval_min/inspection auto-cleanup.</summary>
    public void RefreshConfigSync()
    {
        _configPushTimer?.Stop();
        _configPushTimer = null;
        if (CurrentRole != "administrator") return;

        var minutes = _services.Cfg.ConfigPushIntervalMin();
        if (minutes <= 0) return; // 0 = auto-push disabled — see footnote in NetworkSyncView

        _configPushTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
        _configPushTimer.Tick += async (_, _) => await PushConfigNowAsync();
        _configPushTimer.Start();
    }

    /// <summary>Изменение ОБЩЕГО справочника (тип/подтип шкафа, контроллер, производитель ПЧ/УПП,
    /// тег, расширение) кладётся в накопитель и ждёт кнопки «Отправить всё» на плашке — само по себе
    /// на сетевой диск оно не уезжает.
    ///
    /// Раньше администратору отправка делалась прямо здесь, сразу после правки. Это решало исходную
    /// проблему («добавил производителя — у коллег его нет»: приём чужого конфига включён у всех по
    /// умолчанию, а отправка — только у администратора и по умолчанию выключена,
    /// config_push_interval_min = 0), но заодно лишало его контроля: добавил несколько
    /// производителей — каждый улетел отдельным экспортом, а плашка со списком того, что отправлено,
    /// пропадала практически сразу, потому что накопитель тут же очищался. Отправить пачкой,
    /// посмотреть перед отправкой, что именно уйдёт, или передумать было нечем.
    ///
    /// Теперь исходная проблема закрыта плашкой-накопителем (RefreshPendingChangesBanner): она видна
    /// всегда, пока в очереди что-то есть, переживает перезапуск приложения (очередь в БД) и прямо
    /// называет число неотправленных изменений — не заметить её и «не знать про отправку» уже нельзя.
    /// Сама отправка — SendPendingChangesNow, там же и порядок «сначала забрать, потом отдать».
    ///
    /// Автоотправка по таймеру (PushConfigNowAsync), если администратор её включил, по-прежнему
    /// уносит накопленное сама — эта настройка не менялась.</summary>
    public void PushCatalogChange(string what, string subjectKey = "") => _ = PushCatalogChangeAsync(what, subjectKey);

    private Task PushCatalogChangeAsync(string what, string subjectKey)
    {
        // Накопитель (Database.SyncPendingChange) — и счётчик на плашке, и источник описаний для
        // журнала маркера ревизии при отправке (ExportAsync(changeDescriptions:)). subjectKey даёт
        // карточке выдачи точечную подсветку «правки этой прошивки ещё не на диске».
        _services.Db.AddSyncPendingChange("catalog", what, _services.CurrentUserName, subjectKey);
        RefreshPendingChangesBanner();

        // Полный экспорт разрешён только администратору (см. SendPendingChangesNow) — остальным
        // ролям обещать отправку нельзя, у них правка так и останется локальной.
        ShowStatus(CurrentRole == "administrator"
            ? $"{what}. Чтобы изменение увидели коллеги — «Отправить всё» на плашке сверху"
            : what, category: NotificationCategory.Hierarchy);
        return Task.CompletedTask;
    }

    /// <summary>Runs silently on SUCCESS — no status-bar toast — same reasoning as
    /// CheckForConfigUpdate: this fires every config_push_interval_min minutes, and a repeated toast
    /// for routine background activity reads as spam. ConfigSyncService.Export already persists
    /// config_last_pushed_at, which NetworkSyncView.RefreshIfActive surfaces passively whenever the
    /// user opens that page. A FAILURE is different: previously swallowed completely silently forever
    /// (a share that goes unreachable/read-only for hours meant the administrator's "every 1 minute"
    /// setting just quietly did nothing, with zero trace anywhere — colleagues only noticed their
    /// changes weren't reaching other machines, no evidence pointed at the push side at all). Only
    /// notifies on the state TRANSITION (first failure after a success, and recovery after failures)
    /// so a share that's down for an extended stretch doesn't spam one toast per tick.</summary>
    private async Task PushConfigNowAsync()
    {
        var root = _services.Cfg.RootPath();
        using var activity = BeginSyncActivity("отправка конфига на диск");
        try
        {
            var exportedBy = $"{_services.CurrentUserName} ({RoleLabel})";
            var descriptions = _services.Db.GetSyncPendingChanges().Select(c => c.Description).ToList();
            await ConfigSyncService.ExportAsync(_services, root, exportedBy, descriptions);
            RefreshPendingChangesBanner();
            NoteSyncOutcome(descriptions.Count > 0 ? $"Отправлено на диск (правок: {descriptions.Count})" : null, isError: false);
            if (_configPushLastFailed)
            {
                _configPushLastFailed = false;
                ShowStatus("Автоотправка конфига на диск восстановлена", 8000, NotificationCategory.Sync);
            }
        }
        catch (Exception ex)
        {
            NoteSyncOutcome($"Автоотправка конфига не удалась: {ex.Message}", isError: true);
            if (!_configPushLastFailed)
            {
                _configPushLastFailed = true;
                AddNotification($"Автоотправка конфига на диск не удалась: {ex.Message}", NotificationCategory.Sync);
            }
        }
    }
}
