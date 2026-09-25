using System.Collections.Generic;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Уйдёт ли письмо на самом деле.
///
/// Жалоба Ильи 25.09.2026: «проверил почту, тестовое письмо дошло, опубликовал прошивку — и письмо
/// не отправилось». Причина была не в сервере: кнопка проверки слала письмо в обход и галочки
/// «дублировать уведомления», и списка правил. Связь с сервером она проверяла честно, а сообщала
/// «дублирование работает» — и настройка выглядела законченной, не отправляя ничего.</summary>
public class EmailReadinessTests
{
    private static SmtpSettings Smtp(bool enabled = true, string host = "mail.company.ru", string from = "robot@company.ru") =>
        new(host, 587, true, "robot", "p", from, enabled);

    private static List<EmailRule> Rules(params string[] recipients)
    {
        var list = new List<EmailRule>();
        foreach (var r in recipients) list.Add(new EmailRule { Id = r, Category = "", Recipient = r });
        return list;
    }

    [Fact]
    public void Всё_настроено_значит_готово()
    {
        Assert.True(EmailReadiness.Ready(Smtp(), Rules("ivanov@company.ru")));
    }

    /// <summary>Та самая ловушка. Сервер отвечает, адрес есть, получатель есть — а галочка снята,
    /// и не уходит ничего.</summary>
    [Fact]
    public void Снятая_галочка_названа_прямо()
    {
        var problems = EmailReadiness.Problems(Smtp(enabled: false), Rules("ivanov@company.ru"));

        Assert.Contains(problems, p => p.Contains("галочка"));
    }

    /// <summary>Вторая половина той же ловушки: сервер настроен, галочка стоит, а получателей нет.</summary>
    [Fact]
    public void Отсутствие_получателей_названо_прямо()
    {
        var problems = EmailReadiness.Problems(Smtp(), new List<EmailRule>());

        Assert.Contains(problems, p => p.Contains("получателя"));
    }

    /// <summary>Список из одних групп AD выглядит заполненным, а писем не даёт: группу раскрывает
    /// почтовый сервер, и только если у неё есть собственный адрес.</summary>
    [Fact]
    public void Одни_только_группы_ad_это_тоже_беда()
    {
        var problems = EmailReadiness.Problems(Smtp(), Rules("Программисты", "Наладчики"));

        Assert.Contains(problems, p => p.Contains("групп"));
    }

    /// <summary>Перечисляются ВСЕ беды сразу. По одной — это хождение кругами: исправил, нажал,
    /// узнал про следующую.</summary>
    [Fact]
    public void Беды_перечисляются_все_сразу_а_не_по_одной()
    {
        var problems = EmailReadiness.Problems(
            new SmtpSettings("", 587, true, "", "", "", false), new List<EmailRule>());

        Assert.True(problems.Count >= 3, "ожидались сервер, отправитель, галочка и получатели");
    }

    [Fact]
    public void Выключенное_правило_не_считается_получателем()
    {
        var rules = new List<EmailRule> { new() { Id = "1", Recipient = "ivanov@company.ru", Enabled = false } };

        Assert.Contains(EmailReadiness.Problems(Smtp(), rules), p => p.Contains("получателя"));
    }

    [Fact]
    public void Описание_говорит_словами_а_не_кодом()
    {
        Assert.StartsWith("Письма НЕ уходят", EmailReadiness.Describe(Smtp(enabled: false), Rules("a@b.ru")));
        Assert.StartsWith("Готово", EmailReadiness.Describe(Smtp(), Rules("a@b.ru")));
    }
}

/// <summary>Догадка об адресе почтового сервера по адресу отправителя — то, что делает кнопка
/// «Определить сервер». Просьба Ильи: «добавь для почты кнопку, которая автоматически вытащит
/// сервер SMTP».</summary>
public class SmtpProbeGuessTests
{
    [Fact]
    public void Имена_сервера_угадываются_по_домену_почты()
    {
        var guesses = SmtpProbe.GuessHosts("ivanov@elitacompany.com");

        Assert.Equal("mail.elitacompany.com", guesses[0]);
        Assert.Contains("smtp.elitacompany.com", guesses);
        Assert.Contains("elitacompany.com", guesses);
    }

    /// <summary>Список нарочно короткий: угадать десятью попытками нельзя, а ждать перебора человек
    /// не станет — он закроет окно раньше.</summary>
    [Fact]
    public void Догадок_немного()
    {
        Assert.True(SmtpProbe.GuessHosts("a@b.ru").Count <= 4);
    }

    [Fact]
    public void Без_собачки_гадать_не_из_чего()
    {
        Assert.Empty(SmtpProbe.GuessHosts("ivanov"));
        Assert.Empty(SmtpProbe.GuessHosts(""));
        Assert.Empty(SmtpProbe.GuessHosts("ivanov@"));
    }

    [Fact]
    public void Пустой_адрес_сервера_это_отказ_а_не_попытка_соединения()
    {
        var result = SmtpProbe.Probe("", 587);

        Assert.False(result.Ok);
        Assert.Contains("адрес", result.Error!);
    }
}
