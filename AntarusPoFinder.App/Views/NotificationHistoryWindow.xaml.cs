using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.App.ViewModels;

namespace AntarusPoFinder.App.Views;

public partial class NotificationHistoryWindow : Window
{
    public NotificationHistoryWindow(IEnumerable<NotificationEntry> entries)
    {
        InitializeComponent();
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
}
