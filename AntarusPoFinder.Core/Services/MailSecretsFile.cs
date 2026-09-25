using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AntarusPoFinder.Core.Services;

/// <summary>Разбор файла с реквизитами почтового сервера — чтобы подключить ЛЮБОЙ сервер, не
/// перенабирая настройки руками.
///
/// Просьба Ильи дословно: «можешь сделать чтобы в приложении можно было самому любой сервер
/// подключать как конфигом так и вручную». Поля ввода на вкладке «Почта» никуда не делись — это
/// второй путь, для случая, когда реквизиты пришли письмом или выгрузкой и их проще перетащить.
///
/// Устроен так же и по той же причине, что <see cref="S3SecretsFile"/>: ФОРМАТ ФАЙЛА ЗАРАНЕЕ
/// НЕИЗВЕСТЕН. Почтовые настройки раздают то куском json из панели, то .env, то письмом вида
/// «Сервер: mail.company.ru, порт 587». Требовать один формат — значит вернуть человека к ручному
/// перенабору, ради избавления от которого файл и перетаскивают.
///
/// Чем этот разбор ОТЛИЧАЕТСЯ от разбора ключей хранилища: гадания по расположению здесь нет
/// вовсе. У ключей S3 две длинные строки-токена различимы по длине, а «mail.company.ru», «587» и
/// «ivanov» ни на что не похожи по отдельности — перепутанные местами, они выглядели бы как рабочие
/// настройки ровно до первой отправки. Нет подписей в файле — честно говорим, что не разобрали.</summary>
public sealed record MailSecretsFile(
    string Host, int Port, bool? UseSsl, string User, string Password, string From, string? Error)
{
    public bool Ok => Error is null;

    /// <summary>Печать БЕЗ пароля — по той же причине, что и у <see cref="S3SecretsFile"/>: разбор
    /// живёт там, где ошибку хочется куда-нибудь вывести, и запись целиком положила бы только что
    /// прочитанный пароль в текст сообщения.</summary>
    public override string ToString() =>
        $"MailSecretsFile {{ Host = {Host}, Port = {Port}, UseSsl = {UseSsl}, User = {User}, " +
        $"Password = {(Password.Length == 0 ? "<не найден>" : "<скрыт>")}, From = {From}, Error = {Error} }}";

    /// <summary>Больше этого файл с реквизитами быть не может — там несколько строк. Ограничение не
    /// про память, а про промах: перетащили не тот файл, и вместо внятного «это не похоже на
    /// реквизиты почты» программа молотила бы мегабайты.</summary>
    public const int MaxReasonableBytes = 1024 * 1024;

    private static MailSecretsFile Fail(string error) => new("", 0, null, "", "", "", error);

    public static MailSecretsFile Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return Fail("Файл пустой.");

        var text = content.TrimStart('﻿');
        if (LooksBinary(text))
            return Fail("Это не текстовый файл. Нужен файл с настройками почты — txt, json, env или ini.");

        var found = new Dictionary<Field, string>();
        CollectFromJson(text, found);
        CollectFromLines(text, found);

        var host = Value(found, Field.Host);
        if (host.Length == 0)
            return Fail("В файле не нашёлся адрес почтового сервера. Подойдёт файл, где есть строка " +
                        "вида «smtp_host = mail.company.ru» или «Сервер: mail.company.ru».");

        var from = Value(found, Field.From);
        var user = Value(found, Field.User);
        // «От кого» не указали, а логин похож на ящик — он и есть отправитель. Так почти всегда и
        // бывает, и заставлять вписывать то же самое второй раз значит просить работу ни за чем.
        if (from.Length == 0 && user.Contains('@')) from = user;

        return new MailSecretsFile(
            Host: StripScheme(host),
            Port: ParsePort(Value(found, Field.Port)),
            UseSsl: ParseSsl(Value(found, Field.Ssl)),
            User: user,
            Password: Value(found, Field.Password),
            From: from,
            Error: null);
    }

    private enum Field { Host, Port, Ssl, User, Password, From }

    private static string Value(Dictionary<Field, string> found, Field field) =>
        found.TryGetValue(field, out var v) ? v : "";

    /// <summary>Выигрывает ПЕРВОЕ встреченное значение: если в файле и настоящая строка, и
    /// закомментированный пример, берётся та, что встретилась раньше.</summary>
    private static void Remember(Dictionary<Field, string> found, Field field, string value)
    {
        value = CleanValue(value);
        if (value.Length == 0 || found.ContainsKey(field)) return;
        found[field] = value;
    }

    /// <summary>0 означает «в файле порта не было» — вызывающий оставляет свой прежний. Мусор вместо
    /// числа тоже даёт 0: подставить вместо «пятьсот восемьдесят семь» порт 0 значит молча сломать
    /// отправку, а сказать «порт не разобрал» — честно.</summary>
    private static int ParsePort(string raw) =>
        int.TryParse(raw.Trim(), out var port) && port is > 0 and <= 65535 ? port : 0;

    /// <summary>null — «в файле не сказано», и настройка не трогается. Именно null, а не false:
    /// молча выключить SSL у сервера, который без него не работает, — это отправка, падающая без
    /// объяснимой причины.</summary>
    private static bool? ParseSsl(string raw)
    {
        var v = raw.Trim().ToLowerInvariant();
        if (v.Length == 0) return null;
        if (v is "true" or "1" or "yes" or "on" or "ssl" or "tls" or "starttls" or "да" or "вкл") return true;
        if (v is "false" or "0" or "no" or "off" or "none" or "нет" or "выкл") return false;
        return null;
    }

    /// <summary>«smtps://mail.company.ru» — обычное дело в выгрузках. SmtpClient ждёт голое имя
    /// узла, схему отрезаем, а не отбраковываем файл.</summary>
    private static string StripScheme(string host)
    {
        var at = host.IndexOf("://", StringComparison.Ordinal);
        if (at >= 0) host = host[(at + 3)..];
        return host.Trim().Trim('/');
    }

    private static void CollectFromJson(string text, Dictionary<Field, string> found)
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
        catch (JsonException) { /* не json — ниже отработает построчный разбор */ }

        void Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        var named = FieldForName(property.Name);
                        // Порт и SSL в json лежат числом и логическим значением, а не строкой —
                        // брать только строки значило бы их не заметить.
                        if (named is { } field && property.Value.ValueKind is
                            JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                            Remember(found, field, property.Value.ToString());
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

    private static void CollectFromLines(string text, Dictionary<Field, string> found)
    {
        foreach (var raw in text.Split('\n').Select(l => l.Trim('\r').Trim()))
        {
            var line = raw.StartsWith('#') || raw.StartsWith("//") ? "" : raw;
            if (line.Length == 0 || (line[0] == '[' && line[^1] == ']')) continue;

            var separator = FirstSeparator(line);
            if (separator < 0) continue;

            var name = line[..separator];
            if (!LooksLikeName(name)) continue;
            if (FieldForName(name) is { } field) Remember(found, field, line[(separator + 1)..]);
        }
    }

    /// <summary>Разделителем считается ПЕРВОЕ двоеточие или знак равенства — иначе адрес вида
    /// «smtps://mail.company.ru:465» разрезался бы по собственному двоеточию.</summary>
    private static int FirstSeparator(string line)
    {
        var colon = line.IndexOf(':');
        var equals = line.IndexOf('=');
        if (colon < 0) return equals;
        if (equals < 0) return colon;
        return Math.Min(colon, equals);
    }

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

    private static bool LooksBinary(string text) =>
        text.Take(4096).Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));

    /// <summary>Имена нарочно на двух языках и в нескольких написаниях: файл пишет не наша
    /// программа, и угадать здесь дешевле, чем объяснять человеку, почему его файл «неправильный».
    /// Сравнение по имени без разделителей и регистра — «SMTP_HOST», «smtp host» и «"smtpHost"»
    /// это одно и то же.</summary>
    private static Field? FieldForName(string name)
    {
        var key = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (key.Length == 0) return null;

        if (Hosts.Contains(key)) return Field.Host;
        if (Ports.Contains(key)) return Field.Port;
        if (Ssls.Contains(key)) return Field.Ssl;
        if (Froms.Contains(key)) return Field.From;
        if (Users.Contains(key)) return Field.User;
        if (Passwords.Contains(key)) return Field.Password;
        return null;
    }

    private static readonly HashSet<string> Hosts = new(StringComparer.Ordinal)
    {
        "smtphost", "smtpserver", "mailhost", "mailserver", "host", "hostname", "server",
        "smtpaddress", "mailsmtphost", "emailhost", "outgoingserver",
        "сервер", "почтовыйсервер", "адрессервера", "хост", "серверисходящейпочты", "smtpсервер",
    };

    private static readonly HashSet<string> Ports = new(StringComparer.Ordinal)
    {
        "smtpport", "mailport", "port", "mailsmtpport", "emailport", "outgoingport",
        "порт", "портсервера", "портsmtp",
    };

    private static readonly HashSet<string> Ssls = new(StringComparer.Ordinal)
    {
        "smtpssl", "ssl", "usessl", "enablessl", "tls", "usetls", "starttls", "smtpsecure",
        "encryption", "security", "mailsmtpstarttlsenable",
        "шифрование", "защита", "использоватьssl",
    };

    private static readonly HashSet<string> Users = new(StringComparer.Ordinal)
    {
        "smtpuser", "smtpusername", "smtplogin", "mailuser", "mailusername", "username", "user",
        "login", "account", "mailsmtpuser", "emailuser",
        "логин", "пользователь", "имяпользователя", "учётнаязапись", "учетнаязапись",
    };

    private static readonly HashSet<string> Passwords = new(StringComparer.Ordinal)
    {
        "smtppassword", "smtppass", "mailpassword", "password", "pass", "passwd",
        "mailsmtppassword", "emailpassword", "apikey", "apppassword",
        "пароль", "парольпочты", "парольотящика",
    };

    /// <summary>«От кого» отдельно от логина: у части серверов логин — это «ivanov», а письма
    /// уходят от «no-reply@company.ru», и склеить их в одно поле значит получить письма, которые
    /// сервер отвергнет.</summary>
    private static readonly HashSet<string> Froms = new(StringComparer.Ordinal)
    {
        "smtpfrom", "mailfrom", "from", "fromaddress", "fromemail", "sender", "senderaddress",
        "emailfrom", "replyto",
        "откого", "отправитель", "адресотправителя", "обратныйадрес",
    };
}
