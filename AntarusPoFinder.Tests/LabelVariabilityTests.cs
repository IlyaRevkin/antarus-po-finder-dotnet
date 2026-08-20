using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Что добавилось в подгонку этикетки: поля по сторонам, поворот содержимого под рулонный
/// принтер и своя строка под названием. Просьба была общей — «в целом добавь больше
/// вариативности работы с этикеткой», — поэтому проверяется не «есть настройка», а что от неё
/// действительно меняется в раскладке и что она ничего не ломает.</summary>
public class LabelVariabilityTests
{
    private const string Title = "ЩУН-9 исполнение 2";
    private const string Subtitle = "1.2.0003 от 01.01.2026";
    private const string Caption = "https://fs.elitacompany.ru/po/shun/1-2-0003/rukovodstvo.pdf";

    private static LabelPlan Plan(LabelLayout layout) => LabelPlanner.Plan(layout, Title, Subtitle, Caption);

    // ── Поля по сторонам ─────────────────────────────────────────────────────

    [Fact]
    public void Each_margin_holds_back_its_own_side()
    {
        var plan = Plan(new LabelLayout { WidthMm = 100, HeightMm = 70, Margins = new LabelMargins(2, 4, 6, 8) });

        Assert.Equal(2, plan.Band.X, 3);
        Assert.Equal(4, plan.Band.Y, 3);
        Assert.Equal(100 - 2 - 6, plan.Band.W, 3);
        Assert.Equal(70 - 4 - 8, plan.Band.H, 3);
    }

    [Fact]
    public void Uneven_margins_do_not_push_content_off_the_label()
    {
        var plan = Plan(new LabelLayout { WidthMm = 60, HeightMm = 40, Margins = new LabelMargins(0, 9, 1, 2) });

        Assert.True(plan.FitsInsideBand());
    }

    [Fact]
    public void One_number_still_means_the_same_margin_everywhere()
    {
        // Прежняя настройка одним числом никуда не делась — на ней стоят и умолчание, и старые базы.
        var layout = new LabelLayout { MarginMm = 3 };

        Assert.Equal(new LabelMargins(3, 3, 3, 3), layout.Margins);
        Assert.Equal(3, layout.MarginMm);
    }

    // ── Поворот содержимого ──────────────────────────────────────────────────

    [Fact]
    public void Rotated_layout_is_planned_on_the_turned_side()
    {
        var plan = Plan(new LabelLayout { WidthMm = 100, HeightMm = 50, Rotation = LabelRotation.Clockwise90 });

        // Наклейка 100 × 50, а компоновка ведётся на 50 × 100: обратно её разворачивает отрисовка.
        Assert.Equal(50, plan.Page.W, 3);
        Assert.Equal(100, plan.Page.H, 3);
    }

    [Fact]
    public void Turning_clockwise_sends_the_top_of_the_design_to_the_right_edge()
    {
        var layout = new LabelLayout
        {
            WidthMm = 100,
            HeightMm = 50,
            Margins = new LabelMargins(1, 2, 3, 4),
            Rotation = LabelRotation.Clockwise90,
        };

        // Верх макета смотрит в правый край наклейки, значит «поле сверху» для макета — это правое
        // поле наклейки (3 мм), а «поле слева» — верхнее (2 мм).
        Assert.Equal(new LabelMargins(2, 3, 4, 1), layout.ForDesign().Margins);
    }

    [Fact]
    public void Turning_counter_clockwise_is_the_mirror_of_turning_clockwise()
    {
        var layout = new LabelLayout
        {
            WidthMm = 100,
            HeightMm = 50,
            Margins = new LabelMargins(1, 2, 3, 4),
            Rotation = LabelRotation.CounterClockwise90,
        };

        Assert.Equal(new LabelMargins(4, 1, 2, 3), layout.ForDesign().Margins);
    }

    [Fact]
    public void Calibration_shift_turns_together_with_the_layout()
    {
        // Сдвиг калибрует ПРОТЯЖКУ, то есть живёт в координатах наклейки: повёрнутому макету он
        // должен достаться развёрнутым, иначе «подвинул вправо» уехало бы вниз.
        var design = new LabelLayout { OffsetXMm = 3, OffsetYMm = -2, Rotation = LabelRotation.Clockwise90 }.ForDesign();

        Assert.Equal(-2, design.OffsetXMm, 3);
        Assert.Equal(-3, design.OffsetYMm, 3);
    }

    [Fact]
    public void A_turned_layout_still_fits()
    {
        var plan = Plan(new LabelLayout { WidthMm = 100, HeightMm = 50, Rotation = LabelRotation.CounterClockwise90 });

        Assert.True(plan.FitsInsideBand());
        Assert.False(plan.Qr.IsEmpty);
    }

    // ── Своя строка под названием ────────────────────────────────────────────

    [Fact]
    public void Own_line_takes_room_under_the_name()
    {
        var plain = Plan(new LabelLayout());
        var withNote = Plan(new LabelLayout { NoteText = "Договор 42, объект «Северный»" });

        Assert.True(withNote.Title.H > plain.Title.H,
            "своя строка должна получить своё место, иначе она напечатается поверх версии");
        Assert.True(withNote.FitsInsideBand());
    }

    [Fact]
    public void Own_line_alone_is_enough_to_reserve_a_text_area()
    {
        // Ни названия, ни версии — только своя строка: место под текст всё равно нужно, иначе она
        // просто не напечатается.
        var plan = LabelPlanner.Plan(new LabelLayout { NoteText = "Не снимать" }, "", "", Caption);

        Assert.False(plan.Title.IsEmpty);
        Assert.True(plan.FitsInsideBand());
    }

    [Fact]
    public void A_ridiculous_own_line_does_not_break_the_layout()
    {
        var plan = Plan(new LabelLayout { WidthMm = 40, HeightMm = 30, NoteText = new string('Я', 200) });

        Assert.True(plan.FitsInsideBand());
    }

    // ── Тексты для заказчика ─────────────────────────────────────────────────

    [Fact]
    public void The_label_says_manual_not_instruction()
    {
        // Поправка: «кст текст не инструкция, а руководство по эксплуатации» — это читает заказчик.
        Assert.Equal("Руководство по эксплуатации", LabelLayout.DefaultHeadline);
        Assert.Equal("Руководство по эксплуатации", new LabelLayout().EffectiveHeadline());

        // В центре кода — имя предприятия, и просили именно так: «в центре QR написано в 2 строки
        // AMPE / RUS». Разбиение считает QrHoleText (длиннее четырёх знаков — в строки), и то, что
        // семёрка ложится именно надвое, а не в три строки, — часть просьбы, а не побочный эффект.
        Assert.Equal("AMPERUS", LabelLayout.DefaultHoleText);
        Assert.Equal(new[] { "AMPE", "RUS" }, QrHoleText.Wrap(LabelLayout.DefaultHoleText));
    }
}
