using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Жалоба: «на некоторых принтерах печатает лишнюю одну пустую».
///
/// Настоящая причина была не в вёрстке: этикетка уходила одной страницей размером с наклейку, а лист
/// заданию никто не задавал — брался тот, что стоит у принтера (на стенде драйвер отвечал A4/A5 при
/// наклейке 97.5 × 72). Страница документа, не совпадающая с листом, у драйверов XPS печатается как
/// есть, а у старых драйверов GDI раскладывается на несколько листов — отсюда «на некоторых».
///
/// Проверяется здесь расчётная часть: какой лист мы заказываем и совпадает ли он с тем, что рисуем.
/// Сама отправка задания живёт в App (LabelPrinter) и тестом на машине без принтера не берётся.</summary>
public class LabelPrintJobTests
{
    [Fact]
    public void Sheet_is_exactly_the_label_not_the_printable_band()
    {
        // Поля и сдвиг ужимают СОДЕРЖИМОЕ, но лист остаётся физической наклейкой: печатать её надо
        // целиком, иначе принтер добирает разницу протяжкой.
        var layout = new LabelLayout
        {
            WidthMm = 97.5,
            HeightMm = 72,
            Margins = new LabelMargins(4, 1, 1, 3),
            OffsetXMm = 2,
            OffsetYMm = -2,
        };

        var sheet = LabelPrintJob.SheetFor(layout);

        Assert.Equal(97.5, sheet.WidthMm);
        Assert.Equal(72, sheet.HeightMm);
    }

    [Fact]
    public void Sheet_is_measured_in_microns_for_the_print_schema()
    {
        var sheet = LabelPrintJob.SheetFor(new LabelLayout { WidthMm = 97.5, HeightMm = 72 });

        Assert.Equal(97500, sheet.WidthMicrons);
        Assert.Equal(72000, sheet.HeightMicrons);
    }

    [Fact]
    public void Rotating_the_content_does_not_rotate_the_label_itself()
    {
        // Наклейку режет высечка: как бы мы ни развернули текст, из принтера выходит тот же
        // прямоугольник. Поменяв здесь стороны местами, мы заказали бы лист, которого в рулоне нет.
        var sheet = LabelPrintJob.SheetFor(new LabelLayout
        {
            WidthMm = 100,
            HeightMm = 50,
            Rotation = LabelRotation.Clockwise90,
        });

        Assert.Equal(100, sheet.WidthMm);
        Assert.Equal(50, sheet.HeightMm);
    }

    [Fact]
    public void Sheet_follows_the_limits_of_the_layout()
    {
        // Заказать у драйвера лист, которого макет не допускает, нельзя: печатали бы одно, а
        // показывали другое.
        var sheet = LabelPrintJob.SheetFor(new LabelLayout { WidthMm = 5, HeightMm = 900 });

        Assert.Equal(new LabelLayout { WidthMm = 5, HeightMm = 900 }.Clamped().WidthMm, sheet.WidthMm);
        Assert.Equal(new LabelLayout { WidthMm = 5, HeightMm = 900 }.Clamped().HeightMm, sheet.HeightMm);
    }

    [Theory]
    [InlineData(97.5, 72, LabelRotation.None)]
    [InlineData(97.5, 72, LabelRotation.Clockwise90)]
    [InlineData(40, 30, LabelRotation.None)]
    [InlineData(40, 30, LabelRotation.CounterClockwise90)]
    [InlineData(100, 50, LabelRotation.Clockwise90)]
    [InlineData(30, 100, LabelRotation.None)]
    public void What_we_draw_never_sticks_out_of_the_sheet_we_ordered(double w, double h, LabelRotation rotation)
    {
        // Инвариант против лишней страницы: страница документа — это и есть наклейка целиком.
        var layout = new LabelLayout
        {
            WidthMm = w,
            HeightMm = h,
            Rotation = rotation,
            Margins = new LabelMargins(0.5, 3, 2, 1),
            OffsetXMm = 3,
            OffsetYMm = -2,
            NoteText = "Договор 42, объект «Северный»",
        };

        Assert.True(LabelPrintJob.VisualFitsSheet(layout));
    }

    [Fact]
    public void A_label_is_one_page_and_that_is_a_requirement()
    {
        // Вторая страница на принтере этикеток — это физически вторая наклейка из рулона.
        Assert.Equal(1, LabelPrintJob.Pages);
    }
}
