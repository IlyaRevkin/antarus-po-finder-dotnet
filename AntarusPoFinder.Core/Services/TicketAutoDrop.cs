using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>ТИКЕТЫ УЕЗЖАЮТ САМИ, БЕЗ КНОПКИ «ВЫГРУЗИТЬ…».
///
/// <b>Зачем.</b> Выгрузка тикетов была ручной: человек должен был вспомнить про кнопку, выбрать
/// отбор и дождаться отправки. Ровно про такое участие Илья уже просил один раз — про журналы
/// заливок: «сделай чтобы логи дошли и от меня ничего не требовалось». С тикетами то же самое:
/// жалоба нужна тому, кто чинит программу, а он сидит вне конторской сети и до сетевого диска с
/// тикетами не достаёт. Не нажали кнопку — жалобы для него не существует.
///
/// <b>Почему не хватает обмена через бакет (TicketStorageSync).</b> Он требует ключей от S3, а они
/// заводятся файлом руками и есть не на каждой машине — это уже выяснилось с журналами заливок
/// (21.10.2026, на машине Ильи ключей в профиле не оказалось). Канал, который молчит там, где нет
/// ключей, не годится: жалоба приходит как раз с той машины, где что-то не настроено.
///
/// <b>Как это работает.</b> Устроено ровно как <see cref="LoaderDiagnostics"/>, и нарочно: каждое
/// событие тикета (завели, сменили статус, написали реплику) ложится отдельным файлом в очередь на
/// диске, а ближайший проход синхронизации молча выкладывает очередь на страницу приёма файлов с
/// полем <c>silent=1</c> — телеграм при этом молчит, файл ложится в подпапку diag/. Ни одного
/// всплывающего окна: фоновая работа обязана быть не слышна.
///
/// <b>Почему очередь на диске, а не отправка сразу.</b> Тикет заводят в том числе тогда, когда
/// всё отвалилось, — сети в этот момент может не быть вовсе. Отправка «сейчас или никогда»
/// потеряла бы самое ценное. Запись маленькая, очередь ограничена сверху (<see cref="MaxPending"/>).
///
/// <b>Вложения сюда не едут.</b> Скриншоты и логи остаются на сетевом диске конторы: они бывают по
/// сотне мегабайт, а страница приёма ограничена по размеру. Для них по-прежнему есть ручная кнопка
/// «Выгрузить…» — она собирает архив вместе с вложениями.</summary>
public static class TicketAutoDrop
{
    /// <summary>Сколько записей держим, если сети долго нет. Больше, чем у журналов заливок: тикет
    /// — это слова человека, их не восстановить ничем, тогда как заливка хотя бы повторится.</summary>
    public const int MaxPending = 300;

    /// <summary>Что именно произошло с тикетом. Строками, а не перечислением: запись уезжает json'ом
    /// и читается глазами на той стороне.</summary>
    public static class Event
    {
        /// <summary>Тикет завели.</summary>
        public const string Created = "created";
        /// <summary>Сменили статус (взяли в работу, закрыли, переоткрыли).</summary>
        public const string Status = "status";
        /// <summary>Написали реплику в переписке по тикету.</summary>
        public const string Comment = "comment";
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Без этого кириллица уезжает в \uXXXX: формально верно, но читать такое глазами нельзя,
        // а читают эти записи именно глазами.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Одно событие тикета целиком. Поля — то, чего не восстановить по тексту жалобы: с
    /// какой машины, от какой версии программы и в каком состоянии тикет на этот момент.</summary>
    public sealed record Entry
    {
        [JsonPropertyName("schema")] public int Schema { get; init; } = 1;
        [JsonPropertyName("at")] public string At { get; init; } = "";
        [JsonPropertyName("event")] public string EventType { get; init; } = Event.Created;
        [JsonPropertyName("machine")] public string Machine { get; init; } = "";
        [JsonPropertyName("user")] public string User { get; init; } = "";
        [JsonPropertyName("role")] public string Role { get; init; } = "";
        [JsonPropertyName("appVersion")] public string AppVersion { get; init; } = "";

        [JsonPropertyName("ticketId")] public string TicketId { get; init; } = "";
        [JsonPropertyName("type")] public string Type { get; init; } = "";
        [JsonPropertyName("typeLabel")] public string TypeLabel { get; init; } = "";
        [JsonPropertyName("status")] public string Status { get; init; } = "";
        [JsonPropertyName("statusLabel")] public string StatusLabel { get; init; } = "";
        [JsonPropertyName("severity")] public string Severity { get; init; } = "";
        [JsonPropertyName("fwSyncId")] public string FwSyncId { get; init; } = "";
        [JsonPropertyName("fwLabel")] public string FwLabel { get; init; } = "";
        [JsonPropertyName("createdBy")] public string CreatedBy { get; init; } = "";
        [JsonPropertyName("createdAt")] public string CreatedAt { get; init; } = "";
        [JsonPropertyName("text")] public string Text { get; init; } = "";

        /// <summary>Текст реплики — только у события <see cref="Event.Comment"/>.</summary>
        [JsonPropertyName("comment")] public string? Comment { get; init; }
    }

    /// <summary>Собрать запись по тикету. Отдельным методом, чтобы все заводчики тикетов (страница
    /// «Тикеты», жучок на карточке, «Проверка компьютера») клали в очередь одно и то же: разъехавшиеся
    /// копии этой сборки рано или поздно разошлись бы в мелочах — так уже было с самим созданием
    /// тикета, см. TicketSyncService.CreateTicket.</summary>
    public static Entry EntryFor(Ticket ticket, string eventType, string machine, string user,
        string role, string appVersion, string? comment = null) => new()
        {
            At = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            EventType = eventType,
            Machine = machine,
            User = user,
            Role = role,
            AppVersion = appVersion,
            TicketId = ticket.Id,
            Type = ticket.Type,
            TypeLabel = TicketType.Label(ticket.Type),
            Status = ticket.Status,
            StatusLabel = TicketStatus.Label(ticket.Status),
            Severity = ticket.Severity,
            FwSyncId = ticket.FwSyncId,
            FwLabel = ticket.FwLabel,
            CreatedBy = ticket.CreatedBy,
            CreatedAt = ticket.CreatedAt,
            Text = ticket.Text,
            Comment = comment,
        };

    /// <summary>Папка очереди внутри профиля приложения. Отдельная от очереди журналов заливок:
    /// это разные потоки с разной судьбой, и смешав их, нельзя было бы вычистить один, не тронув
    /// второй.</summary>
    public static string QueueFolder(string appDataFolder) =>
        Path.Combine(appDataFolder, "tickets", "pending");

    /// <summary>Положить событие в очередь. Ничего не бросает: автовыгрузка не имеет права помешать
    /// самому заведению тикета — человек жаловался не на неё.</summary>
    public static string? Enqueue(string appDataFolder, Entry entry)
    {
        try
        {
            var dir = QueueFolder(appDataFolder);
            Directory.CreateDirectory(dir);

            // Имя начинается с даты: по нему и строится порядок очереди, и день в имени виден на
            // той стороне, куда файл приедет.
            var name = $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json";
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, JsonSerializer.Serialize(entry, Json), new UTF8Encoding(false));
            Trim(dir);
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Вытеснить самые старые записи, если очередь переросла <see cref="MaxPending"/>.</summary>
    public static void Trim(string queueFolder)
    {
        try
        {
            var files = Pending(queueFolder);
            foreach (var extra in files.Take(Math.Max(0, files.Count - MaxPending)))
                File.Delete(extra);
        }
        catch (Exception) { /* очередь автовыгрузки не повод падать */ }
    }

    /// <summary>Очередь, от старых к новым.</summary>
    public static List<string> Pending(string queueFolder)
    {
        try
        {
            if (!Directory.Exists(queueFolder)) return new List<string>();
            return Directory.EnumerateFiles(queueFolder, "*.json")
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    public sealed record FlushResult(int Sent, int Failed, string? Error)
    {
        public bool Ok => Error is null;
    }

    /// <summary>Выложить очередь через страницу приёма и стереть выложенное.
    ///
    /// Адрес и код — те же, что у журналов заливок (ConfigService.DiagnosticsDropUrl/Code): канал
    /// один, и заводить второй набор настроек ради той же страницы значит рано или поздно
    /// обнаружить, что один из двух сменили, а второй забыли.
    ///
    /// Удаление ТОЛЬКО после подтверждённой отправки: иначе первая же ошибка сети стирала бы ровно
    /// то, ради чего всё и заведено. Первая неудача прекращает проход — если страница недоступна,
    /// она недоступна и для второго файла.</summary>
    public static async Task<FlushResult> FlushAsync(
        string appDataFolder, string dropUrl, string dropCode,
        HttpClient? http = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dropUrl) || string.IsNullOrWhiteSpace(dropCode))
            return new FlushResult(0, 0, null);

        var dir = QueueFolder(appDataFolder);
        var files = Pending(dir);
        if (files.Count == 0) return new FlushResult(0, 0, null);

        var client = http ?? Shared;
        var sent = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            byte[] body;
            try { body = File.ReadAllBytes(file); }
            catch (IOException) { continue; }                       // кто-то держит файл — вернёмся позже

            try
            {
                using var form = new MultipartFormDataContent();
                // Поле «code» идёт ПЕРВЫМ: страница сверяет его до чтения тела файла.
                form.Add(new StringContent(dropCode), "code");
                form.Add(new StringContent("тикет (автоматически)"), "comment");
                // ТИХО: страница приёма не шлёт телеграм-уведомление и кладёт файл в подпапку diag/.
                // Тикеты заводят пачками, и десяток сообщений о том, чего человек не просил, — это
                // ровно то «участие», от которого и уходим.
                form.Add(new StringContent("1"), "silent");

                var part = new ByteArrayContent(body);
                part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                form.Add(part, "file", Path.GetFileName(file));

                using var response = await client.PostAsync(dropUrl, form, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return new FlushResult(sent, files.Count - sent, $"страница приёма ответила {(int)response.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return new FlushResult(sent, files.Count - sent, ex.Message);
            }

            try { File.Delete(file); } catch (IOException) { /* удалится в следующий проход */ }
            sent++;
        }

        return new FlushResult(sent, 0, null);
    }

    /// <summary>Общий HttpClient с коротким терпением: автовыгрузка — фоновая мелочь, и висеть на
    /// ней минутами незачем.</summary>
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(30) };
}
