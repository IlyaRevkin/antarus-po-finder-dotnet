using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Экран «Состояние подключения»: доступен ли корень сетевого диска, второй диск, цель
/// проверки входа (домен/веб-сервер по текущему «способу проверки») и источник обновлений — с
/// причиной по каждому пункту и кнопкой «Скопировать», чтобы результат можно было переслать, а не
/// разбирать по RDP.
///
/// Отдельное окно, а не блок внутри Настроек, сознательно: его открывают в том числе тогда, когда
/// приложение только запустилось и «ничего не работает», и оно должно быть доступно из окна входа
/// (AdStartupLoginDialog) — то есть ДО того, как главное окно с Настройками вообще существует.
///
/// Все проверки идут в фоне и с таймаутами (см. ConnectionStatusService): на отвалившейся сетевой
/// шаре сама проверка висит секундами, а окно обязано оставаться живым.</summary>
public partial class ConnectionStatusDialog : Window
{
    private readonly ConfigService _cfg;
    private readonly ObservableCollection<CheckRow> _rows = new();
    private IReadOnlyList<ConnectionCheckResult> _lastResults = Array.Empty<ConnectionCheckResult>();
    private bool _running;

    public ConnectionStatusDialog(ConfigService cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        RowsList.ItemsSource = _rows;
        Loaded += async (_, _) => await RunChecksAsync();
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e) => await RunChecksAsync();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResults.Count == 0)
        {
            StatusText.Text = "Проверка ещё идёт";
            return;
        }
        // Буфер обмена может быть временно занят другим приложением — это не повод показывать
        // окно ошибки поверх диагностического экрана.
        StatusText.Text = Services.ClipboardSafe.TrySetText(ConnectionStatusService.BuildReport(_lastResults))
            ? "Скопировано в буфер обмена"
            : "Буфер обмена занят другим приложением";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Четыре проверки стартуют одновременно и строки обновляются по мере готовности —
    /// последовательный запуск сложил бы таймауты друг за другом (до полуминуты на полностью
    /// оборванной сети), а пользователь всё это время смотрел бы на пустое окно.</summary>
    private async Task RunChecksAsync()
    {
        if (_running) return;
        _running = true;
        RecheckBtn.IsEnabled = false;
        StatusText.Text = "Проверка…";
        _lastResults = Array.Empty<ConnectionCheckResult>();

        _rows.Clear();
        var timeout = ConnectionStatusService.DefaultTimeout;
        var jobs = new List<(CheckRow Row, Task<ConnectionCheckResult> Task)>
        {
            (AddRow("Корень сетевого диска"), ConnectionStatusService.CheckFolderAsync("Корень сетевого диска", _cfg.RootPath(), timeout)),
            (AddRow("Второй диск"), ConnectionStatusService.CheckFolderAsync("Второй диск", _cfg.SecondDiskPath(), timeout)),
            (AddRow("Вход по AD"), ConnectionStatusService.CheckAuthTargetAsync(_cfg, timeout)),
            (AddRow("Источник обновлений"), ConnectionStatusService.CheckUpdateSourcesAsync(_cfg.EffectiveAppUpdatePath(), timeout)),
        };

        var results = new List<ConnectionCheckResult>();
        foreach (var (row, task) in jobs)
        {
            ConnectionCheckResult result;
            try
            {
                result = await task;
            }
            catch (Exception ex)
            {
                // Проверки по контракту не бросают (см. ConnectionStatusService), но окно диагностики
                // не имеет права упасть само — иначе оно бесполезно ровно в тот момент, когда нужно.
                result = new ConnectionCheckResult(row.Title, ConnectionState.Failed, "", $"проверка не выполнилась: {ex.Message}");
            }
            row.Apply(result);
            results.Add(result);
        }

        _lastResults = results;
        var failed = results.Count(r => r.State == ConnectionState.Failed);
        StatusText.Text = failed == 0 ? "Всё доступно" : $"Недоступно пунктов: {failed}";
        RecheckBtn.IsEnabled = true;
        _running = false;
    }

    private CheckRow AddRow(string title)
    {
        var row = new CheckRow(title);
        _rows.Add(row);
        return row;
    }

    /// <summary>Строка списка. Своё INotifyPropertyChanged (а не пересборка коллекции целиком) —
    /// чтобы готовые пункты появлялись по одному, не дожидаясь самого медленного.</summary>
    private sealed class CheckRow : INotifyPropertyChanged
    {
        public CheckRow(string title) => Title = title;

        public string Title { get; }
        public string StateText { get; private set; } = ConnectionStatusService.StateLabel(ConnectionState.Checking);
        public string Target { get; private set; } = "";
        public string Details { get; private set; } = "проверяется…";
        public Brush StateBrush { get; private set; } = Brushes.Gray;

        public void Apply(ConnectionCheckResult result)
        {
            StateText = ConnectionStatusService.StateLabel(result.State);
            Target = result.Target;
            Details = result.Details;
            StateBrush = result.State switch
            {
                ConnectionState.Ok => LookupBrush("SuccessBrush", Brushes.Green),
                ConnectionState.Failed => LookupBrush("ErrorBrush", Brushes.Red),
                _ => Brushes.Gray,
            };
            foreach (var name in new[] { nameof(StateText), nameof(Target), nameof(Details), nameof(StateBrush) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>Цвета берём из темы (светлая/тёмная), но с запасным значением: тема грузится
        /// ресурсами приложения, а это окно может открыться и из окна входа — до того, как всё
        /// оформление применено.</summary>
        private static Brush LookupBrush(string key, Brush fallback) =>
            Application.Current?.TryFindResource(key) as Brush ?? fallback;

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
