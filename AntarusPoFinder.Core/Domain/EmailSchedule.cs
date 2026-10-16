using System.Globalization;

namespace AntarusPoFinder.Core.Domain;

/// <summary>Когда именно уходит письмо.
///
/// Просьба Ильи дословно: «более обширную настройку что как когда время и тп отправки по почте».
/// До этого выбора не было вообще: каждое уведомление немедленно превращалось в отдельное письмо, и
/// рабочий день, в который выложили десяток прошивок, давал десяток писем — после чего их перестают
/// читать целиком.</summary>
public enum EmailDeliveryMode
{
    /// <summary>Как было до этой настройки: каждое уведомление — отдельное письмо, сразу.</summary>
    Immediate,

    /// <summary>Копить и слать одним письмом раз в N часов. Отсчёт идёт от ПРЕДЫДУЩЕЙ отправки, а не
    /// от начала суток: иначе первое же включение программы в 17:55 прислало бы сводку в 18:00 из
    /// одной строки.</summary>
    EveryNHours,

    /// <summary>Одно письмо в заданное время суток — «утренняя сводка за вчера».</summary>
    DailyAtTime,
}

/// <summary>Одно накопленное уведомление, ждущее отправки.</summary>
/// <param name="When">Когда случилось — нужно, чтобы в сводке было видно время, а не только текст.</param>
/// <param name="Category">Строковое имя <see cref="NotificationCategory"/>: ровно в таком виде
/// категория лежит и в правилах (<see cref="EmailRule.Category"/>), и в базе уведомлений.</param>
public sealed record PendingEmail(DateTime When, string Category, string Text);

/// <summary>Что делать прямо сейчас: слать вот этот состав — или ждать до указанного времени.</summary>
/// <param name="SendNow">true — отправлять <paramref name="Items"/> немедленно.</param>
/// <param name="Items">Что именно уйдёт. При ожидании — пусто.</param>
/// <param name="NextCheckAt">Когда имеет смысл спросить снова. При <c>SendNow</c> — время следующей
/// отправки по расписанию; таймер в интерфейсе на это число не обязан опираться, но человеку в
/// настройках его показать есть чем.</param>
/// <param name="Reason">Человеческая причина ожидания — для строки состояния.</param>
public sealed record EmailScheduleDecision(
    bool SendNow, IReadOnlyList<PendingEmail> Items, DateTime NextCheckAt, string Reason);

/// <summary>Настройки «когда и что слать». Поверх правил <see cref="EmailRule"/>, а не вместо них:
/// правила решают КОМУ уходит письмо, эти настройки — КОГДА оно уходит и о ЧЁМ.
///
/// Разделение не косметическое. Список получателей общий на всю контору и едет в общем конфиге;
/// «не дёргать меня ночью» — это про конкретную установку и конкретного человека. Свалив то и
/// другое в правила, пришлось бы заводить тихие часы отдельно каждому адресату в каждой строке.</summary>
/// <param name="Categories">Какие категории вообще уходят почтой. ПУСТО — все: это состояние по
/// умолчанию, и оно обязано означать «как раньше», иначе появление настройки молча выключило бы
/// почту у всех, кто уже её настроил.</param>
public sealed record EmailScheduleSettings(
    EmailDeliveryMode Mode = EmailDeliveryMode.Immediate,
    int DigestHours = 4,
    TimeSpan DailyAt = default,
    bool QuietHoursEnabled = false,
    TimeSpan QuietFrom = default,
    TimeSpan QuietTo = default,
    IReadOnlyList<string>? Categories = null)
{
    public IReadOnlyList<string> AllowedCategories => Categories ?? Array.Empty<string>();

    /// <summary>Уходит ли почтой эта категория. Пустой список — все; см. <see cref="Categories"/>.</summary>
    public bool CategoryAllowed(string category) =>
        AllowedCategories.Count == 0 ||
        AllowedCategories.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Решение «слать сейчас или ждать» — чистой функцией, без таймеров, без сети и без базы.
///
/// Почему отдельным классом и без единого побочного эффекта: проверить расписание на живой почте
/// нельзя физически. «Сводка раз в 4 часа» проверяется четырьмя часами ожидания, «не слать с 22:00
/// до 08:00» — ночью, а переход через полночь не проверяется вообще никак, кроме как дождавшись
/// полуночи. Ровно поэтому вся арифметика времени живёт здесь и получает «сейчас» параметром.</summary>
public static class EmailSchedule
{
    /// <summary>Сколько минут минимум между письмами в режиме «сразу». Десять: срочное всё ещё
    /// приезжает в пределах десяти минут, а поток схлопывается с сотни писем до единиц.</summary>
    public const int MinimumGapMinutes = 10;

    /// <summary>Разбор «22:00» из поля ввода. Возвращает null на мусоре — вызывающий сам решает,
    /// ругаться или взять прежнее значение. Намеренно терпимо к «9:5» и к точке вместо двоеточия:
    /// это поле заполняет не программист.</summary>
    public static TimeSpan? ParseTime(string? value)
    {
        var text = (value ?? "").Trim().Replace('.', ':').Replace(',', ':');
        if (text.Length == 0) return null;

        var parts = text.Split(':');
        if (parts.Length is < 1 or > 3) return null;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return null;
        var m = 0;
        if (parts.Length > 1 && !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m)) return null;
        if (h is < 0 or > 23 || m is < 0 or > 59) return null;
        return new TimeSpan(h, m, 0);
    }

    public static string FormatTime(TimeSpan time) =>
        $"{(int)time.TotalHours:00}:{time.Minutes:00}";

    /// <summary>Тихие часы могут переходить через полночь: «с 22:00 по 08:00» — это ДВА куска суток,
    /// а не пустой промежуток. Наивное сравнение from &lt;= now &lt; to на таком интервале не
    /// срабатывало бы никогда, то есть тихие часы просто не работали бы — и именно в том
    /// единственном виде, ради которого их и заводят.</summary>
    public static bool InQuietHours(TimeSpan now, TimeSpan from, TimeSpan to)
    {
        if (from == to) return false;          // пустой интервал — тишины нет
        return from < to
            ? now >= from && now < to
            : now >= from || now < to;         // через полночь
    }

    /// <summary>Ближайший момент после <paramref name="now"/>, когда тишина закончится. Если сейчас
    /// не тихо — само <paramref name="now"/>.</summary>
    public static DateTime QuietEndsAfter(DateTime now, EmailScheduleSettings s)
    {
        if (!s.QuietHoursEnabled || !InQuietHours(now.TimeOfDay, s.QuietFrom, s.QuietTo)) return now;

        var today = now.Date + s.QuietTo;
        // Конец тишины уже прошёл сегодня — значит мы в куске «до полуночи» интервала через полночь,
        // и ждать надо завтрашнего утра.
        return today > now ? today : today.AddDays(1);
    }

    /// <summary>Главный вопрос: отправлять ли накопленное прямо сейчас.</summary>
    /// <param name="lastSentAt">Когда почта уходила в прошлый раз. Для режима «раз в N часов» это
    /// точка отсчёта; при первом запуске сюда идёт момент старта программы, а не DateTime.MinValue,
    /// иначе первая же накопленная строка улетела бы мгновенно и режим выглядел бы сломанным.</param>
    public static EmailScheduleDecision Decide(
        EmailScheduleSettings s, DateTime now, IReadOnlyList<PendingEmail> pending, DateTime lastSentAt)
    {
        // Фильтр категорий применяется и здесь, хотя накопитель уже фильтрует на входе: настройку
        // могли поменять, пока строки лежали в очереди, и слать по старой — значит слать то, от чего
        // человек только что отписался.
        var items = pending.Where(p => s.CategoryAllowed(p.Category)).ToList();

        var due = NextDue(s, now, lastSentAt);
        if (items.Count == 0)
            return new EmailScheduleDecision(false, Array.Empty<PendingEmail>(), due, "Копить нечего.");

        if (now < due)
            return new EmailScheduleDecision(false, Array.Empty<PendingEmail>(), due,
                $"Накоплено: {items.Count}. Уйдёт {Describe(due, now)}.");

        return new EmailScheduleDecision(true, items, NextDue(s, now, now), "Отправляем накопленное.");
    }

    /// <summary>Когда по расписанию наступает следующая отправка. Тихие часы учитываются ПОСЛЕДНИМИ
    /// и сдвигают срок вперёд: тишина — это запрет, а не отмена, накопленное уходит сразу после её
    /// окончания и не пропадает.</summary>
    public static DateTime NextDue(EmailScheduleSettings s, DateTime now, DateTime lastSentAt)
    {
        var due = s.Mode switch
        {
            EmailDeliveryMode.EveryNHours => lastSentAt.AddHours(Math.Max(1, s.DigestHours)),
            EmailDeliveryMode.DailyAtTime => NextDailyOccurrence(now, lastSentAt, s.DailyAt),
            // ⚠️ ДАЖЕ В РЕЖИМЕ «СРАЗУ» ЕСТЬ НИЖНИЙ ПРЕДЕЛ.
            //
            // 15.10.2026 почта отправила за сутки 120 писем: режим был «сразу», а уведомлений в
            // рабочий день набирается именно столько. Ящик, в который валится сто двадцать писем,
            // перестают читать целиком — вместе с тем одним, ради которого всё и заводилось.
            //
            // Поэтому «сразу» означает «не реже, чем раз в MinimumGapMinutes»: первое событие
            // уходит немедленно, а всё, что случилось в ближайшие минуты, догоняет его одним
            // письмом. Это предохранитель, а не настройка: настройкой его можно было бы выключить —
            // и вернуться ровно к тому, от чего уходили.
            _ => lastSentAt.AddMinutes(MinimumGapMinutes),
        };

        if (due < now) due = now;
        if (!s.QuietHoursEnabled) return due;

        // Проверяем тишину в момент НАСТУПЛЕНИЯ срока, а не сейчас: сводка, назначенная на 23:00
        // при тихих часах с 22:00, должна уехать в 08:00, даже если сейчас полдень.
        return InQuietHours(due.TimeOfDay, s.QuietFrom, s.QuietTo) ? QuietEndsAfter(due, s) : due;
    }

    private static DateTime NextDailyOccurrence(DateTime now, DateTime lastSentAt, TimeSpan at)
    {
        var today = now.Date + at;
        // Сегодняшний срок уже наступил и после него ничего не отправлялось — значит он и есть срок.
        if (now >= today && lastSentAt < today) return today;
        return now < today ? today : today.AddDays(1);
    }

    private static string Describe(DateTime due, DateTime now) =>
        due.Date == now.Date ? $"в {due:HH:mm}" : $"{due:dd.MM} в {due:HH:mm}";

    /// <summary>Тема и текст сводки. Отдельно от отправки: письмо из одной строки должно выглядеть
    /// как прежнее одиночное уведомление, а не как «сводка (1 шт.)» — иначе включение режима сводок
    /// читается как поломка.</summary>
    public static (string Subject, string Body) Compose(IReadOnlyList<PendingEmail> items)
    {
        if (items.Count == 1)
            return ("Antarus ПО Finder: " + CategoryLabel(items[0].Category), items[0].Text);

        var body = string.Join(Environment.NewLine,
            items.OrderBy(i => i.When)
                 .Select(i => $"{i.When:dd.MM HH:mm}  [{CategoryLabel(i.Category)}]  {i.Text}"));
        return ($"Antarus ПО Finder: сводка, {items.Count} шт.", body);
    }

    private static string CategoryLabel(string category) =>
        Enum.TryParse<NotificationCategory>(category, out var parsed)
            ? NotificationCategoryInfo.Label(parsed)
            : category;
}
