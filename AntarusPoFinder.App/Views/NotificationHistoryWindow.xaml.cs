using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    /// <summary>Built in code, not XAML-bound — one row per NotificationCategoryInfo.All entry, with
    /// two independent checkboxes: "Показывать" (ConfigService.IsNotificationCategoryEnabled — fully
    /// mutes the category everywhere) and "Считать непрочитанным" (IsNotificationCategoryCountedUnread
    /// — category still shows/logs normally, just doesn't bump the badge). Deliberately silent on
    /// toggle (see the *_Changed handlers): the point of muting/excluding a category is that it stops
    /// making noise, so the action itself shouldn't pop a status message.</summary>
    private void LoadNotificationCategories()
    {
        NotificationCategoriesPanel.Children.Clear();
        foreach (var (category, label) in NotificationCategoryInfo.All)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });

            var nameLabel = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Tag = category };
            Grid.SetColumn(nameLabel, 0);
            row.Children.Add(nameLabel);

            var enabledCheck = new CheckBox
            {
                Tag = category,
                IsChecked = _cfg.IsNotificationCategoryEnabled(category),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            enabledCheck.Checked += NotificationCategoryEnabledCheck_Changed;
            enabledCheck.Unchecked += NotificationCategoryEnabledCheck_Changed;
            Grid.SetColumn(enabledCheck, 1);
            row.Children.Add(enabledCheck);

            var unreadCheck = new CheckBox
            {
                Tag = category,
                IsChecked = _cfg.IsNotificationCategoryCountedUnread(category),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            unreadCheck.Checked += NotificationCategoryUnreadCheck_Changed;
            unreadCheck.Unchecked += NotificationCategoryUnreadCheck_Changed;
            Grid.SetColumn(unreadCheck, 2);
            row.Children.Add(unreadCheck);

            NotificationCategoriesPanel.Children.Add(row);
        }
    }

    private void NotificationCategoryEnabledCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: NotificationCategory category } cb) return;
        _cfg.SetNotificationCategoryEnabled(category, cb.IsChecked == true);
    }

    private void NotificationCategoryUnreadCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: NotificationCategory category } cb) return;
        _cfg.SetNotificationCategoryCountedUnread(category, cb.IsChecked == true);
    }

    private void CategorySettingsToggle_Click(object sender, RoutedEventArgs e) =>
        CategorySettingsPanel.Visibility = CategorySettingsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Clicking a notification row's category badge is the "directly from a notification"
    /// path to that category's settings — opens the (possibly still collapsed) settings panel above
    /// so the operator doesn't have to hunt for the gear button first.</summary>
    private void CategoryBadge_Click(object sender, MouseButtonEventArgs e) =>
        CategorySettingsPanel.Visibility = Visibility.Visible;

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
