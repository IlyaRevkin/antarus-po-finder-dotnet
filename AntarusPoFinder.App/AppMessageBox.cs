using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AntarusPoFinder.App;

/// <summary>Drop-in themed replacement for <see cref="MessageBox"/>.Show — same signature, same
/// call sites, but renders as a normal (dark/light-themed) WPF window instead of the native Win32
/// message box, which always paints white/OS-light chrome regardless of the app's theme. Native
/// MessageBox popups fire on nearly every action in this app (validation, confirmations, results),
/// so leaving them un-themed made almost every window in the app flash white against the dark UI.</summary>
public static class AppMessageBox
{
    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon)
        => Show(messageBoxText, caption, button, icon, MessageBoxResult.None);

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult)
    {
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current.MainWindow;

        var (glyph, brushKey) = icon switch
        {
            MessageBoxImage.Warning => ("⚠", "WarningBrush"),
            MessageBoxImage.Error => ("✕", "ErrorBrush"),
            MessageBoxImage.Question => ("?", "AccentBrush"),
            _ => ("ℹ", "AccentBrush"),
        };

        var result = MessageBoxResult.None;
        var win = new Window
        {
            Title = caption,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 320,
            MaxWidth = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        if (owner is not null && owner != win)
        {
            win.Owner = owner;
        }
        win.SetResourceReference(Window.BackgroundProperty, "BgBrush");
        win.SetResourceReference(Window.ForegroundProperty, "TextBrush");

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var messageRow = new Grid();
        messageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        messageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconText = new TextBlock
        {
            Text = glyph,
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        iconText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        Grid.SetColumn(iconText, 0);

        var messageText = new TextBlock
        {
            Text = messageBoxText,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 420,
        };
        messageText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        Grid.SetColumn(messageText, 1);

        messageRow.Children.Add(iconText);
        messageRow.Children.Add(messageText);
        Grid.SetRow(messageRow, 0);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        Grid.SetRow(buttonPanel, 1);

        void AddButton(string content, MessageBoxResult r, bool isDefault, bool isCancel, bool primary)
        {
            var btn = new Button
            {
                Content = content,
                Width = 90,
                Height = 34,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = isDefault,
                IsCancel = isCancel,
            };
            if (!primary)
                btn.SetResourceReference(Button.StyleProperty, "SecondaryButton");
            btn.Click += (_, _) => { result = r; win.Close(); };
            buttonPanel.Children.Add(btn);
        }

        switch (button)
        {
            case MessageBoxButton.YesNo:
                AddButton("Нет", MessageBoxResult.No, defaultResult == MessageBoxResult.No, true, defaultResult == MessageBoxResult.No);
                AddButton("Да", MessageBoxResult.Yes, defaultResult == MessageBoxResult.Yes, false, defaultResult != MessageBoxResult.No);
                break;
            case MessageBoxButton.OKCancel:
                AddButton("Отмена", MessageBoxResult.Cancel, false, true, false);
                AddButton("ОК", MessageBoxResult.OK, true, false, true);
                break;
            default:
                AddButton("ОК", MessageBoxResult.OK, true, true, true);
                break;
        }

        root.Children.Add(messageRow);
        root.Children.Add(buttonPanel);
        win.Content = root;

        // Closing via the title-bar X (rather than a button) should behave like declining a
        // destructive confirmation, never like accepting one.
        win.Closing += (_, _) =>
        {
            if (result == MessageBoxResult.None)
                result = button == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;
        };
        win.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) win.Close();
        };

        win.ShowDialog();
        return result;
    }
}
