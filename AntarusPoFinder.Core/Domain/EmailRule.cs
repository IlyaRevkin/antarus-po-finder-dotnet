using System.Collections.Generic;
using System.Linq;
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

    /// <summary>ПОДПИСКА — один адрес и ВЕСЬ набор того, что на него уходит.
    ///
    /// Зачем поверх правил. В хранилище правило — это «одна категория → один адрес», и чтобы прислать
    /// человеку три вида событий, их надо завести тремя строками. Жалоба Ильи: «надо сделать чтобы
    /// выбор что получать на почту не выпадающим списком было где можно что-то одно выбрать, а
    /// что-то конкретное» и «нужно добавить для почт что заведены редакт рассылки что получать и
    /// почты в целом».
    ///
    /// Формат хранения при этом НЕ меняется: правила так и ездят в общем конфиге по одной категории
    /// на строку, и старые машины читают их как раньше. Меняется только то, как человек их видит и
    /// правит — одной строкой на адрес.
    ///
    /// Пустой <see cref="Categories"/> означает «всё подряд» — ровно как пустая категория в правиле.</summary>
    public sealed record EmailSubscription(string Recipient, IReadOnlyList<string> Categories, bool Enabled)
    {
        /// <summary>Подписан на всё — в правилах это пустая категория.</summary>
        public bool Everything => Categories.Count == 0;
    }

    /// <summary>Свернуть правила в подписки: по строке на адрес.
    ///
    /// Порядок адресов сохраняется по первому появлению — список правят глазами, и перетасовка
    /// строк при каждом сохранении читалась бы как «что-то изменилось само».
    ///
    /// Правило «на всё» перебивает перечисление: если человеку заведены и «всё подряд», и
    /// «прошивки», он всё равно получает всё — показывать это как «прошивки» значило бы врать.</summary>
    public static List<EmailSubscription> ToSubscriptions(IEnumerable<EmailRule> rules)
    {
        var order = new List<string>();
        var byRecipient = new Dictionary<string, (List<string> Cats, bool Everything, bool Enabled)>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in rules)
        {
            var to = (r.Recipient ?? "").Trim();
            if (to.Length == 0) continue;
            if (!byRecipient.TryGetValue(to, out var acc))
            {
                acc = (new List<string>(), false, false);
                order.Add(to);
            }

            var cat = (r.Category ?? "").Trim();
            if (cat.Length == 0) acc.Everything = true;
            else if (!acc.Cats.Contains(cat, StringComparer.OrdinalIgnoreCase)) acc.Cats.Add(cat);

            // Адрес считается включённым, если включена хотя бы одна его строка: выключенная
            // половина — это «часть рассылки отключили», а не «адрес отключён».
            acc.Enabled |= r.Enabled;
            byRecipient[to] = acc;
        }

        return order
            .Select(to => byRecipient[to])
            .Zip(order, (acc, to) => new EmailSubscription(
                to, acc.Everything ? Array.Empty<string>() : acc.Cats, acc.Enabled))
            .ToList();
    }

    /// <summary>Развернуть подписки обратно в правила — то, что ложится в хранилище и едет коллегам.
    ///
    /// Идентификаторы выдаёт вызывающий: они должны быть уникальны между машинами (см. EmailRule.Id),
    /// а Core о способе их получения ничего не знает.</summary>
    public static List<EmailRule> ToRules(IEnumerable<EmailSubscription> subscriptions, Func<string> newId)
    {
        var rules = new List<EmailRule>();
        foreach (var s in subscriptions)
        {
            var to = (s.Recipient ?? "").Trim();
            if (to.Length == 0) continue;

            if (s.Everything)
            {
                rules.Add(new EmailRule { Id = newId(), Category = "", Recipient = to, Enabled = s.Enabled });
                continue;
            }
            foreach (var cat in s.Categories)
                rules.Add(new EmailRule { Id = newId(), Category = cat, Recipient = to, Enabled = s.Enabled });
        }
        return rules;
    }

    /// <summary>Похоже ли на почтовый ящик. Всё прочее считается ГРУППОЙ AD, а не ошибкой: группы
    /// пишутся без собачки, и ругаться на них значило бы запретить половину того, о чём просили.</summary>
    public static bool LooksLikeMailbox(string value) =>
        Regex.IsMatch((value ?? "").Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
}
