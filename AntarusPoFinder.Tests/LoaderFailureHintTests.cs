using AntarusPoFinder.Core.Loader;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ПОДСКАЗКА ПРИ ОТКАЗЕ ДОЛЖНА НАЗЫВАТЬ ДЕЙСТВИЕ.
///
/// Заведено по живым журналам заливок, которые пошли приходить 21.10.2026 (LoaderDiagnostics).
/// Первое, что в них видно, — не окирпичивание, а это: машина NALADKA1, 22.10.2026, четырнадцать
/// отказов «Активный USB/RNDIS Ethernet адаптер ПЛК не найден» за две минуты, местами по четыре
/// нажатия за две секунды. Потом та же прошивка ушла с первого раза — по Ethernet.
///
/// То есть человек жал «Повторить», ничего не меняя, потому что подсказка была одна на все случаи и
/// не отвечала на вопрос «что именно сейчас не так». Тексты проверяются тестом, потому что они и
/// есть решение задачи: подсказка, которая не называет действие, не работает.</summary>
public class LoaderFailureHintTests
{
    [Fact]
    public void Нет_адаптера_USB_предлагает_Ethernet()
    {
        var advice = LoaderFailureHint.For(
            "Не удалось подключиться к ПЛК: Активный USB/RNDIS Ethernet адаптер ПЛК не найден.",
            PlcConnectionMode.Usb);

        Assert.Equal(LoaderFailureHint.Remedy.SwitchToEthernet, advice.Remedy);
        Assert.True(advice.HasButton);
        Assert.Contains("питание", advice.Text);
        Assert.Contains("Ethernet", LoaderFailureHint.ButtonCaption(advice.Remedy));
        Assert.Equal(PlcConnectionMode.Ethernet, LoaderFailureHint.TargetMode(advice.Remedy));
    }

    [Fact]
    public void Молчание_по_Ethernet_предлагает_USB()
    {
        var advice = LoaderFailureHint.For(
            "Не удалось подключиться к ПЛК: DHCP-запрос не получил ответа.",
            PlcConnectionMode.Ethernet);

        Assert.Equal(LoaderFailureHint.Remedy.SwitchToUsb, advice.Remedy);
        Assert.Equal(PlcConnectionMode.Usb, LoaderFailureHint.TargetMode(advice.Remedy));
    }

    /// <summary>С третьего одинакового отказа подряд подсказка говорит прямо, что повтор ничего не
    /// изменит. Именно столько раз подряд её и жмут, судя по журналам.</summary>
    [Fact]
    public void Повтор_одной_ошибки_подряд_называется_своим_именем()
    {
        var once = LoaderFailureHint.For("USB адаптер не найден", PlcConnectionMode.Usb, repeatCount: 1);
        var again = LoaderFailureHint.For("USB адаптер не найден", PlcConnectionMode.Usb, repeatCount: 3);

        Assert.DoesNotContain("повторяется подряд", once.Text);
        Assert.Contains("повторяется подряд", again.Text);
    }

    /// <summary>Незнакомый отказ не остаётся без слов: общая подсказка про обвязку проверена
    /// практикой и остаётся на месте.</summary>
    [Fact]
    public void Незнакомая_ошибка_получает_общую_подсказку()
    {
        var advice = LoaderFailureHint.For("Что-то пошло не так внутри Loader", PlcConnectionMode.Usb);

        Assert.Equal(LoaderFailureHint.Remedy.None, advice.Remedy);
        Assert.False(advice.HasButton);
        Assert.Contains("шнур загрузки", advice.Text);
    }

    /// <summary>Пустой текст отказа — тоже случай: Loader мог упасть без сообщения, и подсказка
    /// обязана остаться осмысленной.</summary>
    [Fact]
    public void Пустой_отказ_не_ломает_подсказку()
    {
        var advice = LoaderFailureHint.For(null, PlcConnectionMode.Usb);

        Assert.False(string.IsNullOrWhiteSpace(advice.Text));
    }
}
