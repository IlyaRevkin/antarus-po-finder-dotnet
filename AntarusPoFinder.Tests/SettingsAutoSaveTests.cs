using AntarusPoFinder.App.Services;

namespace AntarusPoFinder.Tests;

/// <summary>Кнопок «Сохранить» в Настройках и на странице «Сетевые диски» больше нет — поля
/// сохраняются сами. Здесь проверяются два правила, из-за которых это не «просто сохранить по уходу
/// фокуса»: не писать в конфиг и не сообщать о неизменившемся значении, и не открывать модальное
/// окно на мусорном вводе числового поля.</summary>
public class SettingsAutoSaveTests
{
    [Theory]
    [InlineData("abc", "abc", false)]
    [InlineData("  abc  ", "abc", false)]
    [InlineData("", "", false)]
    [InlineData("abc", "abd", true)]
    [InlineData("abc", "", true)]
    [InlineData("ABC", "abc", true)] // регистр в обычном тексте (напр. имя AD-группы) — значимая правка
    public void TextChanged_ComparesTrimmedOrdinal(string typed, string stored, bool expected) =>
        Assert.Equal(expected, SettingsAutoSave.TextChanged(typed, stored));

    /// <summary>Пути сравниваются без учёта регистра — «D:\ПО» после «d:\по» не повод переписывать
    /// настройку и сообщать оператору о сохранении.</summary>
    [Theory]
    [InlineData(@"D:\ПО", @"d:\по", false)]
    [InlineData(@" D:\ПО ", @"D:\ПО", false)]
    [InlineData(@"D:\ПО", @"E:\ПО", true)]
    [InlineData("", @"D:\ПО", true)]
    public void PathChanged_IgnoresCase(string typed, string stored, bool expected) =>
        Assert.Equal(expected, SettingsAutoSave.PathChanged(typed, stored));

    [Fact]
    public void ParseNumber_NewValidValue_Saves()
    {
        var edit = SettingsAutoSave.ParseNumber("48", stored: 24, min: 0, "плохо");

        Assert.True(edit.Save);
        Assert.False(edit.Invalid);
        Assert.Equal(48, edit.Value);
    }

    [Fact]
    public void ParseNumber_SameValue_SavesNothingAndStaysSilent()
    {
        var edit = SettingsAutoSave.ParseNumber(" 24 ", stored: 24, min: 0, "плохо");

        Assert.False(edit.Save);
        Assert.False(edit.Invalid);
        Assert.Equal("", edit.Message);
    }

    /// <summary>Мусор и значение ниже допустимого — не сохраняем и возвращаем в поле сохранённое
    /// значение (Value), а причину вызывающий отправляет в нижнюю строку состояния, не в модальное
    /// окно: по уходу фокуса окно было бы навязчивым.</summary>
    [Theory]
    [InlineData("не число")]
    [InlineData("")]
    [InlineData("-3")]
    public void ParseNumber_Garbage_RevertsWithMessage(string typed)
    {
        var edit = SettingsAutoSave.ParseNumber(typed, stored: 24, min: 0, "нужно целое число");

        Assert.False(edit.Save);
        Assert.True(edit.Invalid);
        Assert.Equal(24, edit.Value);
        Assert.Equal("нужно целое число", edit.Message);
    }

    /// <summary>Ноль допустим там, где означает «выключено» (интервал отправки, срок резерва), и не
    /// допустим там, где бессмыслен (срок повторного входа по AD — min=1).</summary>
    [Fact]
    public void ParseNumber_ZeroDependsOnMinimum()
    {
        Assert.True(SettingsAutoSave.ParseNumber("0", stored: 5, min: 0, "плохо").Save);
        Assert.True(SettingsAutoSave.ParseNumber("0", stored: 5, min: 1, "плохо").Invalid);
    }
}
