using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AntarusPoFinder.Core.Services;
using QRCoder;

namespace AntarusPoFinder.App.Services;

/// <summary>Печать этикетки с QR-кодом на принтер этикеток.
///
/// Почему не «отправить файл на печать ассоциацией», как это делается с PDF инструкции: этикетки нет
/// как файла — её надо СОБРАТЬ (QR + подписи) под конкретный размер наклейки. WPF это умеет напрямую:
/// собранный визуал печатается PrintDialog.PrintVisual, а размер задаётся в миллиметрах и переводится
/// в аппаратно-независимые единицы (мм × 96 / 25.4). Никаких внешних библиотек для этого не нужно.
///
/// Принтер выбирается по ИМЕНИ из настроек (per-machine): пусто — принтер Windows по умолчанию.
/// Неизвестное имя (принтер отключили/переименовали) не должно ронять печать: молча падаем на
/// принтер по умолчанию, вызывающий сообщает об этом человеку.</summary>
public static class LabelPrinter
{
    /// <summary>Миллиметры в аппаратно-независимые единицы WPF (1/96 дюйма).</summary>
    public static double MmToDiu(double mm) => mm * 96.0 / 25.4;

    public static BitmapSource MakeQr(string content, int pixelsPerModule = 12)
    {
        var data = new QRCodeGenerator().CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var bytes = new PngByteQRCode(data).GetGraphic(pixelsPerModule);
        var bmp = new BitmapImage();
        using var ms = new MemoryStream(bytes);
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Пункты типографские в единицы WPF (1 pt = 1/72 дюйма, DIU = 1/96).</summary>
    public static double PtToDiu(double pt) => pt * 96.0 / 72.0;

    /// <summary>Шрифт этикетки задаётся ЯВНО: визуал собирается вне окна, и наследовать ему шрифт
    /// не от кого — а расчёт компоновки считает ширину символов именно для этой гарнитуры.</summary>
    private static readonly FontFamily LabelFont = new("Segoe UI");

    /// <summary>Сама этикетка. Верстается на белом фоне и чёрным текстом ЯВНО, без ресурсов темы:
    /// печатается она на бумагу, и в тёмной теме приложения этикетка иначе ушла бы на принтер белым
    /// по чёрному.
    ///
    /// <b>Здесь ничего не решается.</b> Где что лежит и каким кеглем печатается — считает
    /// <see cref="LabelPlanner"/> в Core; этот метод только раскладывает элементы по готовым
    /// прямоугольникам. Раньше вёрстку делал WPF-Grid со звёздочными строками, а размер кода
    /// считался отдельной формулой, которая про рамку, отступы и настоящую высоту блока ссылки не
    /// знала — отсюда «увеличил QR, обрезался текст; убрал сдвиг, обрезан QR». Теперь расчёт один и
    /// тот же и для предпросмотра, и для принтера, и он проверяется тестами без окна.
    ///
    /// <paramref name="holeText"/> — короткая подпись в окошке по центру кода (обычно «ИНСТ»); пусто
    /// — окна нет.</summary>
    public static FrameworkElement BuildLabel(LabelLayout layout, string qrContent,
        string title, string subtitle, string caption, string holeText = "") =>
        BuildLabel(layout, qrContent, title, subtitle, caption, holeText, out _);

    /// <summary>То же самое, но отдаёт и саму раскладку — окну предпросмотра нужны её предупреждения
    /// («сторона QR уменьшена», «кегль ссылки уменьшен»): настройку мы не запрещаем, но и молчать о
    /// том, что напечатается не ровно заказанное, не имеем права.</summary>
    public static FrameworkElement BuildLabel(LabelLayout layout, string qrContent,
        string title, string subtitle, string caption, string holeText, out LabelPlan plan)
    {
        var v = layout.Clamped();
        plan = LabelPlanner.Plan(v, title, subtitle, caption);
        plan = WarnAboutModuleSize(plan, qrContent);

        var page = new Canvas
        {
            Width = MmToDiu(v.WidthMm),
            Height = MmToDiu(v.HeightMm),
            Background = Brushes.White,
            // Страховка поверх расчёта: раскладка гарантирует, что всё внутри, но живой текст может
            // оказаться чуть шире оценки — за край этикетки не должно уйти ничего и никогда.
            ClipToBounds = true,
        };

        if (plan.Frame is { } frame) AddFrame(page, frame);
        if (!plan.Qr.IsEmpty) Place(page, BuildQrVisual(v, qrContent, MmToDiu(plan.Qr.W), holeText), plan.Qr);
        if (plan.HasHeadline) Place(page, BuildHeadline(plan, v.EffectiveHeadline()), plan.Headline);
        if (plan.HasTitle) Place(page, BuildTexts(plan, title, subtitle), plan.Title);
        if (plan.HasCaption) Place(page, BuildCaption(plan, caption), plan.Caption);

        Layout(page, page.Width, page.Height);
        return page;
    }

    /// <summary>Проверка «возьмёт ли это обычная камера», которую может сделать только App: число
    /// модулей знает лишь тот, кто уже закодировал ссылку, а QRCoder живёт здесь, а не в Core.
    /// Молчать нельзя: код на 20 мм с длинной ссылкой выглядит в предпросмотре нормально, а
    /// телефоном не берётся — ровно та жалоба, из-за которой наклейку и переделывали.</summary>
    private static LabelPlan WarnAboutModuleSize(LabelPlan plan, string qrContent)
    {
        if (plan.Qr.IsEmpty || string.IsNullOrEmpty(qrContent)) return plan;

        int modules;
        try { modules = QrArt.ModuleCountWithQuietZone(qrContent); }
        catch (Exception) { return plan; }

        if (LabelPlanner.ModulesAreReadable(plan.Qr.W, modules, out var moduleMm)) return plan;

        var text = $"Клетка кода — {moduleMm:0.00} мм при нужных {LabelPlanner.MinModuleMm:0.0} мм: телефон, скорее всего, " +
                   "его не возьмёт. Увеличьте наклейку или сторону QR, либо сократите ссылку " +
                   "(Настройки → Печать, веб-адрес диска инструкций).";
        return plan with { Warnings = plan.Warnings.Concat(new[] { text }).ToList() };
    }

    /// <summary>Подпись назначения («Инструкция для заказчика»). Полужирная и во всю ширину — её
    /// задача объяснить наклейку с одного взгляда, до того как человек полезет за телефоном.</summary>
    private static FrameworkElement BuildHeadline(LabelPlan plan, string text)
    {
        var block = Text(text, plan.HeadlinePt, bold: true);
        block.TextAlignment = TextAlignment.Center;
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        return new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Child = new StackPanel { Width = MmToDiu(plan.Headline.W), Children = { block } },
        };
    }

    /// <summary>Рамка идёт по границе печатной области, а не по краю этикетки: у самого края её
    /// съедала непечатаемая кромка принтера. Обводка рисуется по центру контура, поэтому
    /// прямоугольник ужимается на её толщину.</summary>
    private static void AddFrame(Canvas page, LabelBox box)
    {
        var t = MmToDiu(LabelPlanner.FrameMm);
        var rect = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(0, MmToDiu(box.W) - t),
            Height = Math.Max(0, MmToDiu(box.H) - t),
            Stroke = Brushes.Black,
            StrokeThickness = t,
        };
        Canvas.SetLeft(rect, MmToDiu(box.X) + t / 2);
        Canvas.SetTop(rect, MmToDiu(box.Y) + t / 2);
        page.Children.Add(rect);
    }

    /// <summary>Заголовок и подзаголовок в отведённом прямоугольнике. Кегль уже подобран расчётом;
    /// Viewbox поверх него — вторая линия обороны: если настоящий текст всё-таки оказался выше
    /// оценки, он ужмётся целиком, а не обрежется по нижней строке.</summary>
    private static FrameworkElement BuildTexts(LabelPlan plan, string title, string subtitle)
    {
        var stack = new StackPanel { Width = MmToDiu(plan.Title.W) };
        if (!string.IsNullOrWhiteSpace(title)) stack.Children.Add(Text(title, plan.TitlePt, bold: true));
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var sub = Text(subtitle, plan.SubtitlePt, bold: false);
            sub.Margin = new Thickness(0, MmToDiu(LabelPlanner.CaptionGapMm), 0, 0);
            stack.Children.Add(sub);
        }

        return new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = stack };
    }

    /// <summary>Ссылка под кодом. Здесь Viewbox не годится: ужатый адрес на 4 пт всё равно не
    /// прочитать, поэтому лишнее честно отсекается многоточием — расчёт об этом предупреждает.</summary>
    private static FrameworkElement BuildCaption(LabelPlan plan, string caption)
    {
        var text = Text(caption, plan.CaptionPt, bold: false);
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        return new Border { Child = text, ClipToBounds = true };
    }

    private static TextBlock Text(string value, double pt, bool bold) => new()
    {
        Text = value,
        FontFamily = LabelFont,
        FontSize = PtToDiu(pt),
        FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        Foreground = Brushes.Black,
        TextWrapping = TextWrapping.Wrap,
        // Межстрочный интервал фиксируется тем же коэффициентом, по которому считалась высота блока
        // в LabelPlanner: иначе расчёт и отрисовка разъедутся ровно на разницу интервалов.
        LineHeight = PtToDiu(pt) * 1.25,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    /// <summary>Поставить элемент точно в прямоугольник раскладки (миллиметры → единицы WPF).</summary>
    private static void Place(Canvas page, FrameworkElement element, LabelBox box)
    {
        element.Width = MmToDiu(box.W);
        element.Height = MmToDiu(box.H);
        Canvas.SetLeft(element, MmToDiu(box.X));
        Canvas.SetTop(element, MmToDiu(box.Y));
        page.Children.Add(element);
    }

    /// <summary>Код внутри этикетки: рисованный (см. <see cref="QrArt"/>) или обычная растровая
    /// матрица, если фирменный вид выключили. Растровый вариант оставлен именно как запасной: если
    /// какой-то сканер вдруг заупрямится на скруглённом коде, галочку снимают и печатают привычный.</summary>
    private static FrameworkElement BuildQrVisual(LabelLayout layout, string content, double side, string holeText)
    {
        if (layout.FancyQr) return QrArt.Build(content, side, holeText);

        var image = new Image { Source = MakeQr(content), Width = side, Height = side };
        // Пиксельный QR не должен размываться интерполяцией при печати — иначе телефон его хуже ловит.
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        return image;
    }

    /// <summary>Разметка «вручную»: визуал, который никогда не был на экране, сам себя не измеряет, и
    /// на печать ушёл бы пустым прямоугольником.</summary>
    public static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    public sealed record PrintOutcome(bool Ok, string Message);

    /// <summary>Печать без диалога выбора принтера: наладчику незачем каждый раз подтверждать окно —
    /// принтер задан в Настройки → Печать. Возврат — что сказать человеку в статус-строке.</summary>
    public static PrintOutcome Print(FrameworkElement label, string printerName, string jobName)
    {
        try
        {
            var dlg = new PrintDialog();
            var note = "";
            if (!string.IsNullOrWhiteSpace(printerName))
            {
                var queue = TryFindQueue(printerName);
                if (queue is not null) dlg.PrintQueue = queue;
                else note = $" (принтер «{printerName}» не найден — печать на принтер по умолчанию)";
            }

            dlg.PrintVisual(label, jobName);
            return new PrintOutcome(true, $"Этикетка отправлена на печать{note}");
        }
        catch (Exception ex)
        {
            return new PrintOutcome(false, $"Не удалось напечатать: {ex.Message}");
        }
    }

    /// <summary>Имена принтеров, установленных на этой машине. Пустой список — служба печати
    /// недоступна; окно настроек в этом случае просто оставляет «принтер по умолчанию».</summary>
    public static string[] InstalledPrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            var queues = server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections });
            var names = new System.Collections.Generic.List<string>();
            foreach (var q in queues)
            {
                names.Add(q.Name);
                q.Dispose();
            }
            return names.ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Сервер печати здесь СОЗНАТЕЛЬНО не оборачивается в using, в отличие от
    /// <see cref="InstalledPrinters"/>: там наружу уходят только строки, а здесь — сама очередь, и
    /// живёт она лишь пока жив её сервер. Освободив сервер, мы вернули бы очередь, на которой
    /// PrintVisual падает, — то есть печать на ЯВНО ВЫБРАННЫЙ принтер не работала бы вовсе.</summary>
    private static PrintQueue? TryFindQueue(string name)
    {
        try
        {
            var server = new LocalPrintServer();
            foreach (var q in server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections }))
            {
                if (string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase)) return q;
                q.Dispose();
            }
        }
        catch (Exception)
        {
            // Служба печати недоступна — вызывающий напечатает на принтер по умолчанию.
        }
        return null;
    }
}
