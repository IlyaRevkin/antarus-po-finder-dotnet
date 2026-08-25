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

/// <summary>Проверка цели входа (домен/веб-сервер) — то, что осталось в ConnectionStatusService
/// после переезда диагностики в «Проверку компьютера» (SelfCheckProbe + SelfCheckAnalyzer).
///
/// Главное требование — НЕ ВИСНУТЬ: недостижимый домен отвечает секундами, а под IPsec туннель
/// поднимается позже старта приложения, и окно диагностики обязано оставаться живым ровно тогда,
/// когда оно нужно. Проверки папок, источников обновлений и сборки отчёта переехали вместе с
/// логикой — см. SelfCheckProbeTests и SelfCheckAnalyzerTests.</summary>
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
}
