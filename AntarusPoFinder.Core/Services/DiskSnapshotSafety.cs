using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AntarusPoFinder.Core.Services;

/// <summary>ПРОВЕРКА СЛЕПКА ПЕРЕД ОТПРАВКОЙ: в нём не должно быть ничего секретного.
///
/// <b>Зачем отдельная проверка, если слепок и так собирается из перечисленных полей.</b> Потому что
/// он собирается из них СЕГОДНЯ. Слепок уезжает с рабочей машины наружу сам, по расписанию, и
/// единственное, что отделяет опись диска от утечки реквизитов, — внимательность того, кто через
/// год добавит в <see cref="DiskSnapshotDbRow"/> ещё одно «полезное поле». Эта проверка превращает
/// внимательность в правило, которое держит тест и которое срабатывает ДО отправки, а не после.
///
/// <b>Почему ищем имена полей, а не значения.</b> Значение — это путь на диске, и папка с именем
/// «Секретный проект» ничего не нарушает; отказаться отправить слепок из-за неё было бы глупо и
/// научило бы отключать проверку. А вот ПОЛЕ с именем вроде <c>password</c>, <c>s3Secret</c> или
/// <c>token</c> в слепке означает ровно одно: кто-то положил в него не ту часть базы. Исключение —
/// два значения, которые не бывают ничем другим: <c>enc:</c> (так начинается всё, что программа
/// шифрует перед записью в общий конфиг) и заголовок PEM-ключа.</summary>
public static class DiskSnapshotSafety
{
    /// <summary>Части имён полей, которых в слепке быть не может. Сравнение без регистра и по
    /// вхождению: <c>s3SecretEnc</c> попадается так же, как <c>secret</c>.</summary>
    public static readonly string[] ForbiddenFieldParts =
    {
        "password", "passwd", "secret", "token", "apikey", "accesskey", "privatekey",
        "credential", "пароль",
    };

    /// <summary>Значения, которые не бывают путём на диске: приставка шифрованного значения в общем
    /// конфиге и заголовок PEM-ключа.</summary>
    public static readonly string[] ForbiddenValueMarkers = { "enc:", "-----BEGIN" };

    /// <summary>Что нашлось. Пустой список — отправлять можно.</summary>
    public static IReadOnlyList<string> Scan(string json)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(json)) return found;

        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json),
            new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var name = reader.GetString() ?? "";
                    foreach (var part in ForbiddenFieldParts)
                        if (name.Contains(part, StringComparison.OrdinalIgnoreCase))
                            found.Add($"поле «{name}»");
                }
                else if (reader.TokenType == JsonTokenType.String)
                {
                    var value = reader.GetString() ?? "";
                    foreach (var marker in ForbiddenValueMarkers)
                        if (value.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                            found.Add($"значение, начинающееся на «{marker}»");
                }
            }
        }
        catch (JsonException)
        {
            // Нечитаемый json — не наш вопрос: отправлять его всё равно нечего, а притворяться,
            // что проверка прошла, нельзя.
            found.Add("слепок не разбирается как json");
        }

        return found;
    }

    /// <summary>Почему отправлять нельзя. null — можно. Текст готов к показу и к записи в журнал.</summary>
    public static string? Refusal(string json)
    {
        var found = Scan(json);
        if (found.Count == 0) return null;
        return "Слепок не отправлен: в нём нашлось то, чему там не место — " +
               string.Join(", ", new HashSet<string>(found)) + ".";
    }

    // ── То же самое, но для ПЛОСКОГО ТЕКСТА (журнал приложения, см. AppLogDrop) ──────────────
    //
    // Журнал — не json, разобрать его на «имена полей» и «значения» нельзя, а проверять его надо
    // по тем же правилам и тем же списком: заведи он себе второй список запретных слов, и через
    // год они разошлись бы — причём молча и в ту сторону, которая дороже.
    //
    // Отличие ровно одно и вынужденное. В json имя поля отделено от текста самим форматом, а в
    // журнале слово «пароль» попадается в совершенно безобидных строках: «пароль не подошёл»,
    // «смена пароля отменена». Запрещать их — значит затыкать весь журнал машины из-за честного
    // сообщения об ошибке (отказ касается ВСЕЙ пачки). Поэтому в тексте запретное слово считается
    // находкой, только если за ним следует ЗНАЧЕНИЕ: «=», «:» или «» со стоящим дальше непробелом.
    // Это ровно форма «я пишу в журнал содержимое поля», и она же — единственная опасная.
    //
    // Два значения (enc: и заголовок PEM) ищутся в тексте как есть, без всяких условий: ни то, ни
    // другое не бывает ничем, кроме секрета.

    /// <summary>Что нашлось в плоском тексте. Пустой список — отправлять можно.</summary>
    public static IReadOnlyList<string> ScanText(string? text)
    {
        var found = new List<string>();
        var src = text ?? "";
        if (src.Length == 0) return found;

        foreach (var part in ForbiddenFieldParts)
        {
            var from = 0;
            while (true)
            {
                var at = src.IndexOf(part, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0) break;
                from = at + part.Length;
                if (LooksLikeAssignment(src, from)) { found.Add($"«{part}» со значением"); break; }
            }
        }

        foreach (var marker in ForbiddenValueMarkers)
            if (ContainsAtWordStart(src, marker))
                found.Add($"значение, начинающееся на «{marker}»");

        return found;
    }

    /// <summary>Почему журнал отправлять нельзя. null — можно.</summary>
    public static string? RefusalForText(string? text)
    {
        var found = ScanText(text);
        if (found.Count == 0) return null;
        return "Журнал не отправлен: в нём нашлось то, чему там не место — " +
               string.Join(", ", new HashSet<string>(found)) + ".";
    }

    /// <summary>Приставка найдена С НАЧАЛА СЛОВА. В json это получалось само (значение начиналось
    /// с неё), а в плоском тексте «enc:» прячется в середине доброй половины английских слов —
    /// «sequence:», «reference:», «silence:». Отказ отправить журнал из-за слова «sequence» мы
    /// заметили бы не раньше, чем через месяц полного молчания, поэтому граница слова обязательна.</summary>
    private static bool ContainsAtWordStart(string text, string marker)
    {
        var from = 0;
        while (true)
        {
            var at = text.IndexOf(marker, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            var before = at == 0 ? ' ' : text[at - 1];
            if (!char.IsLetterOrDigit(before)) return true;
            from = at + 1;
        }
    }

    /// <summary>За словом идёт его значение: «пароль: 1234», «token=abc», «secret» : "x". Хвост
    /// вида «пароль не подошёл» значением не считается — см. комментарий выше.</summary>
    private static bool LooksLikeAssignment(string text, int from)
    {
        var i = from;
        // Закрывающая кавычка имени поля, если текст json-подобный.
        while (i < text.Length && (text[i] == '"' || text[i] == '\'' || text[i] == '»')) i++;
        while (i < text.Length && text[i] == ' ') i++;
        if (i >= text.Length || (text[i] != '=' && text[i] != ':')) return false;
        i++;
        while (i < text.Length && (text[i] == ' ' || text[i] == '"' || text[i] == '\'')) i++;
        return i < text.Length && text[i] != '\n' && text[i] != '\r';
    }
}
