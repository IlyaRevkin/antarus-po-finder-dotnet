using AntarusPoFinder.Core.Loader;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>SMH5 НЕЛЬЗЯ МОЛЧА ПРЕДЛАГАТЬ ЗАЛИВКУ БЕЗ ПОДГОТОВКИ.
///
/// Сообщение Ильи 15.10.2026, срочное: «при прошивке через фаиндер SMH5 они окирпичиваются. То есть
/// я загружаю вижу прошивку отключаю питание заново подаю и они уходят в цикл бесконечной
/// перезагрузки, потому приходится форматировать и заливать через smlogic».
///
/// Что здесь происходит на самом деле. Finder заливает готовый .lfs из папки версии. У SMH5 (новое
/// поколение, Rockchip) проект в контроллере состоит не только из LFS: есть ещё ELF/ZOP-часть, и
/// чистит её ТОЛЬКО сценарий подготовки formatAndUpdateFirmware (docs/loader/LOADER_AUTOMATION_API.md).
/// Свежий LFS поверх чужой ELF/ZOP-части и даёт ту самую вечную перезагрузку после снятия питания —
/// а ручная заливка через SMLogix «с форматированием» именно этот шаг и выполняет.
///
/// Пока причина не подтверждена на железе, программа не запрещает заливку без подготовки — но
/// перестаёт предлагать её по умолчанию и прямо предупреждает. Прошлый ответ наладчика («в прошлый
/// раз выбирали без форматирования») для таких контроллеров больше не наследуется: именно он и
/// превращал опасный выбор в выбор по привычке.</summary>
public class Smh5PreparationTests
{
    [Fact]
    public void У_SMH5_по_умолчанию_предлагается_подготовка_даже_если_в_прошлый_раз_её_не_делали()
    {
        Assert.Equal(PlcPreparationAnswer.Format,
            PlcPreparation.DefaultAnswer(rememberedFormat: false, controllerName: "SMH5"));
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
}
