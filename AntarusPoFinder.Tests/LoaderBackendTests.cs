using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Loader;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

public class LoaderBackendTests
{
    private sealed class CollectingProgress : IProgress<LoaderProgress>
    {
        public List<LoaderProgress> Reports { get; } = new();
        public void Report(LoaderProgress value) => Reports.Add(value);
    }

    [Fact]
    public void Resolver_AcceptsAutomationGuiAndDirectoryPaths()
    {
        using var root = new TempRoot();
        var automation = Path.Combine(root.Path, SegneticsLoaderResolver.AutomationExeName);
        var gui = Path.Combine(root.Path, SegneticsLoaderResolver.GuiExeName);
        File.WriteAllText(automation, "MZ");
        File.WriteAllText(gui, "MZ");

        Assert.Equal(automation, SegneticsLoaderResolver.Resolve(automation));
        Assert.Equal(automation, SegneticsLoaderResolver.Resolve(gui));
        Assert.Equal(automation, SegneticsLoaderResolver.Resolve(root.Path));
    }

    /// <summary>Мусор в настройке (указан не тот exe) — не приговор: путь из настройки просто не
    /// попадает в список, и работа идёт запасной копией. Раньше здесь была ошибка «не найден», и
    /// наладчик у шкафа сначала шёл в настройки — ровно та жалоба, из-за которой поиск и
    /// расширили.</summary>
    [Fact]
    public void Resolver_IgnoresUnusableConfiguredPath_AndFallsBackToBundledCopy()
    {
        using var root = new TempRoot();
        var unsupported = Path.Combine(root.Path, "another.exe");
        var bundled = Path.Combine(root.Path, "Loader", SegneticsLoaderResolver.AutomationExeName);
        File.WriteAllText(unsupported, "MZ");
        Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
        File.WriteAllText(bundled, "MZ");

        // Настройка отброшена целиком: подставлять «another.exe» вместо Loader нельзя.
        Assert.Null(SegneticsLoaderResolver.FromConfigured(unsupported));
        var candidates = SegneticsLoaderResolver.CandidatesFrom(unsupported, new[] { bundled });
        Assert.Equal(new[] { bundled }, candidates.ToArray());
        Assert.Equal(bundled, SegneticsLoaderResolver.FirstExisting(candidates));
    }

    /// <summary>Путь настройки идёт ПЕРВЫМ — запасные копии не должны перебивать сознательный выбор
    /// администратора, пока он рабочий.</summary>
    [Fact]
    public void Resolver_PrefersConfiguredPath_OverBundledCopy()
    {
        using var root = new TempRoot();
        var configuredDir = Path.Combine(root.Path, "свой-loader");
        var configured = Path.Combine(configuredDir, SegneticsLoaderResolver.AutomationExeName);
        var bundled = Path.Combine(root.Path, "Loader", SegneticsLoaderResolver.AutomationExeName);
        Directory.CreateDirectory(configuredDir);
        Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
        File.WriteAllText(configured, "MZ");
        File.WriteAllText(bundled, "MZ");

        var candidates = SegneticsLoaderResolver.CandidatesFrom(configuredDir, new[] { bundled });

        Assert.Equal(new[] { configured, bundled }, candidates.ToArray());
        Assert.Equal(configured, SegneticsLoaderResolver.FirstExisting(candidates));
        Assert.False(SegneticsLoaderResolver.UsesFallback(configuredDir, configured));
    }

    /// <summary>Настроенной папки на машине больше нет (переставили Loader, сменилась буква диска) —
    /// берётся встроенная копия, и вызывающий может об этом сказать в журнале операции.</summary>
    [Fact]
    public void Resolver_MissingConfiguredDirectory_UsesFallback_AndSaysSo()
    {
        using var root = new TempRoot();
        var missingDirectory = Path.Combine(root.Path, "moved-loader");
        var bundled = Path.Combine(root.Path, "Loader", SegneticsLoaderResolver.AutomationExeName);
        Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
        File.WriteAllText(bundled, "MZ");

        // То, что человек имел в виду, по-прежнему первое в списке — на нём строится текст ошибки.
        var wanted = Path.Combine(missingDirectory, SegneticsLoaderResolver.AutomationExeName);
        var candidates = SegneticsLoaderResolver.CandidatesFrom(missingDirectory, new[] { bundled });
        Assert.Equal(new[] { wanted, bundled }, candidates.ToArray());

        var resolved = SegneticsLoaderResolver.FirstExisting(candidates);
        Assert.Equal(bundled, resolved);
        Assert.True(SegneticsLoaderResolver.UsesFallback(missingDirectory, resolved!));
    }

    /// <summary>Пустая настройка — это НЕ ошибка: первым же кандидатом идёт встроенная копия рядом с
    /// программой, и именно её берёт «Загрузить в ПЛК» на машине, где путь никто не задавал.</summary>
    [Fact]
    public void Resolver_EmptyConfiguration_LooksForBundledCopyFirst()
    {
        Assert.Null(SegneticsLoaderResolver.FromConfigured(""));
        Assert.Null(SegneticsLoaderResolver.FromConfigured(null));

        Assert.Equal(SegneticsLoaderResolver.DefaultBundledPath, SegneticsLoaderResolver.CandidatePath(null));
        Assert.Equal(SegneticsLoaderResolver.DefaultBundledPath, SegneticsLoaderResolver.CandidatePath("   "));

        // Установленная копия и обычная установка Loader — тоже в списке, после встроенной.
        var all = SegneticsLoaderResolver.Candidates(null);
        Assert.Equal(SegneticsLoaderResolver.DefaultBundledPath, all[0]);
        Assert.True(all.Count > 1, "запасные пути потерялись — «не найден» вернётся к наладчику");
        Assert.All(all, p => Assert.EndsWith(SegneticsLoaderResolver.AutomationExeName, p));
    }

    [Fact]
    public void Factory_ReturnsUnavailableBackendWithoutStubFallback()
    {
        using var root = new TempRoot();
        var missing = Path.Combine(root.Path, SegneticsLoaderResolver.AutomationExeName);

        // На машине, где Segnetics Loader реально установлен, проверять нечего: фабрика обязана
        // вернуть рабочий backend, а не ошибку (в этом и смысл запасных путей).
        if (SegneticsLoaderResolver.Resolve(missing) is not null)
        {
            Assert.True(FirmwareLoaderFactory.Create(missing).IsAvailable);
            return;
        }

        var backend = FirmwareLoaderFactory.Create(missing);

        Assert.False(backend.IsAvailable);
        Assert.Equal("Segnetics Loader Automation", backend.Name);
        Assert.Null(backend.DisplayVersion);
        // В тексте ошибки перечислено, ГДЕ искали: «не найден» без единого пути наладчику ничего
        // не объясняет.
        Assert.Contains("не найден", backend.UnavailableReason!);
        Assert.Contains(missing, backend.UnavailableReason!);
        Assert.Contains(SegneticsLoaderResolver.DefaultBundledPath, backend.UnavailableReason!);
    }

    [Theory]
    [InlineData("2.8.3+aa5cb8943c391a7e4a1520ec3ca237b5bc8fe8a5", "2.8.3.0", "2.8.3")]
    [InlineData(null, "2.8.3.0", "2.8.3")]
    [InlineData("v2.9", null, "2.9")]
    [InlineData("invalid", "invalid", null)]
    public void AutomationBackend_NormalizesExecutableVersion(
        string? productVersion,
        string? fileVersion,
        string? expected)
    {
        Assert.Equal(expected, SegneticsLoaderBackend.NormalizeDisplayVersion(productVersion, fileVersion));
    }

    [Fact]
    public async Task AutomationBackend_TranslatesJsonlEventsAndCompletion()
    {
        using var root = new TempRoot();
        var source = Path.Combine(root.Path, "project.lfs");
        File.WriteAllText(source, "payload");
        var script = WriteScript(root.Path, """
            $request = ([Console]::In.ReadLine() | ConvertFrom-Json)
            $id = $request.operationId
            if ($request.action -ne 'deploy' -or $request.preparation -ne 'formatAndUpdateFirmware') { exit 9 }
            function Emit($value) {
                [Console]::Out.WriteLine(($value | ConvertTo-Json -Compress -Depth 8))
                [Console]::Out.Flush()
            }
            Emit ([ordered]@{ protocolVersion = 1; operationId = $id; event = 'started' })
            Emit ([ordered]@{ protocolVersion = 1; operationId = $id; event = 'plan'; artifactPath = $request.artifactPath; artifactType = 'lfs'; steps = @('deployLfs') })
            Emit ([ordered]@{ protocolVersion = 1; operationId = $id; event = 'progress'; percent = 55; message = 'Uploading' })
            Emit ([ordered]@{ protocolVersion = 1; operationId = $id; event = 'progress'; percent = 56 })
            Emit ([ordered]@{ protocolVersion = 1; operationId = $id; event = 'log'; level = 'warning'; message = 'Network warning' })
            Emit ([ordered]@{ protocolVersion = 1; operationId = $id; event = 'completed'; message = 'Completed'; outputPath = $request.artifactPath; warnings = @('Result warning') })
            $null = [Console]::In.ReadToEnd()
            exit 0
            """);
        var backend = CreatePowerShellBackend(script);
        var progress = new CollectingProgress();

        var result = await backend.RunAsync(Request(source, formatAndUpdate: true), progress, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Completed", result.Message);
        Assert.Equal(source, Assert.Single(result.Artifacts));
        Assert.Contains(progress.Reports,
            item => item.Percent == 55 && item.Message == "Uploading" && item.UpdatesStage);
        Assert.Contains(progress.Reports,
            item => item.Percent == 56 && item.Message == string.Empty && !item.UpdatesStage);
        Assert.Contains(progress.Reports,
            item => item.Level == LoaderLogLevel.Warning && item.Message == "Network warning" && !item.UpdatesStage);
        Assert.Contains(progress.Reports, item => item.Level == LoaderLogLevel.Warning && item.Message == "Result warning");
    }

    [Fact]
    public async Task AutomationBackend_SendsOutputPathForPslDeploy()
    {
        using var root = new TempRoot();
        var source = Path.Combine(root.Path, "project.psl");
        var outputPath = Path.Combine(root.Path, "out", "project.lfs");
        File.WriteAllText(source, "project");
        var script = WriteScript(root.Path, """
            $request = ([Console]::In.ReadLine() | ConvertFrom-Json)
            if ($request.action -ne 'deploy' -or
                $request.outputPath -eq $null -or
                $request.overwriteOutput -ne $true) { exit 9 }
            $event = [ordered]@{
                protocolVersion = 1
                operationId = $request.operationId
                event = 'completed'
                message = 'Completed'
                outputPath = $request.outputPath
            }
            [Console]::Out.WriteLine(($event | ConvertTo-Json -Compress -Depth 8))
            [Console]::Out.Flush()
            $null = [Console]::In.ReadToEnd()
            exit 0
            """);
        var backend = CreatePowerShellBackend(script);

        var result = await backend.RunAsync(
            Request(source, outputPath: outputPath),
            new CollectingProgress(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(Path.GetFullPath(outputPath), Assert.Single(result.Artifacts));
    }

    [Fact]
    public async Task AutomationBackend_RejectsOutputPathForNonPslDeploy()
    {
        using var root = new TempRoot();
        var source = Path.Combine(root.Path, "project.lfs");
        File.WriteAllText(source, "payload");
        var script = WriteScript(root.Path, "exit 9");
        var backend = CreatePowerShellBackend(script);

        var result = await backend.RunAsync(
            Request(source, outputPath: Path.Combine(root.Path, "out", "project.lfs")),
            new CollectingProgress(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("только при загрузке PSL", result.Message);
    }

    [Fact]
    public async Task AutomationBackend_ReturnsUserFailureAndDiagnosticLocation()
    {
        using var root = new TempRoot();
        var source = Path.Combine(root.Path, "project.lfs");
        File.WriteAllText(source, "payload");
        var script = WriteScript(root.Path, """
            $request = ([Console]::In.ReadLine() | ConvertFrom-Json)
            $event = [ordered]@{
                protocolVersion = 1
                operationId = $request.operationId
                event = 'failed'
                error = [ordered]@{
                    code = 'DEPLOY_FAILED'
                    message = 'Deploy failed'
                    details = 'Technical details'
                    logDirectory = 'C:\logs\operation'
                }
            }
            [Console]::Out.WriteLine(($event | ConvertTo-Json -Compress -Depth 8))
            [Console]::Out.Flush()
            $null = [Console]::In.ReadToEnd()
            exit 1
            """);
        var backend = CreatePowerShellBackend(script);
        var progress = new CollectingProgress();

        var result = await backend.RunAsync(Request(source), progress, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Deploy failed", result.Message);
        Assert.DoesNotContain(progress.Reports, item => item.Message.Contains("DEPLOY_FAILED"));
        Assert.DoesNotContain(progress.Reports, item => item.Message == "Technical details");
        Assert.Contains(progress.Reports, item => item.Message.Contains(@"C:\logs\operation"));
    }

    [Fact]
    public async Task AutomationBackend_SendsCancelCommandToSameProcess()
    {
        using var root = new TempRoot();
        var source = Path.Combine(root.Path, "project.lfs");
        File.WriteAllText(source, "payload");
        var script = WriteScript(root.Path, """
            $request = ([Console]::In.ReadLine() | ConvertFrom-Json)
            $started = [ordered]@{ protocolVersion = 1; operationId = $request.operationId; event = 'started' }
            [Console]::Out.WriteLine(($started | ConvertTo-Json -Compress))
            [Console]::Out.Flush()
            $cancel = ([Console]::In.ReadLine() | ConvertFrom-Json)
            if ($cancel.action -eq 'cancel' -and $cancel.operationId -eq $request.operationId) {
                $event = [ordered]@{ protocolVersion = 1; operationId = $request.operationId; event = 'cancelled'; message = 'Cancelled by client' }
                [Console]::Out.WriteLine(($event | ConvertTo-Json -Compress))
                [Console]::Out.Flush()
                exit 2
            }
            exit 1
            """);
        var backend = CreatePowerShellBackend(script);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            backend.RunAsync(Request(source), new CollectingProgress(), cancellation.Token));

        Assert.Equal("Cancelled by client", exception.Message);
    }

    [Fact]
    public void LoaderFiles_PrefersTopLevelOverNested()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "Архив"));
        File.WriteAllText(Path.Combine(root.Path, "Архив", "старая.lfs"), "old");
        File.WriteAllText(Path.Combine(root.Path, "новая.lfs"), "new");

        Assert.Equal(Path.Combine(root.Path, "новая.lfs"), LoaderFiles.FindLfs(root.Path));
    }

    [Fact]
    public void LoaderFiles_FindsNestedWhenNothingOnTop()
    {
        using var root = new TempRoot();
        var nested = Path.Combine(root.Path, "Проект");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "proj.psl"), "psl");

        Assert.Equal(Path.Combine(nested, "proj.psl"), LoaderFiles.FindPsl(root.Path));
        Assert.Null(LoaderFiles.FindLfs(root.Path));
    }

    [Fact]
    public void LoaderFiles_MissingFolder_IsJustNotFound()
    {
        using var root = new TempRoot();

        Assert.Null(LoaderFiles.FindLfs(Path.Combine(root.Path, "нет-такой-папки")));
        Assert.Null(LoaderFiles.FindLfs(""));
    }

    [Fact]
    public void LoaderFiles_FindDeploymentFiles_UsesFolderPriorityForEachType()
    {
        using var root = new TempRoot();
        var local = Path.Combine(root.Path, "local");
        var disk = Path.Combine(root.Path, "disk");
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(disk);
        File.WriteAllText(Path.Combine(local, "project.lfs"), "local lfs");
        File.WriteAllText(Path.Combine(disk, "project.lfs"), "disk lfs");
        File.WriteAllText(Path.Combine(disk, "project.psl"), "disk psl");

        var files = LoaderFiles.FindDeploymentFiles(new[] { local, disk });

        Assert.Equal(Path.Combine(local, "project.lfs"), files.LfsPath);
        Assert.Equal(Path.Combine(disk, "project.psl"), files.PslPath);
    }

    [Fact]
    public void LoaderFiles_FindDeploymentFiles_HonoursOperatorChoicePerType()
    {
        // Пачка прошивок в одной папке версии: оператор выбрал в модерации конкретный .lfs — заливка
        // обязана взять именно его. Подсказка на .psl при этом не должна подменять поиск .lfs.
        using var root = new TempRoot();
        File.WriteAllText(Path.Combine(root.Path, "шкаф_1.lfs"), "a");
        File.WriteAllText(Path.Combine(root.Path, "шкаф_2.lfs"), "b");
        File.WriteAllText(Path.Combine(root.Path, "шкаф_1.psl"), "c");
        File.WriteAllText(Path.Combine(root.Path, "шкаф_2.psl"), "d");

        var chosenLfs = LoaderFiles.FindDeploymentFiles(new[] { root.Path }, "шкаф_2.lfs");
        Assert.Equal(Path.Combine(root.Path, "шкаф_2.lfs"), chosenLfs.LfsPath);
        Assert.Equal(Path.Combine(root.Path, "шкаф_1.psl"), chosenLfs.PslPath);

        var chosenPsl = LoaderFiles.FindDeploymentFiles(new[] { root.Path }, "шкаф_2.psl");
        Assert.Equal(Path.Combine(root.Path, "шкаф_1.lfs"), chosenPsl.LfsPath);
        Assert.Equal(Path.Combine(root.Path, "шкаф_2.psl"), chosenPsl.PslPath);
    }

    [Fact]
    public void LoaderFiles_LfsNameFor_KeepsProjectName()
    {
        Assert.Equal("проект.lfs", LoaderFiles.LfsNameFor(@"C:\версия\проект.psl"));
    }

    [Fact]
    public async Task AutomationBackend_BuildSendsBuildActionWithoutPreparation()
    {
        // Сборка .psl → .lfs идёт действием build: к ПЛК не подключается, поле preparation по
        // контракту Automation в запросе отсутствовать обязано.
        using var root = new TempRoot();
        var source = Path.Combine(root.Path, "project.psl");
        var outputPath = Path.Combine(root.Path, "out", "project.lfs");
        File.WriteAllText(source, "project");
        var script = WriteScript(root.Path, """
            $request = ([Console]::In.ReadLine() | ConvertFrom-Json)
            if ($request.action -ne 'build' -or
                $request.PSObject.Properties.Name -contains 'preparation' -or
                $request.outputPath -eq $null) { exit 9 }
            $event = [ordered]@{
                protocolVersion = 1
                operationId = $request.operationId
                event = 'completed'
                outputPath = $request.outputPath
            }
            [Console]::Out.WriteLine(($event | ConvertTo-Json -Compress -Depth 8))
            [Console]::Out.Flush()
            $null = [Console]::In.ReadToEnd()
            exit 0
            """);
        var backend = CreatePowerShellBackend(script);

        var result = await backend.RunAsync(
            Request(source, outputPath: outputPath, operation: LoaderOperation.Build),
            new CollectingProgress(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("LFS собран.", result.Message);
        Assert.Equal(Path.GetFullPath(outputPath), Assert.Single(result.Artifacts));
    }

    [Fact]
    public async Task AutomationBackend_RefusesToBuildFromLfs()
    {
        using var root = new TempRoot();
        var source = Path.Combine(root.Path, "project.lfs");
        File.WriteAllText(source, "payload");
        var backend = CreatePowerShellBackend(WriteScript(root.Path, "exit 9"));

        var result = await backend.RunAsync(
            Request(source, operation: LoaderOperation.Build),
            new CollectingProgress(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("только из PSL", result.Message);
    }

    private static LoaderRequest Request(
        string source,
        bool formatAndUpdate = false,
        string? outputPath = null,
        LoaderOperation operation = LoaderOperation.Deploy) => new()
    {
        Operation = operation,
        SourcePath = source,
        WorkspaceDir = Path.GetDirectoryName(source)!,
        OutputPath = outputPath,
        VersionName = "2.1.042",
        Options = new LoaderOptions { FormatAndUpdateFirmware = formatAndUpdate },
    };

    private static SegneticsLoaderBackend CreatePowerShellBackend(string scriptPath)
    {
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.True(File.Exists(executable), $"Windows PowerShell not found: {executable}");
        return new SegneticsLoaderBackend(executable,
            new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath });
    }

    private static string WriteScript(string directory, string content)
    {
        var path = Path.Combine(directory, $"fake-loader-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ResolvePreferHint_UsesOperatorChosenLfs_NotFirstFound()
    {
        // Несколько прошивок в одной папке (пачка пожарных шкафов) — оператор указал в модерации
        // нужный файл; заливка обязана взять именно его, а не первый попавшийся.
        using var root = new TempRoot();
        File.WriteAllText(Path.Combine(root.Path, "шкаф_1.lfs"), "a");
        File.WriteAllText(Path.Combine(root.Path, "шкаф_2.lfs"), "b");

        Assert.Equal(Path.Combine(root.Path, "шкаф_2.lfs"),
            LoaderFiles.ResolvePreferHint(new[] { root.Path }, "шкаф_2.lfs",
                LoaderFiles.LfsExtension, LoaderFiles.PslExtension));
    }

    [Fact]
    public void ResolvePreferHint_IgnoresHintOfWrongExtension()
    {
        // «Открыть LFS» ждёт именно .lfs — подсказка на .psl не должна подставляться, берём первый .lfs.
        using var root = new TempRoot();
        File.WriteAllText(Path.Combine(root.Path, "proj.psl"), "psl");
        File.WriteAllText(Path.Combine(root.Path, "prog.lfs"), "lfs");

        Assert.Equal(Path.Combine(root.Path, "prog.lfs"),
            LoaderFiles.ResolvePreferHint(new[] { root.Path }, "proj.psl", LoaderFiles.LfsExtension));
    }

    [Fact]
    public void ResolvePreferHint_NoHint_FallsBackToLfsThenPsl()
    {
        // Без подсказки — прежнее поведение: сначала .lfs, а нет — .psl (лоадер соберёт сам).
        using var root = new TempRoot();
        File.WriteAllText(Path.Combine(root.Path, "proj.psl"), "psl");

        Assert.Equal(Path.Combine(root.Path, "proj.psl"),
            LoaderFiles.ResolvePreferHint(new[] { root.Path }, "",
                LoaderFiles.LfsExtension, LoaderFiles.PslExtension));

        File.WriteAllText(Path.Combine(root.Path, "prog.lfs"), "lfs");
        Assert.Equal(Path.Combine(root.Path, "prog.lfs"),
            LoaderFiles.ResolvePreferHint(new[] { root.Path }, "",
                LoaderFiles.LfsExtension, LoaderFiles.PslExtension));
    }

    [Fact]
    public void FindDeploymentFiles_UsesOperatorChosenLfs_NotFirstFound()
    {
        // Пачка прошивок пожарных шкафов в одной папке версии: заливается ровно тот .lfs, который
        // оператор указал в модерации. Порядок папок тут ни при чём — выбор оператора главнее.
        using var root = new TempRoot();
        File.WriteAllText(Path.Combine(root.Path, "шкаф_1.lfs"), "a");
        File.WriteAllText(Path.Combine(root.Path, "шкаф_2.lfs"), "b");

        var files = LoaderFiles.FindDeploymentFiles(new[] { root.Path }, "шкаф_2.lfs");

        Assert.Equal(Path.Combine(root.Path, "шкаф_2.lfs"), files.LfsPath);
    }

    [Fact]
    public void FindDeploymentFiles_HintOnPsl_DoesNotReplaceLfs()
    {
        // Подсказка применяется по каждому расширению отдельно: указанный .psl остаётся исходником,
        // а заливаемым .lfs становится первый найденный, а не «подсказка любой ценой».
        using var root = new TempRoot();
        File.WriteAllText(Path.Combine(root.Path, "прог.lfs"), "lfs");
        File.WriteAllText(Path.Combine(root.Path, "проект_1.psl"), "a");
        File.WriteAllText(Path.Combine(root.Path, "проект_2.psl"), "b");

        var files = LoaderFiles.FindDeploymentFiles(new[] { root.Path }, "проект_2.psl");

        Assert.Equal(Path.Combine(root.Path, "прог.lfs"), files.LfsPath);
        Assert.Equal(Path.Combine(root.Path, "проект_2.psl"), files.PslPath);
    }

    [Fact]
    public void FindDeploymentFiles_NoHint_KeepsFolderPriority()
    {
        // Без подсказки поведение прежнее — локальная копия раньше сетевой папки.
        using var root = new TempRoot();
        var local = Path.Combine(root.Path, "local");
        var disk = Path.Combine(root.Path, "disk");
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(disk);
        File.WriteAllText(Path.Combine(local, "project.lfs"), "local lfs");
        File.WriteAllText(Path.Combine(disk, "project.lfs"), "disk lfs");

        var files = LoaderFiles.FindDeploymentFiles(new[] { local, disk }, "");

        Assert.Equal(Path.Combine(local, "project.lfs"), files.LfsPath);
    }
}
