using AntarusPoFinder.App.Services;

namespace AntarusPoFinder.Tests;

/// <summary>Поиск обновляется по мере ввода. Здесь — правила, по которым решается, запускать ли
/// поиск после правки текста; сам таймер и отрисовка живут в SearchView и тестом не поднимаются.</summary>
public class SearchLiveQueryTests
{
    /// <summary>Пауза заметно короче осознанной паузы в наборе, но достаточная, чтобы «НГР» не
    /// искалось трижды по дороге.</summary>
    [Fact]
    public void DebounceIsInSensibleRange()
    {
        Assert.InRange(SearchLiveQuery.DebounceMs, 200, 400);
    }

    /// <summary>«Схемы» — обход второго диска (сетевая шара, минуты); по каждой паузе в наборе его
    /// гонять нельзя, там остаются Enter и кнопка «Найти».</summary>
    [Fact]
    public void AutoSearch_NotInSchemasMode()
    {
        Assert.True(SearchLiveQuery.AutoSearchApplies(schemasMode: false));
        Assert.False(SearchLiveQuery.AutoSearchApplies(schemasMode: true));
    }

    [Theory]
    [InlineData("НГР", false, true)]
    [InlineData("", true, true)]   // пустой запрос с фильтрами — осмысленное «покажи всё такое»
    [InlineData("   ", true, true)]
    [InlineData("", false, false)] // стёрли всё и фильтров нет — выдачу надо очистить, а не искать
    [InlineData("   ", false, false)]
    public void HasSomethingToSearch(string query, bool hasFilters, bool expected) =>
        Assert.Equal(expected, SearchLiveQuery.HasSomethingToSearch(query, hasFilters));

    /// <summary>Сравнение по обрезанному тексту: дописанный в конце пробел выдачу не меняет, а
    /// перезапуск стоил бы повторного обхода диска в карточках.</summary>
    [Theory]
    [InlineData("НГР", "НГР", false)]
    [InlineData("НГР ", "НГР", false)]
    [InlineData("НГР", "НГ", true)]
    [InlineData("", "НГР", true)]
    [InlineData("нгр", "НГР", true)] // другой регистр — другой запрос, поиск перезапускается
    public void QueryChanged_ComparesTrimmed(string typed, string lastSearched, bool expected) =>
        Assert.Equal(expected, SearchLiveQuery.QueryChanged(typed, lastSearched));
}
