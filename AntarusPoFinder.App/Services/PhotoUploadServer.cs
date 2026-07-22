using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AntarusPoFinder.App.Services;

/// <summary>Lets a phone on the same LAN upload photos into a folder via a QR code, running for as
/// long as the Осмотр tab is open (persistent panel, not a one-off dialog). Uses a raw TcpListener
/// (not HttpListener) deliberately: HttpListener's HTTP.SYS backend requires admin rights or a
/// netsh URL-ACL reservation for any non-localhost prefix, which would break this for ordinary
/// users — a plain socket mirrors Python's http.server, which has no such restriction.</summary>
public sealed class PhotoUploadServer : IDisposable
{
    private readonly TcpListener _listener;
    private volatile bool _running;
    private volatile string _uploadDir;

    public int Port { get; }
    public string Url { get; }

    /// <summary>Fired (on a background thread) with the number of files saved in one upload request.</summary>
    public event Action<int>? FilesUploaded;

    public PhotoUploadServer(string uploadDir, int port)
    {
        _uploadDir = uploadDir;
        Port = port;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        Url = $"http://{GetLocalIp()}:{port}";

        _running = true;
        var thread = new Thread(ServeLoop) { IsBackground = true };
        thread.Start();
    }

    public void SetUploadDir(string dir) => _uploadDir = dir;

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch { /* already stopped */ }
    }

    private void ServeLoop()
    {
        while (_running)
        {
            TcpClient client;
            // Dispose() above stops the listener, which makes a pending/next AcceptTcpClient throw —
            // that's the intended way to unblock and exit this loop, not an error to report.
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

            var headerBytes = ReadUntilDoubleCrlf(stream);
            if (headerBytes is null) return;

            var headerText = Encoding.ASCII.GetString(headerBytes);
            var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;

            var requestLine = lines[0].Split(' ');
            var method = requestLine.Length > 0 ? requestLine[0] : "GET";

            int contentLength = 0;
            string contentType = "";
            foreach (var line in lines.Skip(1))
            {
                var idx = line.IndexOf(':');
                if (idx < 0) continue;
                var key = line[..idx].Trim().ToLowerInvariant();
                var val = line[(idx + 1)..].Trim();
                if (key == "content-length") int.TryParse(val, out contentLength);
                else if (key == "content-type") contentType = val;
            }

            var body = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = stream.Read(body, read, contentLength - read);
                if (n <= 0) break;
                read += n;
            }

            string html;
            if (method == "POST")
            {
                int count = 0;
                var boundaryIdx = contentType.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
                if (contentType.Contains("multipart/form-data") && boundaryIdx >= 0)
                {
                    var boundary = "--" + contentType[(boundaryIdx + "boundary=".Length)..].Trim().Trim('"');
                    count = SaveMultipartFiles(body, boundary, _uploadDir);
                }
                html = $"""
                    <!doctype html><html><head><meta charset="utf-8"></head>
                    <body style="font-family:sans-serif;text-align:center;padding:24px">
                    <h2>Загружено: {count}</h2><a href="/">Ещё</a></body></html>
                    """;
                if (count > 0) FilesUploaded?.Invoke(count);
            }
            else
            {
                html = """
                    <!doctype html><html><head><meta charset="utf-8">
                    <meta name="viewport" content="width=device-width, initial-scale=1">
                    <title>Загрузка фото</title></head>
                    <body style="font-family:sans-serif;text-align:center;padding:24px">
                    <h2>Загрузка фото</h2>
                    <form method="post" enctype="multipart/form-data">
                      <input type="file" name="files" multiple accept="image/*"><br><br>
                      <button type="submit">Отправить</button>
                    </form></body></html>
                    """;
            }

            var respBytes = Encoding.UTF8.GetBytes(html);
            var responseHeader = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {respBytes.Length}\r\nConnection: close\r\n\r\n");
            stream.Write(responseHeader, 0, responseHeader.Length);
            stream.Write(respBytes, 0, respBytes.Length);
            stream.Flush();
        }
        catch { /* best-effort per connection, matches Python's tolerant request handling */ }
    }

    private static byte[]? ReadUntilDoubleCrlf(NetworkStream stream)
    {
        var buffer = new System.Collections.Generic.List<byte>();
        Span<byte> last4 = stackalloc byte[4];
        int filled = 0;
        while (buffer.Count < 65536)
        {
            int b = stream.ReadByte();
            if (b < 0) return null;
            buffer.Add((byte)b);
            if (filled < 4) { last4[filled] = (byte)b; filled++; }
            else { last4[0] = last4[1]; last4[1] = last4[2]; last4[2] = last4[3]; last4[3] = (byte)b; }
            if (filled == 4 && last4[0] == 13 && last4[1] == 10 && last4[2] == 13 && last4[3] == 10)
                return buffer.ToArray();
        }
        return null;
    }

    private static int SaveMultipartFiles(byte[] body, string boundary, string uploadDir)
    {
        var boundaryBytes = Encoding.Latin1.GetBytes(boundary);
        int count = 0;
        int start = IndexOf(body, boundaryBytes, 0);
        while (start >= 0)
        {
            int partStart = start + boundaryBytes.Length;
            int next = IndexOf(body, boundaryBytes, partStart);
            if (next < 0) break;

            var part = body[partStart..next];
            var headerEnd = IndexOf(part, new byte[] { 13, 10, 13, 10 });
            if (headerEnd >= 0)
            {
                var headerStr = Encoding.Latin1.GetString(part, 0, headerEnd);
                var fnIdx = headerStr.IndexOf("filename=\"", StringComparison.OrdinalIgnoreCase);
                if (fnIdx >= 0)
                {
                    var s = fnIdx + "filename=\"".Length;
                    var eIdx = headerStr.IndexOf('"', s);
                    if (eIdx > s)
                    {
                        var filename = headerStr[s..eIdx];
                        if (!string.IsNullOrEmpty(filename))
                        {
                            var fileBody = part[(headerEnd + 4)..];
                            if (fileBody.Length >= 2 && fileBody[^2] == 13 && fileBody[^1] == 10)
                                fileBody = fileBody[..^2];

                            Directory.CreateDirectory(uploadDir);
                            var dest = Path.Combine(uploadDir, Path.GetFileName(filename));
                            if (File.Exists(dest))
                                dest = Path.Combine(uploadDir, $"{Path.GetFileNameWithoutExtension(filename)}_{DateTime.Now:HHmmss}{Path.GetExtension(filename)}");
                            File.WriteAllBytes(dest, fileBody);
                            count++;
                        }
                    }
                }
            }
            start = next;
        }
        return count;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from = 0)
    {
        if (needle.Length == 0 || haystack.Length - from < needle.Length) return -1;
        for (int i = from; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    private static string GetLocalIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
        }
        // No network/route to the outside — falls back to loopback, same as the address failing to
        // resolve above; the QR code just won't be reachable from a phone, which is self-evident
        // from the URL shown rather than needing a separate error.
        catch { return "127.0.0.1"; }
    }
}
