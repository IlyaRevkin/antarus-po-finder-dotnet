using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AntarusPoFinder.App.Views;

/// <summary>Read-only text (a firmware version string, a filename, etc.) that copies itself to the
/// clipboard on click with a brief color flash for feedback — used as a DataGrid cell template
/// wherever a version/name is shown in a grid, per the naladchik/programmer request to make every
/// displayed firmware name click-to-copy, not just the dedicated button on the search card.</summary>
public partial class CopyableVersionText : UserControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(CopyableVersionText), new PropertyMetadata(""));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public CopyableVersionText()
    {
        InitializeComponent();
    }

    private void Label_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true; // don't let the click bubble into the DataGrid row (selection/double-click)
        if (string.IsNullOrEmpty(Text)) return;
        // Clipboard.SetText can throw if another process is holding the clipboard open right at this
        // instant (COMException, notoriously flaky on Windows) — skipping the success-color flash
        // below is itself the feedback that nothing was copied; a message box for a one-click copy
        // gesture the operator can just retry would be disproportionate.
        try { Clipboard.SetText(Text); }
        catch { return; }

        // Every use of this control is inside a DataGrid cell (Прошивки/Модерация/Резервация номеров/
        // NewVersionsView/HistoryDialog) — clicking to copy on an already-SELECTED row was flashing the
        // plain SuccessBrush (tuned to read on the normal card background) on top of the row's own
        // accent-blue selection highlight, which in both themes turns out low-contrast (light theme:
        // medium green #40A02B on medium blue #1E66F5; dark theme: light green #A6E3A1 on light blue
        // #89B4FA — same problem, just with the light/dark roles swapped). SuccessOnAccentBrush is each
        // theme's color swapped from the OTHER theme's SuccessBrush, which happens to read correctly
        // against that theme's own accent blue — see Light.xaml/Dark.xaml for the actual values.
        var onSelectedBackground = IsOnSelectedBackground(Label);
        Label.Foreground = (Brush)Application.Current.Resources[onSelectedBackground ? "SuccessOnAccentBrush" : "SuccessBrush"];
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // ClearValue, а не "Label.Foreground = original" (как было раньше): захватывать текущий
            // резолвленный Brush и переприсваивать его как ЛОКАЛЬНОЕ значение окончательно рвёт
            // Binding, который держит стиль Styles.xaml (Foreground ячейки DataGridCell, переключается
            // между обычным и TextOnAccentBrush при выделении строки) — родительский Style.Resources
            // ставит на этот TextBlock именно Binding, а не Setter, и присвоение локального значения
            // вообще любого Brush навсегда отключает его. После первого клика по любой версии в
            // таблице (Прошивки/Модерация/Резервация) её текст переставал реагировать на выделение
            // строки — оставался тёмным на синем акцентном фоне (ровно баг «выделено — чёрный текст
            // на синем»). ClearValue снимает локальное значение и возвращает управление Foreground
            // обратно Binding/наследованию — цвет снова корректно следует за выделением ячейки.
            Label.ClearValue(TextBlock.ForegroundProperty);
        };
        timer.Start();
    }

    private static bool IsOnSelectedBackground(DependencyObject start)
    {
        for (var current = start; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is DataGridCell cell) return cell.IsSelected;
            if (current is ListBoxItem item) return item.IsSelected;
        }
        return false;
    }
}
