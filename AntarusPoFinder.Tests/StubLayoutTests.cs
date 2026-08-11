using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Макет страницы-заглушки «Инструкция в разработке». Эту страницу видит заказчик, наведя
/// телефон на наклейку до того, как инструкцию допишут, — то есть это оформление, а не техническая
/// затычка, и менять в ней слово не должно означать выпуск релиза.</summary>
public class StubLayoutTests
{
    [Fact]
    public void Default_KeepsWhatWasHardcodedBefore()
    {
        // Молчаливая смена вида заглушки у всех разом — не то, чего ждут от обновления.
        Assert.Equal(InstructionStub.Text, StubLayout.Default.Title);
        Assert.Contains("заменится настоящей инструкцией", StubLayout.Default.Hint);
    }

    [Fact]
    public void Fill_SubstitutesTheVersion()
    {
        var layout = StubLayout.Default with { Footer = $"Версия {StubLayout.VersionPlaceholder}" };
        Assert.Equal("Версия 1.0.0005.0001", layout.Fill(layout.Footer, "1.0.0005.0001"));
    }

    [Fact]
    public void Fill_WithoutAVersion_DoesNotLeaveThePlaceholderVisible()
    {
        // Общая папка контроллера принадлежит всем его версиям сразу — версии там нет и быть не
        // может, и «Версия {версия}» на странице у заказчика выглядело бы поломкой.
        var layout = StubLayout.Default with { Footer = StubLayout.VersionPlaceholder };
        Assert.Equal("", layout.Fill(layout.Footer, ""));
        Assert.Equal("", layout.Fill(layout.Footer, null));
    }

    [Fact]
    public void Sane_RefusesSizesThatWouldMakeThePageUnreadable()
    {
        var broken = StubLayout.Default with { TitleSize = 0, HintSize = 10, MutedTone = 999 };
        var fixedUp = broken.Sane();

        Assert.True(fixedUp.TitleSize > 0, "нулевой заголовок сделал бы страницу пустой");
        Assert.True(fixedUp.HintSize <= 0.08, "пояснение во весь лист нечитаемо");
        Assert.True(fixedUp.MutedTone <= 200, "почти белый текст на белом не виден");
    }

    [Fact]
    public void Sane_EmptyTitle_FallsBackToTheStandardWording()
    {
        Assert.Equal(InstructionStub.Text, (StubLayout.Default with { Title = "   " }).Sane().Title);
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        var layout = StubLayout.Default with
        {
            Title = "Инструкция готовится",
            Footer = "АМПЕРУС",
            ShowFrame = true,
            TitleSize = 0.05,
        };

        var back = StubLayout.Parse(layout.ToJson());

        Assert.Equal("Инструкция готовится", back.Title);
        Assert.Equal("АМПЕРУС", back.Footer);
        Assert.True(back.ShowFrame);
        Assert.Equal(0.05, back.TitleSize);
    }

    [Fact]
    public void Parse_BrokenJson_FallsBackToDefaultInsteadOfThrowing()
    {
        // Испорченная настройка не должна оставить папку «Инструкция» вовсе без заглушки.
        Assert.Equal(StubLayout.Default.Title, StubLayout.Parse("{сломано").Title);
        Assert.Equal(StubLayout.Default.Title, StubLayout.Parse("").Title);
        Assert.Equal(StubLayout.Default.Title, StubLayout.Parse(null).Title);
    }

    [Theory]
    [InlineData("инструкция_1.0.0005.0001.pdf", "1.0.0005.0001")]
    [InlineData(@"C:\ПО\Инструкция\инструкция_2.1.0042.0001.20260422_1348.pdf", "2.1.0042.0001.20260422_1348")]
    [InlineData("Инструкция в разработке.pdf", "")]
    [InlineData("какой-то документ.pdf", "")]
    public void VersionFromFileName_ReadsBackWhatBuildFileNameWrote(string path, string expected) =>
        Assert.Equal(expected, InstructionNaming.VersionFromFileName(path));
}
