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
    public event EventHandler? ParamsRequested;
    public event EventHandler? InstructionsRequested;
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
        if (hasHmi)
        {
            ActionsPanel.Children.Add(MakeActionButton("Открыть ПЛК", (_, _) => OpenPlcRequested?.Invoke(this, EventArgs.Empty)));
            ActionsPanel.Children.Add(MakeActionButton("Открыть HMI", (_, _) => OpenHmiRequested?.Invoke(this, EventArgs.Empty)));
        }
        else
        {
            // Single-file firmware (Segnetics PSL/LFS today — no separate PLC/HMI project to split
            // like KINCO above). "Открыть прошивку" reads clearer than a bare "Открыть". If a future
            // vendor (e.g. Owen) needs its own split — module firmware vs. PLC firmware — add another
            // branch here the same way hasHmi does for KINCO, rather than overloading this one.
            ActionsPanel.Children.Add(MakeActionButton("Открыть прошивку", (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty)));
        }

        ActionsPanel.Children.Add(MakeActionButton("Открыть папку с файлом", (_, _) => OpenFolderRequested?.Invoke(this, EventArgs.Empty)));

        if (!hasLocal)
        {
            var label = hasAnyLocal ? "Обновить" : "Синхронизировать";
            ActionsPanel.Children.Add(MakeActionButton(label, (_, _) => DownloadRequested?.Invoke(this, EventArgs.Empty)));
        }

        if (!string.IsNullOrEmpty(result.IoMapPath) || hasMap)
            ActionsPanel.Children.Add(MakeActionButton("Карта in/out", (_, _) => MapRequested?.Invoke(this, EventArgs.Empty)));

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
