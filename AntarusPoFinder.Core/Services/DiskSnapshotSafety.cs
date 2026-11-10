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
}
