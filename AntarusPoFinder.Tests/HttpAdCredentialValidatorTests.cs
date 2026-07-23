using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Minimal canned-response HTTP server over a raw TcpListener (same trick as
/// AntarusPoFinder.App.Services.PhotoUploadServer) — no real NTLM handshake is attempted, it just
/// answers the very first request with a fixed status line regardless of headers/body. That's enough
/// to exercise HttpAdCredentialValidator's status-code interpretation (200 → success, 401 → rejected,
/// connection refused → unavailable) without needing a real AD domain/web server, which this sandbox
/// has no access to (see session notes / Task 1 instructions).</summary>
internal sealed class CannedHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _statusCode;
    private readonly string _reason;
    private volatile bool _running = true;

    public int Port { get; }

    public CannedHttpServer(int statusCode, string reason)
    {
        _statusCode = statusCode;
        _reason = reason;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        var thread = new Thread(AcceptLoop) { IsBackground = true };
        thread.Start();
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { return; }
            ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
        }
    }

    private void HandleClient(TcpClient client)
    {
        using var _ = client;
        try
        {
            using var stream = client.GetStream();
            client.ReceiveTimeout = 2000;
            var buffer = new byte[4096];
            try { stream.Read(buffer, 0, buffer.Length); } catch { /* best-effort drain */ }

            var wwwAuth = _statusCode == 401 ? "WWW-Authenticate: NTLM\r\n" : "";
            var resp = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {_statusCode} {_reason}\r\n{wwwAuth}Content-Length: 0\r\nConnection: close\r\n\r\n");
            stream.Write(resp, 0, resp.Length);
        }
        catch { /* best-effort per connection */ }
    }

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch { /* already stopped */ }
    }
}

public class HttpAdCredentialValidatorTests
{
    [Fact]
    public void Validate_ServerReturns200_Succeeds()
    {
        using var server = new CannedHttpServer(200, "OK");
        var validator = new HttpAdCredentialValidator($"http://127.0.0.1:{server.Port}/");

        var status = validator.ValidateWithStatus("Elita", "revkin.i", "pw", out var error);

        Assert.Equal(AdValidationStatus.Success, status);
        Assert.Null(error);
        Assert.True(validator.Validate("Elita", "revkin.i", "pw", out _));
    }

    [Fact]
    public void Validate_ServerReturns401_ReportsInvalidCredentials_NotUnavailable()
    {
        using var server = new CannedHttpServer(401, "Unauthorized");
        var validator = new HttpAdCredentialValidator($"http://127.0.0.1:{server.Port}/");

        var status = validator.ValidateWithStatus("Elita", "revkin.i", "wrongpw", out var error);

        Assert.Equal(AdValidationStatus.InvalidCredentials, status);
        Assert.NotNull(error);
        Assert.False(validator.Validate("Elita", "revkin.i", "wrongpw", out _));
    }

    [Fact]
    public void Validate_NothingListening_ReportsUnavailable_NotInvalidCredentials()
    {
        // Grab a free loopback port, then release it immediately — nothing listens there afterwards,
        // so the connection attempt fails with "connection refused" (a network problem, never a
        // credentials problem).
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var validator = new HttpAdCredentialValidator($"http://127.0.0.1:{port}/");

        var status = validator.ValidateWithStatus("Elita", "revkin.i", "pw", out var error);

        Assert.Equal(AdValidationStatus.Unavailable, status);
        Assert.NotNull(error);
    }

    [Fact]
    public void Validate_EmptyUrl_ReportsUnavailable_WithoutAttemptingNetwork()
    {
        var validator = new HttpAdCredentialValidator("");

        var status = validator.ValidateWithStatus("Elita", "revkin.i", "pw", out var error);

        Assert.Equal(AdValidationStatus.Unavailable, status);
        Assert.Contains("не настроен", error);
    }

    [Fact]
    public void Validate_MalformedUrl_ReportsUnavailable()
    {
        var validator = new HttpAdCredentialValidator("not a url");

        var status = validator.ValidateWithStatus("Elita", "revkin.i", "pw", out var error);

        Assert.Equal(AdValidationStatus.Unavailable, status);
        Assert.NotNull(error);
    }

    // ── IsInsecureUrl (Фикс 5 — сигнал для UI/лога, не блокировка способа) ──────────────────────

    [Theory]
    [InlineData("http://disk.antarus.su/")]
    [InlineData("http://127.0.0.1:8080/")]
    public void IsInsecureUrl_HttpScheme_ReturnsTrue(string url) => Assert.True(HttpAdCredentialValidator.IsInsecureUrl(url));

    [Fact]
    public void IsInsecureUrl_HttpsScheme_ReturnsFalse() =>
        Assert.False(HttpAdCredentialValidator.IsInsecureUrl("https://disk.antarus.su/"));

    [Fact]
    public void IsInsecureUrl_UnparseableUrl_ReturnsFalse() =>
        // Не наше дело здесь решать "плохой URL" — за это отвечает ValidateWithStatus (см.
        // Validate_MalformedUrl_ReportsUnavailable выше), IsInsecureUrl отвечает только про схему
        // валидного URL.
        Assert.False(HttpAdCredentialValidator.IsInsecureUrl("not a url"));
}

/// <summary>Covers способ="оба" (CombinedAdCredentialValidator) with two fakes — no real AD/HTTP
/// needed, just the fallback decision logic itself: primary wins outright on success or a genuine
/// credentials rejection, fallback only gets a turn when primary couldn't even reach its target.</summary>
public class CombinedAdCredentialValidatorTests
{
    private class FakeValidator : IAdCredentialValidator
    {
        private readonly AdValidationStatus _status;
        private readonly string? _error;
        public bool Called { get; private set; }

        public FakeValidator(AdValidationStatus status, string? error = null)
        {
            _status = status;
            _error = error;
        }

        public bool Validate(string domain, string login, string password, out string? error) =>
            ValidateWithStatus(domain, login, password, out error) == AdValidationStatus.Success;

        public AdValidationStatus ValidateWithStatus(string domain, string login, string password, out string? error)
        {
            Called = true;
            error = _status == AdValidationStatus.Success ? null : _error ?? "error";
            return _status;
        }
    }

    [Fact]
    public void PrimarySucceeds_FallbackNeverCalled()
    {
        var primary = new FakeValidator(AdValidationStatus.Success);
        var fallback = new FakeValidator(AdValidationStatus.Success);
        var combined = new CombinedAdCredentialValidator(primary, fallback);

        var status = combined.ValidateWithStatus("d", "l", "p", out _);

        Assert.Equal(AdValidationStatus.Success, status);
        Assert.False(fallback.Called);
    }

    [Fact]
    public void PrimaryRejectsCredentials_FallbackNeverCalled_NotConfusedWithUnavailable()
    {
        var primary = new FakeValidator(AdValidationStatus.InvalidCredentials, "неверный пароль");
        var fallback = new FakeValidator(AdValidationStatus.Success);
        var combined = new CombinedAdCredentialValidator(primary, fallback);

        var status = combined.ValidateWithStatus("d", "l", "p", out var error);

        Assert.Equal(AdValidationStatus.InvalidCredentials, status);
        Assert.False(fallback.Called);
        Assert.Equal("неверный пароль", error);
    }

    [Fact]
    public void PrimaryUnavailable_FallsBackToSecondary()
    {
        var primary = new FakeValidator(AdValidationStatus.Unavailable, "домен недоступен");
        var fallback = new FakeValidator(AdValidationStatus.Success);
        var combined = new CombinedAdCredentialValidator(primary, fallback);

        var status = combined.ValidateWithStatus("d", "l", "p", out _);

        Assert.Equal(AdValidationStatus.Success, status);
        Assert.True(fallback.Called);
    }

    [Fact]
    public void BothUnavailable_ReportsUnavailable_WithBothErrors()
    {
        var primary = new FakeValidator(AdValidationStatus.Unavailable, "LDAP недоступен");
        var fallback = new FakeValidator(AdValidationStatus.Unavailable, "HTTP недоступен");
        var combined = new CombinedAdCredentialValidator(primary, fallback);

        var status = combined.ValidateWithStatus("d", "l", "p", out var error);

        Assert.Equal(AdValidationStatus.Unavailable, status);
        Assert.Contains("LDAP недоступен", error);
        Assert.Contains("HTTP недоступен", error);
    }
}
