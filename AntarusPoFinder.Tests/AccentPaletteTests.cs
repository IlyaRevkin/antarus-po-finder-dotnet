using System.Windows.Media;
using AntarusPoFinder.App;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Цвет оформления. С переходом на палитру цвет задаёт человек, а не список в коде, поэтому
/// проверять «красив ли он» бессмысленно — проверяем то, от чего зависит читаемость.
///
/// Главное здесь: подпись на цветной кнопке должна остаться различимой при ЛЮБОМ выбранном цвете.
/// Раньше она всегда была белой, и потому список цветов приходилось держать коротким и тёмным —
/// на светлом фоне белый текст исчезал. Теперь цвет надписи считается от яркости выбранного цвета,
/// и это единственное, что позволяет отдать выбор целиком человеку.</summary>
public class AccentPaletteTests
{
    private static Color C(string hex) => AccentPalette.Parse(hex);

    /// <summary>Порог 4.5:1 — уровень, с которого мелкий текст уверенно читается. Берём заведомо
    /// трудные цвета: почти белый, ярко-жёлтый и салатовый — на них старая логика (всегда белая
    /// подпись) давала нечитаемое, а также очень тёмные, где наоборот нужна белая.</summary>
    [Theory]
    [InlineData("#FFFFFF")]
    [InlineData("#FFE600")]
    [InlineData("#B6FF00")]
    [InlineData("#00E5FF")]
    [InlineData("#1E66F5")]
    [InlineData("#111418")]
    [InlineData("#7A0000")]
    public void TextOnAccent_IsAlwaysReadable(string hex)
    {
        var accent = C(hex);
        var text = AccentPalette.TextOn(accent);
        var contrast = AccentPalette.Contrast(accent, text);
        Assert.True(contrast >= 4.5,
            $"{hex}: контраст подписи {contrast:F2} < 4.5 — надпись на кнопке будет плыть");
    }

    /// <summary>На светлом цвете подпись обязана стать тёмной, на тёмном — белой. Это и есть та
    /// подмена, ради которой затевался переход к произвольному цвету.</summary>
    [Fact]
    public void TextOnAccent_FlipsWithBrightness()
    {
        Assert.Equal(Colors.White, AccentPalette.TextOn(C("#1E1E2E")));
        Assert.NotEqual(Colors.White, AccentPalette.TextOn(C("#FFE600")));
    }

    /// <summary>Оттенок под курсором обязан отличаться от основного, иначе кнопка не отзывается на
    /// мышь и выглядит залипшей. Проверяем оба края палитры: у почти белого сдвиг возможен только в
    /// тёмную сторону, у почти чёрного — только в светлую.</summary>
    [Theory]
    [InlineData("#FFFFFF")]
    [InlineData("#000000")]
    [InlineData("#1E66F5")]
    public void Hover_DiffersFromAccent(string hex)
    {
        var a = C(hex);
        foreach (var dark in new[] { false, true })
            Assert.NotEqual(a, AccentPalette.Hover(a, dark));
    }

    /// <summary>Подсветка выбранной строки не должна заливаться полным акцентом: текст на строке
    /// задаётся темой, и на насыщенном фоне он потеряется. Значит подсветка обязана остаться близкой
    /// к фону темы.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedBackground_StaysCloseToThemeBackground(bool dark)
    {
        var accent = C("#C2255C");
        var bg = AccentPalette.SelectedBackground(accent, dark);
        var themeBg = dark ? Color.FromRgb(0x1E, 0x1E, 0x2E) : Colors.White;
        Assert.True(AccentPalette.Contrast(bg, themeBg) < 2.0,
            "подсветка строки слишком далеко от фона темы — текст на ней потеряется");
    }

    /// <summary>Мусор в настройке не роняет программу: значение едет в общем конфиге и может прийти
    /// с чужой машины. Возвращаем цвет по умолчанию, чтобы программа открылась.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не цвет")]
    [InlineData("#ZZZZZZ")]
    public void BadValue_FallsBackToDefault(string? bad)
        => Assert.Equal(C(AccentPalette.DefaultHex), C(bad));

    [Theory]
    [InlineData("1E66F5", "#1E66F5")]
    [InlineData("#1e66f5", "#1E66F5")]
    public void Parse_AcceptsHexWithOrWithoutHash(string input, string expected)
        => Assert.Equal(expected, AccentPalette.ToHex(C(input)));

    /// <summary>Совместимость с 1.74.9.1, где цвет хранился именем из короткого списка: у тех, кто
    /// уже выбрал цвет, он обязан сохраниться, а не откатиться к синему.</summary>
    [Theory]
    [InlineData("green", "#1F7A4C")]
    [InlineData("crimson", "#C2255C")]
    [InlineData("blue", "#1E66F5")]
    public void OldNamedValues_StillUnderstood(string stored, string expected)
        => Assert.Equal(expected, AccentPalette.NormalizeStored(stored));

    [Fact]
    public void Samples_AreValidAndDistinct()
    {
        var seen = new System.Collections.Generic.HashSet<string>();
        foreach (var (hex, name) in AccentPalette.Samples)
        {
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.True(seen.Add(AccentPalette.ToHex(C(hex))), $"образец {hex} повторяется");
        }
    }
}
