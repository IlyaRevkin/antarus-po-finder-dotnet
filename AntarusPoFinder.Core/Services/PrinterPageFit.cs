using System.Collections.Generic;
using System.Globalization;

namespace AntarusPoFinder.Core.Services;

/// <summary>Страница так, как её описывает драйвер принтера: размер листа (для принтера этикеток —
/// размер самой наклейки) и непечатаемая кромка с каждой стороны, всё в миллиметрах.
///
/// Зачем понадобилось: у принтера этикеток размер наклейки и её поля уже настроены — в свойствах
/// принтера, один раз и на всю пачку. До сих пор эти же величины приходилось вбивать в макет заново
/// и подбирать на глаз по обрезанной печати. Спросить их у драйвера умеет только App (System.Printing),
/// но решать, что с ними делать, обязан Core: подгонка макета — это правила, а не работа с железом, и
/// проверяться она должна тестами на машине без принтера.</summary>
public sealed record PrinterPageSetup
{
    /// <summary>Имя принтера, который отвечал. Может отличаться от заказанного: пустая настройка
    /// означает «принтер Windows по умолчанию», и в отчёте человеку важно видеть, кого спросили.</summary>
    public string PrinterName { get; init; } = "";

    public double PageWidthMm { get; init; }
    public double PageHeightMm { get; init; }

    /// <summary>Непечатаемая кромка — та полоса у края листа, куда принтер физически не кладёт
    /// краску. Именно из-за неё «верх обрезается», когда поля макета нулевые.</summary>
    public double LeftMm { get; init; }
    public double TopMm { get; init; }
    public double RightMm { get; init; }
    public double BottomMm { get; init; }

    public double MaxUnprintableMm =>
        Math.Max(Math.Max(Clean(LeftMm), Clean(RightMm)), Math.Max(Clean(TopMm), Clean(BottomMm)));

    /// <summary>Отрицательная или нечисловая кромка — это не «принтер печатает за краем листа», а
    /// драйвер, сообщивший ерунду; считаем такую сторону обычной, без запретной полосы.</summary>
    internal static double Clean(double mm) => double.IsNaN(mm) || double.IsInfinity(mm) || mm < 0 ? 0 : mm;
}

/// <summary>Что получилось из настроек драйвера: готовый макет, строка-отчёт для человека и
/// оговорки — то, о чём драйвер умолчал или соврал.</summary>
public sealed record PrinterFitResult(LabelLayout Layout, string Summary, IReadOnlyList<string> Notes)
{
    /// <summary>Отчёт и оговорки одним текстом — окну этикетки больше ничего и не нужно.</summary>
    public string Text => Notes.Count == 0 ? Summary : Summary + "\n" + string.Join("\n", Notes);
}

/// <summary>Подгонка макета этикетки под то, что уже настроено у принтера.
///
/// Правила ровно три, и каждое отвечает на свою жалобу:
///   • <b>размер</b> берётся с листа драйвера — наклейку не надо мерить линейкой и вписывать руками;
///   • <b>поля</b> — самая широкая непечатаемая кромка, одинаково со всех сторон: содержимому нужно
///     начинаться после запретной полосы, а разные поля по сторонам макет и не умеет;
///   • <b>сдвиг</b> — половина разницы между противоположными кромками: поля одинаковые, а запретные
///     полосы у большинства принтеров нет, и без сдвига содержимое стоит по центру ЛИСТА, а не по
///     центру того, что реально печатается. Это и есть «отцентровал».
///
/// Ничего, кроме размера, полей и сдвига, здесь не трогается: кегли, вид кода и подписи — решение
/// человека, а не принтера.</summary>
public static class PrinterPageFit
{
    /// <summary>Запас, который остаётся, даже если драйвер уверяет, что печатает до самого края.
    /// Термопринтеры этикеток так отвечают сплошь и рядом, а первый миллиметр всё равно съедают —
    /// с нулевыми полями мы бы вернулись ровно к тому, с чего начинали («верх обрезается»).</summary>
    public const double ReserveMm = 1.0;

    /// <summary>Во сколько раз лист драйвера должен разойтись с текущей наклейкой, чтобы об этом
    /// стоило сказать вслух. Полтора — это уже не «поправили размер», а «выбран другой принтер»:
    /// на обычном офисном принтере драйвер честно сообщит A4, и наклейка станет листом.</summary>
    private const double SuspiciousRatio = 1.5;

    public static PrinterFitResult Apply(LabelLayout current, PrinterPageSetup setup)
    {
        var notes = new List<string>();
        var left = PrinterPageSetup.Clean(setup.LeftMm);
        var top = PrinterPageSetup.Clean(setup.TopMm);
        var right = PrinterPageSetup.Clean(setup.RightMm);
        var bottom = PrinterPageSetup.Clean(setup.BottomMm);

        var (width, height, sizeTaken) = ResolveSize(current, setup, notes);

        var margin = Math.Max(setup.MaxUnprintableMm, ReserveMm);
        if (setup.MaxUnprintableMm < ReserveMm)
            notes.Add($"Драйвер сообщает, что печатает до самого края. Поля всё равно оставлены " +
                      $"{Num(ReserveMm)} мм: у принтеров этикеток кромка обычно съедается, а нулевые поля — " +
                      "это и есть обрезанный верх.");

        var layout = (current with
        {
            WidthMm = width,
            HeightMm = height,
            MarginMm = Round(margin),
            // Сдвиг — в сторону той кромки, что уже, то есть туда, где печатной площади больше.
            OffsetXMm = Round((left - right) / 2),
            OffsetYMm = Round((top - bottom) / 2),
        }).Clamped();

        if (Math.Abs(layout.MarginMm - Round(margin)) > 0.05)
            notes.Add($"Поля {Num(margin)} мм для наклейки {layout.SizeCaption()} мм — больше четверти её " +
                      $"стороны, поэтому оставлены {Num(layout.MarginMm)} мм. Похоже, драйвер описывает не ту бумагу.");

        return new PrinterFitResult(layout, Summarize(setup, layout, sizeTaken, left, top, right, bottom), notes);
    }

    /// <summary>Размер листа из драйвера, если он вообще годится в наклейку. Не годится — оставляем
    /// прежний: подставить сюда ноль (драйвер промолчал) значило бы стереть настроенный размер.</summary>
    private static (double Width, double Height, bool Taken) ResolveSize(
        LabelLayout current, PrinterPageSetup setup, List<string> notes)
    {
        var w = setup.PageWidthMm;
        var h = setup.PageHeightMm;
        if (double.IsNaN(w) || double.IsNaN(h) || w < MinSideMm || h < MinSideMm || w > MaxSideMm || h > MaxSideMm)
        {
            notes.Add("Размер листа драйвер не сообщил (или он не похож на наклейку) — ширина и высота " +
                      "оставлены прежними, подставлены только поля и сдвиг.");
            return (current.WidthMm, current.HeightMm, false);
        }

        if (w > current.WidthMm * SuspiciousRatio || h > current.HeightMm * SuspiciousRatio)
            notes.Add($"Лист у принтера заметно больше прежней наклейки ({Num(w)} × {Num(h)} против " +
                      $"{current.SizeCaption()} мм). Так отвечает обычный принтер с бумагой A4 — если " +
                      "выбран не принтер этикеток, размер стоит вернуть.");

        return (Round(w), Round(h), true);
    }

    /// <summary>Границы того, что вообще может быть наклейкой. Взяты по самой мягкой стороне макета
    /// (<see cref="LabelLayout.Clamped"/> держит высоту от 15 мм, ширину от 20): что пролезло сюда,
    /// но узко для ширины, макет подтянет сам. За этими границами подстановка только навредит —
    /// лист в палец шириной или в три метра описывает не наклейку.</summary>
    private const double MinSideMm = 15;
    private const double MaxSideMm = 300;

    private static string Summarize(PrinterPageSetup setup, LabelLayout layout, bool sizeTaken,
        double left, double top, double right, double bottom)
    {
        var who = string.IsNullOrWhiteSpace(setup.PrinterName) ? "Принтер по умолчанию" : $"Принтер «{setup.PrinterName}»";
        var sheet = sizeTaken
            ? $"лист {Num(setup.PageWidthMm)} × {Num(setup.PageHeightMm)} мм"
            : "размер листа не сообщён";
        var edges = $"непечатаемые края {Num(left)} / {Num(top)} / {Num(right)} / {Num(bottom)} мм (слева / сверху / справа / снизу)";
        return $"{who}: {sheet}, {edges}. Подставлено: наклейка {layout.SizeCaption()} мм, поля " +
               $"{Num(layout.MarginMm)} мм, сдвиг {Num(layout.OffsetXMm)} / {Num(layout.OffsetYMm)} мм.";
    }

    /// <summary>Десятая миллиметра — предел, в котором это вообще имеет смысл: печатать точнее не
    /// умеет ни один принтер этикеток, а длинный хвост цифр в поле только пугает.</summary>
    private static double Round(double mm) => Math.Round(mm, 1, MidpointRounding.AwayFromZero);

    private static string Num(double mm) => Round(mm).ToString("0.##", CultureInfo.CurrentCulture);
}
