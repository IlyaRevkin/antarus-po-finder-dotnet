using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using AntarusPoFinder.Core.Loader;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Интерактивная загрузка проекта в ПЛК и отдельная сборка .psl → .lfs через Segnetics
/// Loader Automation. Searcher хранит только UI операции и локальную копию исходника; подключение к
/// ПЛК, сборку PSL и загрузку выполняет production-пайплайн Loader.
///
/// Окно КОМПАКТНОЕ и запускается САМО. Раньше оператор, нажав на карточке «Загрузить в ПЛК»,
/// попадал в окно 760×610 с пятью кнопками внизу и обязан был нажать вторую кнопку «Загрузить» —
/// притом что файл уже выбран карточкой и выбирать было нечего. Теперь работа стартует при
/// открытии, всё необязательное (сменить файл, подготовка ПЛК, журнал) убрано под «Дополнительно» и
/// «Подробности», а внизу максимум две кнопки: «Остановить» во время работы и «Закрыть» после.
///
/// Недоступность Automation проверяется ДО открытия окна (см. <see cref="EnsureAvailable"/>):
/// пустое окно с красным баннером и неработающей кнопкой оператору ничего не объясняло.</summary>
public partial class LoaderDialog : Window
{
    private readonly ConfigService _cfg;
    private readonly IFirmwareLoaderBackend _backend;
    private readonly bool _isBuild;
    private readonly Stopwatch _operationStopwatch = new();
    private readonly DispatcherTimer _operationElapsedTimer;

    private LoaderJob _job;
    private CancellationTokenSource? _cts;
    private LoaderWorkspace? _workspace;
    private readonly List<string> _logLines = new();
    private string? _lastLogMessage;
    private LoaderLogLevel _lastLogLevel;
    private DateTime _lastLogAtUtc;
    private bool _running;
    private bool _everStarted;

    private static readonly TimeSpan WorkspaceRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan ImmediateDuplicateWindow = TimeSpan.FromSeconds(1);

    /// <summary>Операция завершилась успехом. Для сборки это значит, что .lfs уже лежит в папке
    /// версии (см. <see cref="PublishedLfs"/>) — вызывающий код может обновить свою выдачу.</summary>
    public bool Succeeded { get; private set; }

    /// <summary>Пути, по которым реально сохранён собранный .lfs.</summary>
    public IReadOnlyList<string> PublishedLfs { get; private set; } = Array.Empty<string>();

    public LoaderDialog(ConfigService cfg, LoaderJob job)
    {
        InitializeComponent();
        _cfg = cfg;
        _job = job;
        _isBuild = job.Operation == LoaderOperation.Build;
        _backend = FirmwareLoaderFactory.Create(cfg.LoaderExePath());

        _operationElapsedTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _operationElapsedTimer.Tick += (_, _) => UpdateOperationElapsedText();

        var version = string.IsNullOrEmpty(_backend.DisplayVersion) ? "" : $" v{_backend.DisplayVersion}";
        Title = _isBuild ? $"Сборка LFS через Segnetics Loader{version}" : $"Загрузка через Segnetics Loader{version}";
        HeaderLabel.Text = _isBuild
            ? $"Сборка LFS из PSL: {job.VersionName}"
            : $"Загрузка в ПЛК: {job.VersionName}";

        PrepareControllerCheck.IsChecked = cfg.LoaderFormatAndUpdateDefault();
        // Подготовка ПЛК относится только к заливке: сборка к контроллеру не подключается вообще.
        if (_isBuild) PrepareControllerCheck.Visibility = Visibility.Collapsed;

        AdvancedExpander.Expanded += (_, _) => AdvancedArrow.Text = "▾";
        AdvancedExpander.Collapsed += (_, _) => AdvancedArrow.Text = "▸";
        DetailsExpander.Expanded += (_, _) => DetailsArrow.Text = "▾";
        DetailsExpander.Collapsed += (_, _) => DetailsArrow.Text = "▸";

        RefreshSourceLabels();
        RefreshPreparationLabel();
        SetRunning(false);

        if (!_backend.IsAvailable)
        {
            ShowUnavailable();
            return;
        }

        AppendLog(_isBuild
            ? "Исходник будет скопирован в локальную рабочую область; на диск уедет только готовый LFS."
            : "Файл будет скопирован в локальную рабочую область перед запуском Loader.");

        // Старт сразу после первой отрисовки: оператор видит окно с прогрессом, а не пустую форму,
        // которую надо «завести» второй кнопкой.
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = RunAsync()));

        Task.Run(() =>
        {
            try { LoaderWorkspace.CleanupOlderThan(ConfigService.LocalLoader, WorkspaceRetention); }
            catch (Exception) { }
        });
    }

    /// <summary>Проверка доступности Automation ОДНИМ местом на всех вызывающих: причина
    /// показывается до открытия окна, обычным сообщением, а не красным баннером внутри окна, из
    /// которого всё равно ничего не запустить.</summary>
    public static bool EnsureAvailable(Window? owner, ConfigService cfg)
    {
        var backend = FirmwareLoaderFactory.Create(cfg.LoaderExePath());
        if (backend.IsAvailable) return true;
        AppMessageBox.Show(
            backend.UnavailableReason ?? "Segnetics Loader Automation недоступен.",
            "Segnetics Loader", MessageBoxButton.OK, MessageBoxImage.Error);
        return false;
    }

    /// <summary>Загрузка в ПЛК с карточки версии. Возвращает true, если Loader отчитался об успехе.</summary>
    public static bool ShowDeploy(Window? owner, ConfigService cfg, LoaderJob job) =>
        Run(owner, cfg, job with { Operation = LoaderOperation.Deploy });

    /// <summary>Сборка .lfs из .psl без подключения к ПЛК (модерация, догрузка после выкладки).</summary>
    public static bool ShowBuild(Window? owner, ConfigService cfg, LoaderJob job) =>
        Run(owner, cfg, job with { Operation = LoaderOperation.Build });

    private static bool Run(Window? owner, ConfigService cfg, LoaderJob job)
    {
        if (!EnsureAvailable(owner, cfg)) return false;
        var dialog = new LoaderDialog(cfg, job) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Succeeded;
    }

    // ── Запуск операции ───────────────────────────────────────────────────

    private async Task RunAsync()
    {
        if (_running) return;

        if (!_backend.IsAvailable)
        {
            ShowUnavailable();
            return;
        }

        var source = _job.SourcePath?.Trim() ?? "";
        if (string.IsNullOrEmpty(source) || !File.Exists(source))
        {
            FinishWithError(string.IsNullOrEmpty(source)
                ? "Файл для загрузки не выбран."
                : $"Файл не найден:\n{source}");
            return;
        }

        var prepareController = !_isBuild && PrepareControllerCheck.IsChecked == true;
        if (!_isBuild && prepareController != _cfg.LoaderFormatAndUpdateDefault())
            _cfg.SetLoaderFormatAndUpdateDefault(prepareController);
        RefreshPreparationLabel();

        _everStarted = true;
        SetRunning(true);
        _cts = new CancellationTokenSource();
        var cancellationToken = _cts.Token;
        var progress = new Progress<LoaderProgress>(OnProgress);

        try
        {
            if (_isBuild) await RunBuildAsync(progress, cancellationToken);
            else await RunDeployAsync(source, prepareController, progress, cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            var message = string.IsNullOrWhiteSpace(ex.Message) || ex.Message == "The operation was canceled."
                ? "Операция отменена."
                : ex.Message;
            AppendLog(message, LoaderLogLevel.Warning);
            Progress.IsIndeterminate = false;
            PercentLabel.Text = "";
            StageLabel.Text = "Остановлено";
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message, LoaderLogLevel.Error);
            ShowFailedState("Ошибка");
        }
        finally
        {
            SaveLogToWorkspace();
            SetRunning(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task RunDeployAsync(
        string source, bool prepareController, IProgress<LoaderProgress> progress, CancellationToken cancellationToken)
    {
        var workspace = LoaderWorkspace.Create(ConfigService.LocalLoader, _job.VersionName);
        _workspace = workspace;
        AppendLog($"Рабочая область: {workspace.Dir}");

        var localSource = await Task.Run(() => workspace.Import(source), cancellationToken);
        AppendLog($"Локальная копия готова: {localSource}", LoaderLogLevel.Success);

        var isPslSource = string.Equals(
            Path.GetExtension(source), LoaderFiles.PslExtension, StringComparison.OrdinalIgnoreCase);
        var outputLfsPath = isPslSource
            ? Path.Combine(workspace.OutputDir, LoaderFiles.LfsNameFor(source))
            : null;

        var request = new LoaderRequest
        {
            Operation = LoaderOperation.Deploy,
            SourcePath = localSource,
            WorkspaceDir = workspace.Dir,
            OutputPath = outputLfsPath,
            VersionName = _job.VersionName,
            Options = new LoaderOptions { FormatAndUpdateFirmware = prepareController },
        };

        var result = await _backend.RunAsync(request, progress, cancellationToken);

        // Собранный по ходу заливки LFS сохраняем В ПАПКУ ВЕРСИИ НА ДИСКЕ, а не только в локальной
        // копии — иначе следующий наладчик на другой машине снова увидит один .psl. Неудача
        // публикации — предупреждение, а не провал операции: прошивка в контроллере уже лежит, и
        // рисовать «Ошибка» после успешной заливки было бы прямой ложью.
        if (result.Success && outputLfsPath is not null)
        {
            if (!File.Exists(outputLfsPath) || !result.Artifacts.Any(path => PathsEqual(path, outputLfsPath)))
                AppendLog("Loader не вернул собранный LFS — в папке версии он не появится.", LoaderLogLevel.Warning);
            else
                await PublishBuiltLfsAsync(outputLfsPath);
        }

        AppendLog(result.Message, result.Success ? LoaderLogLevel.Success : LoaderLogLevel.Error);
        if (result.Success)
        {
            Succeeded = true;
            Progress.IsIndeterminate = false;
            Progress.Value = 100;
            PercentLabel.Text = "100%";
            StageLabel.Text = "Загрузка завершена";
        }
        else
        {
            ShowFailedState("Ошибка");
        }
    }

    private async Task RunBuildAsync(IProgress<LoaderProgress> progress, CancellationToken cancellationToken)
    {
        var plan = new LfsConversionPlan(_job.SourcePath, LfsPublisher.Plan(_job.NetworkFolder, _job.LocalFolder));
        var result = await LfsConversionService.BuildAndPublishAsync(
            _backend, plan, ConfigService.LocalLoader, _job.VersionName, progress, cancellationToken,
            workspace => _workspace = workspace);

        foreach (var warning in result.Warnings) AppendLog(warning, LoaderLogLevel.Warning);
        PublishedLfs = result.Published;

        switch (result.Status)
        {
            case LfsConversionStatus.Built:
                Succeeded = true;
                AppendLog(result.Message, LoaderLogLevel.Success);
                Progress.IsIndeterminate = false;
                Progress.Value = 100;
                PercentLabel.Text = "100%";
                StageLabel.Text = "LFS собран";
                break;

            case LfsConversionStatus.Cancelled:
                AppendLog(result.Message, LoaderLogLevel.Warning);
                Progress.IsIndeterminate = false;
                PercentLabel.Text = "";
                StageLabel.Text = "Остановлено";
                break;

            default:
                AppendLog(result.Message, LoaderLogLevel.Error);
                ShowFailedState("Ошибка сборки");
                break;
        }
    }

    private async Task PublishBuiltLfsAsync(string builtLfs)
    {
        var plan = LfsPublisher.Plan(_job.NetworkFolder, _job.LocalFolder);
        var published = await Task.Run(() => LfsPublisher.PublishAll(builtLfs, plan));
        PublishedLfs = published.Published;
        foreach (var path in published.Published)
            AppendLog($"Собранный LFS сохранён: {path}", LoaderLogLevel.Success);
        foreach (var warning in published.Warnings)
            AppendLog(warning, LoaderLogLevel.Warning);
    }

    // ── Состояние окна ────────────────────────────────────────────────────

    private void SetRunning(bool running)
    {
        _running = running;
        StopBtn.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StopBtn.IsEnabled = running;
        CloseBtn.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        // «Рабочая папка» и «Сохранить журнал…» нужны только по итогу — до первого запуска их
        // показывать нечему, во время работы они только отвлекают.
        MoreBtn.Visibility = !running && _everStarted ? Visibility.Visible : Visibility.Collapsed;
        OpenWorkspaceItem.IsEnabled = _workspace is not null && Directory.Exists(_workspace.Dir);
        SaveLogItem.IsEnabled = _logLines.Count > 0;

        ChangeSourceBtn.IsEnabled = !running;
        RestartBtn.IsEnabled = !running && _backend.IsAvailable;
        PrepareControllerCheck.IsEnabled = !running;

        if (running)
        {
            StartOperationElapsedTimer();
            Progress.IsIndeterminate = false;
            Progress.Value = 0;
            PercentLabel.Text = "0%";
            StageLabel.Text = "Запуск…";
        }
        else
        {
            StopOperationElapsedTimer();
            // Операция закончилась — активной становится «Закрыть», а не то, на чём остался фокус
            // (после авто-старта это «Запустить заново» в «Дополнительно»: Enter/пробел по привычке
            // запускал загрузку ВТОРОЙ раз вместо закрытия окна — жалоба «после загрузки выделенной
            // должна быть кнопка Закрыть»). IsDefault=true даёт и подсветку, и реакцию на Enter;
            // IsCancel на кнопке остаётся, поэтому Esc закрывает окно как раньше.
            CloseBtn.IsDefault = true;
            // В конструкторе SetRunning(false) зовётся ДО показа окна — там Focus() вернул бы false
            // и ничего не сделал, поэтому его там и не пробуем: важен только вызов по итогу
            // операции, когда окно уже открыто.
            if (IsLoaded) CloseBtn.Focus();
        }
    }

    private void ShowUnavailable()
    {
        UnavailableBanner.Visibility = Visibility.Visible;
        UnavailableReasonLabel.Text = _backend.UnavailableReason ?? "";
        if (_backend.UnavailableReason is { Length: > 0 } reason) AppendLog(reason, LoaderLogLevel.Error);
        StageLabel.Text = "Не запускалось";
        PercentLabel.Text = "";
        SetRunning(false);
    }

    /// <summary>Провал показывается сразу с раскрытым журналом: разбираться без него всё равно
    /// невозможно, а лишний клик по «Подробности» в этот момент — издевательство.</summary>
    private void ShowFailedState(string stage)
    {
        Progress.IsIndeterminate = false;
        PercentLabel.Text = "";
        StageLabel.Text = stage;
        DetailsExpander.IsExpanded = true;
    }

    private void FinishWithError(string message)
    {
        AppendLog(message, LoaderLogLevel.Error);
        ShowFailedState("Ошибка");
        SetRunning(false);
    }

    private void RefreshSourceLabels()
    {
        var source = _job.SourcePath ?? "";
        var name = string.IsNullOrEmpty(source) ? "не выбран" : Path.GetFileName(source);
        SourceLabel.Text = $"Файл: {name}";
        SourceLabel.ToolTip = string.IsNullOrEmpty(source) ? null : source;
        AdvancedSourceLabel.Text = string.IsNullOrEmpty(source) ? "Файл не выбран." : $"Файл: {source}";
    }

    /// <summary>Строка про подготовку ПЛК. Авто-старт применяет ЗАПОМНЕННОЕ значение галки, поэтому
    /// оператор обязан видеть его до и во время работы, а не узнавать по факту форматирования.</summary>
    private void RefreshPreparationLabel()
    {
        if (_isBuild)
        {
            PreparationLabel.Visibility = Visibility.Collapsed;
            return;
        }

        var prepare = PrepareControllerCheck.IsChecked == true;
        PreparationLabel.Visibility = Visibility.Visible;
        PreparationLabel.Text = prepare
            ? "Подготовка ПЛК: форматирование проекта и обновление ядра — включено (запомненная настройка)."
            : "Подготовка ПЛК: без форматирования и обновления ядра (запомненная настройка).";
        PreparationLabel.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty, prepare ? "WarningBrush" : "TextMutedBrush");
    }

    // ── Кнопки ────────────────────────────────────────────────────────────

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopBtn.IsEnabled = false;
        StageLabel.Text = "Отправляю команду отмены…";
    }

    private void Restart_Click(object sender, RoutedEventArgs e) => _ = RunAsync();

    private void ChangeSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите проект для загрузки",
            Filter = _isBuild
                ? "Исходник Segnetics (*.psl)|*.psl|Все файлы (*.*)|*.*"
                : "Проекты Segnetics (*.lfs;*.psl)|*.lfs;*.psl|Все файлы (*.*)|*.*",
        };
        var current = _job.SourcePath ?? "";
        if (!string.IsNullOrEmpty(current))
        {
            var directory = Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                dialog.InitialDirectory = directory;
        }
        if (dialog.ShowDialog() != true) return;

        _job = _job with { SourcePath = dialog.FileName };
        RefreshSourceLabels();
        AppendLog($"Выбран другой файл: {dialog.FileName}");
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (MoreBtn.ContextMenu is not { } menu) return;
        menu.PlacementTarget = MoreBtn;
        menu.IsOpen = true;
    }

    private void OpenWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || !Directory.Exists(_workspace.Dir)) return;
        try { Process.Start(new ProcessStartInfo(_workspace.Dir) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            AppendLog($"Не удалось открыть папку: {ex.Message}", LoaderLogLevel.Warning);
        }
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить журнал операции",
            Filter = "Текстовый файл (*.txt)|*.txt|Все файлы (*.*)|*.*",
            FileName = $"loader_{LoaderFileStem()}.txt",
        };
        if (dialog.ShowDialog() != true) return;

        try { File.WriteAllLines(dialog.FileName, _logLines); }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось сохранить файл:\n{ex.Message}", "Segnetics Loader",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ── Прогресс и журнал ─────────────────────────────────────────────────

    private void OnProgress(LoaderProgress value)
    {
        if (value.Percent >= 0)
        {
            Progress.IsIndeterminate = false;
            Progress.Value = Math.Clamp(value.Percent, 0, 100);
            PercentLabel.Text = $"{Progress.Value:0}%";
        }
        else if (Progress.Value == 0)
        {
            Progress.IsIndeterminate = true;
            PercentLabel.Text = "";
        }

        if (value.UpdatesStage)
            StageLabel.Text = value.Stage;
        if (value.Percent < 100 && !string.IsNullOrWhiteSpace(value.Message))
            AppendLog(value.Message, value.Level);
    }

    private void StartOperationElapsedTimer()
    {
        _operationElapsedTimer.Stop();
        _operationStopwatch.Restart();
        ElapsedLabel.Text = FormatOperationElapsed(TimeSpan.Zero);
        ElapsedLabel.Visibility = Visibility.Visible;
        _operationElapsedTimer.Start();
    }

    private void StopOperationElapsedTimer()
    {
        if (_operationStopwatch.IsRunning) _operationStopwatch.Stop();
        _operationElapsedTimer.Stop();
        UpdateOperationElapsedText();
    }

    private void UpdateOperationElapsedText() =>
        ElapsedLabel.Text = FormatOperationElapsed(_operationStopwatch.Elapsed);

    private static string FormatOperationElapsed(TimeSpan elapsed)
    {
        var totalMinutes = Math.Max(0, (int)elapsed.TotalMinutes);
        return $"{totalMinutes:00}:{elapsed.Seconds:00}";
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void AppendLog(string message, LoaderLogLevel level = LoaderLogLevel.Info)
    {
        var normalizedMessage = message.TrimEnd();
        var nowUtc = DateTime.UtcNow;
        if (level == _lastLogLevel &&
            string.Equals(normalizedMessage, _lastLogMessage, StringComparison.Ordinal) &&
            nowUtc - _lastLogAtUtc <= ImmediateDuplicateWindow)
        {
            return;
        }

        _lastLogMessage = normalizedMessage;
        _lastLogLevel = level;
        _lastLogAtUtc = nowUtc;

        var line = $"{DateTime.Now:HH:mm:ss}  {normalizedMessage}";
        _logLines.Add(line);

        var paragraph = new Paragraph(new Run(line))
        {
            Margin = new Thickness(0, 0, 0, 2),
        };
        paragraph.SetResourceReference(TextElement.ForegroundProperty, level switch
        {
            LoaderLogLevel.Success => "SuccessBrush",
            LoaderLogLevel.Warning => "WarningBrush",
            LoaderLogLevel.Error => "ErrorBrush",
            _ => "TextBrush",
        });
        LogDocument.Blocks.Add(paragraph);
        LogBox.ScrollToEnd();
        SaveLogItem.IsEnabled = true;
    }

    private void SaveLogToWorkspace()
    {
        if (_workspace is null || _logLines.Count == 0) return;
        try { File.WriteAllLines(_workspace.LogPath, _logLines); }
        catch (Exception ex)
        {
            AppendLog($"Не удалось сохранить журнал в рабочую область: {ex.Message}", LoaderLogLevel.Warning);
        }
    }

    private string LoaderFileStem()
    {
        var stem = string.Join("_", _job.VersionName.Split(
            Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrEmpty(stem) ? DateTime.Now.ToString("yyyyMMdd_HHmmss") : stem;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _cts?.Cancel();
        _operationElapsedTimer.Stop();
        base.OnClosing(e);
    }
}
