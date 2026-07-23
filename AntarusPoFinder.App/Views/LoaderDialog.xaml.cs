using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.Core.Loader;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>
/// Диалог загрузки прошивки в контроллер через лоадер. Сам лоадер сюда НЕ зашит: диалог знает
/// только контракт <see cref="IFirmwareLoaderBackend"/>, а конкретную реализацию отдаёт
/// <see cref="FirmwareLoaderFactory"/> — сейчас это заготовка (<see cref="StubFirmwareLoaderBackend"/>).
/// Всё, что вокруг (параметры, прогресс-бар, лог, локальная рабочая область, публикация результата
/// на диск), уже работает по-настоящему — коллеге останется подставить свой backend.
/// <para>Важно: любая работа идёт в локальной рабочей области (<see cref="LoaderWorkspace"/>) —
/// исходник сначала копируется на локальную машину, и только успешный результат сборки уезжает
/// обратно на сетевой диск. Приложение не клиент-серверное, лоадер не должен работать по сети.</para>
/// </summary>
public partial class LoaderDialog : Window
{
    private readonly ConfigService _cfg;
    private readonly IFirmwareLoaderBackend _backend;
    private readonly LoaderOperation _operation;
    private readonly string _versionName;
    private readonly string _publishDir;

    private CancellationTokenSource? _cts;
    private LoaderWorkspace? _workspace;
    private readonly List<string> _logLines = new();

    /// <summary>Сколько держать рабочие области прошлых запусков — лог и промежуточные файлы иногда
    /// нужны постфактум, но копиться бесконечно они не должны.</summary>
    private static readonly TimeSpan WorkspaceRetention = TimeSpan.FromDays(7);

    public LoaderDialog(ConfigService cfg, LoaderOperation operation, string versionName, string sourcePath,
        string publishDir = "")
    {
        InitializeComponent();
        _cfg = cfg;
        _operation = operation;
        _versionName = versionName;
        _publishDir = publishDir;
        _backend = FirmwareLoaderFactory.Create(cfg.LoaderExePath());

        Title = operation == LoaderOperation.Build ? "Сборка через лоадер" : "Загрузка в контроллер";
        HeaderLabel.Text = operation == LoaderOperation.Build
            ? $"Сборка проекта: {versionName}"
            : $"Загрузка в контроллер: {versionName}";
        BackendLabel.Text = $"Лоадер: {_backend.Name}";

        StubBanner.Visibility = _backend.IsAvailable ? Visibility.Collapsed : Visibility.Visible;
        StubReasonLabel.Text = _backend.UnavailableReason ?? "";

        SourceInput.Text = sourcePath;
        TargetInput.Text = cfg.LoaderLastTarget();
        FormatCheck.IsChecked = cfg.LoaderFormatDefault();
        UpdateKernelCheck.IsChecked = cfg.LoaderUpdateKernelDefault();

        AppendLog(operation == LoaderOperation.Build
            ? "Сборка выполняется на этом компьютере; на диск попадёт только готовый результат."
            : "Файл сначала копируется на этот компьютер, загрузка идёт с локальной копии.");
        if (!string.IsNullOrEmpty(publishDir))
            AppendLog($"Папка публикации результата: {publishDir}");

        // Старые рабочие области — фоном, чтобы медленный диск не тормозил открытие диалога.
        Task.Run(() =>
        {
            try { LoaderWorkspace.CleanupOlderThan(ConfigService.LocalLoader, WorkspaceRetention); }
            catch (Exception) { /* уборка — не повод показывать оператору ошибку */ }
        });
    }

    /// <summary>Открывает диалог заливки готовой прошивки в контроллер.</summary>
    public static void ShowFlash(Window? owner, ConfigService cfg, string versionName, string sourcePath)
        => new LoaderDialog(cfg, LoaderOperation.Flash, versionName, sourcePath) { Owner = owner }.ShowDialog();

    // ── Запуск ────────────────────────────────────────────────────────────

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        var source = SourceInput.Text.Trim();
        if (string.IsNullOrEmpty(source))
        {
            AppMessageBox.Show("Укажите файл для загрузки.", "Лоадер", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!File.Exists(source) && !Directory.Exists(source))
        {
            AppMessageBox.Show($"Файл или папка не найдены:\n{source}", "Лоадер", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var options = new LoaderOptions
        {
            Format = FormatCheck.IsChecked == true,
            UpdateKernel = UpdateKernelCheck.IsChecked == true,
            Target = TargetInput.Text.Trim(),
        };
        _cfg.SetLoaderFormatDefault(options.Format);
        _cfg.SetLoaderUpdateKernelDefault(options.UpdateKernel);
        _cfg.SetLoaderLastTarget(options.Target);

        SetRunning(true);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = new Progress<LoaderProgress>(OnProgress);

        try
        {
            _workspace = LoaderWorkspace.Create(ConfigService.LocalLoader, _versionName);
            OpenWorkspaceBtn.IsEnabled = true;
            AppendLog($"Рабочая область: {_workspace.Dir}");

            var localSource = await Task.Run(() => _workspace.Import(source), ct);
            AppendLog($"Локальная копия готова: {localSource}", LoaderLogLevel.Success);

            var request = new LoaderRequest
            {
                Operation = _operation,
                SourcePath = localSource,
                WorkspaceDir = _workspace.Dir,
                PublishDir = _publishDir,
                VersionName = _versionName,
                Options = options,
            };

            var result = await _backend.RunAsync(request, progress, ct);
            AppendLog(result.Message, result.Success ? LoaderLogLevel.Success : LoaderLogLevel.Error);

            if (result.Success) await PublishIfNeeded(ct);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Остановлено оператором.", LoaderLogLevel.Warning);
            StageLabel.Text = "Остановлено";
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message, LoaderLogLevel.Error);
            StageLabel.Text = "Ошибка";
        }
        finally
        {
            SaveLogToWorkspace();
            SetRunning(false);
        }
    }

    /// <summary>Результат сборки кладётся на диск ТОЛЬКО после успешного локального прогона — и
    /// только настоящим лоадером: публиковать вывод заготовки в папку версии на сетевом диске
    /// нельзя, иначе рядом с реальной прошивкой появится файл, который никто не собирал.</summary>
    private async Task PublishIfNeeded(CancellationToken ct)
    {
        if (_operation != LoaderOperation.Build || string.IsNullOrEmpty(_publishDir) || _workspace is null) return;

        if (!_backend.IsAvailable)
        {
            AppendLog("Публикация на диск пропущена: работала заготовка, публиковать нечего.", LoaderLogLevel.Warning);
            return;
        }

        var published = await Task.Run(() => _workspace.Publish(_publishDir), ct);
        if (published.Count == 0)
        {
            AppendLog("Лоадер не вернул ни одного файла — публиковать нечего.", LoaderLogLevel.Warning);
            return;
        }
        foreach (var path in published) AppendLog($"На диск: {path}", LoaderLogLevel.Success);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopBtn.IsEnabled = false;
        StageLabel.Text = "Останавливаем…";
    }

    private void OnProgress(LoaderProgress p)
    {
        if (p.Percent >= 0)
        {
            Progress.IsIndeterminate = false;
            Progress.Value = Math.Clamp(p.Percent, 0, 100);
            PercentLabel.Text = $"{Progress.Value:0}%";
        }
        else
        {
            Progress.IsIndeterminate = true;
            PercentLabel.Text = "";
        }
        StageLabel.Text = p.Stage;
        AppendLog($"{p.Stage}: {p.Message}", p.Level);
    }

    private void SetRunning(bool running)
    {
        RunBtn.IsEnabled = !running;
        StopBtn.IsEnabled = running;
        BrowseSourceBtn.IsEnabled = !running;
        SourceInput.IsEnabled = !running;
        TargetInput.IsEnabled = !running;
        FormatCheck.IsEnabled = !running;
        UpdateKernelCheck.IsEnabled = !running;
        SaveLogBtn.IsEnabled = _logLines.Count > 0;
        if (running)
        {
            Progress.Value = 0;
            PercentLabel.Text = "0%";
            StageLabel.Text = "Запуск…";
        }
    }

    // ── Лог ───────────────────────────────────────────────────────────────

    private void AppendLog(string message, LoaderLogLevel level = LoaderLogLevel.Info)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        _logLines.Add(line);

        var block = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };
        block.SetResourceReference(ForegroundProperty, level switch
        {
            LoaderLogLevel.Success => "SuccessBrush",
            LoaderLogLevel.Warning => "WarningBrush",
            LoaderLogLevel.Error => "ErrorBrush",
            _ => "TextBrush",
        });
        LogPanel.Children.Add(block);
        LogScroll.ScrollToEnd();
        SaveLogBtn.IsEnabled = true;
    }

    /// <summary>Лог кладётся в рабочую область автоматически — оператору не нужно помнить про
    /// кнопку «Сохранить лог», чтобы потом было что показать при разборе неудачной загрузки.</summary>
    private void SaveLogToWorkspace()
    {
        if (_workspace is null || _logLines.Count == 0) return;
        try { File.WriteAllLines(_workspace.LogPath, _logLines); }
        catch (Exception ex) { AppendLog($"Не удалось сохранить лог в рабочую область: {ex.Message}", LoaderLogLevel.Warning); }
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить лог загрузки",
            Filter = "Текстовый файл (*.txt)|*.txt|Все файлы (*.*)|*.*",
            FileName = $"loader_{LoaderFileStem()}.txt",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllLines(dlg.FileName, _logLines);
        }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось сохранить файл:\n{ex.Message}", "Лоадер", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string LoaderFileStem()
    {
        var stem = string.Join("_", _versionName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrEmpty(stem) ? DateTime.Now.ToString("yyyyMMdd_HHmmss") : stem;
    }

    // ── Прочие кнопки ─────────────────────────────────────────────────────

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите файл для загрузки",
            Filter = "Файлы Segnetics (*.lfs;*.psl)|*.lfs;*.psl|Все файлы (*.*)|*.*",
        };
        var current = SourceInput.Text.Trim();
        if (!string.IsNullOrEmpty(current))
        {
            var dir = Directory.Exists(current) ? current : Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        }
        if (dlg.ShowDialog() == true) SourceInput.Text = dlg.FileName;
    }

    private void OpenWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || !Directory.Exists(_workspace.Dir)) return;
        try { Process.Start(new ProcessStartInfo(_workspace.Dir) { UseShellExecute = true }); }
        catch (Exception ex) { AppendLog($"Не удалось открыть папку: {ex.Message}", LoaderLogLevel.Warning); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Закрытие окна посреди запуска не должно оставлять фоновую работу без хозяина.
        _cts?.Cancel();
        base.OnClosing(e);
    }
}
