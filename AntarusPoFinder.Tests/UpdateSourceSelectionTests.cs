using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.App;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Выбор источника обновлений — то, от чего зависит, доедут ли до рабочих машин новые
/// версии вообще. Проверяется три вещи: папка приоритетнее GitHub; недоступность ОБОИХ источников —
/// это явная ошибка, а не «обновлений нет»; и правила, по которым машина понимает, какая папка
/// вообще её (локальная настройка / общая синхронизируемая / относительная от корня диска).</summary>
public class UpdateSourceSelectionTests
{
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int Calls { get; private set; }
        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Exception _exception;
        public ThrowingHttpMessageHandler(Exception exception) => _exception = exception;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw _exception;
    }

    private const string OneReleaseJson = """
        [
          {
            "tag_name": "v9.9.9",
            "assets": [
              { "name": "AntarusPoFinder-9.9.9.exe", "browser_download_url": "https://example.invalid/AntarusPoFinder-9.9.9.exe", "size": 10 }
            ]
          }
        ]
        """;

    // ── Приоритет источников ─────────────────────────────────────────────────

    [Fact]
    public async Task CheckForUpdatesAsync_FolderAvailable_UsesFolderAndDoesNotAskGitHub()
    {
        using var root = new TempRoot();
        File.WriteAllText(Path.Combine(root.Path, "AntarusPoFinder-1.0.0.exe"), "release");

        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OneReleaseJson) });
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(handler));

            var result = await AppUpdateService.CheckForUpdatesAsync(root.Path);

            Assert.Equal(UpdateSourceKind.Folder, result.Source);
            Assert.Equal(new Version(1, 0, 0), result.Releases[0].Version);
            Assert.Null(result.FolderProblem);
            // Папка приоритетнее не только «на бумаге»: до GitHub дело не доходит вовсе — именно это и
            // делает обновления независимыми от доступности github.com из заводской сети.
            Assert.Equal(0, handler.Calls);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_FolderUnavailableButGitHubAlive_ReportsFolderProblem()
    {
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OneReleaseJson) })));

            var result = await AppUpdateService.CheckForUpdatesAsync(@"Z:\нет\такой\папки\обновлений");

            // Обновления продолжают работать (запасной источник), но факт «настроенная папка
            // отвалилась» больше не теряется — иначе он неотличим от «папка не настроена».
            Assert.Equal(UpdateSourceKind.GitHub, result.Source);
            Assert.NotNull(result.FolderProblem);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    // ── Обе стороны недоступны = явная ошибка ────────────────────────────────

    [Fact]
    public async Task CheckForUpdatesAsync_BothSourcesUnavailable_ThrowsExplicitErrorMentioningBoth()
    {
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(
                new ThrowingHttpMessageHandler(new HttpRequestException("No such host is known"))));

            var ex = await Assert.ThrowsAsync<UpdateSourcesUnavailableException>(
                () => AppUpdateService.CheckForUpdatesAsync(@"Z:\нет\такой\папки\обновлений"));

            Assert.Contains("Ни один источник обновлений недоступен", ex.Message);
            Assert.Contains(@"Z:\нет\такой\папки\обновлений", ex.Message);
            Assert.Contains("GitHub", ex.Message);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public async Task DescribeError_BothSourcesUnavailable_KeepsFolderReasonInsteadOfOnlyGitHub()
    {
        // Без отдельной ветки в DescribeError разбор цепочки исключений вернул бы обычное «не удалось
        // соединиться с GitHub», и про недоступную папку — главное для этой машины — не сказали бы.
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(
                new ThrowingHttpMessageHandler(new HttpRequestException("No such host is known"))));

            var ex = await Assert.ThrowsAsync<UpdateSourcesUnavailableException>(
                () => AppUpdateService.CheckForUpdatesAsync(@"Z:\нет\такой\папки\обновлений"));

            var described = AppUpdateService.DescribeError(ex);
            Assert.Contains("Папка", described);
            Assert.Contains("GitHub", described);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_NoFolderConfigured_GitHubFailure_StillPropagatesOriginalException()
    {
        // Регрессия: обёртка «недоступны оба» появляется ТОЛЬКО когда папка реально настроена. Если
        // папки нет, ошибка GitHub должна долетать до вызывающего в прежнем виде.
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(
                new ThrowingHttpMessageHandler(new HttpRequestException("No such host is known"))));

            await Assert.ThrowsAsync<HttpRequestException>(() => AppUpdateService.CheckForUpdatesAsync(null));
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    // ── Опрос обоих источников (кнопка «Проверить» / «Состояние подключения») ─

    [Fact]
    public async Task ProbeSourcesAsync_FolderAliveGitHubDown_EffectiveSourceIsFolder()
    {
        using var root = new TempRoot();
        File.WriteAllText(Path.Combine(root.Path, "AntarusPoFinder-2.0.0.exe"), "release");
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(
                new ThrowingHttpMessageHandler(new HttpRequestException("No such host is known"))));

            var report = await AppUpdateService.ProbeSourcesAsync(root.Path, TimeSpan.FromSeconds(5));

            Assert.True(report.Folder.Available);
            Assert.Equal(new Version(2, 0, 0), report.Folder.LatestVersion);
            Assert.False(report.GitHub.Available);
            Assert.Equal(UpdateSourceKind.Folder, report.EffectiveSource);

            var text = AppUpdateService.DescribeSources(report);
            Assert.Contains("2.0.0", text);
            Assert.Contains("НЕДОСТУПЕН", text);
            Assert.Contains("Будет использована папка обновлений", text);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public async Task ProbeSourcesAsync_BothDown_EffectiveSourceIsNullAndSaysSoInPlainRussian()
    {
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(
                new ThrowingHttpMessageHandler(new HttpRequestException("No such host is known"))));

            var report = await AppUpdateService.ProbeSourcesAsync(@"Z:\нет\такой\папки", TimeSpan.FromSeconds(5));

            Assert.Null(report.EffectiveSource);
            Assert.Contains("Ни один источник обновлений не доступен", AppUpdateService.DescribeSources(report));
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public async Task ProbeSourcesAsync_NoFolderConfigured_FolderReportedAsNotConfiguredNotAsBroken()
    {
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OneReleaseJson) })));

            var report = await AppUpdateService.ProbeSourcesAsync("", TimeSpan.FromSeconds(5));

            Assert.False(report.Folder.Configured);
            Assert.Equal(UpdateSourceKind.GitHub, report.EffectiveSource);
            Assert.Contains("не настроена", AppUpdateService.DescribeSources(report));
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    // ── Какая папка «моя»: локальная / общая / относительная от корня диска ───

    [Fact]
    public void Resolve_LocalPathWins_OverSharedOne()
    {
        Assert.Equal(@"D:\local", UpdateFolderResolver.Resolve(@"D:\local", @"\\srv\share\upd", @"Z:\root"));
    }

    [Fact]
    public void Resolve_SharedRelativePath_IsRerootedOnThisMachinesRoot()
    {
        // Главный смысл общей настройки: у коллеги диск подключён как Z:, у меня — как \\srv\share,
        // но «Обновления» лежат в одном и том же месте относительно корня.
        Assert.Equal(Path.Combine(@"Z:\root", "Обновления"), UpdateFolderResolver.Resolve("", "Обновления", @"Z:\root"));
        Assert.Equal(Path.Combine(@"\\srv\share", "Обновления"), UpdateFolderResolver.Resolve("", @".\Обновления", @"\\srv\share"));
    }

    [Fact]
    public void Resolve_SharedUncPath_IsUsedAsIs()
    {
        Assert.Equal(@"\\srv\share\upd", UpdateFolderResolver.Resolve("", @"\\srv\share\upd", @"Z:\root"));
    }

    [Fact]
    public void Resolve_NothingConfigured_ReturnsEmptyMeaningGitHub()
    {
        Assert.Equal("", UpdateFolderResolver.Resolve("", "", @"Z:\root"));
        Assert.Equal("", UpdateFolderResolver.Resolve(null, null, null));
        // Относительный общий путь без корня разворачивать не от чего — лучше «не настроено», чем
        // путь от текущего каталога процесса, который выглядел бы настроенным, но битым.
        Assert.Equal("", UpdateFolderResolver.Resolve("", "Обновления", ""));
    }

    [Fact]
    public void EffectiveAppUpdatePath_ReadsLocalThenSharedFromSettings()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);
        cfg.SetRootPath(@"Z:\root");

        Assert.Equal("", cfg.EffectiveAppUpdatePath());

        cfg.SetAppUpdatePathShared("Обновления");
        Assert.Equal(Path.Combine(@"Z:\root", "Обновления"), cfg.EffectiveAppUpdatePath());

        cfg.SetAppUpdatePath(@"D:\своя\папка");
        Assert.Equal(@"D:\своя\папка", cfg.EffectiveAppUpdatePath());
    }
}
