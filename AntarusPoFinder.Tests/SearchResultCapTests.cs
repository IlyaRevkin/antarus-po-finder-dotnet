using AntarusPoFinder.App.Views;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Потолок отрисовки выдачи поиска по схемам. Появился по живому прогону: запрос «1» по
/// диску с десятками тысяч файлов нарисовал 23 446 карточек в невиртуализованном StackPanel, и окно
/// перестало отвечать — то есть широкий запрос вешал программу ровно так же, как до вынесения обхода
/// диска в фон.</summary>
public class SearchResultCapTests
{
    [Fact]
    public void Describe_shows_plain_count_when_everything_fits()
    {
        Assert.Equal("17", SearchResultCap.Describe(matched: 17, shown: 17));
        Assert.Equal("0", SearchResultCap.Describe(matched: 0, shown: 0));
    }

    [Fact]
    public void Describe_says_how_many_of_them_are_on_screen_when_capped()
    {
        Assert.Equal("23446 (показаны первые 300)", SearchResultCap.Describe(matched: 23446, shown: 300));
    }

    /// <summary>Потолок общий для обеих выдач по схемам — потоковой и мгновенной из кэша. Разойдись
    /// они, кэш-путь (тот самый, что вешал окно) снова рисовал бы больше потоковой.</summary>
    [Fact]
    public void Cap_is_a_sane_single_number()
    {
        Assert.InRange(SearchResultCap.MaxCards, 50, 1000);
    }
}
