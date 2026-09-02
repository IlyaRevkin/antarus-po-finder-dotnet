using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Уведомления: история + (перенесено сюда из Настройки → Прочее — логичнее настраивать
/// видимость категорий прямо там же, где видна сама история, чем в отдельном разделе Настроек)
/// какие категории вообще показывать.
///
/// ⚠️ Главная правка по тикету kiselyov.a («практически всегда уведомления остаются пустыми»).
/// Прежнее окно при ЗАКРЫТИИ метило прочитанным весь список, а список по умолчанию показывал только
/// непрочитанное — значит второе и любое следующее открытие колокольчика давало ПУСТОЕ окно.
/// Воспроизведено живым прогоном scratchpad/live/notifications_run.py: первое открытие — две записи,
/// второе — ноль. Теперь:
/// <list type="bullet">
/// <item>список показывает ВСЁ, что есть в истории; новое отмечено точкой и полужирным
/// (NotificationEntry.IsNew — пометка держится всё открытие окна);</item>
/// <item>«прочитано» ставится построчно, в момент, когда строку реально построили и показали
/// (<see cref="NotificationRow_Loaded"/>), — только это и убавляет счётчик на колокольчике;</item>
/// <item>убирает записи из списка ТОЛЬКО человек: крестиком у строки или кнопкой «Очистить».</item>
/// </list></summary>
public partial class NotificationHistoryWindow : Window
{
    private readonly NotificationCenter _center;
    private readonly ConfigService _cfg;

    /// <summary>Показывать только новые. Живёт на время одного открытия окна: «покажи, что нового» —
    /// разовый жест, а не настройка. По умолчанию выключен, иначе окно снова могло бы оказаться
    /// пустым при полной истории.</summary>
    private bool _onlyUnread;

    /// <summary>Своё представление коллекции, а НЕ CollectionViewSource.GetDefaultView(...).
    /// Представление по умолчанию одно на всё приложение и переживает закрытие окна: прошлая версия
    /// вешала на него фильтр и снимала его вручную в OnClosed — забудь эту строку, и главный список
    /// уведомлений остался бы отфильтрованным навсегда.</summary>
    private readonly ListCollectionView _view;

    public NotificationHistoryWindow(NotificationCenter center, ConfigService cfg)
    {
        InitializeComponent();
        _center = center;
        _cfg = cfg;

        // Пометка «новое» пересобирается на КАЖДОЕ открытие окна: что не успели прочитать в прошлый
        // раз, тем и осталось новым. См. NotificationEntry.IsNew — по IsRead её не нарисовать.
        foreach (var entry in center.History)
            entry.IsNew = !entry.IsRead;

        _view = new ListCollectionView(center.History)
        {
            // Отбор идёт по IsNew, а не по IsRead: IsRead гаснет в тот же миг, когда строку
            // показали, и «только новые» опустел бы через секунду после нажатия.
            // ⚠️ Живой фильтрации (IsLiveFiltering) здесь нет намеренно — отбор пересчитывается
            // только по нажатию кнопки, иначе строка исчезала бы у человека из-под курсора.
            Filter = o => !_onlyUnread || o is not NotificationEntry entry || entry.IsNew,
        };
        ListBoxHistory.ItemsSource = _view;

        RefreshHeader();
        LoadNotificationCategories();
    }

    /// <summary>Подписи кнопки отбора и заглушки пустого списка. Зовётся после всего, что меняет
    /// состав или счётчик.</summary>
    private void RefreshHeader()
    {
        var fresh = _center.History.Count(x => x.IsNew);
        OnlyUnreadToggle.Content = _onlyUnread ? "Показать все" : $"Только новые ({fresh})";
        OnlyUnreadToggle.IsEnabled = _onlyUnread || fresh > 0;
        MarkAllReadBtn.IsEnabled = fresh > 0 || _center.UnreadCount > 0;

        var shown = _view.Count;
        EmptyHint.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = _center.History.Count == 0
            ? "Уведомлений пока нет.\nЗдесь окажется всё, что программа показывала в строке состояния и баннерами."
            : "Новых уведомлений нет — нажмите «Показать все», чтобы увидеть всю историю.";
    }

    /// <summary>Строку построили — значит человек её видит. Только это и гасит уведомление в
    /// счётчике: открытие окна само по себе больше не значит «всё прочитано» (тикет: «убирать из
    /// счётчика количество ТОЛЬКО прочитанные»).
    ///
    /// Работает благодаря виртуализации списка: контейнеры создаются под видимую область, а не на
    /// всю историю. Строки, до которых не долистали, прочитанными не становятся.</summary>
    private void NotificationRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NotificationEntry entry }) return;
        if (entry.IsRead) return;
        _center.MarkRead(entry);
        RefreshHeader();
    }

    private void OnlyUnreadToggle_Click(object sender, RoutedEventArgs e)
    {
        _onlyUnread = !_onlyUnread;
        _view.Refresh();
        RefreshHeader();
    }

    private void MarkAllRead_Click(object sender, RoutedEventArgs e)
    {
        _center.MarkAllRead();
        if (_onlyUnread) _view.Refresh();
        RefreshHeader();
    }

    /// <summary>Удаление ПОШТУЧНО. Без вопроса «точно ли»: это одна строка журнала, а не данные, —
    /// переспрашивать на каждый крестик утомительнее, чем потерять одно сообщение. «Очистить» ниже
    /// переспрашивает, потому что стирает всё разом.</summary>
    private void DeleteOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not NotificationEntry entry) return;
        _center.Delete(entry);
        RefreshHeader();
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
        // Категория перестала (или начала) считаться — счётчик обязан пересчитаться сразу, а не
        // ждать следующего уведомления.
        _center.Refresh();
    }

    private void CategorySettingsToggle_Click(object sender, RoutedEventArgs e) =>
        CategorySettingsPanel.Visibility = CategorySettingsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

    private void Reopen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not NotificationEntry entry) return;

        // Модальные подробности (напр. «Что нового») показываются поверх этого окна — после их
        // закрытия оператор остаётся в списке и может открыть следующее уведомление, поэтому окно
        // истории НЕ закрываем. Баннер-reopen (обновление/синхронизация) рисуется на главном окне, за
        // этим модальным окном его не видно — тогда закрываем, как и раньше, чтобы баннер стал виден.
        if (entry.ReopenIsModal)
        {
            entry.Reopen?.Invoke();
            return;
        }
        entry.Reopen?.Invoke();
        Close();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_center.History.Count == 0) return;
        var reply = AppMessageBox.Show("Очистить всю историю уведомлений?", "Уведомления",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;
        _center.Clear();
        RefreshHeader();
    }
}
