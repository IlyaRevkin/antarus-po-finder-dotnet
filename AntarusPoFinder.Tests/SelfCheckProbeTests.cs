using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.App;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Сбор снимка машины для «Проверки компьютера». Выводов здесь не делается (за них
/// отвечает SelfCheckAnalyzer), поэтому и проверяется другое — то, на чём диагностика ломается
/// в реальности:
/// <list type="bullet">
/// <item>НЕ ВИСНУТЬ. Обращение к отвалившейся SMB-шаре само по себе отвечает секундами, а проверок
/// пять; сложить их таймауты подряд — значит повесить окно ровно в том случае, ради которого оно
/// написано;</item>
/// <item>НЕ БРОСАТЬ. На сломанной машине падает половина обращений, и проверка обязана довести
/// остальные до конца.</item>
/// </list></summary>
public class SelfCheckProbeTests
{
    /// <summary>Запас поверх таймаута: планировщик потоков и Task.Delay точностью до миллисекунды не
    /// обладают, но «уложились в таймаут + запас» и «висим, пока шара не ответит» — разница на
    /// порядки, так что грубого запаса достаточно и тест не становится хрупким.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(3);

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("No such host is known");
    }

    [Fact]
    public async Task CollectAsync_EverythingUnreachable_FinishesWithinOneTimeoutBudget()
    {
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
            var facts = await SelfCheckProbe.CollectAsync(cfg, db, "revkin.i", timeout);
            sw.Stop();

            Assert.True(sw.Elapsed < timeout + Slack, $"сбор снимка занял {sw.Elapsed}");
            Assert.False(facts.RootExists);
            Assert.False(facts.SecondDiskExists);
            Assert.False(facts.UpdateFolderReachable);
            Assert.False(facts.GitHubReachable);
            // Ни до одной цели внутри конторы не дозвонились — именно этим «наладчик вне офиса»
            // отличается от «настроено противоречиво».
            Assert.False(facts.OfficeNetworkReachable);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public async Task CollectAsync_ReachableRoot_IsReportedAsExistingAndReadable()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();
        var cfg = new ConfigService(db);
        cfg.SetRootPath(root.Path);
        cfg.Set("ad_domain", "");

        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new ThrowingHttpMessageHandler()));

            var facts = await SelfCheckProbe.CollectAsync(cfg, db, "revkin.i", TimeSpan.FromSeconds(5));

            Assert.True(facts.RootExists);
            Assert.True(facts.RootReadable);
            Assert.True(facts.OfficeNetworkReachable);
            Assert.Equal(Environment.MachineName, facts.MachineName);
            Assert.Equal("revkin.i", facts.AppUser);
            // База открыта — значит пути посчитаны, а не «не проверялись».
            Assert.True(facts.StoredPathsChecked);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    /// <summary>Окно диагностики открывается и из окна входа, до загрузки базы. Проверка путей тогда
    /// пропускается — но молча и честно, а не выдуманными нулями, которые выглядели бы как «всё в
    /// порядке».</summary>
    [Fact]
    public async Task CollectAsync_WithoutDatabase_MarksStoredPathsAsNotChecked()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);
        cfg.Set("ad_domain", "");

        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new ThrowingHttpMessageHandler()));

            var facts = await SelfCheckProbe.CollectAsync(cfg, null, "revkin.i", TimeSpan.FromSeconds(5));

            Assert.False(facts.StoredPathsChecked);
            var paths = SelfCheckAnalyzer.Analyze(facts).First(x => x.Title == "Пути к файлам в базе");
            Assert.Equal(SelfCheckSeverity.Info, paths.Severity);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    /// <summary>Общая папка обновлений, заданная относительным путём, разворачивается от корня ЭТОЙ
    /// машины — сквозной прогон настройки, гипотезы и сбора фактов в одном месте.</summary>
    [Fact]
    public async Task CollectAsync_RelativeSharedUpdatePath_IsResolvedAgainstThisMachinesRoot()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "Обновления"));

        var cfg = new ConfigService(db);
        cfg.SetRootPath(root.Path);
        cfg.SetAppUpdatePathShared("Обновления");
        cfg.Set("ad_domain", "");

        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new ThrowingHttpMessageHandler()));

            var facts = await SelfCheckProbe.CollectAsync(cfg, db, "revkin.i", TimeSpan.FromSeconds(5));

            Assert.Equal(Path.Combine(root.Path, "Обновления"), facts.UpdatePathEffective);
            Assert.True(facts.UpdateFolderReachable);
            Assert.Contains("развёрнутая от корня", SelfCheckAnalyzer.UpdateRuleText(facts));
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }
}
