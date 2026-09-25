using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.Core.Domain;

/// <summary>Уйдёт ли письмо на самом деле — и если нет, то почему именно.
///
/// Заведено после жалобы: «проверил почту, тестовое письмо дошло, опубликовал прошивку — и письмо не
/// отправилось». Разгадка была не в сервере: кнопка «Проверить отправку» слала письмо В ОБХОД и
/// галочки «дублировать уведомления», и списка правил, — то есть проверяла только связь с сервером,
/// а сообщала «дублирование работает». Настройка выглядела законченной, хотя не отправила бы ничего.
///
/// Поэтому состояние считается одним местом и целиком: сервер, галочка и правила вместе. Частичная
/// правда здесь хуже молчания — человек уходит уверенным, что всё настроено.</summary>
public static class EmailReadiness
{
    /// <summary>Причины, по которым письма не уйдут. Пустой список — всё готово.
    ///
    /// Именно СПИСОК, а не первая найденная беда: у ненастроенной почты обычно не хватает сразу
    /// нескольких вещей, и показывать их по одной значит заставить человека ходить кругами.</summary>
    public static List<string> Problems(SmtpSettings smtp, IReadOnlyList<EmailRule> rules)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(smtp.Host))
            problems.Add("не указан адрес почтового сервера");
        if (string.IsNullOrWhiteSpace(smtp.From))
            problems.Add("не указано, с какого адреса отправлять");
        if (!smtp.Enabled)
            problems.Add("выключена галочка «Дублировать уведомления на почту»");

        var live = rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Recipient)).ToList();
        if (live.Count == 0)
            problems.Add("не задано ни одного получателя");
        else if (live.All(r => !EmailRouting.LooksLikeMailbox(r.Recipient)))
            // Группа AD раскрывается почтовым сервером, только если у неё есть свой адрес. Список
            // из одних названий групп выглядит заполненным, а писем не даёт.
            problems.Add("среди получателей нет ни одного почтового адреса — только названия групп");

        return problems;
    }

    public static bool Ready(SmtpSettings smtp, IReadOnlyList<EmailRule> rules) =>
        Problems(smtp, rules).Count == 0;

    /// <summary>Строка для человека. Говорит либо что всё готово и куда именно пойдут письма, либо
    /// чего не хватает — перечислением, а не намёком.</summary>
    public static string Describe(SmtpSettings smtp, IReadOnlyList<EmailRule> rules)
    {
        var problems = Problems(smtp, rules);
        if (problems.Count > 0)
            return "Письма НЕ уходят: " + string.Join("; ", problems) + ".";

        var boxes = rules.Where(r => r.Enabled && EmailRouting.LooksLikeMailbox(r.Recipient)).ToList();
        var everything = boxes.Any(r => r.Category.Length == 0);
        return everything
            ? $"Готово: все уведомления уходят на {boxes.Count} адрес(ов)."
            : $"Готово: уведомления выбранных видов уходят на {boxes.Count} адрес(ов).";
    }
}
