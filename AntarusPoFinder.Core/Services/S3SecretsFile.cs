using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AntarusPoFinder.Core.Services;

/// <summary>Разбор файла с ключами доступа к хранилищу на хостинге — того самого «файла secrets»,
/// который присылает хостинг-провайдер (просьба Ивана Герасимова от 06.08.2026: «вместо полей ввода
/// сделать зону, куда файл просто перетаскивается»).
///
/// Смысл класса в том, что ФОРМАТ ФАЙЛА ЗАРАНЕЕ НЕИЗВЕСТЕН. Панель Timeweb отдаёт ключи то парой
/// строк, то csv, то json; человек может переслать выгрузку из AWS-совместимой консоли или просто
/// сохранить письмо в txt. Требовать один-единственный формат — значит вернуть человека ровно к той
/// ручной возне, ради избавления от которой зона и делается: он всё равно откроет файл и будет
/// копировать оттуда строки. Поэтому разбор нарочно терпимый: сначала ищем ключи ПО ИМЕНИ (json,
/// ini/env, «ключ: значение», csv с шапкой), и только если имён в файле нет вообще — по расположению
/// (две строки-токена или одна строка «идентификатор:секрет»).
///
/// Догадка о порядке помечается флагом <see cref="OrderGuessed"/>: интерфейс обязан о ней сказать,
/// потому что перепутанные местами ключи выглядят как рабочие ровно до первой попытки выложить файл,
/// и человеку иначе не за что зацепиться при разборе «почему не пускает».
///
/// Разбор — чистая функция над текстом (файл читает вызывающий): так его можно проверить тестами на
/// всех форматах сразу, не раскладывая по диску временные файлы.</summary>
public sealed record S3SecretsFile(
    string AccessKey,
    string SecretKey,
    string Endpoint,
    string Bucket,
    string Region,
    bool OrderGuessed,
    string? Error)
{
    /// <summary>Оба ключа нашлись. Адрес/бакет/регион необязательны — они уже заполнены по
    /// умолчанию, и файл с одними ключами это нормальный, самый частый случай.</summary>
    public bool Ok => Error is null;

    /// <summary>Печать записи БЕЗ секретного ключа — по той же причине, что и у
    /// <see cref="S3Settings.ToString"/>: разбор файла с ключами живёт ровно в том месте, где ошибку
    /// хочется куда-нибудь вывести («что за файл мне подсунули»), и напечатанная целиком запись
    /// положила бы только что прочитанный Secret Access Key в текст сообщения.</summary>
    public override string ToString() =>
        $"S3SecretsFile {{ AccessKey = {AccessKey}, SecretKey = {(SecretKey.Length == 0 ? "<не найден>" : "<скрыт>")}, " +
        $"Endpoint = {Endpoint}, Bucket = {Bucket}, Region = {Region}, OrderGuessed = {OrderGuessed}, Error = {Error} }}";

    private static S3SecretsFile Fail(string error) => new("", "", "", "", "", false, error);

    /// <summary>Больше этого файл с ключами быть не может — там от силы несколько строк. Ограничение
    /// не про память, а про промах: перетащили не тот файл (архив, скан, выгрузку), и вместо
    /// осмысленного «это не похоже на файл с ключами» программа минуту молотила бы мегабайты.</summary>
    public const int MaxReasonableBytes = 1024 * 1024;

    public static S3SecretsFile Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return Fail("Файл пустой.");

        var text = content.TrimStart('﻿');
        if (LooksBinary(text))
            return Fail("Это не текстовый файл. Нужен файл с ключами — txt, csv, json или env, " +
                        "который прислал хостинг.");

        var found = new Dictionary<Field, Match>();

        // Порядок попыток — от самого надёжного к самому рискованному: имя поля в файле значит
        // ровно то, что написано, а расположение — это уже догадка.
        CollectFromJson(text, found);
        CollectFromLines(text, found);
        CollectFromCsv(text, found);

        // Гадание по расположению — только когда подписей в файле нет СОВСЕМ. Домешивать догадку к
        // тому, что уже нашлось по имени, нельзя: в строке «aws_access_key_id = AKIA…» два токена, и
        // догадка радостно объявила бы секретным ключом само имя поля или уже найденный
        // идентификатор. Файл с половиной ключей должен честно считаться половиной.
        var orderGuessed = false;
        if (!found.ContainsKey(Field.Access) && !found.ContainsKey(Field.Secret))
            orderGuessed = CollectByPosition(text, found);

        var access = Value(found, Field.Access);
        var secret = Value(found, Field.Secret);

        if (access.Length == 0 && secret.Length == 0)
            return Fail("В файле не нашлись ключи доступа. Подойдёт файл, где есть строки вида " +
                        "«Access Key ID» и «Secret Access Key» (txt, csv, json, env), или просто две " +
                        "строки: сначала идентификатор, потом секретный ключ.");
        if (secret.Length == 0)
            return Fail("В файле нашёлся только Access Key ID, а секретного ключа нет. " +
                        "Похоже, прислали половину — нужен файл с обоими ключами.");
        if (access.Length == 0)
            return Fail("В файле нашёлся только секретный ключ, а Access Key ID нет. " +
                        "Похоже, прислали половину — нужен файл с обоими ключами.");

        return new S3SecretsFile(
            AccessKey: access,
            SecretKey: secret,
            Endpoint: NormalizeEndpoint(Value(found, Field.Endpoint)),
            Bucket: Value(found, Field.Bucket).Trim('/'),
            Region: Value(found, Field.Region),
            OrderGuessed: orderGuessed,
            Error: null);
    }

    private enum Field { Access, Secret, Endpoint, Bucket, Region }

    /// <summary>Найденное значение и то, насколько однозначно называлось поле, из которого оно взято.
    /// Разделение нужно из-за реальных файлов вроде выгрузки AWS-консоли
    /// «User name,Password,Access key ID,Secret access key»: «Password» там — пароль от личного
    /// кабинета, а вовсе не секретный ключ, и стоит РАНЬШЕ настоящего ключа. Точное имя всегда
    /// перебивает приблизительное, независимо от порядка в файле.</summary>
    private readonly record struct Match(string Value, bool Exact);

    private static string Value(Dictionary<Field, Match> found, Field field) =>
        found.TryGetValue(field, out var v) ? v.Value : "";

    /// <summary>Среди одинаково точных имён выигрывает первое: если в файле и настоящий ключ, и
    /// закомментированный пример, берётся тот, что встретился раньше.</summary>
    private static void Remember(Dictionary<Field, Match> found, Field field, string value, bool exact = true)
    {
        value = CleanValue(value);
        if (value.Length == 0) return;
        if (found.TryGetValue(field, out var existing) && (existing.Exact || !exact)) return;
        found[field] = new Match(value, exact);
    }

    // ── Разбор по именам ──────────────────────────────────────────────────────────────────────

    /// <summary>json — отдельной веткой, а не построчно: провайдеры часто отдают его ОДНОЙ строкой
    /// (<c>{"accessKeyId":"…","secretAccessKey":"…"}</c>), и построчный разбор «до первого
    /// двоеточия» вытащил бы из неё мусор. Вложенность любая (у AWS-совместимых консолей ключи лежат
    /// внутри объекта <c>AccessKey</c>) — обходим дерево целиком и смотрим только на имена листьев.</summary>
    private static void CollectFromJson(string text, Dictionary<Field, Match> found)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return;

        try
        {
            using var doc = JsonDocument.Parse(trimmed, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            Walk(doc.RootElement);
        }
        catch (JsonException)
        {
            // Не json (или битый) — не беда, ниже отработает построчный разбор.
        }

        void Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String &&
                            FieldForName(property.Name) is { } named)
                            Remember(found, named.Field, property.Value.GetString() ?? "", named.Exact);
                        else
                            Walk(property.Value);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray()) Walk(item);
                    break;
            }
        }
    }

    /// <summary>Построчно: ini/env (<c>aws_access_key_id = …</c>), «Ключ доступа: …», yaml-подобное.
    /// Разделителем считается ПЕРВОЕ двоеточие или знак равенства — иначе адрес хранилища
    /// (<c>https://s3.twcstorage.ru</c>) разрезался бы по своему же двоеточию.</summary>
    private static void CollectFromLines(string text, Dictionary<Field, Match> found)
    {
        foreach (var raw in Lines(text))
        {
            var line = StripComment(raw);
            if (line.Length == 0 || (line[0] == '[' && line[^1] == ']')) continue; // [default] в ini

            var separator = FirstSeparator(line);
            if (separator < 0) continue;

            var name = line[..separator];
            var value = line[(separator + 1)..];
            if (!LooksLikeName(name)) continue;
            if (FieldForName(name) is { } named) Remember(found, named.Field, value, named.Exact);
        }
    }

    /// <summary>csv «шапка + строка значений» — так ключи отдаёт консоль AWS (credentials.csv) и
    /// повторяют за ней многие панели. Ищем строку, в которой хотя бы одна колонка называется знакомо,
    /// и берём значения из следующей непустой строки по номерам колонок.</summary>
    private static void CollectFromCsv(string text, Dictionary<Field, Match> found)
    {
        var lines = Lines(text).Select(StripComment).Where(l => l.Length > 0).ToList();

        for (var i = 0; i < lines.Count - 1; i++)
        {
            var separator = lines[i].Contains(';') ? ';' : ',';
            var header = SplitCsv(lines[i], separator);
            if (header.Length < 2) continue;

            var fields = header.Select(FieldForName).ToArray();
            if (fields.All(f => f is null)) continue;

            var values = SplitCsv(lines[i + 1], separator);
            for (var column = 0; column < fields.Length && column < values.Length; column++)
                if (fields[column] is { } named)
                    Remember(found, named.Field, values[column], named.Exact);
            return;
        }
    }

    private static string[] SplitCsv(string line, char separator) =>
        line.Split(separator).Select(part => part.Trim()).ToArray();

    // ── Разбор по расположению ────────────────────────────────────────────────────────────────

    /// <summary>Последняя попытка — когда в файле вообще нет подписей: две строки-токена, либо одна
    /// строка «идентификатор:секрет» (ровно тот формат, в котором ключи чаще всего пересылают в
    /// переписке). Возвращает true, если порядок пришлось УГАДАТЬ, — интерфейс обязан предупредить.
    ///
    /// Если длины разошлись характерно (идентификатор короткий, секрет длинный — так у Timeweb и у
    /// всех AWS-совместимых), доверяем длине, а не порядку: перепутать местами в письме легко, а
    /// длину подделать нечем.</summary>
    private static bool CollectByPosition(string text, Dictionary<Field, Match> found)
    {
        var tokens = new List<string>();
        foreach (var raw in Lines(text))
        {
            var line = StripComment(raw);
            if (line.Length == 0) continue;

            var parts = line.Split(new[] { ':', ';', ',', ' ', '\t', '=' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length is 0 or > 2) return false; // строка с пояснениями — гадать нельзя
            if (!parts.All(LooksLikeKeyValue)) return false;
            tokens.AddRange(parts);
        }

        if (tokens.Count != 2) return false;

        var (first, second) = (tokens[0], tokens[1]);
        if (first.Length >= 32 && second.Length <= 24)
            (first, second) = (second, first);

        Remember(found, Field.Access, first);
        Remember(found, Field.Secret, second);
        return true;
    }

    /// <summary>Похоже на сам ключ, а не на слово из пояснения: ключи — это длинные строки без
    /// пробелов из букв, цифр и служебных символов base64.</summary>
    private static bool LooksLikeKeyValue(string token) =>
        token.Length is >= 12 and <= 256 &&
        token.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '_' or '-' or '.');

    // ── Мелочи разбора ────────────────────────────────────────────────────────────────────────

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim('\r').Trim());

    private static string StripComment(string line) =>
        line.StartsWith('#') || line.StartsWith("//") ? "" : line.Trim();

    private static int FirstSeparator(string line)
    {
        var colon = line.IndexOf(':');
        var equals = line.IndexOf('=');
        if (colon < 0) return equals;
        if (equals < 0) return colon;
        return Math.Min(colon, equals);
    }

    /// <summary>Слева от разделителя — название поля, а не начало значения. Отсекает строки вроде
    /// «Ключи для хранилища amperus: смотри ниже» и адреса, случайно оказавшиеся в начале строки.</summary>
    private static bool LooksLikeName(string name)
    {
        var trimmed = name.Trim().Trim('"', '\'', '-', '*', '•').Trim();
        return trimmed.Length is > 0 and <= 48 &&
               trimmed.All(c => char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' or '.' or '"' or '\'');
    }

    private static string CleanValue(string value)
    {
        var trimmed = value.Trim().TrimEnd(',', ';').Trim();
        if (trimmed.Length >= 2 && trimmed[0] == trimmed[^1] && trimmed[0] is '"' or '\'')
            trimmed = trimmed[1..^1];
        return trimmed.Trim();
    }

    /// <summary>Адрес хранилища без схемы (<c>s3.twcstorage.ru</c>) — обычное дело в письмах, а
    /// запрос по такому адресу отправить некуда. Достраиваем https, а не отбраковываем файл.</summary>
    private static string NormalizeEndpoint(string endpoint)
    {
        if (endpoint.Length == 0) return "";
        if (!endpoint.Contains("://")) endpoint = "https://" + endpoint;
        return endpoint.TrimEnd('/');
    }

    private static bool LooksBinary(string text) =>
        text.Take(4096).Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));

    // ── Имена полей ───────────────────────────────────────────────────────────────────────────

    /// <summary>Как поле может называться в присланном файле. Списки нарочно длинные и на двух
    /// языках: файл пишет не наша программа, и «угадать» здесь дешевле, чем потом объяснять человеку,
    /// почему его файл «неправильный». Сравнение — по имени, очищенному от разделителей и регистра,
    /// поэтому <c>AWS_ACCESS_KEY_ID</c>, <c>aws access key id</c> и <c>"AccessKeyId"</c> — одно и то же.</summary>
    private readonly record struct NamedField(Field Field, bool Exact);

    private static NamedField? FieldForName(string name)
    {
        var key = Normalize(name);
        if (key.Length == 0) return null;

        if (Access.Contains(key)) return new NamedField(Field.Access, true);
        if (Secret.Contains(key)) return new NamedField(Field.Secret, true);
        if (Endpoints.Contains(key)) return new NamedField(Field.Endpoint, true);
        if (Buckets.Contains(key)) return new NamedField(Field.Bucket, true);
        if (Regions.Contains(key)) return new NamedField(Field.Region, true);

        if (MaybeAccess.Contains(key)) return new NamedField(Field.Access, false);
        if (MaybeSecret.Contains(key)) return new NamedField(Field.Secret, false);
        return null;
    }

    private static string Normalize(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static readonly HashSet<string> Access = new(StringComparer.Ordinal)
    {
        "accesskey", "accesskeyid", "awsaccesskeyid", "awsaccesskey", "s3accesskey", "s3accesskeyid",
        "accessid", "keyid", "publickey",
        "ключдоступа", "идентификаторключа", "идентификаторключадоступа", "идентификатордоступа",
        "публичныйключ", "имяключа",
    };

    private static readonly HashSet<string> Secret = new(StringComparer.Ordinal)
    {
        "secretkey", "secretaccesskey", "awssecretaccesskey", "awssecretkey", "s3secretkey",
        "secretkeyid", "privatekey",
        "секретныйключ", "секретныйключдоступа", "закрытыйключ", "приватныйключ", "секретныйкод",
    };

    /// <summary>Имена, которые ОБЫЧНО означают ключ, но не всегда: «login/password» рядом с ключами
    /// нередко оказываются входом в личный кабинет хостинга, а «key/secret» — сокращением. Такие
    /// значения берутся, только если точного имени в файле не нашлось (см. <see cref="Match"/>).</summary>
    private static readonly HashSet<string> MaybeAccess = new(StringComparer.Ordinal)
    {
        "key", "access", "login", "user", "username", "ключ", "доступ", "логин", "пользователь",
    };

    private static readonly HashSet<string> MaybeSecret = new(StringComparer.Ordinal)
    {
        "secret", "password", "pass", "секрет", "пароль",
    };

    private static readonly HashSet<string> Endpoints = new(StringComparer.Ordinal)
    {
        "endpoint", "endpointurl", "s3endpoint", "awsendpointurl", "url", "host", "hostname",
        "server", "storageurl", "s3url",
        "адрес", "адресхранилища", "сервер", "хост", "точкавхода",
    };

    private static readonly HashSet<string> Buckets = new(StringComparer.Ordinal)
    {
        "bucket", "bucketname", "s3bucket", "container",
        "бакет", "имябакета", "контейнер", "хранилище",
    };

    private static readonly HashSet<string> Regions = new(StringComparer.Ordinal)
    {
        "region", "regionname", "s3region", "awsregion", "awsdefaultregion", "defaultregion",
        "регион", "зона",
    };
}
