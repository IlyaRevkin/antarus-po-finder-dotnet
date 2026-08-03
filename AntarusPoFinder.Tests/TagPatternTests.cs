using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Подстановка в теге — звёздочка вместо меняющейся части названия шкафа (см.
/// Core/Services/TagPattern.cs). Одна прошивка подходит десятку шкафов, отличающихся амперажом:
/// «…ПЖ-ПП-2-(9-14А)-АВР-FD-Ст», «…-(20-25А)-…» — и вместо десятка почти одинаковых тегов ставится
/// один: «…ПЖ-ПП-2-(*-*А)-АВР-FD-Ст».
///
/// Здесь проверяется сам матчер, отдельно от поиска: краевые случаи (тег без звёздочки, звёздочка в
/// начале/конце, несколько звёздочек, регистр, кириллица) и оба направления сравнения.</summary>
public class TagPatternTests
{
    private const string Template = "Шкаф управления пожарными насосами АМПЕРУС ПЖ-ПП-2-(*-*А)-АВР-FD-Ст";
    private static string Cabinet(string amps) => $"Шкаф управления пожарными насосами АМПЕРУС ПЖ-ПП-2-({amps}А)-АВР-FD-Ст";

    [Fact]
    public void HasWildcard_DetectsOnlyStar()
    {
        Assert.True(TagPattern.HasWildcard("ПЖ-ПП-2-(*-*А)"));
        Assert.False(TagPattern.HasWildcard("ПЖ-ПП-2-(9-14А)"));
        Assert.False(TagPattern.HasWildcard(""));
        Assert.False(TagPattern.HasWildcard(null));
    }

    /// <summary>Ровно тот сценарий, ради которого всё затевалось: один тег закрывает весь ряд
    /// амперажей.</summary>
    [Theory]
    [InlineData("9-14")]
    [InlineData("20-25")]
    [InlineData("2,5-4")]
    [InlineData("100-125")]
    public void Template_MatchesEveryAmperageVariant(string amps) =>
        Assert.True(TagPattern.Matches(Template, Cabinet(amps)));

    /// <summary>Шаблон не должен становиться «совпадает со всем»: другая серия шкафа мимо.</summary>
    [Theory]
    [InlineData("Шкаф управления пожарными насосами АМПЕРУС ПЖ-ПП-3-(9-14А)-АВР-FD-Ст")]
    [InlineData("Шкаф управления пожарными насосами АМПЕРУС ПЖ-ПП-2-(9-14А)-АВР-VD-Ст")]
    [InlineData("Шкаф управления вентиляцией АМПЕРУС ПЖ-ПП-2-(9-14А)-АВР-FD-Ст")]
    [InlineData("Шкаф управления пожарными насосами АМПЕРУС ПЖ-ПП-2-(9-14А)-АВР-FD")]
    public void Template_DoesNotMatchDifferentCabinet(string other) =>
        Assert.False(TagPattern.Matches(Template, other));

    /// <summary>Тег без звёздочки ведёт себя ровно как обычное сравнение строк — ничего «умного» с
    /// давно проставленными тегами не происходит.</summary>
    [Fact]
    public void WithoutWildcard_BehavesAsPlainEquality()
    {
        Assert.True(TagPattern.Matches(Cabinet("9-14"), Cabinet("9-14")));
        Assert.False(TagPattern.Matches(Cabinet("9-14"), Cabinet("20-25")));
        // MatchesEither занимается ТОЛЬКО подстановкой: когда звёздочек нет ни у кого, обычным
        // равенством строк ведает вызывающий (см. Database.Search).
        Assert.False(TagPattern.MatchesEither(Cabinet("9-14"), Cabinet("9-14")));
    }

    [Fact]
    public void StarAtStart_MatchesAnyPrefix()
    {
        Assert.True(TagPattern.Matches("*-АВР-FD-Ст", "ПЖ-ПП-2-(9-14А)-АВР-FD-Ст"));
        Assert.False(TagPattern.Matches("*-АВР-FD-Ст", "ПЖ-ПП-2-(9-14А)-АВР-VD-Ст"));
    }

    [Fact]
    public void StarAtEnd_MatchesAnySuffix()
    {
        Assert.True(TagPattern.Matches("Шкаф управления пожарными*", "Шкаф управления пожарными насосами АМПЕРУС"));
        Assert.False(TagPattern.Matches("Шкаф управления пожарными*", "Шкаф управления вентиляцией"));
    }

    /// <summary>Звёздочка подставляет и пустую строку — «любая последовательность символов, в том
    /// числе никакой»: правило одно, объяснимое одной строкой подсказки.</summary>
    [Fact]
    public void Star_MatchesEmptySequenceToo()
    {
        Assert.True(TagPattern.Matches("Шкаф*", "Шкаф"));
        Assert.True(TagPattern.Matches("*", ""));
        Assert.True(TagPattern.Matches("*Шкаф*", "Шкаф"));
    }

    /// <summary>Несколько звёздочек подряд — то же, что одна: отдельного смысла у «**» нет.</summary>
    [Fact]
    public void ConsecutiveStars_BehaveAsOne()
    {
        Assert.True(TagPattern.Matches("ПЖ-**-Ст", "ПЖ-ПП-2-(9-14А)-АВР-FD-Ст"));
        Assert.True(TagPattern.Matches("*ПЖ*ПП*Ст*", "Шкаф ПЖ ПП Ст"));
    }

    /// <summary>Звёздочка подставляет и буквы, и цифры, и пробелы — не только ампераж (в названиях
    /// варьируется и тип привода, и исполнение).</summary>
    [Fact]
    public void Star_SubstitutesLettersDigitsAndSpaces()
    {
        Assert.True(TagPattern.Matches("Шкаф-*-Ст", "Шкаф-FD-Ст"));
        Assert.True(TagPattern.Matches("Шкаф-*-Ст", "Шкаф-125-Ст"));
        Assert.True(TagPattern.Matches("Шкаф-*-Ст", "Шкаф-FD 125 АВР-Ст"));
    }

    [Fact]
    public void Matching_IsCaseInsensitive_ForCyrillicAndLatin()
    {
        Assert.True(TagPattern.Matches(Template.ToUpperInvariant(), Cabinet("9-14")));
        Assert.True(TagPattern.Matches(Template.ToLowerInvariant(), Cabinet("9-14").ToUpperInvariant()));
        Assert.True(TagPattern.Matches("шкаф-*-fd", "ШКАФ-9-14А-FD"));
    }

    /// <summary>«В обе стороны»: звёздочка может стоять и в теге (наладчик вводит конкретное название
    /// шкафа), и в самом запросе (программист ищет по шаблону).</summary>
    [Fact]
    public void MatchesEither_WorksWithWildcardOnSide()
    {
        Assert.True(TagPattern.MatchesEither(Template, Cabinet("20-25")));   // звёздочка в теге
        Assert.True(TagPattern.MatchesEither(Cabinet("20-25"), Template));   // звёздочка в запросе
        Assert.False(TagPattern.MatchesEither(Template, "Шкаф управления вентиляцией"));
    }

    /// <summary>Звёздочки с обеих сторон — совпадение засчитывается, если сработало хоть одно
    /// направление (осмысленного правила «кто главнее» тут нет).</summary>
    [Fact]
    public void MatchesEither_WildcardOnBothSides()
    {
        Assert.True(TagPattern.MatchesEither("Шкаф*Ст", "Шкаф*"));
        Assert.False(TagPattern.MatchesEither("Шкаф*Ст", "Насос*"));
    }

    [Fact]
    public void EmptyStrings_DoNotMatchAnything()
    {
        Assert.False(TagPattern.Matches("", "Шкаф"));
        Assert.True(TagPattern.Matches("", ""));
        Assert.False(TagPattern.MatchesEither(null, null));
    }
}
