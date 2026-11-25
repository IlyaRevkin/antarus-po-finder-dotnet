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

    // ── 24.11.2026: «видел твою попытку починить, но не вылечилось» ──────────────────────────
    //
    // Журнал заливки с машины NALADKA1 (приходит сам, LoaderDiagnostics): appVersion 2.2.8,
    // Loader 2.9.13, connection «ethernet/ Unknown», в журнале — «В настройках Segnetics Loader не
    // нашлось полей подключения». То есть выбор Ethernet физически никуда не уехал, Loader пошёл по
    // своему USB и вернул «Активный USB/RNDIS Ethernet адаптер ПЛК не найден», а окно в ответ
    // уверенно написало «Loader настроен на USB». Ниже заперты оба урока: читать чужие написания
    // режима и не выдавать свою догадку за настройки Loader.

    /// <summary>Чужой файл пишет режим как ему удобно. Прежнее точное сравнение отвечало «не знаю»
    /// даже на то, что успело прочитать.</summary>
    [Theory]
    [InlineData("UsbRndis", PlcConnectionMode.Usb)]
    [InlineData("usb_rndis", PlcConnectionMode.Usb)]
    [InlineData("Ethernet_DHCP", PlcConnectionMode.Ethernet)]
    [InlineData("EthernetStatic", PlcConnectionMode.Ethernet)]
    [InlineData("lan", PlcConnectionMode.Ethernet)]
    [InlineData("", PlcConnectionMode.Unspecified)]
    [InlineData("192.168.1.10", PlcConnectionMode.Unspecified)]
    public void ParseMode_ПониматьЧужиеНаписания(string raw, PlcConnectionMode expected) =>
        Assert.Equal(expected, LoaderConnectionSettings.ParseMode(raw));

    /// <summary>Поле «connection» с адресом внутри — не режим. Имена полей мы берём широко, и это
    /// единственная защита от того, чтобы записать «ethernet» поверх чужого адреса.</summary>
    [Fact]
    public void ПолеСНепонятнымЗначением_НеТрогаемВовсе()
    {
        var path = TempSettings("{\"connection\": \"192.168.1.10\"}");
        try
        {
            var before = File.ReadAllText(path);
            var applied = LoaderConnectionSettings.Apply(PlcConnectionMode.Ethernet, null, null, path);

            Assert.False(applied.Applied);
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally { Cleanup(path); }
    }

    /// <summary>А вот поле с узнаваемым режимом правится, как бы оно ни называлось и как бы режим в
    /// нём ни был записан.</summary>
    [Fact]
    public void ПолеСУзнаваемымРежимом_Переписывается()
    {
        var path = TempSettings("{\"plc\": {\"link\": \"UsbRndis\"}}");
        try
        {
            var (applied, state) = LoaderConnectionSettings.ApplyAndDescribe(
                PlcConnectionMode.Ethernet, null, null, path);

            Assert.True(applied.Applied);
            Assert.True(state.Honored);
            Assert.Equal(PlcConnectionMode.Ethernet, state.Effective);
            // Старое значение остаётся в журнале: если Loader окажется разборчив к написанию,
            // это будет видно сразу, а не ещё через круг переписки.
            Assert.Contains("UsbRndis", Assert.Single(applied.ChangedKeys));
        }
        finally { Cleanup(path); }
    }

    /// <summary>В настройках УЖЕ стоит ровно то, что выбрано. Это успех, а не «не нашлось полей»:
    /// прежний код мерил успех числом изменений и на таком файле пугал несуществующим расхождением.</summary>
    [Fact]
    public void КогдаВLoaderУжеСтоитНужное_ЭтоУспехАНеОтказ()
    {
        var path = TempSettings("{\"connectionMode\": \"ethernet\"}");
        try
        {
            var (applied, state) = LoaderConnectionSettings.ApplyAndDescribe(
                PlcConnectionMode.Ethernet, null, null, path);

            Assert.True(applied.Applied);
            Assert.Empty(applied.ChangedKeys);
            Assert.Null(applied.Message);
            Assert.True(state.Honored);
            Assert.Null(state.Trouble);
        }
        finally { Cleanup(path); }
    }

    /// <summary>ГЛАВНЫЙ СЛУЧАЙ ПОВТОРНОЙ ЖАЛОБЫ: выбран Ethernet, а знакомого поля в чужом файле
    /// нет. Состояние обязано сказать это прямо и назвать выбор НЕдействующим, а в объяснении —
    /// перечислить поля файла: без них мы так и будем гадать, как называется режим в этой версии
    /// Loader.</summary>
    [Fact]
    public void ВыбранEthernet_АПоляВLoaderНет_ЧестноеСообщение()
    {
        var path = TempSettings("{\"ConnectionProfile\": \"default\", \"login\": \"root\"}");
        try
        {
            var (applied, state) = LoaderConnectionSettings.ApplyAndDescribe(
                PlcConnectionMode.Ethernet, null, null, path);

            Assert.False(applied.Applied);
            Assert.False(state.Honored);
            Assert.NotNull(state.TransferProblem);
            Assert.NotNull(state.Trouble);
            Assert.Contains("Ethernet", state.Summary);
            Assert.Contains("не удалось", state.Summary);
            // Имена полей чужого файла — да, значения — нет: там же SSH-учётные данные.
            Assert.Contains("ConnectionProfile", state.Summary);
            Assert.DoesNotContain("root", state.Summary);
        }
        finally { Cleanup(path); }
    }

    /// <summary>И отдельно — окно подготовки. Раньше оно на этом же состоянии писало «Loader сейчас
    /// настроен на Ethernet», хотя настройки Loader не читались вовсе: программа выдавала свой
    /// выбор за его настройки, а человек видел это рядом с ошибкой про USB.</summary>
    [Fact]
    public void ОкноПодготовки_НеВыдаётНашВыборЗаНастройкиLoader()
    {
        var state = LoaderConnectionSettings.Describe(PlcConnectionMode.Ethernet, PlcConnectionMode.Unspecified)
            with { TransferProblem = "поля нет." };

        var obstacle = PlcPreparation.ConnectionObstacle(state);

        Assert.DoesNotContain("Loader сейчас настроен", obstacle);
        Assert.Contains("В программе выбрано Ethernet", obstacle);
        Assert.Contains("поля нет.", obstacle);
    }

    private static string TempSettings(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    private static void Cleanup(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        var backup = path + ".antarus-backup";
        if (File.Exists(backup)) File.Delete(backup);
    }
}
