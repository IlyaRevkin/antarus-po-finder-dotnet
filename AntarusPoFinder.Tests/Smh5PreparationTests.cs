using AntarusPoFinder.Core.Loader;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>SMH5: ПОЧЕМУ ОНИ ОКИРПИЧИВАЛИСЬ — И ПОЧЕМУ ЛЕЧИТЬ ЭТО ФОРМАТИРОВАНИЕМ БЫЛО НЕВЕРНО.
///
/// 15.10.2026, срочное сообщение Ильи: «при прошивке через фаиндер SMH5 они окирпичиваются… уходят
/// в цикл бесконечной перезагрузки, потому приходится форматировать и заливать через smlogic».
/// Тогда решили, что контроллеру не хватает подготовки ПЛК, и форматирование для SMH5 сделали
/// обязательным.
///
/// 21.10.2026 Илья проверил на железе: «проблема точно в фаиндере, так как и через лоджик и через
/// лоадер всё ок и без форматирования». Значит дело было не в подготовке, а в том, ЧТО Finder
/// отправлял в контроллер: голый .lfs вместо проекта целиком. У SMH5 проект состоит из LFS и ELF,
/// и от одного LFS в контроллере остаётся чужая ELF-часть (разбор — в DeployArtifact).
///
/// Поэтому здесь закреплены ДВА правила сразу:
/// 1. выбор «залить как есть» наладчику возвращён — навязанное форматирование стирало рабочий
///    проект и перешивало ядро там, где достаточно залить правильный файл;
/// 2. предупреждение осталось, но говорит правду: подготовка нужна тогда, когда контроллер уже в
///    перезагрузочной петле.</summary>
public class Smh5PreparationTests
{
    /// <summary>Прошлый выбор наладчика снова наследуется и для SMH5: форматирование перестало
    /// быть обязательным, когда нашлась настоящая причина.</summary>
    [Fact]
    public void У_SMH5_подсвечен_прошлый_выбор_как_и_у_всех()
    {
        Assert.Equal(PlcPreparationAnswer.Keep,
            PlcPreparation.DefaultAnswer(rememberedFormat: false, controllerName: "SMH5"));
        Assert.Equal(PlcPreparationAnswer.Format,
            PlcPreparation.DefaultAnswer(rememberedFormat: true, controllerName: "SMH5"));
    }

    /// <summary>Для остальных контроллеров поведение прежнее: подсвечен прошлый выбор. Иначе эта
    /// правка тихо поменяла бы привычный порядок работы на всех объектах разом.</summary>
    [Theory]
    [InlineData("SMH4")]
    [InlineData("PIXEL2-1321")]
    [InlineData("")]
    [InlineData(null)]
    public void У_остальных_контроллеров_по_прежнему_подсвечен_прошлый_выбор(string? controller)
    {
        Assert.Equal(PlcPreparationAnswer.Keep,
            PlcPreparation.DefaultAnswer(rememberedFormat: false, controllerName: controller));
        Assert.Equal(PlcPreparationAnswer.Format,
            PlcPreparation.DefaultAnswer(rememberedFormat: true, controllerName: controller));
    }

    /// <summary>Предупреждение должно называть само явление, а не быть вежливым «будьте
    /// внимательны»: наладчик читает его за секунду перед необратимым действием.</summary>
    [Fact]
    public void Предупреждение_называет_что_именно_случится()
    {
        var text = PlcPreparation.WarningFor("SMH5");

        Assert.Contains("перезагруз", text);
        Assert.Contains("SMH5", text);
        // И не зовёт форматировать «на всякий случай»: это стирание рабочего проекта.
        Assert.Contains("целиком", text);
    }

    /// <summary>Там, где предупреждать не о чем, окно не должно показывать пустую рамку: рамка на
    /// каждом запуске приучает не читать её вовсе — и тогда она не сработает там, где нужна.</summary>
    [Theory]
    [InlineData("SMH4")]
    [InlineData("PIXEL2-1321")]
    [InlineData("")]
    public void Для_остальных_предупреждения_нет(string controller)
    {
        Assert.Equal("", PlcPreparation.WarningFor(controller));
    }

    /// <summary>Правило выводится из семейства, а не из точного совпадения имени: в справочнике
    /// контроллеров модификации пишут по-разному («SMH5», «SMH5-0000»), и пропустить такую строку
    /// значило бы оставить тот же кирпич.</summary>
    [Theory]
    [InlineData("SMH5")]
    [InlineData("smh5")]
    [InlineData("SMH5-0000")]
    public void Семейство_распознаётся_независимо_от_написания(string controller)
    {
        Assert.True(PlcPreparation.NeedsFormatByDefault(controller));
    }

    /// <summary>Выбор есть у ВСЕХ, включая SMH5. Отнимать его было лечением не той причины:
    /// форматирование перешивает ядро и стирает проект, а ломал заливку неверно выбранный файл.</summary>
    [Fact]
    public void У_SMH5_вариант_без_форматирования_снова_доступен()
    {
        Assert.True(PlcPreparation.AllowKeep("SMH5"));
    }

    [Theory]
    [InlineData("SMH4")]
    [InlineData("PIXEL2-1321")]
    [InlineData("")]
    public void У_остальных_контроллеров_выбор_остаётся(string controller)
    {
        Assert.True(PlcPreparation.AllowKeep(controller));
    }
}
