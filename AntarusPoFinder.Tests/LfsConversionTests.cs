using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Loader;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Автоконвертация .psl → .lfs: собранный файл обязан оказаться в папке версии НА СЕТЕВОМ
/// ДИСКЕ, рядом с исходником, а не только в локальном кэше заливавшей машины — иначе цель («залили
/// psl, наладчик на другой машине сразу видит lfs») не достигается: локальный кэш чужим машинам не
/// виден и затирается следующей синхронизацией.</summary>
public class LfsConversionTests
{
    /// <summary>Backend-заглушка: пишет «собранный» файл туда, куда попросили, и отчитывается
    /// успехом — реальная сборка требует изолированного SMLogix и в тестах невозможна.</summary>
    private sealed class FakeBuildBackend : IFirmwareLoaderBackend
    {
        private readonly bool _succeed;
        private readonly bool _writeOutput;

        public FakeBuildBackend(bool succeed = true, bool writeOutput = true, bool available = true)
        {
            _succeed = succeed;
            _writeOutput = writeOutput;
            IsAvailable = available;
        }

        public List<LoaderRequest> Requests { get; } = new();
        public string Name => "fake";
        public string? DisplayVersion => null;
        public bool IsAvailable { get; }
        public string? UnavailableReason => IsAvailable ? null : "SMLogix не установлен";

        public Task<LoaderResult> RunAsync(
            LoaderRequest request, IProgress<LoaderProgress> progress, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (!_succeed) return Task.FromResult(LoaderResult.Fail("Сборка провалилась"));
            if (_writeOutput && request.OutputPath is { Length: > 0 } output)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, "собранный lfs");
                return Task.FromResult(LoaderResult.Ok("Готово", new[] { output }));
            }
            return Task.FromResult(LoaderResult.Ok("Готово"));
        }
    }

    private sealed class NullProgress : IProgress<LoaderProgress>
    {
        public void Report(LoaderProgress value) { }
    }

    private static (string Network, string Local, string Workspace) Layout(TempRoot root)
    {
        var network = Path.Combine(root.Path, "disk", "версия");
        var local = Path.Combine(root.Path, "cache", "версия");
        var workspace = Path.Combine(root.Path, "loader");
        Directory.CreateDirectory(network);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(workspace);
        return (network, local, workspace);
    }

    // ── Решение: надо ли собирать ─────────────────────────────────────────

    [Fact]
    public void Decide_WithoutNetworkFolder_RefusesToBuild()
    {
        using var root = new TempRoot();
        var (_, local, _) = Layout(root);
        File.WriteAllText(Path.Combine(local, "проект.psl"), "psl");

        // Собрать-то можно, но положить результат туда, где его увидят коллеги, — некуда.
        Assert.Equal(LfsConversionNeed.Unreachable,
            LfsConversionService.Decide("", local, null).Need);
        Assert.Equal(LfsConversionNeed.Unreachable,
            LfsConversionService.Decide(Path.Combine(root.Path, "нет-такой-папки"), local, null).Need);
    }

    [Fact]
    public void Decide_LfsAlreadyOnDisk_NothingToDo()
    {
        using var root = new TempRoot();
        var (network, local, _) = Layout(root);
        File.WriteAllText(Path.Combine(network, "проект.psl"), "psl");
        File.WriteAllText(Path.Combine(network, "проект.lfs"), "lfs");

        var decision = LfsConversionService.Decide(network, local, null);

        Assert.Equal(LfsConversionNeed.AlreadyPresent, decision.Need);
        Assert.Null(decision.Plan);
    }

    [Fact]
    public void Decide_WithoutPsl_ReportsNoSource()
    {
        using var root = new TempRoot();
        var (network, local, _) = Layout(root);
        File.WriteAllText(Path.Combine(network, "readme.txt"), "ничего интересного");

        Assert.Equal(LfsConversionNeed.NoSource, LfsConversionService.Decide(network, local, null).Need);
    }

    [Fact]
    public void Decide_TakesSourceFromDisk_NotFromLocalCache()
    {
        // Собранный .lfs ляжет РЯДОМ с сетевым .psl, поэтому и собираться он должен из него:
        // устаревшая локальная копия дала бы коллегам .lfs, не соответствующий лежащему рядом psl.
        using var root = new TempRoot();
        var (network, local, _) = Layout(root);
        File.WriteAllText(Path.Combine(network, "проект.psl"), "сетевой");
        File.WriteAllText(Path.Combine(local, "проект.psl"), "локальный устаревший");

        var decision = LfsConversionService.Decide(network, local, null);

        Assert.Equal(LfsConversionNeed.Build, decision.Need);
        Assert.Equal(Path.Combine(network, "проект.psl"), decision.Plan!.PslPath);
        Assert.Equal("проект.lfs", decision.Plan.OutputFileName);
    }

    [Fact]
    public void Decide_UsesOperatorChosenPsl()
    {
        // В папке версии пачка исходников (по одному на шкаф) — берём указанный в модерации.
        using var root = new TempRoot();
        var (network, local, _) = Layout(root);
        File.WriteAllText(Path.Combine(network, "шкаф_1.psl"), "1");
        File.WriteAllText(Path.Combine(network, "шкаф_2.psl"), "2");

        var decision = LfsConversionService.Decide(network, local, "шкаф_2.psl");

        Assert.Equal(Path.Combine(network, "шкаф_2.psl"), decision.Plan!.PslPath);
        Assert.Equal("шкаф_2.lfs", decision.Plan.OutputFileName);
    }

    // ── Сборка и публикация ───────────────────────────────────────────────

    [Fact]
    public async Task BuildAndPublish_PutsLfsNextToPslOnDiskAndMirrorsLocally()
    {
        using var root = new TempRoot();
        var (network, local, workspace) = Layout(root);
        File.WriteAllText(Path.Combine(network, "проект.psl"), "psl");

        var decision = LfsConversionService.Decide(network, local, null);
        var backend = new FakeBuildBackend();

        var result = await LfsConversionService.BuildAndPublishAsync(
            backend, decision.Plan!, workspace, "2.1.042", new NullProgress(), CancellationToken.None);

        Assert.Equal(LfsConversionStatus.Built, result.Status);
        Assert.True(File.Exists(Path.Combine(network, "проект.lfs")));
        Assert.True(File.Exists(Path.Combine(local, "проект.lfs")));
        Assert.Empty(result.Warnings);
        // Сборка идёт с ЛОКАЛЬНОЙ копии исходника, а не прямо с сетевого диска.
        var request = Assert.Single(backend.Requests);
        Assert.Equal(LoaderOperation.Build, request.Operation);
        Assert.StartsWith(workspace, request.SourcePath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(workspace, request.OutputPath!, StringComparison.OrdinalIgnoreCase);
        // После сборки в папке версии не остаётся временных огрызков публикации.
        Assert.Empty(Directory.EnumerateFiles(network, "*" + LfsPublisher.TempSuffix));
    }

    [Fact]
    public async Task BuildAndPublish_WithoutLocalCopy_StillPublishesToDisk()
    {
        using var root = new TempRoot();
        var (network, _, workspace) = Layout(root);
        File.WriteAllText(Path.Combine(network, "проект.psl"), "psl");

        var decision = LfsConversionService.Decide(network, null, null);
        var result = await LfsConversionService.BuildAndPublishAsync(
            new FakeBuildBackend(), decision.Plan!, workspace, "2.1.042", new NullProgress(), CancellationToken.None);

        Assert.Equal(LfsConversionStatus.Built, result.Status);
        Assert.Equal(Path.Combine(network, "проект.lfs"), Assert.Single(result.Published));
    }

    [Fact]
    public async Task BuildAndPublish_BackendFailure_LeavesVersionFolderUntouched()
    {
        using var root = new TempRoot();
        var (network, local, workspace) = Layout(root);
        File.WriteAllText(Path.Combine(network, "проект.psl"), "psl");

        var decision = LfsConversionService.Decide(network, local, null);
        var result = await LfsConversionService.BuildAndPublishAsync(
            new FakeBuildBackend(succeed: false), decision.Plan!, workspace, "2.1.042",
            new NullProgress(), CancellationToken.None);

        Assert.Equal(LfsConversionStatus.Failed, result.Status);
        Assert.Equal("Сборка провалилась", result.Message);
        Assert.False(File.Exists(Path.Combine(network, "проект.lfs")));
    }

    [Fact]
    public async Task BuildAndPublish_SuccessWithoutFile_IsFailureNotSilentSkip()
    {
        using var root = new TempRoot();
        var (network, local, workspace) = Layout(root);
        File.WriteAllText(Path.Combine(network, "проект.psl"), "psl");

        var decision = LfsConversionService.Decide(network, local, null);
        var result = await LfsConversionService.BuildAndPublishAsync(
            new FakeBuildBackend(writeOutput: false), decision.Plan!, workspace, "2.1.042",
            new NullProgress(), CancellationToken.None);

        Assert.Equal(LfsConversionStatus.Failed, result.Status);
        Assert.False(File.Exists(Path.Combine(network, "проект.lfs")));
    }

    [Fact]
    public async Task BuildAndPublish_WithoutAutomation_SaysSoInsteadOfFailing()
    {
        using var root = new TempRoot();
        var (network, local, workspace) = Layout(root);
        File.WriteAllText(Path.Combine(network, "проект.psl"), "psl");

        var decision = LfsConversionService.Decide(network, local, null);
        var result = await LfsConversionService.BuildAndPublishAsync(
            new FakeBuildBackend(available: false), decision.Plan!, workspace, "2.1.042",
            new NullProgress(), CancellationToken.None);

        Assert.Equal(LfsConversionStatus.Unavailable, result.Status);
        Assert.Contains("SMLogix", result.Message);
    }

    // ── Публикация ────────────────────────────────────────────────────────

    [Fact]
    public void Publish_OverwritesPreviousFileAndLeavesNoTemporaries()
    {
        using var root = new TempRoot();
        var (network, _, workspace) = Layout(root);
        var built = Path.Combine(workspace, "проект.lfs");
        File.WriteAllText(Path.Combine(network, "проект.lfs"), "старый");
        File.WriteAllText(built, "новый");

        var published = LfsPublisher.Publish(built, network);

        Assert.Equal(Path.Combine(network, "проект.lfs"), published);
        Assert.Equal("новый", File.ReadAllText(published));
        Assert.Empty(Directory.EnumerateFiles(network, "*" + LfsPublisher.TempSuffix));
    }

    [Fact]
    public void PublishAll_UnreachableDisk_WarnsButKeepsLocalCopy()
    {
        // Шара отвалилась — это в проекте норма: собранный файл всё равно ложится в локальную копию,
        // но оператор обязан узнать, что коллеги его пока не увидят.
        using var root = new TempRoot();
        var (_, local, workspace) = Layout(root);
        var built = Path.Combine(workspace, "проект.lfs");
        File.WriteAllText(built, "новый");

        var plan = LfsPublisher.Plan(Path.Combine(root.Path, "оторванная-шара"), local);
        var result = LfsPublisher.PublishAll(built, plan);

        Assert.Null(plan.NetworkFolder);
        Assert.Equal(Path.Combine(local, "проект.lfs"), Assert.Single(result.Published));
        Assert.Contains(result.Warnings, w => w.Contains("сетевом диске"));
    }

    [Fact]
    public void PublishAll_NoFoldersAtAll_ReportsItInsteadOfPretendingSuccess()
    {
        using var root = new TempRoot();
        var (_, _, workspace) = Layout(root);
        var built = Path.Combine(workspace, "проект.lfs");
        File.WriteAllText(built, "новый");

        var result = LfsPublisher.PublishAll(built, LfsPublisher.Plan(null, null));

        Assert.False(result.AnyPublished);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Plan_KeepsNetworkFolderFirst()
    {
        using var root = new TempRoot();
        var (network, local, _) = Layout(root);

        var plan = LfsPublisher.Plan(network, local);

        Assert.Equal(new[] { network, local }, plan.Folders);
    }

    // ── Куда именно ложится собранный файл ────────────────────────────────
    // Жалоба: «когда он создаёт LFS, он кладёт не в папку Прошивка, а в корень папки версии».

    [Fact]
    public void Plan_RebuiltVersion_TargetsFirmwareSubfolder()
    {
        using var root = new TempRoot();
        var (network, local, _) = Layout(root);
        var networkFirmware = VersionLayout.FirmwareFolder(network);
        var localFirmware = VersionLayout.FirmwareFolder(local);
        Directory.CreateDirectory(networkFirmware);
        Directory.CreateDirectory(localFirmware);

        var plan = LfsPublisher.Plan(network, local);

        Assert.Equal(networkFirmware, plan.NetworkFolder);
        Assert.Equal(localFirmware, plan.LocalFolder);
    }

    [Fact]
    public void Plan_VersionWithoutFirmwareFolder_StaysInVersionRoot()
    {
        // Режим совместимости: у неперестроенной версии подпапки нет, и заводить её тут нельзя —
        // коллеги со старым клиентом смотрят только в корень папки версии.
        using var root = new TempRoot();
        var (network, _, _) = Layout(root);

        var plan = LfsPublisher.Plan(network, null);

        Assert.Equal(network, plan.NetworkFolder);
    }

    [Fact]
    public async Task BuildAndPublish_RebuiltVersion_PutsLfsNextToItsSource()
    {
        using var root = new TempRoot();
        var (network, _, workspace) = Layout(root);
        var firmware = VersionLayout.FirmwareFolder(network);
        Directory.CreateDirectory(firmware);
        // Исходник лежит там, где ему и положено у перестроенной версии.
        File.WriteAllText(Path.Combine(firmware, "проект.psl"), "исходник");

        var decision = LfsConversionService.Decide(network, null, null);
        Assert.Equal(LfsConversionNeed.Build, decision.Need);

        var result = await LfsConversionService.BuildAndPublishAsync(
            new FakeBuildBackend(), decision.Plan!, workspace, "1.0.0001.0001",
            new NullProgress(), CancellationToken.None);

        Assert.Equal(LfsConversionStatus.Built, result.Status);
        Assert.Equal(Path.Combine(firmware, "проект.lfs"), Assert.Single(result.Published));
        // В корне папки версии не должно появиться ничего.
        Assert.Empty(Directory.EnumerateFiles(network));
    }

    [Fact]
    public void Decide_LfsLeftInVersionRootByOlderVersion_IsStillFound()
    {
        // Диск перестроили, но собранный до этого .lfs так и лежит в корне. Предлагать собрать его
        // заново — значит гонять сборку минут на десять ради файла, который уже есть.
        using var root = new TempRoot();
        var (network, _, _) = Layout(root);
        Directory.CreateDirectory(VersionLayout.FirmwareFolder(network));
        File.WriteAllText(Path.Combine(network, "проект.lfs"), "собран давно");

        var decision = LfsConversionService.Decide(network, null, null);

        Assert.Equal(LfsConversionNeed.AlreadyPresent, decision.Need);
    }
}
