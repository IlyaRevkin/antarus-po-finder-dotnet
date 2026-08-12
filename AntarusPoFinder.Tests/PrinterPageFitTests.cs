using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.Tests;

/// <summary>Подгонка макета этикетки под настройки драйвера. Смысл тестов — в том, что правила
/// проверяются на машине без принтера: сам опрос драйвера (App/PrinterPageProbe) только переводит
/// единицы, а все решения приняты здесь.</summary>
public class PrinterPageFitTests
{
    private static PrinterPageSetup Setup(double w, double h, double left, double top, double right, double bottom) =>
        new()
        {
            PrinterName = "Zebra",
            PageWidthMm = w,
            PageHeightMm = h,
            LeftMm = left,
            TopMm = top,
            RightMm = right,
            BottomMm = bottom,
        };

    [Fact]
    public void Takes_page_size_and_puts_each_edge_into_its_own_margin()
    {
        var fit = PrinterPageFit.Apply(new LabelLayout(), Setup(100, 70, 1.5, 2, 1.5, 2));

        Assert.Equal(100, fit.Layout.WidthMm);
        Assert.Equal(70, fit.Layout.HeightMm);
        // Каждая кромка ложится в поле своей стороны. Раньше здесь бралась самая широкая на все
        // четыре — и по узким сторонам содержимое отступало лишнее, что и добирали сдвигом.
        Assert.Equal(new LabelMargins(1.5, 2, 1.5, 2), fit.Layout.Margins);
        Assert.Empty(fit.Notes);
    }

    [Fact]
    public void Symmetric_edges_leave_no_offset()
    {
        var fit = PrinterPageFit.Apply(new LabelLayout(), Setup(100, 70, 2, 2, 2, 2));

        Assert.Equal(0, fit.Layout.OffsetXMm);
        Assert.Equal(0, fit.Layout.OffsetYMm);
    }

    [Fact]
    public void Asymmetric_edges_become_asymmetric_margins_and_leave_the_shift_alone()
    {
        // Слева печатать нельзя 4 мм, справа — 1. Раньше поле бралось одно (4 мм со всех сторон), а
        // перекос добирался сдвигом всего макета на 1.5 мм — то есть одна кривизна лечилась другой.
        // Теперь кромки просто ложатся в поля своих сторон, и двигать нечего.
        var fit = PrinterPageFit.Apply(new LabelLayout { OffsetXMm = 4, OffsetYMm = -3 }, Setup(100, 70, 4, 1, 1, 3));

        Assert.Equal(new LabelMargins(4, 1, 1, 3), fit.Layout.Margins);
        Assert.Equal(0, fit.Layout.OffsetXMm);
        Assert.Equal(0, fit.Layout.OffsetYMm);
    }

    [Fact]
    public void Sets_previous_manual_calibration_back_to_zero_when_printer_is_symmetric()
    {
        // Сдвиг подбирали руками под прежний принтер. Если новый печатает симметрично, оставленная
        // калибровка — это ровно та кривизна, от которой уходили.
        var crooked = new LabelLayout { OffsetXMm = 4, OffsetYMm = -3 };
        var fit = PrinterPageFit.Apply(crooked, Setup(100, 70, 2, 2, 2, 2));

        Assert.Equal(0, fit.Layout.OffsetXMm);
        Assert.Equal(0, fit.Layout.OffsetYMm);
    }

    [Fact]
    public void Keeps_a_reserve_when_driver_claims_edge_to_edge_printing()
    {
        var fit = PrinterPageFit.Apply(new LabelLayout(), Setup(100, 70, 0, 0, 0, 0));

        Assert.Equal(PrinterPageFit.ReserveMm, fit.Layout.MarginMm);
        Assert.Contains(fit.Notes, n => n.Contains("до самого края"));
    }

    [Fact]
    public void Nonsense_edges_do_not_become_negative_margins()
    {
        var fit = PrinterPageFit.Apply(new LabelLayout(), Setup(100, 70, -5, double.NaN, 2, 2));

        // Ерунда от драйвера считается «кромки нет», а не отрицательным полем; запас всё равно
        // остаётся (ReserveMm), иначе вернулся бы обрезанный край.
        Assert.Equal(new LabelMargins(PrinterPageFit.ReserveMm, PrinterPageFit.ReserveMm, 2, 2), fit.Layout.Margins);
        Assert.False(double.IsNaN(fit.Layout.OffsetXMm));
        Assert.False(double.IsNaN(fit.Layout.OffsetYMm));
    }

    [Fact]
    public void Silent_driver_keeps_the_size_that_was_set_by_hand()
    {
        var current = new LabelLayout { WidthMm = 97.5, HeightMm = 72 };
        var fit = PrinterPageFit.Apply(current, Setup(0, 0, 1, 1, 1, 1));

        Assert.Equal(97.5, fit.Layout.WidthMm);
        Assert.Equal(72, fit.Layout.HeightMm);
        Assert.Equal(1, fit.Layout.MarginMm);
        Assert.Contains(fit.Notes, n => n.Contains("оставлены прежними"));
    }

    [Fact]
    public void Office_printer_with_a4_paper_is_applied_but_called_out()
    {
        var fit = PrinterPageFit.Apply(new LabelLayout(), Setup(210, 297, 5, 5, 5, 5));

        // Заказанное делаем: человек мог осознанно печатать наклейки на листе. Но молчать нельзя —
        // чаще это просто «выбран не тот принтер».
        Assert.Equal(210, fit.Layout.WidthMm);
        Assert.Contains(fit.Notes, n => n.Contains("A4"));
    }

    [Fact]
    public void Margin_never_eats_more_than_the_layout_allows()
    {
        // Кромка в 20 мм на наклейке 40 × 30 — это не поля, а описание совсем другой бумаги.
        var fit = PrinterPageFit.Apply(new LabelLayout(), Setup(40, 30, 20, 20, 20, 20));

        Assert.True(fit.Layout.MarginMm <= 30.0 / 4);
        Assert.Contains(fit.Notes, n => n.Contains("не ту бумагу"));
    }

    [Fact]
    public void Nothing_but_size_margin_and_offset_is_touched()
    {
        var current = new LabelLayout
        {
            TitlePt = 22,
            CaptionPt = 11,
            Style = QrStyle.Classic,
            QrPlace = QrPlacement.Right,
            HeadlineText = "Руководство по эксплуатации",
            HoleText = "ИНСТ",
            ShowFrame = false,
        };

        var fit = PrinterPageFit.Apply(current, Setup(100, 70, 2, 2, 2, 2));

        Assert.Equal(current.TitlePt, fit.Layout.TitlePt);
        Assert.Equal(current.CaptionPt, fit.Layout.CaptionPt);
        Assert.Equal(current.Style, fit.Layout.Style);
        Assert.Equal(current.QrPlace, fit.Layout.QrPlace);
        Assert.Equal(current.HeadlineText, fit.Layout.HeadlineText);
        Assert.Equal(current.HoleText, fit.Layout.HoleText);
        Assert.False(fit.Layout.ShowFrame);
    }

    [Fact]
    public void Report_names_the_printer_and_both_numbers()
    {
        var fit = PrinterPageFit.Apply(new LabelLayout(), Setup(100, 70, 1, 2, 1, 2));

        Assert.Contains("Zebra", fit.Summary);
        Assert.Contains("100", fit.Summary);
        Assert.Contains("70", fit.Summary);
        Assert.Contains(fit.Summary, fit.Text);
    }
}
