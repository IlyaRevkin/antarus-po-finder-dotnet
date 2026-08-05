using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AntarusPoFinder.Core.Loader;

/// <summary>Находит Automation API Segnetics Loader. Настройка может указывать каталог Loader, GUI exe
/// или непосредственно Automation exe.
///
/// <b>Ненастроенный путь — не ошибка.</b> Раньше «Загрузить в ПЛК» на машине, где путь в настройках не
/// задан (или задан неверно — переставили Loader, сменилась буква диска), заканчивалось красным
/// «Segnetics Loader Automation не найден» и на этом всё: наладчик у шкафа должен был сначала пойти в
/// настройки. Теперь путей ищется НЕСКОЛЬКО (см. <see cref="Candidates"/>) и берётся первый живой —
/// прежде всего встроенная копия, которую ставит наш же установщик рядом с программой. Ошибка остаётся
/// только для случая, когда Loader не нашёлся вообще нигде, и в ней перечислено, где искали.</summary>
public static class SegneticsLoaderResolver
{
    public const string AutomationExeName = "SegneticsLoader.Automation.exe";
    public const string GuiExeName = "SegneticsLoader.exe";
    public const string BundledSubfolder = "Loader";

    /// <summary>Встроенная копия — папка <c>Loader</c> рядом с самой программой (её кладёт MSI, см.
    /// installer/Package.wxs, SegneticsLoaderFeature).</summary>
    public static string DefaultBundledPath =>
        Path.Combine(AppContext.BaseDirectory, BundledSubfolder, AutomationExeName);

    /// <summary>Куда MSI ставит программу: <c>%LOCALAPPDATA%\Programs\AntarusPoFinder</c>. Нужен
    /// портативному exe — запущенный из «Загрузок», рядом с собой встроенного Loader он не имеет, а
    /// установленная копия на машине обычно есть.</summary>
    private static string InstalledAppFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "AntarusPoFinder");

    /// <summary>Все места, где имеет смысл искать Automation, в порядке предпочтения. Пустые/битые
    /// элементы отсеиваются, повторы убираются — список идёт и в поиск, и в текст ошибки.</summary>
    public static IReadOnlyList<string> Candidates(string? configuredPath) =>
        CandidatesFrom(configuredPath, DefaultFallbacks());

    /// <summary>То же самое, но с ЯВНЫМ списком запасных мест. Отдельный вход нужен проверкам: иначе
    /// результат зависел бы от того, стоит ли на конкретной машине Segnetics Loader своим
    /// установщиком, и один и тот же тест был бы зелёным у одного и красным у другого.</summary>
    public static IReadOnlyList<string> CandidatesFrom(string? configuredPath, IEnumerable<string>? fallbacks)
    {
        var list = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!list.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) list.Add(path);
        }

        Add(FromConfigured(configuredPath));
        foreach (var path in fallbacks ?? Array.Empty<string>()) Add(path);
        return list;
    }

    /// <summary>Запасные места в порядке предпочтения: наша встроенная копия рядом с программой,
    /// та же копия у установленной версии (нужна портативному exe из «Загрузок») и, последней
    /// надеждой, обычная установка самого Segnetics Loader своим установщиком.</summary>
    private static IEnumerable<string> DefaultFallbacks()
    {
        yield return DefaultBundledPath;
        yield return Path.Combine(InstalledAppFolder, BundledSubfolder, AutomationExeName);

        foreach (var programs in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(programs)) continue;
            yield return Path.Combine(programs, "Segnetics", "SegneticsLoader", AutomationExeName);
            yield return Path.Combine(programs, "SegneticsLoader", AutomationExeName);
        }
    }

    public static string? Resolve(string? configuredPath) => FirstExisting(Candidates(configuredPath));

    /// <summary>Первый существующий файл из списка. Недоступная сетевая шара в настройке — не повод
    /// падать: просто идём дальше по списку.</summary>
    public static string? FirstExisting(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            try { if (File.Exists(candidate)) return candidate; }
            catch (Exception) { }
        }
        return null;
    }

    /// <summary>Взят ли запасной путь вместо заданного в настройках. Нужен только для строки в журнале
    /// операции: наладчик обязан видеть, что грузит встроенным Loader, а не тем, который прописан.</summary>
    public static bool UsesFallback(string? configuredPath, string resolved) =>
        FromConfigured(configuredPath) is { } wanted &&
        !string.Equals(wanted, resolved, StringComparison.OrdinalIgnoreCase);

    /// <summary>Первый кандидат — то, что человек ИМЕЛ В ВИДУ. Сохранено под прежним именем: на него
    /// опираются тексты ошибок и тесты.</summary>
    public static string? CandidatePath(string? configuredPath) => Candidates(configuredPath).FirstOrDefault();

    /// <summary>Путь, вытекающий ИЗ НАСТРОЙКИ. null — настройка пуста или указывает на что-то, что
    /// Loader-ом быть не может.</summary>
    public static string? FromConfigured(string? configuredPath)
    {
        var configured = configuredPath?.Trim().Trim('"');
        if (string.IsNullOrEmpty(configured)) return null;

        try
        {
            if (Directory.Exists(configured) || string.IsNullOrEmpty(Path.GetExtension(configured)))
                return Path.Combine(configured, AutomationExeName);

            var fileName = Path.GetFileName(configured);
            if (string.Equals(fileName, AutomationExeName, StringComparison.OrdinalIgnoreCase))
                return configured;

            if (string.Equals(fileName, GuiExeName, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(Path.GetDirectoryName(configured) ?? "", AutomationExeName);
        }
        catch (Exception)
        {
            // Мусор в настройке (недопустимые символы в пути) — тот же случай, что «указано не то».
        }

        return null;
    }
}

/// <summary>Клиент UTF-8 JSONL-протокола <c>SegneticsLoader.Automation.exe --stdio</c>.</summary>
public sealed class SegneticsLoaderBackend : IFirmwareLoaderBackend
{
    private const int ProtocolVersion = 1;
    private readonly string _exePath;
    private readonly IReadOnlyList<string> _prefixArguments;

    public SegneticsLoaderBackend(string exePath)
        : this(exePath, Array.Empty<string>())
    {
    }

    internal SegneticsLoaderBackend(string exePath, IReadOnlyList<string> prefixArguments)
    {
        _exePath = exePath;
        _prefixArguments = prefixArguments;
        DisplayVersion = ReadDisplayVersion(exePath);
    }

    public string Name => "Segnetics Loader Automation";

    public string? DisplayVersion { get; }

    public bool IsAvailable => File.Exists(_exePath);

    public string? UnavailableReason => IsAvailable
        ? null
        : $"Segnetics Loader Automation не найден: {_exePath}";

    private static string? ReadDisplayVersion(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            return NormalizeDisplayVersion(info.ProductVersion, info.FileVersion);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string? NormalizeDisplayVersion(string? productVersion, string? fileVersion)
    {
        foreach (var rawVersion in new[] { productVersion, fileVersion })
        {
            var value = rawVersion?.Trim();
            if (string.IsNullOrEmpty(value)) continue;

            var metadataIndex = value.IndexOf('+');
            if (metadataIndex >= 0) value = value[..metadataIndex];
            if (value.StartsWith('v')) value = value[1..];

            if (!Version.TryParse(value, out var version)) continue;
            return version.Build >= 0
                ? $"{version.Major}.{version.Minor}.{version.Build}"
                : $"{version.Major}.{version.Minor}";
        }

        return null;
    }

    public async Task<LoaderResult> RunAsync(
        LoaderRequest request,
        IProgress<LoaderProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsAvailable)
            return LoaderResult.Fail(UnavailableReason ?? "Segnetics Loader Automation недоступен.");

        if (request.Operation is not (LoaderOperation.Deploy or LoaderOperation.Build))
            return LoaderResult.Fail("Searcher поддерживает только загрузку проекта в ПЛК и сборку LFS.");

        if (!File.Exists(request.SourcePath))
            return LoaderResult.Fail($"Файл проекта не найден: {request.SourcePath}");

        var isBuild = request.Operation == LoaderOperation.Build;
        var isPslSource = string.Equals(
            Path.GetExtension(request.SourcePath), LoaderFiles.PslExtension, StringComparison.OrdinalIgnoreCase);
        var operationId = $"finder-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}";
        if (isBuild && !isPslSource)
            return LoaderResult.Fail("Собрать LFS можно только из PSL-проекта.");
        if (!string.IsNullOrWhiteSpace(request.OutputPath) && !isPslSource)
            return LoaderResult.Fail("Сохранение выходного LFS поддерживается только при загрузке PSL-проекта.");

        // Для build поле preparation по контракту Automation должно отсутствовать (или быть "none"):
        // сборка к ПЛК не подключается, форматировать и обновлять ядро там нечему.
        var startRequest = new Dictionary<string, object?>
        {
            ["protocolVersion"] = ProtocolVersion,
            ["operationId"] = operationId,
            ["action"] = isBuild ? "build" : "deploy",
            ["artifactPath"] = request.SourcePath,
        };
        if (!isBuild)
        {
            startRequest["preparation"] = request.Options.FormatAndUpdateFirmware
                ? "formatAndUpdateFirmware"
                : "none";
        }
        if (!string.IsNullOrWhiteSpace(request.OutputPath))
        {
            startRequest["outputPath"] = Path.GetFullPath(request.OutputPath);
            startRequest["overwriteOutput"] = true;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _exePath,
            WorkingDirectory = Path.GetDirectoryName(_exePath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in _prefixArguments) startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("--stdio");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return LoaderResult.Fail("Не удалось запустить Segnetics Loader Automation.");
        }
        catch (Exception ex)
        {
            return LoaderResult.Fail($"Не удалось запустить Segnetics Loader Automation: {ex.Message}");
        }

        process.StandardInput.AutoFlush = true;
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(startRequest)).ConfigureAwait(false);

        using var cancelRegistration = cancellationToken.Register(() =>
        {
            _ = Task.Run(() => SendCancelAsync(process, operationId));
        });

        LoaderResult? terminalResult = null;
        string? cancelledMessage = null;
        var terminalSeen = false;

        while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var document = JsonDocument.Parse(line.TrimStart('\uFEFF'));
                var root = document.RootElement;
                if (!root.TryGetProperty("protocolVersion", out var protocolElement) ||
                    !protocolElement.TryGetInt32(out var eventProtocolVersion) ||
                    eventProtocolVersion != ProtocolVersion)
                {
                    progress.Report(new LoaderProgress(-1, "Протокол",
                        "Версия события Segnetics Loader Automation не поддерживается.",
                        LoaderLogLevel.Warning));
                    continue;
                }

                if (!TryReadString(root, "operationId", out var eventOperationId) ||
                    !string.Equals(eventOperationId, operationId, StringComparison.Ordinal))
                {
                    progress.Report(new LoaderProgress(-1, "Протокол",
                        "Получено событие Automation с другим идентификатором операции.",
                        LoaderLogLevel.Warning));
                    continue;
                }

                if (!TryReadString(root, "event", out var eventType))
                {
                    progress.Report(new LoaderProgress(-1, "Протокол",
                        "Получена строка Automation без типа события.", LoaderLogLevel.Warning));
                    continue;
                }

                switch (eventType)
                {
                    case "started":
                        progress.Report(new LoaderProgress(0, "Запуск",
                            "Segnetics Loader принял запрос."));
                        break;

                    case "plan":
                        progress.Report(new LoaderProgress(0, "План загрузки", DescribePlan(root)));
                        break;

                    case "progress":
                        var percent = root.TryGetProperty("percent", out var percentElement) &&
                                      percentElement.TryGetDouble(out var numericPercent)
                            ? Math.Clamp((int)Math.Round(numericPercent), 0, 100)
                            : -1;
                        var hasProgressMessage = TryReadString(root, "message", out var message);
                        progress.Report(new LoaderProgress(
                            percent,
                            "Выполнение",
                            hasProgressMessage ? message : string.Empty,
                            UpdatesStage: hasProgressMessage));
                        break;

                    case "log":
                        var logMessage = TryReadString(root, "message", out var logged)
                            ? logged
                            : "Segnetics Loader передал пустую строку журнала.";
                        var level = TryReadString(root, "level", out var levelName)
                            ? ParseLogLevel(levelName)
                            : LoaderLogLevel.Info;
                        progress.Report(new LoaderProgress(
                            -1, "Журнал Loader", logMessage, level, UpdatesStage: false));
                        break;

                    case "completed":
                        if (terminalSeen) break;
                        terminalSeen = true;
                        var completedMessage = TryReadString(root, "message", out var completed)
                            ? completed
                            : isBuild ? "LFS собран." : "Проект загружен в ПЛК.";
                        ReportWarnings(root, progress);
                        var artifacts = TryReadString(root, "outputPath", out var outputPath)
                            ? new[] { outputPath }
                            : Array.Empty<string>();
                        terminalResult = LoaderResult.Ok(completedMessage, artifacts);
                        CloseRequestStream(process);
                        break;

                    case "failed":
                        if (terminalSeen) break;
                        terminalSeen = true;
                        terminalResult = ParseFailure(root, progress);
                        CloseRequestStream(process);
                        break;

                    case "cancelled":
                        if (terminalSeen) break;
                        terminalSeen = true;
                        cancelledMessage = TryReadString(root, "message", out var cancelled)
                            ? cancelled
                            : "Операция отменена.";
                        CloseRequestStream(process);
                        break;

                    default:
                        progress.Report(new LoaderProgress(-1, "Протокол",
                            $"Получено неизвестное событие Automation: {eventType}",
                            LoaderLogLevel.Warning));
                        break;
                }
            }
            catch (JsonException ex)
            {
                progress.Report(new LoaderProgress(-1, "Протокол",
                    $"Не удалось разобрать строку Automation: {ex.Message}", LoaderLogLevel.Warning));
            }
        }

        await process.WaitForExitAsync().ConfigureAwait(false);
        var stderr = (await stderrTask.ConfigureAwait(false)).Trim();

        if (cancelledMessage is not null)
            throw new OperationCanceledException(cancelledMessage);

        if (terminalResult is null)
        {
            if (stderr.Contains("You must install or update .NET", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase))
            {
                return LoaderResult.Fail(
                    "Не удалось запустить Segnetics Loader Automation: установите Microsoft .NET 8 Runtime x64.");
            }

            var details = string.IsNullOrEmpty(stderr) ? "" : $"\n{stderr}";
            return LoaderResult.Fail(
                $"Segnetics Loader Automation завершился без итогового события. Код завершения: {process.ExitCode}.{details}");
        }

        if (terminalResult.Success && process.ExitCode != 0)
            return LoaderResult.Fail(
                $"Segnetics Loader сообщил об успешной загрузке, но завершился с кодом {process.ExitCode}.");

        return terminalResult;
    }

    private static void CloseRequestStream(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception)
        {
            // Процесс мог закрыть stdin одновременно с отправкой терминального события.
        }
    }

    private static async Task SendCancelAsync(Process process, string operationId)
    {
        try
        {
            if (process.HasExited) return;
            var request = JsonSerializer.Serialize(new
            {
                protocolVersion = ProtocolVersion,
                operationId,
                action = "cancel",
            });
            await process.StandardInput.WriteLineAsync(request).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Завершившийся процесс закрывает stdin раньше, чем callback отмены успевает записать строку.
        }
    }

    private static LoaderResult ParseFailure(JsonElement root, IProgress<LoaderProgress> progress)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return LoaderResult.Fail("Segnetics Loader завершил операцию с ошибкой без описания.");

        var message = TryReadString(error, "message", out var userMessage)
            ? userMessage
            : "Segnetics Loader завершил операцию с ошибкой.";
        if (TryReadString(error, "logDirectory", out var logDirectory))
            progress.Report(new LoaderProgress(-1, "Диагностика",
                $"Диагностические файлы Loader: {logDirectory}", LoaderLogLevel.Info));
        return LoaderResult.Fail(message);
    }

    private static void ReportWarnings(JsonElement root, IProgress<LoaderProgress> progress)
    {
        if (!root.TryGetProperty("warnings", out var warnings) || warnings.ValueKind != JsonValueKind.Array)
            return;

        foreach (var warning in warnings.EnumerateArray())
            if (warning.ValueKind == JsonValueKind.String && warning.GetString() is { Length: > 0 } text)
                progress.Report(new LoaderProgress(-1, "Предупреждение", text, LoaderLogLevel.Warning));
    }

    private static string DescribePlan(JsonElement root)
    {
        var artifact = TryReadString(root, "artifactType", out var artifactType)
            ? artifactType.ToUpperInvariant()
            : "неизвестный формат";
        var steps = root.TryGetProperty("steps", out var stepsElement) && stepsElement.ValueKind == JsonValueKind.Array
            ? stepsElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => DescribeStep(item.GetString() ?? ""))
                .ToArray()
            : Array.Empty<string>();
        return steps.Length == 0
            ? $"Тип файла: {artifact}."
            : $"Тип файла: {artifact}. Этапы: {string.Join(", ", steps)}.";
    }

    private static string DescribeStep(string step) => step switch
    {
        "formatAndUpdateFirmware" => "форматирование проекта и обновление ядра",
        "buildPslToLfs" => "сборка PSL в LFS",
        "extractZop" => "распаковка ZOP",
        "deployLfs" => "загрузка LFS",
        "waitForProjectReady" => "ожидание запуска проекта",
        "deployElf" => "загрузка ELF",
        _ => step,
    };

    private static LoaderLogLevel ParseLogLevel(string level) => level switch
    {
        "warning" => LoaderLogLevel.Warning,
        "error" => LoaderLogLevel.Error,
        _ => LoaderLogLevel.Info,
    };

    private static bool TryReadString(JsonElement element, string propertyName, out string value)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? "";
            return true;
        }

        value = "";
        return false;
    }
}
