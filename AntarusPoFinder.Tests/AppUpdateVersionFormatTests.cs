using System;
using AntarusPoFinder.App;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Как версия выглядит для человека. С 24.08.2026 четвёртая цифра перестала быть локальным
/// номером сборки и означает «мелкая правка» (см. CLAUDE.md) — значит её надо показывать. Но только
/// когда она есть: напечатать её всегда — переписать все уже вышедшие версии в «1.74.0.0», не
/// печатать никогда — сделать 1.74.0 и 1.74.0.1 неразличимыми на экране, а именно по этой строке
/// человек и говорит, что у него стоит.</summary>
public class AppUpdateVersionFormatTests
{
    [Theory]
    [InlineData(1, 74, 0, 0, "1.74.0")]
    [InlineData(1, 74, 0, 1, "1.74.0.1")]
    [InlineData(1, 74, 1, 0, "1.74.1")]
    [InlineData(2, 0, 0, 12, "2.0.0.12")]
    public void Format_ShowsTheFourthDigitOnlyWhenItMeansSomething(int a, int b, int c, int d, string expected) =>
        Assert.Equal(expected, AppUpdateService.Format(new Version(a, b, c, d)));

    /// <summary>Версия из чужой строки может оказаться двухкомпонентной («v1.2») — ToString(3) на
    /// такой падает, а разбор имён файлов релиза обязан пережить что угодно.</summary>
    [Fact]
    public void Format_SurvivesShortVersions()
    {
        Assert.Equal("1.2", AppUpdateService.Format(new Version(1, 2)));
        Assert.Equal("1.2.3", AppUpdateService.Format(new Version(1, 2, 3)));
    }
}
