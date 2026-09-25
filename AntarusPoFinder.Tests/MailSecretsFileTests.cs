using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Подключение любого почтового сервера файлом.
///
/// Просьба Ильи дословно: «можешь сделать чтобы в приложении можно было самому любой сервер
/// подключать как конфигом так и вручную».
///
/// Проверяется здесь, а не глазами на живой почте, потому что беда у неправильно разобранного
/// файла ровно одна и очень тихая: настройки выглядят заполненными и правдоподобными, а письма
/// не уходят — и разбираться человек идёт не в программу, а к почтовому администратору.</summary>
public class MailSecretsFileTests
{
    [Fact]
    public void Понимает_env()
    {
        var file = MailSecretsFile.Parse(
            "SMTP_HOST=mail.company.ru\nSMTP_PORT=465\nSMTP_USER=robot\nSMTP_PASSWORD=s3cret\nSMTP_FROM=robot@company.ru\nSMTP_SSL=true");

        Assert.True(file.Ok);
        Assert.Equal("mail.company.ru", file.Host);
        Assert.Equal(465, file.Port);
        Assert.Equal("robot", file.User);
        Assert.Equal("s3cret", file.Password);
        Assert.Equal("robot@company.ru", file.From);
        Assert.True(file.UseSsl);
    }

    /// <summary>В json порт — число, а SSL — логическое значение, не строки. Брать только строки
    /// значило бы молча их не заметить и оставить прежний порт.</summary>
    [Fact]
    public void Понимает_json_с_числом_и_логическим_значением()
    {
        var file = MailSecretsFile.Parse(
            "{\"smtp\":{\"host\":\"smtp.yandex.ru\",\"port\":587,\"ssl\":true,\"user\":\"a@b.ru\",\"password\":\"p\"}}");

        Assert.True(file.Ok);
        Assert.Equal("smtp.yandex.ru", file.Host);
        Assert.Equal(587, file.Port);
        Assert.True(file.UseSsl);
    }

    /// <summary>Письмо от почтового администратора, написанное словами и по-русски. Самый частый
    /// способ, которым реквизиты и приезжают.</summary>
    [Fact]
    public void Понимает_письмо_по_русски()
    {
        var file = MailSecretsFile.Parse(
            "Сервер: mail.antarus.ru\nПорт: 25\nЛогин: finder\nПароль: qwerty123\nОт кого: finder@antarus.ru");

        Assert.True(file.Ok);
        Assert.Equal("mail.antarus.ru", file.Host);
        Assert.Equal(25, file.Port);
        Assert.Equal("finder", file.User);
        Assert.Equal("finder@antarus.ru", file.From);
    }

    /// <summary>Адрес со схемой и портом внутри. Резать по первому двоеточию обязательно — иначе
    /// «smtps://mail.company.ru» разрезалось бы по собственному, и хостом стало бы «smtps».</summary>
    [Fact]
    public void Схема_отрезается_а_адрес_не_ломается_о_своё_двоеточие()
    {
        var file = MailSecretsFile.Parse("smtp_host = smtps://mail.company.ru");

        Assert.True(file.Ok);
        Assert.Equal("mail.company.ru", file.Host);
    }

    /// <summary>Логин-ящик становится отправителем сам. Так почти всегда и есть, и просить вписать
    /// то же самое второй раз — работа ни за чем.</summary>
    [Fact]
    public void Логин_ящик_становится_отправителем()
    {
        var file = MailSecretsFile.Parse("host: mail.ru\nuser: robot@mail.ru\npassword: x");

        Assert.Equal("robot@mail.ru", file.From);
    }

    /// <summary>Не сказано про SSL — настройку не трогаем. Молча выключить SSL у сервера, который
    /// без него не работает, значит получить отправку, падающую без объяснимой причины.</summary>
    [Fact]
    public void Молчание_про_ssl_не_выключает_его()
    {
        var file = MailSecretsFile.Parse("host: mail.ru\nuser: a@b.ru\npassword: x");

        Assert.Null(file.UseSsl);
    }

    /// <summary>Порт не разобрался — 0, то есть «оставить прежний». Подставить 0 в настройки значило
    /// бы молча сломать отправку.</summary>
    [Fact]
    public void Непонятный_порт_не_превращается_в_ноль_порт()
    {
        var file = MailSecretsFile.Parse("host: mail.ru\nport: пятьсот восемьдесят семь");

        Assert.Equal(0, file.Port);
    }

    /// <summary>Без адреса сервера подключаться некуда — это отказ, а не «половина настроек».</summary>
    [Fact]
    public void Без_адреса_сервера_честный_отказ()
    {
        var file = MailSecretsFile.Parse("user: a@b.ru\npassword: x");

        Assert.False(file.Ok);
        Assert.Contains("адрес", file.Error!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Пустой_и_двоичный_файл_отвергаются()
    {
        Assert.False(MailSecretsFile.Parse("").Ok);
        Assert.False(MailSecretsFile.Parse("PK\u0003\u0004\u0000\u0000binary").Ok);
    }

    /// <summary>Пароль не должен попадать в печать записи: разбор живёт ровно там, где ошибку
    /// хочется куда-нибудь вывести.</summary>
    [Fact]
    public void Пароль_не_печатается()
    {
        var text = MailSecretsFile.Parse("host: mail.ru\nuser: a@b.ru\npassword: sup3rs3cret").ToString();

        Assert.DoesNotContain("sup3rs3cret", text);
    }

    /// <summary>Закомментированный пример не должен перебивать настоящую строку.</summary>
    [Fact]
    public void Закомментированный_пример_не_выигрывает()
    {
        var file = MailSecretsFile.Parse("# smtp_host = example.com\nsmtp_host = mail.company.ru");

        Assert.Equal("mail.company.ru", file.Host);
    }

    /// <summary>Один и тот же ключ дважды — берётся ПЕРВЫЙ.
    ///
    /// Заведён после того, как проверка мутацией показала: тест про закомментированный пример выше
    /// на самом деле проверял отсечение комментариев, а не правило «выигрывает первое» — строка с
    /// решёткой до этого правила просто не доходит. Заменить первое значение последним можно было
    /// незаметно для всего набора.
    ///
    /// Правило важно на настоящих файлах: в .env рабочая строка обычно стоит сверху, а ниже лежат
    /// прошлые значения, оставленные «на всякий случай».</summary>
    [Fact]
    public void Из_двух_одинаковых_строк_берётся_первая()
    {
        var file = MailSecretsFile.Parse(
            "smtp_host = mail.company.ru\nsmtp_host = old-mail.company.ru\nport = 587\nport = 25");

        Assert.Equal("mail.company.ru", file.Host);
        Assert.Equal(587, file.Port);
    }
}
