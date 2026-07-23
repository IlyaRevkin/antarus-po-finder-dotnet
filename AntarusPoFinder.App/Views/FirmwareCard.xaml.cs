using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>One search-result card. Port of app/ui/widgets/firmware_card.py — button visibility
/// rules are reproduced exactly (see SearchView, which computes the has*/flags per result).</summary>
public partial class FirmwareCard : UserControl
{
    public HierarchyResult Result { get; private set; } = null!;

    public event EventHandler? OpenRequested;
    public event EventHandler? OpenFolderRequested;
    public event EventHandler? OpenPlcRequested;
    public event EventHandler? OpenHmiRequested;
    public event EventHandler? DownloadRequested;
    public event EventHandler? MapRequested;
    public event EventHandler? ModbusMapRequested;
    public event EventHandler? ParamsRequested;
    public event EventHandler? InstructionsRequested;
    /// <summary>Separately-uploaded HMI project (see UploadView "Добавить HMI-проект") — not to be
    /// confused with OpenHmiRequested above, which opens the HMI file inside a KINCO folder that
    /// already mixes PLC+HMI files together (hasHmi in SearchView).</summary>
    public event EventHandler? HmiProjectRequested;
    public event EventHandler? HistoryRequested;
    public event EventHandler? CopyNameRequested;
    public event EventHandler? TagsEditRequested;

    public FirmwareCard()
    {
        InitializeComponent();
    }

    public void Configure(HierarchyResult result, bool hasLocal, bool hasAnyLocal, bool hasParams, bool hasHmi, bool hasMap,
        bool canEditTags = false)
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

        // Порядок кнопок: сначала «Открыть прошивку ПЛК», СРАЗУ за ней «Открыть HMI проект» —
        // две кнопки открытия самого ПО стоят рядом, а не разнесены через полсписка (раньше
        // HMI-кнопка была последней, после Инструкций).
        // hasHmi — старый KINCO-детект по расширениям файлов внутри одной папки; остаётся только
        // как фоллбэк для уже загруженных записей без явных хинтов исполняемых файлов.
        var plcBtn = hasHmi
            ? MakeActionButton("Открыть прошивку ПЛК", (_, _) => OpenPlcRequested?.Invoke(this, EventArgs.Empty))
            : MakeActionButton("Открыть прошивку ПЛК", (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        if (!string.IsNullOrEmpty(result.ExecutableHint))
            plcBtn.ToolTip = $"Исполняемый файл: {result.ExecutableHint}";
        ActionsPanel.Children.Add(plcBtn);

        if (hasHmi)
            ActionsPanel.Children.Add(MakeActionButton("Открыть HMI проект", (_, _) => OpenHmiRequested?.Invoke(this, EventArgs.Empty)));
        else if (!string.IsNullOrEmpty(result.HmiPath))
            ActionsPanel.Children.Add(MakeActionButton("Открыть HMI проект", (_, _) => HmiProjectRequested?.Invoke(this, EventArgs.Empty)));

        ActionsPanel.Children.Add(MakeActionButton("Открыть папку с файлом", (_, _) => OpenFolderRequested?.Invoke(this, EventArgs.Empty)));

        if (!hasLocal)
        {
            var label = hasAnyLocal ? "Обновить" : "Синхронизировать";
            ActionsPanel.Children.Add(MakeActionButton(label, (_, _) => DownloadRequested?.Invoke(this, EventArgs.Empty)));
        }

        if (!string.IsNullOrEmpty(result.IoMapPath) || hasMap)
            ActionsPanel.Children.Add(MakeActionButton("Карта in/out", (_, _) => MapRequested?.Invoke(this, EventArgs.Empty)));

        if (!string.IsNullOrEmpty(result.ModbusMapPath))
            ActionsPanel.Children.Add(MakeActionButton("Карта modbus", (_, _) => ModbusMapRequested?.Invoke(this, EventArgs.Empty)));

        if (hasParams)
            ActionsPanel.Children.Add(MakeActionButton("Параметры", (_, _) => ParamsRequested?.Invoke(this, EventArgs.Empty)));

        if (!string.IsNullOrEmpty(result.InstructionsPath))
            ActionsPanel.Children.Add(MakeActionButton("Инструкции", (_, _) => InstructionsRequested?.Invoke(this, EventArgs.Empty)));

        ActionsPanel.Children.Add(MakeActionButton("История", (_, _) => HistoryRequested?.Invoke(this, EventArgs.Empty)));

        if (canEditTags)
            ActionsPanel.Children.Add(MakeActionButton("Теги", (_, _) => TagsEditRequested?.Invoke(this, EventArgs.Empty)));
    }

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
