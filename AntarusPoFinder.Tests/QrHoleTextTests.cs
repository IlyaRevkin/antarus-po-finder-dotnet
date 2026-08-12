using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Вёрстка подписи в окошке по центру кода.
///
/// <b>Просьба:</b> «подпись в центре чтобы могла в 2-3 строки по длине, если больше 4 букв». Смысл в
/// кегле: плашке расти некуда (её площадь ограничена тем, что вытягивает коррекция ошибок), поэтому
/// длинная подпись одной строкой упирается в ширину и вырождается. Разложенная по строкам, она
/// занимает квадрат вместо полоски — и печатается заметно крупнее.</summary>
public class QrHoleTextTests
{
    [Fact]
    public void ShortCaption_StaysOneLine()
    {
        // Короткую рвать незачем: «ИН/СТ» читается хуже, чем «ИНСТ» целиком.
        Assert.Equal(new[] { "ИНСТ" }, QrHoleText.Wrap("ИНСТ"));
        Assert.Equal(new[] { "ОТК" }, QrHoleText.Wrap("  ОТК "));
        Assert.Empty(QrHoleText.Wrap(""));
        Assert.Empty(QrHoleText.Wrap(null));
    }

    /// <summary>Одно длинное слово режется по буквам, а строки выравниваются по длине: разница в
    /// длине строк на плашке видна сразу, и она же задаёт кегль.</summary>
    [Fact]
    public void OneLongWord_IsSplitIntoEvenLines()
    {
        Assert.Equal(new[] { "ИНСТ", "РУК", "ЦИЯ" }, QrHoleText.Wrap("ИНСТРУКЦИЯ"));
        Assert.Equal(new[] { "ПАСП", "ОРТ" }, QrHoleText.Wrap("ПАСПОРТ"));
        Assert.Equal("ИНСТ\nРУК\nЦИЯ", QrHoleText.Format("ИНСТРУКЦИЯ"));
        Assert.Equal(3, QrHoleText.LineCount("ИНСТРУКЦИЯ"));
    }

    /// <summary>Больше трёх строк плашка не вмещает — строки сливаются с собственным просветом.
    /// Предел длины подписи (MaxHoleTextLength) подобран ровно под три строки.</summary>
    [Fact]
    public void NeverMoreThanThreeLines()
    {
        var longest = new string('И', LabelLayout.MaxHoleTextLength);

        Assert.Equal(QrHoleText.MaxLines, QrHoleText.Wrap(longest).Count);
        Assert.All(QrHoleText.Wrap(longest), line => Assert.True(line.Length <= 5));
        Assert.Equal(QrHoleText.MaxLines, QrHoleText.Wrap("ОТК ПРОШИВКА ЩИТА ПЖ").Count);
    }

    /// <summary>Подпись из нескольких слов ломается по пробелам: «ОТК ПРОШИВКА» на двух строках
    /// читается как два слова, а не как разрезанное посередине одно.</summary>
    [Fact]
    public void SeveralWords_BreakOnSpaces()
    {
        Assert.Equal(new[] { "ОТК", "ПРОШИВКА" }, QrHoleText.Wrap("ОТК ПРОШИВКА"));
        Assert.Equal(new[] { "ЩИТ", "ПЖ", "НАЛАДКА" }, QrHoleText.Wrap("ЩИТ ПЖ НАЛАДКА"));
    }

    /// <summary>Два случая, когда по словам не выходит и подпись режется по буквам: слов больше, чем
    /// строк на плашке, и слово, которое само по себе вдвое длиннее ровной строки (соседние строки
    /// остались бы полупустыми, а кегль всё равно задало бы оно).</summary>
    [Fact]
    public void WhenWordsDoNotFit_TheCaptionIsCutByLetters()
    {
        // Четыре слова на три строки не разложить, не слепив два из них в одну.
        Assert.Equal(new[] { "ОТК П", "Ж ЩИ", "Т ПИ" }, QrHoleText.Wrap("ОТК ПЖ ЩИТ ПИ"));

        // «ПРОШИВКАЩИТ» рядом с однобуквенными словами: по словам вышло бы 1/1/11.
        Assert.Equal(new[] { "И И П", "РОШИВ", "КАЩИТ" }, QrHoleText.Wrap("И И ПРОШИВКАЩИТ"));
    }

    /// <summary>Сколько бы строк ни вышло, подпись не теряет ни одной буквы — на плашке печатается
    /// ровно то, что набрали (после обрезки по MaxHoleTextLength).</summary>
    [Theory]
    [InlineData("ИНСТ")]
    [InlineData("ИНСТРУКЦИЯ")]
    [InlineData("ОТК ПРОШИВКА")]
    [InlineData("ЩИТ ПЖ НАЛАДКА")]
    public void NothingIsLostInTheWrap(string text)
    {
        var joined = string.Concat(QrHoleText.Wrap(text)).Replace(" ", "");

        Assert.Equal(text.Replace(" ", ""), joined);
    }
}
