using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Компоновка этикетки с QR.
///
/// <b>Жалоба, из-за которой эти тесты появились.</b> «QR отрезает текст, потому что я увеличил
/// размер до 85, сделал сверху сдвиг 5 — и текст улетел; убираю сдвиг — текст обрезан и QR обрезан;
/// меняю высоту — QR обрезан; увеличиваю сдвиг — текст улетает». То есть правильного сочетания
/// настроек не существовало вовсе: раскладку собирал WPF-Grid, а сторона кода считалась отдельной
/// формулой, которая не знала ни про рамку, ни про отступы, ни про настоящую высоту блока ссылки, —
/// и любое изменение одной настройки ломало другую.
///
/// Поэтому здесь проверяется не «красиво», а единственное, что вообще имеет значение на бумаге:
/// при ЛЮБЫХ допустимых настройках всё содержимое лежит внутри печатной области. Перебором, а не
/// парой удачных примеров, — именно неудачные сочетания и были у Ильи на столе.</summary>
public class LabelPlanTests
{
    private const string Url = "https://disk.antarus.su/cloud/ПО/ПЖ%20ПИ/SMH5/1.0.0004.0003/инструкция.pdf";
    private const string Title = "SMH5 · ПЖ ПИ";
    private const string Subtitle = "1.0.0004.0003 от 01.01.2026";

    // ── Главный инвариант ────────────────────────────────────────────────────

    /// <summary>Перебор настроек, которые человек реально может выставить в окне макета. Ни одно
    /// сочетание не имеет права дать содержимое за пределами печатной области.</summary>
    [Fact]
    public void Plan_KeepsEverythingInsideThePrintableArea_ForEverySettingCombination()
    {
        var checkedCount = 0;

        foreach (var (w, h) in new[] { (97.5, 72.0), (58.0, 40.0), (40.0, 20.0), (150.0, 100.0), (20.0, 15.0) })
        foreach (var margin in new[] { 0.0, 1.0, 3.0, 5.0 })
        foreach (var offset in new[] { -20.0, -5.0, 0.0, 5.0, 20.0 })
        foreach (var qr in new[] { 0.0, 10.0, 40.0, 85.0, 300.0 })
        foreach (var titlePt in new[] { 6.0, 16.0, 48.0 })
        foreach (var captionPt in new[] { 5.0, 9.0, 24.0 })
        foreach (var showLink in new[] { true, false })
        foreach (var showFrame in new[] { true, false })
        {
            var layout = new LabelLayout
            {
                WidthMm = w, HeightMm = h, MarginMm = margin,
                OffsetXMm = offset, OffsetYMm = -offset,
                QrMm = qr, TitlePt = titlePt, CaptionPt = captionPt,
                ShowLink = showLink, ShowFrame = showFrame,
            };

            var plan = LabelPlanner.Plan(layout, Title, Subtitle, Url);
            var what = $"{w}×{h}, поля {margin}, сдвиг {offset}, QR {qr}, кегли {titlePt}/{captionPt}, " +
                       $"ссылка {showLink}, рамка {showFrame}";

            Assert.True(plan.Band.Inside(plan.Page), $"печатная область вышла за этикетку: {what}");
            Assert.True(plan.FitsInsideBand(), $"содержимое вышло за печатную область: {what}");
            Assert.True(plan.Qr.W >= 0 && !double.IsNaN(plan.Qr.W), $"сторона кода не число: {what}");
            Assert.Equal(plan.Qr.W, plan.Qr.H, 3);
            checkedCount++;
        }

        // Проверка самой проверки: перебор действительно прошёл, а не свернулся в ноль итераций.
        Assert.True(checkedCount > 1000, $"перебрано слишком мало сочетаний: {checkedCount}");
    }

    /// <summary>Блоки не наезжают друг на друга: код, текст и ссылка идут сверху вниз и делят место,
    /// а не рисуются один поверх другого (именно так «текст обрезался кодом»).</summary>
    [Fact]
    public void Plan_BlocksDoNotOverlap()
    {
        foreach (var (w, h) in new[] { (97.5, 72.0), (58.0, 40.0), (150.0, 60.0), (30.0, 60.0) })
        foreach (var qr in new[] { 0.0, 25.0, 85.0 })
        {
            var plan = LabelPlanner.Plan(
                new LabelLayout { WidthMm = w, HeightMm = h, QrMm = qr }, Title, Subtitle, Url);
            var what = $"{w}×{h}, QR {qr}";

            if (plan.HasTitle)
            {
                var apart = plan.Stacked
                    ? plan.Title.Y >= plan.Qr.Bottom - 0.01
                    : plan.Title.X >= plan.Qr.Right - 0.01;
                Assert.True(apart, $"заголовок наехал на код: {what}");
            }

            if (plan.HasCaption)
            {
                Assert.True(plan.Caption.Y >= plan.Qr.Bottom - 0.01, $"ссылка наехала на код: {what}");
                if (plan.HasTitle)
                    Assert.True(plan.Caption.Y >= plan.Title.Bottom - 0.01, $"ссылка наехала на заголовок: {what}");
            }
        }
    }

    // ── Дословные сценарии из жалобы ─────────────────────────────────────────

    /// <summary>«Увеличил размер до 85 — QR отрезает текст». На этикетке 97.5×72 сторона 85 мм не
    /// помещается физически: раскладка обязана ужать код до остатка, а не обрезать его и текст.</summary>
    [Fact]
    public void Plan_OversizedQr_IsShrunkToFit_NotCropped()
    {
        var plan = LabelPlanner.Plan(
            new LabelLayout { WidthMm = 97.5, HeightMm = 72, MarginMm = 3, QrMm = 85 }, Title, Subtitle, Url);

        Assert.True(plan.FitsInsideBand());
        Assert.True(plan.Qr.W < 85, "код должен был ужаться до места, а не остаться 85 мм");
        Assert.True(plan.Qr.W >= LabelPlanner.MinQrMm, "код ужали до нечитаемого");
        Assert.True(plan.HasTitle, "заголовку должно остаться место рядом с кодом");
        Assert.Contains(plan.Warnings, w => w.Contains("QR", StringComparison.Ordinal));
    }

    /// <summary>«Сделал сверху сдвиг 5 — и текст улетел». Сдвиг теперь двигает не содержимое за край,
    /// а саму печатную полосу, после чего она обрезается краем этикетки: содержимого за бумагой
    /// не остаётся ни при каком сдвиге, включая предельный.</summary>
    [Fact]
    public void Plan_Offset_MovesTheBandAndShrinksIt_NothingLeavesTheLabel()
    {
        foreach (var offset in new[] { -20.0, -5.0, 5.0, 20.0 })
        {
            var plan = LabelPlanner.Plan(
                new LabelLayout { WidthMm = 97.5, HeightMm = 72, MarginMm = 3, OffsetYMm = offset, OffsetXMm = offset },
                Title, Subtitle, Url);

            Assert.True(plan.Band.Inside(plan.Page), $"полоса вышла за этикетку при сдвиге {offset}");
            Assert.True(plan.FitsInsideBand(), $"содержимое вышло за полосу при сдвиге {offset}");
        }

        // Сдвиг вниз действительно двигает содержимое (иначе настройка бесполезна), но верх полосы
        // при этом не уходит выше нуля, а низ — ниже края.
        var down = LabelPlanner.Plan(
            new LabelLayout { WidthMm = 97.5, HeightMm = 72, MarginMm = 3, OffsetYMm = 5 }, Title, Subtitle, Url);
        Assert.Equal(8, down.Band.Y, 2);
        Assert.True(down.Band.Bottom <= 72.01);
    }

    /// <summary>«Меняю высоту — QR обрезан». Высота меняется — код меняется вместе с ней и остаётся
    /// внутри; заодно проверяется, что при разумной этикетке он не вырождается в точку.</summary>
    [Fact]
    public void Plan_QrFollowsHeight_AndStaysScannable()
    {
        double previous = 0;
        foreach (var h in new[] { 30.0, 40.0, 50.0, 60.0, 72.0, 90.0 })
        {
            var plan = LabelPlanner.Plan(
                new LabelLayout { WidthMm = 97.5, HeightMm = h, MarginMm = 3 }, Title, Subtitle, Url);

            Assert.True(plan.FitsInsideBand(), $"содержимое вышло за полосу при высоте {h}");
            Assert.True(plan.Qr.W >= LabelPlanner.MinQrMm, $"код меньше {LabelPlanner.MinQrMm} мм при высоте {h}");
            Assert.True(plan.Qr.W >= previous, "с ростом высоты код не должен уменьшаться");
            previous = plan.Qr.W;
        }
    }

    // ── Отдельные блоки ──────────────────────────────────────────────────────

    /// <summary>Место под ссылку резервируется ровно то, которое она займёт. Раньше формула считала
    /// 2.6 строки кегля, а блок занимал до 3.6 строки плюс отступ — эта разница и вылезала за край.</summary>
    [Fact]
    public void Plan_CaptionReservesExactlyTheHeightItPrints()
    {
        var plan = LabelPlanner.Plan(
            new LabelLayout { WidthMm = 97.5, HeightMm = 72, MarginMm = 3 }, Title, Subtitle, Url);

        Assert.True(plan.HasCaption);
        Assert.InRange(plan.CaptionLines, 1, LabelPlanner.MaxCaptionLines);
        Assert.Equal(plan.CaptionLines * LabelPlanner.LineHeightMm(plan.CaptionPt), plan.Caption.H, 3);
        Assert.True(plan.Caption.Bottom <= plan.Band.Bottom + 0.01);
    }

    /// <summary>Ссылку можно выключить — тогда её место целиком уходит коду.</summary>
    [Fact]
    public void Plan_WithoutLink_GivesTheRoomToTheCode()
    {
        var layout = new LabelLayout { WidthMm = 97.5, HeightMm = 72, MarginMm = 3 };
        var withLink = LabelPlanner.Plan(layout, Title, Subtitle, Url);
        var without = LabelPlanner.Plan(layout with { ShowLink = false }, Title, Subtitle, Url);

        Assert.False(without.HasCaption);
        Assert.True(without.Qr.W > withLink.Qr.W);
        Assert.True(without.FitsInsideBand());
    }

    /// <summary>Длинный заголовок в узкой колонке ужимается кеглем, а не вылезает за этикетку.</summary>
    [Fact]
    public void Plan_LongTitle_ShrinksInsteadOfOverflowing()
    {
        var longTitle = "Контроллер ПЖ ПИ SMH5 с расширенным набором модулей и длинным названием";
        var plan = LabelPlanner.Plan(
            new LabelLayout { WidthMm = 97.5, HeightMm = 72, MarginMm = 3, TitlePt = 48 },
            longTitle, Subtitle, Url);

        Assert.True(plan.TitlePt < 48, "кегль заголовка должен был уменьшиться");
        Assert.True(plan.Title.Inside(plan.Band));
        Assert.Contains(plan.Warnings, w => w.Contains("заголовк", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Узкая этикетка: рядом с кодом текстовой колонке не остаётся ширины — текст уходит
    /// ПОД код, а не превращается в лесенку по одной букве и не пропадает вовсе.</summary>
    [Fact]
    public void Plan_NarrowLabel_PutsTextUnderTheCode()
    {
        var plan = LabelPlanner.Plan(
            new LabelLayout { WidthMm = 30, HeightMm = 60, MarginMm = 2, ShowLink = false }, Title, "", "");

        Assert.True(plan.Stacked, "на узкой этикетке текст должен уходить под код");
        Assert.True(plan.HasTitle);
        Assert.True(plan.Title.Y >= plan.Qr.Bottom - 0.01);
        Assert.True(plan.FitsInsideBand());
    }

    /// <summary>Рамка идёт по границе печатной области, а не по краю этикетки: у самого края её
    /// съедала непечатаемая кромка принтера. Содержимое при этом лежит внутри рамки.</summary>
    [Fact]
    public void Plan_Frame_RunsAlongThePrintableArea_AndContentStaysInsideIt()
    {
        var layout = new LabelLayout { WidthMm = 97.5, HeightMm = 72, MarginMm = 3 };
        var framed = LabelPlanner.Plan(layout, Title, Subtitle, Url);
        var bare = LabelPlanner.Plan(layout with { ShowFrame = false }, Title, Subtitle, Url);

        Assert.NotNull(framed.Frame);
        Assert.Equal(3, framed.Frame!.Value.X, 2);
        Assert.True(framed.Frame.Value.Inside(framed.Page));
        // Рамка съедает свою толщину у содержимого — без неё коду достаётся чуть больше.
        Assert.True(bare.Qr.W > framed.Qr.W);

        var inside = framed.Frame.Value.Deflate(LabelPlanner.FrameMm + LabelPlanner.FramePadMm);
        Assert.True(framed.Qr.Inside(inside));
        Assert.True(framed.Caption.Inside(inside));
    }

    /// <summary>Поля 0 — законная настройка (рулон без высечки), и она тоже обязана давать целую
    /// этикетку: содержимое просто прижимается к краю, а не выезжает за него.</summary>
    [Fact]
    public void Plan_ZeroMargin_StillFits()
    {
        var plan = LabelPlanner.Plan(
            new LabelLayout { WidthMm = 97.5, HeightMm = 72, MarginMm = 0 }, Title, Subtitle, Url);

        Assert.Equal(0, plan.Band.X, 3);
        Assert.Equal(97.5, plan.Band.W, 3);
        Assert.True(plan.FitsInsideBand());
    }

    /// <summary>На настройках по умолчанию предупреждать не о чем: если стандартная этикетка уже
    /// требует пояснений, значит, сломаны сами умолчания.</summary>
    [Fact]
    public void Plan_Defaults_ProduceNoWarnings()
    {
        var plan = LabelPlanner.Plan(new LabelLayout(), Title, Subtitle, Url);

        Assert.Empty(plan.Warnings);
        Assert.True(plan.HasTitle);
        Assert.True(plan.HasCaption);
        Assert.True(plan.Qr.W >= 40, "на стандартной этикетке 97.5×72 коду есть где развернуться");
    }

    /// <summary>Раскладка — чистая функция: предпросмотр и печать строят её по одним и тем же
    /// настройкам и обязаны получить один и тот же результат. Ради этого расчёт и вынесен из
    /// отрисовки — «версии для экрана» и «версии для принтера» больше нет.</summary>
    [Fact]
    public void Plan_IsDeterministic()
    {
        var layout = new LabelLayout { WidthMm = 80, HeightMm = 50, MarginMm = 2, QrMm = 30, OffsetXMm = 3 };
        var first = LabelPlanner.Plan(layout, Title, Subtitle, Url);
        var second = LabelPlanner.Plan(layout, Title, Subtitle, Url);

        Assert.Equal(first.Qr, second.Qr);
        Assert.Equal(first.Title, second.Title);
        Assert.Equal(first.Caption, second.Caption);
        Assert.Equal(first.TitlePt, second.TitlePt);
        Assert.Equal(first.CaptionPt, second.CaptionPt);
        Assert.Equal(first.WarningText, second.WarningText);
    }

    // ── Оценка текста ────────────────────────────────────────────────────────

    /// <summary>Оценка числа строк должна рвать длинное слово посередине — ссылка это одно слово на
    /// сто символов, и «оно не переносится» означало бы, что места под неё резервируется одна
    /// строка вместо трёх.</summary>
    [Fact]
    public void EstimateLines_BreaksLongWords_AndCountsWraps()
    {
        // Ширина под ровно 10 символов кегля 10 пт (с четвертью символа сверху, чтобы округление
        // вниз не зависело от последнего бита double).
        var width = 10.25 * LabelPlanner.PtToMm(10) * LabelPlanner.CharWidthRegular;

        Assert.Equal(1, LabelPlanner.EstimateLines("12345", width, 10));
        Assert.Equal(3, LabelPlanner.EstimateLines(new string('x', 30), width, 10));
        Assert.Equal(2, LabelPlanner.EstimateLines("абвгде ёжзийк", width, 10));
        Assert.Equal(2, LabelPlanner.EstimateLines("первая\nвторая", width, 10));
        Assert.Equal(0, LabelPlanner.EstimateLines("", width, 10));
        Assert.Equal(0, LabelPlanner.EstimateLines(null, width, 10));
    }

    /// <summary>Мелкий кегль вмещает больше символов в строку — на этом и держится автоподбор.</summary>
    [Fact]
    public void EstimateLines_SmallerFontNeedsFewerLines()
    {
        var text = new string('x', 200);
        var lines = new[] { 6.0, 9.0, 16.0 }.Select(pt => LabelPlanner.EstimateLines(text, 40, pt)).ToArray();

        Assert.True(lines[0] < lines[1] && lines[1] < lines[2], string.Join(" < ", lines.Reverse()));
    }

    // ── Согласованность со старой точкой входа ───────────────────────────────

    /// <summary>LabelLayout.EffectiveQrMm остался в API — но теперь это та же величина, что и в
    /// раскладке, а не вторая независимая формула (их расхождение и было корнем всей жалобы).</summary>
    [Fact]
    public void EffectiveQrMm_MatchesThePlan()
    {
        var layout = new LabelLayout { WidthMm = 97.5, HeightMm = 72, MarginMm = 3 }.Clamped();

        Assert.Equal(LabelPlanner.Plan(layout, "Заголовок", "", "ссылка").Qr.W, layout.EffectiveQrMm(), 3);
    }
}
