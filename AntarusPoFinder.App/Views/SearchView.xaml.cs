using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Loader;
using AntarusPoFinder.Core.Services;

using AntarusPoFinder.App;

namespace AntarusPoFinder.App.Views;

public partial class SearchView : UserControl
{
    private readonly AppServices _services;
    private readonly IAppHost _host;
    private Dictionary<int, EquipmentSubType> _subtypesById = new();

    /// <summary>Exact query text (+ mode/exact-word) the layout-fallback question was already
    /// resolved for during this page instance's lifetime — see ConfirmLayoutFallback. Without this,
    /// every silent re-run of the SAME unchanged query (RefreshIfActive on tab switch, background
    /// config-sync ticks via MainWindowViewModel.RefreshSearchIfActive, closing an edit-tags dialog
    /// which calls PerformSearch() again) re-asked "это точно оно?" from scratch AND recorded a
    /// fresh yes/no vote each time — burning through LayoutFallbackDecisionThreshold's vote margin
    /// on repeated re-searches of text the operator never touched again, not genuinely new searches.
    /// Real bug report: answered "да" once, then just switching tabs asked again for the same typed
    /// text. Cleared whenever the actual query text changes (a new/different query is always asked
    /// fresh, exactly as before) — see PerformSearch.</summary>
    private string? _lastLayoutFallbackResolvedKey;
    private bool _lastLayoutFallbackResolvedYes;

    // Расширения программы ПЛК переехали в PlcOpenResolver (там же, где решается, что открывать);
    // здесь остались только расширения панели — по ним карточка понимает, что HMI вообще есть.
    private static readonly string[] KincoHmiExts = { ".dpj", ".emt", ".emtp", ".emsln" };

    /// <summary>Exposes specific named controls to OnboardingOverlay (MainWindow.ShowOnboarding) —
    /// x:Name fields are private to this partial class by default, so the tour can't reach them
    /// directly. Returns null for an unknown key rather than throwing, so a tour step silently
    /// skips instead of crashing if this ever falls out of sync with the tour's step list.</summary>
    public FrameworkElement? OnboardingTarget(string key) => key switch
    {
        "input" => SearchInput,
        "mode" => ModeSelectorPanel,
        _ => null,
    };

    public SearchView(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;
    }

    // ── Search ────────────────────────────────────────────────────────────

    private void SearchInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        // Enter — «искать прямо сейчас»: отменяем отложенный запуск, чтобы он не повторил тот же
        // поиск ещё раз через долю секунды.
        _liveSearchTimer?.Stop();
        PerformSearch();
    }

    // ── Поиск по мере ввода ───────────────────────────────────────────────
    // Кнопка «Найти» осталась (немедленный запуск), но ждать её больше не нужно: выдача
    // перезапускается сама через паузу после последнего нажатия клавиши. Устаревшие результаты
    // отбрасываются уже существующим механизмом поколений (_searchGeneration) — поздний ответ
    // прошлого запроса не перетирает свежий.

    private DispatcherTimer? _liveSearchTimer;

    /// <summary>Запрос, по которому последний раз реально запускался поиск — чтобы правка, не
    /// меняющая сути (например, добавленный в конце пробел), не гоняла поиск и обход диска заново.</summary>
    private string _lastLiveQuery = "";

    /// <summary>true, пока идёт поиск, запущенный НЕ кнопкой/Enter'ом, а паузой в наборе. Гасит
    /// подсказку про раскладку клавиатуры (см. LayoutFallbackAllowed): модальный вопрос «вы имели в
    /// виду вот это?» посреди набора текста — ровно то, чего в живом поиске быть не должно. По Enter
    /// и по кнопке «Найти» вопрос задаётся как раньше.</summary>
    private bool _liveSearchPass;

    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!SearchLiveQuery.AutoSearchApplies(CurrentMode == SearchMode.Schemas)) return;
        if (!SearchLiveQuery.QueryChanged(SearchInput.Text, _lastLiveQuery)) return;

        _liveSearchTimer ??= CreateLiveSearchTimer();
        // Перезапуск таймера с нуля на каждое нажатие — это и есть debounce: поиск уйдёт один раз,
        // когда оператор перестанет печатать, а не на каждую букву.
        _liveSearchTimer.Stop();
        _liveSearchTimer.Start();
    }

    private DispatcherTimer CreateLiveSearchTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SearchLiveQuery.DebounceMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RunLiveSearch();
        };
        return timer;
    }

    private void RunLiveSearch()
    {
        if (!SearchLiveQuery.AutoSearchApplies(CurrentMode == SearchMode.Schemas)) return;
        if (!SearchLiveQuery.QueryChanged(SearchInput.Text, _lastLiveQuery)) return;

        // Стёрли запрос и фильтров нет — на экране не должна оставаться выдача по тексту, которого
        // в поле уже нет. Тот же вид, что после «Сбросить поиск».
        if (!SearchLiveQuery.HasSomethingToSearch(SearchInput.Text, !ActiveFilters().IsEmpty))
        {
            _lastLiveQuery = "";
            ClearResultsView();
            return;
        }

        _liveSearchPass = true;
        try { PerformSearch(); }
        finally { _liveSearchPass = false; }
    }

    /// <summary>Пустой экран выдачи — общий для «Сбросить поиск» и для стёртого до конца запроса.</summary>
    private void ClearResultsView()
    {
        _searchGeneration++;
        ResultsPanel.Children.Clear();
        ClearSchemaResults();
        StatusLabel.Text = "";
        EmptyLabel.Text = "Введите запрос — выдача обновится сама";
        EmptyLabel.Visibility = Visibility.Visible;
    }

    /// <summary>Клик по строке поиска выделяет весь запрос целиком — чтобы новый запрос вставлялся
    /// одним Ctrl+V поверх старого, без предварительной чистки поля. Оба обработчика нужны вместе:
    /// GotKeyboardFocus выделяет при переходе фокуса (Tab, программный Focus), а PreviewMouseLeft-
    /// ButtonDown перехватывает клик мышью — иначе WPF сразу после выделения поставил бы каретку в
    /// точку клика и снял его.
    ///
    /// Важно, что выделяет и клик по УЖЕ активному полю: после набора запроса фокус остаётся в
    /// строке, и «выделять только при получении фокуса» означало бы, что в самом частом случае
    /// (посмотрел выдачу — вставляю следующий запрос) клик даёт каретку, а не выделение. Повторный
    /// клик, когда всё уже выделено, пропускается — так остаётся возможность поставить каретку и
    /// поправить запрос руками.</summary>
    private void SearchInput_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => SearchInput.SelectAll();

    private void SearchInput_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (SearchInput.IsKeyboardFocusWithin
            && SearchInput.Text.Length > 0
            && SearchInput.SelectionLength == SearchInput.Text.Length)
            return;

        e.Handled = true;
        SearchInput.Focus();
        SearchInput.SelectAll();
    }

    // ── Фильтры ───────────────────────────────────────────────────────────
    // Отдельной кнопкой и свёрнуты по умолчанию: в строке поиска и так три кнопки, а фильтры нужны
    // не каждый раз. Списки наполняются при первом раскрытии и при каждом последующем — справочники
    // и теги меняются (загрузили прошивку, приехала синхронизация), и показывать вчерашний набор
    // значений хуже, чем лишний раз спросить БД: это локальные быстрые запросы, не поход на диск.

    /// <summary>internal (не private) — только чтобы DedupeFilterOptions/SubtypeFilterLabel ниже
    /// можно было проверить тестом напрямую, без поднятия самого WPF-контрола (см. AssemblyInfo.cs,
    /// InternalsVisibleTo("AntarusPoFinder.Tests") — уже используется так же для AppUpdateService).</summary>
    internal sealed record FilterOption(string Label, int? Id = null, string? Text = null);

    private enum SearchMode { Firmware, Params, Schemas }

    /// <summary>Что сейчас выбрано в трёхпозиционном переключателе. Через null-условные обращения:
    /// FwModeRadio.IsChecked="True" в XAML поднимает Checked ещё до того, как соседним радиокнопкам
    /// присвоены поля x:Name (см. AnimateModeThumb).</summary>
    private SearchMode CurrentMode =>
        SchemasModeRadio?.IsChecked == true ? SearchMode.Schemas :
        ParamsModeRadio?.IsChecked == true ? SearchMode.Params : SearchMode.Firmware;

    private bool FiltersVisible => FiltersPanel.Visibility == Visibility.Visible;

    private void ToggleFilters_Click(object sender, RoutedEventArgs e)
    {
        if (FiltersVisible)
        {
            FiltersPanel.Visibility = Visibility.Collapsed;
            UpdateFiltersButton();
            return;
        }

        ReloadFilterOptions();
        FiltersPanel.Visibility = Visibility.Visible;
        UpdateFiltersButton();
    }

    private void ReloadFilterOptions()
    {
        var groups = _services.Db.GetAllEquipmentGroups();
        var controllers = _services.Db.GetAllControllerModels();

        FillFilter(FilterGroupCombo, "Тип шкафа: любой",
            groups.Where(g => g.Id is not null).Select(g => new FilterOption(g.Name, g.Id)));
        ReloadSubtypeFilter();
        FillFilter(FilterControllerCombo, "Контроллер: любой",
            controllers.Where(c => c.Id is not null).Select(c => new FilterOption(c.Name, c.Id)));
        FillFilter(FilterLaunchCombo, "Тип пуска: любой",
            ConfigService.LaunchTypes.Select(lt => new FilterOption(lt, null, lt)));
        BuildSchemaExtensionChecks();
        ApplyModeToFilters();
    }

    /// <summary>Подтипы — только выбранного типа шкафа. Раньше список был общий на всю базу, и при
    /// выбранном «ПЖ» в нём предлагались подтипы НГР и всех остальных типов: выбрать такую пару значило
    /// гарантированно получить пустую выдачу (запись не может одновременно принадлежать типу ПЖ и
    /// подтипу из НГР). Тип не выбран — показываем все подтипы, как и раньше.
    ///
    /// Когда тип не выбран, имя подтипа перестаёт быть уникальным ключом — «2.0» есть и у ПЖ, и у НГР
    /// (см. HierarchyDefaultsData: у обоих типов подтип с таким именем и prefix=0, но РАЗНЫЕ Id). Раньше
    /// FillFilter схлопывал одноимённые варианты в один пункт списка по имени (см. её комментарий про
    /// GroupBy) — второй «2.0» пропадал из выпадающего списка целиком, а тот, что оставался, был
    /// привязан к произвольному (первому попавшемуся при чтении из БД) Id: выбор подтипа «2.0» в
    /// фильтре превращался в лотерею между ПЖ и НГР, невидимую для оператора. Подписываем каждый
    /// вариант его типом шкафа (SubtypeFilterLabel) — и подписи перестают совпадать, и оператор видит,
    /// какой именно «2.0» выбирает.</summary>
    private void ReloadSubtypeFilter()
    {
        var groupId = (FilterGroupCombo.SelectedItem as FilterOption)?.Id;
        List<EquipmentSubType> subtypes;
        IReadOnlyDictionary<int, string>? groupNames = null;
        if (groupId is null)
        {
            subtypes = _services.Db.GetAllEquipmentSubtypes();
            groupNames = _services.Db.GetAllEquipmentGroups()
                .Where(g => g.Id is not null)
                .ToDictionary(g => g.Id!.Value, g => g.Name);
        }
        else
        {
            subtypes = _services.Db.GetSubtypesForGroup(groupId.Value);
        }

        FillFilter(FilterSubtypeCombo, "Подтип: любой",
            subtypes.Where(s => s.Id is not null && s.Name != "—")
                .Select(s => new FilterOption(SubtypeFilterLabel(s, groupNames), s.Id)));
    }

    /// <summary>Подпись подтипа для панели фильтров: пока список охватывает только один тип шкафа
    /// (groupNamesById не задан) — голое имя, как и раньше. Как только список объединяет несколько
    /// типов (Тип шкафа: «любой») — имя подтипа дополняется его типом в скобках («2.0 (ПЖ)»,
    /// «2.0 (НГР)»), тем же приёмом, что уже показывает тип у подтипа в «Параметрах ПЧ/УПП» (см.
    /// SubtypeMultiSelect.RebuildItemsCore) — только формат чуть компактнее (без отдельного "/").
    /// internal static — чистая функция от данных, без обращения к контролам, проверяется тестом
    /// напрямую (см. SearchFilterLogicTests).</summary>
    internal static string SubtypeFilterLabel(EquipmentSubType subtype, IReadOnlyDictionary<int, string>? groupNamesById) =>
        groupNamesById is not null && groupNamesById.TryGetValue(subtype.GroupId, out var groupName) && !string.IsNullOrEmpty(groupName)
            ? $"{subtype.Name} ({groupName})"
            : subtype.Name;

    // ── Фильтр по расширению (только «Схемы») ─────────────────────────────
    // Рядом со схемой в PDF на диске лежит её же исходник в DWG и десяток фотографий шкафа — без
    // этого фильтра половина выдачи по шкафу состоит не из того, что нужно прямо сейчас.

    private readonly List<(CheckBox Check, string[] Extensions)> _schemaExtChecks = new();

    /// <summary>Одна галочка на каждое расширение из настроенного списка (Настройки → Иерархия →
    /// «Расширения поиска схем», Database.GetAllowedExtensionsSchematic) — раньше был фиксированный
    /// SchemaExtensionOptions с семью захардкоженными группами (в т.ч. пары «JPG» на .jpg+.jpeg,
    /// «TIFF» на .tif+.tiff); список стал настраиваемым, и группировка синонимичных расширений под
    /// одной подписью ушла вместе с хардкодом — теперь у каждого настроенного расширения своя
    /// галочка, зато любое добавленное в Настройках (.xlsx, .doc и т.п.) появляется здесь само.</summary>
    private void BuildSchemaExtensionChecks()
    {
        if (_schemaExtChecks.Count > 0) return; // набор расширений строится один раз за сессию страницы
        foreach (var ext in _services.Db.GetAllowedExtensionsSchematic())
        {
            var extLower = "." + ext.Trim().ToLowerInvariant().TrimStart('.');
            var cb = new CheckBox { Content = ext.ToUpperInvariant(), Margin = new Thickness(0, 0, 14, 6) };
            cb.Checked += SchemaExtension_Changed;
            cb.Unchecked += SchemaExtension_Changed;
            _schemaExtChecks.Add((cb, new[] { extLower }));
            SchemaExtPanel.Children.Add(cb);
        }
    }

    /// <summary>Полный настроенный список — то, что обход второго диска вообще считает схемой (весь
    /// «универсум» файлов, из которого галочки выше выбирают показываемое подмножество). Читается из
    /// БД при каждом поиске (не кэшируется на странице), поэтому изменение списка в Настройках
    /// подхватывается следующим же «Найти» без перезапуска программы — в отличие от самих галочек
    /// фильтра (BuildSchemaExtensionChecks), которые строятся один раз за сессию страницы.</summary>
    private HashSet<string> ActiveSchemaScanExtensions() =>
        _services.Db.GetAllowedExtensionsSchematic()
            .Select(e => "." + e.Trim().ToLowerInvariant().TrimStart('.'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void SchemaExtension_Changed(object sender, RoutedEventArgs e)
    {
        if (_fillingFilters) return;
        UpdateFiltersButton();
        if (CurrentMode == SearchMode.Schemas) PerformSearch();
    }

    /// <summary>Отмеченные расширения. Пустой набор — фильтр не задан, подходит любое расширение
    /// (см. SchematicService.HitMatchesExtension).</summary>
    private HashSet<string> ActiveSchemaExtensions() =>
        _schemaExtChecks.Where(c => c.Check.IsChecked == true)
            .SelectMany(c => c.Extensions)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private const string FirmwareFiltersHint =
        "С пустым запросом покажет всё, что подходит под фильтры. Поиск по тегу — прямо в строке поиска (с «Точное совпадение слова» найдёт ровно нужный шкаф).";
    private const string SchemaFiltersHint =
        "Показывать только файлы выбранных форматов. Ничего не отмечено — показываются все.";

    /// <summary>У каждого режима поиска свой набор фильтров: у прошивок — справочники, у схем —
    /// расширение файла, у параметров фильтров нет вовсе (и кнопка «Фильтры» там не показывается, а
    /// не висит, ничего не делая — на это и была жалоба).</summary>
    private void ApplyModeToFilters()
    {
        if (FiltersToggle is null) return;
        var mode = CurrentMode;
        FirmwareFiltersPanel.Visibility = mode == SearchMode.Firmware ? Visibility.Visible : Visibility.Collapsed;
        SchemaFiltersPanel.Visibility = mode == SearchMode.Schemas ? Visibility.Visible : Visibility.Collapsed;
        FiltersHint.Text = mode == SearchMode.Schemas ? SchemaFiltersHint : FirmwareFiltersHint;
        if (mode == SearchMode.Params) FiltersPanel.Visibility = Visibility.Collapsed;
        UpdateFiltersButton();
    }

    /// <summary>Наполнение идёт под флагом _fillingFilters: смена ItemsSource/SelectedIndex поднимает
    /// SelectionChanged, и без флага перезаполнение подтипов после смены типа шкафа запускало бы
    /// поиск ещё раз (а по схемам — ещё один обход диска).</summary>
    private void FillFilter(ComboBox combo, string anyLabel, IEnumerable<FilterOption> options)
    {
        var previous = combo.SelectedItem as FilterOption;
        var items = new List<FilterOption> { new(anyLabel) };
        items.AddRange(DedupeFilterOptions(options));

        var wasFilling = _fillingFilters;
        _fillingFilters = true;
        try
        {
            combo.ItemsSource = items;
            // Пока у варианта есть Id — восстанавливаем выбор ПО ID, не по подписи: подпись подтипа
            // умеет меняться между перезаполнениями (см. SubtypeFilterLabel — суффикс типа появляется/
            // пропадает вместе с тем, сужен ли список одним типом шкафа), а сам подтип остаётся тем же.
            // У вариантов без Id (LaunchType, сама заглушка "любой") сравниваем по подписи/тексту —
            // им сравнивать больше не по чему.
            var restored = previous is null ? -1
                : previous.Id is int prevId ? items.FindIndex(o => o.Id == prevId)
                : items.FindIndex(o => o.Label == previous.Label && o.Text == previous.Text);
            combo.SelectedIndex = restored < 0 ? 0 : restored;
        }
        finally { _fillingFilters = wasFilling; }
    }

    /// <summary>Убирает настоящие дубликаты варианта фильтра: тот же Id (для группы/подтипа/
    /// контроллера), а для вариантов без Id (тип пуска) — тот же текст. НЕ схлопывает разные записи с
    /// одинаковой ПОДПИСЬЮ — прежняя версия дедупила именно по Label (см. историю правки), и это было
    /// ошибкой: у справочника бывают одноимённые, но РАЗНЫЕ записи (подтип «2.0» и у ПЖ, и у НГР — два
    /// разных Id). Дедуп по подписи одну из них тихо прятал из списка целиком, а оставшаяся получала
    /// произвольный (первый попавшийся при чтении из БД) Id — фильтр «Подтип: 2.0» реально мог отфильтровать
    /// не тот тип шкафа, который выбирал оператор. internal static — чистая функция, проверяется тестом
    /// напрямую (см. SearchFilterLogicTests).</summary>
    internal static List<FilterOption> DedupeFilterOptions(IEnumerable<FilterOption> options) =>
        options.Where(o => !string.IsNullOrWhiteSpace(o.Label))
            .GroupBy(o => o.Id?.ToString() ?? o.Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.First())
            .OrderBy(o => o.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>true, пока списки фильтров наполняются кодом — SelectionChanged, который при этом
    /// поднимает сам ComboBox, не должен ни перезапускать поиск, ни пересобирать соседний список.</summary>
    private bool _fillingFilters;

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingFilters) return;
        // Сменили тип шкафа — список подтипов обязан сузиться до подтипов этого типа.
        if (ReferenceEquals(sender, FilterGroupCombo)) ReloadSubtypeFilter();
        PerformSearch();
    }

    private void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        ResetFilterCombos();
        UpdateFiltersButton();
        PerformSearch();
    }

    private void ResetFilterCombos()
    {
        _fillingFilters = true;
        try
        {
            foreach (var combo in new[] { FilterGroupCombo, FilterSubtypeCombo, FilterControllerCombo, FilterLaunchCombo })
                if (combo.Items.Count > 0) combo.SelectedIndex = 0;
            foreach (var (check, _) in _schemaExtChecks) check.IsChecked = false;
        }
        finally { _fillingFilters = false; }
        ReloadSubtypeFilter();
    }

    /// <summary>Что выбрано в панели фильтров прямо сейчас. Свёрнутая панель фильтры НЕ отменяет —
    /// они продолжают действовать, поэтому на кнопке «Фильтры» и стоит точка, когда что-то выбрано.</summary>
    private FirmwareSearchFilters ActiveFilters()
    {
        if (FilterGroupCombo is null) return FirmwareSearchFilters.None; // до InitializeComponent
        return new FirmwareSearchFilters
        {
            GroupId = (FilterGroupCombo.SelectedItem as FilterOption)?.Id,
            SubtypeId = (FilterSubtypeCombo.SelectedItem as FilterOption)?.Id,
            ControllerId = (FilterControllerCombo.SelectedItem as FilterOption)?.Id,
            LaunchType = (FilterLaunchCombo.SelectedItem as FilterOption)?.Text,
        };
    }

    /// <summary>Кнопка показывает состояние фильтров ТОГО режима, который сейчас выбран, и вовсе
    /// исчезает в «Параметрах», где фильтров нет: кнопка, которая открывает пустую панель, — это ровно
    /// то, на что жаловался оператор («фильтры в схемах же не работают»).</summary>
    private void UpdateFiltersButton()
    {
        if (FiltersToggle is null) return;
        var mode = CurrentMode;
        if (mode == SearchMode.Params)
        {
            FiltersToggle.Visibility = Visibility.Collapsed;
            return;
        }
        FiltersToggle.Visibility = Visibility.Visible;
        var active = mode == SearchMode.Schemas
            ? _schemaExtChecks.Any(c => c.Check.IsChecked == true)
            : !ActiveFilters().IsEmpty;
        FiltersToggle.Content = FiltersVisible ? "Фильтры ▴" : active ? "Фильтры ▾ ●" : "Фильтры ▾";
    }

    private void Search_Click(object sender, RoutedEventArgs e) => PerformSearch();

    /// <summary>Показанная выдача устарела — при следующем заходе на вкладку (или прямо сейчас, если
    /// вкладка активна) её нужно перезапустить. false — выдача на экране актуальна, обычный возврат на
    /// вкладку её не трогает. Изначально true: показывать ещё нечего, первый заход и так не ищет.</summary>
    private bool _resultsDirty = true;

    /// <summary>Пометить выдачу устаревшей. Вызывается ТОЛЬКО на реальных изменениях данных (см.
    /// MainWindowViewModel.RefreshSearchIfActive — применён общий конфиг/обновление прошивок — и
    /// IAppHost.InvalidateSearchResults — загрузка/откат прошивки). Локальные правки внутри самой
    /// страницы (EditTags, DownloadFirmware) перезапускают поиск напрямую и в пометке не нуждаются.</summary>
    public void MarkResultsDirty() => _resultsDirty = true;

    /// <summary>Re-runs the last query so results (rollback status, tags, etc.) don't go stale — the
    /// page instance is cached across navigation. Но перезапуск теперь только когда выдачу реально
    /// пометили устаревшей (MarkResultsDirty): обычный возврат на вкладку (глянул Настройки/Схемы и
    /// вернулся) НЕ гоняет поиск и диск заново — сохраняются карточки, прокрутка, не «улетает»
    /// повторный запрос (жалоба пользователя), и второй диск на 400 ГБ не обходится по новой.</summary>
    public void RefreshIfActive()
    {
        // Выдача бывает и без запроса — одними фильтрами.
        if (string.IsNullOrWhiteSpace(SearchInput.Text) && ActiveFilters().IsEmpty) return;
        if (!_resultsDirty) return;
        PerformSearch();
    }

    private void ResetSearch_Click(object sender, RoutedEventArgs e)
    {
        // Отложенный запуск по мере ввода отменяем до очистки поля — иначе он сработал бы уже после
        // сброса и нарисовал выдачу по только что стёртому запросу.
        _liveSearchTimer?.Stop();
        _lastLiveQuery = "";
        SearchInput.Text = "";
        // Фильтры сбрасываются вместе с запросом: иначе «сбросил поиск, а всё равно ничего не
        // находит» — забытый фильтр в свёрнутой панели не виден.
        ResetFilterCombos();
        UpdateFiltersButton();
        ClearResultsView();
        SearchInput.Focus();
    }

    /// <summary>Re-runs the current query in the new mode as soon as the user flips Прошивки/
    /// Параметры/Схемы or the exact-word checkbox — matches the immediate feedback of a live
    /// filter instead of requiring another click on «Найти».</summary>
    private void SearchMode_Changed(object sender, RoutedEventArgs e)
    {
        AnimateModeThumb();
        // Ушли в «Схемы» — отложенный запуск по мере ввода там не работает (обход второго диска, см.
        // SearchLiveQuery.AutoSearchApplies); заодно гасим уже заведённый таймер, чтобы он не запустил
        // обход диска через долю секунды после переключения.
        _liveSearchTimer?.Stop();
        // Набор фильтров у каждого режима свой — переставляем его до поиска, чтобы тот уже читал
        // фильтры нового режима.
        ApplyModeToFilters();
        if (!string.IsNullOrWhiteSpace(SearchInput.Text)) PerformSearch();
    }

    /// <summary>Width of one segment in the three-way Прошивки/Параметры/Схемы slider — must match
    /// the Width set on each RadioButton and on ModeThumb in SearchView.xaml.</summary>
    private const double ModeSegmentWidth = 150;

    /// <summary>Glides ModeThumb under whichever segment is now checked instead of each segment
    /// flipping its own background — a real sliding toggle, not three independently-styled pills.
    /// Guarded with null-conditionals: FwModeRadio's IsChecked="True" in XAML fires its Checked
    /// event the moment InitializeComponent parses that element, which is BEFORE ParamsModeRadio/
    /// SchemasModeRadio (declared later in the same XAML) get their x:Name fields connected —
    /// reading them unconditionally here crashed the app on every startup.</summary>
    private void AnimateModeThumb()
    {
        if (ModeThumbTransform is null) return;
        var index = SchemasModeRadio?.IsChecked == true ? 2 : ParamsModeRadio?.IsChecked == true ? 1 : 0;
        ModeThumbTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(index * ModeSegmentWidth, TimeSpan.FromSeconds(0.15)));
    }

    private void PerformSearch()
    {
        var query = SearchInput.Text.Trim();
        // Что реально ушло в поиск — по этому значению поиск по мере ввода понимает, изменился ли
        // запрос с прошлого раза (см. SearchInput_TextChanged).
        _lastLiveQuery = query;
        UpdateFiltersButton();
        // Пустой запрос сам по себе ничего не ищет, но с заданными фильтрами — это осмысленное
        // «покажи всё такое» (все прошивки НГР на SMH5, все с типом пуска ПЧ и т.п.). Только для
        // прошивок: у параметров и схем фильтров нет.
        var filtersOnly = string.IsNullOrEmpty(query) && FwModeRadio.IsChecked == true && !ActiveFilters().IsEmpty;
        if (string.IsNullOrEmpty(query) && !filtersOnly) return;

        // Новая выдача — карточки прошлой сейчас будут выброшены, значит незавершённая
        // автосинхронизация по ним больше не актуальна (см. AutoSyncMissingAsync).
        _searchGeneration++;
        // Сейчас перерисуем — то, что окажется на экране, актуально; дальнейшие возвраты на вкладку
        // не будут перезапускать поиск, пока данные снова не пометят устаревшими (MarkResultsDirty).
        _resultsDirty = false;
        StatusLabel.Text = "Поиск…";
        ResultsPanel.Children.Clear();
        ClearSchemaResults();
        EmptyLabel.Visibility = Visibility.Collapsed;

        if (SchemasModeRadio.IsChecked == true)
            PerformSchemasSearch(query);
        else if (ParamsModeRadio.IsChecked == true)
            PerformParamsSearch(query);
        else
            PerformFirmwareSearch(query);
    }

    private const string NoResultsHint = "Ничего не найдено — попробуйте другой запрос или снимите «Точное совпадение слова»";
    private const string NoResultsFilteredHint = "Ничего не найдено — возможно, слишком узкие фильтры: «Фильтры» → «Сбросить фильтры»";

    /// <summary>Нормализованный запрос последней выдачи — под ним и записывается выбор версии
    /// (Database.RecordFwUsage). Пустой, если выдача получена без запроса, одними фильтрами:
    /// «по такому запросу обычно ставят вот эту» без запроса не имеет смысла.</summary>
    private string _lastUsageKey = "";

    /// <summary>Оператор выбрал эту версию из выдачи — открыл проект/файл, залил в контроллер или
    /// скачал. Это и есть тот факт, который потом поднимает её выше среди одинаково подходящих
    /// (см. Database.FwUsage.cs). Просмотр карт/инструкций/истории сюда не считается: это чтение
    /// сопутствующего, а не «взял эту прошивку».</summary>
    private void RecordUsage(HierarchyResult result)
    {
        if (string.IsNullOrEmpty(_lastUsageKey) || result.FwVersionId <= 0) return;
        if (!_services.Cfg.UsageConfirmEnabled()) return;
        if (!ConfirmThisWasTheOne(result)) return;

        try { _services.Db.RecordFwUsage(_lastUsageKey, result.FwVersionId); }
        catch { /* статистика — вспомогательная вещь, ронять из-за неё действие оператора нельзя */ }
    }

    /// <summary>Ответы на «та ли это прошивка» в пределах жизни страницы — повторное нажатие по той
    /// же версии того же запроса (открыл проект, потом ещё раз) не переспрашивает и не добавляет
    /// второй голос в обучение.</summary>
    private readonly Dictionary<(string Query, int Version), bool> _usageAnswers = new();

    /// <summary>Открыть карточку можно и промахнувшись — а засчитанный промах поднимает чужую версию
    /// в выдаче по этому запросу. Поэтому перед записью спрашиваем, та ли это прошивка, и учимся на
    /// ответах ровно как подсказка про раскладку (Database.RecordFwUsageConfirmFeedback): несколько
    /// одинаковых ответов подряд — и вопрос больше не задаётся, а выбор либо засчитывается молча,
    /// либо не засчитывается вовсе.</summary>
    private bool ConfirmThisWasTheOne(HierarchyResult result)
    {
        var decision = _services.Db.GetFwUsageConfirmDecision();
        if (decision == UsageConfirmDecision.Never) return false;
        if (decision == UsageConfirmDecision.Always) return true;

        var key = (_lastUsageKey, result.FwVersionId);
        if (_usageAnswers.TryGetValue(key, out var earlier)) return earlier;

        var name = string.IsNullOrEmpty(result.VersionRaw) ? result.Name : $"{result.Name} — {result.VersionRaw}";
        var reply = AppMessageBox.Show(
            $"{name}\n\nЭто та прошивка, которую вы искали?\n\n" +
            "Ответ идёт в подсказку «по такому запросу обычно ставят эту версию» — она поднимает нужную " +
            "версию выше среди одинаково подходящих. Несколько одинаковых ответов подряд — и программа " +
            "перестанет спрашивать (Настройки → Общие).",
            "Та ли это прошивка?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

        var confirmed = reply == MessageBoxResult.Yes;
        _usageAnswers[key] = confirmed;
        try { _services.Db.RecordFwUsageConfirmFeedback(confirmed, _services.Cfg.UsageConfirmThreshold()); }
        catch { /* см. выше — обучение не важнее самого действия оператора */ }
        return confirmed;
    }

    private void PerformFirmwareSearch(string query)
    {
        var exact = ExactWordCheck.IsChecked == true;
        var filters = ActiveFilters();
        var results = SearchService.Search(_services.Db, query, exact,
            LayoutFallbackAllowed(query), out var usedFallback, out var convertedQuery, filters,
            _services.Cfg.FwUsageThreshold(), _services.Cfg.FwUsageMultiplier(), _services.Cfg.RootPath());

        if (results.Count == 0)
        {
            ShowNoResults(query, filters.IsEmpty ? NoResultsHint : NoResultsFilteredHint);
            return;
        }

        // Что именно искали — нужно, чтобы записать выбор оператора по этому запросу
        // (см. RecordUsage): выбор осмыслен только в паре с запросом, который его показал.
        _lastUsageKey = SearchService.UsageKey(query);

        // Обычный поиск находит «широко» — по любому ОДНОМУ совпавшему слову. Чтобы одно общее слово
        // («SMH») не тащило в выдачу чужие прошивки, у которых нет остальных слов запроса, показываем
        // сразу только карточки, совпавшие по МАКСИМУМУ введённых слов, а менее точные прячем под
        // «Показать ещё» (строятся по клику). В точном поиске и в пустом запросе с фильтрами число
        // совпавших слов у всех строк одинаково — там weak пуст, сворачивать нечего.
        var maxMatched = results.Max(r => r.MatchedTokens);
        var strong = results.Where(r => r.MatchedTokens >= maxMatched).ToList();
        var weak = results.Where(r => r.MatchedTokens < maxMatched).ToList();

        // «Найдено: N» показывает СНАЧАЛА только число точных (strong) совпадений — «сколько найдено
        // именно точных». Менее точные (weak) прибавятся к счётчику лишь когда оператор раскроет их
        // кнопкой «Показать ещё» (см. AddWeakMatchesFold): пока они спрятаны — они и не в счёте.
        _foundCount = strong.Count;
        _foundFiltered = !filters.IsEmpty;
        UpdateFoundLabel();
        _subtypesById = _services.Db.GetAllEquipmentSubtypes().Where(s => s.Id is not null).ToDictionary(s => s.Id!.Value);
        var canEditTags = _services.Cfg.CurrentRole() is "administrator";
        var autoSync = _services.Cfg.SearchAutoSync();
        // Доступность Automation-компонента считаем один раз на выдачу. Саму кнопку не скрываем при
        // его отсутствии: по нажатию оператор получит точную причину, а не молчаливое исчезновение.
        var loaderConnected = FirmwareLoaderFactory.Create(_services.Cfg.LoaderExePath()).IsAvailable;
        // Прошивки, чьи правки (теги/описание) ещё лежат в накопителе и не уехали на диск — карточка
        // покажет «правки этой прошивки ещё не на диске» (см. FirmwareCardFlags.TagsPending). Читаем
        // один раз на всю выдачу; на не-администраторских машинах набор обычно пуст (правят только они).
        var pendingSubjects = _services.Db.GetPendingSubjectKeys();
        var pending = new List<(FirmwareCard Card, HierarchyResult Result, FirmwareCardFlags Flags)>();
        var generation = _searchGeneration;

        (FirmwareCard Card, HierarchyResult Result, FirmwareCardFlags Flags) BuildCard(HierarchyResult result)
        {
            var subtypeName = _subtypesById.TryGetValue(result.SubtypeId, out var sub) ? sub.Name : "";
            // Только дешёвые признаки: локальный кэш (свой диск) и запрос в SQLite. Всё, что требует
            // обхода папки версии — она обычно на сетевом диске — считается потом, в фоне
            // (ScanDiskFlagsAsync): раньше это делалось прямо здесь, синхронно, на КАЖДЫЙ результат,
            // и «Найти» на десяти результатах вешало окно на секунды — ровно жалоба «нажимаю кнопку,
            // ничего не происходит, тыкаю несколько раз — тогда находит» (клики копились в очереди).
            var flags = new FirmwareCardFlags
            {
                HasLocal = HasLocal(result),
                HasAnyLocal = HasAnyLocal(result),
                HasParams = subtypeName != "ПП" && _services.Db.GetParamFiles(subtypeId: result.SubtypeId).Count > 0,
                // Доп. материалы — записи в БД, а не файлы «где-то рядом»: считаются здесь же, дешёвым
                // COUNT, и не ждут фонового обхода папки версии.
                ExtraFilesCount = _services.Db.CountFwAttachments(result.FwVersionId),
                CanEditTags = canEditTags,
                AutoSync = autoSync,
                LoaderConnected = loaderConnected,
                TagsPending = pendingSubjects.Contains(result.FwVersionId.ToString()),
                DiskScanPending = true,
                // По контроллеру/подсказке файла — до обхода диска; после обхода уточняется тем, что
                // реально нашлось рядом (см. ScanDiskFlagsAsync).
                IsSegnetics = SegneticsProject.IsRelevant(result.Controller, result.ExecutableHint),
                ConnectionMode = _services.Cfg.LoaderConnectionMode(),
                ConnectionHint = ConnectionHintText(),
            };

            var card = new FirmwareCard();
            card.Configure(result, flags);
            // Выбор версии засчитывается на действиях «взял эту прошивку» — открыл проект/файл,
            // залил в контроллер, скачал локально (см. RecordUsage).
            card.OpenFolderRequested += (s, _) => OpenFirmwareFolder(((FirmwareCard)s!).Result);
            card.OpenServerFolderRequested += (s, _) => OpenServerFolder(((FirmwareCard)s!).Result);
            card.OpenPlcRequested += (s, _) => { RecordUsage(((FirmwareCard)s!).Result); OpenPlc(((FirmwareCard)s!).Result); };
            card.OpenHmiRequested += (s, _) => { RecordUsage(((FirmwareCard)s!).Result); OpenHmi(((FirmwareCard)s!).Result); };
            card.OpenLfsRequested += (s, _) =>
            {
                RecordUsage(((FirmwareCard)s!).Result);
                OpenLoaderFile(((FirmwareCard)s!).Result, LoaderFiles.LfsExtension, "LFS");
            };
            card.OpenPslRequested += (s, _) =>
            {
                RecordUsage(((FirmwareCard)s!).Result);
                OpenLoaderFile(((FirmwareCard)s!).Result, LoaderFiles.PslExtension, "PSL");
            };
            card.LoaderRequested += (s, _) => { RecordUsage(((FirmwareCard)s!).Result); OpenLoader(((FirmwareCard)s!).Result); };
            card.ConnectionModeChangeRequested += (_, mode) => SaveConnectionMode(mode);
            card.DownloadRequested += (s, _) => { RecordUsage(((FirmwareCard)s!).Result); DownloadFirmware(((FirmwareCard)s!).Result); };
            card.MapRequested += (s, _) => OpenMap(((FirmwareCard)s!).Result);
            card.ModbusMapRequested += (s, _) => OpenModbusMap(((FirmwareCard)s!).Result);
            card.ParamsRequested += (s, _) => OpenParams(((FirmwareCard)s!).Result);
            card.InstructionsRequested += (s, _) => OpenInstructions(((FirmwareCard)s!).Result);
            card.OpenInstructionFolderRequested += (s, _) => OpenInstructionFolder(((FirmwareCard)s!).Result);
            card.EditInstructionRequested += (s, _) => EditInstruction(((FirmwareCard)s!).Result);
            card.OpenInstructionPdfRequested += (s, e) => { _ = OpenInstructionPdfAsync(((FirmwareCard)s!).Result); };
            card.PrintInstructionRequested += (s, e) => { _ = PrintInstructionAsync(((FirmwareCard)s!).Result); };
            card.InstructionLabelRequested += (s, _) => ShowInstructionLabel(((FirmwareCard)s!).Result);
            card.ExtraFilesRequested += (s, _) => OpenExtraFiles(((FirmwareCard)s!).Result);
            card.HistoryRequested += (s, _) => ShowHistory(((FirmwareCard)s!).Result);
            card.CopyNameRequested += (s, _) => CopyName(((FirmwareCard)s!).Result);
            card.TagsEditRequested += (s, _) => EditTags(((FirmwareCard)s!).Result);
            ResultsPanel.Children.Add(card);

            return (card, result, flags);
        }

        foreach (var result in strong) pending.Add(BuildCard(result));

        if (weak.Count > 0) AddWeakMatchesFold(weak, BuildCard, generation);

        if (!ConfirmLayoutFallback(query, usedFallback, convertedQuery))
        {
            ShowNoResults(query, NoResultsHint);
            return;
        }

        _ = ScanDiskFlagsAsync(pending, generation);
    }

    /// <summary>Кнопка «Показать ещё» под точными совпадениями обычного поиска: менее точные карточки
    /// (совпало меньше слов запроса) строятся и досматриваются на диске только по клику, чтобы широкий
    /// запрос не рисовал и не обходил десятки чужих прошивок зря. Поколение (<paramref name="generation"/>)
    /// сверяется на клике — если оператор успел искать заново, кнопка от старой выдачи ничего не делает.</summary>
    private void AddWeakMatchesFold(List<HierarchyResult> weak,
        Func<HierarchyResult, (FirmwareCard Card, HierarchyResult Result, FirmwareCardFlags Flags)> buildCard,
        int generation)
    {
        var button = new Button
        {
            Content = $"Показать ещё {weak.Count} — менее точные совпадения",
            Style = (Style)FindResource("SecondaryButton"),
            Margin = new Thickness(0, 8, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        button.Click += (_, _) =>
        {
            if (generation != _searchGeneration) return;
            ResultsPanel.Children.Remove(button);
            // Теперь менее точные совпадения на экране — они входят и в счёт «Найдено».
            _foundCount += weak.Count;
            UpdateFoundLabel();
            var revealed = weak.Select(buildCard).ToList();
            _ = ScanDiskFlagsAsync(revealed, generation);
        };
        ResultsPanel.Children.Add(button);
    }

    private int _foundCount;
    private bool _foundFiltered;

    /// <summary>Строка «Найдено: N». _foundCount — число ТОЧНЫХ совпадений (strong), растёт при
    /// раскрытии «Показать ещё» (менее точные weak). Версии, которых не нашли на диске, больше НЕ
    /// вычитаются: их не прячут, а показывают с пометкой (см. ScanDiskFlagsAsync), поэтому в счёте
    /// они по-прежнему есть — иначе «найдено 0» при видимых на экране карточках вводило в ступор.</summary>
    private void UpdateFoundLabel()
    {
        StatusLabel.Text = _foundFiltered ? $"Найдено: {_foundCount} (с фильтрами)" : $"Найдено: {_foundCount}";
    }

    // ── Что лежит рядом с версией на диске ────────────────────────────────
    // Обход папки версии (LFS/PSL/HMI/карта ВВ) — единственная по-настоящему медленная часть выдачи:
    // папка живёт на сетевом диске компании, который регулярно отвечает через раз. Поэтому карточки
    // рисуются сразу, а этот обход идёт следом в фоне и дорисовывает их по мере готовности.

    private readonly record struct DiskScan(bool HasLfs, bool HasPsl, bool HasHmi,
        bool HasIoMap, bool HasInstructions, bool HasInstructionDocx, bool HasInstructionPrintable,
        bool HasInstructionStub, bool HasModbus,
        string? PlcOpenExtension, string? HmiOpenExtension, bool NetworkAlive);

    /// <summary>Один обход на версию вместо трёх (LFS/PSL + HMI по расширениям): все три признака
    /// вытаскиваются за одно перечисление файлов первой же папки-кандидата, где вообще что-то
    /// нашлось. Только папки САМОЙ версии (VersionFolders): признак «есть LFS» должен относиться к
    /// той версии, на карточке которой он написан.</summary>
    private static DiskScan ScanVersionFolder(HierarchyResult result, DocRoots roots)
    {
        bool lfs = false, psl = false, hmiFile = false, networkAlive = false;
        foreach (var dir in VersionFolders(result))
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    networkAlive = true; // в папке версии на сетевом диске реально лежат файлы
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext == LoaderFiles.LfsExtension) lfs = true;
                    else if (ext == LoaderFiles.PslExtension) psl = true;
                    else if (KincoHmiExts.Contains(ext)) hmiFile = true;
                }
            }
            catch (Exception) { /* недоступная папка — просто «не нашли», см. LoaderFiles.Find */ }
            if (lfs || psl || hmiFile) break;
        }

        var hasHmi = !string.IsNullOrEmpty(result.HmiPath)
            || ExecutableHintResolver.Normalize(result.HmiExecutableHint) is not null
            || hmiFile;
        // Карта ВВ / инструкция / карта Modbus — есть, только если реально найден файл (путь версии,
        // указывающий на существующий файл, ЛИБО непустая общая папка документа), а не просто
        // заполненное поле в БД. Тот же резолвер потом открывает самый свежий файл (см. OpenMap и др.).
        var hasIoMap = ResolveDocFile(result, result.IoMapPath, "Карта ВВ") is not null;
        // Инструкция разбирается детальнее прочих документов: у неё различаются исходный docx (для правки)
        // и pdf (для печати), от чего зависят разные пункты меню карточки (см. FirmwareCard.AddInstructionItems).
        var instr = ResolveInstruction(result, roots);
        var hasInstructions = instr.HasAny;
        var hasInstrDocx = instr.Docx is not null;
        var hasInstrPrintable = instr.CanPrint;
        // Заглушка «Инструкция в разработке» документом не считается, но путь у неё тот же, по
        // которому ляжет настоящий документ, — от этого зависит только кнопка «QR инструкции»
        // (см. FirmwareCardFlags.HasInstructionStub). Ищем, лишь когда документа нет: иначе это
        // лишний обход папки на сетевом диске у каждой карточки.
        var hasInstrStub = !hasInstructions && InstructionStub.ExistingIn(InstructionFolder(result, roots)) is not null;
        var hasModbus = ResolveDocFile(result, result.ModbusMapPath, "Карта Modbus") is not null;
        // Расширение того файла, который реально откроет «Открыть прошивку ПЛК» — считается тем же
        // резолвером, что и само открытие (PlcOpenResolver), поэтому подпись кнопки не может
        // разойтись с тем, что откроется, и работает для ЛЮБОГО проекта, не только .psl/.lfs.
        var plcExt = PlcOpenResolver.ResolveExtension(PlcSources(result));
        // То же самое для панели: расширение считает HmiOpenResolver, он же потом и открывает (OpenHmi).
        // Только когда панель вообще есть — иначе это лишний обход папок ради подписи несуществующей кнопки.
        var hmiExt = hasHmi ? HmiOpenResolver.ResolveExtension(HmiSources(result)) : null;
        return new DiskScan(lfs, psl, hasHmi, hasIoMap, hasInstructions, hasInstrDocx, hasInstrPrintable,
            hasInstrStub, hasModbus, plcExt, hmiExt, networkAlive);
    }

    /// <summary>Папки, по которым PlcOpenResolver ищет файл проекта ПЛК — см. его комментарий про
    /// разницу между наборами.</summary>
    private static PlcOpenSources PlcSources(HierarchyResult result)
    {
        var net = ResolvedNetworkDir(result);
        return new()
        {
            CandidateFolders = CandidateFolders(result).ToList(),
            VersionFolders = VersionFolders(result).ToList(),
            FilteredFolders = new[] { Path.Combine(ConfigService.LocalFw, SanitizeName(result.Name)), net },
            ExecutableHint = result.ExecutableHint,
            NetworkFolder = net,
        };
    }

    /// <summary>Источники файла панели для HmiOpenResolver — зеркально PlcSources.
    /// Ходит на диск (FindSiblingFolder) — вызывать из фонового обхода или по клику, не в отрисовке.</summary>
    private static HmiOpenSources HmiSources(HierarchyResult result) => new()
    {
        HmiPath = result.HmiPath,
        SiblingHmiFolder = FindSiblingFolder(result, "HMI"),
        ExecutableHint = result.HmiExecutableHint,
        CandidateFolders = CandidateFolders(result).ToList(),
        FilteredFolders = new[] { Path.Combine(ConfigService.LocalFw, SanitizeName(result.Name)), ResolvedNetworkDir(result) },
    };

    /// <summary>Самый свежий актуальный файл документа (карта ВВ / инструкция / карта Modbus) —
    /// общая папка документа рядом с папкой контроллера, см. DocFileResolver.
    /// Ходит на диск — вызывать из фонового потока (ScanVersionFolder) или по клику, не в отрисовке.</summary>
    private static string? ResolveDocFile(HierarchyResult result, string? storedPath, string sharedFolderName) =>
        DocFileResolver.Resolve(storedPath, FindSiblingFolder(result, sharedFolderName));

    /// <summary>Корень диска прошивок, от которого зависит поиск инструкции. Читается из настроек на
    /// потоке интерфейса и передаётся в фоновый обход параметром — лезть за ним в базу из фонового
    /// потока нельзя (соединение SQLite одно на приложение и не потокобезопасно).</summary>
    private readonly record struct DocRoots(string First);

    private DocRoots CurrentDocRoots() => new(_services.Cfg.RootPath());

    /// <summary>Папка, из которой читается инструкция: общая папка «Инструкция» рядом с папкой
    /// контроллера на первом диске.</summary>
    private static string? InstructionFolder(HierarchyResult result, DocRoots roots) =>
        FindSiblingFolder(result, "Инструкция");

    /// <summary>docx/pdf инструкции этой версии (см. InstructionDocResolver). Ходит на диск — из
    /// фонового обхода или по клику, не в отрисовке.</summary>
    private static InstructionDoc ResolveInstruction(HierarchyResult result, DocRoots roots) =>
        InstructionDocResolver.Resolve(result.InstructionsPath, InstructionFolder(result, roots));

    /// <summary>Дорисовывает карточки признаками с диска, потом запускает автосинхронизацию тех, у
    /// кого нет локальной копии. Последовательно и с проверкой поколения выдачи — по тем же причинам,
    /// что и AutoSyncMissingAsync.</summary>
    private async Task ScanDiskFlagsAsync(List<(FirmwareCard Card, HierarchyResult Result, FirmwareCardFlags Flags)> cards, int generation)
    {
        var pendingSync = new List<(FirmwareCard Card, HierarchyResult Result)>();

        // Сетевой диск доступен? Только тогда «папки версии на диске нет» означает «прошивку удалили»,
        // а не «сеть сейчас отвалилась». При недоступном диске ничего не прячем — иначе при обрыве сети
        // выдача схлопнулась бы в ноль (см. #12: «прошивка есть локально, а на диске её нет»).
        var root = _services.Cfg.RootPath();
        var netReachable = !string.IsNullOrEmpty(root) && Directory.Exists(root);
        // Читаем настройки один раз здесь, на потоке интерфейса: обход ниже уходит в Task.Run.
        var roots = CurrentDocRoots();

        foreach (var (card, result, baseFlags) in cards)
        {
            if (generation != _searchGeneration) return;

            var scan = await Task.Run(() => ScanVersionFolder(result, roots));
            if (generation != _searchGeneration) return;

            // Версия, которой нет ни в локальном кэше, ни в папке на доступном сетевом диске — это
            // «мёртвая» ссылка (прошивку удалили с диска): открыть её нечем, и в выдаче ей не место.
            // Запись в БД не трогаем (удаление не должно уехать на другие машины как тумбстоун, см.
            // ConfigExchange) — просто убираем карточку из показанной выдачи; вернутся файлы — вернётся
            // и карточка при следующем поиске.
            //
            // НО «папку не нашли» ≠ «прошивку удалили»: если путь версии вообще не удалось разложить на
            // ЭТОТ диск (сохранён у коллеги как «Z:\Software\…», а у нас диск смонтирован как
            // «\\ant_srv\Software\…», и FirmwarePathLocalizer не смог заякориться на «ПО»/«Параметры»),
            // то result.FirmwareDir так и остался чужим — он НЕ под нашим корнем, и «Directory.Exists
            // вернул false» не значит, что прошивки нет, значит только «мы не туда посмотрели». Прятать
            // такую версию — ровно жалоба «прошивка есть, теги совпадают, а поиск её не находит».
            // Прячем, только когда искали в правильном месте: путь пуст или лежит под нашим корнем.
            //
            // И «папки по точному пути нет» ≠ «прошивку удалили»: папку версии могли ПЕРЕИМЕНОВАТЬ на
            // диске (откат дописал «_ОТКАТАНО», правку hw переписали номер в середине имени, перезалив
            // сменил дату), а disk_path в базе остался прежним. Файлы лежат в той же папке контроллера
            // под соседним именем той же сборки — FirmwareDiskPresence опознаёт её по номеру ИЛИ по
            // метке даты-времени сборки.
            //
            // РАНЬШЕ здесь карточку убирали с экрана и считали «скрыто отсутствующих на диске». Это
            // давало регулярную жалобу «выбрал фильтр — найдено 0, скрыто отсутствующих, хотя прошивка
            // на диске ЕСТЬ»: любой промах определения присутствия (переименовали hw без метки сборки,
            // нестандартное имя папки Pixel, иначе смонтированный диск) молча прятал живой результат, и
            // человек не понимал, куда делась прошивка. Теперь ничего не прячем — карточку показываем с
            // явной пометкой «на диске не найдена» (FirmwareCardFlags.DiskMissing) и не тянем её
            // автосинхронизацией (тянуть нечего). Спрятать реальную прошивку хуже, чем показать её с
            // предупреждением: если файлов правда нет — карточка честно об этом и скажет, а решение
            // открыть/поискать вручную остаётся за оператором.
            var diskMissing = netReachable && !baseFlags.HasLocal && !scan.NetworkAlive
                && !FirmwareDiskPresence.VersionPresentOnDisk(result.FirmwareDir, result.VersionRaw)
                && PathCheckableHere(result, root);
            if (diskMissing)
            {
                var missingFlags = baseFlags with { DiskScanPending = false, DiskMissing = true };
                card.Configure(result, missingFlags);
                // Пере-показать статус явно: первая отрисовка показала «синхронизируем…», а Configure
                // второй раз статус не трогает (_syncStatusShown) — без этого «обновляем…» висело бы,
                // хотя на диске версии нет и синхронизировать нечего.
                card.RefreshSyncStatus(missingFlags);
                continue;
            }

            card.Configure(result, baseFlags with
            {
                HasLfs = scan.HasLfs,
                HasPsl = scan.HasPsl,
                HasHmi = scan.HasHmi,
                HasIoMap = scan.HasIoMap,
                HasInstructions = scan.HasInstructions,
                HasInstructionDocx = scan.HasInstructionDocx,
                HasInstructionPrintable = scan.HasInstructionPrintable,
                HasInstructionStub = scan.HasInstructionStub,
                HasModbus = scan.HasModbus,
                PlcOpenExtension = scan.PlcOpenExtension,
                HmiOpenExtension = scan.HmiOpenExtension,
                DiskScanPending = false,
                IsSegnetics = SegneticsProject.IsRelevant(result.Controller, result.ExecutableHint, scan.HasLfs, scan.HasPsl),
            });

            if (baseFlags.AutoSync && !baseFlags.HasLocal) pendingSync.Add((card, result));
        }

        if (pendingSync.Count > 0) await AutoSyncMissingAsync(pendingSync, generation);
    }

    /// <summary>Смогли ли мы вообще проверить, есть ли эта версия на диске — то есть искали ли в
    /// правильном месте. True, когда сетевой путь версии пуст (проверять нечего — решение принимается
    /// по локальному кэшу) или лежит под корнем ЭТОГО диска (значит FirmwarePathLocalizer разложил
    /// путь на нашу машину, и «папки нет» действительно означает «прошивку удалили»). False, когда
    /// путь остался чужим (не под нашим корнем) — его не удалось разложить на этот диск, и отсутствие
    /// папки ничего не доказывает: прятать версию по такому «нет» нельзя, иначе прошивка коллеги с
    /// иначе смонтированным диском молча пропадает из выдачи, хотя реально существует.</summary>
    private static bool PathCheckableHere(HierarchyResult result, string root)
    {
        var dir = result.FirmwareDir ?? "";
        if (dir.Length == 0) return true;
        if (string.IsNullOrEmpty(root)) return false;
        return dir.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    // ── Автосинхронизация локальных копий ─────────────────────────────────
    // Раньше на каждой карточке без локальной копии была кнопка «Синхронизировать»/«Обновить», и
    // наладчик жал её вручную по одной. Теперь найденное подтягивается само, а кнопка осталась
    // только в меню «Ещё» — как запасной вариант (автосинхронизация выключена / упала с ошибкой).

    /// <summary>Сколько версий тянуть автоматически за одну выдачу. Потолок нужен: широкий запрос
    /// может найти десятки версий, и качать их все с сетевого диска — не то, чего оператор просил,
    /// нажав «Найти». Что не влезло — видно в статусе, молча не отбрасывается.</summary>
    private const int AutoSyncMaxPerSearch = 10;

    /// <summary>Номер текущей выдачи. Автосинхронизация асинхронная и может пережить сам поиск
    /// (переключили режим, ввели другой запрос, фоновый тик синхронизации перерисовал результаты) —
    /// карточки к этому моменту уже другие, поэтому устаревший прогон просто прекращается.</summary>
    private int _searchGeneration;

    private async Task AutoSyncMissingAsync(List<(FirmwareCard Card, HierarchyResult Result)> pending, int generation)
    {
        var skipped = pending.Count - AutoSyncMaxPerSearch;
        if (skipped > 0)
        {
            foreach (var (card, _) in pending.Skip(AutoSyncMaxPerSearch))
                card.SetSyncStatus("Локальной копии нет. Автосинхронизация за раз тянет не больше " +
                    $"{AutoSyncMaxPerSearch} версий — «Ещё» → «Обновить локальную копию с диска».", "WarningBrush");
            StatusLabel.Text += $"  ·  автосинхронизация: {AutoSyncMaxPerSearch} из {pending.Count}, остальные — вручную";
            pending = pending.Take(AutoSyncMaxPerSearch).ToList();
        }

        // Последовательно, а не параллельно: сетевой диск компании и так регулярно отваливается
        // (см. NetworkPathHelper), десяток одновременных копирований делу не поможет.
        //
        // Ход виден дважды: подробно на самой карточке и общей строкой внизу окна — карточка может
        // быть уже прокручена за экран, а «программа не отвечает» пользователь замечает как раз
        // тогда, когда не на что посмотреть.
        using var busy = _host.BeginBusy("Синхронизация локальных копий…");
        for (int i = 0; i < pending.Count; i++)
        {
            var (card, result) = pending[i];
            if (generation != _searchGeneration) return;

            busy.Text = $"Синхронизация: {result.Name} {result.VersionRaw}".Trim();
            busy.Report(i, pending.Count);

            if (string.IsNullOrEmpty(result.FirmwareDir))
            {
                card.SetSyncStatus("Папка версии на диске не указана", "WarningBrush");
                continue;
            }

            card.SetSyncStatus("Синхронизация с диском…");
            try
            {
                // Проверка существования — тоже поход на сетевой диск, поэтому вместе с копированием
                // уходит в фоновый поток: на отвалившейся шаре она сама по себе висит секундами.
                // Ищем РЕАЛЬНУЮ папку сборки (точную или переименованного/перезалитого соседа), а не
                // слепо точный disk_path — иначе синхра падала «папки нет» на живой прошивке, и
                // «локальная копия устарела, обновляем» висела вечно (см. FirmwareDiskPresence).
                var dst = await Task.Run(() =>
                    FirmwareDiskPresence.ResolveVersionDir(result.FirmwareDir, result.VersionRaw) is not null
                        ? FirmwareSync.CopyToLocal(result) : null);
                if (generation != _searchGeneration) return;
                card.SetSyncStatus(dst is null
                    ? $"Папка версии не найдена на диске: {result.FirmwareDir}"
                    : $"✓ Локальная копия обновлена: {dst}", dst is null ? "WarningBrush" : "SuccessBrush");
            }
            catch (Exception ex)
            {
                if (generation != _searchGeneration) return;
                card.SetSyncStatus($"Не удалось синхронизировать: {ex.Message}. " +
                    "Повторить — «Ещё» → «Обновить локальную копию с диска».", "ErrorBrush");
            }
        }
    }

    private void PerformParamsSearch(string query)
    {
        var exact = ExactWordCheck.IsChecked == true;
        var files = SearchService.SearchWithLayoutFallback(query, exact, (q, ex) =>
        {
            var tokens = SearchService.Normalize(q).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return _services.Db.SearchParamFilesByTokens(tokens, ex);
        }, LayoutFallbackAllowed(query), out var usedFallback, out var convertedQuery);
        if (files.Count == 0)
        {
            ShowNoResults(query, NoResultsHint);
            return;
        }

        StatusLabel.Text = $"Найдено: {files.Count}";
        var canEditTags = _services.Cfg.CurrentRole() is "administrator";
        foreach (var file in files)
            ResultsPanel.Children.Add(MakeParamFileCard(file, canEditTags));

        if (!ConfirmLayoutFallback(query, usedFallback, convertedQuery))
            ShowNoResults(query, NoResultsHint);
    }

    private Border MakeParamFileCard(ParamFile file, bool canEditTags)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = $"{file.Filename} [{file.Manufacturer}]",
            Style = (Style)FindResource("SubtitleText"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.Join(" / ", new[] { file.GroupName, file.SubtypeName }.Where(s => !string.IsNullOrEmpty(s))),
            Style = (Style)FindResource("MutedText"),
            Margin = new Thickness(0, 2, 0, 0),
        });
        if (!string.IsNullOrEmpty(file.Description))
            panel.Children.Add(new TextBlock { Text = file.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });

        var tags = TagString.Parse(file.Tags);
        if (tags.Count > 0)
        {
            var tagsView = new TagBubbleEditor { Margin = new Thickness(0, 4, 0, 0) };
            tagsView.Configure(tags, null, readOnly: true);
            panel.Children.Add(tagsView);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var openBtn = new Button { Content = "Открыть", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        openBtn.Click += (_, _) => OpenParamFile(file);
        var openFolderBtn = new Button { Content = "Открыть папку с файлом", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        openFolderBtn.Click += (_, _) => OpenParamFileFolder(file);
        actions.Children.Add(openBtn);
        actions.Children.Add(openFolderBtn);
        if (canEditTags)
        {
            var tagsBtn = new Button { Content = "Теги", Style = (Style)FindResource("SecondaryButton") };
            tagsBtn.Click += (_, _) => EditParamTags(file);
            actions.Children.Add(tagsBtn);
        }
        panel.Children.Add(actions);

        return new Border { Style = (Style)FindResource("CardBorder"), Margin = new Thickness(0, 0, 0, 10), Child = panel };
    }

    /// <summary>Обход второго диска, идущий прямо сейчас — null, когда ничего не идёт. Обход всегда
    /// РОВНО ОДИН: он не привязан к запросу (просто читает диск), поэтому повторное «Найти» его не
    /// дублирует, а только переставляет фильтр выдачи (см. PerformSchemasSearchAsync). Именно на
    /// дубликатах и росла очередь фоновых операций у оператора: каждое нажатие вешало свой обход и
    /// свой индикатор занятости, пять нажатий — пять обходов одной и той же сетевой шары.</summary>
    private SchemasScan? _schemasScan;

    /// <summary>Больше этого числа карточек за один поиск по схемам не рисуем: обход диска сыплет
    /// совпадения по ходу дела, и на слишком общем запросе («а») их набралось бы столько, что окно
    /// встало бы на отрисовке — ровно та беда, от которой этот поиск и уводили в фон. Счётчик
    /// найденного при этом продолжает считать всё (см. SchemasScan.Matched), оператор видит, что
    /// показано не всё. Тот же потолок действует и на выдачу из прогретого кэша — см.
    /// SearchResultCap.</summary>
    private const int MaxSchemaCardsShown = SearchResultCap.MaxCards;

    /// <summary>Состояние одного обхода второго диска: что уже найдено на диске (Found — все файлы
    /// схем, независимо от запроса) и по какому запросу это сейчас фильтруется. Found пополняется на
    /// фоновом потоке обхода, а перечитывается на потоке интерфейса при смене запроса — отсюда Sync.
    /// Tokens/ExactWord/Generation, наоборот, пишет только поток интерфейса, а читает фоновый: под тем
    /// же замком, чтобы обход не отфильтровал пачку по половине нового запроса.</summary>
    private sealed class SchemasScan
    {
        public required string DiskPath { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public object Sync { get; } = new();

        /// <summary>Весь настроенный список расширений схем (Настройки → Иерархия), с которым этот
        /// обход был начат — в отличие от Extensions ниже (какие из уже НАЙДЕННЫХ файлов сейчас
        /// показывать) не меняется при RetargetSchemasScan: список того, что вообще считается схемой,
        /// не зависит от текста запроса.</summary>
        public HashSet<string>? ScanExtensions { get; init; }

        /// <summary>Все файлы схем, которые обход уже нашёл — по ним выдача перерисовывается
        /// мгновенно, когда оператор меняет запрос, не дожидаясь конца обхода.</summary>
        public List<SchematicHit> Found { get; } = new();

        /// <summary>Совпадения, найденные обходом, но ещё не доехавшие до экрана. Обход отдаёт файлы
        /// пачками по несколько тысяч в секунду, и отдельная отправка КАЖДОГО совпадения на поток
        /// интерфейса (Dispatcher.BeginInvoke) заваливала очередь этого потока настолько, что окно
        /// переставало отвечать — то есть потоковая выдача добивалась ровно обратного тому, ради
        /// чего делалась. Теперь совпадения копятся здесь, а на поток интерфейса уходит ОДНА заявка
        /// на отрисовку всего накопленного (см. FlushQueued/FlushSchemaCards).
        ///
        /// Всё, что здесь лежит, всегда подходит под ТЕКУЩИЙ запрос: совпадение проверяется под тем
        /// же замком, что и смена запроса, а сама смена (RetargetSchemasScan) очередь очищает.</summary>
        public List<SchematicHit> Pending { get; } = new();

        /// <summary>Заявка на отрисовку уже стоит в очереди потока интерфейса — вторую ставить не
        /// нужно, она заберёт и то, что добавится до её выполнения.</summary>
        public bool FlushQueued { get; set; }

        public string[] Tokens { get; set; } = Array.Empty<string>();
        public bool ExactWord { get; set; }
        public string Query { get; set; } = "";

        /// <summary>Фильтр по расширению файла (пустой — любое). Как и Tokens, читается фоновым
        /// обходом под замком: обход не должен отфильтровать пачку по половине нового фильтра.</summary>
        public HashSet<string> Extensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Поколение поиска, для которого сейчас рисуется выдача — см. _searchGeneration.</summary>
        public int Generation { get; set; }
    }

    // ── Выдача по схемам: что совпало, что нарисовано, «Показать ещё» ──────
    // Совпадений на широком запросе десятки тысяч, а карточки лежат в невиртуализованном StackPanel,
    // поэтому рисуется пачка в MaxSchemaCardsShown штук. Раньше на этом всё и заканчивалось: «показаны
    // первые 300», а как посмотреть остальные — никак. Теперь весь список совпадений остаётся в
    // памяти, и кнопка внизу дорисовывает следующую пачку.

    private readonly List<SchematicHit> _schemaMatched = new();
    private int _schemaShown;
    private Button? _schemaMoreButton;

    private void ClearSchemaResults()
    {
        _schemaMatched.Clear();
        _schemaShown = 0;
        _schemaMoreButton = null;
    }

    /// <summary>Кнопка «Показать ещё» всегда последняя в списке и всегда отражает актуальный остаток:
    /// во время обхода диска совпадения продолжают прибывать, и остаток растёт прямо под ней.</summary>
    private void SyncSchemaMoreButton()
    {
        if (_schemaMoreButton is not null)
        {
            ResultsPanel.Children.Remove(_schemaMoreButton);
            _schemaMoreButton = null;
        }
        var rest = _schemaMatched.Count - _schemaShown;
        if (rest <= 0) return;

        var next = Math.Min(rest, MaxSchemaCardsShown);
        _schemaMoreButton = new Button
        {
            Content = $"Показать ещё {next} (не показано: {rest})",
            Style = (Style)FindResource("SecondaryButton"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 16),
        };
        _schemaMoreButton.Click += (_, _) => ShowMoreSchemaCards();
        ResultsPanel.Children.Add(_schemaMoreButton);
    }

    private void ShowMoreSchemaCards()
    {
        var upTo = Math.Min(_schemaMatched.Count, _schemaShown + MaxSchemaCardsShown);
        for (; _schemaShown < upTo; _schemaShown++)
            ResultsPanel.Children.Insert(_schemaShown, MakeSchematicCard(_schemaMatched[_schemaShown]));
        SyncSchemaMoreButton();
    }

    private void PerformSchemasSearch(string query) => _ = PerformSchemasSearchAsync(query, _searchGeneration);

    /// <summary>Кнопка «Остановить» — прерывает идущий обход второго диска. Уже показанные карточки
    /// остаются на экране: оператор жмёт её именно тогда, когда нужное уже нашлось.</summary>
    private void StopSearch_Click(object sender, RoutedEventArgs e)
    {
        var scan = _schemasScan;
        if (scan is null) return;
        try { scan.Cts.Cancel(); } catch (ObjectDisposedException) { /* обход уже завершился сам */ }
        StopSearchButton.IsEnabled = false;
    }

    private void UpdateStopButton()
    {
        var running = _schemasScan is not null;
        StopSearchButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StopSearchButton.IsEnabled = running;
    }

    /// <summary>Асинхронная версия поиска по Схемам. Единственная по-настоящему медленная часть —
    /// обход второго диска в SchematicService: сетевая шара бывает под 400 ГБ, и Directory.
    /// EnumerateFiles по всем подпапкам раньше шёл прямо здесь, синхронно, на потоке интерфейса —
    /// первый поиск за сессию (и первый после смены пути второго диска, пока не наполнился кэш
    /// SchematicService) намертво вешал окно на всё время обхода.
    ///
    /// Обход уходит в Task.Run под тем же индикатором занятости внизу окна, что и остальные фоновые
    /// операции (см. AutoSyncMissingAsync, DownloadFirmware) — окно остаётся отзывчивым, оператор
    /// может уйти на другую вкладку. Только сам обход (EnsureScanned) идёт в фоне: подбор совпадений
    /// по конкретному запросу (Matches) — это уже дешёвая фильтрация прогретого кэша в памяти, и её
    /// нарочно оставляем синхронной на потоке интерфейса, потому что у неё out-параметры (usedFallback/
    /// convertedQuery для проверки раскладки клавиатуры) — через await/Task их не передать, а вызывать
    /// после await, как здесь, можно.
    ///
    /// generation — тот же приём, что и в ScanDiskFlagsAsync/AutoSyncMissingAsync: если за время обхода
    /// стартовал новый поиск (другой запрос, режим или фильтр), устаревшая выдача просто не рисуется —
    /// новая уже отрисована собственным запуском этого же метода.
    ///
    /// Три вещи, которых здесь раньше не было и по которым пришли жалобы:
    /// 1. Обход РОВНО ОДИН на диск. Раньше повторное «Найти» дожидалось той же задачи, но заводило свой
    ///    индикатор занятости — у оператора «росла очередь» ровно из этих ожиданий. Теперь второе
    ///    нажатие вообще не начинает новую операцию: обход к запросу не привязан, ему просто
    ///    переставляют фильтр.
    /// 2. Выдача появляется ПО ХОДУ обхода (onFound), а не после него — 400 ГБ шара читается минутами,
    ///    и всё это время экран был пуст.
    /// 3. Обход прерывается кнопкой «Остановить» — увидел нужное, дальше читать диск незачем.</summary>
    private async Task PerformSchemasSearchAsync(string query, int generation)
    {
        var diskPath = _services.Cfg.SecondDiskPath();
        if (string.IsNullOrEmpty(diskPath))
        {
            StatusLabel.Text = "Путь ко второму диску не задан";
            EmptyLabel.Text = "Второй диск не настроен — укажите его в разделе «Настройки»";
            EmptyLabel.Visibility = Visibility.Visible;
            return;
        }

        var exact = ExactWordCheck.IsChecked == true;
        var tokens = SchematicService.QueryTokens(query);
        var extensions = ActiveSchemaExtensions();
        // Весь настроенный список (Настройки → Иерархия → «Расширения поиска схем») — что вообще
        // считается схемой при обходе диска, независимо от того, какие галочки сейчас отмечены.
        var scanExtensions = ActiveSchemaScanExtensions();

        // Обход этого же диска уже идёт — новый не запускаем (см. п.1 в комментарии выше), а
        // перенацеливаем текущий на новый запрос: то, что диск успел отдать, перерисовывается сразу,
        // остальное дорисуется по мере обхода.
        if (_schemasScan is { } running && running.DiskPath == diskPath)
        {
            RetargetSchemasScan(running, query, tokens, exact, extensions, generation);
            return;
        }

        // Диск уже обойден в этой сессии (обычный случай для второго и следующих поисков) — фильтруем
        // готовый список в памяти, без фона, индикатора занятости и кнопки «Остановить».
        if (_services.Schematics.IsScanned(diskPath, scanExtensions))
        {
            ShowSchemasFromCache(diskPath, query, exact, extensions, scanExtensions);
            return;
        }

        var scan = new SchemasScan
        {
            DiskPath = diskPath,
            Cts = new CancellationTokenSource(),
            ScanExtensions = scanExtensions,
            Tokens = tokens,
            ExactWord = exact,
            Extensions = extensions,
            Query = query,
            Generation = generation,
        };
        _schemasScan = scan;
        UpdateStopButton();
        StatusLabel.Text = "Чтение второго диска… найдено: 0";

        var cancelled = false;
        using (_host.BeginBusy("Чтение второго диска…"))
        {
            try
            {
                await Task.Run(() => _services.Schematics.EnsureScanned(diskPath, scan.Cts.Token,
                    hit => OnSchemaFileFound(scan, hit), scanExtensions));
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            finally
            {
                if (ReferenceEquals(_schemasScan, scan)) _schemasScan = null;
                scan.Cts.Dispose();
                UpdateStopButton();
            }
        }

        // Выдача уже устарела — поверх этого поиска запустился другой (сменили режим/запрос так, что
        // обход стал не нужен). Рисовать итог нечего: экраном владеет тот, другой поиск.
        if (scan.Generation != _searchGeneration) return;

        if (cancelled)
        {
            // Последняя пачка совпадений могла не успеть доехать до потока интерфейса: заявка на
            // отрисовку ставится с фоновым приоритетом, а обход к этому моменту уже закончился и
            // обнулил _schemasScan — FlushSchemaCards такую пачку отбрасывает как чужую. Прерванный
            // обход кэша не пишет, перерисовать выдачу неоткуда, поэтому дочерпываем пачку сами.
            DrainPendingSchemaCards(scan);
            StatusLabel.Text = _schemaMatched.Count > 0
                ? $"Поиск остановлен — найдено: {ShownOf()}"
                : "Поиск остановлен — диск прочитан не полностью";
            if (_schemaMatched.Count == 0)
            {
                EmptyLabel.Text = "Поиск остановлен до того, как что-то нашлось — нажмите «Найти», чтобы прочитать диск заново";
                EmptyLabel.Visibility = Visibility.Visible;
            }
            return;
        }

        FinishSchemasScan(scan);
    }

    private const string SchemaNotFoundHint = "Схема не найдена — проверьте название шкафа или второй диск";
    private const string SchemaNotFoundFilteredHint = "Схема не найдена — возможно, дело в фильтре по расширению: «Фильтры» → «Сбросить фильтры»";

    /// <summary>Сколько найдено и сколько из этого показано — вторая часть появляется, только если
    /// упёрлись в потолок отрисовки.</summary>
    private string ShownOf() => SearchResultCap.Describe(_schemaMatched.Count, _schemaShown);

    /// <summary>Оператор нажал «Найти» с другим запросом, пока диск ещё читается. Обход общий и
    /// продолжается, меняется только то, что из него показывать.</summary>
    private void RetargetSchemasScan(SchemasScan scan, string query, string[] tokens, bool exact,
        HashSet<string> extensions, int generation)
    {
        List<SchematicHit> alreadyFound;
        lock (scan.Sync)
        {
            scan.Tokens = tokens;
            scan.ExactWord = exact;
            scan.Extensions = extensions;
            scan.Query = query;
            scan.Generation = generation;
            // Накопленное по СТАРОМУ запросу выбрасываем прямо здесь, под тем же замком, под которым
            // меняется сам запрос: иначе пачка, найденная секунду назад, дорисовалась бы поверх
            // выдачи нового запроса. Всё, что подходит под новый, ниже перерисовывается из Found.
            scan.Pending.Clear();
            alreadyFound = new List<SchematicHit>(scan.Found);
        }

        ResultsPanel.Children.Clear();
        ClearSchemaResults();
        foreach (var hit in alreadyFound)
        {
            if (!SchematicService.HitMatches(hit, tokens, exact)) continue;
            if (!SchematicService.HitMatchesExtension(hit, extensions)) continue;
            AddSchemaCard(hit);
        }
        SyncSchemaMoreButton();
        StatusLabel.Text = $"Чтение второго диска… найдено: {ShownOf()}";
        EmptyLabel.Visibility = _schemaShown > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_schemaShown == 0) EmptyLabel.Text = "Диск ещё читается — совпадений пока нет";
    }

    /// <summary>Диск уже обойден: выдача целиком, сразу и в привычном порядке (по названию шкафа).
    /// Здесь же — вопрос про раскладку клавиатуры: он имеет смысл только когда точно известно, что по
    /// набранному не нашлось ничего, а на середине обхода это ещё не известно.</summary>
    private void ShowSchemasFromCache(string diskPath, string query, bool exact, HashSet<string> extensions,
        HashSet<string>? scanExtensions = null)
    {
        var hits = _services.Schematics.Matches(diskPath, query, exact,
            LayoutFallbackAllowed(query), out var usedFallback, out var convertedQuery, extensions,
            scanExtensions ?? ActiveSchemaScanExtensions());
        if (hits.Count == 0)
        {
            ShowNoResults(query, extensions.Count > 0 ? SchemaNotFoundFilteredHint : SchemaNotFoundHint);
            return;
        }

        // Потолок отрисовки тот же, что и у потоковой выдачи: этот путь рисовал ВСЁ, что нашлось на
        // диске, и на широком запросе («1» по домашней папке — 23 446 совпадений в живом прогоне)
        // вешал окно на минуты. Остальное дорисовывается кнопкой «Показать ещё».
        ResultsPanel.Children.Clear();
        ClearSchemaResults();
        foreach (var hit in hits) AddSchemaCard(hit);
        SyncSchemaMoreButton();
        StatusLabel.Text = $"Найдено: {ShownOf()}";

        if (!ConfirmLayoutFallback(query, usedFallback, convertedQuery))
            ShowNoResults(query, SchemaNotFoundHint);
    }

    /// <summary>Обход дошёл до конца — выдача перерисовывается из прогретого кэша целиком.
    ///
    /// Раньше здесь только переписывался текст статуса поверх того, что успела нарисовать потоковая
    /// выдача, и этому нельзя было верить по двум причинам. Во-первых, пачка, найденная в самом конце
    /// обхода, до потока интерфейса не доезжала (заявка фонового приоритета против уже обнулённого
    /// _schemasScan) — живой прогон показал 108 карточек и «Найдено: 108» там, где на диске лежали 352
    /// подходящих файла. Во-вторых, потоковая выдача идёт в порядке обхода папок, а не по названию
    /// шкафа, как везде в программе. Перерисовка стоит копейки: кэш уже в памяти, а карточек всё
    /// равно не больше потолка отрисовки.
    ///
    /// Заодно только теперь точно известно, что по набранному не нашлось ничего, — и можно предложить
    /// ту же выдачу в другой раскладке клавиатуры.</summary>
    private void FinishSchemasScan(SchemasScan scan) =>
        ShowSchemasFromCache(scan.DiskPath, scan.Query, scan.ExactWord, scan.Extensions, scan.ScanExtensions);

    /// <summary>Дорисовать пачку совпадений, которую обход накопил, но не успел отдать интерфейсу.
    /// В отличие от FlushSchemaCards не сверяется с _schemasScan: вызывается уже ПОСЛЕ того, как обход
    /// снял себя с этого поля, и пачка всё ещё наша.</summary>
    private void DrainPendingSchemaCards(SchemasScan scan)
    {
        List<SchematicHit> batch;
        lock (scan.Sync)
        {
            scan.FlushQueued = false;
            if (scan.Pending.Count == 0) return;
            batch = new List<SchematicHit>(scan.Pending);
            scan.Pending.Clear();
        }
        if (scan.Generation != _searchGeneration) return;

        foreach (var hit in batch) AddSchemaCard(hit);
        SyncSchemaMoreButton();
    }

    /// <summary>Обход нашёл очередной файл схемы — вызывается на ФОНОВОМ потоке. Фильтр применяется
    /// здесь же, под замком (запрос мог смениться прямо сейчас), и на поток интерфейса уходят только
    /// совпадения: их единицы-десятки, а файлов на диске — сотни тысяч.</summary>
    private void OnSchemaFileFound(SchemasScan scan, SchematicHit hit)
    {
        bool needFlush;
        lock (scan.Sync)
        {
            scan.Found.Add(hit);
            if (!SchematicService.HitMatches(hit, scan.Tokens, scan.ExactWord)) return;
            if (!SchematicService.HitMatchesExtension(hit, scan.Extensions)) return;
            scan.Pending.Add(hit);
            needFlush = !scan.FlushQueued;
            scan.FlushQueued = true;
        }
        // Заявка ставится на фоновом приоритете и только одна на пачку: интерфейс сам решит, когда
        // ему удобно нарисовать накопленное, и не захлебнётся на широком запросе.
        if (needFlush)
            Dispatcher.BeginInvoke(new Action(() => FlushSchemaCards(scan)), DispatcherPriority.Background);
    }

    /// <summary>Нарисовать всё, что обход нашёл с прошлого раза. На потоке интерфейса.</summary>
    private void FlushSchemaCards(SchemasScan scan)
    {
        List<SchematicHit> batch;
        lock (scan.Sync)
        {
            scan.FlushQueued = false;
            if (scan.Pending.Count == 0) return;
            batch = new List<SchematicHit>(scan.Pending);
            scan.Pending.Clear();
        }
        // Пока пачка ехала на поток интерфейса, поиск мог смениться — тогда она уже не наша.
        if (!ReferenceEquals(_schemasScan, scan) || scan.Generation != _searchGeneration) return;

        foreach (var hit in batch) AddSchemaCard(hit);
        SyncSchemaMoreButton();
        StatusLabel.Text = $"Чтение второго диска… найдено: {ShownOf()}";
    }

    /// <summary>Совпадение попадает в общий список ВСЕГДА, а карточка рисуется, только пока не упёрлись
    /// в потолок — остальное дорисует «Показать ещё» (см. SyncSchemaMoreButton, её вызывает тот, кто
    /// добавил пачку целиком). Карточка вставляется перед кнопкой, а не в конец списка.</summary>
    private void AddSchemaCard(SchematicHit hit)
    {
        _schemaMatched.Add(hit);
        if (_schemaShown >= MaxSchemaCardsShown) return;
        EmptyLabel.Visibility = Visibility.Collapsed;
        ResultsPanel.Children.Insert(_schemaShown, MakeSchematicCard(hit));
        _schemaShown++;
    }

    // ── Keyboard-layout fallback prompt / learning ──────────────────────────
    // See SearchService.SearchWithLayoutFallback and Database.LayoutFallback — a search that found
    // nothing as typed but did find something after remapping the query to the other keyboard layout
    // asks the operator once whether that guess was right, and remembers the answer per exact query
    // string so a consistent answer eventually stops (or permanently skips) the prompt.

    private static string LayoutFallbackKey(string query) => query.Trim().ToUpperInvariant();

    private bool LayoutFallbackAllowed(string query) =>
        !_liveSearchPass &&
        _services.Cfg.LayoutFallbackEnabled() &&
        _services.Db.GetLayoutFallbackDecision(LayoutFallbackKey(query)) != LayoutFallbackDecision.Never;

    /// <summary>Call after rendering results. Returns false when the operator rejected the converted
    /// query — the caller should then discard the just-rendered results and show "not found" instead,
    /// since they weren't what was actually searched for.</summary>
    private bool ConfirmLayoutFallback(string originalQuery, bool usedFallback, string convertedQuery)
    {
        if (!usedFallback) return true;

        var key = LayoutFallbackKey(originalQuery);
        if (_services.Db.GetLayoutFallbackDecision(key) == LayoutFallbackDecision.Always) return true;

        // Already asked (and answered) this exact question earlier in this page instance's life —
        // a silent re-search of the SAME unchanged text (tab switch, background sync tick, closing
        // an edit dialog) reuses that answer instead of prompting again and recording a second vote.
        if (_lastLayoutFallbackResolvedKey == key) return _lastLayoutFallbackResolvedYes;

        var reply = AppMessageBox.Show(
            $"По запросу «{originalQuery}» ничего не найдено. Похоже, была включена не та раскладка " +
            $"клавиатуры — показаны результаты по «{convertedQuery}».\n\nЭто то, что вы искали?",
            "Проверка раскладки клавиатуры", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

        _services.Db.RecordLayoutFallbackFeedback(key, reply == MessageBoxResult.Yes, _services.Cfg.LayoutFallbackThreshold());
        _lastLayoutFallbackResolvedKey = key;
        _lastLayoutFallbackResolvedYes = reply == MessageBoxResult.Yes;
        return _lastLayoutFallbackResolvedYes;
    }

    private void ShowNoResults(string query, string hint)
    {
        ResultsPanel.Children.Clear();
        ClearSchemaResults();
        StatusLabel.Text = $"По запросу «{query}» ничего не найдено";
        EmptyLabel.Text = hint;
        EmptyLabel.Visibility = Visibility.Visible;
    }

    private Border MakeSchematicCard(SchematicHit hit)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = hit.CabinetName,
            Style = (Style)FindResource("SubtitleText"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = hit.Path,
            Style = (Style)FindResource("MutedText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var openBtn = new Button { Content = "Открыть", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        openBtn.Click += (_, _) => OpenSchematic(hit);
        actions.Children.Add(openBtn);
        // Рядом со схемой в папке шкафа обычно лежит остальное по нему же (исходник DWG, фотографии,
        // спецификация) — открыть папку целиком нужно не реже, чем сам файл.
        var folderBtn = new Button { Content = "Открыть папку", Style = (Style)FindResource("SecondaryButton") };
        folderBtn.ToolTip = "Открыть папку, в которой лежит этот файл — с выделенным файлом";
        folderBtn.Click += (_, _) => OpenSchematicFolder(hit);
        actions.Children.Add(folderBtn);
        panel.Children.Add(actions);

        return new Border { Style = (Style)FindResource("CardBorder"), Margin = new Thickness(0, 0, 0, 10), Child = panel };
    }

    private void OpenSchematic(SchematicHit hit)
    {
        if (!File.Exists(hit.Path))
        {
            AppMessageBox.Show($"Файл схемы не найден:\n{hit.Path}", "Схема", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TryOpen(hit.Path);
    }

    /// <summary>Проводник с выделенным файлом (/select), а не просто открытая папка: в папке шкафа
    /// лежит десяток файлов, и найденный в ней ещё надо отыскать глазами. Файла уже нет (диск успел
    /// измениться с момента обхода) — открываем хотя бы саму папку.</summary>
    private static void OpenSchematicFolder(SchematicHit hit)
    {
        if (File.Exists(hit.Path))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{hit.Path}\"") { UseShellExecute = true });
                return;
            }
            catch (Exception) { /* ниже — обычное открытие папки */ }
        }

        var folder = Path.GetDirectoryName(hit.Path);
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) TryOpen(folder);
        else AppMessageBox.Show($"Папка не найдена:\n{folder}", "Схема", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void EditParamTags(ParamFile file)
    {
        var title = $"{file.Filename} [{file.Manufacturer}]";
        var dlg = new EditParamTagsDialog(_services.Db, file.Tags, title) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        _services.Db.UpdateParamFileTags(file.Id!.Value, dlg.ResultTags);
        _host.ShowStatus($"Теги обновлены: {file.Filename}", category: NotificationCategory.FirmwareAndParams);
        PerformSearch();
    }

    private static void OpenParamFile(ParamFile file)
    {
        var full = Path.Combine(file.DiskPath, file.Filename);
        if (File.Exists(full)) TryOpen(full);
        else if (Directory.Exists(file.DiskPath)) TryOpen(file.DiskPath);
        else AppMessageBox.Show($"Файл не найден:\n{full}", "Параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void OpenParamFileFolder(ParamFile file)
    {
        if (Directory.Exists(file.DiskPath)) TryOpen(file.DiskPath);
        else AppMessageBox.Show($"Папка не найдена:\n{file.DiskPath}", "Параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void EditTags(HierarchyResult result)
    {
        var v = _services.Db.GetFwVersionById(result.FwVersionId);
        if (v is null)
        {
            AppMessageBox.Show("Версия не найдена в базе.", "Теги", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dlg = new EditFirmwareDialog(_services, v, $"{result.Name} {result.VersionRaw}") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        _services.Db.UpdateFwVersion(v.Id!.Value, dlg.ResultDescription, dlg.ResultTags, dlg.ResultLaunchTypes,
            dlg.ResultHmiExecutableHint, dlg.ResultExecutableHint);
        // Что именно изменилось (какие теги добавились/убрались и у какой прошивки по-человечески)
        // сообщает сам ReportChanges — прежняя строка «Теги обновлены: 2.0.0042.0003» дублировала
        // его и была заметно менее внятной.
        EditFirmwareDialog.ReportChanges(dlg, _host);
        PerformSearch();
    }

    // ── Local cache helpers ───────────────────────────────────────────────

    private static string SanitizeName(string name) => LocalFirmwareCache.SanitizeName(name);

    private static bool HasLocal(HierarchyResult result) => LocalFirmwareCache.HasVersion(result.Name, result.VersionRaw);

    private static bool HasAnyLocal(HierarchyResult result) => LocalFirmwareCache.HasAny(result.Name);

    /// <summary>preferredName, when set, is FwVersionRecord.ExecutableHint — the file the operator
    /// explicitly picked at upload time because the folder had nothing matching a recognized
    /// extension (see UploadView.PromptExecutableHint). Takes priority over the "first non-doc
    /// file" heuristic below, which is otherwise arbitrary when a folder holds several files
    /// (driver DLLs etc. alongside the real executable).</summary>
    private static string? FindUsableFile(string dir, string? preferredName = null)
    {
        if (!Directory.Exists(dir)) return null;
        // Подсказка может указывать на файл во ВЛОЖЕННОЙ папке («Driver\App.exe») — разбирает и
        // проверяет её ExecutableHintResolver, он же отсекает мусорные значения (абсолютный путь,
        // «..»), которые могли приехать с другой машины через синхронизацию конфига.
        var preferred = ExecutableHintResolver.Resolve(dir, preferredName);
        if (preferred is not null) return preferred;
        return Directory.EnumerateFiles(dir).FirstOrDefault(f =>
            Path.GetExtension(f).ToLowerInvariant() is var ext && ext != ".md" && ext != ".txt" && ext != ".log");
    }

    // Детект «первый файл с нужным расширением в дереве папки» переехал в PlcOpenResolver.
    // FindByExtensions — им пользуются оба резолвера (ПЛК и панель), здесь дубля больше нет.

    private static string? FindSiblingFolder(HierarchyResult result, string folderName)
    {
        // От РЕАЛЬНОЙ папки версии, а не от записанного disk_path: если её переименовали/перезалили,
        // точного пути нет, и папка контроллера — с ней рядом лежат «Карта ВВ»/«Инструкция»/
        // «Карта Modbus» — иначе не находилась. Ровно жалоба «инструкция на диске есть, а в карточке её
        // взаимодействия нет».
        //
        // Сама развилка «своя папка внутри версии или общая папка контроллера» живёт в VersionLayout
        // (docs/hierarchy-rework-plan.md, этап 4) — это ЕДИНСТВЕННОЕ место в приложении, откуда
        // читаются документы версии, поэтому одной этой строчкой обе раскладки становятся видны
        // одинаково. Заодно чинится ОПЦ новой раскладки: её родитель — папка «ОПЦ», а не контроллер,
        // и прежний Directory.GetParent искал документы внутри «ОПЦ» (ControllerFolderOf знает про
        // лишний уровень).
        var versionDir = ResolvedNetworkDir(result);
        if (!Directory.Exists(versionDir)) return null;
        return VersionLayout.SlotBestReadFolder(versionDir, VersionLayout.ControllerFolderOf(versionDir), folderName);
    }

    // ── Card actions ──────────────────────────────────────────────────────

    /// <summary>Папки, в которых может лежать эта версия, в порядке предпочтения: точная папка версии
    /// в локальном кэше, остальные локальные версии этой прошивки (свежие первыми), и только потом
    /// сетевая папка — открывать локальную копию всегда лучше, чем дёргать сеть.</summary>
    private static IEnumerable<string> CandidateFolders(HierarchyResult result)
    {
        var baseDir = Path.Combine(ConfigService.LocalFw, SanitizeName(result.Name));
        yield return Path.Combine(baseDir, result.VersionRaw);

        if (Directory.Exists(baseDir))
            foreach (var sub in Directory.EnumerateDirectories(baseDir).OrderByDescending(d => d))
                yield return sub;

        var net = ResolvedNetworkDir(result);
        if (!string.IsNullOrEmpty(net)) yield return net;
    }

    /// <summary>Реальная папка версии на сетевом диске: точный disk_path, если он на месте, иначе
    /// соседняя папка ТОЙ ЖЕ сборки — точную могли переименовать/перезалить под другой датой, а
    /// disk_path устареть после синхры (файлы лежат рядом под другим именем, см.
    /// FirmwareDiskPresence.ResolveVersionDir). Раньше открытие/синхра/обход шли по точному disk_path
    /// вслепую и упирались в «папки нет», хотя прошивка на диске есть — та же жалоба, что чинил показ
    /// карточки, но там робастной сделали только проверку «прятать ли», а открытие/синхру — нет.
    /// Возвращаем исходный путь, если реальную папку не нашли: пусть вызывающий покажет его в «не
    /// найдено». Ходит на диск — звать из фонового обхода или по клику, не из отрисовки.</summary>
    private static string ResolvedNetworkDir(HierarchyResult result) =>
        FirmwareDiskPresence.ResolveVersionDir(result.FirmwareDir, result.VersionRaw) ?? result.FirmwareDir;

    /// <summary>Папки ИМЕННО этой версии — без соседних версий из локального кэша, в отличие от
    /// CandidateFolders.
    ///
    /// Для «чем открыть» подмена соседней версией — приемлемый фоллбэк (лучше открыть хоть что-то,
    /// чем сказать «не найдено»), а для .lfs/.psl — нет: карточка тогда пишет «LFS ✓» у версии, где
    /// его не выкладывали, кнопка «Загрузить в ПЛК» подставляет .lfs ЧУЖОЙ версии, и в контроллер
    /// уезжает не та прошивка. Поймано живьём: версия с одним .psl показывала «LFS ✓», потому что
    /// рядом в кэше лежала более свежая версия с собранным файлом.</summary>
    private static IEnumerable<string> VersionFolders(HierarchyResult result)
    {
        yield return Path.Combine(ConfigService.LocalFw, SanitizeName(result.Name), result.VersionRaw);
        var net = ResolvedNetworkDir(result);
        if (!string.IsNullOrEmpty(net)) yield return net;
    }

    private static string? ResolveOpenTarget(HierarchyResult result)
    {
        foreach (var dir in CandidateFolders(result))
            if (FindUsableFile(dir, result.ExecutableHint) is { } target) return target;

        // Ничего похожего на открываемый файл — но если папка версии на диске есть (точная или
        // соседняя той же сборки), показать хотя бы её содержимое полезнее, чем сказать «не найдено».
        var net = ResolvedNetworkDir(result);
        return Directory.Exists(net) ? net : null;
    }

    /// <summary>Проекты, где ПЛК и панель лежат в ОДНОЙ папке — это не только KINCO: то же бывает у
    /// любого вендора, где панель собирается отдельным файлом рядом с программой ПЛК. Поэтому сначала
    /// смотрим на явно указанный оператором исполняемый файл (работает для любого проекта и для
    /// файлов во вложенных папках), и только если подсказки нет — на старый детект по расширениям,
    /// иначе «первый подходящий файл в папке» может открыть файл панели вместо программы ПЛК.</summary>
    private void OpenPlc(HierarchyResult result)
    {
        // Что именно откроется — решает PlcOpenResolver, тот же, что посчитал расширение для подписи
        // кнопки при обходе диска. Держать эти два решения в одном месте обязательно: разойдись они —
        // на кнопке было бы написано одно расширение, а открывался бы другой файл.
        var target = PlcOpenResolver.Resolve(PlcSources(result));
        if (target is null)
        {
            AppMessageBox.Show("Прошивка не найдена локально.\nНажмите «Скачать» для копирования с сервера.", "Открыть",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        TryOpen(target);
    }

    /// <summary>Зеркально OpenPlc: решение «что откроется» живёт в HmiOpenResolver (отдельная папка
    /// HMI-проекта → явно указанный файл панели внутри папки версии → детект по расширениям), он же
    /// посчитал расширение для подписи кнопки при обходе диска — разойтись они не могут.</summary>
    private void OpenHmi(HierarchyResult result)
    {
        if (HmiOpenResolver.Resolve(HmiSources(result)) is { } target)
        {
            // Проект формата «папка» (.fsprj), загруженный когда-то ОДНИМ файлом: соседние файлы —
            // модель панели, драйверы — на диск не попали, и FStudio откроет его пустым, ругнувшись
            // «модель HMI не соответствует текущему программному обеспечению». Само по себе это уже не
            // случится (см. FirmwareAttachmentsService.CopyHmiProject), но у всех, кто загрузил панель
            // до этого, такие проекты на диске лежат — и молча открывать их нельзя: человек решит, что
            // сломан сам проект, а не то, как его положили.
            if (HmiProjectFormat.IsStrippedCopy(target, result.VersionRaw))
            {
                switch (HmiRepairDialog.Ask(Window.GetWindow(this), target))
                {
                    case HmiRepairChoice.Repair:
                        RepairHmiProject(result);
                        return;
                    case HmiRepairChoice.OpenAnyway:
                        break;
                    default:
                        return;
                }
            }
            TryOpen(target);
            return;
        }
        // Два разных «не найдено»: у версии записан путь к отдельному проекту панели, но на диске его
        // нет — это про конкретный путь; иначе панель просто не нашлась рядом с версией.
        if (!string.IsNullOrEmpty(result.HmiPath))
            AppMessageBox.Show($"HMI-проект не найден.\nПуть: {result.HmiPath}", "HMI-проект",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            AppMessageBox.Show("Прошивка не найдена локально.\nНажмите «Скачать» для копирования с сервера.",
                "Открыть HMI", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Заменяет лежащий на диске обрубок проекта панели нормальной папкой — прямо отсюда, с
    /// карточки, а не «сходите в модерацию»: путь через модерацию для таких версий не работал (поле
    /// «Открывать файл» указывало в папку прошивки), да и наткнувшийся на пустой проект наладчик идти
    /// туда не обязан. Оригинал спрашиваем у него: на диске его нет — программа когда-то забрала
    /// оттуда один файл, а остальное осталось на машине программиста.</summary>
    private void RepairHmiProject(HierarchyResult result)
    {
        var root = _services.Cfg.RootPath();
        var record = _services.Db.GetFwVersionById(result.FwVersionId);
        var names = _services.Db.GetFwVersionNames(result.FwVersionId);
        if (record is null || names is null || string.IsNullOrEmpty(root))
        {
            AppMessageBox.Show("Починить не получилось: не нашлась запись версии либо недоступен сетевой диск.",
                "HMI-проект", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Оригинальный файл HMI-проекта",
            Filter = "HMI-проект (*.fsprj)|*.fsprj|Все файлы (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        var picked = dlg.FileName;

        // Выбрали такую же копию без окружения (проще всего — ту же самую, диалог открывается там, где
        // был последний раз): скопировать её обратно значит получить ровно тот же пустой проект.
        if (HmiProjectFormat.IsStrippedCopy(picked, record.VersionRaw))
        {
            AppMessageBox.Show(
                "Этот файл лежит без своего окружения — то есть это такая же копия, а не оригинал.\n\n" +
                "Выберите проект там, где он открывается нормально: рядом с ним должны лежать модель " +
                "панели и драйверы.", "HMI-проект", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (HmiProjectFormat.SelectionWarning(picked) is { } warning)
        {
            AppMessageBox.Show(warning, "HMI-проект", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var request = new FirmwareAttachmentsRequest
        {
            RootPath = root,
            GroupName = names.Value.GroupName,
            SubtypeName = names.Value.SubtypeName,
            ControllerName = names.Value.ControllerName,
            HmiSourcePath = picked,
        };
        FirmwareAttachmentsResult applied;
        try
        {
            applied = FirmwareAttachmentsService.Apply(_services.Db, _services.Hierarchy, record, request);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppMessageBox.Show($"Не удалось скопировать проект панели:\n{ex.Message}", "HMI-проект",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (applied.Warnings.Count > 0)
        {
            AppMessageBox.Show(string.Join("\n", applied.Warnings), "HMI-проект",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Запись обновилась (Apply пишет hmi_path) — карточки в выдаче держат прежний путь, поэтому
        // поиск перечитываем, а открываем уже по новому пути из записи.
        PerformSearch();
        if (HmiOpenResolver.Resolve(HmiSources(result) with { HmiPath = record.HmiPath }) is { } repaired)
            TryOpen(repaired);
    }

    private void OpenFirmwareFolder(HierarchyResult result)
    {
        var target = ResolveOpenTarget(result);
        if (target is null)
        {
            AppMessageBox.Show("Прошивка не найдена локально.\nНажмите «Скачать» для копирования с сервера.", "Открыть папку",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (Directory.Exists(target))
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
    }

    /// <summary>Открыть папку версии именно на сетевом диске (result.FirmwareDir уже приведён к нашей
    /// форме диска в SearchService через FirmwarePathLocalizer) — в отличие от «Открыть папку с
    /// файлами», которая предпочитает локальную копию. Нужно, чтобы наладчик вручную почистил лишние
    /// файлы (несколько .lfs в одной папке) прямо там, где их видят коллеги.</summary>
    private void OpenServerFolder(HierarchyResult result)
    {
        // Реальная папка сборки, а не записанный disk_path вслепую: папку могли переименовать или
        // перезалить под другой датой — тогда точного пути нет, а файлы лежат рядом (ResolvedNetworkDir).
        var dir = string.IsNullOrEmpty(result.FirmwareDir) ? "" : ResolvedNetworkDir(result);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            AppMessageBox.Show(
                string.IsNullOrEmpty(dir)
                    ? "У этой версии не записан путь к папке на диске."
                    : $"Папка версии не найдена на сетевом диске:\n{dir}",
                "Открыть папку на сервере", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    private void OpenLoaderFile(HierarchyResult result, string extension, string label)
    {
        var path = LoaderFiles.ResolvePreferHint(VersionFolders(result), result.ExecutableHint, extension);
        if (path is null)
        {
            AppMessageBox.Show($"Файл {label} не найден ни в локальной копии, ни в папке версии на диске.",
                $"Открыть файл {label}", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        TryOpen(path);
    }

    /// <summary>Сохраняет выбор способа подключения, сделанный прямо на карточке (см.
    /// FirmwareCard.ConnectionModeChangeRequested). Настройка машинная и общая для всех карточек —
    /// та же, что в Настройки → Лоадер, поэтому уже открытые карточки просто подхватят её при
    /// следующей отрисовке, отдельно их обновлять не нужно.</summary>
    private void SaveConnectionMode(string mode)
    {
        if (mode == _services.Cfg.LoaderConnectionMode()) return;
        _services.Cfg.SetLoaderConnectionMode(mode);
        // Настройка одна на машину — показываем её сразу на ВСЕХ карточках выдачи, иначе соседние
        // списки продолжали бы показывать прежний способ до перестроения выдачи.
        foreach (var card in ResultsPanel.Children.OfType<FirmwareCard>()) card.ShowConnectionMode(mode);
        var caption = LoaderConnectionSettings.ModeCaption(LoaderConnectionSettings.ParseMode(mode));
        _host.ShowStatus($"Подключение к ПЛК: {caption}");
    }

    /// <summary>Строка для подсказки списка на карточке: адрес и адаптер видны, не заходя в Настройки —
    /// «Ethernet» без адреса ничего наладчику не говорит.</summary>
    private string ConnectionHintText()
    {
        var parts = new List<string>();
        var ip = _services.Cfg.LoaderPlcIp();
        if (ip.Length > 0) parts.Add($"адрес ПЛК: {ip}");
        var adapter = _services.Cfg.LoaderNetworkAdapter();
        if (adapter.Length > 0) parts.Add($"адаптер: {adapter}");
        return parts.Count == 0
            ? "Адрес ПЛК и сетевой адаптер — в Настройки → Лоадер."
            : string.Join(", ", parts) + " (меняются в Настройки → Лоадер)";
    }

    /// <summary>Открывает интерактивную загрузку через Automation API. Готовый LFS имеет приоритет;
    /// при его отсутствии PSL собирается и загружается production-пайплайном Loader, а собранный
    /// файл уезжает в папку версии НА ДИСКЕ (LoaderJob.NetworkFolder) — чтобы следующий наладчик на
    /// другой машине увидел готовый .lfs, а не собирал его заново.
    /// Доступность Automation проверяет сам диалог (LoaderDialog.EnsureAvailable) — до открытия окна.</summary>
    private void OpenLoader(HierarchyResult result)
    {
        var versionName = $"{result.Name} {result.VersionRaw}".Trim();
        if (!LoaderDialog.EnsureAvailable(Window.GetWindow(this), _services.Cfg)) return;

        // ExecutableHint — выбранный оператором в модерации файл. Он обязателен здесь: в папке версии
        // может лежать пачка прошивок (пожарные шкафы), и «первый найденный .lfs» уезжал в контроллер
        // вместо нужного.
        var files = LoaderFiles.FindDeploymentFiles(VersionFolders(result), result.ExecutableHint);
        if (!files.HasAny)
        {
            AppMessageBox.Show(
                "Для этой версии не найден файл LFS или PSL.",
                "Загрузка в ПЛК",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        LoaderDialog.ShowDeploy(Window.GetWindow(this), _services.Cfg, new LoaderJob
        {
            VersionName = versionName,
            SourcePath = files.LfsPath ?? files.PslPath!,
            NetworkFolder = result.FirmwareDir ?? "",
            LocalFolder = Path.Combine(ConfigService.LocalFw, SanitizeName(result.Name), result.VersionRaw),
        });
    }

    /// <summary>Копирование — в фоновом потоке, с индикатором внизу окна: папка версии тянется с
    /// сетевой шары и бывает в сотни мегабайт, а раньше на всё это время окно просто замирало.</summary>
    private async void DownloadFirmware(HierarchyResult result)
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(result.FirmwareDir))
        {
            AppMessageBox.Show("Путь к диску или папка прошивки не заданы.", "Скачать", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!Directory.Exists(result.FirmwareDir))
        {
            AppMessageBox.Show($"Папка не найдена на диске:\n{result.FirmwareDir}", "Скачать", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string dst;
        try
        {
            using (_host.BeginBusy($"Скачивание: {result.Name} {result.VersionRaw}".Trim()))
                dst = await Task.Run(() => FirmwareSync.CopyToLocal(result));
        }
        catch (Exception ex)
        {
            AppMessageBox.Show(ex.Message, "Скачать", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _host.ShowStatus($"Скопировано: {result.Name}");

        var dlg = new SyncResultDialog(result, dst) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();

        PerformSearch();
    }

    private void OpenMap(HierarchyResult result)
    {
        var path = ResolveDocFile(result, result.IoMapPath, "Карта ВВ");
        if (path is null)
        {
            AppMessageBox.Show($"Файл карты не найден.\nПуть: {result.IoMapPath}", "Карта in/out", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TryOpen(path);
    }

    private void OpenModbusMap(HierarchyResult result)
    {
        var path = ResolveDocFile(result, result.ModbusMapPath, "Карта Modbus");
        if (path is null)
        {
            AppMessageBox.Show($"Файл карты Modbus не найден.\nПуть: {result.ModbusMapPath}", "Карта modbus", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TryOpen(path);
    }

    /// <summary>Доп. материалы версии (см. FwAttachment): краткое руководство наладчика, специфика
    /// работы объекта, прошивка ПЛК поставщика. Один файл открывается сразу — предлагать выбор из
    /// одного пункта незачем; несколько — списком, где рядом с именем стоят вид и комментарий: именно
    /// они и объясняют, какой из файлов сейчас нужен.</summary>
    private void OpenExtraFiles(HierarchyResult result)
    {
        var files = _services.Db.GetFwAttachments(result.FwVersionId);
        if (files.Count == 0)
        {
            AppMessageBox.Show("К этой версии доп. материалы не приложены.", "Доп. материалы",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (files.Count == 1)
        {
            OpenExtraFile(files[0]);
            return;
        }

        var options = files.Select((f, i) => new PickOptionDialog.Option(i, ExtraFileLabel(f))).ToList();
        var picked = PickOptionDialog.Pick(Window.GetWindow(this), "Доп. материалы",
            $"{result.Name} {result.VersionRaw}".Trim() + " — что открыть?", options, 0);
        if (picked is int index && index >= 0 && index < files.Count) OpenExtraFile(files[index]);
    }

    private static string ExtraFileLabel(AntarusPoFinder.Core.Domain.FwAttachment f)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(f.Kind)) parts.Add(f.Kind);
        parts.Add(f.Filename);
        if (!string.IsNullOrWhiteSpace(f.Comment)) parts.Add(f.Comment);
        return string.Join(" — ", parts);
    }

    private void OpenExtraFile(AntarusPoFinder.Core.Domain.FwAttachment attachment)
    {
        var path = FirmwarePathLocalizer.Localize(attachment.DiskPath, _services.Cfg.RootPath());
        if (!File.Exists(path))
        {
            AppMessageBox.Show($"Файл не найден на диске:\n{path}", "Доп. материалы",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TryOpen(path);
    }

    private void OpenParams(HierarchyResult result)
    {
        var files = _services.Db.GetParamFiles(subtypeId: result.SubtypeId);
        if (files.Count == 0)
        {
            AppMessageBox.Show("Параметры для этого типа шкафа не найдены.\nЗагрузите параметры в разделе «Параметры».",
                "Параметры", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new CardParamsDialog(files, _services.Cfg) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }

    private void OpenInstructions(HierarchyResult result)
    {
        // Не общий ResolveDocFile: у инструкции своя папка чтения — зеркало на третьем диске,
        // если оно есть (см. InstructionFolder).
        var path = DocFileResolver.Resolve(result.InstructionsPath, InstructionFolder(result, CurrentDocRoots()));
        if (path is null)
        {
            AppMessageBox.Show($"Файл инструкций не найден.\nПуть: {result.InstructionsPath}", "Инструкции", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TryOpen(path);
    }

    private void OpenInstructionFolder(HierarchyResult result)
    {
        var doc = ResolveInstruction(result, CurrentDocRoots());
        if (doc.Folder is not null && Directory.Exists(doc.Folder))
            TryOpen(doc.Folder);
        else
            AppMessageBox.Show("Папка инструкции не найдена.", "Инструкция", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void EditInstruction(HierarchyResult result)
    {
        var doc = ResolveInstruction(result, CurrentDocRoots());
        if (doc.Docx is not null && File.Exists(doc.Docx))
            TryOpen(doc.Docx);
        else
            AppMessageBox.Show("Исходный документ (docx) инструкции не найден — править нечего.",
                "Инструкция", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task OpenInstructionPdfAsync(HierarchyResult result)
    {
        var pdf = await EnsureInstructionPdfAsync(result);
        if (pdf is not null) TryOpen(pdf);
    }

    private async Task PrintInstructionAsync(HierarchyResult result)
    {
        var pdf = await EnsureInstructionPdfAsync(result);
        if (pdf is null) return;
        // Инструкция печатается обычным листом с двусторонней печатью (переворот по длинному краю),
        // в отличие от паспорта-буклета — см. DuplexPrinting/PrintTicketXml.
        var outcome = DuplexPrinting.PrintInstructionDuplex(pdf);
        _host.ShowStatus(outcome.DuplexApplied
            ? "Инструкция отправлена на печать (двусторонняя)"
            : "Инструкция отправлена на печать — двустороннюю печать выставить не удалось, проверьте настройки принтера");
    }

    /// <summary>Готовый к печати PDF инструкции: если docx правили после последней сборки (или pdf ещё
    /// нет), пересобирает его из docx рядом с исходником, иначе отдаёт уже лежащий PDF. Сама работа —
    /// в PrintableDocActions. null — печатать/показывать нечего (сообщение об этом уже показано).</summary>
    private async Task<string?> EnsureInstructionPdfAsync(HierarchyResult result)
    {
        var doc = ResolveInstruction(result, CurrentDocRoots());
        return await PrintableDocActions.EnsurePdfAsync(doc, _host, "Инструкция", "инструкции",
            "AntarusInstr", "пунктом «Редактировать инструкцию (docx)»");
    }

    /// <summary>«QR и этикетка» — наклейка со ссылкой на инструкцию. В QR уходит именно PDF, если он
    /// есть: ссылку открывают телефоном, и docx на телефоне бесполезен. Пересборкой PDF из docx тут
    /// НЕ занимаемся (в отличие от «Печать инструкции»): печать этикетки не должна на минуту
    /// подвешивать оператора запуском Word — берём то, что на диске уже лежит.</summary>
    private void ShowInstructionLabel(HierarchyResult result)
    {
        var roots = CurrentDocRoots();
        var doc = ResolveInstruction(result, roots);
        // Документа ещё нет — берём заглушку: она лежит ровно по тому пути, по которому потом ляжет
        // настоящая инструкция, поэтому напечатанный сейчас QR не придётся переклеивать
        // (см. InstructionStub).
        var file = doc.Pdf ?? doc.Newest ?? doc.Docx ?? InstructionStub.ExistingIn(InstructionFolder(result, roots));
        InstructionLabelWindow.ShowFor(Window.GetWindow(this), _services, _host,
            result.Name, result.VersionRaw, file);
    }

    private void ShowHistory(HierarchyResult result)
    {
        var versions = _services.Db.GetFwVersionsHistory(result.SubtypeId, result.ControllerId);
        var dlg = new HistoryDialog(result.Name, versions, _services, result.SubtypeId, result.ControllerId, _host)
        {
            Owner = Window.GetWindow(this)
        };
        dlg.ShowDialog();
    }

    /// <summary>Copies just the numeric version stem — since Round 31 (see FirmwareNaming.
    /// BuildFirmwareFilename) that IS the on-disk firmware filename; the group/subtype/controller
    /// prefix this used to also copy (e.g. "НГР-КПЧ_SMH5_2.1.042...") was the OLD naming convention,
    /// dropped when the filename was simplified — copying it here was stale and no longer matched
    /// what's actually on disk. ToUpperInvariant matches BuildFirmwareFilename's own casing (moot in
    /// practice since VersionRaw is purely digits/dots/underscore, but kept for consistency/safety).</summary>
    private void CopyName(HierarchyResult result)
    {
        var text = result.VersionRaw.ToUpperInvariant();
        Clipboard.SetText(text);
        _host.ShowStatus($"Скопировано: {text}");
    }

    private static void TryOpen(string path) => PrintableDocActions.Open(path);
}
