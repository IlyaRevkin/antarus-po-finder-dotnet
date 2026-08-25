using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.App;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>AppUpdateService had zero test coverage before this — and a real field incident (Раунд
/// 35: self-update silently failed, next launch was still the old version with no clue why). These
/// tests cover the pieces of the update flow that don't require a real network call or actually
/// restarting the process (InstallAndRestartAsync itself shuts the app down — not something a unit
/// test can safely exercise): folder-source release listing/version ordering, DescribeError's
/// network-vs-TLS-vs-generic classification, and DownloadReleaseAsync's byte-for-byte size
/// verification (the fix for "downloaded fine, then won't launch" truncated downloads) via a fake
/// HttpMessageHandler (see AppUpdateService.SetHttpClientForTests).</summary>
public class AppUpdateServiceTests
{
    /// <summary>Routes every request to a caller-supplied responder instead of hitting the real
    /// network — the standard seam for testing HttpClient-based code deterministically.</summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder(request));
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Exception _exception;
        public ThrowingHttpMessageHandler(Exception exception) => _exception = exception;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw _exception;
    }

    // ── Folder source (no network at all) ────────────────────────────────────

    [Fact]
    public async Task CheckForUpdatesAsync_FolderSource_OrdersReleasesNewestVersionFirst()
    {
        using var root = new TempRoot();
        File.WriteAllText(Path.Combine(root.Path, "AntarusPoFinder-1.2.0.exe"), "v1.2.0");
        File.WriteAllText(Path.Combine(root.Path, "AntarusPoFinder-1.10.0.exe"), "v1.10.0"); // numeric, not lexicographic
        File.WriteAllText(Path.Combine(root.Path, "AntarusPoFinder-1.3.5.exe"), "v1.3.5");

        var result = await AppUpdateService.CheckForUpdatesAsync(root.Path);

        Assert.Equal(UpdateSourceKind.Folder, result.Source);
        Assert.Equal(3, result.Releases.Count);
        // 1.10.0 must sort ABOVE 1.3.5 (Version comparison, not string comparison).
        Assert.Equal(new Version(1, 10, 0), result.Releases[0].Version);
        Assert.Equal(new Version(1, 3, 5), result.Releases[1].Version);
        Assert.Equal(new Version(1, 2, 0), result.Releases[2].Version);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_FolderSource_IgnoresFilesNotMatchingReleaseNamePattern()
    {
        using var root = new TempRoot();
        File.WriteAllText(Path.Combine(root.Path, "AntarusPoFinder-2.0.0.exe"), "real release");
        File.WriteAllText(Path.Combine(root.Path, "readme.txt"), "not a release");
        File.WriteAllText(Path.Combine(root.Path, "SomeOtherApp-2.0.0.exe"), "wrong prefix");
        File.WriteAllText(Path.Combine(root.Path, "AntarusPoFinder-not-a-version.exe"), "unparseable version");

        var result = await AppUpdateService.CheckForUpdatesAsync(root.Path);

        Assert.Single(result.Releases);
        Assert.Equal(new Version(2, 0, 0), result.Releases[0].Version);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_FolderSource_EmptyFolder_ReturnsNoReleases()
    {
        using var root = new TempRoot();
        var result = await AppUpdateService.CheckForUpdatesAsync(root.Path);
        Assert.Empty(result.Releases);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_FolderPathMissing_FallsBackToGitHubSource()
    {
        // Folder configured but doesn't exist on disk (e.g. a network share that's currently
        // unreachable) — CheckForUpdatesAsync must fall back to GitHub rather than throwing/crashing
        // the caller (MainWindowViewModel's background check, SettingsView's manual check).
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") })));

            var result = await AppUpdateService.CheckForUpdatesAsync(@"Z:\this\path\does\not\exist\at\all");

            Assert.Equal(UpdateSourceKind.GitHub, result.Source);
            Assert.Empty(result.Releases);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    // ── Network error handling (must be visible/propagate, not swallowed here — see
    //    MainWindowViewModel.CheckForAppUpdatesAsync/SettingsView for how callers surface it) ──────

    [Fact]
    public async Task CheckForUpdatesAsync_GitHubSource_NetworkFailure_PropagatesException()
    {
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(
                new ThrowingHttpMessageHandler(new HttpRequestException("No such host is known"))));

            await Assert.ThrowsAsync<HttpRequestException>(() => AppUpdateService.CheckForUpdatesAsync(null));
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Theory]
    [InlineData(typeof(System.Net.Sockets.SocketException), "Не удалось соединиться")]
    [InlineData(typeof(TaskCanceledException), "Не удалось соединиться")]
    public void DescribeError_NetworkException_ReturnsActionableRussianMessage(Type exceptionType, string expectedSubstring)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;
        var message = AppUpdateService.DescribeError(ex);
        Assert.Contains(expectedSubstring, message);
    }

    [Fact]
    public void DescribeError_TlsFailure_MentionsSecureConnection()
    {
        var ex = new System.Security.Authentication.AuthenticationException("The remote certificate is invalid");
        var message = AppUpdateService.DescribeError(ex);
        Assert.Contains("защищённое соединение", message);
    }

    [Fact]
    public void DescribeError_TlsFailureNestedAsInnerException_StillClassifiedCorrectly()
    {
        // DescribeError walks the InnerException chain — a raw HttpRequestException whose actual
        // cause is a wrapped TLS failure (the real shape .NET throws in practice) must still be
        // classified as a TLS problem, not fall through to the generic ex.Message branch.
        var inner = new System.Security.Authentication.AuthenticationException("SSL connection could not be established");
        var outer = new HttpRequestException("The SSL connection could not be established", inner);
        var message = AppUpdateService.DescribeError(outer);
        Assert.Contains("защищённое соединение", message);
    }

    [Fact]
    public void DescribeError_UnrecognizedException_FallsBackToRawMessage()
    {
        var ex = new InvalidOperationException("some unrelated failure");
        Assert.Equal("some unrelated failure", AppUpdateService.DescribeError(ex));
    }

    // ── Download + size verification (the actual Round-35-adjacent fix under test) ─────────────

    [Fact]
    public async Task DownloadReleaseAsync_SizeMatchesExpected_SavesFileAndReturnsPath()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("fake exe payload, byte-for-byte");
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));

            var release = new UpdateRelease(new Version(1, 2, 3), "AntarusPoFinder-1.2.3.exe", UpdateSourceKind.GitHub,
                DownloadUrl: "https://example.invalid/release.exe", ExpectedSize: bytes.Length);

            var path = await AppUpdateService.DownloadReleaseAsync(release);
            try
            {
                Assert.True(File.Exists(path));
                Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public async Task DownloadReleaseAsync_TruncatedDownload_ThrowsAndDeletesPartialFile()
    {
        // Root scenario this check exists for: a dropped connection/corporate proxy cuts the stream
        // short. Before the size check existed, this silently produced a broken .exe that "downloaded
        // fine, then won't launch" — much harder to diagnose than a clear error at download time.
        var actualBytes = System.Text.Encoding.UTF8.GetBytes("only half of this arrived");
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(actualBytes) })));

            var release = new UpdateRelease(new Version(1, 2, 3), "AntarusPoFinder-1.2.3.exe", UpdateSourceKind.GitHub,
                DownloadUrl: "https://example.invalid/release.exe", ExpectedSize: actualBytes.Length + 500);

            var tempPath = Path.Combine(Path.GetTempPath(), release.FileName);
            var ex = await Assert.ThrowsAsync<IOException>(() => AppUpdateService.DownloadReleaseAsync(release));

            Assert.Contains("повреждённым", ex.Message);
            Assert.False(File.Exists(tempPath)); // the truncated file must not be left behind for InstallAndRestart to pick up
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public async Task DownloadReleaseAsync_NoExpectedSizeReported_SkipsVerification()
    {
        // Some sources (or older GitHub API responses) may not report an asset size at all
        // (ExpectedSize <= 0) — must not be treated as "0 bytes expected" and reject everything.
        var bytes = System.Text.Encoding.UTF8.GetBytes("payload of unknown expected size");
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));

            var release = new UpdateRelease(new Version(1, 2, 3), "AntarusPoFinder-1.2.3.exe", UpdateSourceKind.GitHub,
                DownloadUrl: "https://example.invalid/release.exe", ExpectedSize: 0);

            var path = await AppUpdateService.DownloadReleaseAsync(release);
            try { Assert.True(File.Exists(path)); }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public async Task DownloadReleaseAsync_HttpErrorStatus_ThrowsInsteadOfSavingBrokenFile()
    {
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.NotFound))));

            var release = new UpdateRelease(new Version(1, 2, 3), "AntarusPoFinder-1.2.3.exe", UpdateSourceKind.GitHub,
                DownloadUrl: "https://example.invalid/missing.exe", ExpectedSize: 100);

            await Assert.ThrowsAsync<HttpRequestException>(() => AppUpdateService.DownloadReleaseAsync(release));
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    // ── SHA256 integrity check (Фикс 4 — GitHub source) ─────────────────────────────────────────

    [Fact]
    public async Task DownloadReleaseAsync_Sha256Matches_SavesFile()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("real release payload");
        var hashHex = Convert.ToHexString(SHA256.HashData(bytes));
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHttpMessageHandler(req =>
                req.RequestUri!.AbsoluteUri.EndsWith(".sha256")
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(hashHex) }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));

            var release = new UpdateRelease(new Version(1, 2, 3), "AntarusPoFinder-1.2.3.exe", UpdateSourceKind.GitHub,
                DownloadUrl: "https://example.invalid/AntarusPoFinder-1.2.3.exe", ExpectedSize: bytes.Length,
                Sha256Url: "https://example.invalid/AntarusPoFinder-1.2.3.exe.sha256");

            var path = await AppUpdateService.DownloadReleaseAsync(release);
            try { Assert.True(File.Exists(path)); }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public async Task DownloadReleaseAsync_Sha256Mismatch_ThrowsAndDeletesFile()
    {
        // The scenario the whole fix exists for: a downloaded file with a CORRECT byte count (so the
        // existing size check alone would wave it through) but wrong content — e.g. a compromised
        // release asset, or a proxy that swaps the file while keeping Content-Length intact.
        var bytes = System.Text.Encoding.UTF8.GetBytes("real release payload");
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHttpMessageHandler(req =>
                req.RequestUri!.AbsoluteUri.EndsWith(".sha256")
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('a', 64)) } // wrong hash
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));

            var release = new UpdateRelease(new Version(1, 2, 3), "AntarusPoFinder-1.2.3.exe", UpdateSourceKind.GitHub,
                DownloadUrl: "https://example.invalid/AntarusPoFinder-1.2.3.exe", ExpectedSize: bytes.Length,
                Sha256Url: "https://example.invalid/AntarusPoFinder-1.2.3.exe.sha256");

            var tempPath = Path.Combine(Path.GetTempPath(), release.FileName);
            var ex = await Assert.ThrowsAsync<IOException>(() => AppUpdateService.DownloadReleaseAsync(release));

            Assert.Contains("подлинности", ex.Message);
            Assert.False(File.Exists(tempPath)); // must not leave a file that failed integrity check behind
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public async Task DownloadReleaseAsync_NoSha256Url_SkipsVerificationAndSucceeds()
    {
        // Backward compatibility: a release built before this fix has no .sha256 asset at all
        // (Sha256Url stays "" — see ListGitHubReleasesAsync) — must behave exactly as before, not
        // suddenly refuse every old release.
        var bytes = System.Text.Encoding.UTF8.GetBytes("old release, no sha256 asset");
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));

            var release = new UpdateRelease(new Version(1, 2, 3), "AntarusPoFinder-1.2.3.exe", UpdateSourceKind.GitHub,
                DownloadUrl: "https://example.invalid/release.exe", ExpectedSize: bytes.Length);

            var path = await AppUpdateService.DownloadReleaseAsync(release);
            try { Assert.True(File.Exists(path)); }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    // ── SHA256 integrity check (Фикс 4 — folder/network-share source) ──────────────────────────

    [Fact]
    public void VerifyFolderSha256IfPresent_MatchingHash_DoesNotThrow()
    {
        using var root = new TempRoot();
        var exePath = Path.Combine(root.Path, "AntarusPoFinder-1.0.0.exe");
        File.WriteAllText(exePath, "folder release payload");
        var hashHex = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exePath)));
        File.WriteAllText(exePath + ".sha256", hashHex);

        AppUpdateService.VerifyFolderSha256IfPresent(exePath); // must not throw
    }

    [Fact]
    public void VerifyFolderSha256IfPresent_MismatchedHash_Throws()
    {
        using var root = new TempRoot();
        var exePath = Path.Combine(root.Path, "AntarusPoFinder-1.0.0.exe");
        File.WriteAllText(exePath, "folder release payload");
        File.WriteAllText(exePath + ".sha256", new string('a', 64));

        var ex = Assert.Throws<IOException>(() => AppUpdateService.VerifyFolderSha256IfPresent(exePath));
        Assert.Contains("подлинности", ex.Message);
    }

    [Fact]
    public void VerifyFolderSha256IfPresent_NoShaFileNextToIt_DoesNotThrow()
    {
        // Backward compatibility with releases already sitting in the network update folder from
        // before this fix, which never had a .sha256 sibling copied alongside them.
        using var root = new TempRoot();
        var exePath = Path.Combine(root.Path, "AntarusPoFinder-1.0.0.exe");
        File.WriteAllText(exePath, "old-style release, no .sha256 sibling");

        AppUpdateService.VerifyFolderSha256IfPresent(exePath);
    }

    // ── GitHub release listing finds the matching .sha256 asset (Фикс 4) ───────────────────────

    [Fact]
    public async Task CheckForUpdatesAsync_GitHubSource_FindsMatchingSha256Asset()
    {
        const string releasesJson = """
            [
              {
                "tag_name": "v1.2.3",
                "assets": [
                  { "name": "AntarusPoFinder-1.2.3.exe", "browser_download_url": "https://example.invalid/AntarusPoFinder-1.2.3.exe", "size": 123 },
                  { "name": "AntarusPoFinder-1.2.3.exe.sha256", "browser_download_url": "https://example.invalid/AntarusPoFinder-1.2.3.exe.sha256", "size": 64 },
                  { "name": "AntarusPoFinder-1.2.3-setup.msi", "browser_download_url": "https://example.invalid/setup.msi", "size": 999 }
                ]
              }
            ]
            """;
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releasesJson) })));

            var result = await AppUpdateService.CheckForUpdatesAsync(null);

            Assert.Single(result.Releases);
            Assert.Equal("https://example.invalid/AntarusPoFinder-1.2.3.exe.sha256", result.Releases[0].Sha256Url);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_GitHubSource_NoSha256Asset_LeavesSha256UrlEmpty()
    {
        const string releasesJson = """
            [
              {
                "tag_name": "v1.2.3",
                "assets": [
                  { "name": "AntarusPoFinder-1.2.3.exe", "browser_download_url": "https://example.invalid/AntarusPoFinder-1.2.3.exe", "size": 123 }
                ]
              }
            ]
            """;
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releasesJson) })));

            var result = await AppUpdateService.CheckForUpdatesAsync(null);

            Assert.Single(result.Releases);
            Assert.Equal("", result.Releases[0].Sha256Url);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    // ── К релизу приложены посторонние .exe (служба обмена) ───────────────────────────────────
    //
    // Раньше отбор был «первый .exe-ассет релиза», и это ловушка: с v1.74.0.2 рядом с приложением
    // едет antarus-sync.exe (служба обмена для ИТ, tools/sync-server). GitHub отдаёт ассеты в
    // порядке загрузки, то есть посторонний exe вполне может оказаться первым — и приложение
    // подменило бы им само себя на всех машинах. Размер и SHA такую подмену не ловят: файл целый,
    // просто не тот. Отличает их только имя.

    [Fact]
    public async Task CheckForUpdatesAsync_GitHubSource_IgnoresForeignExeAsset()
    {
        const string releasesJson = """
            [
              {
                "tag_name": "v1.2.3",
                "assets": [
                  { "name": "antarus-sync.exe", "browser_download_url": "https://example.invalid/antarus-sync.exe", "size": 9500000 },
                  { "name": "antarus-sync.exe.sha256", "browser_download_url": "https://example.invalid/antarus-sync.exe.sha256", "size": 64 },
                  { "name": "AntarusPoFinder-1.2.3.exe", "browser_download_url": "https://example.invalid/AntarusPoFinder-1.2.3.exe", "size": 123 },
                  { "name": "AntarusPoFinder-1.2.3.exe.sha256", "browser_download_url": "https://example.invalid/AntarusPoFinder-1.2.3.exe.sha256", "size": 64 }
                ]
              }
            ]
            """;
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releasesJson) })));

            var result = await AppUpdateService.CheckForUpdatesAsync(null);

            Assert.Single(result.Releases);
            Assert.Equal("AntarusPoFinder-1.2.3.exe", result.Releases[0].FileName);
            Assert.Equal("https://example.invalid/AntarusPoFinder-1.2.3.exe", result.Releases[0].DownloadUrl);
            Assert.Equal("https://example.invalid/AntarusPoFinder-1.2.3.exe.sha256", result.Releases[0].Sha256Url);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_GitHubSource_ReleaseWithOnlyForeignExe_IsSkipped()
    {
        const string releasesJson = """
            [
              {
                "tag_name": "v1.2.3",
                "assets": [
                  { "name": "antarus-sync.exe", "browser_download_url": "https://example.invalid/antarus-sync.exe", "size": 9500000 }
                ]
              }
            ]
            """;
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releasesJson) })));

            var result = await AppUpdateService.CheckForUpdatesAsync(null);

            Assert.Empty(result.Releases);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    // ── Релиз с неверсионным тегом не виден автообновлению ────────────────────────────────────
    //
    // На этом держится безопасность отдельной ленты релизов службы обмена (тег sync-server-v1.0.0,
    // ассет antarus-sync.exe). Установленные КОПИИ СТАРЫХ ВЕРСИЙ содержат прежний отбор «первый
    // .exe в релизе», и починка в 1.74.0.2 до них не доезжает — они узнают о ней, только обновившись.
    // Единственное, что защищает их от скачивания службы вместо приложения, — то, что релиз с
    // неразбираемым тегом пропускается целиком, ещё до просмотра ассетов.

    [Fact]
    public async Task CheckForUpdatesAsync_GitHubSource_NonVersionTag_IsSkippedBeforeAssets()
    {
        const string releasesJson = """
            [
              {
                "tag_name": "sync-server-v1.0.0",
                "assets": [
                  { "name": "antarus-sync.exe", "browser_download_url": "https://example.invalid/antarus-sync.exe", "size": 9500000 }
                ]
              },
              {
                "tag_name": "v1.2.3",
                "assets": [
                  { "name": "AntarusPoFinder-1.2.3.exe", "browser_download_url": "https://example.invalid/AntarusPoFinder-1.2.3.exe", "size": 123 }
                ]
              }
            ]
            """;
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releasesJson) })));

            var result = await AppUpdateService.CheckForUpdatesAsync(null);

            Assert.Single(result.Releases);
            Assert.Equal("AntarusPoFinder-1.2.3.exe", result.Releases[0].FileName);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    // ── Version/CurrentVersion sanity ────────────────────────────────────────

    [Fact]
    public void CurrentVersion_IsNeverNull()
    {
        // Regression guard for the `?? new Version(0,0,0,0)` fallback — must never throw even if
        // the executing assembly somehow has no version, since every "is there a newer release"
        // comparison in the app depends on this never being null.
        Assert.NotNull(AppUpdateService.CurrentVersion);
    }

    // ── "Что нового" — ShouldShowWhatsNew (чистая функция решения, без WPF/сети) ────────────────

    [Fact]
    public void ShouldShowWhatsNew_LastShownEmpty_ReturnsFalse()
    {
        // Самая первая установка / первый запуск после появления этой фичи на уже существующей
        // установке — показывать нечего "что нового" относительно ничего. Вызывающая сторона
        // (MainWindowViewModel.CheckWhatsNewAsync) в этом случае молча записывает текущую версию.
        Assert.False(AppUpdateService.ShouldShowWhatsNew("", "1.5.0"));
    }

    [Fact]
    public void ShouldShowWhatsNew_LastShownNull_ReturnsFalse()
    {
        Assert.False(AppUpdateService.ShouldShowWhatsNew(null, "1.5.0"));
    }

    [Fact]
    public void ShouldShowWhatsNew_LastShownDiffersFromCurrent_ReturnsTrue()
    {
        // Приложение только что автообновилось: было отмечено на 1.4.0, сейчас уже 1.5.0.
        Assert.True(AppUpdateService.ShouldShowWhatsNew("1.4.0", "1.5.0"));
    }

    [Fact]
    public void ShouldShowWhatsNew_LastShownEqualsCurrent_ReturnsFalse()
    {
        // Уже показывали (или зачли) для этой версии — повторно не нужно.
        Assert.False(AppUpdateService.ShouldShowWhatsNew("1.5.0", "1.5.0"));
    }

    [Fact]
    public void ShouldShowWhatsNew_LastShownEqualsCurrent_CaseInsensitive_ReturnsFalse()
    {
        Assert.False(AppUpdateService.ShouldShowWhatsNew("1.5.0", "1.5.0".ToUpperInvariant()));
    }

    // ── GetReleaseNotesAsync ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetReleaseNotesAsync_ReleaseFound_ReturnsBody()
    {
        const string body = "- Статистика выборов\n- Расширения поиска схем";
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHttpMessageHandler(req =>
            {
                Assert.EndsWith("/releases/tags/v1.5.0", req.RequestUri!.AbsoluteUri);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{ "tag_name": "v1.5.0", "body": {{ToJsonString(body)}} }""")
                };
            })));

            var notes = await AppUpdateService.GetReleaseNotesAsync("1.5.0");

            Assert.Equal(body, notes);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public async Task GetReleaseNotesAsync_ReleaseNotFound_ReturnsNull()
    {
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.NotFound))));

            var notes = await AppUpdateService.GetReleaseNotesAsync("9.9.9");

            Assert.Null(notes);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public async Task GetReleaseNotesAsync_NetworkFailure_ReturnsNullInsteadOfThrowing()
    {
        // Отсутствие сети/недоступный GitHub — не критично для этой фичи: окно «Что нового» просто
        // не покажется, а не роняет запуск приложения исключением.
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(
                new ThrowingHttpMessageHandler(new HttpRequestException("No such host is known"))));

            var notes = await AppUpdateService.GetReleaseNotesAsync("1.5.0");

            Assert.Null(notes);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public async Task GetReleaseNotesAsync_EmptyBody_ReturnsNull()
    {
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "tag_name": "v1.5.0", "body": "" }""")
                })));

            var notes = await AppUpdateService.GetReleaseNotesAsync("1.5.0");

            Assert.Null(notes);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    [Fact]
    public async Task GetReleaseNotesAsync_MissingBodyProperty_ReturnsNull()
    {
        try
        {
            AppUpdateService.SetHttpClientForTests(new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "tag_name": "v1.5.0" }""")
                })));

            var notes = await AppUpdateService.GetReleaseNotesAsync("1.5.0");

            Assert.Null(notes);
        }
        finally { AppUpdateService.ResetHttpClientForTests(); }
    }

    private static string ToJsonString(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
