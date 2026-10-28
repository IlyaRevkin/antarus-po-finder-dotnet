using System;
using System.Windows.Media;

namespace AntarusPoFinder.App;

/// <summary>Насколько сильно просвечивают поверхности темы «Стекло».
///
/// Тема задаёт альфу каждой поверхности числом (сайдбар плотнее карточек, карточки плотнее полей —
/// именно этот порядок и читается как глубина). Ползунок в настройках двигает их ВСЕ разом, сохраняя
/// соотношение: иначе при сильной прозрачности сайдбар и карточка сливались бы в одно пятно, а ради
/// этой разницы тема и затевалась.
///
/// Отсчёт ведётся от <see cref="Default"/> — непрозрачности карточки, какой она записана в Glass.xaml.
/// На этом значении ползунок не меняет НИЧЕГО: тема выглядит ровно так, как её нарисовали, и человек,
/// не трогавший настройку, видит то же, что и раньше.
///
/// Нижняя граница — не вкусовая. Это рабочий экран, по которому ищут прошивки полдня; на альфе ниже
/// 40 % текст начинает спорить с обоями под окном, и «красиво на снимке» превращается в «невозможно
/// читать». Верхняя граница — матовое стекло без просвета: размытие за окном (WindowBackdrop) никуда
/// не девается, но содержимое перестаёт сквозить.</summary>
public static class GlassSurface
{
    /// <summary>Непрозрачность карточки в процентах, как она записана в Glass.xaml (0xA6 ≈ 65 %).
    /// Значение по умолчанию и точка, относительно которой считается весь остальной набор.</summary>
    public const int Default = 65;

    public const int Min = 40;
    public const int Max = 100;

    /// <summary>Поверхности, которых касается ползунок. Текст, границы и акцент НЕ трогаем: читаемость
    /// не должна зависеть от того, куда человек увёл ручку.</summary>
    public static readonly string[] Keys =
    {
        "BgSidebarBrush", "BgCardBrush", "BgInputBrush", "TagBgBrush",
    };

    public static int Clamp(int percent) => Math.Clamp(percent, Min, Max);

    /// <summary>Цвет поверхности при выбранной прозрачности. Меняется только альфа — оттенок темы
    /// остаётся её собственным.</summary>
    public static Color Scale(Color themeColor, int percent)
    {
        var factor = Clamp(percent) / (double)Default;
        var alpha = (int)Math.Round(themeColor.A * factor);
        return Color.FromArgb((byte)Math.Clamp(alpha, 0, 255), themeColor.R, themeColor.G, themeColor.B);
    }

    /// <summary>Поверхности, которым положен блик, — крупные: сайдбар и карточки. Поля ввода и
    /// плашки остаются ровными: на мелком элементе градиент читается не как стекло, а как грязь.</summary>
    private static readonly string[] Specular = { "BgSidebarBrush", "BgCardBrush" };

    public static bool HasSpecular(string key) => Array.IndexOf(Specular, key) >= 0;

    /// <summary>Та же поверхность, но со светом сверху.
    ///
    /// У Apple стекло — это не ровная полупрозрачная заливка, а материал, который сверху ловит свет и
    /// книзу темнеет. Разница маленькая и именно поэтому работает: ровный прямоугольник выглядит
    /// плёнкой, поверхность с бликом — толщиной. Больше <see cref="LiftPercent"/> брать нельзя — на
    /// высокой карточке заметная растяжка превращается в полосу, а не в стекло.</summary>
    public static Brush Surface(Color themeColor, int percent, bool specular)
    {
        var baseColor = Scale(themeColor, percent);
        if (!specular) return new SolidColorBrush(baseColor);

        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(0, 1),
        };
        brush.GradientStops.Add(new GradientStop(Lift(baseColor, LiftPercent), 0));
        brush.GradientStops.Add(new GradientStop(baseColor, 0.55));
        brush.GradientStops.Add(new GradientStop(baseColor, 1));
        return brush;
    }

    /// <summary>На сколько процентов к белому подсвечена верхняя кромка поверхности.</summary>
    public const int LiftPercent = 7;

    /// <summary>Подмешать белого, не трогая прозрачность: блик — это свет НА стекле, а не дырка в нём.</summary>
    private static Color Lift(Color color, int percent)
    {
        double k = percent / 100.0;
        byte Mix(byte c) => (byte)Math.Clamp(Math.Round(c + (255 - c) * k), 0, 255);
        return Color.FromArgb(color.A, Mix(color.R), Mix(color.G), Mix(color.B));
    }
}
