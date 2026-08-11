using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AntarusPoFinder.Core.Infrastructure;

/// <summary>Подпись запроса по схеме AWS Signature Version 4 — тем же способом, что и любой клиент
/// S3 (Cyberduck, aws-cli, WinSCP). Нужна ровно затем, чтобы приложение могло само класть файлы в
/// бакет хостинга, не таща в проект AWS SDK: из всего SDK нам нужны два запроса (PUT объекта и
/// проверка доступа), а зависимость пришлось бы тянуть целиком и обновлять вместе с ней.
///
/// Схема считает подпись в четыре шага (порядок и каждая мелочь в нём значимы — сервер повторяет
/// расчёт у себя и сверяет результат побайтно):
///   1. <b>Канонический запрос</b> — метод, путь, параметры, заголовки и хеш тела, записанные строго
///      определённым образом (см. <see cref="CanonicalRequest"/>);
///   2. <b>Строка для подписи</b> — алгоритм, время, «область действия» (дата/регион/сервис) и хеш
///      канонического запроса;
///   3. <b>Ключ подписи</b> — секрет, последовательно «просоленный» датой, регионом и сервисом
///      (см. <see cref="SigningKey"/>): благодаря этому утёкшая подпись годна лишь на один день и
///      только для одного сервиса в одном регионе;
///   4. сама подпись = HMAC-SHA256 строки для подписи этим ключом.
///
/// Разбиение на публичные шаги — не ради красоты: каждый из них проверяется тестами по эталонным
/// примерам из документации AWS, иначе «подпись не сошлась» отлаживалось бы по ответу сервера
/// «SignatureDoesNotMatch», который никогда не говорит, ЧТО именно разошлось.</summary>
public static class AwsV4Signer
{
    public const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>Хеш пустого тела — константа схемы: он же подставляется в заголовок
    /// x-amz-content-sha256 у запросов без тела (проверка доступа, HEAD).</summary>
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>Метка времени в формате схемы (20130524T000000Z). Всегда UTC: подпись действительна
    /// ±15 минут, и часовой пояс машины на это влиять не должен.</summary>
    public static string AmzDate(DateTimeOffset when) =>
        when.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static string DateStamp(DateTimeOffset when) =>
        when.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>«Область действия» подписи: дата/регион/сервис/aws4_request.</summary>
    public static string CredentialScope(DateTimeOffset when, string region, string service) =>
        $"{DateStamp(when)}/{region}/{service}/aws4_request";

    /// <summary>Шаг 1. Канонический запрос. Тонкости, из-за которых подпись чаще всего не сходится:
    ///   • путь кодируется по правилам URI, но слеши-разделители остаются слешами; для S3 путь
    ///     кодируется ОДИН раз (для остальных сервисов — дважды), поэтому сюда он приходит уже
    ///     готовым от <see cref="CanonicalPath"/>;
    ///   • имена заголовков — в нижнем регистре, значения — со схлопнутыми пробелами, и всё это
    ///     отсортировано по имени;
    ///   • список подписанных заголовков идёт и отдельной строкой, и в заголовке Authorization —
    ///     сервер подписывает ровно то, что в нём перечислено, и ничего больше.</summary>
    public static string CanonicalRequest(string method, string canonicalPath, string canonicalQuery,
        IReadOnlyDictionary<string, string> headers, string payloadHash)
    {
        var sorted = headers
            .Select(h => (Name: h.Key.ToLowerInvariant(), Value: Collapse(h.Value)))
            .OrderBy(h => h.Name, StringComparer.Ordinal)
            .ToList();

        var canonicalHeaders = string.Concat(sorted.Select(h => $"{h.Name}:{h.Value}\n"));
        var signedHeaders = string.Join(";", sorted.Select(h => h.Name));

        return string.Join("\n", method, canonicalPath, canonicalQuery, canonicalHeaders, signedHeaders, payloadHash);
    }

    /// <summary>Имена подписанных заголовков — тот же список, что попадает в канонический запрос.
    /// Отдельным методом, потому что нужен ещё и в заголовке Authorization.</summary>
    public static string SignedHeaders(IReadOnlyDictionary<string, string> headers) =>
        string.Join(";", headers.Keys.Select(k => k.ToLowerInvariant()).OrderBy(k => k, StringComparer.Ordinal));

    /// <summary>Шаг 2. Строка для подписи.</summary>
    public static string StringToSign(DateTimeOffset when, string region, string service, string canonicalRequest) =>
        string.Join("\n", Algorithm, AmzDate(when), CredentialScope(when, region, service),
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

    /// <summary>Шаг 3. Ключ подписи: секрет с префиксом «AWS4», последовательно пропущенный через
    /// HMAC по дате, региону, сервису и завершающей константе.</summary>
    public static byte[] SigningKey(string secretKey, DateTimeOffset when, string region, string service)
    {
        var kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), DateStamp(when));
        var kRegion = Hmac(kDate, region);
        var kService = Hmac(kRegion, service);
        return Hmac(kService, "aws4_request");
    }

    /// <summary>Шаг 4 + сборка заголовка Authorization целиком — то, что вешается на запрос.</summary>
    public static string AuthorizationHeader(string accessKey, string secretKey, DateTimeOffset when,
        string region, string service, IReadOnlyDictionary<string, string> headers, string canonicalRequest)
    {
        var signature = Hex(Hmac(SigningKey(secretKey, when, region, service), StringToSign(when, region, service, canonicalRequest)));
        return $"{Algorithm} Credential={accessKey}/{CredentialScope(when, region, service)}, " +
               $"SignedHeaders={SignedHeaders(headers)}, Signature={signature}";
    }

    /// <summary>Путь объекта в каноническом виде: каждый сегмент кодируется по RFC 3986, слеши
    /// остаются разделителями. Именно здесь кириллические имена файлов («инструкция_2.1.pdf» в папке
    /// «ПО/НГР/…») превращаются в проценты — в отличие от ссылки под QR-кодом, где они специально
    /// остаются буквами (см. LabelLinkBuilder), подпись обязана считаться от закодированного пути,
    /// иначе сервер посчитает её от своего варианта и не сойдётся.</summary>
    public static string CanonicalPath(string objectPath)
    {
        if (string.IsNullOrEmpty(objectPath)) return "/";
        var segments = objectPath.Split('/').Select(UriEncode);
        var path = string.Join("/", segments);
        return path.StartsWith('/') ? path : "/" + path;
    }

    /// <summary>Кодирование по правилам схемы: незарезервированными считаются только A-Z a-z 0-9
    /// и «-_.~», всё остальное — проценты с ЗАГЛАВНЫМИ шестнадцатеричными буквами. Стандартный
    /// Uri.EscapeDataString почти совпадает, но не гарантирует этих правил для всех символов,
    /// а «почти» здесь означает «подпись не сошлась».</summary>
    public static string UriEncode(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var ch = (char)b;
            if (ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~')
                sb.Append(ch);
            else
                sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    public static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();

    public static string Sha256Hex(byte[] data) => Hex(SHA256.HashData(data));

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    /// <summary>Значение заголовка для канонического вида: обрезано по краям, внутренние подряд
    /// идущие пробелы схлопнуты в один (требование схемы).</summary>
    private static string Collapse(string value)
    {
        var trimmed = value.Trim();
        var sb = new StringBuilder(trimmed.Length);
        var lastWasSpace = false;
        foreach (var ch in trimmed)
        {
            var isSpace = ch == ' ';
            if (isSpace && lastWasSpace) continue;
            sb.Append(ch);
            lastWasSpace = isSpace;
        }
        return sb.ToString();
    }
}
