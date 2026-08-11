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

    /// <summary>Тихая зона в модулях — по стандарту ISO/IEC 18004 не меньше четырёх с каждой стороны.
    ///
    /// <b>Раньше её здесь ВЫРЕЗАЛИ</b> («под неё отводится поле самой этикетки»), и это и была главная
    /// причина жалобы «фирменный QR телефоном не считывается». Поле этикетки тихой зоной не работает:
    /// сразу за кодом идёт рамка наклейки (ShowFrame) или название установки в соседней колонке в 2.5 мм
    /// — сканер видит тёмное вплотную к коду и не находит его границ. Теперь пустая рамка входит В САМ
    /// визуал кода: что бы ни стояло рядом на этикетке, четыре модуля белого вокруг есть всегда.</summary>
    public const int QuietModules = 4;

    /// <summary>Скругление углового маркера в модулях. Оно чисто внешнее: сканер ищет маркер по
    /// соотношению толщин на разрезе через центр, а скругление углов его не меняет — проверено
    /// декодером на 0, 0.8 и 1.6 модуля (QrDecoderTests).</summary>
    public const double FinderCornerRadius = 1.6;

    public static QRCodeData Encode(string content) =>
        new QRCodeGenerator().CreateQrCode(string.IsNullOrEmpty(content) ? " " : content,
            QRCodeGenerator.ECCLevel.Q);

    /// <summary>Сколько клеток укладывается по стороне визуала — данные ПЛЮС тихая зона с обеих
    /// сторон. Именно это число делит сторону наклейки на модули, поэтому по нему и проверяется, не
    /// стал ли модуль мельче различимого (см. LabelPlanner.MinModuleMm).</summary>
    public static int ModuleCountWithQuietZone(string content) => Encode(content).ModuleMatrix.Count;

    /// <summary>Визуал кода стороной <paramref name="side"/> (в единицах WPF). <paramref name="hole"/>
    /// — что написать в окошке по центру; пустая строка — окна нет вовсе.</summary>
    public static FrameworkElement Build(string content, double side, string hole)
    {
        var matrix = Encode(content).ModuleMatrix;
        var total = matrix.Count;                    // данные + тихая зона с обеих сторон
        var modules = total - 2 * QuietModules;      // только сам код
        if (modules <= 0) { modules = total; }
        var step = side / total;

        var dots = new GeometryGroup { FillRule = FillRule.Nonzero };
        var holeFrom = modules * (0.5 - CenterHoleRatio / 2);
        var holeTo = modules * (0.5 + CenterHoleRatio / 2);

        for (var y = 0; y < modules; y++)
        {
            var row = matrix[y + QuietModules];
            for (var x = 0; x < modules; x++)
            {
                if (!row[x + QuietModules]) continue;
                if (IsFinder(x, y, modules)) continue;
                if (hole.Length > 0 && x + 1 > holeFrom && x < holeTo && y + 1 > holeFrom && y < holeTo) continue;

                // Клетка рисуется ЦЕЛИКОМ, без воздуха по краям. Прежний зазор в 8 % стороны делал код
                // «точечным», но он же и разрывал связные тёмные области: на 203 dpi каждая точка
                // теряла по краю по печатной точке, соседние модули переставали смыкаться, и сканеру
                // доставалась россыпь пятен вместо кода. Фирменный вид держится скруглением углов —
                // соседние клетки при этом сливаются в скруглённые дорожки, как в любом «rounded QR».
                var rect = new Rect((x + QuietModules) * step, (y + QuietModules) * step, step, step);
                dots.Children.Add(new RectangleGeometry(rect, step * 0.25, step * 0.25));
            }
        }

        var canvas = new Canvas { Width = side, Height = side, Background = Brushes.White };
        canvas.Children.Add(new Path { Data = dots, Fill = Brushes.Black });

        foreach (var (fx, fy) in new[] { (0, 0), (modules - 7, 0), (0, modules - 7) })
            canvas.Children.Add(Finder((fx + QuietModules) * step, (fy + QuietModules) * step, step));

        // Плашка считается от стороны САМОГО КОДА, а не от визуала с тихой зоной: доля выреза
        // ограничена тем, что восстанавливает коррекция ошибок, и она про модули кода.
        if (hole.Length > 0) AddHole(canvas, side, modules * step, step, hole);
        return canvas;
    }

    /// <summary>Клетка внутри одного из трёх угловых маркеров 7×7.</summary>
    private static bool IsFinder(int x, int y, int modules) =>
        (x < 7 && y < 7) || (x >= modules - 7 && y < 7) || (x < 7 && y >= modules - 7);

    /// <summary>Угловой маркер: рамка 7×7 толщиной в модуль и залитый квадрат 3×3 внутри. Скругление
    /// только по контуру — положение и толщина в точности как у обычного кода.
    ///
    /// <b>Здесь и был главный баг «фирменный QR не читается телефоном».</b> Рамка задавалась
    /// прямоугольником 6×6 со сдвигом на полмодуля — «обводка рисуется по центру контура, иначе маркер
    /// вылезет за свои 7 модулей». Для <see cref="Rectangle"/> это неверно: WPF-фигура и так вписывает
    /// обводку ВНУТРЬ своих Width/Height (контур ужимается на половину толщины сам). В итоге маркер
    /// выходил шириной не 7 модулей, а 6, да ещё съезжал на полмодуля: горизонтальный разрез через его
    /// центр давал 1 : 0.5 : 3 : 0.5 : 1 вместо канонического 1 : 1 : 3 : 1 : 1. По этому соотношению
    /// сканер маркеры и ищет — не найдя их, он не видит кода ВООБЩЕ, сколько ни наводи камеру. Проверено
    /// настоящим декодером: QrDecoderTests, старая геометрия не читается ни в одном варианте.</summary>
    private static FrameworkElement Finder(double left, double top, double step)
    {
        var group = new Canvas { Width = 7 * step, Height = 7 * step };
        Canvas.SetLeft(group, left);
        Canvas.SetTop(group, top);

        // Ровно 7 модулей на сторону: обводку толщиной в модуль Rectangle укладывает внутрь этих 7,
        // оставляя белое кольцо и 3×3 по центру — то самое соотношение 1 : 1 : 3 : 1 : 1.
        var outer = new Rectangle
        {
            Width = 7 * step,
            Height = 7 * step,
            RadiusX = step * FinderCornerRadius,
            RadiusY = step * FinderCornerRadius,
            Stroke = Brushes.Black,
            StrokeThickness = step,
        };
        group.Children.Add(outer);

        var inner = new Rectangle
        {
            Width = 3 * step,
            Height = 3 * step,
            RadiusX = step * FinderCornerRadius / 2,
            RadiusY = step * FinderCornerRadius / 2,
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

    private static void AddHole(Canvas canvas, double side, double codeSide, double step, string text)
    {
        var (size, border, inner) = HoleGeometry(codeSide, step);

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
