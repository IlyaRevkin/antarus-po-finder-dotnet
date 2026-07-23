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

    private static readonly string[] KincoPlcExts = { ".kpr", ".kpj", ".kpro", ".cpj", ".prj" };
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
        // Теги — только те, что реально стоят на версиях: справочник целиком (GetAllTags) содержит и
        // те, которые никому ещё не проставили, и выбирать их в фильтре бессмысленно — пустая выдача.
        _allTagOptions = _services.Db.GetTagsInUse().Select(t => new FilterOption(t, null, t)).ToList();
        FillFilter(FilterTagCombo, TagFilterAnyLabel, _allTagOptions);
    }

    private const string TagFilterAnyLabel = "Тег: любой";
    private List<FilterOption> _allTagOptions = new();

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

    /// <summary>Набранное в поле тега сужает список — это и есть «поиск тегов»: список у наладчика
    /// длинный, и пролистывать его до нужного названия шкафа руками бессмысленно. Пока оператор
    /// печатает, выбор не сбрасываем и поиск не перезапускаем — только когда он выберет тег из
    /// списка (SelectionChanged) или очистит поле.</summary>
    private void FilterTagText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_reloadingTagOptions || FilterTagCombo.ItemsSource is null) return;

        var text = FilterTagCombo.Text?.Trim() ?? "";
        var matching = text.Length == 0
            ? _allTagOptions
            : _allTagOptions.Where(o => o.Label.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();

        _reloadingTagOptions = true;
        var items = new List<FilterOption> { new(TagFilterAnyLabel) };
        items.AddRange(matching);
        FilterTagCombo.ItemsSource = items;
        FilterTagCombo.Text = text;
        FilterTagCombo.IsDropDownOpen = text.Length > 0 && matching.Count > 0;
        _reloadingTagOptions = false;
    }

    private bool _reloadingTagOptions;

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_reloadingTagOptions) return;
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
        _reloadingTagOptions = true;
        foreach (var combo in new[] { FilterGroupCombo, FilterSubtypeCombo, FilterControllerCombo, FilterLaunchCombo })
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        FilterTagCombo.ItemsSource = new List<FilterOption> { new(TagFilterAnyLabel) }.Concat(_allTagOptions).ToList();
        FilterTagCombo.SelectedIndex = 0;
        _reloadingTagOptions = false;
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
            Tag = (FilterTagCombo.SelectedItem as FilterOption)?.Text,
        };
    }

    private void UpdateFiltersButton()
    {
        if (FiltersToggle is null) return;
        var active = !ActiveFilters().IsEmpty;
        FiltersToggle.Content = FiltersVisible ? "Фильтры ▴" : active ? "Фильтры ▾ ●" : "Фильтры ▾";
    }

    private void Search_Click(object sender, RoutedEventArgs e) => PerformSearch();

    /// <summary>Re-runs the last query so results (rollback status, tags, etc.) don't go stale —
    /// the page instance is cached across navigation, so switching away and back would otherwise
    /// keep showing whatever was on screen before other tabs changed the data.</summary>
    public void RefreshIfActive()
    {
        // Выдача бывает и без запроса — одними фильтрами; её тоже нужно освежать.
        if (!string.IsNullOrWhiteSpace(SearchInput.Text) || !ActiveFilters().IsEmpty)
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

    private readonly record struct DiskScan(bool HasLfs, bool HasPsl, bool HasHmi, bool HasMap);

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
        var hasMap = string.IsNullOrEmpty(result.IoMapPath) && FindSiblingFolder(result, "Карта ВВ") is not null;
        return new DiskScan(lfs, psl, hasHmi, hasMap);
    }

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
                HasMap = scan.HasMap,
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

    /// <summary>Обход второго диска, который выполняет прямо сейчас фоновая задача EnsureScanned —
    /// null, когда ничего не идёт. Ключ дедупликации — путь диска: пока обход конкретного диска не
    /// завершился, повторный поиск по Схемам (второй клик «Найти», фоновый RefreshIfActive, смена
    /// фильтра/режима) ждёт ТУ ЖЕ задачу вместо второго параллельного Directory.EnumerateFiles по тем
    /// же сотням гигабайт.</summary>
    private Task? _schemasScanTask;
    private string? _schemasScanDiskPath;

    private void PerformSchemasSearch(string query) => _ = PerformSchemasSearchAsync(query, _searchGeneration);

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
    /// новая уже отрисована собственным запуском этого же метода.</summary>
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

        const string schemaNotFoundHint = "Схема не найдена — проверьте название шкафа или второй диск";
        var exact = ExactWordCheck.IsChecked == true;

        using var busy = _host.BeginBusy("Чтение второго диска…");

        // Проверка/присваивание ниже — синхронный код до первого await, выполняется целиком на потоке
        // интерфейса без прерываний, а SearchView — единственный потребитель SchematicService (одна
        // страница, живёт в кэше вкладок MainWindowViewModel), поэтому гонки с другим вызовом этого же
        // метода здесь быть не может — то же рассуждение, что и у _searchGeneration.
        if (_schemasScanTask is null || _schemasScanDiskPath != diskPath)
        {
            _schemasScanDiskPath = diskPath;
            _schemasScanTask = Task.Run(() => _services.Schematics.EnsureScanned(diskPath));
        }
        var scanTask = _schemasScanTask;

        try { await scanTask; }
        finally
        {
            // Обход этого диска завершён (успешно или с ошибкой) — следующий поиск прогревает кэш
            // заново своим Task.Run; если диск не поменялся, SchematicService.Scanned() сам увидит
            // валидный кэш и вернёт его почти мгновенно, так что это не лишний обход с нуля.
            if (ReferenceEquals(_schemasScanTask, scanTask)) { _schemasScanTask = null; _schemasScanDiskPath = null; }
        }

        // Выдача уже устарела — другой поиск запустился и закончился (или ещё идёт) поверх этого.
        if (generation != _searchGeneration) return;

        var hits = _services.Schematics.Matches(diskPath, query, exact,
            LayoutFallbackAllowed(query), out var usedFallback, out var convertedQuery);
        if (hits.Count == 0)
        {
            ShowNoResults(query, schemaNotFoundHint);
            return;
        }

        StatusLabel.Text = $"Найдено: {hits.Count}";
        foreach (var hit in hits)
            ResultsPanel.Children.Add(MakeSchematicCard(hit));

        if (!ConfirmLayoutFallback(query, usedFallback, convertedQuery))
            ShowNoResults(query, schemaNotFoundHint);
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
        EditFirmwareDialog.ReportAttachments(dlg.AttachmentsResult, _host);
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

    private static string? FindFilteredIn(string dir, string[] exts) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            : null;

    private static string? FindFiltered(HierarchyResult result, string[] exts)
    {
        var localDir = Path.Combine(ConfigService.LocalFw, SanitizeName(result.Name));
        return FindFilteredIn(localDir, exts) ?? FindFilteredIn(result.FirmwareDir, exts);
    }

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

    /// <summary>Файл, на который указывает подсказка исполняемого файла, в первой же папке, где он
    /// реально есть. Null — если подсказки нет или файл не найден нигде.</summary>
    private static string? ResolveHintedFile(HierarchyResult result, string? hint)
    {
        if (ExecutableHintResolver.Normalize(hint) is null) return null;
        foreach (var dir in CandidateFolders(result))
            if (ExecutableHintResolver.Resolve(dir, hint) is { } resolved) return resolved;
        return null;
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
        if (ResolveHintedFile(result, result.ExecutableHint) is { } hinted)
        {
            TryOpen(hinted);
            return;
        }
        // Проект Segnetics: рядом лежат .psl (исходник SMLogix) и .lfs (скомпилированный файл для
        // заливки). Открывать надо именно .psl — .lfs не открывается ничем, кроме лоадера, а общая
        // эвристика «первый непонятный файл в папке» вполне могла взять его и молча открыть блокнот
        // (карточка теперь и называет эту кнопку «Открыть проект PSL», когда исходник найден).
        if (LoaderFiles.FindIn(VersionFolders(result), LoaderFiles.PslExtension) is { } psl)
        {
            TryOpen(psl);
            return;
        }
        if (FindFiltered(result, KincoHmiExts) is not null && FindFiltered(result, KincoPlcExts) is not null)
        {
            OpenFiltered(result, KincoPlcExts, "ПЛК");
            return;
        }
        OpenFirmware(result);
    }

    /// <summary>Зеркально OpenPlc: отдельная папка HMI-проекта (чекбокс «Добавить HMI-проект» при
    /// загрузке) → явно указанный файл панели внутри папки версии → старый детект по расширениям.</summary>
    private void OpenHmi(HierarchyResult result)
    {
        if (!string.IsNullOrEmpty(result.HmiPath))
        {
            OpenHmiProject(result);
            return;
        }
        if (ResolveHintedFile(result, result.HmiExecutableHint) is { } hinted)
        {
            TryOpen(hinted);
            return;
        }
        OpenFiltered(result, KincoHmiExts, "HMI");
    }

    private void OpenFirmware(HierarchyResult result)
    {
        var target = ResolveOpenTarget(result);
        if (target is null)
        {
            AppMessageBox.Show("Прошивка не найдена локально.\nНажмите «Скачать» для копирования с сервера.", "Открыть",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
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

    private void OpenFiltered(HierarchyResult result, string[] exts, string label)
    {
        var target = FindFiltered(result, exts);
        if (target is null)
        {
            AppMessageBox.Show("Прошивка не найдена локально.\nНажмите «Скачать» для копирования с сервера.", $"Открыть {label}",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
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
        var path = result.IoMapPath;
        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path)))
            path = FindSiblingFolder(result, "Карта ВВ") ?? path;

        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path)))
        {
            AppMessageBox.Show($"Файл карты не найден.\nПуть: {result.IoMapPath}", "Карта in/out", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TryOpen(path);
    }

    private void OpenModbusMap(HierarchyResult result)
    {
        var path = result.ModbusMapPath;
        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path)))
            path = FindSiblingFolder(result, "Карта Modbus") ?? path;

        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path)))
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
        var path = result.InstructionsPath;
        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path)))
            path = FindSiblingFolder(result, "Инструкция") ?? path;

        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path)))
        {
            AppMessageBox.Show($"Файл инструкций не найден.\nПуть: {result.InstructionsPath}", "Инструкции", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TryOpen(path);
    }

    /// <summary>Отдельно загруженный HMI-проект (чекбокс «Добавить HMI-проект» в загрузке). Если это
    /// папка и оператор указал, какой файл внутри исполняемый (HmiExecutableHint), открывается сразу
    /// он, а не просто папка. Вызывается только из OpenHmi — см. порядок вариантов там.</summary>
    private void OpenHmiProject(HierarchyResult result)
    {
        var path = result.HmiPath;
        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path)))
            path = FindSiblingFolder(result, "HMI") ?? path;

        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path)))
        {
            AppMessageBox.Show($"HMI-проект не найден.\nПуть: {result.HmiPath}", "HMI-проект", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var hmiExe = ExecutableHintResolver.Resolve(path, result.HmiExecutableHint);
        if (hmiExe is not null) { TryOpen(hmiExe); return; }
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
