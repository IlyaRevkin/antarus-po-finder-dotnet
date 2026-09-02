using System.Windows;
using System.Windows.Media;

namespace AntarusPoFinder.App.Services;

/// <summary>Фирменный знак ANTARUS — кольцо с треугольником — как рисуемая фигура.
///
/// <b>Почему не SVG-файл.</b> WPF картинки SVG не открывает вовсе, а тянуть ради одного логотипа
/// целую библиотеку разбора SVG — это лишняя зависимость в приложении, которое собирается одним
/// самодостаточным файлом. Здесь она и не нужна: язык описания контуров у WPF тот же самый, что у
/// атрибута <c>d</c> в SVG, поэтому оба контура из Assets/antarus-logo.svg перенесены сюда дословно и
/// разбираются <see cref="Geometry.Parse"/>. Никакого «нарисовал похожий кружок»: это ровно тот
/// контур, что лежит в файле, — знак уходит заказчику и обязан быть настоящим.
///
/// Единственная правка против файла — приставка «F0» у кольца: так в WPF записывается правило
/// заливки even-odd (в SVG это <c>fill-rule="evenodd"</c>). Без неё кольцо залилось бы сплошным
/// кругом и накрыло треугольник.</summary>
public static class AntarusMark
{
    /// <summary>Фирменный синий — им же красится кольцо в исходном файле.</summary>
    public static readonly Color Blue = Color.FromRgb(0x00, 0x47, 0x8F);

    /// <summary>Красный треугольника.</summary>
    public static readonly Color Red = Color.FromRgb(0xEF, 0x48, 0x39);

    /// <summary>Сторона исходного квадрата знака (viewBox="0 0 512 512").</summary>
    private const double Side = 512.0;

    private const string TrianglePath = "M259.939 78.7693L362.339 252.062H157.539L259.939 78.7693Z";

    private const string RingPath =
        "F0 M256 433.231C353.882 433.231 433.231 353.882 433.231 256C433.231 158.118 353.882 78.7692 " +
        "256 78.7692C158.118 78.7692 78.7692 158.118 78.7692 256C78.7692 353.882 158.118 433.231 256 " +
        "433.231ZM256 512C397.385 512 512 397.385 512 256C512 114.615 397.385 0 256 0C114.615 0 0 " +
        "114.615 0 256C0 397.385 114.615 512 256 512Z";

    /// <summary>Нарисовать знак в квадрате со стороной <paramref name="side"/> с левым верхним углом
    /// в <paramref name="origin"/>. Контуры заморожены: они не меняются, а замороженная фигура не
    /// пересобирается на каждой странице.</summary>
    public static void Draw(DrawingContext dc, Point origin, double side)
    {
        var scale = side / Side;

        dc.PushTransform(new TranslateTransform(origin.X, origin.Y));
        dc.PushTransform(new ScaleTransform(scale, scale));
        try
        {
            dc.DrawGeometry(RingBrush, null, Ring);
            dc.DrawGeometry(TriangleBrush, null, Triangle);
        }
        finally
        {
            dc.Pop();
            dc.Pop();
        }
    }

    private static readonly Geometry Ring = Frozen(Geometry.Parse(RingPath));
    private static readonly Geometry Triangle = Frozen(Geometry.Parse(TrianglePath));
    private static readonly Brush RingBrush = Frozen(new SolidColorBrush(Blue));
    private static readonly Brush TriangleBrush = Frozen(new SolidColorBrush(Red));

    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }
}
