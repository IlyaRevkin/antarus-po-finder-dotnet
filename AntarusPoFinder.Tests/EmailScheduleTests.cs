using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Domain;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Когда именно уходит письмо. Жалоба Ильи дословно: «более обширную настройку что как
/// когда время и тп отправки по почте».
///
/// Почему это проверяется тестом, а не руками: расписание нельзя увидеть глазами. «Сводка раз в
/// 4 часа» проверяется четырьмя часами ожидания, «не слать с 22:00 по 08:00» — ночью, а переход
/// тихих часов через полночь не проверяется вообще никак, кроме как дождавшись полуночи. Сломайся
/// здесь арифметика — узнали бы мы об этом по жалобе «письма перестали приходить», через неделю.</summary>
public class EmailScheduleTests
{
    private static readonly DateTime Полдень = new(2026, 10, 15, 12, 00, 00);

    private static PendingEmail Событие(string категория = "Sync", string текст = "что-то случилось")
        => new(Полдень, категория, текст);

    // ── Тихие часы ───────────────────────────────────────────────────

    /// <summary>Главная ловушка тихих часов: интервал «с 22:00 по 08:00» переходит через полночь, и
    /// наивное сравнение from &lt;= now &lt; to на нём не срабатывает НИКОГДА. То есть тихие часы
    /// молча не работали бы ровно в том единственном виде, ради которого их и заводят — ночном.</summary>
    [Fact]
    public void Тихие_часы_через_полночь_ловят_и_вечер_и_утро()
    {
        var с = new TimeSpan(22, 0, 0);
        var по = new TimeSpan(8, 0, 0);

        Assert.True(EmailSchedule.InQuietHours(new TimeSpan(23, 30, 0), с, по));  // поздний вечер
        Assert.True(EmailSchedule.InQuietHours(new TimeSpan(3, 0, 0), с, по));    // ночь
        Assert.True(EmailSchedule.InQuietHours(new TimeSpan(7, 59, 0), с, по));   // перед концом
        Assert.False(EmailSchedule.InQuietHours(new TimeSpan(8, 0, 0), с, по));   // конец уже не тихо
        Assert.False(EmailSchedule.InQuietHours(new TimeSpan(12, 0, 0), с, по));  // рабочий день
    }

    /// <summary>Тишина — это ЗАПРЕТ, а не отмена: накопленное обязано уехать сразу после её
    /// окончания. Если бы оно терялось, человек, включивший тихие часы, перестал бы получать
    /// ночные события навсегда и не узнал бы об этом.</summary>
    [Fact]
    public void В_тихие_часы_письмо_откладывается_а_не_пропадает()
    {
        var настройки = new EmailScheduleSettings(
            Mode: EmailDeliveryMode.Immediate,
            QuietHoursEnabled: true, QuietFrom: new TimeSpan(22, 0, 0), QuietTo: new TimeSpan(8, 0, 0));
        var ночь = new DateTime(2026, 10, 15, 23, 10, 00);
        var накоплено = new[] { Событие() };

        var решение = EmailSchedule.Decide(настройки, ночь, накоплено, ночь);

        Assert.False(решение.SendNow);
        Assert.Equal(new DateTime(2026, 10, 16, 8, 0, 0), решение.NextCheckAt);

        // Утро наступило — то же самое накопленное уходит целиком.
        var утро = new DateTime(2026, 10, 16, 8, 0, 0);
        var утреннее = EmailSchedule.Decide(настройки, утро, накоплено, ночь);
        Assert.True(утреннее.SendNow);
        Assert.Single(утреннее.Items);
    }

    /// <summary>Режим «сразу» без тихих часов обязан остаться ровно тем, чем был до появления всей
    /// этой настройки: письмо уходит немедленно. Это поведение по умолчанию у всех, у кого почта уже
    /// настроена, и сломать его — значит выключить им почту молча.</summary>
    [Fact]
    public void Режим_сразу_отправляет_немедленно()
    {
        var решение = EmailSchedule.Decide(new EmailScheduleSettings(), Полдень, new[] { Событие() }, Полдень);

        Assert.True(решение.SendNow);
        Assert.Single(решение.Items);
    }

    // ── Сводки ───────────────────────────────────────────────────────

    /// <summary>Сводка раз в N часов отсчитывается от ПРЕДЫДУЩЕЙ отправки. Отсчитывай она от начала
    /// суток — первое же включение программы в 17:55 прислало бы в 18:00 сводку из одной строки,
    /// и режим выглядел бы сломанным с первой минуты.</summary>
    [Fact]
    public void Сводка_раз_в_N_часов_ждёт_свой_срок()
    {
        var настройки = new EmailScheduleSettings(Mode: EmailDeliveryMode.EveryNHours, DigestHours: 4);
        var отправили = new DateTime(2026, 10, 15, 10, 00, 00);
        var накоплено = new[] { Событие(), Событие(текст: "и ещё") };

        var рано = EmailSchedule.Decide(настройки, new DateTime(2026, 10, 15, 13, 59, 00), накоплено, отправили);
        Assert.False(рано.SendNow);
        Assert.Equal(new DateTime(2026, 10, 15, 14, 0, 0), рано.NextCheckAt);

        var пора = EmailSchedule.Decide(настройки, new DateTime(2026, 10, 15, 14, 00, 00), накоплено, отправили);
        Assert.True(пора.SendNow);
        Assert.Equal(2, пора.Items.Count);
    }

    /// <summary>Суточная сводка уходит один раз за сутки, а не на каждом тике таймера после
    /// назначенного часа. Иначе «сводка в 9:00» превратилась бы в письмо каждую минуту до полуночи
    /// — самый заметный способ сделать так, чтобы почту отключили совсем.</summary>
    [Fact]
    public void Суточная_сводка_не_повторяется_в_тот_же_день()
    {
        var настройки = new EmailScheduleSettings(Mode: EmailDeliveryMode.DailyAtTime, DailyAt: new TimeSpan(9, 0, 0));
        var накоплено = new[] { Событие() };

        var первая = EmailSchedule.Decide(настройки, new DateTime(2026, 10, 15, 9, 01, 00), накоплено,
            lastSentAt: new DateTime(2026, 10, 15, 8, 00, 00));
        Assert.True(первая.SendNow);

        // Уже отправляли сегодня в 9:01 — следующая только завтра в 9:00.
        var вторая = EmailSchedule.Decide(настройки, new DateTime(2026, 10, 15, 9, 02, 00), накоплено,
            lastSentAt: new DateTime(2026, 10, 15, 9, 01, 00));
        Assert.False(вторая.SendNow);
        Assert.Equal(new DateTime(2026, 10, 16, 9, 0, 0), вторая.NextCheckAt);
    }

    /// <summary>Пустая очередь не должна порождать письмо «ни о чём» — иначе режим сводок раз в час
    /// давал бы 24 пустых письма в сутки.</summary>
    [Fact]
    public void Пустая_очередь_письма_не_даёт()
    {
        var настройки = new EmailScheduleSettings(Mode: EmailDeliveryMode.EveryNHours, DigestHours: 1);

        var решение = EmailSchedule.Decide(настройки, Полдень, Array.Empty<PendingEmail>(),
            lastSentAt: Полдень.AddHours(-5));

        Assert.False(решение.SendNow);
    }

    // ── Какие категории уходят ───────────────────────────────────────

    /// <summary>Пустой список категорий обязан означать «все». Это состояние по умолчанию у каждого,
    /// кто настроил почту ДО появления этой настройки: прочитай мы пустой список как «ничего» —
    /// обновление молча выключило бы им почту целиком, и жалоба пришла бы не сразу.</summary>
    [Fact]
    public void Без_выбранных_категорий_уходят_все()
    {
        var настройки = new EmailScheduleSettings();

        Assert.True(настройки.CategoryAllowed("Sync"));
        Assert.True(настройки.CategoryAllowed("FirmwareAndParams"));
    }

    /// <summary>Выбранные категории — это фильтр ЧТО, поверх правил КОМУ. Невыбранная категория не
    /// уходит никому, даже если правило «всё подряд» её ловит.</summary>
    [Fact]
    public void Невыбранная_категория_в_письмо_не_попадает()
    {
        var настройки = new EmailScheduleSettings(Categories: new[] { "FirmwareAndParams" });

        Assert.True(настройки.CategoryAllowed("FirmwareAndParams"));
        Assert.False(настройки.CategoryAllowed("Sync"));

        // И в самом решении тоже: настройку могли поменять, пока строки лежали в очереди.
        var решение = EmailSchedule.Decide(настройки, Полдень,
            new[] { Событие("Sync"), Событие("FirmwareAndParams", "новая прошивка") }, Полдень);

        Assert.True(решение.SendNow);
        Assert.Equal("новая прошивка", Assert.Single(решение.Items).Text);
    }

    // ── Разбор времени из поля ввода ─────────────────────────────────

    /// <summary>Время вводит не программист. «9:5» и «22.00» обязаны пониматься, а мусор —
    /// отвергаться явно (null), а не превращаться молча в полночь: письмо, уходящее в 00:00 вместо
    /// 09:00, выглядит как «почта сломалась», и следа почему не остаётся.</summary>
    [Theory]
    [InlineData("09:00", 9, 0)]
    [InlineData(" 9:5 ", 9, 5)]
    [InlineData("22.00", 22, 0)]
    [InlineData("7", 7, 0)]
    public void Время_разбирается_терпимо(string ввод, int час, int минута)
        => Assert.Equal(new TimeSpan(час, минута, 0), EmailSchedule.ParseTime(ввод));

    [Theory]
    [InlineData("")]
    [InlineData("утром")]
    [InlineData("25:00")]
    [InlineData("9:70")]
    public void Непонятое_время_отвергается_а_не_становится_полуночью(string ввод)
        => Assert.Null(EmailSchedule.ParseTime(ввод));

    // ── Текст письма ─────────────────────────────────────────────────

    /// <summary>Сводка из одной строки обязана выглядеть как прежнее одиночное уведомление. Иначе
    /// включение режима сводок читается как поломка: пришло то же самое, но с заголовком «сводка,
    /// 1 шт.» и временем в теле.</summary>
    [Fact]
    public void Сводка_из_одной_строки_выглядит_как_обычное_письмо()
    {
        var (тема, тело) = EmailSchedule.Compose(new[] { Событие("Sync", "диск недоступен") });

        Assert.Equal("диск недоступен", тело);
        Assert.DoesNotContain("сводка", тема, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void В_сводке_видно_время_и_вид_каждого_события()
    {
        var (тема, тело) = EmailSchedule.Compose(new[]
        {
            new PendingEmail(new DateTime(2026, 10, 15, 9, 05, 00), "Sync", "диск недоступен"),
            new PendingEmail(new DateTime(2026, 10, 15, 11, 40, 00), "FirmwareAndParams", "выложена версия 2.1"),
        });

        Assert.Contains("2 шт.", тема);
        Assert.Contains("09:05", тело);
        Assert.Contains("11:40", тело);
        Assert.Contains("выложена версия 2.1", тело);
        // Категория подписана по-человечески, а не именем из enum: письмо читает не программист.
        Assert.Contains(NotificationCategoryInfo.Label(NotificationCategory.Sync), тело);
    }

    /// <summary>Порядок в сводке — по времени события, а не по тому, как строки легли в очередь.
    /// В очередь они попадают по мере прихода, но сводка читается как лента дня.</summary>
    [Fact]
    public void Сводка_упорядочена_по_времени()
    {
        var (_, тело) = EmailSchedule.Compose(new[]
        {
            new PendingEmail(new DateTime(2026, 10, 15, 18, 00, 00), "Sync", "позднее"),
            new PendingEmail(new DateTime(2026, 10, 15, 8, 00, 00), "Sync", "раннее"),
        });

        Assert.True(тело.IndexOf("раннее", StringComparison.Ordinal) < тело.IndexOf("позднее", StringComparison.Ordinal));
    }
}
