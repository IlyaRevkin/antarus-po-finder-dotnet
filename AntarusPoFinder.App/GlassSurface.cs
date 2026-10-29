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
    /// <summary>Положение ползунка по умолчанию. На нём стекло выглядит так, как его нарисовали в
    /// теме: человек, не трогавший настройку, видит то же, что и раньше.</summary>
    public const int Default = 60;

    /// <summary>Ноль — совсем непрозрачно, сто — максимум прозрачности. Так и подписано на экране.
    ///
    /// Прежняя шкала шла от 40 до 100 и означала «непрозрачность карточки в процентах». Жалоба
    /// дословно: «регулировка кривая какая-то и диапазон странный» — и справедливо. Ползунок, у
    /// которого правый край означает МЕНЬШЕ прозрачности, читается наоборот; у краёв не было
    /// смысла — сороковник взят с потолка, а на сотне карточка становилась глухой, поле ввода нет,
    /// потому что каждая поверхность считалась от своей альфы. Теперь края значат ровно то, что
    /// написано, и поверхности идут к ним вместе.</summary>
    public const int Min = 0;
    public const int Max = 100;

    /// <summary>Какая доля от заданной в теме альфы остаётся на правом краю шкалы. Меньше брать
    /// нельзя: на рабочем экране, по которому ищут прошивки полдня, текст начинает спорить с
    /// обоями под окном.</summary>
    private const double MostTransparent = 0.6;

    /// <summary>Поверхности, которых касается ползунок. Текст, границы и акцент НЕ трогаем:
    /// читаемость не должна зависеть от того, куда человек увёл ручку.</summary>
    public static readonly string[] Keys =
    {
        "BgSidebarBrush", "BgCardBrush", "BgInputBrush", "TagBgBrush",
    };

    public static int Clamp(int percent) => Math.Clamp(percent, Min, Max);

    /// <summary>Цвет поверхности при выбранной прозрачности. Меняется только альфа — оттенок темы
    /// остаётся её собственным.
    ///
    /// Ноль — глухая поверхность (альфа 255), сто — самая прозрачная. Между ними прямая, поэтому
    /// ручка двигается предсказуемо: одинаковый шаг ползунка — одинаковый шаг на экране.</summary>
    public static Color Scale(Color themeColor, int percent)
    {
        // Шкала ломаная, и это сделано нарочно. Нужны ТРИ осмысленные точки: ноль — глухая
        // поверхность, умолчание — ровно то, как стекло нарисовано в теме, сто — максимум
        // прозрачности. Одной прямой через них не провести: у сайдбара, карточки и поля разная
        // альфа, и любая общая прямая проходила бы мимо темы у двух из трёх. Излом на умолчании
        // глазом неразличим, а вот «вернул ручку на место, а вид не тот» заметили бы сразу.
        var t = Clamp(percent) / 100.0;
        var pivot = Default / 100.0;
        double alpha;
        if (t <= pivot)
            alpha = 255 + (themeColor.A - 255) * (t / pivot);
        else
            alpha = themeColor.A + (themeColor.A * MostTransparent - themeColor.A) * ((t - pivot) / (1 - pivot));
        return Color.FromArgb((byte)Math.Clamp((int)Math.Round(alpha), 0, 255), themeColor.R, themeColor.G, themeColor.B);
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
