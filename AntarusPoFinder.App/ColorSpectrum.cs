using System;
using System.Windows;
using System.Windows.Media;

namespace AntarusPoFinder.App;

/// <summary>Счёт для выбора цвета мышью — отдельно от самого элемента управления
/// (<see cref="Views.ColorSpectrumPicker"/>), потому что проверить это глазами нельзя: «цвет
/// получился не тот» — не то наблюдение, по которому находят ошибку. Здесь только арифметика, без
/// единого обращения к визуалу, и она проверяется тестами.
///
/// Модель — HSV, а не RGB, и это не произвол: именно так выглядит привычный «спектр». Оттенок (H) —
/// радужная полоса, насыщенность и яркость (S, V) — квадрат под ней. В RGB такой квадрат не
/// нарисовать: три числа не ложатся на две оси.</summary>
public static class ColorSpectrum
{
    /// <summary>RGB → HSV. Оттенок в градусах [0, 360), насыщенность и яркость — доли [0, 1].
    /// У серого (включая чёрный и белый) оттенка нет вовсе; возвращаем 0, но вызывающий код обязан
    /// хранить СВОЙ оттенок отдельно — иначе ползунок прыгал бы в красный каждый раз, когда человек
    /// увёл насыщенность в ноль (см. ColorSpectrumPicker).</summary>
    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;

        double h;
        if (d == 0) h = 0;
        else if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * (((b - r) / d) + 2);
        else h = 60 * (((r - g) / d) + 4);
        if (h < 0) h += 360;

        var s = max == 0 ? 0 : d / max;
        return (h, s, max);
    }

    /// <summary>HSV → RGB. Входные значения приводятся к допустимым: угол заворачивается по кругу
    /// (361° — это 1°, а не ошибка), доли обрезаются по краям. Так ведёт себя мышь, выехавшая за
    /// границу квадрата, и ругаться на это незачем.</summary>
    public static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Clamp01(s);
        v = Clamp01(v);

        var c = v * s;
        var x = c * (1 - Math.Abs(((h / 60) % 2) - 1));
        var m = v - c;

        (double r, double g, double b) rgb = h switch
        {
            < 60 => (c, x, 0),
            < 120 => (x, c, 0),
            < 180 => (0, c, x),
            < 240 => (0, x, c),
            < 300 => (x, 0, c),
            _ => (c, 0, x),
        };

        return Color.FromRgb(Byte(rgb.r + m), Byte(rgb.g + m), Byte(rgb.b + m));
    }

    /// <summary>Точка нажатия в квадрате → насыщенность и яркость. Влево — бледнее, вверх — светлее:
    /// так устроен этот квадрат во всех привычных программах, и переучивать человека не за чем.
    /// Выход за край не ошибка, а обычное дело при перетаскивании — значения обрезаются.</summary>
    public static (double S, double V) PointToSv(Point p, double width, double height)
    {
        if (width <= 0 || height <= 0) return (0, 1);
        return (Clamp01(p.X / width), Clamp01(1 - p.Y / height));
    }

    /// <summary>Обратное: куда поставить маркер для выбранных насыщенности и яркости.</summary>
    public static Point SvToPoint(double s, double v, double width, double height) =>
        new(Clamp01(s) * Math.Max(0, width), (1 - Clamp01(v)) * Math.Max(0, height));

    /// <summary>Точка нажатия на радужной полосе → оттенок в градусах.</summary>
    public static double PointToHue(double x, double width) =>
        width <= 0 ? 0 : Clamp01(x / width) * 359.999;

    /// <summary>Обратное: куда поставить маркер оттенка на полосе.</summary>
    public static double HueToX(double hue, double width) =>
        ((((hue % 360) + 360) % 360) / 360.0) * Math.Max(0, width);

    private static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;

    private static byte Byte(double x) => (byte)Math.Round(Clamp01(x) * 255, MidpointRounding.AwayFromZero);
}
