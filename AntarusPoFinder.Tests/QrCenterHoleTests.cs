using System.Windows;
using AntarusPoFinder.App.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Подпись в центре фирменного QR.
///
/// <b>Жалоба:</b> «в фирменном QR обрезаны буквы в центре — написано „ИНСТ“, но „И“ и „Т“
/// подрезаны». Причина: кегль подписи был жёстко привязан к стороне плашки (0.42 от неё), то есть
/// подобран под ДВА символа. Четыре буквы кириллицей в полужирном начертании выходили за плашку и
/// обрезались её краями. Теперь кегль подбирается замером строки, а плашка не растёт — её площадь
/// ограничена тем, что вытягивает уровень коррекции ошибок.</summary>
public class QrCenterHoleTests
{
    /// <summary>Типичные размеры кода на этикетке: сторона в единицах WPF (мм × 96/25.4) и шаг
    /// модуля для кода версии ~5 (37 модулей).</summary>
    private static (double Side, double Step) Code(double mm) => (mm * 96 / 25.4, mm * 96 / 25.4 / 37);

    [Theory]
    [InlineData("ИНСТ", 25)]
    [InlineData("ИНСТ", 40)]
    [InlineData("ИНСТ", 55)]
    [InlineData("ТЕСТ", 55)]
    [InlineData("ОТК", 30)]
    [InlineData("И", 20)]
    public void HoleCaption_FitsInsideThePlateWithMargin(string text, double sideMm)
    {
        var (side, step) = Code(sideMm);
        var (_, _, inner) = QrArt.HoleGeometry(side, step);

        var size = QrArt.FitFontSize(text, inner);
        var measured = QrArt.Measure(text, size);

        Assert.True(measured.Width <= inner.Width + 0.01,
            $"«{text}» шире окна плашки: {measured.Width:0.##} > {inner.Width:0.##}");
        Assert.True(measured.Height <= inner.Height + 0.01,
            $"«{text}» выше окна плашки: {measured.Height:0.##} > {inner.Height:0.##}");
        Assert.True(size > 1, "кегль подписи выродился");
    }

    /// <summary>Прежняя формула проверяется прямо здесь — чтобы никто не «вернул как было»: жёсткие
    /// 0.42 от стороны плашки для четырёх букв дают строку заведомо шире самой плашки.</summary>
    [Fact]
    public void FixedFontSize_TheOldWay_WouldNotHaveFit()
    {
        var (side, step) = Code(55);
        var (plate, _, _) = QrArt.HoleGeometry(side, step);

        var old = QrArt.Measure("ИНСТ", plate * 0.42);

        Assert.True(old.Width > plate, "проверка потеряла смысл: старый кегль внезапно помещается");
    }

    /// <summary>Плашка не имеет права расти вслед за подписью: вырез — это выбитые модули, а
    /// восстанавливает их коррекция ошибок. Уровень Q вытягивает до 25 % кодовых слов; вырез в
    /// 0.20 стороны — это 4 % площади, и запас нужен потому, что бьёт он сплошным пятном.</summary>
    [Fact]
    public void HolePlate_StaysWithinWhatErrorCorrectionCanRecover()
    {
        var area = QrArt.CenterHoleRatio * QrArt.CenterHoleRatio;

        Assert.True(area <= 0.06, $"вырез занимает {area:P1} площади кода — это уже риск нечитаемого кода");
    }

    /// <summary>Длинная подпись не растягивает плашку и не вылезает за неё — просто становится
    /// мельче. Это и есть правило «уменьшаем подпись, а не увеличиваем окно».</summary>
    [Fact]
    public void LongerCaption_ShrinksInsteadOfOverflowing()
    {
        var (side, step) = Code(55);
        var (_, _, inner) = QrArt.HoleGeometry(side, step);

        var two = QrArt.FitFontSize("ИН", inner);
        var four = QrArt.FitFontSize("ИНСТ", inner);
        var seven = QrArt.FitFontSize("ИНСТРУК", inner);

        Assert.True(two > four && four > seven, "чем длиннее подпись, тем мельче кегль");
        Assert.True(QrArt.Measure("ИНСТРУК", seven).Width <= inner.Width + 0.01);
    }

    /// <summary>Пустая подпись — окна нет вовсе; вырожденные размеры не должны ронять расчёт.</summary>
    [Fact]
    public void FitFontSize_SurvivesDegenerateInput()
    {
        Assert.Equal(1, QrArt.FitFontSize("", new Size(10, 10)));
        Assert.Equal(1, QrArt.FitFontSize("ИНСТ", new Size(0, 0)));
    }
}
