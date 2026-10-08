using AntarusPoFinder.Core.Domain;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Строка под названием прошивки на карточке выдачи.
///
/// Жалоба Ильи 25.09.2026, дословно: «всё ещё не закрыт баг, что пишет конфигурация; название
/// конфигурации (по типу „2 насоса") уже достаточно, слово конфигурация и какая конфигурация
/// избыточно для вывода».
///
/// До этого строка собиралась прямо в обработчике карточки, и проверить её было нечем, кроме глаз, —
/// поэтому каждая правка подписи возвращалась новой жалобой.</summary>
public class FwCardMetaTests
{
    private static string Line(string execution = "", string configName = "", string controller = "",
        string equipmentType = "", string workType = "", string uploadDate = "", int usageCount = 0,
        bool isOpc = false, string requestNum = "", string cabinetSn = "") =>
        FwCardMeta.Line(execution, configName, controller, equipmentType, workType, uploadDate, usageCount,
            isOpc, requestNum, cabinetSn);

    /// <summary>Жалоба дословно: «я загрузил опц, нигде не написано что это опц, в итоге номер
    /// заявки или сн не отображается в карточке». Всё три вещи — в строке, и ОПЦ первым словом:
    /// это единственная пометка, которая меняет смысл всей карточки.</summary>
    [Fact]
    public void Опц_называет_себя_опц_и_показывает_оба_номера()
    {
        var line = Line(isOpc: true, requestNum: "01312", cabinetSn: "SN-778899",
            controller: "SMH5", uploadDate: "01.09.2026");

        Assert.StartsWith("ОПЦ", line);
        Assert.Contains("Заявка: 01312", line);
        Assert.Contains("SN: SN-778899", line);
    }

    /// <summary>У номеров подписи ОСТАЮТСЯ, в отличие от названия комплектации: голые «01312» и
    /// «SN-778899» в ряду через точку читаются как мусор, а перепутать их между собой легко — оба
    /// просто числа.</summary>
    [Fact]
    public void Номера_подписаны_чтобы_их_не_перепутали()
    {
        var line = Line(isOpc: true, requestNum: "01312", cabinetSn: "778899");

        Assert.DoesNotContain(FwCardMeta.Separator + "01312", line);
        Assert.Contains("Заявка: 01312", line);
        Assert.Contains("SN: 778899", line);
    }

    /// <summary>Заполнено только одно поле из двух — пустое не оставляет пустой подписи. У ОПЦ так
    /// чаще всего и бывает: обязательно ОДНО из двух (см. OpcFields).</summary>
    [Fact]
    public void Незаполненный_номер_не_оставляет_пустой_подписи()
    {
        var line = Line(isOpc: true, requestNum: "01312");

        Assert.DoesNotContain("SN:", line);
        Assert.DoesNotContain(FwCardMeta.Separator + FwCardMeta.Separator, line);
    }

    /// <summary>Обычная прошивка словом «ОПЦ» себя не называет: пометка должна что-то значить.</summary>
    [Fact]
    public void Обычная_прошивка_не_помечается_как_опц()
    {
        Assert.DoesNotContain("ОПЦ", Line(execution: "2 насоса", controller: "SMH5"));
    }

    /// <summary>Главное. «Конфигурация: 2 конфигурация» не значит вообще ничего.</summary>
    [Fact]
    public void Название_комплектации_пишется_без_слова_конфигурация()
    {
        var line = Line(configName: "2 насоса");

        Assert.Equal("2 насоса", line);
        Assert.DoesNotContain("онфигурация", line);
    }

    [Fact]
    public void Исполнение_идёт_первым_чтобы_его_прочли_сразу()
    {
        var line = Line(execution: "3 и более насосов", configName: "2 насоса", controller: "SMH5");

        Assert.StartsWith("3 и более насосов", line);
    }

    /// <summary>У контроллера подпись ОСТАЁТСЯ. «SMH5» само по себе ни о чём не говорит тому, кто
    /// пришёл в программу впервые, и спутать его с названием шкафа легко — это не тот же случай,
    /// что «2 насоса».</summary>
    [Fact]
    public void У_контроллера_подпись_остаётся()
    {
        Assert.Contains("Контроллер: SMH5", Line(controller: "SMH5"));
    }

    [Fact]
    public void Пустые_поля_не_дают_пустых_кусков_и_лишних_разделителей()
    {
        var line = Line(configName: "2 насоса", uploadDate: "01.09.2026");

        Assert.Equal("2 насоса" + FwCardMeta.Separator + "01.09.2026", line);
        Assert.DoesNotContain(FwCardMeta.Separator + FwCardMeta.Separator, line);
    }

    [Fact]
    public void Пробельное_значение_считается_пустым()
    {
        Assert.Equal("", Line(configName: "   ", execution: "\t"));
    }

    /// <summary>«1 раз», а не «1 раз(а)»: счёт читают люди, а не программа.</summary>
    [Fact]
    public void Счётчик_выборов_склоняется_по_человечески()
    {
        Assert.Contains("выбирали 1 раз", Line(usageCount: 1));
        Assert.Contains("выбирали 5 раз", Line(usageCount: 5));
        Assert.DoesNotContain("выбирали", Line(usageCount: 0));
    }

    [Fact]
    public void Обычная_прошивка_без_пометок_даёт_пустую_строку()
    {
        Assert.Equal("", Line());
    }
}
