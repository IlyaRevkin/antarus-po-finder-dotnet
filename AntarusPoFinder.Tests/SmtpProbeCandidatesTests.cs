using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Что опрашивает кнопка «Определить сервер».
///
/// Жалоба Ильи дословно: «я нажимаю определить сервер, он находил, но автоматически не подставляет».
/// Причина была не в подстановке, а здесь: догадки по домену перебирались ТОЛЬКО когда поле сервера
/// пустое. Вписанный по памяти неверный адрес («mail.elita.ru» вместо «mail.elitacompany.ru»)
/// останавливал поиск на себе — то есть кнопка работала ровно в том случае, когда и так ничего не
/// было введено, и молчала в том, ради которого её нажимают.
///
/// Проверяется списком кандидатов, а не живым опросом: сеть в тесте — это чужой почтовый сервер,
/// брандмауэр и пять секунд таймаута на каждую попытку.</summary>
public class SmtpProbeCandidatesTests
{
    /// <summary>Главный тест от самой жалобы: неверный вписанный адрес не должен обрывать поиск.</summary>
    [Fact]
    public void Неверный_вписанный_адрес_не_отменяет_перебор_догадок()
    {
        var кандидаты = SmtpProbe.BuildCandidates("mail.elita.ru", "ivanov@elitacompany.ru");

        Assert.Equal("mail.elita.ru", кандидаты[0]);
        Assert.Contains("mail.elitacompany.ru", кандидаты);
        Assert.Contains("smtp.elitacompany.ru", кандидаты);
        Assert.Contains("elitacompany.ru", кандидаты);
    }

    /// <summary>Вписанное идёт ПЕРВЫМ. Человек мог указать релей, имя которого по домену не
    /// угадать; подменить его догадкой, пока он отвечает, — значит переписать верную настройку.</summary>
    [Fact]
    public void Вписанное_опрашивается_первым()
    {
        var кандидаты = SmtpProbe.BuildCandidates("relay-01.local", "ivanov@elitacompany.ru");

        Assert.Equal("relay-01.local", кандидаты[0]);
    }

    /// <summary>Вписали ровно то, что и так угадалось бы, — опрашивать это дважды незачем: каждая
    /// лишняя попытка стоит до пяти секунд ожидания на молчащем адресе.</summary>
    [Fact]
    public void Совпавшая_догадка_не_опрашивается_дважды()
    {
        var кандидаты = SmtpProbe.BuildCandidates("MAIL.elitacompany.ru", "ivanov@elitacompany.ru");

        Assert.Single(кандидаты, c => c.Equals("mail.elitacompany.ru", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Без_адреса_и_без_от_кого_опрашивать_нечего()
        => Assert.Empty(SmtpProbe.BuildCandidates("", ""));

    /// <summary>Поле сервера пустое — прежнее поведение: перебираем догадки по домену.</summary>
    [Fact]
    public void Пустое_поле_сервера_даёт_догадки_по_домену()
    {
        var кандидаты = SmtpProbe.BuildCandidates("", "ivanov@elitacompany.ru");

        Assert.Equal(SmtpProbe.GuessHosts("ivanov@elitacompany.ru"), кандидаты);
    }
}
