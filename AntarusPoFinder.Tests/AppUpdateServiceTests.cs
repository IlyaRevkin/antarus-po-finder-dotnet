using System;
using System.IO;
using System.Net;
using System.Net.Http;
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

    // ── Version/CurrentVersion sanity ────────────────────────────────────────

    [Fact]
    public void CurrentVersion_IsNeverNull()
    {
        // Regression guard for the `?? new Version(0,0,0,0)` fallback — must never throw even if
        // the executing assembly somehow has no version, since every "is there a newer release"
        // comparison in the app depends on this never being null.
        Assert.NotNull(AppUpdateService.CurrentVersion);
    }
}
