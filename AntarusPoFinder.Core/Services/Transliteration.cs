using System;
using System.Collections.Generic;
using System.Text;

namespace AntarusPoFinder.Core.Services;

/// <summary>Кириллица → латиница для АДРЕСОВ на хостинге. К файлам и папкам на диске отношения не
/// имеет: там всё остаётся по-русски, переименовывать рабочий диск ради ссылки никто не собирается.
///
/// Зачем вообще. Раскладка в бакете повторяет раскладку на диске (см. <see cref="S3Settings.KeyFor"/>),
/// а диск у нас весь русский — «ПО\ПЖ\2.0\SMH5\…\Инструкция\инструкция_2.1.0042.0001.pdf». Ссылка под
/// QR получалась кириллической, и хотя формально это законный IRI, на практике по дороге к телефону
/// её кто-нибудь да покорёжит: почтовые клиенты и мессенджеры перекодируют такое по-своему, S3-клиенты
/// вроде Cyberduck показывают ключи в процентах, а подписи AWS SigV4 считаются по точной байтовой
/// форме ключа — расхождение в кодировке между тем, кто клал, и тем, кто читает, даёт «файл не
/// найден» на ровном месте.
///
/// Таблица звучаний — практическая, а не ГОСТ: «щ» это «sch», «ц» это «c», твёрдый и мягкий знаки
/// пропадают. Читаемость важнее обратимости — обратно из адреса в имя папки никто не переводит, путь
/// на диске всегда берётся из базы.</summary>
public static class Transliteration
{
    private static readonly Dictionary<char, string> Letters = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m",
        ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
        ['ф'] = "f", ['х'] = "h", ['ц'] = "c", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    /// <summary>Символы, которые в адресе можно оставить как есть. Всё остальное — включая пробел,
    /// плюс, амперсанд, кавычки и запятую — превращается в подчёркивание: адрес обязан пережить
    /// пересылку в мессенджере и ручное копирование из письма, а эти знаки там регулярно ломаются.</summary>
    private static bool IsSafe(char ch) =>
        (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')
        || ch is '.' or '-' or '_' or '(' or ')';

    /// <summary>Автоматический перевод одного имени папки или файла. Регистр сохраняется по смыслу:
    /// имя, набранное капсом («ПЖ», «НГР»), остаётся капсом («PZH», «NGR») — это аббревиатуры, и
    /// «Pzh» в адресе читалось бы как опечатка; обычное слово переводится с заглавной буквы.</summary>
    public static string Auto(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var upper = IsAllUpperCyrillic(name);
        var sb = new StringBuilder(name.Length * 2);

        foreach (var ch in name.Trim())
        {
            var lower = char.ToLowerInvariant(ch);
            if (Letters.TryGetValue(lower, out var latin))
            {
                if (latin.Length == 0) continue;
                var isUpper = upper || char.IsUpper(ch);
                sb.Append(isUpper
                    ? (upper ? latin.ToUpperInvariant() : char.ToUpperInvariant(latin[0]) + latin[1..])
                    : latin);
                continue;
            }
            sb.Append(IsSafe(ch) ? ch : '_');
        }

        return Tidy(sb.ToString());
    }

    /// <summary>Подряд идущие подчёркивания схлопываются, крайние убираются: «Карта ВВ» не должна
    /// давать «Karta__VV», а «(черновик) » — «_chernovik__».</summary>
    private static string Tidy(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch == '_' && sb.Length > 0 && sb[^1] == '_') continue;
            sb.Append(ch);
        }
        var result = sb.ToString().Trim('_');
        // Имя, состоявшее из одних непереводимых знаков, не должно исчезнуть совсем — иначе в пути
        // схлопнутся два разных уровня и файл уедет не туда.
        return result.Length > 0 ? result : "_";
    }

    /// <summary>Имя целиком набрано кириллическими прописными («ПЖ», «НГР-КНС»). Латинские буквы и
    /// цифры в счёт не идут: «SMH5» уже латиница и через таблицу не проходит, а «ВЗУ-2» — аббревиатура
    /// с цифрой и обязано остаться капсом.</summary>
    private static bool IsAllUpperCyrillic(string name)
    {
        var sawCyrillic = false;
        foreach (var ch in name)
        {
            if (!Letters.ContainsKey(char.ToLowerInvariant(ch))) continue;
            if (char.IsLower(ch)) return false;
            sawCyrillic = true;
        }
        return sawCyrillic;
    }

    /// <summary>Есть ли в имени вообще кириллица — по этому признаку справочник решает, о каких
    /// папках стоит спрашивать человека, а какие («SMH5», «1.0.0005.0001») переводить незачем.</summary>
    public static bool HasCyrillic(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var ch in name)
            if (Letters.ContainsKey(char.ToLowerInvariant(ch)))
                return true;
        return false;
    }
}
