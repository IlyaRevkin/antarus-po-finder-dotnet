using System.Windows;
using System.Windows.Media;
using AntarusPoFinder.App;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Арифметика выбора цвета спектром (ColorSpectrum).
///
/// Просьба Ильи: «сделай выбор цвета не только кодом, но и спектром». Проверять такое глазами
/// бессмысленно — «получился не тот цвет» не указывает, где ошибка, — поэтому счёт вынесен из
/// элемента управления и проверяется здесь.</summary>
public class ColorSpectrumTests
{
    /// <summary>Главное свойство: цвет, разобранный в HSV и собранный обратно, остаётся собой.
    /// Без него маркеры на спектре «уплывали» бы при каждом открытии настроек.</summary>
    [Theory]
    [InlineData(0x1E, 0x66, 0xF5)]   // синий по умолчанию
    [InlineData(0xFF, 0x00, 0x00)]   // чистый красный — граница круга оттенков
    [InlineData(0x00, 0xFF, 0x00)]
    [InlineData(0x00, 0x00, 0xFF)]
    [InlineData(0xFF, 0xFF, 0xFF)]   // белый: насыщенности нет
    [InlineData(0x00, 0x00, 0x00)]   // чёрный: нет ни насыщенности, ни яркости
    [InlineData(0x80, 0x80, 0x80)]   // серый
    [InlineData(0xB7, 0x79, 0x1F)]
    public void Цвет_переживает_перевод_в_hsv_и_обратно(int r, int g, int b)
    {
        var source = Color.FromRgb((byte)r, (byte)g, (byte)b);

        var (h, s, v) = ColorSpectrum.ToHsv(source);
        var back = ColorSpectrum.FromHsv(h, s, v);

        Assert.Equal(source, back);
    }

    [Fact]
    public void Оттенок_красного_нулевой_а_зелёного_сто_двадцать()
    {
        Assert.Equal(0, ColorSpectrum.ToHsv(Colors.Red).H, 1);
        Assert.Equal(120, ColorSpectrum.ToHsv(Colors.Lime).H, 1);
        Assert.Equal(240, ColorSpectrum.ToHsv(Colors.Blue).H, 1);
    }

    /// <summary>Квадрат: влево — бледнее, вверх — светлее. Так он устроен во всех привычных
    /// программах, и переучивать человека не за чем.</summary>
    [Fact]
    public void В_квадрате_влево_бледнее_а_вверх_светлее()
    {
        var topLeft = ColorSpectrum.PointToSv(new Point(0, 0), 200, 100);
        var topRight = ColorSpectrum.PointToSv(new Point(200, 0), 200, 100);
        var bottomRight = ColorSpectrum.PointToSv(new Point(200, 100), 200, 100);

        Assert.Equal(0, topLeft.S, 3);
        Assert.Equal(1, topLeft.V, 3);
        Assert.Equal(1, topRight.S, 3);
        Assert.Equal(1, topRight.V, 3);
        Assert.Equal(0, bottomRight.V, 3);
    }

    /// <summary>Мышь, уехавшая за край при перетаскивании, — обычное дело, а не ошибка: значения
    /// обрезаются по краям, а не вылетают за [0, 1] и не роняют перевод в цвет.</summary>
    [Fact]
    public void Выход_за_край_квадрата_обрезается()
    {
        var far = ColorSpectrum.PointToSv(new Point(900, -400), 200, 100);

        Assert.Equal(1, far.S, 3);
        Assert.Equal(1, far.V, 3);

        var before = ColorSpectrum.PointToSv(new Point(-50, 400), 200, 100);
        Assert.Equal(0, before.S, 3);
        Assert.Equal(0, before.V, 3);
    }

    /// <summary>Маркер встаёт туда, откуда его взяли: точка → S/V → точка.</summary>
    [Fact]
    public void Маркер_квадрата_возвращается_на_своё_место()
    {
        var p = new Point(63, 29);

        var (s, v) = ColorSpectrum.PointToSv(p, 200, 100);
        var back = ColorSpectrum.SvToPoint(s, v, 200, 100);

        Assert.Equal(p.X, back.X, 3);
        Assert.Equal(p.Y, back.Y, 3);
    }

    /// <summary>Радуга: левый край — красный, середина — бирюза, правый — снова красный (круг
    /// замкнулся). Последнее важно: 360° не должно превращаться в 0° СКАЧКОМ посреди полосы.</summary>
    [Fact]
    public void Радуга_идёт_по_кругу_оттенков()
    {
        Assert.Equal(0, ColorSpectrum.PointToHue(0, 240), 1);
        Assert.Equal(180, ColorSpectrum.PointToHue(120, 240), 1);
        Assert.True(ColorSpectrum.PointToHue(240, 240) > 359);
    }

    [Fact]
    public void Маркер_радуги_возвращается_на_своё_место()
    {
        var hue = ColorSpectrum.PointToHue(97, 240);

        Assert.Equal(97, ColorSpectrum.HueToX(hue, 240), 1);
    }

    /// <summary>Нулевая ширина (элемент ещё не разложен по месту) не должна ронять счёт делением
    /// на ноль: первый Redraw случается до того, как WPF посчитает размеры.</summary>
    [Fact]
    public void Неразложенный_элемент_не_роняет_счёт()
    {
        Assert.Equal(0, ColorSpectrum.PointToHue(10, 0), 3);
        var sv = ColorSpectrum.PointToSv(new Point(10, 10), 0, 0);
        Assert.Equal(0, sv.S, 3);
        Assert.Equal(1, sv.V, 3);
    }
}
