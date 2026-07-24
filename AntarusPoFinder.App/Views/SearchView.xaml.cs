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
        if (e.Key == Key.Enter) PerformSearch();
    }

    // ── Фильтры ───────────────────────────────────────────────────────────
    // Отдельной кнопкой и свёрнуты по умолчанию: в строке поиска и так три кнопки, а фильтры нужны
    // не каждый раз. Списки наполняются при первом раскрытии и при каждом последующем — справочники
    // и теги меняются (загрузили прошивку, приехала синхронизация), и показывать вчерашний набор
    // значений хуже, чем лишний раз спросить БД: это локальные быстрые запросы, не поход на диск.

    private sealed record FilterOption(string Label, int? Id = null, string? Text = null);

    private bool FiltersVisible => FiltersPanel.Visibility == Visibility.Visible;

    private void ToggleFilters_Click(object sender, RoutedEventArgs e)
    {
        if (FiltersVisible)
        {
            FiltersPanel.Visibility = Visibility.Collapsed;
            FiltersToggle.Content = ActiveFilters().IsEmpty ? "Фильтры ▾" : "Фильтры ▾ ●";
            return;
        }

        ReloadFilterOptions();
        FiltersPanel.Visibility = Visibility.Visible;
        FiltersToggle.Content = "Фильтры ▴";
    }

    private void ReloadFilterOptions()
    {
        var groups = _services.Db.GetAllEquipmentGroups();
        var subtypes = _services.Db.GetAllEquipmentSubtypes();
        var controllers = _services.Db.GetAllControllerModels();

        FillFilter(FilterGroupCombo, "Тип шкафа: любой",
            groups.Where(g => g.Id is not null).Select(g => new FilterOption(g.Name, g.Id)));
        FillFilter(FilterSubtypeCombo, "Подтип: любой",
            subtypes.Where(s => s.Id is not null && s.Name != "—").Select(s => new FilterOption(s.Name, s.Id)));
        FillFilter(FilterControllerCombo, "Контроллер: любой",
            controllers.Where(c => c.Id is not null).Select(c => new FilterOption(c.Name, c.Id)));
        FillFilter(FilterLaunchCombo, "Тип пуска: любой",
            ConfigService.LaunchTypes.Select(lt => new FilterOption(lt, null, lt)));
    }

    private static void FillFilter(ComboBox combo, string anyLabel, IEnumerable<FilterOption> options)
    {
        var previous = (combo.SelectedItem as FilterOption)?.Label;
        var items = new List<FilterOption> { new(anyLabel) };
        items.AddRange(options
            .Where(o => !string.IsNullOrWhiteSpace(o.Label))
            .GroupBy(o => o.Label, StringComparer.CurrentCultureIgnoreCase).Select(g => g.First())
            .OrderBy(o => o.Label, StringComparer.CurrentCultureIgnoreCase));
        combo.ItemsSource = items;
        var restored = previous is null ? 0 : items.FindIndex(o => o.Label == previous);
        combo.SelectedIndex = restored < 0 ? 0 : restored;
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => PerformSearch();

    private void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        ResetFilterCombos();
        UpdateFiltersButton();
        PerformSearch();
    }

    private void ResetFilterCombos()
    {
        foreach (var combo in new[] { FilterGroupCombo, FilterSubtypeCombo, FilterControllerCombo, FilterLaunchCombo })
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
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

    private void UpdateFiltersButton()
    {
        if (FiltersToggle is null) return;
        var active = !ActiveFilters().IsEmpty;
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
        SearchInput.Text = "";
        // Фильтры сбрасываются вместе с запросом: иначе «сбросил поиск, а всё равно ничего не
        // находит» — забытый фильтр в свёрнутой панели не виден.
        ResetFilterCombos();
        UpdateFiltersButton();
        ResultsPanel.Children.Clear();
        StatusLabel.Text = "";
        EmptyLabel.Text = "Введите запрос и нажмите «Найти»";
        EmptyLabel.Visibility = Visibility.Visible;
        SearchInput.Focus();
    }

    /// <summary>Re-runs the current query in the new mode as soon as the user flips Прошивки/
    /// Параметры/Схемы or the exact-word checkbox — matches the immediate feedback of a live
    /// filter instead of requiring another click on «Найти».</summary>
    private void SearchMode_Changed(object sender, RoutedEventArgs e)
    {
        AnimateModeThumb();
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
        try { _services.Db.RecordFwUsage(_lastUsageKey, result.FwVersionId); }
        catch { /* статистика — вспомогательная вещь, ронять из-за неё действие оператора нельзя */ }
    }

    private void PerformFirmwareSearch(string query)
    {
        var exact = ExactWordCheck.IsChecked == true;
        var filters = ActiveFilters();
        var results = SearchService.Search(_services.Db, query, exact,
            LayoutFallbackAllowed(query), out var usedFallback, out var convertedQuery, filters);
        if (results.Count == 0)
        {
            ShowNoResults(query, filters.IsEmpty ? NoResultsHint : NoResultsFilteredHint);
            return;
        }

        // Что именно искали — нужно, чтобы записать выбор оператора по этому запросу
        // (см. RecordUsage): выбор осмыслен только в паре с запросом, который его показал.
        _lastUsageKey = SearchService.UsageKey(query);
        StatusLabel.Text = filters.IsEmpty ? $"Найдено: {results.Count}" : $"Найдено: {results.Count} (с фильтрами)";
        _subtypesById = _services.Db.GetAllEquipmentSubtypes().Where(s => s.Id is not null).ToDictionary(s => s.Id!.Value);
        var canEditTags = _services.Cfg.CurrentRole() is "administrator";
        var autoSync = _services.Cfg.SearchAutoSync();
        // Подключён ли настоящий лоадер — сейчас всегда false (в приложении только заготовка,
        // StubFirmwareLoaderBackend.IsAvailable = false), поэтому «Загрузить в ПЛК» станет основной
        // кнопкой карточки лишь когда лоадер реально допилят. Считаем один раз на всю выдачу.
        var loaderConnected = FirmwareLoaderFactory.Create(_services.Cfg.LoaderExePath()).IsAvailable;
        var pending = new List<(FirmwareCard Card, HierarchyResult Result, FirmwareCardFlags Flags)>();

        foreach (var result in results)
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
                CanEditTags = canEditTags,
                AutoSync = autoSync,
                LoaderConnected = loaderConnected,
                DiskScanPending = true,
                // По контроллеру/подсказке файла — до обхода диска; после обхода уточняется тем, что
                // реально нашлось рядом (см. ScanDiskFlagsAsync).
                IsSegnetics = SegneticsProject.IsRelevant(result.Controller, result.ExecutableHint),
            };

            var card = new FirmwareCard();
            card.Configure(result, flags);
            // Выбор версии засчитывается на действиях «взял эту прошивку» — открыл проект/файл,
            // залил в контроллер, скачал локально (см. RecordUsage).
            card.OpenFolderRequested += (s, _) => OpenFirmwareFolder(((FirmwareCard)s!).Result);
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
            card.DownloadRequested += (s, _) => { RecordUsage(((FirmwareCard)s!).Result); DownloadFirmware(((FirmwareCard)s!).Result); };
            card.MapRequested += (s, _) => OpenMap(((FirmwareCard)s!).Result);
            card.ModbusMapRequested += (s, _) => OpenModbusMap(((FirmwareCard)s!).Result);
            card.ParamsRequested += (s, _) => OpenParams(((FirmwareCard)s!).Result);
            card.InstructionsRequested += (s, _) => OpenInstructions(((FirmwareCard)s!).Result);
            card.HistoryRequested += (s, _) => ShowHistory(((FirmwareCard)s!).Result);
            card.CopyNameRequested += (s, _) => CopyName(((FirmwareCard)s!).Result);
            card.TagsEditRequested += (s, _) => EditTags(((FirmwareCard)s!).Result);
            ResultsPanel.Children.Add(card);

            pending.Add((card, result, flags));
        }

        if (!ConfirmLayoutFallback(query, usedFallback, convertedQuery))
        {
            ShowNoResults(query, NoResultsHint);
            return;
        }

        _ = ScanDiskFlagsAsync(pending, _searchGeneration);
    }

    // ── Что лежит рядом с версией на диске ────────────────────────────────
    // Обход папки версии (LFS/PSL/HMI/карта ВВ) — единственная по-настоящему медленная часть выдачи:
    // папка живёт на сетевом диске компании, который регулярно отвечает через раз. Поэтому карточки
    // рисуются сразу, а этот обход идёт следом в фоне и дорисовывает их по мере готовности.

    private readonly record struct DiskScan(bool HasLfs, bool HasPsl, bool HasHmi,
        bool HasIoMap, bool HasInstructions, bool HasModbus,
        string? PlcOpenExtension, string? HmiOpenExtension);

    /// <summary>Один обход на версию вместо трёх (LFS/PSL + HMI по расширениям): все три признака
    /// вытаскиваются за одно перечисление файлов первой же папки-кандидата, где вообще что-то
    /// нашлось. Только папки САМОЙ версии (VersionFolders): признак «есть LFS» должен относиться к
    /// той версии, на карточке которой он написан.</summary>
    private static DiskScan ScanVersionFolder(HierarchyResult result)
    {
        bool lfs = false, psl = false, hmiFile = false;
        foreach (var dir in VersionFolders(result))
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
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
        var hasInstructions = ResolveDocFile(result, result.InstructionsPath, "Инструкция") is not null;
        var hasModbus = ResolveDocFile(result, result.ModbusMapPath, "Карта Modbus") is not null;
        // Расширение того файла, который реально откроет «Открыть прошивку ПЛК» — считается тем же
        // резолвером, что и само открытие (PlcOpenResolver), поэтому подпись кнопки не может
        // разойтись с тем, что откроется, и работает для ЛЮБОГО проекта, не только .psl/.lfs.
        var plcExt = PlcOpenResolver.ResolveExtension(PlcSources(result));
        // То же самое для панели: расширение считает HmiOpenResolver, он же потом и открывает (OpenHmi).
        // Только когда панель вообще есть — иначе это лишний обход папок ради подписи несуществующей кнопки.
        var hmiExt = hasHmi ? HmiOpenResolver.ResolveExtension(HmiSources(result)) : null;
        return new DiskScan(lfs, psl, hasHmi, hasIoMap, hasInstructions, hasModbus, plcExt, hmiExt);
    }

    /// <summary>Папки, по которым PlcOpenResolver ищет файл проекта ПЛК — см. его комментарий про
    /// разницу между наборами.</summary>
    private static PlcOpenSources PlcSources(HierarchyResult result) => new()
    {
        CandidateFolders = CandidateFolders(result).ToList(),
        VersionFolders = VersionFolders(result).ToList(),
        FilteredFolders = new[] { Path.Combine(ConfigService.LocalFw, SanitizeName(result.Name)), result.FirmwareDir ?? "" },
        ExecutableHint = result.ExecutableHint,
        NetworkFolder = result.FirmwareDir,
    };

    /// <summary>Источники файла панели для HmiOpenResolver — зеркально PlcSources.
    /// Ходит на диск (FindSiblingFolder) — вызывать из фонового обхода или по клику, не в отрисовке.</summary>
    private static HmiOpenSources HmiSources(HierarchyResult result) => new()
    {
        HmiPath = result.HmiPath,
        SiblingHmiFolder = FindSiblingFolder(result, "HMI"),
        ExecutableHint = result.HmiExecutableHint,
        CandidateFolders = CandidateFolders(result).ToList(),
        FilteredFolders = new[] { Path.Combine(ConfigService.LocalFw, SanitizeName(result.Name)), result.FirmwareDir ?? "" },
    };

    /// <summary>Самый свежий актуальный файл документа (карта ВВ / инструкция / карта Modbus) —
    /// общая папка документа рядом с папкой контроллера, см. DocFileResolver.
    /// Ходит на диск — вызывать из фонового потока (ScanVersionFolder) или по клику, не в отрисовке.</summary>
    private static string? ResolveDocFile(HierarchyResult result, string? storedPath, string sharedFolderName) =>
        DocFileResolver.Resolve(storedPath, FindSiblingFolder(result, sharedFolderName));

    /// <summary>Дорисовывает карточки признаками с диска, потом запускает автосинхронизацию тех, у
    /// кого нет локальной копии. Последовательно и с проверкой поколения выдачи — по тем же причинам,
    /// что и AutoSyncMissingAsync.</summary>
    private async Task ScanDiskFlagsAsync(List<(FirmwareCard Card, HierarchyResult Result, FirmwareCardFlags Flags)> cards, int generation)
    {
        var pendingSync = new List<(FirmwareCard Card, HierarchyResult Result)>();

        foreach (var (card, result, baseFlags) in cards)
        {
            if (generation != _searchGeneration) return;

            var scan = await Task.Run(() => ScanVersionFolder(result));
            if (generation != _searchGeneration) return;

            card.Configure(result, baseFlags with
            {
                HasLfs = scan.HasLfs,
                HasPsl = scan.HasPsl,
                HasHmi = scan.HasHmi,
                HasIoMap = scan.HasIoMap,
                HasInstructions = scan.HasInstructions,
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
                var dst = await Task.Run(() =>
                    Directory.Exists(result.FirmwareDir) ? FirmwareSync.CopyToLocal(result) : null);
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
    /// показано не всё.</summary>
    private const int MaxSchemaCardsShown = 300;

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

        /// <summary>Все файлы схем, которые обход уже нашёл — по ним выдача перерисовывается
        /// мгновенно, когда оператор меняет запрос, не дожидаясь конца обхода.</summary>
        public List<SchematicHit> Found { get; } = new();

        public string[] Tokens { get; set; } = Array.Empty<string>();
        public bool ExactWord { get; set; }
        public string Query { get; set; } = "";

        /// <summary>Поколение поиска, для которого сейчас рисуется выдача — см. _searchGeneration.</summary>
        public int Generation { get; set; }

        /// <summary>Номер текущего фильтра — растёт на каждую смену запроса у ЖИВОГО обхода
        /// (RetargetSchemasScan). Совпадения едут на поток интерфейса через Dispatcher, и без этого
        /// номера уже отправленные в очередь карточки по СТАРОМУ запросу дорисовались бы поверх
        /// выдачи нового: проверки одного лишь Generation тут мало — при смене запроса обход остаётся
        /// тем же самым и его Generation тоже становится новым.</summary>
        public int FilterEpoch { get; set; }

        /// <summary>Сколько совпало и сколько из них реально нарисовано (см. MaxSchemaCardsShown).</summary>
        public int Matched { get; set; }
        public int Shown { get; set; }
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

        // Обход этого же диска уже идёт — новый не запускаем (см. п.1 в комментарии выше), а
        // перенацеливаем текущий на новый запрос: то, что диск успел отдать, перерисовывается сразу,
        // остальное дорисуется по мере обхода.
        if (_schemasScan is { } running && running.DiskPath == diskPath)
        {
            RetargetSchemasScan(running, query, tokens, exact, generation);
            return;
        }

        // Диск уже обойден в этой сессии (обычный случай для второго и следующих поисков) — фильтруем
        // готовый список в памяти, без фона, индикатора занятости и кнопки «Остановить».
        if (_services.Schematics.IsScanned(diskPath))
        {
            ShowSchemasFromCache(diskPath, query, exact);
            return;
        }

        var scan = new SchemasScan
        {
            DiskPath = diskPath,
            Cts = new CancellationTokenSource(),
            Tokens = tokens,
            ExactWord = exact,
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
                    hit => OnSchemaFileFound(scan, hit)));
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
            StatusLabel.Text = scan.Matched > 0
                ? $"Поиск остановлен — найдено: {ShownOf(scan)}"
                : "Поиск остановлен — диск прочитан не полностью";
            if (scan.Matched == 0)
            {
                EmptyLabel.Text = "Поиск остановлен до того, как что-то нашлось — нажмите «Найти», чтобы прочитать диск заново";
                EmptyLabel.Visibility = Visibility.Visible;
            }
            return;
        }

        FinishSchemasScan(scan);
    }

    private const string SchemaNotFoundHint = "Схема не найдена — проверьте название шкафа или второй диск";

    /// <summary>Сколько найдено и сколько из этого показано — вторая часть появляется, только если
    /// упёрлись в потолок отрисовки.</summary>
    private static string ShownOf(SchemasScan scan) =>
        scan.Matched > scan.Shown ? $"{scan.Matched} (показаны первые {scan.Shown})" : scan.Matched.ToString();

    /// <summary>Оператор нажал «Найти» с другим запросом, пока диск ещё читается. Обход общий и
    /// продолжается, меняется только то, что из него показывать.</summary>
    private void RetargetSchemasScan(SchemasScan scan, string query, string[] tokens, bool exact, int generation)
    {
        List<SchematicHit> alreadyFound;
        lock (scan.Sync)
        {
            scan.Tokens = tokens;
            scan.ExactWord = exact;
            scan.Query = query;
            scan.Generation = generation;
            scan.FilterEpoch++;
            scan.Matched = 0;
            scan.Shown = 0;
            alreadyFound = new List<SchematicHit>(scan.Found);
        }

        ResultsPanel.Children.Clear();
        foreach (var hit in alreadyFound)
        {
            if (!SchematicService.HitMatches(hit, tokens, exact)) continue;
            AddSchemaCard(scan, hit);
        }
        StatusLabel.Text = $"Чтение второго диска… найдено: {ShownOf(scan)}";
        EmptyLabel.Visibility = scan.Shown > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (scan.Shown == 0) EmptyLabel.Text = "Диск ещё читается — совпадений пока нет";
    }

    /// <summary>Диск уже обойден: выдача целиком, сразу и в привычном порядке (по названию шкафа).
    /// Здесь же — вопрос про раскладку клавиатуры: он имеет смысл только когда точно известно, что по
    /// набранному не нашлось ничего, а на середине обхода это ещё не известно.</summary>
    private void ShowSchemasFromCache(string diskPath, string query, bool exact)
    {
        var hits = _services.Schematics.Matches(diskPath, query, exact,
            LayoutFallbackAllowed(query), out var usedFallback, out var convertedQuery);
        if (hits.Count == 0)
        {
            ShowNoResults(query, SchemaNotFoundHint);
            return;
        }

        StatusLabel.Text = $"Найдено: {hits.Count}";
        foreach (var hit in hits)
            ResultsPanel.Children.Add(MakeSchematicCard(hit));

        if (!ConfirmLayoutFallback(query, usedFallback, convertedQuery))
            ShowNoResults(query, SchemaNotFoundHint);
    }

    /// <summary>Обход дошёл до конца. Если по набранному не нашлось ничего — только теперь это точно
    /// известно, и можно предложить ту же выдачу в другой раскладке клавиатуры (кэш уже тёплый, так
    /// что перепроверка стоит копейки).</summary>
    private void FinishSchemasScan(SchemasScan scan)
    {
        if (scan.Matched == 0)
        {
            ShowSchemasFromCache(scan.DiskPath, scan.Query, scan.ExactWord);
            return;
        }
        StatusLabel.Text = $"Найдено: {ShownOf(scan)}";
    }

    /// <summary>Обход нашёл очередной файл схемы — вызывается на ФОНОВОМ потоке. Фильтр применяется
    /// здесь же, под замком (запрос мог смениться прямо сейчас), и на поток интерфейса уходят только
    /// совпадения: их единицы-десятки, а файлов на диске — сотни тысяч.</summary>
    private void OnSchemaFileFound(SchemasScan scan, SchematicHit hit)
    {
        bool matched;
        int epoch;
        lock (scan.Sync)
        {
            scan.Found.Add(hit);
            matched = SchematicService.HitMatches(hit, scan.Tokens, scan.ExactWord);
            epoch = scan.FilterEpoch;
        }
        if (!matched) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            // Пока карточка ехала на поток интерфейса, поиск мог смениться — тогда она уже не наша.
            if (!ReferenceEquals(_schemasScan, scan) || scan.Generation != _searchGeneration) return;
            if (scan.FilterEpoch != epoch) return;
            AddSchemaCard(scan, hit);
            StatusLabel.Text = $"Чтение второго диска… найдено: {ShownOf(scan)}";
        }));
    }

    private void AddSchemaCard(SchemasScan scan, SchematicHit hit)
    {
        scan.Matched++;
        if (scan.Shown >= MaxSchemaCardsShown) return;
        scan.Shown++;
        EmptyLabel.Visibility = Visibility.Collapsed;
        ResultsPanel.Children.Add(MakeSchematicCard(hit));
    }

    // ── Keyboard-layout fallback prompt / learning ──────────────────────────
    // See SearchService.SearchWithLayoutFallback and Database.LayoutFallback — a search that found
    // nothing as typed but did find something after remapping the query to the other keyboard layout
    // asks the operator once whether that guess was right, and remembers the answer per exact query
    // string so a consistent answer eventually stops (or permanently skips) the prompt.

    private static string LayoutFallbackKey(string query) => query.Trim().ToUpperInvariant();

    private bool LayoutFallbackAllowed(string query) =>
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

    private void EditParamTags(ParamFile file)
    {
        var title = $"{file.Filename} [{file.Manufacturer}]";
        var dlg = new EditParamTagsDialog(_services.Db, file, title) { Owner = Window.GetWindow(this) };
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
        EditFirmwareDialog.ReportChanges(dlg, _host);
        _host.ShowStatus($"Теги обновлены: {result.VersionRaw}", category: NotificationCategory.FirmwareAndParams);
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
        if (!Directory.Exists(result.FirmwareDir)) return null;
        var ctrlDir = Directory.GetParent(result.FirmwareDir)?.FullName;
        if (ctrlDir is null) return null;
        var candidate = Path.Combine(ctrlDir, folderName);
        return Directory.Exists(candidate) ? candidate : null;
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

        if (!string.IsNullOrEmpty(result.FirmwareDir)) yield return result.FirmwareDir;
    }

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
        if (!string.IsNullOrEmpty(result.FirmwareDir)) yield return result.FirmwareDir;
    }

    private static string? ResolveOpenTarget(HierarchyResult result)
    {
        foreach (var dir in CandidateFolders(result))
            if (FindUsableFile(dir, result.ExecutableHint) is { } target) return target;

        // Ничего похожего на открываемый файл — но если папка версии на диске есть, показать хотя бы
        // её содержимое полезнее, чем сказать «не найдено».
        return Directory.Exists(result.FirmwareDir) ? result.FirmwareDir : null;
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

    private void OpenLoaderFile(HierarchyResult result, string extension, string label)
    {
        var path = LoaderFiles.FindIn(VersionFolders(result), extension);
        if (path is null)
        {
            AppMessageBox.Show($"Файл {label} не найден ни в локальной копии, ни в папке версии на диске.",
                $"Открыть файл {label}", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        TryOpen(path);
    }

    /// <summary>Загрузка в контроллер через лоадер. Подставляет найденный .lfs — заливается именно
    /// он; если его нет, диалог открывается с пустым полем, и оператор выбирает файл сам (лоадера
    /// всё равно пока нет — см. LoaderDialog).</summary>
    private void OpenLoader(HierarchyResult result)
    {
        var source = LoaderFiles.FindIn(VersionFolders(result), LoaderFiles.LfsExtension) ?? "";
        LoaderDialog.ShowFlash(Window.GetWindow(this), _services.Cfg,
            $"{result.Name} {result.VersionRaw}".Trim(), source);
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
        var path = ResolveDocFile(result, result.InstructionsPath, "Инструкция");
        if (path is null)
        {
            AppMessageBox.Show($"Файл инструкций не найден.\nПуть: {result.InstructionsPath}", "Инструкции", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TryOpen(path);
    }

    private void ShowHistory(HierarchyResult result)
    {
        var versions = _services.Db.GetFwVersionsHistory(result.SubtypeId, result.ControllerId);
        var dlg = new HistoryDialog(result.Name, versions) { Owner = Window.GetWindow(this) };
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

    private static void TryOpen(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            var reply = AppMessageBox.Show(
                $"Не удалось открыть файл:\n{path}\n\nВозможно, не установлена программа для этого типа файлов.\n\nОткрыть папку с файлом?",
                "Открыть файл", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
            if (reply != MessageBoxResult.Yes) return;
            try
            {
                var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                if (folder is not null) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch { /* give up gracefully, matches Python's swallowed OSError here */ }
        }
    }
}
