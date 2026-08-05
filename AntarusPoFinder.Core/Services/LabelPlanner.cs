using System;
using System.Collections.Generic;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Прямоугольник этикетки в миллиметрах. Свой, а не System.Windows.Rect: расчёт живёт в
/// Core, где WPF нет, — иначе компоновку нельзя было бы проверить тестами без окна.</summary>
public readonly record struct LabelBox(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public bool IsEmpty => W <= 0.01 || H <= 0.01;

    public LabelBox Deflate(double mm) =>
        new(X + mm, Y + mm, Math.Max(0, W - 2 * mm), Math.Max(0, H - 2 * mm));

    /// <summary>Лежит ли целиком внутри <paramref name="other"/>. Допуск — на накопленную ошибку
    /// double: сравниваются суммы миллиметров, и «ровно по краю» не должно считаться выходом.</summary>
    public bool Inside(LabelBox other, double tolerance = 0.01) =>
        IsEmpty ||
        (X >= other.X - tolerance && Y >= other.Y - tolerance &&
         Right <= other.Right + tolerance && Bottom <= other.Bottom + tolerance);
}

/// <summary>Готовая раскладка этикетки: где именно лежит код, где заголовок, где ссылка и каким
/// кеглем их печатать. Всё в миллиметрах от левого верхнего угла этикетки.
///
/// Отрисовка (LabelPrinter в App) ничего не решает сама — она только раскладывает элементы по этим
/// прямоугольникам. Благодаря этому предпросмотр и печать физически не могут разойтись, а сама
/// раскладка проверяется тестами без единого окна.</summary>
public sealed record LabelPlan
{
    /// <summary>Вся этикетка целиком.</summary>
    public LabelBox Page { get; init; }

    /// <summary>Печатная область: этикетка минус поля, сдвинутая калибровкой и обрезанная краем
    /// бумаги. Всё содержимое обязано лежать внутри неё.</summary>
    public LabelBox Band { get; init; }

    /// <summary>Рамка по краю — по границе печатной области, а НЕ по физическому краю этикетки:
    /// нарисованная у самого края, она первой и уходила в непечатаемую кромку.</summary>
    public LabelBox? Frame { get; init; }

    public LabelBox Qr { get; init; }

    /// <summary>Подпись назначения («Инструкция для заказчика»). Пустой прямоугольник — подписи нет.
    /// Отдельным блоком, а не частью заголовка: она объясняет НАЗНАЧЕНИЕ наклейки и должна читаться
    /// первой, а заголовок с версией — это уже «что именно». Место берётся у текста, а не у кода
    /// (см. LabelPlanner.Plan): наклейку делают ради того, чтобы код взяла камера.</summary>
    public LabelBox Headline { get; init; }

    /// <summary>Блок «заголовок + подзаголовок». Пустой прямоугольник — текста нет вовсе.</summary>
    public LabelBox Title { get; init; }

    /// <summary>Строка со ссылкой. Пустой прямоугольник — ссылку не печатаем.</summary>
    public LabelBox Caption { get; init; }

    public double TitlePt { get; init; }
    public double SubtitlePt { get; init; }
    public double CaptionPt { get; init; }
    public double HeadlinePt { get; init; }
    public int CaptionLines { get; init; }

    /// <summary>Текст ушёл ПОД код, а не вправо от него: на узкой этикетке рядом с кодом ему просто
    /// не остаётся ширины.</summary>
    public bool Stacked { get; init; }

    /// <summary>Что пришлось изменить против заданных настроек. Показывается человеку прямо в
    /// предпросмотре: раскладка молча не обрезает содержимое, но и молча не притворяется, что
    /// напечатает ровно то, что попросили.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool HasTitle => !Title.IsEmpty;
    public bool HasCaption => !Caption.IsEmpty;
    public bool HasHeadline => !Headline.IsEmpty;
    public string WarningText => string.Join("\n", Warnings);

    /// <summary>Главный инвариант: ничего не торчит за печатную область. Проверяется тестами на
    /// переборе настроек — именно его нарушение и давало «QR обрезан, текст улетел».</summary>
    public bool FitsInsideBand() =>
        Qr.Inside(Band) && Title.Inside(Band) && Caption.Inside(Band) && Headline.Inside(Band) &&
        (Frame is not { } f || f.Inside(Page));
}

/// <summary>Компоновка этикетки: единственное место, где решается, что куда ляжет.
///
/// <b>Что было сломано.</b> Раскладку раньше собирал WPF-Grid в LabelPrinter, а размер кода считался
/// отдельной формулой в LabelLayout — и эти два расчёта не сходились. Под ссылку формула резервировала
/// 2.6 строки кегля, а сам TextBlock мог занять 3.6 строки плюс отступ; заданная человеком сторона
/// кода вписывалась в высоту БЕЗ учёта рамки и отступа между блоками; сдвиг макета вообще двигал
/// содержимое за край, не отнимая при этом ни миллиметра у места под содержимое. Отсюда и жалоба:
/// «увеличил QR — обрезался текст, убрал сдвиг — обрезан QR, увеличил сдвиг — текст улетел». Никакой
/// «правильной» комбинации не было: любая настройка ломала другую.
///
/// <b>Как считается теперь.</b> Сверху вниз, вычитая занятое место из остатка:
/// <list type="number">
/// <item>печатная область = этикетка − поля, сдвинутая калибровкой и обрезанная краем бумаги;</item>
/// <item>рамка (если нужна) съедает свою толщину;</item>
/// <item>снизу отрезается блок ссылки — ровно столько строк, сколько реально уйдёт на печать;</item>
/// <item>в оставшемся вверху прямоугольнике код получает сторону не больше остатка, а текст —
///       всё, что осталось правее кода (или под ним, если правее места нет).</item>
/// </list>
/// Заданные человеком величины при этом не игнорируются, а ограничиваются: если сторона кода или
/// кегль не помещаются, они уменьшаются, и об этом пишется предупреждение — обрезать содержимое
/// раскладка не имеет права ни при каких значениях настроек.</summary>
public static class LabelPlanner
{
    /// <summary>Минимальная ширина текстовой колонки рядом с кодом. Уже — и заголовок превращается в
    /// лесенку из одной буквы, поэтому вместо этого текст переезжает под код.</summary>
    public const double MinTextMm = 16;

    /// <summary>Меньше этого код не печатаем: на 203 dpi модули сливаются, и телефон его не берёт.</summary>
    public const double MinQrMm = 12;

    public const double GapMm = 2.5;
    public const double CaptionGapMm = 1.2;
    public const int MaxCaptionLines = 3;

    /// <summary>Толщина рамки и её отступ до содержимого.</summary>
    public const double FrameMm = 0.35;
    public const double FramePadMm = 0.6;

    public const double MinTitlePt = 5;
    public const double MinCaptionPt = 5;

    /// <summary>Доля высоты, больше которой ссылке не отдаём: она вспомогательная, а место нужно коду.
    ///
    /// 0.3, а не 0.4: на мелкой наклейке 40×30 ссылка съедала столько, что код ужимался почти до
    /// нижнего предела (~13 мм) и читался плохо. На крупных размерах (97.5×72) разницы нет — там
    /// ссылка и так укладывается в свои две-три строки задолго до предела доли.</summary>
    public const double CaptionShareMax = 0.3;

    /// <summary>Доля ОБЛАСТИ ТЕКСТА под подпись назначения («Инструкция для заказчика»): не больше
    /// трети того места, что и так отведено под заголовок. У кода она не забирает ничего (см. Plan),
    /// но и заголовок с версией задавить не должна — подпись из двух строк уже прочитана.</summary>
    public const double HeadlineShareMax = 0.34;

    public const int MaxHeadlineLines = 2;

    /// <summary>Кегль подписи назначения относительно заголовка. Чуть мельче названия установки:
    /// она поясняющая, а не главная.</summary>
    public const double HeadlinePtFactor = 0.8;

    /// <summary>Сторона одного модуля кода, ниже которой обычная телефонная камера начинает
    /// промахиваться. На термопринтере 203 dpi точка — 0.125 мм, и на модуль надо минимум три
    /// точки; 0.4 мм — это те же три точки с запасом на разброс печати. Проверяется отдельно от
    /// <see cref="MinQrMm"/>: код на 20 мм читается прекрасно, пока в нём 25 модулей, и не читается
    /// вовсе, когда в него зашили длинную ссылку и модулей стало 57.</summary>
    public const double MinModuleMm = 0.4;

    /// <summary>Ширина «среднего» символа в долях кегля. Segoe UI кириллицей даёт около половины
    /// кегля, жирное начертание — чуть больше. Оценка нужна только чтобы заранее отвести место;
    /// от ошибки оценки страхует сама отрисовка (текст ужимается в отведённый прямоугольник).</summary>
    public const double CharWidthRegular = 0.52;
    public const double CharWidthBold = 0.58;

    public static double PtToMm(double pt) => pt * 25.4 / 72;

    /// <summary>Высота строки с межстрочным интервалом.</summary>
    public static double LineHeightMm(double pt) => PtToMm(pt) * 1.25;

    /// <summary>Сколько строк займёт текст в колонке шириной <paramref name="widthMm"/>.
    ///
    /// Переносится текст там же, где его переносит WPF: по пробелам, ПОСЛЕ косой черты и дефиса
    /// (для ссылки это главное — она одно слово, но рвётся по сегментам пути, и потому занимает
    /// заметно больше строк, чем при простой набивке по символам), а кусок длиннее строки рвётся
    /// посередине.</summary>
    public static int EstimateLines(string? text, double widthMm, double pt, double charWidthFactor = CharWidthRegular)
    {
        if (string.IsNullOrWhiteSpace(text) || widthMm <= 0) return 0;

        var charMm = Math.Max(0.01, PtToMm(pt) * charWidthFactor);
        var perLine = Math.Max(1, (int)Math.Floor(widthMm / charMm));
        var lines = 0;

        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var here = 1;
            var used = 0;
            foreach (var (chunk, spaceBefore) in Chunks(paragraph))
            {
                var need = chunk + (spaceBefore && used > 0 ? 1 : 0);
                if (used > 0 && used + need > perLine)
                {
                    here++;
                    used = 0;
                    need = chunk;
                }

                if (need > perLine)
                {
                    var extra = (need - 1) / perLine;
                    here += extra;
                    used = need - extra * perLine;
                }
                else
                {
                    used += need;
                }
            }

            lines += here;
        }

        return Math.Max(1, lines);
    }

    /// <summary>Куски, которые перенос не разрывает: слово или его часть, заканчивающаяся косой
    /// чертой либо дефисом. Отдаётся длина куска и был ли перед ним пробел.</summary>
    private static IEnumerable<(int Length, bool SpaceBefore)> Chunks(string paragraph)
    {
        var length = 0;
        var spaceBefore = false;
        var pendingSpace = false;

        foreach (var ch in paragraph)
        {
            if (ch is ' ' or '\t')
            {
                if (length > 0)
                {
                    yield return (length, spaceBefore);
                    length = 0;
                }
                pendingSpace = true;
                continue;
            }

            if (length == 0)
            {
                spaceBefore = pendingSpace;
                pendingSpace = false;
            }

            length++;
            if (ch is '/' or '-' or '\\')
            {
                yield return (length, spaceBefore);
                length = 0;
            }
        }

        if (length > 0) yield return (length, spaceBefore);
    }

    /// <summary>Высота блока «заголовок + подзаголовок» при заданном кегле заголовка.</summary>
    public static double TextBlockHeightMm(string? title, string? subtitle, double widthMm, double titlePt)
    {
        var h = EstimateLines(title, widthMm, titlePt, CharWidthBold) * LineHeightMm(titlePt);
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var subPt = SubtitlePtFor(titlePt);
            h += CaptionGapMm + EstimateLines(subtitle, widthMm, subPt) * LineHeightMm(subPt);
        }
        return h;
    }

    public static double SubtitlePtFor(double titlePt) => Math.Max(MinTitlePt, titlePt * 0.72);

    // ── Сама компоновка ──────────────────────────────────────────────────────

    public static LabelPlan Plan(LabelLayout raw, string? title, string? subtitle, string? caption)
    {
        var v = raw.Clamped();
        var warnings = new List<string>();
        var page = new LabelBox(0, 0, v.WidthMm, v.HeightMm);

        var band = Band(v, warnings);
        if (band.IsEmpty)
        {
            // Вырожденный случай (поля/сдвиг съели всё) — печатаем пустую этикетку, но честно
            // говорим об этом, а не рисуем содержимое поверх края.
            warnings.Add("Поля и сдвиг не оставили места под содержимое — уменьшите поля или сдвиг.");
            return new LabelPlan { Page = page, Band = band, Warnings = warnings };
        }

        LabelBox? frame = null;
        var inner = band;
        if (v.ShowFrame && band.W > 6 * (FrameMm + FramePadMm) && band.H > 6 * (FrameMm + FramePadMm))
        {
            frame = band;
            inner = band.Deflate(FrameMm + FramePadMm);
        }

        var (captionBox, captionPt, captionLines) = PlanCaption(v, inner, caption, warnings);

        var belowCaption = captionBox.IsEmpty
            ? inner
            : new LabelBox(inner.X, inner.Y, inner.W, Math.Max(0, inner.H - captionBox.H - CaptionGapMm));

        var hasText = !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(subtitle);

        // ГЛАВНОЕ ПРАВИЛО подписи назначения: она НЕ отнимает место у кода. Наклейку делают ради
        // того, чтобы код взяла камера телефона, а полоса во всю ширину сверху съедала высоту как
        // раз у него (на 97.5×72 сторона падала с 55 до 44 мм, на 40×30 — с 15.6 до 12.1, то есть к
        // самому пределу читаемости). Поэтому подпись садится ПЕРВОЙ СТРОКОЙ в область текста — в
        // колонку рядом с кодом или в блок под ним, — а свою полосу берёт только там, где текста
        // нет вовсе и отнимать не у кого.
        LabelBox qr, textArea, headlineBox = default;
        bool stacked;
        var headlinePt = HeadlinePtFor(v);

        if (hasText)
        {
            (qr, textArea, stacked) = PlanQrAndText(v, belowCaption, hasText, warnings);
            if (!textArea.IsEmpty)
            {
                (headlineBox, headlinePt) = PlanHeadline(v, textArea, warnings);
                if (!headlineBox.IsEmpty)
                    textArea = new LabelBox(textArea.X, textArea.Y + headlineBox.H + CaptionGapMm, textArea.W,
                        Math.Max(0, textArea.H - headlineBox.H - CaptionGapMm));
            }
        }
        else
        {
            (headlineBox, headlinePt) = PlanHeadline(v, belowCaption, warnings);
            var forCode = headlineBox.IsEmpty
                ? belowCaption
                : new LabelBox(belowCaption.X, belowCaption.Y + headlineBox.H + CaptionGapMm, belowCaption.W,
                    Math.Max(0, belowCaption.H - headlineBox.H - CaptionGapMm));
            (qr, textArea, stacked) = PlanQrAndText(v, forCode, hasText, warnings);
        }

        var (titleBox, titlePt) = PlanTitle(v, textArea, title, subtitle, warnings);

        return new LabelPlan
        {
            Page = page,
            Band = band,
            Frame = frame,
            Qr = qr,
            Headline = headlineBox,
            Title = titleBox,
            Caption = captionBox,
            TitlePt = titlePt,
            SubtitlePt = SubtitlePtFor(titlePt),
            CaptionPt = captionPt,
            HeadlinePt = headlinePt,
            CaptionLines = captionLines,
            Stacked = stacked,
            Warnings = warnings,
        };
    }

    /// <summary>Сколько модулей в коде помещается на миллиметр — по этому числу и решается, возьмёт
    /// ли код обычная камера. Отдельная функция, потому что число модулей знает только тот, кто уже
    /// закодировал ссылку (Core про QRCoder не знает), а предупреждение выдавать надо здесь.</summary>
    public static bool ModulesAreReadable(double qrSideMm, int modules, out double moduleMm)
    {
        moduleMm = modules <= 0 ? 0 : qrSideMm / modules;
        return modules <= 0 || moduleMm >= MinModuleMm - 0.0001;
    }

    /// <summary>Кегль подписи назначения — чуть мельче заголовка: она поясняющая, а не главная.</summary>
    private static double HeadlinePtFor(LabelLayout v) => Math.Max(MinTitlePt, v.TitlePt * HeadlinePtFactor);

    /// <summary>Подпись назначения наклейки — первой строкой в отведённой ей области (см. Plan: это
    /// область ТЕКСТА, а не полоса, отрезанная у кода). Пустой текст — блока нет вовсе; длинная
    /// строка сначала ужимается кеглем, а если и это не помогает, честно предупреждаем и отдаём ей
    /// не больше <see cref="HeadlineShareMax"/> высоты области.</summary>
    private static (LabelBox Box, double Pt) PlanHeadline(LabelLayout v, LabelBox area, List<string> warnings)
    {
        var text = v.EffectiveHeadline();
        var pt = HeadlinePtFor(v);
        if (area.IsEmpty || text.Length == 0) return (default, pt);

        var budget = area.H * HeadlineShareMax;
        var lines = EstimateLines(text, area.W, pt, CharWidthBold);
        while (pt > MinTitlePt && (lines > MaxHeadlineLines || lines * LineHeightMm(pt) > budget))
        {
            pt = Math.Max(MinTitlePt, pt - 0.5);
            lines = EstimateLines(text, area.W, pt, CharWidthBold);
        }

        var allowed = Math.Min(MaxHeadlineLines, Math.Max(1, (int)Math.Floor(budget / LineHeightMm(pt))));
        var shown = Math.Min(lines, allowed);
        var height = shown * LineHeightMm(pt);

        if (height + CaptionGapMm >= area.H)
        {
            warnings.Add("Подпись назначения не помещается — она не напечатается. Увеличьте этикетку или снимите галочку «Подпись назначения».");
            return (default, pt);
        }

        if (shown < lines)
            warnings.Add("Подпись назначения длиннее двух строк — на этикетке поместится только её начало. Сократите текст.");

        return (new LabelBox(area.X, area.Y, area.W, height), pt);
    }

    /// <summary>Печатная область. Сдвиг здесь не просто двигает содержимое (так и уезжал текст за
    /// край), а двигает саму полосу, после чего она обрезается краем этикетки: съехавшая полоса
    /// становится уже, содержимому достаётся меньше места — и оно остаётся на бумаге.</summary>
    private static LabelBox Band(LabelLayout v, List<string> warnings)
    {
        var offX = LimitOffset(v.OffsetXMm, v.WidthMm, v.MarginMm);
        var offY = LimitOffset(v.OffsetYMm, v.HeightMm, v.MarginMm);
        if (Math.Abs(offX - v.OffsetXMm) > 0.01 || Math.Abs(offY - v.OffsetYMm) > 0.01)
            warnings.Add($"Сдвиг больше половины этикетки не имеет смысла — учтён как {Mm(offX)} / {Mm(offY)} мм.");

        var x0 = Math.Clamp(v.MarginMm + offX, 0, v.WidthMm);
        var x1 = Math.Clamp(v.WidthMm - v.MarginMm + offX, 0, v.WidthMm);
        var y0 = Math.Clamp(v.MarginMm + offY, 0, v.HeightMm);
        var y1 = Math.Clamp(v.HeightMm - v.MarginMm + offY, 0, v.HeightMm);

        var band = new LabelBox(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));

        var full = Math.Max(0, v.WidthMm - 2 * v.MarginMm) * Math.Max(0, v.HeightMm - 2 * v.MarginMm);
        if (full > 0 && band.W * band.H < full - 0.01)
            warnings.Add("Сдвиг вывел макет к краю — содержимое ужато, чтобы не уйти за этикетку.");

        return band;
    }

    /// <summary>Сдвиг не должен съедать больше половины полосы: дальше это уже не калибровка
    /// принтера, а способ выкинуть содержимое с этикетки.</summary>
    private static double LimitOffset(double off, double sideMm, double marginMm)
    {
        var half = Math.Max(0, (sideMm - 2 * marginMm) / 2);
        return Math.Clamp(off, -half, half);
    }

    private static (LabelBox Box, double Pt, int Lines) PlanCaption(
        LabelLayout v, LabelBox inner, string? caption, List<string> warnings)
    {
        if (!v.ShowLink || string.IsNullOrWhiteSpace(caption) || inner.IsEmpty)
            return (default, v.CaptionPt, 0);

        var pt = v.CaptionPt;
        var budget = inner.H * CaptionShareMax;
        var lines = EstimateLines(caption, inner.W, pt);

        // Сначала пробуем уместить ссылку, уменьшая кегль: одна строка мелким шрифтом полезнее трёх
        // строк, которые отняли у кода половину этикетки.
        while (pt > MinCaptionPt && (lines > MaxCaptionLines || lines * LineHeightMm(pt) > budget))
        {
            pt = Math.Max(MinCaptionPt, pt - 0.5);
            lines = EstimateLines(caption, inner.W, pt);
        }

        var allowed = Math.Min(MaxCaptionLines, Math.Max(1, (int)Math.Floor(budget / LineHeightMm(pt))));
        var shown = Math.Min(lines, allowed);
        var height = shown * LineHeightMm(pt);

        if (height + CaptionGapMm >= inner.H)
        {
            warnings.Add("Ссылка не помещается — печатается только код. Увеличьте этикетку или снимите «Печатать ссылку».");
            return (default, pt, 0);
        }

        if (pt < v.CaptionPt - 0.01)
            warnings.Add($"Кегль ссылки уменьшен до {Mm(pt)} пт — заданный не помещался.");
        if (shown < lines)
            warnings.Add("Ссылка длиннее трёх строк — на этикетке будет виден только её начальный кусок.");

        return (new LabelBox(inner.X, inner.Bottom - height, inner.W, height), pt, shown);
    }

    /// <summary>Код и место под текст. Сторона кода — не больше того, что реально осталось: заданная
    /// в настройках величина именно ограничивается, а не «побеждает» остальные блоки.</summary>
    private static (LabelBox Qr, LabelBox Text, bool Stacked) PlanQrAndText(
        LabelLayout v, LabelBox upper, bool hasText, List<string> warnings)
    {
        if (upper.IsEmpty) return (default, default, false);

        var wanted = v.QrMm > 0 ? v.QrMm : double.MaxValue;   // 0 — «сам», берём максимум по месту
        var side = Math.Min(wanted, Math.Min(upper.W, upper.H));
        var stacked = false;

        if (hasText)
        {
            var forColumn = upper.W - GapMm - MinTextMm;
            if (side > forColumn)
            {
                if (forColumn >= MinQrMm)
                {
                    side = forColumn;
                }
                else
                {
                    // Рядом с кодом текстовой колонке не остаётся ширины — ставим текст под код.
                    stacked = true;
                    var forStack = upper.H - GapMm - LineHeightMm(MinTitlePt) * 2;
                    side = Math.Min(Math.Min(wanted, upper.W), Math.Max(MinQrMm, forStack));
                    side = Math.Min(side, upper.H);
                }
            }
        }

        if (v.QrMm > 0 && side < v.QrMm - 0.01)
            warnings.Add($"Сторона QR уменьшена до {Mm(side)} мм — заданные {Mm(v.QrMm)} мм не помещаются в печатную область.");
        if (side < MinQrMm - 0.01)
            warnings.Add($"Код получился меньше {Mm(MinQrMm)} мм — телефон может его не взять. Нужна этикетка крупнее.");

        LabelBox qr, text;
        if (!hasText)
        {
            qr = new LabelBox(upper.X + (upper.W - side) / 2, upper.Y + (upper.H - side) / 2, side, side);
            text = default;
        }
        else if (stacked)
        {
            qr = new LabelBox(upper.X + (upper.W - side) / 2, upper.Y, side, side);
            text = new LabelBox(upper.X, upper.Y + side + GapMm, upper.W, Math.Max(0, upper.H - side - GapMm));
        }
        else
        {
            qr = new LabelBox(upper.X, upper.Y + (upper.H - side) / 2, side, side);
            text = new LabelBox(upper.X + side + GapMm, upper.Y, Math.Max(0, upper.W - side - GapMm), upper.H);
        }

        if (hasText && text.IsEmpty)
            warnings.Add("Для заголовка не осталось места — печатается только код.");

        return (qr, text, stacked);
    }

    /// <summary>Кегль заголовка подбирается под отведённый прямоугольник: заданные 16 пт на узкой
    /// колонке дают лесенку в семь строк, которая раньше просто вылезала за этикетку.</summary>
    private static (LabelBox Box, double Pt) PlanTitle(
        LabelLayout v, LabelBox area, string? title, string? subtitle, List<string> warnings)
    {
        var pt = v.TitlePt;
        if (area.IsEmpty || (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(subtitle)))
            return (default, pt);

        var need = TextBlockHeightMm(title, subtitle, area.W, pt);
        while (pt > MinTitlePt && need > area.H)
        {
            pt = Math.Max(MinTitlePt, pt - 0.5);
            need = TextBlockHeightMm(title, subtitle, area.W, pt);
        }

        if (pt < v.TitlePt - 0.01)
            warnings.Add($"Кегль заголовка уменьшен до {Mm(pt)} пт — заданный не помещался в отведённое место.");
        if (need > area.H + 0.01)
            warnings.Add("Заголовок слишком длинный для этикетки — он будет ужат целиком, но мелко.");

        var height = Math.Min(area.H, need);
        return (new LabelBox(area.X, area.Y + (area.H - height) / 2, area.W, height), pt);
    }

    private static string Mm(double value) =>
        value.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
}
