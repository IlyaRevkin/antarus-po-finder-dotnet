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
        try { Clipboard.SetText(Text); }
        catch { return; }

        var original = Label.Foreground;
        Label.Foreground = (Brush)Application.Current.Resources["SuccessBrush"];
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Label.Foreground = original;
        };
        timer.Start();
    }
}
