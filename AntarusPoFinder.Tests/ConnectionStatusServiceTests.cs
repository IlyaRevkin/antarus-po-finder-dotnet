using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.App;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Экран «Состояние подключения». Главное требование к нему — НЕ ВИСНУТЬ: проверка
/// отвалившейся сетевой шары сама по себе отвечает секундами, и если её не ограничить таймаутом,
/// диагностическое окно повиснет ровно в тот момент, когда оно и нужно. Поэтому основная часть
/// тестов — про соблюдение таймаута, а не про «зелёное/красное».</summary>
public class ConnectionStatusServiceTests
{
    /// <summary>Регрессия, пойманная живым GUI-прогоном: проверка недостижимого домена оставляла
    /// «ненаблюдённое» исключение брошенной по таймауту задачи подключения. Оно прилетало с потока
    /// финализатора в TaskScheduler.UnobservedTaskException, а тот в приложении показывает модальное
    /// «Произошла ошибка» и заводит тикет о сбое — то есть экран диагностики ронял приложение ровно
    /// на той машине, где домен недоступен (вне домена, поднимающийся IPsec-туннель). Здесь именно
    /// это и проверяется: после сборки мусора ни одного ненаблюдённого исключения быть не должно.</summary>
    [Fact]
    public void TryTcpConnect_UnreachableHost_LeavesNoUnobservedTaskException()
    {
        var unobserved = 0;
        void Handler(object? _, UnobservedTaskExceptionEventArgs args)
        {
            Interlocked.Increment(ref unobserved);
            args.SetObserved(); // иначе упадём сами и утащим соседние тесты
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            // Имя заведомо не разрешается в DNS, таймаут заведомо меньше времени отказа резолвера —
            // ровно тот путь, где задача бросается и падает уже после нашего ухода.
            Assert.False(ConnectionStatusService.TryTcpConnect(
                "не-существует-antarus-проверка.invalid", 389, TimeSpan.FromMilliseconds(50)));

            // Дать брошенной задаче упасть, затем прогнать финализаторы дважды: исключение
            // публикуется именно финализатором Task, и одного прохода не всегда достаточно.
            Thread.Sleep(3000);
            for (var i = 0; i < 2; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            GC.Collect();

            Assert.Equal(0, Volatile.Read(ref unobserved));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No such host is known");
    }

    /// <summary>Запас поверх таймаута: планировщик потоков и Task.Delay точностью до миллисекунды не
    /// обладают, но «уложились в таймаут + запас» и «висим, пока шара не ответит» — разница на
    /// порядки, так что грубого запаса достаточно и тест не становится хрупким.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task CheckFolderAsync_ProbeHangs_ReturnsWithinTimeoutInsteadOfWaitingForIt()
    {
        var timeout = TimeSpan.FromMilliseconds(300);
        var sw = Stopwatch.StartNew();

        var result = await ConnectionStatusService.CheckFolderAsync(
            "Корень сетевого диска", @"\\мертвая-шара\ПО", timeout,
            probe: _ => { Thread.Sleep(TimeSpan.FromSeconds(30)); return true; });

        sw.Stop();
        Assert.True(sw.Elapsed < timeout + Slack, $"проверка вернулась за {sw.Elapsed}, а должна была уложиться в {timeout} + запас");
        Assert.Equal(ConnectionState.Failed, result.State);
        Assert.Contains("не ответил", result.Details);
    }

    [Fact]
    public async Task CheckFolderAsync_ProbeThrows_ReportsFailureWithReasonAndDoesNotThrow()
    {
        var result = await ConnectionStatusService.CheckFolderAsync(
            "Второй диск", @"Z:\второй", TimeSpan.FromSeconds(5),
            probe: _ => throw new IOException("Доступ запрещён"));

        Assert.Equal(ConnectionState.Failed, result.State);
        Assert.Contains("Доступ запрещён", result.Details);
    }

    [Fact]
    public async Task CheckFolderAsync_ExistingFolder_IsOk()
    {
        using var root = new TempRoot();
        var result = await ConnectionStatusService.CheckFolderAsync("Корень сетевого диска", root.Path, TimeSpan.FromSeconds(5));

        Assert.Equal(ConnectionState.Ok, result.State);
        Assert.Equal(root.Path, result.Target);
    }

    [Fact]
    public async Task CheckFolderAsync_PathNotConfigured_IsNotReportedAsFailure()
    {
        // «Второй диск не настроен» не должно гореть красным — иначе на красное перестанут смотреть.
        var result = await ConnectionStatusService.CheckFolderAsync("Второй диск", "", TimeSpan.FromSeconds(5));

        Assert.Equal(ConnectionState.NotConfigured, result.State);
    }

    [Fact]
    public async Task CheckAuthTargetAsync_HttpMode_UsesWebServerAndReportsItsProblem()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);
        cfg.Set("ad_auth_mode", "http");
        cfg.SetAdHttpUrl("https://disk.example.invalid/cloud");

        var result = await ConnectionStatusService.CheckAuthTargetAsync(cfg, TimeSpan.FromSeconds(5),
            tcpProbe: (_, _) => throw new InvalidOperationException("в режиме http домен трогать нельзя"),
            httpProbe: _ => "сервер не отвечает");

        Assert.Equal(ConnectionState.Failed, result.State);
        Assert.Contains("сервер не отвечает", result.Details);
    }

    [Fact]
    public async Task CheckAuthTargetAsync_BothMode_DomainDown_ButWebServerAlive_IsOk()
    {
        // Ровно то же правило, что у CombinedAdCredentialValidator: при недоступном домене вход
        // всё равно проходит через веб-сервер — значит и состояние «зелёное», а причина видна в тексте.
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);
        cfg.Set("ad_auth_mode", "both");
        cfg.Set("ad_domain", "Elita");
        cfg.SetAdHttpUrl("https://disk.example.invalid/cloud");

        var result = await ConnectionStatusService.CheckAuthTargetAsync(cfg, TimeSpan.FromSeconds(5),
            tcpProbe: (_, _) => false,
            httpProbe: _ => null);

        Assert.Equal(ConnectionState.Ok, result.State);
        Assert.Contains("домен", result.Details);
        Assert.Contains("веб-сервер", result.Details);
    }

    [Fact]
    public async Task CheckAuthTargetAsync_BothMode_EverythingDown_IsFailed()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);
        cfg.Set("ad_auth_mode", "both");
        cfg.Set("ad_domain", "Elita");
        cfg.SetAdHttpUrl("https://disk.example.invalid/cloud");

        var result = await ConnectionStatusService.CheckAuthTargetAsync(cfg, TimeSpan.FromSeconds(5),
            tcpProbe: (_, _) => false,
            httpProbe: _ => "сервер не отвечает");

        Assert.Equal(ConnectionState.Failed, result.State);
    }

    [Fact]
    public async Task CheckAuthTargetAsync_DomainNotConfigured_IsNotReportedAsFailure()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);
        cfg.Set("ad_auth_mode", "ldap");
        cfg.Set("ad_domain", "");

        var result = await ConnectionStatusService.CheckAuthTargetAsync(cfg, TimeSpan.FromSeconds(5),
            tcpProbe: (_, _) => throw new InvalidOperationException("проверять нечего"));

        Assert.Equal(ConnectionState.NotConfigured, result.State);
    }

    [Fact]
    public async Task CheckUpdateSourcesAsync_BothSourcesDown_IsFailedAndSaysWhy()
    {
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new ThrowingHttpMessageHandler()));

            var result = await ConnectionStatusService.CheckUpdateSourcesAsync(@"Z:\нет\такой\папки", TimeSpan.FromSeconds(5));

            Assert.Equal(ConnectionState.Failed, result.State);
            Assert.Contains("не доступен", result.Details);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public async Task CheckUpdateSourcesAsync_FolderAlive_IsOkEvenWithoutGitHub()
    {
        using var root = new TempRoot();
        File.WriteAllText(Path.Combine(root.Path, "AntarusPoFinder-3.0.0.exe"), "release");
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new ThrowingHttpMessageHandler()));

            var result = await ConnectionStatusService.CheckUpdateSourcesAsync(root.Path, TimeSpan.FromSeconds(5));

            Assert.Equal(ConnectionState.Ok, result.State);
            Assert.Contains("3.0.0", result.Details);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public async Task CheckAllAsync_AllTargetsUnreachable_StillFinishesWithinOneTimeoutBudget()
    {
        // Проверки идут параллельно: последовательно четыре таймаута сложились бы в минуту ожидания
        // на полностью оборванной сети, и «не блокировать интерфейс» превратилось бы в фикцию.
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);
        cfg.SetRootPath(@"\\мертвая-шара\ПО");
        cfg.SetSecondDiskPath(@"\\мертвая-шара\Второй");
        cfg.Set("ad_auth_mode", "ldap");
        cfg.Set("ad_domain", ""); // не ходим в реальный DNS/домен из теста
        cfg.SetAppUpdatePath(@"\\мертвая-шара\Обновления");

        var timeout = TimeSpan.FromMilliseconds(500);
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new ThrowingHttpMessageHandler()));

            var sw = Stopwatch.StartNew();
            var results = await ConnectionStatusService.CheckAllAsync(cfg, timeout);
            sw.Stop();

            Assert.Equal(4, results.Count);
            Assert.True(sw.Elapsed < timeout + Slack, $"полная проверка заняла {sw.Elapsed}");
            Assert.All(results, r => Assert.NotEqual(ConnectionState.Checking, r.State));
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public void BuildReport_ContainsEveryCheckAndItsReason_SoItCanJustBeForwarded()
    {
        var results = new[]
        {
            new ConnectionCheckResult("Корень сетевого диска", ConnectionState.Ok, @"Z:\Software", "доступен"),
            new ConnectionCheckResult("Контроллер домена (LDAP)", ConnectionState.Failed, "Elita:389", "недоступен — нет сети до домена"),
            new ConnectionCheckResult("Второй диск", ConnectionState.NotConfigured, "", "путь не задан в настройках"),
        };

        var report = ConnectionStatusService.BuildReport(results);

        Assert.Contains("Корень сетевого диска", report);
        Assert.Contains(@"Z:\Software", report);
        Assert.Contains("недоступен — нет сети до домена", report);
        Assert.Contains("путь не задан в настройках", report);
        Assert.Contains(Environment.MachineName, report);
    }
}
