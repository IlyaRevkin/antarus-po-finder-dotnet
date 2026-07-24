using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Что именно есть у этой конкретной записи — считает SearchView (он один знает про диск,
/// локальный кэш и БД), карточка только рисует. Отдельным типом, а не десятком bool-аргументов:
/// параметров стало столько, что позиционный вызов перестал читаться.</summary>
public sealed record FirmwareCardFlags
{
    /// <summary>Локально лежит ИМЕННО эта версия.</summary>
    public bool HasLocal { get; init; }

    /// <summary>Локально лежит хоть какая-то версия этой прошивки (тогда речь про обновление, а не
    /// про первую загрузку — влияет только на текст статуса).</summary>
    public bool HasAnyLocal { get; init; }

    public bool HasParams { get; init; }
    public bool HasHmi { get; init; }

    /// <summary>Есть РЕАЛЬНЫЙ файл карты ВВ / инструкции / карты Modbus — проверено обходом диска, а не
    /// просто заполненным путём в БД (тот мог указывать на версию, где файла уже нет — отсюда была
    /// жалоба «кнопка есть, а открывать нечего»). Считается в фоне (SearchView.ScanVersionFolder): до
    /// конца обхода все три false, пункты появляются в «Ещё» по мере готовности. Открывается всегда
    /// самый свежий файл в общей папке документа, а не путь конкретной версии.</summary>
    public bool HasIoMap { get; init; }
    public bool HasInstructions { get; init; }
    public bool HasModbus { get; init; }

    /// <summary>Расширение файла, который реально откроет «Открыть прошивку ПЛК» — считает
    /// PlcOpenResolver при обходе диска, тот же резолвер и открывает. Пишется на кнопке в скобках для
    /// ЛЮБОГО проекта, не только .psl/.lfs. null — обход ещё не дошёл до этой карточки, откроется
    /// папка либо файл без расширения: тогда кнопка без скобок, пустые скобки хуже.</summary>
    public string? PlcOpenExtension { get; init; }

    /// <summary>То же самое для кнопки «Открыть HMI проект» — расширение файла панели, который реально
    /// откроется (считает HmiOpenResolver при обходе диска). null — обход не дошёл, панели нет, или
    /// откроется папка проекта: тогда кнопка без расширения.</summary>
    public string? HmiOpenExtension { get; init; }

    /// <summary>Подключён настоящий лоадер (IFirmwareLoaderBackend.IsAvailable). Пока в приложении
    /// только заготовка (всегда false) — «Загрузить в ПЛК» становится ОСНОВНОЙ кнопкой лишь когда
    /// лоадер реально подключён И рядом есть .lfs; иначе основная кнопка — «Открыть прошивку ПЛК».</summary>
    public bool LoaderConnected { get; init; }

    /// <summary>Нашёлся .lfs / .psl.</summary>
    public bool HasLfs { get; init; }
    public bool HasPsl { get; init; }

    /// <summary>Бывают ли у этой версии .psl/.lfs вообще (SegneticsProject.IsRelevant). У шкафа на
    /// KINCO их не бывает, и «LFS —» там означало бы потерянный файл вместо «не про эту версию».</summary>
    public bool IsSegnetics { get; init; }

    /// <summary>Обход диска (LFS/PSL/HMI/карта) ещё идёт — карточка уже нарисована, но про файлы
    /// рядом с версией пока ничего не известно. Нужен, чтобы «нет LFS» не показывалось секунду как
    /// факт, пока сетевую папку ещё читают (см. SearchView.ScanDiskFlagsAsync).</summary>
    public bool DiskScanPending { get; init; }

    public bool CanEditTags { get; init; }

    /// <summary>Включена ли автосинхронизация локальной копии (Настройки → Общие). От неё зависит
    /// только начальный текст статуса — пункт ручной синхронизации в меню есть всегда.</summary>
    public bool AutoSync { get; init; }
}

/// <summary>One search-result card. Кнопок стало слишком много для одного ряда, поэтому основными
/// остались только те, ради которых карточку открывают (открыть ПЛК/HMI, параметры, инструкции,
/// загрузка в контроллер), а остальное убрано в меню «Ещё» — см. Configure.</summary>
public partial class FirmwareCard : UserControl
{
    public HierarchyResult Result { get; private set; } = null!;

    public event EventHandler? OpenFolderRequested;
    /// <summary>Открыть прошивку ПЛК / HMI-проект. Какой именно файл открывается — решает SearchView
    /// (подсказка исполняемого файла у записи, отдельная папка HMI-проекта, старый детект по
    /// расширениям); карточка про эти варианты не знает и рисует по одной кнопке на каждый.</summary>
    public event EventHandler? OpenPlcRequested;
    public event EventHandler? OpenHmiRequested;
    public event EventHandler? OpenLfsRequested;
    public event EventHandler? OpenPslRequested;
    /// <summary>Ручная синхронизация локальной копии с диском — раньше была основной кнопкой
    /// «Синхронизировать»/«Обновить», теперь запасной вариант в меню (обычно копия подтягивается
    /// сама, см. SearchView.AutoSyncMissing).</summary>
    public event EventHandler? DownloadRequested;
    public event EventHandler? LoaderRequested;
    public event EventHandler? MapRequested;
    public event EventHandler? ModbusMapRequested;
    public event EventHandler? ParamsRequested;
    public event EventHandler? InstructionsRequested;
    public event EventHandler? HistoryRequested;
    public event EventHandler? CopyNameRequested;
    public event EventHandler? TagsEditRequested;

    public FirmwareCard()
    {
        InitializeComponent();
        MorePopup.Opened += MorePopup_Opened;
        MorePopup.Closed += MorePopup_Closed;
    }

    public void Configure(HierarchyResult result, FirmwareCardFlags flags)
    {
        Result = result;

        NameLabel.Text = result.Name;
        VersionLabel.Text = result.VersionRaw;
        VersionLabel.ToolTip =
            "Формат версии: eq_prefix.sub_prefix.hw.sw.ГГГГММДД_ЧЧММ\n" +
            ".PSL — исходный проект, .LFS — скомпилированный файл";

        var metaParts = new List<string>();
        if (!string.IsNullOrEmpty(result.Controller)) metaParts.Add($"Контроллер: {result.Controller}");
        if (!string.IsNullOrEmpty(result.EquipmentType)) metaParts.Add(result.EquipmentType);
        if (!string.IsNullOrEmpty(result.WorkType)) metaParts.Add(result.WorkType);
        if (result.UploadDate is not null) metaParts.Add(result.UploadDate.Value.ToString("dd.MM.yyyy"));
        // «По такому же запросу эту версию уже ставили N раз» — то, из-за чего она стоит выше
        // остальных (см. Database.FwUsage.cs). Без этой строки подъём выглядел бы необъяснимым.
        if (result.UsageCount > 0)
            metaParts.Add(result.UsageCount == 1
                ? "по этому запросу выбирали 1 раз"
                : $"по этому запросу выбирали {result.UsageCount} раз");
        MetaLabel.Text = string.Join("  ·  ", metaParts);

        // Read-only display here — editing tags (and description/launch types together) happens
        // through the single "Теги" button below, not inline, to avoid two competing tag editors
        // on the same card.
        var tags = TagString.Parse(result.Tags);
        TagsView.Configure(tags, null, readOnly: true);
        TagsView.Visibility = tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        SoftwareNameLabel.Text = $"{result.Name} {result.VersionRaw}".Trim();

        ShowFilesLine(result, flags);

        ActionsPanel.Children.Clear();
        MorePanel.Children.Clear();

        // ── Основной ряд ───────────────────────────────────────────────────
        // Первой кнопкой — либо «Загрузить в ПЛК», либо «Открыть прошивку ПЛК», но не обе сразу:
        //   • «Загрузить в ПЛК (.lfs)» показывается ТОЛЬКО когда подключён настоящий лоадер И рядом
        //     есть .lfs (в контроллер заливают именно скомпилированный файл). Тогда «открыть» уходит
        //     в «Ещё».
        //   • во всех остальных случаях — «Открыть прошивку ПЛК (.ext)»: расширение файла, который
        //     реально откроется, пишется прямо на кнопке, и не только для .psl/.lfs, а для любого
        //     проекта — считает его тот же PlcOpenResolver, который потом и открывает.
        // Дальше — HMI-проект и параметры отдельными кнопками, если есть. Всё прочее (второй файл
        // пары, открыть проект при заливке, папка, документация, история) — в «Ещё».
        var openExt = flags.PlcOpenExtension;
        var showLoad = flags.LoaderConnected && flags.HasLfs;

        if (showLoad)
        {
            var loadBtn = MakeActionButton("Загрузить в ПЛК (.lfs)",
                (_, _) => LoaderRequested?.Invoke(this, EventArgs.Empty));
            loadBtn.ToolTip = "Загрузка в контроллер через подключённый лоадер: найденный .lfs " +
                              "подставится сам, дальше — параметры, прогресс и лог";
            ActionsPanel.Children.Add(loadBtn);
        }
        else
        {
            var plcBtn = MakeActionButton(
                openExt is null ? "Открыть прошивку ПЛК" : $"Открыть прошивку ПЛК ({openExt})",
                (_, _) => OpenPlcRequested?.Invoke(this, EventArgs.Empty));
            plcBtn.ToolTip = PrimaryOpenTooltip(result, flags, openExt);
            ActionsPanel.Children.Add(plcBtn);
        }

        if (flags.HasHmi)
        {
            // Панель может быть унаследована от прошлой версии программы ПЛК (её обновляли, HMI —
            // нет, см. FirmwareUploadService). Тогда честнее сразу сказать, от какой именно версии
            // проект, а не делать вид, что он собран вместе с этой.
            var hmiFrom = HmiSourceVersion(result);
            var hmiBtn = MakeActionButton($"Открыть HMI проект{HmiButtonSuffix(flags.HmiOpenExtension, hmiFrom)}",
                (_, _) => OpenHmiRequested?.Invoke(this, EventArgs.Empty));
            var hmiTips = new List<string>();
            if (hmiFrom is not null) hmiTips.Add($"HMI-проект от версии {hmiFrom} — в этой версии панель не обновляли");
            if (!string.IsNullOrEmpty(result.HmiExecutableHint)) hmiTips.Add($"Исполняемый файл: {result.HmiExecutableHint}");
            if (hmiTips.Count > 0) hmiBtn.ToolTip = string.Join("\n", hmiTips);
            ActionsPanel.Children.Add(hmiBtn);
        }

        if (flags.HasParams)
            ActionsPanel.Children.Add(MakeActionButton("Параметры", (_, _) => ParamsRequested?.Invoke(this, EventArgs.Empty)));

        // ── Меню «Ещё»: всё остальное, по разделам ────────────────────────
        AddMenuHeader("Файлы версии");
        AddMenuItem("Открыть папку с файлами", () => OpenFolderRequested?.Invoke(this, EventArgs.Empty));
        // Когда основная кнопка — «Загрузить», «открыть прошивку» остаётся доступной здесь.
        if (showLoad)
            AddMenuItem(openExt is null ? "Открыть прошивку ПЛК" : $"Открыть прошивку ПЛК ({openExt})",
                () => OpenPlcRequested?.Invoke(this, EventArgs.Empty),
                "Открыть проект/файл для просмотра, без заливки в контроллер");
        // Второй файл пары Segnetics — тот, что не открывается основной кнопкой. Пункт добавляется,
        // только если файл реально есть (у KINCO и т.п. .lfs/.psl не бывает вовсе).
        if (flags.IsSegnetics)
        {
            if (flags.HasPsl && openExt != ".psl")
                AddMenuItem("Открыть проект (PSL)", () => OpenPslRequested?.Invoke(this, EventArgs.Empty),
                    "Исходный проект SMLogix — открывают, когда нужно править");
            if (flags.HasLfs && openExt != ".lfs")
                AddMenuItem("Открыть прошивку (LFS)", () => OpenLfsRequested?.Invoke(this, EventArgs.Empty),
                    "Скомпилированный файл, который заливается в контроллер");
        }
        // Запасного «Загрузить в ПЛК» в меню НЕТ намеренно: заливка предлагается ровно тогда, когда
        // её можно выполнить (лоадер подключён И есть .lfs) — тогда она основная кнопка. Когда
        // выполнить нельзя, вместо неё показывается «Открыть прошивку ПЛК», а не пункт, который
        // упрётся в неподключённый лоадер: кнопка, которая заведомо ничего не сделает, хуже, чем её
        // отсутствие. Открыть сам .lfs (без заливки) по-прежнему можно пунктом ниже.
        AddMenuItem("Обновить локальную копию с диска", () => DownloadRequested?.Invoke(this, EventArgs.Empty),
            "Скопировать версию с сетевого диска заново — если автосинхронизация выключена или не удалась");

        // Пункт есть, только когда РЕАЛЬНО есть что открыть (флаг посчитан обходом диска, а не по
        // заполненному пути в БД — раньше кнопка «Карта in/out» висела и при пустой папке, отсюда была
        // жалоба «зачем она, файла же нет»). Клик всегда открывает самый свежий файл документа, а не
        // путь конкретной версии (см. SearchView.OpenMap/OpenInstructions/OpenModbusMap). Раздел целиком
        // пропускается, если показывать нечего — иначе «ДОКУМЕНТАЦИЯ» висела бы пустым заголовком.
        if (flags.HasIoMap || flags.HasModbus || flags.HasInstructions)
        {
            AddMenuHeader("Документация");
            if (flags.HasIoMap)
                AddMenuItem("Карта in/out", () => MapRequested?.Invoke(this, EventArgs.Empty),
                    "Открывается самый свежий файл карты ВВ");
            if (flags.HasModbus)
                AddMenuItem("Карта modbus", () => ModbusMapRequested?.Invoke(this, EventArgs.Empty),
                    "Открывается самый свежий файл карты Modbus");
            if (flags.HasInstructions)
                AddMenuItem("Инструкции", () => InstructionsRequested?.Invoke(this, EventArgs.Empty),
                    "Открывается самый свежий файл инструкции");
        }

        AddMenuHeader("Версия");
        AddMenuItem("История версий", () => HistoryRequested?.Invoke(this, EventArgs.Empty));
        if (flags.CanEditTags)
            AddMenuItem("Теги и описание", () => TagsEditRequested?.Invoke(this, EventArgs.Empty));

        var moreBtn = MakeActionButton("Ещё ▾", (_, _) => ToggleMore());
        moreBtn.ToolTip = "Файлы версии (папка, LFS, PSL), документация, история, теги";
        ActionsPanel.Children.Add(moreBtn);
        MorePopup.PlacementTarget = moreBtn;

        // Только при первой отрисовке: карточка перерисовывается второй раз, когда досчитается
        // обход диска (SearchView.ScanDiskFlagsAsync), и затирать этим уже показанный ход
        // автосинхронизации («Синхронизация с диском…», «✓ Локальная копия обновлена») нельзя.
        if (!_syncStatusShown)
        {
            _syncStatusShown = true;
            ShowInitialSyncStatus(flags);
        }
    }

    private bool _syncStatusShown;

    /// <summary>Хвост подписи кнопки панели: расширение того, что откроется, и от какой версии взят
    /// проект, если он унаследован. Оба факта важны, поэтому в одних скобках через запятую —
    /// «Открыть HMI проект (.dpj, от 2.1.041)», а не двумя парами скобок подряд. Пустых скобок не
    /// бывает: нечего сказать — суффикса нет вовсе.</summary>
    private static string HmiButtonSuffix(string? ext, string? fromVersion) => (ext, fromVersion) switch
    {
        (null, null) => "",
        (not null, null) => $" ({ext})",
        (null, not null) => $" (от {fromVersion})",
        _ => $" ({ext}, от {fromVersion})",
    };

    private static string? PrimaryOpenTooltip(HierarchyResult result, FirmwareCardFlags flags, string? openExt)
    {
        if (openExt == ".psl")
            return "Исходный проект SMLogix (.psl)" + (flags.HasLfs ? ". Скомпилированный .lfs — в «Ещё»" : "");
        if (openExt == ".lfs")
            return "Скомпилированный файл .lfs — открывается лоадером";
        return !string.IsNullOrEmpty(result.ExecutableHint) ? $"Исполняемый файл: {result.ExecutableHint}" : null;
    }

    /// <summary>Номер версии, к которой был приложен HMI-проект, если это НЕ текущая версия. Папка
    /// проекта называется «{номер версии}_hmi» (см. FirmwareAttachmentsService.CopyHmiProject) —
    /// отдельного поля «от какой версии панель» в базе нет и не нужно, имя папки это и есть.
    /// null — панель от этой же версии либо путь непонятного вида.</summary>
    private static string? HmiSourceVersion(HierarchyResult result)
    {
        if (string.IsNullOrEmpty(result.HmiPath)) return null;
        var folder = System.IO.Path.GetFileName(result.HmiPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(folder)) return null;

        const string suffix = "_hmi";
        if (!folder.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        var version = folder[..^suffix.Length];
        return string.Equals(version, result.VersionRaw, StringComparison.OrdinalIgnoreCase) || version.Length == 0
            ? null
            : version;
    }

    /// <summary>Строка «что лежит рядом с версией». У Segnetics LFS/PSL показываются с явным «нет» —
    /// именно про них был вопрос «есть он или нет»; остальное перечисляется, только когда есть.</summary>
    private void ShowFilesLine(HierarchyResult result, FirmwareCardFlags flags)
    {
        FilesLabel.ToolTip = flags.IsSegnetics
            ? ".LFS — скомпилированный файл, его заливают в контроллер лоадером.\n" +
              ".PSL — исходный проект SMLogix, его открывают для правки."
            : null;

        if (flags.DiskScanPending)
        {
            FilesLabel.Visibility = Visibility.Visible;
            FilesLabel.Text = "Файлы: проверяем папку версии…";
            return;
        }

        var parts = new List<string>();
        // «LFS —»/«PSL —» — только там, где эти файлы бывают: у KINCO-шкафа их отсутствие не новость,
        // а прочерк выглядел бы как потерянный файл (см. SegneticsProject).
        if (flags.IsSegnetics)
        {
            parts.Add(flags.HasLfs ? "LFS ✓" : "LFS —");
            parts.Add(flags.HasPsl ? "PSL ✓" : "PSL —");
        }
        if (flags.HasHmi) parts.Add(HmiSourceVersion(result) is { } from ? $"HMI ✓ (от {from})" : "HMI ✓");
        if (flags.HasParams) parts.Add("параметры ✓");
        if (flags.HasIoMap) parts.Add("карта ВВ ✓");
        if (flags.HasModbus) parts.Add("карта modbus ✓");
        if (flags.HasInstructions) parts.Add("инструкция ✓");
        // Ни одного файла-спутника — строка не нужна вовсе, пустое «Файлы:» только занимает место.
        FilesLabel.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FilesLabel.Text = "Файлы: " + string.Join(" · ", parts);
    }

    // ── Статус локальной копии ────────────────────────────────────────────

    private void ShowInitialSyncStatus(FirmwareCardFlags flags)
    {
        if (flags.HasLocal)
        {
            SetSyncStatus(null);
            return;
        }
        if (flags.AutoSync)
        {
            SetSyncStatus(flags.HasAnyLocal
                ? "Локальная копия устарела — обновляем…"
                : "Локальной копии нет — синхронизируем…");
            return;
        }
        SetSyncStatus("Локальной копии нет. Автосинхронизация выключена — «Ещё» → «Обновить локальную копию с диска».",
            "WarningBrush");
    }

    /// <summary>text = null — скрыть строку статуса. brushKey — ключ темы (никаких hex-цветов).</summary>
    public void SetSyncStatus(string? text, string brushKey = "TextMutedBrush")
    {
        if (string.IsNullOrEmpty(text))
        {
            SyncStatusLabel.Visibility = Visibility.Collapsed;
            return;
        }
        SyncStatusLabel.Text = text;
        SyncStatusLabel.SetResourceReference(ForegroundProperty, brushKey);
        SyncStatusLabel.Visibility = Visibility.Visible;
    }

    // ── Кнопки/меню ───────────────────────────────────────────────────────

    private Button MakeActionButton(string text, RoutedEventHandler onClick)
    {
        var btn = new Button
        {
            Content = text,
            Style = (Style)FindResource("SecondaryButton"),
            Margin = new Thickness(0, 0, 8, 8),
        };
        btn.Click += onClick;
        return btn;
    }

    private void AddMenuHeader(string text)
    {
        var header = new TextBlock
        {
            Text = text.ToUpperInvariant(),
            Style = (Style)FindResource("MutedText"),
            Margin = new Thickness(2, MorePanel.Children.Count == 0 ? 0 : 8, 0, 4),
            FontSize = 10,
        };
        MorePanel.Children.Add(header);
    }

    /// <summary>Пункт добавляется, только пока для него действительно есть что показать (см. вызовы в
    /// Configure) — недоступных серых пунктов с объяснением «почему нельзя» больше нет, поэтому и
    /// enabled-параметр здесь не нужен.</summary>
    private void AddMenuItem(string text, Action action, string? tooltip = null)
    {
        var btn = new Button
        {
            Content = text,
            Style = (Style)FindResource("SecondaryButton"),
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            ToolTip = tooltip,
        };
        btn.Click += (_, _) =>
        {
            MorePopup.IsOpen = false;
            action();
        };
        MorePanel.Children.Add(btn);
    }

    /// <summary>Открытый Popup со StaysOpen="False" держит захват мыши и закрывается сам по нажатию
    /// мимо — а нажатие по самой кнопке «Ещё ▾» для него тоже «мимо». Порядок, снятый живьём:
    /// popup съедает нажатие и закрывается МОЛЧА (событие Closed при этом не приходит вообще, и
    /// PreviewMouseLeftButtonDown до кнопки тоже не доезжает), а через ~4 мс кнопке приходит Click —
    /// который видит IsOpen=false и открывает меню заново. Отсюда жалоба «нажимаю — ничего, тыкаю
    /// несколько раз»: закрыть меню той же кнопкой, которой открыл, было невозможно.
    ///
    /// Поэтому опорная точка — PreviewMouseDownOutsideCapturedElement: единственное событие, которое
    /// про это нажатие вообще приходит, и приходит гарантированно ДО Click (одно и то же нажатие).
    /// Click сразу после него — тот самый закрывающий клик, открывать по нему нечего.</summary>
    private DateTime _moreDismissedAt = DateTime.MinValue;

    private void MorePopup_Opened(object? sender, EventArgs e)
    {
        // Снять перед добавлением: авто-закрытие Closed не поднимает, так что штатной точки для
        // отписки нет, и без этого обработчик копился бы с каждым открытием.
        System.Windows.Input.Mouse.RemovePreviewMouseDownOutsideCapturedElementHandler(MorePopup, MoreDismissedByClickOutside);
        System.Windows.Input.Mouse.AddPreviewMouseDownOutsideCapturedElementHandler(MorePopup, MoreDismissedByClickOutside);
    }

    private void MorePopup_Closed(object? sender, EventArgs e) =>
        System.Windows.Input.Mouse.RemovePreviewMouseDownOutsideCapturedElementHandler(MorePopup, MoreDismissedByClickOutside);

    private void MoreDismissedByClickOutside(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _moreDismissedAt = DateTime.Now;
        System.Windows.Input.Mouse.RemovePreviewMouseDownOutsideCapturedElementHandler(MorePopup, MoreDismissedByClickOutside);
    }

    private void ToggleMore()
    {
        // Порог заведомо больше зазора между закрытием и Click (единицы мс) и заведомо меньше
        // осмысленного «закрыл и сразу передумал».
        var dismissedByThisClick = !MorePopup.IsOpen
            && (DateTime.Now - _moreDismissedAt).TotalMilliseconds < 250;
        if (dismissedByThisClick) return;
        MorePopup.IsOpen = !MorePopup.IsOpen;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        FlashCopyFeedback();
        CopyNameRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Header_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        FlashCopyFeedback();
        CopyNameRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Visible confirmation right where the operator clicked — a status-bar message alone
    /// (the only feedback before this) is easy to miss, especially clicking from a long results list.</summary>
    private void FlashCopyFeedback()
    {
        var original = CopyButton.Content;
        CopyButton.Content = "✓ Скопировано";
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CopyButton.Content = original;
        };
        timer.Start();
    }
}
