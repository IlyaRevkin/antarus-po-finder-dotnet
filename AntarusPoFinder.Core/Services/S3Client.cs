using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Infrastructure;

namespace AntarusPoFinder.Core.Services;

/// <summary>Минимальный клиент S3 — ровно два запроса, которые нужны приложению: положить файл и
/// проверить, что реквизиты рабочие. Своими руками, а не через AWS SDK: пакет тянет за собой
/// десяток зависимостей и собственную модель конфигурации ради двух запросов, а подпись — это
/// сотня строк (см. <see cref="AwsV4Signer"/>), проверенная тестами по эталону из botocore.
///
/// Адресация ПУТЁМ (<c>https://s3.twcstorage.ru/amperus/ключ</c>), а не поддоменом
/// (<c>https://amperus.s3.twcstorage.ru/ключ</c>): второй способ требует, чтобы у хостинга был
/// сертификат на поддомен с именем бакета, — у части провайдеров его нет, и тогда запрос падает
/// на проверке сертификата ещё до всякой подписи. Путь работает у всех.</summary>
public sealed class S3Client
{
    private readonly HttpClient _http;

    /// <summary>Время на один запрос. Файл инструкции — это сканы и pdf на десятки мегабайт, а
    /// канал до хостинга может быть каким угодно, поэтому пять минут, а не стандартные 100 секунд
    /// HttpClient: смысл в том, чтобы ошибка пришла ЗАМЕТНО раньше, чем человек решит, что
    /// программа зависла, но и чтобы тяжёлый скан по слабому каналу успел долететь.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Один HttpClient на всё приложение — так и положено с HttpClient: он рассчитан на
    /// переиспользование, а создаваемый на каждую выкладку держит своё TCP-соединение открытым ещё
    /// пару минут после Dispose. Выкладок за смену бывает много (папка сканов — это файл на страницу),
    /// и каждая своим клиентом выедала бы порты.</summary>
    private static readonly HttpClient Shared = new() { Timeout = DefaultTimeout };

    /// <param name="http">Подставляется тестами (перехватывающий обработчик). Владелец — тот, кто
    /// подставил; свой клиент этот класс не заводит и закрывать за собой нечего.</param>
    public S3Client(HttpClient? http = null) => _http = http ?? Shared;

    /// <summary>Чем закончилась попытка. Отдельным типом, а не исключением: выкладка на хостинг —
    /// дополнение к укладке на диск, и её неудача НЕ должна отменять загрузку версии (файл уже лёг
    /// на диск, карточка уже создана). Вызывающий превращает <see cref="Error"/> в предупреждение
    /// рядом с остальными, ровно как с недоступным третьим диском.</summary>
    public sealed record Result(bool Ok, string? Error, string? Url)
    {
        public static Result Success(string url) => new(true, null, url);
        public static Result Fail(string error) => new(false, error, null);
    }

    /// <summary>Кладёт файл в бакет под указанным ключом. Тело подписывается по-настоящему (хеш
    /// содержимого в x-amz-content-sha256), а не помечается как «неподписанное»: часть провайдеров
    /// неподписанное тело не принимает, а посчитать SHA-256 по файлу дешевле, чем разбираться
    /// потом, почему у одного хостинга работает, а у другого нет.</summary>
    public async Task<Result> PutFileAsync(S3Settings s, string key, string filePath,
        CancellationToken ct = default)
    {
        if (!s.CanPublish) return Result.Fail("Хранилище на хостинге не настроено");

        byte[] content;
        try { content = await File.ReadAllBytesAsync(filePath, ct); }
        catch (Exception ex) { return Result.Fail($"не прочитать файл — {ex.Message}"); }

        return await PutBytesAsync(s, key, content, ContentTypeFor(filePath), ct);
    }

    /// <summary>То же самое для уже готового содержимого — им пользуются и выкладка файла выше, и
    /// тесты, которым незачем ходить на диск.</summary>
    public async Task<Result> PutBytesAsync(S3Settings s, string key, byte[] content, string contentType,
        CancellationToken ct = default)
    {
        if (!s.CanPublish) return Result.Fail("Хранилище на хостинге не настроено");

        try
        {
            var request = BuildRequest(s, HttpMethod.Put, key, content, contentType, DateTimeOffset.UtcNow);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return Result.Fail(await DescribeFailureAsync(response, ct));
            return Result.Success(PublicUrl(s, key));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result.Fail("хранилище не ответило вовремя");
        }
        catch (Exception ex)
        {
            return Result.Fail(ex.Message);
        }
    }

    /// <summary>Лежит ли объект в бакете — HEAD по ключу. Нужен странице «Хранилище», чтобы отвечать
    /// на вопрос «эта инструкция там или нет» ПРАВДОЙ, а не своей записью о том, что когда-то
    /// выкладывали: файл могли удалить руками через S3-клиент, выложить с другой машины, или наша
    /// запись о выкладке могла потеряться вместе с локальной базой.
    ///
    /// Отсутствие объекта (404) — это НЕ ошибка, а законный ответ «нет», поэтому у метода отдельный
    /// тип результата: <c>Exists</c> false при <c>Ok</c> true. Смешай мы их в один Result — страница
    /// не смогла бы отличить «точно не выложено» от «не смогли проверить», а это разные состояния:
    /// первое чинится кнопкой «Выложить», второе — разбирательством с сетью.</summary>
    public sealed record Presence(bool Ok, bool Exists, long? Length, string? Error)
    {
        public static Presence Found(long? length) => new(true, true, length, null);
        public static Presence Missing() => new(true, false, null, null);
        public static Presence Unknown(string error) => new(false, false, null, error);
    }

    public async Task<Presence> HeadAsync(S3Settings s, string key, CancellationToken ct = default)
    {
        if (!s.CanPublish) return Presence.Unknown("Хранилище на хостинге не настроено");

        try
        {
            var request = BuildRequest(s, HttpMethod.Head, key, Array.Empty<byte>(), null, DateTimeOffset.UtcNow);
            using var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound) return Presence.Missing();
            if (!response.IsSuccessStatusCode) return Presence.Unknown(await DescribeFailureAsync(response, ct));
            return Presence.Found(response.Content.Headers.ContentLength);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Presence.Unknown("хранилище не ответило вовремя");
        }
        catch (Exception ex)
        {
            return Presence.Unknown(ex.Message);
        }
    }

    // ── Обзор бакета ────────────────────────────────────────────────────────────
    // Страница «Хранилище» показывает то, что ДОЛЖНО лежать на хостинге (список строится из базы и
    // диска), и по каждой строке умеет спросить, лежит ли. Чего она не умела вовсе — показать то,
    // что там лежит НА САМОМ ДЕЛЕ: объекты, оставшиеся после переименований, выкладки с другой
    // машины, ручных опытов. Просьба от 12.08.2026: «сделать взаимодействие с файловой системой
    // во вкладке хранилище, типа чтобы можно было посмотреть, может удалить что-то — допустим, тот
    // же мусор вручную». Для этого нужны ровно два запроса: перечислить и удалить.

    /// <summary>Объект в бакете таким, каким его показывает хостинг.</summary>
    public sealed record BucketObject(string Key, long Size, DateTime? Modified);

    /// <summary>Одна страница ответа: объекты, «папки» (общие префиксы) и метка продолжения. Хостинг
    /// отдаёт список порциями (обычно по тысяче), и продолжение — единственный способ узнать
    /// остальное; страница ходит по нему сама, но остановиться посреди обхода тоже должна уметь.</summary>
    public sealed record ListPage(bool Ok, string? Error, IReadOnlyList<BucketObject> Objects,
        IReadOnlyList<string> Folders, string? NextToken)
    {
        public static ListPage Fail(string error) =>
            new(false, error, Array.Empty<BucketObject>(), Array.Empty<string>(), null);
    }

    /// <param name="grouped">true — как в проводнике: вложенное скрывается за «папками» (delimiter),
    /// показывается только содержимое текущего уровня. false — все ключи под префиксом подряд;
    /// так считается объём «папки» перед её удалением.</param>
    public async Task<ListPage> ListAsync(S3Settings s, string prefix = "", bool grouped = true,
        string? continuationToken = null, int maxKeys = 1000, CancellationToken ct = default)
    {
        if (!s.HasAddress) return ListPage.Fail("не задан адрес хранилища или бакет");
        if (!s.HasCredentials) return ListPage.Fail("не заданы ключи доступа");

        var query = new List<(string Name, string Value)>
        {
            ("list-type", "2"),
            ("max-keys", maxKeys.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        if (!string.IsNullOrEmpty(prefix)) query.Add(("prefix", prefix));
        if (grouped) query.Add(("delimiter", "/"));
        if (!string.IsNullOrEmpty(continuationToken)) query.Add(("continuation-token", continuationToken));

        try
        {
            var request = BuildRequest(s, HttpMethod.Get, "", Array.Empty<byte>(), null, DateTimeOffset.UtcNow, query);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return ListPage.Fail(await DescribeFailureAsync(response, ct));
            return ParseListing(await response.Content.ReadAsStringAsync(ct));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ListPage.Fail("хранилище не ответило вовремя");
        }
        catch (Exception ex)
        {
            return ListPage.Fail(ex.Message);
        }
    }

    /// <summary>Разбор ответа ListObjectsV2. Отдельным методом и публично — чтобы проверялся тестом
    /// на настоящих ответах: у разных провайдеров (AWS, Ceph у Timeweb) отличаются пространство имён,
    /// порядок узлов и то, приходит ли <c>NextContinuationToken</c> при <c>IsTruncated=false</c>.
    /// Имена узлов ищутся БЕЗ учёта пространства имён по этой же причине.</summary>
    public static ListPage ParseListing(string xml)
    {
        try
        {
            var root = System.Xml.Linq.XDocument.Parse(xml).Root;
            if (root is null) return ListPage.Fail("хранилище прислало пустой ответ");

            var objects = new List<BucketObject>();
            foreach (var node in Elements(root, "Contents"))
            {
                var key = Value(node, "Key");
                if (key.Length == 0) continue;
                long.TryParse(Value(node, "Size"), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var size);
                DateTime? modified = DateTime.TryParse(Value(node, "LastModified"),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var when) ? when.ToLocalTime() : null;
                objects.Add(new BucketObject(key, size, modified));
            }

            var folders = new List<string>();
            foreach (var node in Elements(root, "CommonPrefixes"))
            {
                var value = Value(node, "Prefix");
                if (value.Length > 0) folders.Add(value);
            }

            // Продолжение берём ТОЛЬКО когда список правда обрезан: часть провайдеров присылает метку
            // и в последнем ответе, и обход по ней ходил бы по кругу.
            var truncated = string.Equals(Value(root, "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase);
            var token = truncated ? Value(root, "NextContinuationToken") : "";

            return new ListPage(true, null, objects, folders, token.Length > 0 ? token : null);
        }
        catch (Exception ex)
        {
            return ListPage.Fail($"не разобрать ответ хранилища — {ex.Message}");
        }

        static IEnumerable<System.Xml.Linq.XElement> Elements(System.Xml.Linq.XElement parent, string name) =>
            parent.Elements().Where(e => e.Name.LocalName == name);

        static string Value(System.Xml.Linq.XElement parent, string name) =>
            parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";
    }

    /// <summary>Удаляет объект. Безвозвратно и мимо всякой корзины — у бакета её нет, и спрашивает об
    /// этом страница, а не клиент. Удаление того, чего уже нет, у S3 успешно (204), и это правильно:
    /// цель «в бакете этого нет» достигнута.</summary>
    public async Task<Result> DeleteAsync(S3Settings s, string key, CancellationToken ct = default)
    {
        if (!s.HasAddress) return Result.Fail("не задан адрес хранилища или бакет");
        if (!s.HasCredentials) return Result.Fail("не заданы ключи доступа");
        if (string.IsNullOrWhiteSpace(key)) return Result.Fail("не указан ключ объекта");

        try
        {
            var request = BuildRequest(s, HttpMethod.Delete, key, Array.Empty<byte>(), null, DateTimeOffset.UtcNow);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
                return Result.Fail(await DescribeFailureAsync(response, ct));
            return Result.Success(PublicUrl(s, key));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result.Fail("хранилище не ответило вовремя");
        }
        catch (Exception ex)
        {
            return Result.Fail(ex.Message);
        }
    }

    /// <summary>Проверка «реквизиты рабочие»: запрашиваем у бакета один объект из списка. Именно
    /// список, а не запись пробного файла, — проверка не должна оставлять за собой мусор в чужом
    /// бакете, и она же честно отвечает на вопрос «а ключи вообще подходят к ЭТОМУ бакету».
    /// Единственное, чего она не проверяет, — право на ЗАПИСЬ: его без записи не проверить, о чём
    /// и сказано в подсказке рядом с кнопкой.</summary>
    public async Task<Result> CheckAsync(S3Settings s, CancellationToken ct = default)
    {
        if (!s.HasAddress) return Result.Fail("не задан адрес хранилища или бакет");
        if (!s.HasCredentials) return Result.Fail("не заданы ключи доступа");

        try
        {
            var request = BuildRequest(s, HttpMethod.Get, "", Array.Empty<byte>(), null,
                DateTimeOffset.UtcNow, query: "list-type=2&max-keys=1");
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return Result.Fail(await DescribeFailureAsync(response, ct));
            return Result.Success(s.Endpoint.TrimEnd('/') + "/" + s.Bucket);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result.Fail("хранилище не ответило вовремя");
        }
        catch (Exception ex)
        {
            return Result.Fail(ex.Message);
        }
    }

    /// <summary>Собранный и подписанный запрос. Публичный, чтобы тест мог проверить подпись, не
    /// поднимая сервер: подпись — единственное место, где ошибка не видна ни по коду, ни по логам,
    /// а только по ответу «SignatureDoesNotMatch» от чужого сервера.</summary>
    public static HttpRequestMessage BuildRequest(S3Settings s, HttpMethod method, string key,
        byte[] content, string? contentType, DateTimeOffset when, string query = "") =>
        BuildRequest(s, method, key, content, contentType, when, SplitQuery(query));

    /// <summary>То же самое, но параметры приходят парами, а не строкой. Так их и надо передавать,
    /// когда в значении бывает что угодно: префикс папки — это кириллица со слешами, а метка
    /// продолжения — набор символов на усмотрение хостинга. Собранные в строку, они разъезжались бы
    /// с ней при разборе (слеш в значении обязан стать <c>%2F</c>, а разделителем остаться не
    /// должен).</summary>
    public static HttpRequestMessage BuildRequest(S3Settings s, HttpMethod method, string key,
        byte[] content, string? contentType, DateTimeOffset when,
        IReadOnlyList<(string Name, string Value)> query)
    {
        var endpoint = new Uri(s.Endpoint.TrimEnd('/'));
        var objectPath = string.IsNullOrEmpty(key) ? s.Bucket : $"{s.Bucket}/{key}";
        var canonicalPath = AwsV4Signer.CanonicalPath(objectPath);
        // В адрес идёт ТА ЖЕ строка параметров, от которой считается подпись. Раньше в адрес уходил
        // исходный вид, а подписывался закодированный: пока параметрами были «list-type=2» и
        // «max-keys=1», это одно и то же, но с первым же префиксом с кириллицей подпись не сошлась бы.
        var canonicalQuery = EncodeQuery(query);
        var url = endpoint.GetLeftPart(UriPartial.Authority) + canonicalPath +
                  (canonicalQuery.Length > 0 ? "?" + canonicalQuery : "");

        var payloadHash = content.Length == 0 ? AwsV4Signer.EmptyPayloadHash : AwsV4Signer.Sha256Hex(content);
        var amzDate = AwsV4Signer.AmzDate(when);
        var host = endpoint.IsDefaultPort ? endpoint.Host : $"{endpoint.Host}:{endpoint.Port}";

        // Подписываем ровно три заголовка — host и два обязательных x-amz-*. Content-Type
        // сознательно НЕ подписывается: он не влияет на права и на адрес объекта, а любой прокси по
        // дороге, поправивший его, ломал бы подпись.
        var headers = new Dictionary<string, string>
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate,
        };

        var canonicalRequest = AwsV4Signer.CanonicalRequest(method.Method, canonicalPath,
            canonicalQuery, headers, payloadHash);

        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("Authorization",
            AwsV4Signer.AuthorizationHeader(s.AccessKey, s.SecretKey, when, s.Region, "s3", headers, canonicalRequest));

        if (content.Length > 0 || method == HttpMethod.Put)
        {
            request.Content = new ByteArrayContent(content);
            if (!string.IsNullOrEmpty(contentType))
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }
        return request;
    }

    /// <summary>Параметры запроса в каноническом виде: имя=значение, отсортировано по имени. У нас
    /// их всего два (list-type и max-keys), но порядок всё равно обязан быть определённым — иначе
    /// сервер посчитает подпись от другой строки.</summary>
    public static string CanonicalQuery(string query) => EncodeQuery(SplitQuery(query));

    /// <summary>Параметры строкой — в пары. Значение отделяется по ПЕРВОМУ «=»: у метки продолжения
    /// внутри бывает и «=», и «/», и в значении они обязаны остаться.</summary>
    private static List<(string Name, string Value)> SplitQuery(string query)
    {
        var pairs = new List<(string Name, string Value)>();
        if (string.IsNullOrEmpty(query)) return pairs;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            pairs.Add(eq < 0 ? (part, "") : (part[..eq], part[(eq + 1)..]));
        }
        return pairs;
    }

    private static string EncodeQuery(IReadOnlyList<(string Name, string Value)> query)
    {
        if (query.Count == 0) return "";
        var encoded = query
            .Select(p => (Name: AwsV4Signer.UriEncode(p.Name), Value: AwsV4Signer.UriEncode(p.Value)))
            .ToList();
        encoded.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return string.Join("&", encoded.Select(p => $"{p.Name}={p.Value}"));
    }

    /// <summary>Адрес, по которому файл потом откроется снаружи: веб-адрес хостинга + ключ. Если
    /// веб-адрес не задан, возвращаем адрес самого хранилища — он хотя бы позволяет убедиться, что
    /// файл долетел, хотя в QR такой ссылке не место.</summary>
    public static string PublicUrl(S3Settings s, string key)
    {
        var web = (s.WebUrl ?? "").Trim().TrimEnd('/');
        var path = string.Join("/", Array.ConvertAll(key.Split('/'), LabelLinkBuilder.EscapeSegment));
        return web.Length > 0
            ? $"{web}/{path}"
            : $"{s.Endpoint.TrimEnd('/')}/{s.Bucket}/{path}";
    }

    /// <summary>Ошибка сервера человеческим языком. S3 отвечает XML-документом с кодом ошибки
    /// внутри, и именно код («AccessDenied», «SignatureDoesNotMatch», «NoSuchBucket») говорит, что
    /// чинить, — тогда как один лишь HTTP-код 403 не отличает «ключи не те» от «нет прав».</summary>
    private static async Task<string> DescribeFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = "";
        try { body = await response.Content.ReadAsStringAsync(ct); }
        catch (Exception) { /* тело читать необязательно — код ответа уже есть */ }

        var code = Between(body, "<Code>", "</Code>");
        var message = Between(body, "<Message>", "</Message>");

        var human = code switch
        {
            "SignatureDoesNotMatch" => "не сходится подпись — проверьте Secret Access Key",
            "InvalidAccessKeyId" => "хостинг не знает такой Access Key",
            "AccessDenied" => "доступ запрещён — у ключа нет прав на этот бакет",
            "NoSuchBucket" => "на хостинге нет такого бакета",
            "RequestTimeTooSkewed" => "часы компьютера разошлись с хостингом больше чем на 15 минут",
            _ => null,
        };

        if (human is not null) return human;
        if (!string.IsNullOrEmpty(message)) return message;
        if (response.StatusCode == HttpStatusCode.Forbidden) return "доступ запрещён (403)";
        return $"хостинг ответил {(int)response.StatusCode} {response.ReasonPhrase}";
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        if (from < 0) return "";
        from += start.Length;
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        return to < 0 ? "" : text[from..to];
    }

    /// <summary>Тип содержимого по расширению — чтобы pdf с хостинга открывался в браузере телефона,
    /// а не скачивался файлом (ровно то, ради чего наклейку с QR и печатают).</summary>
    public static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".doc" => "application/msword",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".txt" => "text/plain; charset=utf-8",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };
}
