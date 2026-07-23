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

    /// <summary>Нашёлся .lfs / .psl — кнопки открытия этих файлов показываются только тогда, когда
    /// открывать реально есть что.</summary>
    public bool HasLfs { get; init; }
    public bool HasPsl { get; init; }

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

        ActionsPanel.Children.Clear();
        MorePanel.Children.Clear();

        // ── Основной ряд: только то, ради чего карточку открывают ──────────
        // Порядок: сначала «Открыть прошивку ПЛК», СРАЗУ за ней «Открыть HMI проект» — две кнопки
        // открытия самого ПО стоят рядом, а не разнесены через полсписка.
        var plcBtn = MakeActionButton("Открыть прошивку ПЛК", (_, _) => OpenPlcRequested?.Invoke(this, EventArgs.Empty));
        if (!string.IsNullOrEmpty(result.ExecutableHint))
            plcBtn.ToolTip = $"Исполняемый файл: {result.ExecutableHint}";
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

        if (!string.IsNullOrEmpty(result.InstructionsPath))
            ActionsPanel.Children.Add(MakeActionButton("Инструкции", (_, _) => InstructionsRequested?.Invoke(this, EventArgs.Empty)));

        var loaderBtn = MakeActionButton("Загрузить в ПЛК", (_, _) => LoaderRequested?.Invoke(this, EventArgs.Empty));
        loaderBtn.ToolTip = "Загрузка в контроллер через лоадер: параметры, прогресс и лог";
        ActionsPanel.Children.Add(loaderBtn);

        // ── Меню «Ещё»: всё остальное ─────────────────────────────────────
        AddMenuItem("Открыть папку с файлом", () => OpenFolderRequested?.Invoke(this, EventArgs.Empty));
        if (flags.HasLfs)
            AddMenuItem("Открыть файл LFS", () => OpenLfsRequested?.Invoke(this, EventArgs.Empty),
                "Скомпилированный файл, который заливается в контроллер");
        if (flags.HasPsl)
            AddMenuItem("Открыть файл PSL", () => OpenPslRequested?.Invoke(this, EventArgs.Empty),
                "Исходный проект SMLogix");
        if (!string.IsNullOrEmpty(result.IoMapPath) || flags.HasMap)
            AddMenuItem("Карта in/out", () => MapRequested?.Invoke(this, EventArgs.Empty));
        if (!string.IsNullOrEmpty(result.ModbusMapPath))
            AddMenuItem("Карта modbus", () => ModbusMapRequested?.Invoke(this, EventArgs.Empty));
        AddMenuItem("История", () => HistoryRequested?.Invoke(this, EventArgs.Empty));
        if (flags.CanEditTags)
            AddMenuItem("Теги", () => TagsEditRequested?.Invoke(this, EventArgs.Empty));
        AddMenuItem("Обновить локальную копию с диска", () => DownloadRequested?.Invoke(this, EventArgs.Empty),
            "Скопировать версию с сетевого диска заново — если автосинхронизация выключена или не удалась");

        var moreBtn = MakeActionButton("Ещё ▾", (_, _) => ToggleMore());
        moreBtn.ToolTip = "Папка с файлами, LFS/PSL, карты, история, теги, синхронизация";
        ActionsPanel.Children.Add(moreBtn);
        MorePopup.PlacementTarget = moreBtn;

        ShowInitialSyncStatus(flags);
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

    private void ToggleMore() => MorePopup.IsOpen = !MorePopup.IsOpen;

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
