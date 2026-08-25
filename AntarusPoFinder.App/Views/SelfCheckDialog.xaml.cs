using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>«Проверка компьютера»: не «доступно/недоступно», а разбор — что не так, почему и что с
/// этим делать. Сюда же приделан единственный путь к готовому тикету: коллеге, у которого «ничего
/// не работает», не нужно ничего объяснять словами.
///
/// Отдельное окно, а не блок внутри Настроек, сознательно: его открывают в том числе тогда, когда
/// приложение только запустилось и «ничего не работает», и оно должно быть доступно из окна входа
/// (AdStartupLoginDialog) — то есть ДО того, как главное окно с Настройками вообще существует.
///
/// Ни одного вывода это окно не делает само: собирает снимок машины (SelfCheckProbe) и показывает
/// то, что решил SelfCheckAnalyzer в Core. Все проверки идут в фоне и с таймаутами — на отвалившейся
/// сетевой шаре сама проверка висит секундами, а окно обязано оставаться живым.</summary>
public partial class SelfCheckDialog : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<CheckRow> _rows = new();
    private SelfCheckFacts? _facts;
    private IReadOnlyList<SelfCheckFinding> _findings = Array.Empty<SelfCheckFinding>();
    private bool _running;

    public SelfCheckDialog(AppServices services)
    {
        InitializeComponent();
        _services = services;
        RowsList.ItemsSource = _rows;
        Loaded += async (_, _) => await RunChecksAsync();
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e) => await RunChecksAsync();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_facts is null)
        {
            StatusText.Text = "Проверка ещё идёт";
            return;
        }
        // Буфер обмена может быть временно занят другим приложением — это не повод показывать
        // окно ошибки поверх диагностического экрана.
        StatusText.Text = ClipboardSafe.TrySetText(SelfCheckReport.BuildReport(_facts, _findings, DateTime.Now))
            ? "Скопировано в буфер обмена"
            : "Буфер обмена занят другим приложением";
    }

    private async Task RunChecksAsync()
    {
        if (_running) return;
        _running = true;
        RecheckBtn.IsEnabled = false;
        StatusText.Text = "Проверка…";
        ProblemBanner.Visibility = Visibility.Collapsed;
        _rows.Clear();
        _facts = null;
        _findings = Array.Empty<SelfCheckFinding>();

        SelfCheckFacts facts;
        try
        {
            facts = await SelfCheckProbe.CollectAsync(_services.Cfg, _services.Db, _services.CurrentUserName, SelfCheckProbe.DefaultTimeout);
        }
        catch (Exception ex)
        {
            // Сбор снимка по контракту не бросает, но окно диагностики не имеет права упасть само —
            // иначе оно бесполезно ровно в тот момент, когда нужно.
            StatusText.Text = $"Проверка не выполнилась: {ex.Message}";
            RecheckBtn.IsEnabled = true;
            _running = false;
            return;
        }

        _facts = facts;
        _findings = SelfCheckAnalyzer.Analyze(facts);
        foreach (var finding in _findings) _rows.Add(new CheckRow(finding));

        var problems = _findings.Count(x => x.Severity == SelfCheckSeverity.Problem);
        var warnings = _findings.Count(x => x.Severity == SelfCheckSeverity.Warning);
        StatusText.Text = problems > 0
            ? $"Проблем: {problems}"
            : warnings > 0 ? $"Предупреждений: {warnings}" : "Всё в порядке";

        if (problems > 0)
        {
            ProblemBannerText.Text = problems == 1
                ? "Найдена проблема. Если разобраться самостоятельно не выходит — составьте тикет: он уже будет заполнен всем, что программа выяснила про этот компьютер, и объяснять на словах ничего не придётся."
                : $"Найдено проблем: {problems}. Если разобраться самостоятельно не выходит — составьте тикет: он уже будет заполнен всем, что программа выяснила про этот компьютер, и объяснять на словах ничего не придётся.";
            ProblemBanner.Visibility = Visibility.Visible;
        }

        RecheckBtn.IsEnabled = true;
        _running = false;
    }

    /// <summary>Тикет НИКОГДА не создаётся молча: сначала показывается весь текст целиком, его можно
    /// дописать своими словами или передумать, и только кнопка в том окне заводит тикет.</summary>
    private void CreateTicket_Click(object sender, RoutedEventArgs e)
    {
        if (_facts is null) return;

        var draft = SelfCheckReport.BuildTicketText(_facts, _findings, DateTime.Now);
        var dlg = new SelfCheckTicketDialog(draft) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        try
        {
            TicketSyncService.CreateTicket(_services, dlg.SelectedType, dlg.TicketText);
        }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось создать тикет: {ex.Message}", "Проверка компьютера",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Недоступный диск здесь не ошибка: событие лежит в очереди и уйдёт само, когда диск
        // появится (TicketSyncService.FlushOutbox зовётся при каждом открытии страницы «Тикеты»).
        // Сказать об этом надо прямо, иначе человек решит, что тикет пропал.
        var root = _services.Cfg.RootPath();
        var onDisk = root.Length > 0 && System.IO.Directory.Exists(root);
        AppMessageBox.Show(
            onDisk
                ? "Тикет создан и отправлен. Он виден на странице «Тикеты»."
                : "Тикет создан. Сетевой диск сейчас недоступен, поэтому он ждёт отправки на этом компьютере и уйдёт сам, как только диск появится. Он уже виден на странице «Тикеты».",
            "Проверка компьютера", MessageBoxButton.OK, MessageBoxImage.Information);

        CreateTicketBtn.IsEnabled = false;
        StatusText.Text = "Тикет создан";
    }

    /// <summary>Строка списка. Обычный неизменяемый объект: результаты приходят все разом (снимок
    /// собирается целиком), поэтому обновлять строки по одной, как раньше, больше незачем.</summary>
    private sealed class CheckRow
    {
        public CheckRow(SelfCheckFinding finding)
        {
            StateText = SelfCheckAnalyzer.SeverityLabel(finding.Severity);
            Title = finding.Title;
            Target = finding.Target;
            Reason = finding.Reason;
            FixText = finding.Fix.Length > 0 ? "Что делать: " + finding.Fix : "";
            StateBrush = finding.Severity switch
            {
                SelfCheckSeverity.Ok => LookupBrush("SuccessBrush", Brushes.Green),
                SelfCheckSeverity.Warning => LookupBrush("WarningBrush", Brushes.Orange),
                SelfCheckSeverity.Problem => LookupBrush("ErrorBrush", Brushes.Red),
                _ => Brushes.Gray,
            };
        }

        public string StateText { get; }
        public string Title { get; }
        public string Target { get; }
        public string Reason { get; }
        public string FixText { get; }
        public Brush StateBrush { get; }

        public Visibility TargetVisibility => Target.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FixVisibility => FixText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Цвета берём из темы (светлая/тёмная), но с запасным значением: тема грузится
        /// ресурсами приложения, а это окно может открыться и из окна входа — до того, как всё
        /// оформление применено.</summary>
        private static Brush LookupBrush(string key, Brush fallback) =>
            Application.Current?.TryFindResource(key) as Brush ?? fallback;
    }
}
