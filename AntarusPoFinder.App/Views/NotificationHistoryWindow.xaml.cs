using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Уведомления: история + (перенесено сюда из Настройки → Прочее — логичнее настраивать
/// видимость категорий прямо там же, где видна сама история, чем в отдельном разделе Настроек)
/// какие категории вообще показывать. Обычное окно, не страница с RefreshIfActive — категории
/// заполняются один раз при открытии, как и остальной контент окна.</summary>
public partial class NotificationHistoryWindow : Window
{
    private readonly ObservableCollection<NotificationEntry> _entries;
    private readonly ConfigService _cfg;

    public NotificationHistoryWindow(ObservableCollection<NotificationEntry> entries, ConfigService cfg)
    {
        InitializeComponent();
        _entries = entries;
        _cfg = cfg;
        ListBoxHistory.ItemsSource = entries;
        LoadNotificationCategories();
    }

    /// <summary>Built in code, not XAML-bound — one CheckBox per NotificationCategoryInfo.All entry,
    /// pre-checked from ConfigService.IsNotificationCategoryEnabled. Deliberately silent on toggle
    /// (see NotificationCategoryCheck_Changed): the point of muting a category is that it stops
    /// making noise, so the mute action itself shouldn't pop a status message.</summary>
    private void LoadNotificationCategories()
    {
        NotificationCategoriesPanel.Children.Clear();
        foreach (var (category, label) in NotificationCategoryInfo.All)
        {
            var cb = new CheckBox
            {
                Content = label,
                Tag = category,
                IsChecked = _cfg.IsNotificationCategoryEnabled(category),
                Margin = new Thickness(0, 0, 0, 6),
            };
            cb.Checked += NotificationCategoryCheck_Changed;
            cb.Unchecked += NotificationCategoryCheck_Changed;
            NotificationCategoriesPanel.Children.Add(cb);
        }
    }

    private void NotificationCategoryCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: NotificationCategory category } cb) return;
        _cfg.SetNotificationCategoryEnabled(category, cb.IsChecked == true);
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
