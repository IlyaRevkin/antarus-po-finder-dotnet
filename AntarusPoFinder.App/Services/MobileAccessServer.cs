using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Services;

/// <summary>РАЗДАЁТ МОБИЛЬНУЮ СТРАНИЦУ В ЛОКАЛЬНОЙ СЕТИ. Транспорт и только транспорт: что именно
/// показывать, решает <see cref="MobileSite"/> в Core — см. docs/mobile-access-plan.md о том, почему
/// страницу раздаёт сам Finder, а не хостинг.
///
/// <b>Голый TcpListener, а не HttpListener</b> — по той же причине, что и в
/// <see cref="PhotoUploadServer"/>: HTTP.SYS требует прав администратора или резервации URL через
/// netsh на любой префикс, кроме localhost, то есть у обычного пользователя не завёлся бы вовсе.
///
/// <b>Чем отличается от приёма фото, и это главное.</b> Там токен одноразовый и живёт в памяти
/// процесса, поэтому QR приходится сканировать заново на каждое открытие вкладки. Здесь токен
/// ПОСТОЯННЫЙ — его хранит профиль (см. ConfigService.MobileAccessToken) — и адрес не меняется:
/// телефон добавляет закладку один раз. Это прямая просьба Ильи «чтобы без qr»; то, что компьютер
/// при этом всё равно должен быть включён, этим не лечится и вынесено развилкой в план.
///
/// <b>Только GET и только чтение.</b> Ни одной записи, ни одного файла с диска наружу: первый слой
/// отвечает на вопрос «актуальна ли прошивка», а не выдаёт прошивку. Поэтому и разбора тела запроса
/// здесь нет — тело, если оно пришло, вычитывается и выбрасывается.
///
/// <b>База читается чужим потоком.</b> <see cref="Database"/> держит одно соединение SQLite без
/// блокировок, поэтому сервер не ходит в базу сам: он зовёт <see cref="_render"/>, а страница,
/// которая его создала, заворачивает этот вызов в Dispatcher. Запросов с одного телефона единицы,
/// так что цена такой переброски нулевая.</summary>
public sealed class MobileAccessServer : IDisposable
{
    /// <summary>Длина тела, после которой запрос отбрасывается без разбора. Мобильному доступу тело
    /// не нужно вовсе, но заголовок Content-Length может прийти от кого угодно — читать по нему
    /// сколько сказано значило бы отдать память первому встречному в той же Wi-Fi сети.</summary>
    private const int MaxBodyBytes = 64 * 1024;

    /// <summary>Сколько байт файла карты ВВ соглашаемся отдать. Карта — это лист Excel или
    /// несколько страниц PDF, то есть единицы мегабайт; файл на полсотни мегабайт в папке карты
    /// почти наверняка попал туда по ошибке, а телефон на объекте качал бы его по чужому Wi-Fi
    /// столько, что успел бы передумать.</summary>
    private const long MaxFileBytes = 32L * 1024 * 1024;

    private readonly TcpListener _listener;
    private readonly Func<string, string, MobileSite.Page> _render;
    private readonly Func<int, IoMapDoc?>? _ioMap;
    private volatile bool _running;

    public int Port { get; }

    /// <summary>Токен в пути — единственное, что отличает телефон сервисника от любого другого
    /// устройства в той же сети. Постоянный (приходит из профиля), поэтому ссылка переживает
    /// перезапуск программы.</summary>
    public string Token { get; }

    /// <summary>Полный адрес, который человек заносит в закладки телефона.</summary>
    public string Url { get; }

    /// <summary>Сколько страниц отдано с момента запуска. Нужно только интерфейсу — увидеть, что
    /// телефон вообще достучался, иначе «не работает» и «никто не заходил» неразличимы.</summary>
    public int Served => _served;
    private int _served;

    /// <summary>Сработало (в фоновом потоке) после каждого отданного ответа.</summary>
    public event Action? Request;

    /// <param name="render">Путь после токена и query-строка → страница. Вызывается ИЗ ПОТОКА
    /// СЕРВЕРА: вызывающий обязан сам перебросить это туда, где живёт база.</param>
    /// <param name="ioMap">Карта ВВ версии. Нужна здесь, а не только отрисовке: по адресу
    /// <see cref="MobileSite.FileRoute"/> отдаётся САМ файл, то есть байты с диска, а разметка
    /// байтов отдавать не умеет и не должна. Вызывается так же, как <paramref name="render"/>, —
    /// из потока сервера, и перебросить это к базе обязан вызывающий.</param>
    public MobileAccessServer(int port, string token, Func<string, string, MobileSite.Page> render,
        Func<int, IoMapDoc?>? ioMap = null)
    {
        _render = render;
        _ioMap = ioMap;
        Token = token;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        // Реальный порт из сокета, а не параметр: при port=0 (тесты) его выбирает ОС, и узнать его
        // можно только после Start().
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Url = $"http://{LocalIp()}:{Port}/{Token}/";

        _running = true;
        new Thread(ServeLoop) { IsBackground = true }.Start();
    }

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch { /* уже остановлен */ }
    }

    private void ServeLoop()
    {
        while (_running)
        {
            TcpClient client;
            // Dispose() останавливает слушателя, и ожидающий Accept из-за этого бросает — это и есть
            // штатный выход из цикла, а не ошибка.
            try { client = _listener.AcceptTcpClient(); }
            catch { return; }
            ThreadPool.QueueUserWorkItem(_ => Handle(client));
        }
    }

    private void Handle(TcpClient client)
    {
        using var _ = client;
        try
        {
            using var stream = client.GetStream();

            var header = ReadHeader(stream);
            if (header is null) return;

            var lines = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;

            var parts = lines[0].Split(' ');
            var method = parts.Length > 0 ? parts[0] : "GET";
            var rawPath = parts.Length > 1 ? parts[1] : "/";

            var contentLength = 0;
            foreach (var line in lines.Skip(1))
            {
                var idx = line.IndexOf(':');
                if (idx < 0) continue;
                if (line[..idx].Trim().Equals("content-length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line[(idx + 1)..].Trim(), out contentLength);
            }
            // Тело здесь не нужно никому, но оставить его непрочитанным в сокете значит с большой
            // вероятностью отдать клиенту TCP RST вместо ответа — он увидит обрыв связи вместо
            // текста. Поэтому вычитываем и выбрасываем; всё, что длиннее предела, отбрасываем вместе
            // с соединением, не выделяя под него ничего.
            if (contentLength > MaxBodyBytes) return;
            if (contentLength > 0) Drain(stream, contentLength);

            var (path, query) = SplitPath(rawPath);
            var pathToken = path.Split('/', 2)[0];
            if (!string.Equals(pathToken, Token, StringComparison.Ordinal))
            {
                Write(stream, 403, "text/plain; charset=utf-8", "Доступ запрещён.");
                return;
            }
            if (method != "GET" && method != "HEAD")
            {
                Write(stream, 405, "text/plain; charset=utf-8", "Только чтение.");
                return;
            }

            var tail = path.Length > pathToken.Length ? path[(pathToken.Length + 1)..] : "";

            // Файл карты ВВ — единственный ответ не HTML-ом, поэтому он разбирается ДО отрисовки.
            if (MobileSite.TryParseFileRoute(tail, out var fileId))
            {
                WriteIoMapFile(stream, fileId);
                Interlocked.Increment(ref _served);
                Request?.Invoke();
                return;
            }

            var page = _render(tail, query);
            Write(stream, page.Status, "text/html; charset=utf-8", page.Html);

            Interlocked.Increment(ref _served);
            Request?.Invoke();
        }
        catch { /* best-effort на соединение, как и в PhotoUploadServer */ }
    }

    /// <summary>Отдать телефону сам файл карты ВВ. Читается целиком в память и только потом
    /// пишется в сокет: файл лежит на сетевом диске, и отдавать его поблочно означало бы держать
    /// открытым хэндл к шаре всё время медленной передачи на телефон, а размер здесь ограничен
    /// <see cref="MaxFileBytes"/> — лишней памяти это не стоит.</summary>
    private void WriteIoMapFile(NetworkStream stream, int fwVersionId)
    {
        var doc = _ioMap?.Invoke(fwVersionId);
        if (doc is null)
        {
            Write(stream, 404, "text/html; charset=utf-8",
                MobileSite.Message("Карта ВВ", "Файла карты больше нет — возможно, его убрали с диска."));
            return;
        }
        if (doc.SizeBytes > MaxFileBytes)
        {
            Write(stream, 413, "text/html; charset=utf-8", MobileSite.Message("Карта ВВ",
                $"Файл слишком большой для телефона ({doc.SizeBytes / 1024 / 1024} МБ) — откройте его с компьютера."));
            return;
        }

        byte[] bytes;
        try { bytes = File.ReadAllBytes(doc.FullPath); }
        catch (Exception)
        {
            Write(stream, 404, "text/html; charset=utf-8",
                MobileSite.Message("Карта ВВ", "Файл сейчас не читается — сетевой диск недоступен или занят."));
            return;
        }

        try
        {
            // inline, а не attachment: PDF и картинку телефон обязан показать сам, не кладя в
            // «Загрузки». Книгу Excel он всё равно отдаст приложению — для неё разницы нет.
            // Имя файла русское, поэтому только filename* (RFC 5987): голый filename= с кириллицей
            // браузеры читают по-разному, и имя приезжало бы крокозябрами.
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: {ContentType(doc.FileName)}\r\n" +
                $"Content-Disposition: inline; filename*=UTF-8''{Uri.EscapeDataString(doc.FileName)}\r\n" +
                $"Cache-Control: no-store\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            stream.Write(header, 0, header.Length);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        catch { /* best-effort: телефон ушёл из сети посреди передачи */ }
    }

    private static string ContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".xlsm" => "application/vnd.ms-excel.sheet.macroEnabled.12",
        ".xls" => "application/vnd.ms-excel",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".doc" => "application/msword",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".txt" => "text/plain; charset=utf-8",
        // Неизвестное расширение отдаём потоком байт, а не угадываем: угаданный неверно тип —
        // это телефон, пытающийся нарисовать архив картинкой.
        _ => "application/octet-stream",
    };

    /// <summary>Путь без ведущих слэшей и query-строка отдельно. Отдельным методом (и публичным)
    /// ради тестов: «/токен», «/токен/», «/токен/v/7?q=1» обязаны разбираться одинаково.</summary>
    public static (string Path, string Query) SplitPath(string rawPath)
    {
        var split = rawPath.Split('?', 2);
        return (split[0].Trim('/'), split.Length > 1 ? split[1] : "");
    }

    private static string? ReadHeader(NetworkStream stream)
    {
        var buffer = new List<byte>();
        while (buffer.Count < 16384)
        {
            var b = stream.ReadByte();
            if (b < 0) return null;
            buffer.Add((byte)b);
            var n = buffer.Count;
            if (n >= 4 && buffer[n - 4] == 13 && buffer[n - 3] == 10 && buffer[n - 2] == 13 && buffer[n - 1] == 10)
                return Encoding.ASCII.GetString(buffer.ToArray());
        }
        return null;
    }

    private static void Drain(NetworkStream stream, int length)
    {
        try
        {
            var buffer = new byte[Math.Min(length, 8192)];
            var remaining = length;
            while (remaining > 0)
            {
                var n = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (n <= 0) break;
                remaining -= n;
            }
        }
        catch { /* best-effort, ответ всё равно пишется следом */ }
    }

    private static void Write(NetworkStream stream, int status, string contentType, string body)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: {contentType}\r\n" +
                // Страница считается по живой базе, и закэшированный телефоном ответ показал бы
                // вчерашнюю «актуальную версию» — ровно то, ради чего всё и затевалось.
                $"Cache-Control: no-store\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            stream.Write(header, 0, header.Length);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        catch { /* best-effort */ }
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        _ => "OK",
    };

    /// <summary>Адрес этой машины в той сети, через которую она выходит наружу. Петля в ответе
    /// означает, что сети нет вовсе, — ссылку в этом случае телефон не откроет, и это видно по
    /// самому адресу, отдельного сообщения не нужно.</summary>
    private static string LocalIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
        }
        catch { return "127.0.0.1"; }
    }
}
