using System.Collections.Generic;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Кириллица в адресе на хостинге. Диск у нас весь русский, а ссылка под QR и ключ объекта
/// в бакете обязаны быть латинскими — иначе по дороге к телефону адрес перекодирует кто угодно
/// (почта, мессенджер, S3-клиент), а подпись SigV4 считается по точной байтовой форме ключа.
///
/// Главное свойство, которое здесь и проверяется: ссылка и ключ считаются ОДНИМ И ТЕМ ЖЕ
/// справочником. Ссылку печатает одна машина, файл выкладывает другая — разойдись у них перевод хоть
/// одного сегмента, наклейка поведёт в пустоту.</summary>
public class TransliterationTests
{
    [Theory]
    [InlineData("Инструкция", "Instrukciya")]
    [InlineData("Прошивка", "Proshivka")]
    [InlineData("Карта ВВ", "Karta_VV")]
    [InlineData("Карта Modbus", "Karta_Modbus")]
    [InlineData("ПЖ", "PZH")]
    [InlineData("НГР-КНС", "NGR-KNS")]
    [InlineData("ВЗУ-ПИ", "VZU-PI")]
    [InlineData("ОПЦ", "OPC")]
    [InlineData("Щит", "Schit")]
    [InlineData("Подъезд", "Podezd")]
    public void Auto_TranslatesFolderNames(string source, string expected) =>
        Assert.Equal(expected, Transliteration.Auto(source));

    [Theory]
    [InlineData("SMH5", "SMH5")]
    [InlineData("1.0.0005.0001", "1.0.0005.0001")]
    [InlineData("HMI", "HMI")]
    public void Auto_LeavesLatinAndNumbersAlone(string source, string expected) =>
        Assert.Equal(expected, Transliteration.Auto(source));

    [Fact]
    public void Auto_ReplacesEverythingUnsafeForAnAddress()
    {
        // Пробелы, кавычки, плюсы и запятые ломаются при пересылке ссылки в мессенджере.
        Assert.Equal("shkaf_2_novyy", Transliteration.Auto("шкаф + 2, новый"));
        Assert.Equal("A_B", Transliteration.Auto("  A / B  "));
        // Имя из одних непереводимых знаков не должно исчезнуть: иначе схлопнутся два уровня пути.
        Assert.Equal("_", Transliteration.Auto("+++"));
    }

    [Fact]
    public void Segment_KeepsFileExtension()
    {
        var map = TranslitMap.Empty;
        Assert.Equal("instrukciya_2.1.0042.0001.pdf", map.Segment("инструкция_2.1.0042.0001.pdf"));
        Assert.Equal("karta_vv.xlsx", map.Segment("карта_вв.XLSX"));
    }

    [Fact]
    public void Segment_ManualOverrideWins()
    {
        var map = TranslitMap.Empty.With("Инструкция", "manual").With("ПЖ", "fire");

        Assert.Equal("manual", map.Segment("Инструкция"));
        Assert.Equal("manual", map.Segment("инструкция")); // регистр ключа не важен
        Assert.Equal("fire", map.Segment("ПЖ"));
        Assert.Equal("Proshivka", map.Segment("Прошивка")); // не переопределено — автоперевод
    }

    [Fact]
    public void Segment_OverrideAppliesToFileStemToo()
    {
        var map = TranslitMap.Empty.With("инструкция_2.1", "manual_2.1");
        Assert.Equal("manual_2.1.pdf", map.Segment("инструкция_2.1.pdf"));
    }

    [Fact]
    public void With_EmptyValue_RemovesTheOverride()
    {
        var map = TranslitMap.Empty.With("ПЖ", "fire");
        Assert.Equal("fire", map.Segment("ПЖ"));

        var back = map.With("ПЖ", "");
        Assert.Equal("PZH", back.Segment("ПЖ"));
        Assert.Equal(0, back.Count);
    }

    [Fact]
    public void RoundTripsThroughJson_WithStableOrder()
    {
        var map = TranslitMap.Empty.With("Прошивка", "firmware").With("Инструкция", "manual");
        var json = map.ToJson();

        Assert.Equal(json, TranslitMap.Parse(json).ToJson()); // порядок стабилен — не «правка» для синхронизации
        Assert.Equal("manual", TranslitMap.Parse(json).Segment("Инструкция"));
        Assert.Equal("firmware", TranslitMap.Parse(json).Segment("Прошивка"));
    }

    [Fact]
    public void Parse_BrokenJson_FallsBackToAutoInsteadOfThrowing()
    {
        var map = TranslitMap.Parse("{это не json");
        Assert.Equal(0, map.Count);
        Assert.Equal("Instrukciya", map.Segment("Инструкция"));
    }

    [Fact]
    public void PathAndUrl_AgreeOnEverySegment()
    {
        // Ровно то, ради чего справочник общий: ключ объекта и хвост ссылки обязаны совпасть.
        var map = TranslitMap.Empty.With("Инструкция", "manual");
        var settings = new S3Settings(
            Endpoint: "https://s3.twcstorage.ru", Bucket: "amperus", Region: "ru-1", Prefix: "",
            AccessKey: "id", SecretKey: "secret", WebUrl: "https://fs.elitacompany.ru", Enabled: true)
        {
            Translit = map,
        };

        const string root = @"Z:\ПО";
        var file = @"Z:\ПО\ПЖ\2.0\SMH5\1.0.0005.0001\Инструкция\инструкция_1.0.0005.0001.pdf";
        var relative = LabelLinkBuilder.RelativeTo(root, file)!;

        var key = settings.KeyFor(relative);
        var url = LabelLinkBuilder.BuildUrl(settings.WebUrl, root, file, map);

        Assert.Equal("PZH/2.0/SMH5/1.0.0005.0001/manual/instrukciya_1.0.0005.0001.pdf", key);
        Assert.Equal(settings.WebUrl + "/" + key, url);
        Assert.DoesNotContain('П', url!);
    }

    [Fact]
    public void Url_WithoutMap_KeepsPreviousBehaviour()
    {
        // Ссылка на внутренний ресурс переводить ничего не должна — там кириллица законна.
        const string root = @"Z:\ПО";
        var url = LabelLinkBuilder.BuildUrl("http://server/inst", root, @"Z:\ПО\ПЖ\Инструкция\файл.pdf");
        Assert.Equal("http://server/inst/ПЖ/Инструкция/файл.pdf", url);
    }

    [Fact]
    public void KeyFor_KeepsBucketPrefix()
    {
        var settings = new S3Settings("https://s3", "amperus", "ru-1", "po", "id", "secret", "https://fs", true)
        {
            Translit = TranslitMap.Empty,
        };
        Assert.Equal("po/PZH/Instrukciya", settings.KeyFor(@"ПЖ\Инструкция"));
    }

    [Fact]
    public void FromPairs_SkipsEmptyRows()
    {
        var map = TranslitMap.FromPairs(new[]
        {
            new KeyValuePair<string, string>("ПЖ", "fire"),
            new KeyValuePair<string, string>("  ", "мусор"),
            new KeyValuePair<string, string>("НГР", "   "),
        });

        Assert.Equal(1, map.Count);
        Assert.Equal("fire", map.Segment("ПЖ"));
    }
}
