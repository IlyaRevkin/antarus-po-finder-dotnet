using System;
using System.Net.Http;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AntarusPoFinder.Core.Services;

/// <summary>ЖУРНАЛ КАЖДОЙ ЗАЛИВКИ В ПЛК — САМ УЕЗЖАЕТ В ХРАНИЛИЩЕ.
///
/// <b>Зачем.</b> Причину окирпичивания SMH5 искали дважды и дважды не нашли: сначала решили, что не
/// хватает подготовки ПЛК, потом — что Finder заливает не тот тип файла. Обе версии опроверг сам
/// Илья, и обе были догадками, потому что СЛЕДОВ не оставалось: окно загрузки показывало журнал и
/// закрывалось вместе с ним. Просьба Ильи 21.10.2026: «сделай чтобы логи через хранилище тебе дошли
/// и от меня ничего не требовалось».
///
/// <b>Как это работает.</b> После каждой операции окно загрузки кладёт запись в очередь на диске, а
/// ближайший проход синхронизации с хранилищем выкладывает её в бакет и удаляет локальную копию.
/// Тот же бакет, те же ключи, тот же молчаливый режим, что у тикетов: ключей нет — ничего не
/// происходит, и это штатное состояние (см. S3Settings.CanPublish).
///
/// <b>Почему очередь на диске, а не отправка сразу.</b> Заливка идёт в цеху, где сети может не быть
/// вовсе, и ровно та заливка, после которой контроллер испортился, — самая ценная. Отправка «прямо
/// сейчас или никогда» потеряла бы именно её. Запись маленькая (единицы килобайт), очередь
/// ограничена сверху (см. <see cref="MaxPending"/>), поэтому месяц без сети ничем не грозит.
///
/// <b>Почему не тикетом.</b> Тикет — это разговор с человеком, он виден всем и требует ответа.
/// Здесь нужен тихий технический след КАЖДОЙ заливки, включая успешные: беда SMH5 как раз в том,
/// что заливка выглядит успешной, а контроллер умирает позже, при снятии питания.</summary>
public static class LoaderDiagnostics
{
    /// <summary>Папка в бакете. Отдельная от тикетов: это машинные записи, которые читает тот, кто
    /// чинит программу, а не люди в конторе.</summary>
    public const string StorageFolder = "diagnostics/loader";

    /// <summary>Сколько записей держим в очереди, если сети долго нет. Сто заливок — это недели
    /// работы; дальше старые вытесняются новыми: свежая заливка ближе к беде, чем месячной
    /// давности.</summary>
    public const int MaxPending = 100;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Одна заливка целиком. Поля подобраны по вопросам, на которые не смогли ответить при
    /// разборе SMH5: ЧТО ушло в контроллер, КАКОЙ план построил Loader, с какой подготовкой, чем
    /// кончилось и сколько заняло.</summary>
    public sealed record Entry
    {
        [JsonPropertyName("schema")] public int Schema { get; init; } = 1;
        [JsonPropertyName("at")] public string At { get; init; } = "";
        [JsonPropertyName("machine")] public string Machine { get; init; } = "";
        [JsonPropertyName("user")] public string User { get; init; } = "";
        [JsonPropertyName("appVersion")] public string AppVersion { get; init; } = "";
        [JsonPropertyName("loaderVersion")] public string LoaderVersion { get; init; } = "";
        [JsonPropertyName("operation")] public string Operation { get; init; } = "";
        [JsonPropertyName("controller")] public string Controller { get; init; } = "";
        [JsonPropertyName("equipment")] public string Equipment { get; init; } = "";
        [JsonPropertyName("version")] public string Version { get; init; } = "";
        [JsonPropertyName("artifact")] public string Artifact { get; init; } = "";
        [JsonPropertyName("artifactType")] public string ArtifactType { get; init; } = "";
        [JsonPropertyName("preparation")] public string Preparation { get; init; } = "";
        [JsonPropertyName("ok")] public bool Ok { get; init; }
        [JsonPropertyName("outcome")] public string Outcome { get; init; } = "";
        [JsonPropertyName("seconds")] public int Seconds { get; init; }

        /// <summary>Журнал окна целиком. Он и есть главная ценность: план от Loader, его
        /// собственные строки и порядок событий. Несколько килобайт текста.</summary>
        [JsonPropertyName("log")] public string Log { get; init; } = "";
    }

    /// <summary>Папка очереди внутри профиля приложения.</summary>
    public static string QueueFolder(string appDataFolder) =>
        Path.Combine(appDataFolder, "diagnostics", "pending");

    /// <summary>Положить запись в очередь. Ничего не бросает: диагностика не имеет права мешать
    /// самой заливке — ради неё человек и открывал окно.</summary>
    public static string? Enqueue(string appDataFolder, Entry entry)
    {
        try
        {
            var dir = QueueFolder(appDataFolder);
            Directory.CreateDirectory(dir);

            var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"[..40] + ".json";
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
        catch (Exception) { /* очередь диагностики не повод падать */ }
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

    /// <summary>Ключ объекта в бакете: по дню, потом имя файла. По дню — чтобы перечисление за
    /// «последнюю неделю» было одним запросом с префиксом, а не выборкой из всего, что накопилось.</summary>
    public static string KeyFor(string fileName)
    {
        var day = fileName.Length >= 8 ? fileName[..8] : DateTime.Now.ToString("yyyyMMdd");
        return $"{StorageFolder}/{day}/{fileName}";
    }

    public sealed record FlushResult(int Sent, int Failed, string? Error)
    {
        public bool Ok => Error is null;
    }

    /// <summary>КУДА уезжают журналы — страница приёма файлов на домашнем сервере.
    ///
    /// Почему не бакет на хостинге, хотя тикеты ездят именно туда. Ключи от бакета есть не на каждой
    /// машине: они заводятся файлом руками, и на части компьютеров их просто нет (проверено
    /// 21.10.2026 — на машине Ильи ключей в профиле не оказалось). Журнал заливки обязан уезжать
    /// ОТТУДА, ГДЕ ЗАЛИВАЮТ, а это как раз цеховой компьютер за корпоративным фаерволом, где наружу
    /// открыт только 443. Страница приёма для этого и заведена: она принимает POST без всяких
    /// ключей и уже используется для выгрузки тикетов.
    ///
    /// Код доступа — не секрет уровня пароля: страница умеет ТОЛЬКО принимать (скачивания нет
    /// вовсе), ограничена по размеру, по расширениям и по числу попыток с одного адреса, а всё
    /// старше 90 дней удаляется само. Он лежит в настройках программы, чтобы его можно было
    /// сменить, не пересобирая её.</summary>
    /// <summary>Принимает файл именно <c>/drop/upload</c>; <c>/drop</c> — это страница для человека,
    /// и на POST она отвечает 405 (проверено 21.10.2026 вживую, а не по памяти).</summary>
    public const string DefaultDropUrl = "https://files.rev-il.ru/drop/upload";

    /// <summary>Выложить очередь через страницу приёма и стереть выложенное.
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
                form.Add(new StringContent("журнал заливки в ПЛК (автоматически)"), "comment");
                // ТИХО: страница приёма не шлёт телеграм-уведомление и кладёт файл в подпапку diag/.
                // Журнал уходит после КАЖДОЙ заливки, и десяток сообщений в день о том, чего человек
                // не просил, — это ровно то «участие», которого просили избежать.
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

    /// <summary>Общий HttpClient с коротким терпением: отправка журналов — фоновая мелочь, и висеть
    /// на ней минутами незачем.</summary>
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(30) };
}
