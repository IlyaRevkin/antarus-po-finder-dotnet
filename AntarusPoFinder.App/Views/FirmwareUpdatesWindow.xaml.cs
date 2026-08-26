using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Lists locally cached firmware that the server has a newer version of — opened from the
/// MainWindow "Доступно обновление ПО" banner's "Подробно" link. Each row can be updated on its own,
/// selected for a batch update, or opted into silent auto-update going forward.</summary>
public partial class FirmwareUpdatesWindow : Window
{
    private readonly AppServices _services;
    private readonly List<FirmwareUpdateInfo> _updates;
    private readonly Dictionary<FirmwareUpdateInfo, CheckBox> _selectCheckboxes = new();

    /// <summary>How many rows were actually updated while this window was open — the caller uses this
    /// to recompute the remaining banner count/visibility after the dialog closes.</summary>
    public int UpdatedCount { get; private set; }

    public FirmwareUpdatesWindow(AppServices services, IEnumerable<FirmwareUpdateInfo> updates)
    {
        InitializeComponent();
        _services = services;
        _updates = updates.ToList();
        Render();
    }

    private void Render()
    {
        ItemsPanel.Children.Clear();
        _selectCheckboxes.Clear();

        if (_updates.Count == 0)
        {
            ItemsPanel.Children.Add(new TextBlock { Text = "Все локальные прошивки актуальны.", Style = (Style)FindResource("MutedText") });
            UpdateAllButton.IsEnabled = false;
            UpdateSelectedButton.IsEnabled = false;
            return;
        }

        UpdateAllButton.IsEnabled = true;
        UpdateSelectedButton.IsEnabled = true;
        foreach (var u in _updates)
            ItemsPanel.Children.Add(MakeRow(u));
    }

    private Border MakeRow(FirmwareUpdateInfo u)
    {
        var select = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        _selectCheckboxes[u] = select;

        var title = new TextBlock
        {
            Text = $"{u.Name}: {u.CurrentLocalVersion} → {u.Latest.VersionRaw}",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)FindResource("SubtitleText"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(select);
        header.Children.Add(title);

        var changesBtn = new Button { Content = "Что нового", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        changesBtn.Click += (_, _) => ShowChangelog(u);

        var updateBtn = new Button { Content = "Обновить", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        updateBtn.Click += async (_, _) => await ApplyBatchAsync(new List<FirmwareUpdateInfo> { u });

        var autoCheck = new CheckBox
        {
            Content = "Автообновление",
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = _services.Cfg.IsFwAutoUpdate(u.LocalDir),
        };
        autoCheck.Checked += (_, _) => _services.Cfg.SetFwAutoUpdate(u.LocalDir, true);
        autoCheck.Unchecked += (_, _) => _services.Cfg.SetFwAutoUpdate(u.LocalDir, false);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(changesBtn);
        actions.Children.Add(updateBtn);
        actions.Children.Add(autoCheck);

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(actions);

        return new Border { Style = (Style)FindResource("CardBorder"), Margin = new Thickness(0, 0, 0, 10), Child = panel };
    }

    private void ShowChangelog(FirmwareUpdateInfo u)
    {
        var text = string.IsNullOrWhiteSpace(u.Latest.Changelog) ? "Список изменений не указан." : u.Latest.Changelog;
        AppMessageBox.Show(text, $"Что нового — {u.Name} {u.Latest.VersionRaw}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Копирование — в фоновом потоке: прошивка тянется с сетевой шары, и раньше на всё это
    /// время замирало не только это окно, но и всё приложение за ним.</summary>
    private async Task<bool> ApplyUpdateAsync(FirmwareUpdateInfo u)
    {
        try
        {
            var source = SearchService.ToHierarchyResult(u.Latest, localRoot: _services.Cfg.RootPath());
            await Task.Run(() => FirmwareSync.CopyToLocal(source));
            UpdatedCount++;
            _updates.Remove(u);
            return true;
        }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось обновить «{u.Name}»:\n{ex.Message}", "Обновить", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>Общий ход для всех трёх кнопок обновления: пока идёт копирование, кнопки погашены
    /// (иначе повторный клик запустил бы второй проход по тем же прошивкам), внизу видно, какая
    /// именно версия сейчас тянется и сколько их осталось.</summary>
    private async Task ApplyBatchAsync(List<FirmwareUpdateInfo> batch)
    {
        if (batch.Count == 0) return;

        // Занимать нам нечего (пишем только в локальный кэш), но ЧИТАЕМ мы сетевой диск. Пока по нему
        // ходит перестройка раскладки, прочитанное — половина переезда, и утащить это в локальную
        // копию значит получить у себя мусор с виду рабочей версии.
        if (_services.Operations.WholeDiskBusyReason() is { } busyReason)
        {
            AppMessageBox.Show(busyReason, "Обновить", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        UpdateAllButton.IsEnabled = false;
        UpdateSelectedButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        _running = true;
        try
        {
            for (int i = 0; i < batch.Count; i++)
            {
                ProgressText.Text = $"Обновление: {batch[i].Name} ({i + 1} из {batch.Count})";
                ProgressIndicator.Value = i * 100.0 / batch.Count;
                await ApplyUpdateAsync(batch[i]);
            }
        }
        finally
        {
            _running = false;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ProgressText.Text = "";
            ProgressIndicator.Value = 0;
            Render(); // сам расставит IsEnabled — в том числе гасит кнопки, когда обновлять больше нечего
        }
    }

    private async void UpdateAll_Click(object sender, RoutedEventArgs e) => await ApplyBatchAsync(_updates.ToList());

    private async void UpdateSelected_Click(object sender, RoutedEventArgs e) =>
        await ApplyBatchAsync(_selectCheckboxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList());

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Идёт ли прямо сейчас пачка обновлений.</summary>
    private bool _running;

    /// <summary>⚠️ Пока пачка идёт, окно не закрывается.
    ///
    /// Окно перестало быть модальным, и закрытый посреди копирования список приводил бы к тому, что
    /// вызывающая сторона пересчитывала «что ещё не обновлено» по недоделанной пачке, а следующее
    /// нажатие на баннер открывало бы ВТОРОЕ окно, тянущее те же папки одновременно с первым.
    /// Отдельной отмены у копирования нет (FirmwareSync.CopyToLocal без токена), поэтому честнее
    /// сказать «дождитесь», чем сделать вид, что закрытие что-то останавливает.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_running)
        {
            e.Cancel = true;
            AppMessageBox.Show(
                "Обновление ещё идёт — дождитесь окончания." + Environment.NewLine + Environment.NewLine +
                "Программой при этом можно пользоваться: окно не модальное, просто отодвиньте его.",
                "Обновить", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        base.OnClosing(e);
    }
}
