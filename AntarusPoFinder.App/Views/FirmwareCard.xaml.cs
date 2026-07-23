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
    public bool HasMap { get; init; }

    /// <summary>Нашёлся .lfs / .psl. У Segnetics-проектов (см. <see cref="IsSegnetics"/>) пункты меню
    /// для них показываются ВСЕГДА: спрятанная строка «Открыть файл LFS» не отличима от «я не туда
    /// посмотрел», поэтому отсутствие файла — это неактивный пункт с объяснением, а не пустое
    /// место.</summary>
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
        MetaLabel.Text = string.Join("  ·  ", metaParts);

        var desc = result.Description ?? "";
        var truncated = desc.Length > 120;
        DescLabel.Text = truncated ? desc[..120] + "…" : desc;
        DescLabel.Visibility = string.IsNullOrEmpty(desc) ? Visibility.Collapsed : Visibility.Visible;
        DescLabel.Cursor = truncated ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
        DescLabel.ToolTip = truncated ? "Нажмите, чтобы посмотреть полностью" : null;

        // Read-only display here — editing tags (and description/launch types together) happens
        // through the single "Теги" button below, not inline, to avoid two competing tag editors
        // on the same card.
        var tags = result.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        TagsView.Configure(tags, null, readOnly: true);
        TagsView.Visibility = tags.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        SoftwareNameLabel.Text = $"{result.Name} {result.VersionRaw}".Trim();

        ShowFilesLine(result, flags);

        ActionsPanel.Children.Clear();
        MorePanel.Children.Clear();

        // ── Основной ряд: открыть → посмотреть → залить ────────────────────
        // Порядок соответствует тому, как карточкой пользуются: сначала открыть проект, потом
        // сопутствующее (панель, параметры), последней — заливка в контроллер. Всё остальное —
        // в меню «Ещё», сгруппированное по смыслу.
        //
        // Название первой кнопки зависит от того, что реально лежит рядом: у Segnetics-проектов
        // открывается исходник .psl (залитый .lfs текстовым редактором открывать бессмысленно), и
        // кнопка так и называется — раньше она называлась «Открыть прошивку ПЛК» независимо от
        // того, что откроется, и оператор не понимал, PSL это или нет.
        var plcLabel = flags.HasPsl && string.IsNullOrEmpty(result.ExecutableHint)
            ? "Открыть проект PSL"
            : "Открыть прошивку ПЛК";
        var plcBtn = MakeActionButton(plcLabel, (_, _) => OpenPlcRequested?.Invoke(this, EventArgs.Empty));
        plcBtn.ToolTip = !string.IsNullOrEmpty(result.ExecutableHint)
            ? $"Исполняемый файл: {result.ExecutableHint}"
            : flags.HasPsl ? "Исходный проект SMLogix (.psl)" : null;
        ActionsPanel.Children.Add(plcBtn);

        if (flags.HasHmi)
        {
            var hmiBtn = MakeActionButton("Открыть HMI проект", (_, _) => OpenHmiRequested?.Invoke(this, EventArgs.Empty));
            if (!string.IsNullOrEmpty(result.HmiExecutableHint))
                hmiBtn.ToolTip = $"Исполняемый файл: {result.HmiExecutableHint}";
            ActionsPanel.Children.Add(hmiBtn);
        }

        if (flags.HasParams)
            ActionsPanel.Children.Add(MakeActionButton("Параметры", (_, _) => ParamsRequested?.Invoke(this, EventArgs.Empty)));

        var loaderBtn = MakeActionButton(flags.HasLfs ? "Загрузить в ПЛК (LFS)" : "Загрузить в ПЛК",
            (_, _) => LoaderRequested?.Invoke(this, EventArgs.Empty));
        loaderBtn.ToolTip = flags.HasLfs
            ? "Загрузка в контроллер через лоадер: найденный .lfs подставится сам, дальше — параметры, прогресс и лог"
            : "Загрузка в контроллер через лоадер. Рядом с версией нет .lfs — файл придётся выбрать вручную в диалоге";
        ActionsPanel.Children.Add(loaderBtn);

        // ── Меню «Ещё»: всё остальное, по разделам ────────────────────────
        AddMenuHeader("Файлы версии");
        AddMenuItem("Открыть папку с файлами", () => OpenFolderRequested?.Invoke(this, EventArgs.Empty));
        // У Segnetics-проекта пункты LFS/PSL показываются всегда — неактивный пункт с причиной
        // честнее, чем исчезнувшая строка. У остальных (KINCO и т.п.) их не бывает в принципе, и
        // строка про них — просто мусор в меню.
        if (flags.IsSegnetics)
        {
            AddMenuItem("Открыть файл LFS", () => OpenLfsRequested?.Invoke(this, EventArgs.Empty),
                flags.HasLfs
                    ? "Скомпилированный файл, который заливается в контроллер"
                    : "Рядом с версией нет .lfs — версия загружена без скомпилированного файла",
                enabled: flags.HasLfs);
            AddMenuItem("Открыть файл PSL", () => OpenPslRequested?.Invoke(this, EventArgs.Empty),
                flags.HasPsl
                    ? "Исходный проект SMLogix"
                    : "Рядом с версией нет .psl — либо это не проект SMLogix, либо исходник не выкладывали",
                enabled: flags.HasPsl);
        }
        AddMenuItem("Обновить локальную копию с диска", () => DownloadRequested?.Invoke(this, EventArgs.Empty),
            "Скопировать версию с сетевого диска заново — если автосинхронизация выключена или не удалась");

        AddMenuHeader("Документация");
        var hasIoMap = !string.IsNullOrEmpty(result.IoMapPath) || flags.HasMap;
        AddMenuItem("Карта in/out", () => MapRequested?.Invoke(this, EventArgs.Empty),
            hasIoMap ? null : "Карта in/out к этой версии не приложена", enabled: hasIoMap);
        var hasModbus = !string.IsNullOrEmpty(result.ModbusMapPath);
        AddMenuItem("Карта modbus", () => ModbusMapRequested?.Invoke(this, EventArgs.Empty),
            hasModbus ? null : "Карта modbus к этой версии не приложена", enabled: hasModbus);
        var hasInstructions = !string.IsNullOrEmpty(result.InstructionsPath);
        AddMenuItem("Инструкции", () => InstructionsRequested?.Invoke(this, EventArgs.Empty),
            hasInstructions ? null : "Инструкция к этой версии не приложена", enabled: hasInstructions);

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
        if (flags.HasHmi) parts.Add("HMI ✓");
        if (flags.HasParams) parts.Add("параметры ✓");
        if (!string.IsNullOrEmpty(result.IoMapPath) || flags.HasMap) parts.Add("карта ВВ ✓");
        if (!string.IsNullOrEmpty(result.ModbusMapPath)) parts.Add("карта modbus ✓");
        if (!string.IsNullOrEmpty(result.InstructionsPath)) parts.Add("инструкция ✓");
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

    /// <summary>enabled=false — пункт остаётся на месте, но недоступен, а tooltip объясняет, почему
    /// (см. комментарий про LFS/PSL в Configure).</summary>
    private void AddMenuItem(string text, Action action, string? tooltip = null, bool enabled = true)
    {
        var btn = new Button
        {
            Content = text,
            Style = (Style)FindResource("SecondaryButton"),
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            IsEnabled = enabled,
            // Подсказка на выключенной кнопке по умолчанию не показывается — иначе объяснение
            // «почему нельзя» невидимо ровно в том случае, ради которого написано.
            ToolTip = tooltip,
        };
        if (tooltip is not null) ToolTipService.SetShowOnDisabled(btn, true);
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

    private void DescLabel_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var desc = Result.Description ?? "";
        if (desc.Length <= 120) return;
        TextViewDialog.Show(Window.GetWindow(this), $"{Result.Name} {Result.VersionRaw}".Trim(), desc);
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
