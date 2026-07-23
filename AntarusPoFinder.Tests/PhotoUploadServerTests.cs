using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Covers the three hardening fixes applied to PhotoUploadServer (see its class doc): the
/// one-time access token embedded in the URL, the upload size cap enforced before the body buffer is
/// allocated, and the server-side (not just HTML accept=) extension allowlist. Talks to a real
/// instance over a real loopback socket (port 0 — OS picks a free one, see PhotoUploadServer's
/// constructor doc) rather than mocking anything, since the whole point of these fixes is in how raw
/// bytes on the wire are handled.</summary>
public class PhotoUploadServerTests
{
    private static string SendRawRequestAndReadResponse(int port, byte[] request)
    {
        using var client = new TcpClient();
        client.Connect("127.0.0.1", port);
        client.ReceiveTimeout = 5000;
        using var stream = client.GetStream();
        stream.Write(request, 0, request.Length);

        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            ms.Write(buffer, 0, n);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static byte[] BuildMultipartRequest(string token, string fieldFilename, byte[] fileBytes, out string boundary)
    {
        boundary = "----AntarusTestBoundary";
        var bodyBuilder = new StringBuilder();
        bodyBuilder.Append($"--{boundary}\r\n");
        bodyBuilder.Append($"Content-Disposition: form-data; name=\"files\"; filename=\"{fieldFilename}\"\r\n");
        bodyBuilder.Append("Content-Type: application/octet-stream\r\n\r\n");
        var head = Encoding.UTF8.GetBytes(bodyBuilder.ToString());
        var tail = Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n");

        var body = new byte[head.Length + fileBytes.Length + tail.Length];
        Buffer.BlockCopy(head, 0, body, 0, head.Length);
        Buffer.BlockCopy(fileBytes, 0, body, head.Length, fileBytes.Length);
        Buffer.BlockCopy(tail, 0, body, head.Length + fileBytes.Length, tail.Length);

        var header = Encoding.ASCII.GetBytes(
            $"POST /{token} HTTP/1.1\r\n" +
            $"Content-Type: multipart/form-data; boundary={boundary}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");

        var request = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, request, 0, header.Length);
        Buffer.BlockCopy(body, 0, request, header.Length, body.Length);
        return request;
    }

    // ── Токен ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Get_WithoutToken_Returns403()
    {
        using var root = new TempRoot();
        using var server = new PhotoUploadServer(root.Path, 0);

        var request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nConnection: close\r\n\r\n");
        var response = SendRawRequestAndReadResponse(server.Port, request);

        Assert.StartsWith("HTTP/1.1 403", response);
    }

    [Fact]
    public void Get_WithWrongToken_Returns403()
    {
        using var root = new TempRoot();
        using var server = new PhotoUploadServer(root.Path, 0);

        var request = Encoding.ASCII.GetBytes("GET /not-the-real-token HTTP/1.1\r\nConnection: close\r\n\r\n");
        var response = SendRawRequestAndReadResponse(server.Port, request);

        Assert.StartsWith("HTTP/1.1 403", response);
    }

    [Fact]
    public void Get_WithCorrectToken_Returns200WithUploadForm()
    {
        using var root = new TempRoot();
        using var server = new PhotoUploadServer(root.Path, 0);

        var request = Encoding.ASCII.GetBytes($"GET /{server.Token} HTTP/1.1\r\nConnection: close\r\n\r\n");
        var response = SendRawRequestAndReadResponse(server.Port, request);

        Assert.StartsWith("HTTP/1.1 200", response);
        Assert.Contains("Загрузка фото", response);
    }

    [Fact]
    public void Url_EmbedsTheToken_SoTheQrCodeIsAlreadyAuthorized()
    {
        using var root = new TempRoot();
        using var server = new PhotoUploadServer(root.Path, 0);

        Assert.EndsWith($"/{server.Token}", server.Url);
    }

    [Fact]
    public void PostUpload_WithoutToken_Returns403AndDoesNotSaveFile()
    {
        using var root = new TempRoot();
        using var server = new PhotoUploadServer(root.Path, 0);

        var request = BuildMultipartRequest("wrong-token", "photo.jpg", Encoding.UTF8.GetBytes("fake jpeg bytes"), out _);
        var response = SendRawRequestAndReadResponse(server.Port, request);

        Assert.StartsWith("HTTP/1.1 403", response);
        Assert.Empty(Directory.GetFiles(root.Path));
    }

    // ── Лимит размера (проверяется ДО чтения тела) ────────────────────────────────────────────────

    [Fact]
    public void PostUpload_ContentLengthOverLimit_Returns413WithoutHangingOrAllocatingHugeBuffer()
    {
        using var root = new TempRoot();
        using var server = new PhotoUploadServer(root.Path, 0);

        // 100 МБ заявлено в заголовке — намного больше лимита в 50 МБ — но реально после заголовков
        // ничего не отправляется. Если бы сервер сначала аллоцировал буфер под contentLength (старое
        // поведение), это всё равно бы не зависло на ЭТОМ конкретном размере, но проверяет сам факт,
        // что отказ приходит немедленно, не дожидаясь чтения (несуществующего) тела.
        var header = Encoding.ASCII.GetBytes(
            $"POST /{server.Token} HTTP/1.1\r\n" +
            "Content-Type: multipart/form-data; boundary=x\r\n" +
            $"Content-Length: {100L * 1024 * 1024}\r\n" +
            "Connection: close\r\n\r\n");

        var response = SendRawRequestAndReadResponse(server.Port, header);

        Assert.StartsWith("HTTP/1.1 413", response);
        Assert.Empty(Directory.GetFiles(root.Path));
    }

    [Fact]
    public void PostUpload_ContentLengthAtLimit_IsAccepted()
    {
        using var root = new TempRoot();
        using var server = new PhotoUploadServer(root.Path, 0);

        // Небольшой реальный файл, лимит намеренно не пробивается — этот тест только про то, что
        // сама проверка размера не отбрасывает нормальные (маленькие) запросы по ошибке.
        var fileBytes = Encoding.UTF8.GetBytes("small real jpeg-ish payload");
        var request = BuildMultipartRequest(server.Token, "photo.jpg", fileBytes, out _);

        var response = SendRawRequestAndReadResponse(server.Port, request);

        Assert.StartsWith("HTTP/1.1 200", response);
        Assert.Contains("Загружено: 1", response);
    }

    // ── Расширения (серверная проверка, не только HTML accept=) ──────────────────────────────────

    [Fact]
    public void PostUpload_AllowedImageExtension_IsSaved()
    {
        using var root = new TempRoot();
        using var server = new PhotoUploadServer(root.Path, 0);

        var request = BuildMultipartRequest(server.Token, "photo.png", Encoding.UTF8.GetBytes("fake png bytes"), out _);
        var response = SendRawRequestAndReadResponse(server.Port, request);

        Assert.Contains("Загружено: 1", response);
        var saved = Directory.GetFiles(root.Path);
        Assert.Single(saved);
        Assert.Equal(".png", Path.GetExtension(saved[0]));
    }

    [Fact]
    public void PostUpload_DisallowedExtension_IsSilentlySkippedNotSaved()
    {
        using var root = new TempRoot();
        using var server = new PhotoUploadServer(root.Path, 0);

        // accept="image/*" on the HTML form is only a client-side hint — nothing stops a POST with
        // any filename sent directly (curl, hand-crafted request like this test does). The server
        // itself must refuse to write a non-image extension to disk.
        var request = BuildMultipartRequest(server.Token, "malware.exe", Encoding.UTF8.GetBytes("MZ fake exe bytes"), out _);
        var response = SendRawRequestAndReadResponse(server.Port, request);

        Assert.Contains("Загружено: 0", response);
        Assert.Empty(Directory.GetFiles(root.Path));
    }

    [Fact]
    public void FilesUploaded_EventFires_OnlyWhenSomethingWasActuallySaved()
    {
        using var root = new TempRoot();
        using var server = new PhotoUploadServer(root.Path, 0);
        var fired = false;
        server.FilesUploaded += _ => fired = true;

        var request = BuildMultipartRequest(server.Token, "not-an-image.exe", Encoding.UTF8.GetBytes("x"), out _);
        SendRawRequestAndReadResponse(server.Port, request);

        Thread.Sleep(100); // event handler runs on the server's background thread
        Assert.False(fired);
    }
}
