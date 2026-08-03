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

/// <summary>Интерактивная загрузка проекта через Segnetics Loader Automation. Searcher хранит
/// только UI операции и локальную копию исходника; подключение к ПЛК, сборку PSL и загрузку
/// выполняет production-пайплайн Loader.</summary>
public partial class LoaderDialog : Window
{
    private readonly ConfigService _cfg;
    private readonly IFirmwareLoaderBackend _backend;
    private readonly string _versionName;
    private readonly Stopwatch _operationStopwatch = new();
    private readonly DispatcherTimer _operationElapsedTimer;

    private CancellationTokenSource? _cts;
    private LoaderWorkspace? _workspace;
    private readonly List<string> _logLines = new();
    private string? _lastLogMessage;
    private LoaderLogLevel _lastLogLevel;
    private DateTime _lastLogAtUtc;

    private static readonly TimeSpan WorkspaceRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan ImmediateDuplicateWindow = TimeSpan.FromSeconds(1);

    public LoaderDialog(ConfigService cfg, string versionName, string sourcePath)
    {
        InitializeComponent();
        _operationElapsedTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _operationElapsedTimer.Tick += (_, _) => UpdateOperationElapsedText();
        _cfg = cfg;
        _versionName = versionName;
        _backend = FirmwareLoaderFactory.Create(cfg.LoaderExePath());

        Title = string.IsNullOrEmpty(_backend.DisplayVersion)
            ? "Загрузка через Segnetics Loader"
            : $"Загрузка через Segnetics Loader v{_backend.DisplayVersion}";
        HeaderLabel.Text = $"Загрузка в ПЛК: {versionName}";
        UnavailableBanner.Visibility = _backend.IsAvailable ? Visibility.Collapsed : Visibility.Visible;
        UnavailableReasonLabel.Text = _backend.UnavailableReason ?? "";
        RunBtn.IsEnabled = _backend.IsAvailable;

        SourceInput.Text = sourcePath;
        SourceInput.Loaded += (_, _) => ScrollSourcePathToEnd();
        PrepareControllerCheck.IsChecked = cfg.LoaderFormatAndUpdateDefault();

        AppendLog("Файл будет скопирован в локальную рабочую область перед запуском Loader.");
        if (!_backend.IsAvailable && _backend.UnavailableReason is { Length: > 0 } reason)
            AppendLog(reason, LoaderLogLevel.Error);

        Task.Run(() =>
        {
            try { LoaderWorkspace.CleanupOlderThan(ConfigService.LocalLoader, WorkspaceRetention); }
            catch (Exception) { }
        });
    }

    public static void ShowDeploy(Window? owner, ConfigService cfg, string versionName, string sourcePath) =>
        new LoaderDialog(cfg, versionName, sourcePath) { Owner = owner }.ShowDialog();

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (!_backend.IsAvailable)
        {
            AppMessageBox.Show(
                _backend.UnavailableReason ?? "Segnetics Loader Automation недоступен.",
                "Segnetics Loader",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var source = SourceInput.Text.Trim();
        if (string.IsNullOrEmpty(source))
        {
            AppMessageBox.Show("Укажите файл для загрузки.", "Segnetics Loader",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!File.Exists(source))
        {
            AppMessageBox.Show($"Файл не найден:\n{source}", "Segnetics Loader",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var prepareController = PrepareControllerCheck.IsChecked == true;
        _cfg.SetLoaderFormatAndUpdateDefault(prepareController);

        SetRunning(true);
        _cts = new CancellationTokenSource();
        var cancellationToken = _cts.Token;
        var progress = new Progress<LoaderProgress>(OnProgress);

        try
        {
            var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(source))
                ?? throw new InvalidDataException("Не удалось определить папку исходного проекта.");
            var isPslSource = string.Equals(
                Path.GetExtension(source),
                ".psl",
                StringComparison.OrdinalIgnoreCase);
            var workspace = LoaderWorkspace.Create(ConfigService.LocalLoader, _versionName);
            _workspace = workspace;
            OpenWorkspaceBtn.IsEnabled = true;
            AppendLog($"Рабочая область: {workspace.Dir}");

            var localSource = await Task.Run(() => workspace.Import(source), cancellationToken);
            AppendLog($"Локальная копия готова: {localSource}", LoaderLogLevel.Success);
            var outputLfsPath = isPslSource
                ? Path.Combine(
                    workspace.OutputDir,
                    $"{Path.GetFileNameWithoutExtension(source)}.lfs")
                : null;

            var request = new LoaderRequest
            {
                SourcePath = localSource,
                WorkspaceDir = workspace.Dir,
                OutputPath = outputLfsPath,
                VersionName = _versionName,
                Options = new LoaderOptions { FormatAndUpdateFirmware = prepareController },
            };

            var result = await _backend.RunAsync(request, progress, cancellationToken);
            if (result.Success && outputLfsPath is not null)
            {
                if (!File.Exists(outputLfsPath) ||
                    !result.Artifacts.Any(path => PathsEqual(path, outputLfsPath)))
                {
                    throw new InvalidDataException(
                        "Проект загружен в ПЛК, но Loader не вернул сохранённый LFS-файл.");
                }

                IReadOnlyList<string> published;
                try
                {
                    published = await Task.Run(() => workspace.Publish(sourceDirectory));
                }
                catch (Exception exception)
                {
                    throw new IOException(
                        "Проект загружен в ПЛК, но собранный LFS не удалось сохранить в папке проекта.",
                        exception);
                }

                foreach (var path in published)
                {
                    AppendLog($"Собранный LFS сохранён: {path}", LoaderLogLevel.Success);
                }
            }

            AppendLog(result.Message, result.Success ? LoaderLogLevel.Success : LoaderLogLevel.Error);
            if (result.Success)
            {
                Progress.IsIndeterminate = false;
                Progress.Value = 100;
                PercentLabel.Text = "100%";
                StageLabel.Text = "Загрузка завершена";
            }
            else
            {
                Progress.IsIndeterminate = false;
                PercentLabel.Text = "";
                StageLabel.Text = "Ошибка";
            }
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
            Progress.IsIndeterminate = false;
            PercentLabel.Text = "";
            StageLabel.Text = "Ошибка";
        }
        finally
        {
            SaveLogToWorkspace();
            SetRunning(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopBtn.IsEnabled = false;
        StageLabel.Text = "Отправляю команду отмены…";
    }

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

    private void SetRunning(bool running)
    {
        RunBtn.IsEnabled = !running && _backend.IsAvailable;
        StopBtn.IsEnabled = running;
        BrowseSourceBtn.IsEnabled = !running;
        SourceInput.IsEnabled = !running;
        PrepareControllerCheck.IsEnabled = !running;
        SaveLogBtn.IsEnabled = _logLines.Count > 0;
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
        }
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
        if (_operationStopwatch.IsRunning)
        {
            _operationStopwatch.Stop();
        }

        _operationElapsedTimer.Stop();
        UpdateOperationElapsedText();
    }

    private void UpdateOperationElapsedText()
    {
        ElapsedLabel.Text = FormatOperationElapsed(_operationStopwatch.Elapsed);
    }

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
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
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
        SaveLogBtn.IsEnabled = true;
    }

    private void SaveLogToWorkspace()
    {
        if (_workspace is null || _logLines.Count == 0) return;
        try { File.WriteAllLines(_workspace.LogPath, _logLines); }
        catch (Exception ex)
        {
            AppendLog($"Не удалось сохранить лог в рабочую область: {ex.Message}", LoaderLogLevel.Warning);
        }
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить лог загрузки",
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

    private string LoaderFileStem()
    {
        var stem = string.Join("_", _versionName.Split(
            Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrEmpty(stem) ? DateTime.Now.ToString("yyyyMMdd_HHmmss") : stem;
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите проект для загрузки",
            Filter = "Проекты Segnetics (*.lfs;*.psl)|*.lfs;*.psl|Все файлы (*.*)|*.*",
        };
        var current = SourceInput.Text.Trim();
        if (!string.IsNullOrEmpty(current))
        {
            var directory = Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                dialog.InitialDirectory = directory;
        }
        if (dialog.ShowDialog() == true)
        {
            SourceInput.Text = dialog.FileName;
            ScrollSourcePathToEnd();
        }
    }

    private void ScrollSourcePathToEnd()
    {
        SourceInput.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() =>
            {
                SourceInput.CaretIndex = SourceInput.Text.Length;
                SourceInput.SelectionLength = 0;
                SourceInput.ScrollToHorizontalOffset(SourceInput.ExtentWidth);
            }));
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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _cts?.Cancel();
        _operationElapsedTimer.Stop();
        base.OnClosing(e);
    }
}
