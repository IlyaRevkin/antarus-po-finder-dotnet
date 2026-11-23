using System.IO;
using System.Text;
using AntarusPoFinder.Core.Loader;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>«ЧЕРЕЗ ЧТО МЫ ГРУЗИМ» — один ответ, а не два спорящих.
///
/// Тикет наладчика от 19.11.2026 дословно: «Статуст подключение противоречит тексту причины, что
/// делает некоторые догадки о том, что переключатель "через что загружаем" выполняет декоративную
/// функцию». Расхождение было настоящее: выбор в карточке хранился у нас, заливка шла по настройкам
/// самого Segnetics Loader, и эти два источника на экране никогда не сверялись.
///
/// Здесь заперт разбор состояния: что показывать человеку при каждом сочетании «выбрано у нас» ×
/// «стоит в Loader», и какой режим считать ДЕЙСТВУЮЩИМ. Последнее важнее всего: решающий голос у
/// Loader, потому что заливку выполняет он и читает свои собственные настройки.</summary>
public class LoaderConnectionStateTests
{
    /// <summary>Loader настроен так же, как выбрано у нас, — противоречия нет, предупреждать не о чем.</summary>
    [Fact]
    public void WhenLoaderAgrees_TheChoiceIsHonoured()
    {
        var state = LoaderConnectionSettings.Describe(PlcConnectionMode.Usb, PlcConnectionMode.Usb);

        Assert.Equal(LoaderConnectionAgreement.Agreed, state.Agreement);
        Assert.True(state.Honored);
        Assert.Equal(PlcConnectionMode.Usb, state.Effective);
        Assert.Null(state.Conflict);
        Assert.Contains("USB", state.Summary);
    }

    /// <summary>ГЛАВНЫЙ СЛУЧАЙ ТИКЕТА: у нас USB, в Loader осталось Ethernet. Заливка пойдёт по
    /// Loader — значит и состояние обязано говорить «Ethernet», а не повторять наш выбор. И сказать
    /// это надо ОДНОЙ строкой, в которой названы оба факта: иначе человек снова увидит «USB» в
    /// одном месте и «Ethernet» в другом.</summary>
    [Fact]
    public void WhenLoaderDisagrees_TheLoaderWins_AndTheTextSaysBoth()
    {
        var state = LoaderConnectionSettings.Describe(PlcConnectionMode.Usb, PlcConnectionMode.Ethernet);

        Assert.Equal(LoaderConnectionAgreement.Conflict, state.Agreement);
        Assert.False(state.Honored);
        Assert.Equal(PlcConnectionMode.Ethernet, state.Effective);
        Assert.NotNull(state.Conflict);
        Assert.Contains("USB", state.Summary);
        Assert.Contains("Ethernet", state.Summary);
        Assert.Equal(state.Conflict, state.Summary);
    }

    /// <summary>Настройки Loader не прочитались. Это «не знаем», а не «значит, по-нашему»: делать
    /// отсюда выводы нельзя, можно только честно предупредить.</summary>
    [Fact]
    public void WhenLoaderSettingsAreUnreadable_TheAnswerIsUnknown_NotOurChoice()
    {
        var state = LoaderConnectionSettings.Describe(PlcConnectionMode.Usb, PlcConnectionMode.Unspecified);

        Assert.Equal(LoaderConnectionAgreement.Unknown, state.Agreement);
        Assert.False(state.Honored);
        // Действующим остаётся наш выбор — больше опереться не на что, — но текст об этом не умалчивает.
        Assert.Equal(PlcConnectionMode.Usb, state.Effective);
        Assert.Contains("не удалось", state.Summary);
    }

    /// <summary>«Как в Loader» — выбора у нас не сделано, спорить не с чем: показываем то, что
    /// стоит у него.</summary>
    [Fact]
    public void WhenWeChoseNothing_WeSimplyReportWhatTheLoaderHas()
    {
        var state = LoaderConnectionSettings.Describe(PlcConnectionMode.Unspecified, PlcConnectionMode.Ethernet);

        Assert.Equal(LoaderConnectionAgreement.Agreed, state.Agreement);
        Assert.Equal(PlcConnectionMode.Ethernet, state.Effective);
        Assert.Contains("Ethernet", state.Summary);
    }

    /// <summary>Перенос выбора и состояние считаются ОДНИМ вызовом, и состояние берётся по факту —
    /// перечитыванием файла, а не по тому, что мы собирались записать.</summary>
    [Fact]
    public void ApplyAndDescribe_ReadsTheFileBack()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, "{\"connectionMode\": \"ethernet\", \"ipAddress\": \"192.168.1.9\"}",
            new UTF8Encoding(false));
        try
        {
            var (applied, state) = LoaderConnectionSettings.ApplyAndDescribe(
                PlcConnectionMode.Usb, ip: null, adapter: null, path);

            Assert.True(applied.Applied);
            Assert.Equal(LoaderConnectionAgreement.Agreed, state.Agreement);
            Assert.Equal(PlcConnectionMode.Usb, state.Effective);
        }
        finally
        {
            File.Delete(path);
            var backup = path + ".antarus-backup";
            if (File.Exists(backup)) File.Delete(backup);
        }
    }

    /// <summary>ВОТ ОТ ЧЕГО ПЕРЕКЛЮЧАТЕЛЬ И КАЗАЛСЯ ДЕКОРАТИВНЫМ: в настройках Loader нет ни одного
    /// знакомого поля, переносить выбор некуда. Раньше это оставалось предупреждением в свёрнутом
    /// журнале; теперь состояние прямо говорит, что выбор не действует.</summary>
    [Fact]
    public void WhenTheChoiceCannotBeTransferred_TheStateAdmitsIt()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, "{\"somethingElse\": \"value\"}", new UTF8Encoding(false));
        try
        {
            var (applied, state) = LoaderConnectionSettings.ApplyAndDescribe(
                PlcConnectionMode.Usb, ip: null, adapter: null, path);

            Assert.False(applied.Applied);
            Assert.False(state.Honored);
            Assert.NotEqual(LoaderConnectionAgreement.Agreed, state.Agreement);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Подготовка ПЛК работает только по USB. Когда выбор не доехал и Loader остался на
    /// Ethernet, предупреждение обязано назвать ОБА режима — иначе получается ровно то, на что
    /// жаловался наладчик: карточка говорит одно, окно подготовки другое.</summary>
    [Fact]
    public void PreparationWarning_NamesBothSides_WhenTheChoiceDidNotReachTheLoader()
    {
        var state = LoaderConnectionSettings.Describe(PlcConnectionMode.Usb, PlcConnectionMode.Ethernet);

        var obstacle = PlcPreparation.ConnectionObstacle(state);

        Assert.True(PlcPreparation.ConnectionBlocks(state));
        Assert.Contains("USB", obstacle);
        Assert.Contains("Ethernet", obstacle);
    }

    /// <summary>Выбран и подтверждён USB — препятствий нет, лишнего предупреждения тоже.</summary>
    [Fact]
    public void PreparationWarning_IsSilent_WhenUsbIsConfirmed()
    {
        var state = LoaderConnectionSettings.Describe(PlcConnectionMode.Usb, PlcConnectionMode.Usb);

        Assert.Equal("", PlcPreparation.ConnectionObstacle(state));
        Assert.False(PlcPreparation.ConnectionBlocks(state));
    }

    /// <summary>Прочитать настройки Loader не удалось: предупреждаем, но не запрещаем — запрет по
    /// догадке остановил бы работу там, где всё исправно.</summary>
    [Fact]
    public void PreparationWarning_WarnsButDoesNotBlock_WhenNothingIsKnown()
    {
        var state = LoaderConnectionSettings.Describe(PlcConnectionMode.Usb, PlcConnectionMode.Unspecified);

        Assert.NotEqual("", PlcPreparation.ConnectionObstacle(state));
        Assert.False(PlcPreparation.ConnectionBlocks(state));
    }
}
