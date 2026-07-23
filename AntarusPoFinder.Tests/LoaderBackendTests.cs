using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Loader;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Заготовка лоадера и поиск .lfs/.psl. Заглушка обязана вести себя как настоящий лоадер
/// по прогрессу/логу — и при этом нигде не притворяться, что что-то реально загрузила.</summary>
public class LoaderBackendTests
{
    private static readonly StubFirmwareLoaderBackend Stub = new(stepDelay: TimeSpan.Zero);

    /// <summary>Синхронный сбор отчётов: штатный <see cref="Progress{T}"/> доставляет их через
    /// SynchronizationContext, и без UI-потока часть пришла бы уже после await — тесты плавали бы.</summary>
    private sealed class CollectingProgress : IProgress<LoaderProgress>
    {
        public List<LoaderProgress> Reports { get; } = new();
        public void Report(LoaderProgress value) => Reports.Add(value);
    }

    private static LoaderRequest Request(string workspaceDir, LoaderOperation op = LoaderOperation.Flash,
        bool format = false, bool kernel = false) => new()
    {
        Operation = op,
        SourcePath = Path.Combine(workspaceDir, "src", "prog.lfs"),
        WorkspaceDir = workspaceDir,
        VersionName = "2.1.042",
        Options = new LoaderOptions { Format = format, UpdateKernel = kernel, Target = "COM3" },
    };

    [Fact]
    public async Task Stub_ReportsMonotonicProgressUpTo100()
    {
        using var root = new TempRoot();
        var progress = new CollectingProgress();

        var result = await Stub.RunAsync(Request(root.Path), progress, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotEmpty(progress.Reports);
        Assert.Equal(100, progress.Reports[^1].Percent);
        for (var i = 1; i < progress.Reports.Count; i++)
            Assert.True(progress.Reports[i].Percent >= progress.Reports[i - 1].Percent, "прогресс не должен идти назад");
    }

    [Fact]
    public async Task Stub_NeverClaimsRealWork()
    {
        using var root = new TempRoot();
        var progress = new CollectingProgress();

        var result = await Stub.RunAsync(Request(root.Path), progress, CancellationToken.None);

        Assert.False(Stub.IsAvailable);
        Assert.Contains("реальная сборка/загрузка не выполнялась", result.Message);
        Assert.All(progress.Reports.Where(r => r.Message.Contains("контроллер", StringComparison.OrdinalIgnoreCase)),
            r => Assert.StartsWith(StubFirmwareLoaderBackend.LogPrefix, r.Message));
        // Вместо .lfs заглушка кладёт памятку — её нельзя перепутать с настоящей сборкой.
        var marker = Path.Combine(root.Path, "out", StubFirmwareLoaderBackend.StubMarkerFileName);
        Assert.True(File.Exists(marker));
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(root.Path, "out")),
            f => f.EndsWith(".lfs", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public async Task Stub_OptionsChangeTheStages()
    {
        using var root = new TempRoot();
        var withOptions = new CollectingProgress();
        var withoutOptions = new CollectingProgress();

        await Stub.RunAsync(Request(root.Path, format: true, kernel: true), withOptions, CancellationToken.None);
        await Stub.RunAsync(Request(root.Path), withoutOptions, CancellationToken.None);

        Assert.Contains(withOptions.Reports, r => r.Message.Contains("Форматирование памяти"));
        Assert.Contains(withOptions.Reports, r => r.Message.Contains("Обновление ядра контроллера"));
        Assert.Contains(withoutOptions.Reports, r => r.Message.Contains("Форматирование отключено"));
        Assert.Contains(withoutOptions.Reports, r => r.Message.Contains("Обновление ядра отключено"));
    }

    [Fact]
    public async Task Stub_BuildAndFlash_HaveDifferentStages()
    {
        using var root = new TempRoot();
        var build = new CollectingProgress();
        var flash = new CollectingProgress();

        await Stub.RunAsync(Request(root.Path, LoaderOperation.Build), build, CancellationToken.None);
        await Stub.RunAsync(Request(root.Path), flash, CancellationToken.None);

        Assert.Contains(build.Reports, r => r.Stage == "Сборка");
        Assert.DoesNotContain(build.Reports, r => r.Stage == "Передача");
        Assert.Contains(flash.Reports, r => r.Stage == "Передача");
        Assert.DoesNotContain(flash.Reports, r => r.Stage == "Сборка");
    }

    [Fact]
    public async Task Stub_HonoursCancellation()
    {
        using var root = new TempRoot();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Stub.RunAsync(Request(root.Path), new CollectingProgress(), cts.Token));
    }

    [Fact]
    public void Factory_AlwaysStubForNow_ButExplainsWhy()
    {
        var noPath = FirmwareLoaderFactory.Create("");
        var badPath = FirmwareLoaderFactory.Create(@"C:\нет\такого\loader.exe");

        Assert.False(noPath.IsAvailable);
        Assert.False(badPath.IsAvailable);
        Assert.Contains("не задан", noPath.UnavailableReason!);
        Assert.Contains("не найден", badPath.UnavailableReason!);
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
    public void LoaderFiles_FindIn_TakesFirstFolderThatHasIt()
    {
        using var root = new TempRoot();
        var local = Path.Combine(root.Path, "local");
        var disk = Path.Combine(root.Path, "disk");
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(disk);
        File.WriteAllText(Path.Combine(disk, "prog.lfs"), "disk");
        File.WriteAllText(Path.Combine(local, "prog.lfs"), "local");

        // Порядок кандидатов задаёт вызывающий (SearchView: локальный кэш раньше сетевой папки).
        Assert.Equal(Path.Combine(local, "prog.lfs"),
            LoaderFiles.FindIn(new[] { local, disk }, LoaderFiles.LfsExtension));
        Assert.Equal(Path.Combine(disk, "prog.lfs"),
            LoaderFiles.FindIn(new[] { Path.Combine(root.Path, "нет"), disk }, LoaderFiles.LfsExtension));
    }
}
