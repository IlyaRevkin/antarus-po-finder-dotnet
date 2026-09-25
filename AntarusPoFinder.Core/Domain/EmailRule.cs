using System.Text.RegularExpressions;

namespace AntarusPoFinder.Core.Domain;

/// <summary>Одно правило «что кому дублировать на почту».
///
/// Просьба владельца дословно: «вкладка в настройках должна быть где можно настроить какие действия
/// будут дублироваться на email для каких групп ad или конкретных почтовых ящиков и тп».
///
/// Адресат — строка, а не «либо ящик, либо группа»: групп AD и ящиков в одном списке, различаются
/// по наличию собачки. Разводить их в два разных списка значило бы заставить человека помнить,
/// в какой из них он вписывал адрес.</summary>
public sealed class EmailRule
{
    /// <summary>Своё имя строки: правила заводятся на разных машинах и приезжают в общий конфиг,
    /// а нумеровать их по порядку значит столкнуться номерами.</summary>
    public string Id { get; set; } = "";

    /// <summary>Какие события. Пусто — ВСЕ категории: это и есть «дублировать всё», самый частый
    /// случай при первой настройке.</summary>
    public string Category { get; set; } = "";

    /// <summary>Ящик («ivanov@company.ru») или группа AD («Программисты»).</summary>
    public string Recipient { get; set; } = "";

    public bool Enabled { get; set; } = true;
}

/// <summary>Кому уходит письмо по конкретному событию.
///
/// Отдельно от самой отправки намеренно: решить «кому» можно и нужно без почтового сервера, и
/// именно здесь живут все грабли — выключенное правило, правило «на всё», один и тот же адрес в
/// двух правилах. Проверить это глазами на живой почте нельзя: письмо либо пришло, либо нет, а
/// «пришло дважды» замечают через неделю.</summary>
public static class EmailRouting
{
    public static List<string> RecipientsFor(string category, IEnumerable<EmailRule> rules)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var r in rules)
        {
            if (!r.Enabled) continue;
            var to = (r.Recipient ?? "").Trim();
            if (to.Length == 0) continue;
            // Пустая категория в правиле — «всё подряд».
            if (r.Category.Length > 0 && !string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase)) continue;
            // Один адрес, попавший под два правила, получает ОДНО письмо. Дубль тут не безобиден:
            // человек, которому всё приходит по два раза, перестаёт читать и первое.
            if (!seen.Add(to)) continue;
            result.Add(to);
        }
        return result;
    }

    /// <summary>Похоже ли на почтовый ящик. Всё прочее считается ГРУППОЙ AD, а не ошибкой: группы
    /// пишутся без собачки, и ругаться на них значило бы запретить половину того, о чём просили.</summary>
    public static bool LooksLikeMailbox(string value) =>
        Regex.IsMatch((value ?? "").Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
}
