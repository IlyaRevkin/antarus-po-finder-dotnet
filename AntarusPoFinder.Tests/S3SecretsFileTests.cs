using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Разбор присланного хостингом файла с ключами (зона перетаскивания в Настройки → Сетевые
/// диски вместо полей ввода — просьба Ивана Герасимова от 06.08.2026).
///
/// Проверяется главное обещание зоны: файл кладут КАК ЕСТЬ, каким его отдал хостинг. Поэтому тесты —
/// это набор реальных форматов, в которых ключи приходят: env/ini, json (в том числе одной строкой и
/// с вложенностью), csv с шапкой, письмо с подписями по-русски, две голые строки. Ровно так же важна
/// вторая половина: файл, в котором ключей нет, обязан быть отвергнут с внятным текстом, а не
/// сохранён наполовину — половина ключей выглядит как настроенное хранилище ровно до первой
/// выкладки.</summary>
public class S3SecretsFileTests
{
    private const string Access = "AKIAIOSFODNN7EXAMPLE";
    private const string Secret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

    [Fact]
    public void ReadsEnvAndIniFormat()
    {
        var parsed = S3SecretsFile.Parse($"""
            # ключи для бакета amperus
            [default]
            aws_access_key_id = {Access}
            aws_secret_access_key = {Secret}
            region = ru-1
            """);

        Assert.True(parsed.Ok);
        Assert.Equal(Access, parsed.AccessKey);
        Assert.Equal(Secret, parsed.SecretKey);
        Assert.Equal("ru-1", parsed.Region);
        Assert.False(parsed.OrderGuessed);
    }

    /// <summary>json нередко приходит одной строкой — построчный разбор «до первого двоеточия» вытащил
    /// бы из неё мусор, поэтому json разбирается как json.</summary>
    [Fact]
    public void ReadsSingleLineJson()
    {
        var parsed = S3SecretsFile.Parse(
            $$"""{"accessKeyId":"{{Access}}","secretAccessKey":"{{Secret}}","bucket":"amperus"}""");

        Assert.True(parsed.Ok);
        Assert.Equal(Access, parsed.AccessKey);
        Assert.Equal(Secret, parsed.SecretKey);
        Assert.Equal("amperus", parsed.Bucket);
    }

    /// <summary>У AWS-совместимых консолей ключи лежат внутри вложенного объекта.</summary>
    [Fact]
    public void ReadsNestedJson()
    {
        var parsed = S3SecretsFile.Parse($$"""
            {
              "AccessKey": {
                "UserName": "finder",
                "AccessKeyId": "{{Access}}",
                "SecretAccessKey": "{{Secret}}"
              }
            }
            """);

        Assert.True(parsed.Ok);
        Assert.Equal(Access, parsed.AccessKey);
        Assert.Equal(Secret, parsed.SecretKey);
    }

    /// <summary>Выгрузка консоли в csv. Ловушка здесь в колонке «Password» — это пароль от личного
    /// кабинета, а не секретный ключ, и стоит она РАНЬШЕ настоящего ключа: точное имя обязано
    /// перебивать приблизительное независимо от порядка колонок.</summary>
    [Fact]
    public void ReadsCsvAndPrefersTheExactColumnOverPassword()
    {
        var parsed = S3SecretsFile.Parse($"""
            User name,Password,Access key ID,Secret access key,Console login link
            finder,QwErTy123456,{Access},{Secret},https://console.example/login
            """);

        Assert.True(parsed.Ok);
        Assert.Equal(Access, parsed.AccessKey);
        Assert.Equal(Secret, parsed.SecretKey);
    }

    /// <summary>Письмо от хостинга, сохранённое в txt: подписи по-русски, адрес без схемы.</summary>
    [Fact]
    public void ReadsRussianLabelsAndCompletesEndpointScheme()
    {
        var parsed = S3SecretsFile.Parse($"""
            Ключ доступа: {Access}
            Секретный ключ: {Secret}
            Адрес хранилища: s3.twcstorage.ru
            Бакет: amperus
            """);

        Assert.True(parsed.Ok);
        Assert.Equal(Access, parsed.AccessKey);
        Assert.Equal(Secret, parsed.SecretKey);
        Assert.Equal("https://s3.twcstorage.ru", parsed.Endpoint);
        Assert.Equal("amperus", parsed.Bucket);
    }

    /// <summary>Самый частый вид пересылки в переписке — одна строка «идентификатор:секрет».</summary>
    [Fact]
    public void ReadsBareColonPairAndMarksTheOrderAsGuessed()
    {
        var parsed = S3SecretsFile.Parse($"{Access}:{Secret}");

        Assert.True(parsed.Ok);
        Assert.Equal(Access, parsed.AccessKey);
        Assert.Equal(Secret, parsed.SecretKey);
        Assert.True(parsed.OrderGuessed);
    }

    /// <summary>Две голые строки, причём в обратном порядке. Длина различает их надёжнее порядка:
    /// идентификатор короткий, секрет длинный — так у Timeweb и у всех AWS-совместимых.</summary>
    [Fact]
    public void RecognizesSwappedBareLinesByLength()
    {
        var parsed = S3SecretsFile.Parse($"{Secret}\r\n{Access}\r\n");

        Assert.True(parsed.Ok);
        Assert.Equal(Access, parsed.AccessKey);
        Assert.Equal(Secret, parsed.SecretKey);
        Assert.True(parsed.OrderGuessed);
    }

    [Fact]
    public void KeepsQuotedAndTrailingCommaValuesClean()
    {
        var parsed = S3SecretsFile.Parse($"""
            "AccessKeyId": "{Access}",
            'SecretKey' = '{Secret}';
            """);

        Assert.True(parsed.Ok);
        Assert.Equal(Access, parsed.AccessKey);
        Assert.Equal(Secret, parsed.SecretKey);
    }

    /// <summary>Половина ключей — это не «почти настроено», а нерабочее хранилище, и сказать об этом
    /// нужно сразу: иначе выкладка молча не будет происходить, а карточка будет выглядеть заполненной.</summary>
    [Fact]
    public void RejectsAFileWithOnlyOneOfTheTwoKeys()
    {
        var parsed = S3SecretsFile.Parse($"aws_access_key_id = {Access}");

        Assert.False(parsed.Ok);
        Assert.Equal("", parsed.SecretKey);
        Assert.Contains("только Access Key ID", parsed.Error);
    }

    [Fact]
    public void RejectsProseWithoutKeys()
    {
        var parsed = S3SecretsFile.Parse("Добрый день! Ключи вышлю завтра, сегодня не успеваю.");

        Assert.False(parsed.Ok);
        Assert.Contains("не нашлись ключи", parsed.Error);
    }

    /// <summary>Перетащили не тот файл — архив, скан, документ. Ответ должен быть про файл, а не про
    /// разбор: человек ошибся файлом, а не форматом.</summary>
    [Fact]
    public void RejectsBinaryContent()
    {
        var parsed = S3SecretsFile.Parse("PK" + (char)3 + (char)4 + "keys.zip");

        Assert.False(parsed.Ok);
        Assert.Contains("не текстовый файл", parsed.Error);
    }

    [Fact]
    public void RejectsEmptyFile()
    {
        Assert.False(S3SecretsFile.Parse("   \r\n  ").Ok);
        Assert.False(S3SecretsFile.Parse(null).Ok);
    }

    /// <summary>Разбор живёт ровно там, где результат хочется куда-нибудь вывести («что за файл мне
    /// подсунули»), а запись C# по умолчанию печатает ВСЕ свои свойства. Одного «{parsed}» в тексте
    /// сообщения хватило бы, чтобы только что прочитанный Secret Access Key лёг открытым текстом в
    /// журнал или в тикет.</summary>
    [Fact]
    public void ToString_DoesNotRevealTheSecret()
    {
        var parsed = S3SecretsFile.Parse(
            "aws_access_key_id = AKIAIOSFODNN7EXAMPLE\naws_secret_access_key = wJalrXUtnFEMI0000EXAMPLEKEY");

        Assert.True(parsed.Ok);
        Assert.DoesNotContain(parsed.SecretKey, parsed.ToString());
        // Access Key ID — не секрет, это лишь идентификатор пары; в диагностике он нужен.
        Assert.Contains(parsed.AccessKey, parsed.ToString());
    }
}
