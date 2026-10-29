using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Tests;

/// <summary>Номер ПО — счётчик: 0001, 0002, … 99999, A0000, A0001 (правило Ильи). Буква здесь
/// старший разряд счёта, а не пометка варианта.
///
/// Главное, что проверяется: в базе номер остался ЦЕЛЫМ, а буквенный вид считается кодеком. На целом
/// держатся выдача следующего номера (Database.GetNextSwVersion — MAX+1), сортировка истории и
/// освобождение номера при откате; MAX+1 сам переходит границу 99999 → A0000, потому что это просто
/// 99999 → 100000.</summary>
public class SwNumberCodeTests
{
    [Theory]
    [InlineData(1, "0001")]
    [InlineData(42, "0042")]
    [InlineData(9999, "9999")]
    // Ведущие нули — до четырёх знаков, как было всегда: строка версии это имя папки на сетевом
    // диске, и переход на пять знаков переименовал бы каждую уже лежащую там версию.
    [InlineData(10_000, "10000")]
    [InlineData(99_999, "99999")]
    [InlineData(100_000, "A0000")]
    [InlineData(100_001, "A0001")]
    [InlineData(109_999, "A9999")]
    [InlineData(110_000, "B0000")]
    [InlineData(359_999, "Z9999")]
    public void Format_CountsOnPastTheDigits(int ordinal, string expected) =>
        Assert.Equal(expected, SwNumberCode.Format(ordinal));

    /// <summary>Тот самый переход, ради которого всё затевалось: следующий за 99999 номер (MAX+1 в
    /// базе) печатается как A0000.</summary>
    [Fact]
    public void NextAfter99999_IsA0000()
    {
        int next = SwNumberCode.Parse("99999")!.Value + 1; // ровно то, что делает GetNextSwVersion
        Assert.Equal("A0000", SwNumberCode.Format(next));
    }

    [Theory]
    [InlineData("0001", 1)]
    [InlineData("1", 1)]            // старые папки без дополнения нулями
    [InlineData("0042", 42)]
    [InlineData("99999", 99_999)]
    [InlineData("A0000", 100_000)]
    [InlineData("a0000", 100_000)]  // регистр ввода не важен
    [InlineData("А0000", 100_000)]  // кириллическая «А» — та же раскладка у наладчика
    [InlineData("Z9999", 359_999)]
    public void TryParse_ReadsBothOldAndNew(string code, int expected)
    {
        Assert.True(SwNumberCode.TryParse(code, out int ordinal));
        Assert.Equal(expected, ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A000")]   // цифр должно быть ровно четыре
    [InlineData("A00000")]
    [InlineData("AB000")]  // буква только в старшем разряде
    [InlineData("Б0000")]  // кириллица без латинского двойника
    [InlineData("-1")]
    [InlineData("00 01")]
    public void TryParse_RejectsJunk(string code) =>
        Assert.False(SwNumberCode.TryParse(code, out _));

    /// <summary>Format и TryParse — обратны друг другу на всём диапазоне, включая границы разрядов.</summary>
    [Fact]
    public void FormatAndParse_RoundTrip()
    {
        foreach (int n in new[] { 1, 9999, 10_000, 99_999, 100_000, 100_001, 199_999, 200_000, 359_999 })
        {
            Assert.True(SwNumberCode.TryParse(SwNumberCode.Format(n), out int back));
            Assert.Equal(n, back);
        }
    }

    /// <summary>Сортировка идёт по числу, а не по коду: лексически «A0000» встал бы перед «0002».</summary>
    [Fact]
    public void Sorting_StaysNumeric()
    {
        var codes = new[] { 100_000, 2, 99_999, 1, 110_000 }
            .OrderBy(n => n)
            .Select(SwNumberCode.Format)
            .ToArray();

        Assert.Equal(new[] { "0001", "0002", "99999", "A0000", "B0000" }, codes);
    }

    /// <summary>Строка версии целиком: номер с буквой разбирается обеими сборками, а чисто цифровой
    /// старый номер читается ровно как раньше — папки с такими именами уже лежат на диске.</summary>
    [Fact]
    public void FwVersionNumber_BuildsAndParsesLetteredCounter()
    {
        var v = FwVersionNumber.Build(2, 1, 42, 100_000, includeDate: false);
        Assert.Equal("2.1.0042.A0000", v.Raw);
        Assert.Equal("A0000", v.SwCode);

        var parsed = FwVersionNumber.Parse("2.1.0042.A0000.20260422_1348");
        Assert.NotNull(parsed);
        Assert.Equal(100_000, parsed!.SwVersion);
        Assert.Equal("20260422_1348", parsed.DtStr);

        var old = FwVersionNumber.Parse("2.1.0042.0001");
        Assert.NotNull(old);
        Assert.Equal(1, old!.SwVersion);
        Assert.Equal("0001", old.SwCode);
        Assert.Equal("2.1.0042.0001", old.Raw);
    }

    /// <summary>Номер с буквой встаёт ПОСЛЕ всех цифровых, а не между ними.</summary>
    [Fact]
    public void FwVersionNumber_ComparesLetteredAfterDigits()
    {
        var last = FwVersionNumber.Build(2, 1, 42, 99_999, includeDate: false);
        var first = FwVersionNumber.Build(2, 1, 42, 100_000, includeDate: false);
        var small = FwVersionNumber.Build(2, 1, 42, 2, includeDate: false);

        Assert.True(small.CompareTo(last) < 0);
        Assert.True(last.CompareTo(first) < 0);
    }
}
