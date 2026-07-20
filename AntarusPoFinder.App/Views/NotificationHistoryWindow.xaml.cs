using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.App.ViewModels;

namespace AntarusPoFinder.App.Views;

public partial class NotificationHistoryWindow : Window
{
    private readonly ObservableCollection<NotificationEntry> _entries;

    public NotificationHistoryWindow(ObservableCollection<NotificationEntry> entries)
    {
        InitializeComponent();
        _entries = entries;
        ListBoxHistory.ItemsSource = entries;
    }

    private void Reopen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is NotificationEntry entry)
        {
            entry.Reopen?.Invoke();
            Close();
        }
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_entries.Count == 0) return;
        var reply = AppMessageBox.Show("Очистить всю историю уведомлений?", "Уведомления",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;
        _entries.Clear();
    }
}
