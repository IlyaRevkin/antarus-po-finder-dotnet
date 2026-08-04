using System;
using System.Globalization;

namespace AntarusPoFinder.Core.Services;

/// <summary>Макет этикетки с QR — всё, что задаёт её вид, одной записью. Отдельно от самой отрисовки
/// (LabelPrinter живёт в App: там WPF), потому что настройки читаются и пишутся в Core и должны
/// проверяться тестами без окна.
///
/// <b>Зачем понадобилось.</b> Раньше настраивались только ширина и высота, а всё остальное было
/// зашито в код: поля 0, ссылка 8-м кеглем в самом низу, рамка вплотную к краю. На настоящем
/// принтере этикеток это давало ровно то, о чём была жалоба: «что 97, что 90, что 100 ставлю —
/// верх обрезается», и ссылку «плохо пропечатывает, очень мелкая». Причина у обеих одна: печать
/// начинается от физического угла листа, у которого есть непечатаемая зона, а поля этикетки были
/// нулевыми, и первые миллиметры содержимого просто не попадали на бумагу.
///
/// Поэтому здесь есть и <see cref="MarginMm"/> (отступ содержимого от краёв — лечит обрезку сам по
/// себе), и <see cref="OffsetXMm"/>/<see cref="OffsetYMm"/> (сдвиг всего макета — на случай, когда
/// принтер смещает лист, и лечить надо не полями, а именно сдвигом). Разделены они намеренно:
/// поля влияют на то, сколько места остаётся содержимому, а сдвиг — нет.</summary>
public sealed record LabelLayout
{
    public double WidthMm { get; init; } = 97.5;
    public double HeightMm { get; init; } = 72;

    /// <summary>Отступ содержимого от краёв этикетки. По умолчанию 3 мм — с запасом больше
    /// непечатаемой зоны любого известного термопринтера этикеток.</summary>
    public double MarginMm { get; init; } = 3;

    /// <summary>Сдвиг всего содержимого вправо/вниз (можно отрицательный) — калибровка под
    /// конкретный принтер. Per-machine, как и имя принтера.</summary>
    public double OffsetXMm { get; init; }
    public double OffsetYMm { get; init; }

    /// <summary>Сторона QR-кода. 0 — «сам»: столько, сколько остаётся по высоте, но не больше
    /// половины ширины этикетки.</summary>
    public double QrMm { get; init; }

    public double TitlePt { get; init; } = 16;

    /// <summary>Кегль строки со ссылкой. 9 вместо прежних зашитых 8 — на 203 dpi термопринтере
    /// восьмёрка кириллицей уже разваливается.</summary>
    public double CaptionPt { get; init; } = 9;

    /// <summary>Печатать ли ссылку текстом под QR. Она нужна, когда телефон код не берёт, но на
    /// узкой этикетке иногда важнее заголовок — поэтому выключаемо.</summary>
    public bool ShowLink { get; init; } = true;

    /// <summary>Рамка по краю этикетки. На листах с высечкой она помогает попасть в границы, на
    /// рулоне — только тратит тонер.</summary>
    public bool ShowFrame { get; init; } = true;

    /// <summary>Рисованный QR (скруглённые модули, фирменные «глаза», подпись в центре) вместо
    /// обычной чёрной матрицы. Читается теми же сканерами: уровень коррекции Q допускает потерю до
    /// четверти кода, а вырез под подпись занимает заметно меньше.</summary>
    public bool FancyQr { get; init; } = true;

    // ── Границы разумного ────────────────────────────────────────────────────

    /// <summary>Приводит значения к рабочему диапазону. Нужна и при чтении настроек (там могло
    /// оказаться что угодно, в т.ч. чужое синхронизированное), и при живой правке в окне: этикетка
    /// с полями в половину своей ширины не должна складываться в отрицательный размер.</summary>
    public LabelLayout Clamped()
    {
        var w = Clamp(WidthMm, 20, 300);
        var h = Clamp(HeightMm, 15, 300);
        return this with
        {
            WidthMm = w,
            HeightMm = h,
            MarginMm = Clamp(MarginMm, 0, Math.Min(w, h) / 4),
            OffsetXMm = Clamp(OffsetXMm, -20, 20),
            OffsetYMm = Clamp(OffsetYMm, -20, 20),
            QrMm = QrMm <= 0 ? 0 : Clamp(QrMm, 10, Math.Min(w, h)),
            TitlePt = Clamp(TitlePt, 6, 48),
            CaptionPt = Clamp(CaptionPt, 5, 24),
        };
    }

    /// <summary>Сторона QR в миллиметрах с учётом «0 = сам».
    ///
    /// Раньше здесь была своя формула, и именно её расхождение с настоящей вёрсткой давало «увеличил
    /// QR — обрезался текст»: место под ссылку она оценивала в 2.6 строки кегля, а сам блок ссылки
    /// занимал до 3.6 строки плюс отступ, и про рамку с промежутком между блоками формула не знала
    /// вовсе. Теперь величина берётся из общей компоновки (<see cref="LabelPlanner"/>) — той самой,
    /// по которой этикетка и рисуется, так что разойтись им больше негде.</summary>
    public double EffectiveQrMm() => LabelPlanner.Plan(this, "Заголовок", "", ShowLink ? "ссылка" : "").Qr.W;

    private static double Clamp(double value, double min, double max) =>
        double.IsNaN(value) ? min : Math.Min(max, Math.Max(min, value));

    // ── Настройки ────────────────────────────────────────────────────────────

    public static LabelLayout FromConfig(ConfigService cfg) => new LabelLayout
    {
        WidthMm = cfg.LabelWidthMm(),
        HeightMm = cfg.LabelHeightMm(),
        MarginMm = cfg.LabelNumber("label_margin_mm", 3),
        OffsetXMm = cfg.LabelNumber("label_offset_x_mm", 0),
        OffsetYMm = cfg.LabelNumber("label_offset_y_mm", 0),
        QrMm = cfg.LabelNumber("label_qr_mm", 0),
        TitlePt = cfg.LabelNumber("label_title_pt", 16),
        CaptionPt = cfg.LabelNumber("label_caption_pt", 9),
        ShowLink = cfg.LabelFlag("label_show_link", true),
        ShowFrame = cfg.LabelFlag("label_show_frame", true),
        FancyQr = cfg.LabelFlag("label_fancy_qr", true),
    }.Clamped();

    public void SaveTo(ConfigService cfg)
    {
        var v = Clamped();
        cfg.SetLabelWidthMm(v.WidthMm);
        cfg.SetLabelHeightMm(v.HeightMm);
        cfg.SetLabelNumber("label_margin_mm", v.MarginMm);
        cfg.SetLabelNumber("label_offset_x_mm", v.OffsetXMm);
        cfg.SetLabelNumber("label_offset_y_mm", v.OffsetYMm);
        cfg.SetLabelNumber("label_qr_mm", v.QrMm);
        cfg.SetLabelNumber("label_title_pt", v.TitlePt);
        cfg.SetLabelNumber("label_caption_pt", v.CaptionPt);
        cfg.SetLabelFlag("label_show_link", v.ShowLink);
        cfg.SetLabelFlag("label_show_frame", v.ShowFrame);
        cfg.SetLabelFlag("label_fancy_qr", v.FancyQr);
    }

    public string SizeCaption() =>
        $"{WidthMm.ToString("0.##", CultureInfo.CurrentCulture)} × {HeightMm.ToString("0.##", CultureInfo.CurrentCulture)}";
}
