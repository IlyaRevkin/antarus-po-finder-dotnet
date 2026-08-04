using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using QRCoder;

namespace AntarusPoFinder.App.Services;

/// <summary>Рисованный QR-код: та же матрица, что у обычного, но векторная и с фирменным видом —
/// скруглённые модули, крупные «глаза» со скруглением и белое окно в центре под подпись.
///
/// <b>Почему не PNG из QRCoder.</b> Готовая картинка растровая: на 203-точечном термопринтере её
/// приходится масштабировать, и края модулей замыливаются (отсюда и жалоба, что мелкое печатается
/// плохо). Здесь код собирается фигурами и печатается вектором — при любом размере этикетки края
/// остаются резкими.
///
/// <b>Почему это не ломает считывание.</b> Скругление не меняет ни размер модуля, ни его центр —
/// сканер берёт пробу в середине клетки. Вырез в центре — стандартный приём «QR с логотипом»: при
/// уровне коррекции Q восстанавливается до четверти кода, а вырез занимает около 4 % площади. Три
/// угловых маркера («глаза») рисуются отдельно и целиком: по ним сканер находит и выравнивает код,
/// трогать их нельзя.</summary>
public static class QrArt
{
    /// <summary>Сторона выреза под подпись в долях стороны кода.
    ///
    /// Величину НЕ увеличиваем: 0.20 стороны — это 4 % площади кода, а уровень коррекции Q
    /// восстанавливает до 25 % кодовых слов; запас нужен потому, что вырез бьёт по модулям сплошным
    /// пятном, а не равномерно, и часть его площади приходится на служебные дорожки. Если подпись не
    /// помещается — уменьшается подпись (см. <see cref="FitFontSize"/>), а не плашка.</summary>
    public const double CenterHoleRatio = 0.20;

    /// <summary>Тихая зона, которую QRCoder уже вложил в матрицу. Она обязательна для считывания, но
    /// мы отводим под неё поле самой этикетки, а не пиксели кода — иначе на маленькой наклейке треть
    /// стороны ушла бы под пустоту.</summary>
    private const int QuietModules = 4;

    public static QRCodeData Encode(string content) =>
        new QRCodeGenerator().CreateQrCode(string.IsNullOrEmpty(content) ? " " : content,
            QRCodeGenerator.ECCLevel.Q);

    /// <summary>Визуал кода стороной <paramref name="side"/> (в единицах WPF). <paramref name="hole"/>
    /// — что написать в окошке по центру; пустая строка — окна нет вовсе.</summary>
    public static FrameworkElement Build(string content, double side, string hole)
    {
        var matrix = Encode(content).ModuleMatrix;
        var modules = matrix.Count - 2 * QuietModules;
        if (modules <= 0) modules = matrix.Count;
        var step = side / modules;

        var dots = new GeometryGroup { FillRule = FillRule.Nonzero };
        var holeFrom = modules * (0.5 - CenterHoleRatio / 2);
        var holeTo = modules * (0.5 + CenterHoleRatio / 2);
        var inset = step * 0.08;

        for (var y = 0; y < modules; y++)
        {
            var row = matrix[y + QuietModules];
            for (var x = 0; x < modules; x++)
            {
                if (!row[x + QuietModules]) continue;
                if (IsFinder(x, y, modules)) continue;
                if (hole.Length > 0 && x + 1 > holeFrom && x < holeTo && y + 1 > holeFrom && y < holeTo) continue;

                // Модуль чуть меньше клетки — между точками остаётся воздух, из-за которого код и
                // выглядит «точечным», а не сплошной кашей.
                var rect = new Rect(x * step + inset, y * step + inset, step - 2 * inset, step - 2 * inset);
                dots.Children.Add(new RectangleGeometry(rect, rect.Width / 2.6, rect.Height / 2.6));
            }
        }

        var canvas = new Canvas { Width = side, Height = side, Background = Brushes.White };
        canvas.Children.Add(new Path { Data = dots, Fill = Brushes.Black });

        foreach (var (fx, fy) in new[] { (0, 0), (modules - 7, 0), (0, modules - 7) })
            canvas.Children.Add(Finder(fx * step, fy * step, step));

        if (hole.Length > 0) AddHole(canvas, side, step, hole);
        return canvas;
    }

    /// <summary>Клетка внутри одного из трёх угловых маркеров 7×7.</summary>
    private static bool IsFinder(int x, int y, int modules) =>
        (x < 7 && y < 7) || (x >= modules - 7 && y < 7) || (x < 7 && y >= modules - 7);

    /// <summary>Угловой маркер: рамка 7×7 толщиной в модуль и залитый квадрат 3×3 внутри. Скругление
    /// только по контуру — положение и толщина в точности как у обычного кода.</summary>
    private static FrameworkElement Finder(double left, double top, double step)
    {
        var group = new Canvas { Width = 7 * step, Height = 7 * step };
        Canvas.SetLeft(group, left);
        Canvas.SetTop(group, top);

        // Обводка рисуется по центру контура, поэтому прямоугольник ужимается на половину толщины с
        // каждой стороны — иначе маркер вылез бы за свои 7 модулей.
        var outer = new Rectangle
        {
            Width = 6 * step,
            Height = 6 * step,
            RadiusX = step * 1.6,
            RadiusY = step * 1.6,
            Stroke = Brushes.Black,
            StrokeThickness = step,
        };
        Canvas.SetLeft(outer, step / 2);
        Canvas.SetTop(outer, step / 2);
        group.Children.Add(outer);

        var inner = new Rectangle
        {
            Width = 3 * step,
            Height = 3 * step,
            RadiusX = step * 0.8,
            RadiusY = step * 0.8,
            Fill = Brushes.Black,
        };
        Canvas.SetLeft(inner, 2 * step);
        Canvas.SetTop(inner, 2 * step);
        group.Children.Add(inner);

        return group;
    }

    /// <summary>Гарнитура подписи задаётся явно: визуал собирается вне окна, наследовать шрифт не от
    /// кого, а кегль подбирается замером именно этой гарнитуры.</summary>
    private static readonly FontFamily HoleFont = new("Segoe UI");

    /// <summary>Внутренний отступ подписи от рамки плашки, в долях её стороны. Тот самый «запас», без
    /// которого буквы липнут к рамке и выглядят подрезанными, даже когда формально помещаются.</summary>
    private const double HolePaddingRatio = 0.12;

    /// <summary>Кегль, при котором строка целиком помещается в окно <paramref name="box"/>.
    ///
    /// <b>Зачем замер, а не формула.</b> Раньше кегль был жёстко привязан к стороне плашки
    /// (0.42 от неё), и подпись из четырёх букв в неё просто не влезала по ширине: «ИНСТ»
    /// печаталось с подрезанными «И» и «Т». Ширина зависит и от числа букв, и от гарнитуры, и от
    /// того, что кириллица в полужирном шире латиницы, — угадать её коэффициентом нельзя, поэтому
    /// строка меряется настоящим FormattedText на пробном кегле и масштабируется.
    ///
    /// Плашка при этом НЕ растёт: её площадь ограничена тем, что уровень коррекции Q восстанавливает
    /// (см. <see cref="CenterHoleRatio"/>), — не помещается подпись, уменьшается подпись.</summary>
    public static double FitFontSize(string text, Size box, double probe = 100)
    {
        if (string.IsNullOrEmpty(text) || box.Width <= 0 || box.Height <= 0) return 1;

        var measured = Measure(text, probe);
        var scale = Math.Min(box.Width / Math.Max(0.01, measured.Width),
                             box.Height / Math.Max(0.01, measured.Height));
        return Math.Max(1, probe * scale);
    }

    /// <summary>Размер строки в единицах WPF при заданном кегле — тем же начертанием, каким она потом
    /// и печатается.</summary>
    public static Size Measure(string text, double fontSize)
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(HoleFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            fontSize, Brushes.Black, 96);
        return new Size(formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }

    /// <summary>Геометрия окна под подпись: сторона плашки, толщина её рамки и тот прямоугольник, в
    /// который подпись обязана поместиться целиком. Отдельно от отрисовки, чтобы «помещается ли
    /// „ИНСТ“» можно было проверить тестом, а не глазами на напечатанной наклейке.</summary>
    public static (double Plate, double Border, Size Inner) HoleGeometry(double side, double step)
    {
        var plate = side * CenterHoleRatio;
        var border = Math.Max(0.6, step * 0.25);
        var free = Math.Max(1, plate - 2 * (border + plate * HolePaddingRatio));
        return (plate, border, new Size(free, free));
    }

    private static void AddHole(Canvas canvas, double side, double step, string text)
    {
        var (size, border, inner) = HoleGeometry(side, step);

        var box = new Border
        {
            Width = size,
            Height = size,
            Background = Brushes.White,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(border),
            CornerRadius = new CornerRadius(size / 5),
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.Black,
                FontFamily = HoleFont,
                FontWeight = FontWeights.Bold,
                FontSize = FitFontSize(text, inner),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            },
        };
        Canvas.SetLeft(box, (side - size) / 2);
        Canvas.SetTop(box, (side - size) / 2);
        canvas.Children.Add(box);
    }
}
