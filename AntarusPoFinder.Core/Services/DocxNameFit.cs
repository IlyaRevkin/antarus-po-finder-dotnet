using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Настройки «как ужимать длинное название шкафа».</summary>
/// <param name="Enabled">Выключено — подстановка ведёт себя ровно как до появления подгонки.</param>
/// <param name="MinScale">Насколько сильно вообще позволено уменьшать кегль, долей от исходного.
/// Ниже этого текст перестаёт читаться на бумаге, и дальше в дело идёт сжатие самих букв.</param>
/// <param name="MinPointSize">Тот же пол, но абсолютный: у бланка с мелким шрифтом половина кегля —
/// это уже нечитаемо в принципе, сколько ни считай в долях.</param>
public sealed record NameFitOptions(bool Enabled = true, double MinScale = 0.55, double MinPointSize = 7.0)
{
    public static readonly NameFitOptions Default = new();
    public static readonly NameFitOptions Off = new(Enabled: false);

    /// <summary>Кегль в Word задаётся половинками пункта (w:sz), поэтому пол считаем сразу в них.</summary>
    public int MinHalfPoints => Math.Max(2, (int)Math.Round(MinPointSize * 2));
}

/// <param name="HalfPoints">Кегль, которым печатать значение (половинки пункта, как w:sz).</param>
/// <param name="FitTextTwips">Ширина, в которую Word обязан вписать значение, сжав сами буквы
/// (w:fitText). null — обошлись уменьшением кегля.</param>
public readonly record struct NameFitDecision(int HalfPoints, int? FitTextTwips);

/// <summary>Подгонка длинного названия шкафа под то место, которое отведено ему в бланке.
///
/// Зачем: в бланке паспорта название стоит в готовой рамке — строке таблицы или строке титульного
/// листа. Короткое («ЩУН-3») встаёт как влитое, а длинное («Щит управления насосной станцией
/// пожаротушения ЩУНП-11/2-А») переносится на вторую строку, строка таблицы вырастает, и хвост
/// бланка уезжает на следующую страницу — ровно то, о чём просил Илья: «если оно длинное — чтобы
/// текст не уехал на другую страницу и форматирование не сбилось».
///
/// Как: подставленное значение получает СВОЙ кегль, подобранный так, чтобы строка уложилась в
/// отведённую ширину. Ни переносов, ни лишних абзацев не появляется — меняется только размер шрифта
/// у самого названия, всё остальное в бланке (жирная подпись слева, шапка, поля) остаётся как было.
/// Уменьшать бесконечно нельзя, поэтому есть пол (<see cref="NameFitOptions"/>); если и на нём
/// название не влезает, к нему добавляется w:fitText — штатное «вписать текст в ширину» Word,
/// который дожимает уже сами буквы. Пол при этом гарантирует, что до сжатия букв дело доходит
/// только у совсем неправдоподобных названий.
///
/// Почему не переносом внутри рамки: перенос — это и есть вторая строка, из-за которой всё и
/// едет. Почему не обрезанием: паспорт уходит заказчику, и молча потерянный хвост названия хуже
/// мелкого шрифта.
///
/// Ширина считается по самому документу: ячейка таблицы знает свою ширину (w:tcW), абзац вне
/// таблицы — полосу набора страницы (w:pgSz минус w:pgMar) минус свои отступы. Ширина текста
/// оценивается по таблице средних ширин символов (<see cref="CharEms"/>) — точных метрик шрифта у
/// нас нет и быть не может (шрифт живёт на машине, а не в docx), но оценка нужна не «до пикселя», а
/// чтобы решить, уменьшать ли кегль и насколько; ошибка в пару процентов здесь ничего не меняет.</summary>
public static class DocxNameFit
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>Твипы (1/1440 дюйма) в одной половинке пункта: 1 пункт = 20 твипов.</summary>
    public const int TwipsPerHalfPoint = 10;

    /// <summary>Кегль, когда в документе он нигде не задан явно: 11 пунктов — то, что Word ставит
    /// новому документу.</summary>
    public const int DefaultHalfPoints = 22;

    /// <summary>Полоса набора, когда страницу измерить не по чему (колонтитул, обрывок документа):
    /// A4 книжная с полями Word по умолчанию (по дюйму слева и справа).</summary>
    public const int FallbackWidthTwips = 9026;

    /// <summary>Поле ячейки таблицы по умолчанию с каждой стороны (Word ставит 0,19 см).</summary>
    private const int DefaultCellMarginTwips = 108;

    // ── Ширина текста ────────────────────────────────────────────────────────────────────────

    /// <summary>Средняя ширина символа в долях кегля (em). Числа — не из метрик конкретного шрифта,
    /// а усреднённые по обычным для бланков гарнитурам (Times New Roman, Calibri, Arial): нам нужно
    /// отличить «влезает» от «не влезает», а не сверстать строку до пикселя. Разряды выделены
    /// отдельно, потому что название шкафа наполовину состоит из цифр и дефисов, и считать их по
    /// строчной букве значило бы систематически завышать ширину.</summary>
    public static double CharEms(char c)
    {
        if (c == ' ' || c == '\u00A0') return 0.28;
        if (char.IsDigit(c)) return 0.55;
        if ("iljItfr()[]{}.,;:'!|/\\-`\"".IndexOf(c) >= 0) return 0.32;
        if ("MWmwШЩЖЮжшщю@%—–".IndexOf(c) >= 0) return 0.90;
        if (char.IsUpper(c)) return 0.68;
        return 0.52;
    }

    /// <summary>Сколько твипов займёт текст, набранный кеглем <paramref name="halfPoints"/>.</summary>
    public static double TextWidthTwips(string? text, int halfPoints)
    {
        if (string.IsNullOrEmpty(text) || halfPoints <= 0) return 0;
        var ems = 0.0;
        foreach (var c in text) ems += CharEms(c);
        return ems * halfPoints * TwipsPerHalfPoint;
    }

    // ── Решение ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Чистая арифметика подгонки, вынесенная отдельно от разбора XML: сколько места есть
    /// (<paramref name="availableTwips"/>), сколько из него уже занято остальным текстом строки
    /// (<paramref name="otherTwips"/>) и сколько просит название, набранное исходным кеглем
    /// (<paramref name="valueTwipsAtBase"/>).</summary>
    public static NameFitDecision Decide(double availableTwips, double otherTwips, double valueTwipsAtBase,
        int baseHalfPoints, NameFitOptions options)
    {
        if (baseHalfPoints <= 0) baseHalfPoints = DefaultHalfPoints;
        if (!options.Enabled || valueTwipsAtBase <= 0 || availableTwips <= 0)
            return new NameFitDecision(baseHalfPoints, null);

        var room = availableTwips - otherTwips;
        // Подпись слева («Наименование:») съела всю строку — считать, что места нет вовсе, нельзя:
        // тогда любое название уходило бы на минимальный кегль. Оставляем названию пятую часть
        // строки: это заведомо меньше, чем есть на самом деле (подпись переносится вместе с ним),
        // и решение остаётся в безопасную сторону.
        if (room <= 0) room = availableTwips * 0.2;
        if (valueTwipsAtBase <= room) return new NameFitDecision(baseHalfPoints, null);

        var floorHalf = Math.Max(options.MinHalfPoints, (int)Math.Floor(baseHalfPoints * options.MinScale));
        if (floorHalf > baseHalfPoints) floorHalf = baseHalfPoints;

        // Ширина пропорциональна кеглю, поэтому нужный кегль считается напрямую. Вниз, а не к
        // ближайшему: «почти влезает» — это всё ещё вторая строка.
        var wanted = (int)Math.Floor(baseHalfPoints * (room / valueTwipsAtBase));
        var half = Math.Clamp(wanted, floorHalf, baseHalfPoints);

        // Даже на минимальном кегле не влезает — дожимаем сами буквы штатным «вписать текст».
        return wanted >= half ? new NameFitDecision(half, null) : new NameFitDecision(half, Math.Max(1, (int)Math.Floor(room)));
    }

    // ── Разбор документа ─────────────────────────────────────────────────────────────────────

    /// <summary>Кегль по умолчанию для всего документа — из word/styles.xml (docDefaults). Нужен
    /// потому, что в самом бланке у абзаца с меткой размер шрифта чаще всего не проставлен: он
    /// наследуется от стиля. Стилей мы не раскручиваем (это отдельная машина наследования), но
    /// документный умолчательный размер закрывает подавляющее большинство бланков; нет и его —
    /// <see cref="DefaultHalfPoints"/>.</summary>
    public static int DocumentDefaultHalfPoints(XDocument? styles)
    {
        var sz = styles?.Root?.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr")?.Element(W + "sz");
        return ReadHalfPoints(sz) ?? DefaultHalfPoints;
    }

    /// <summary>Кегль, которым реально набран прогон: его собственный w:sz, иначе размер, заданный
    /// абзацу целиком (w:pPr/w:rPr/w:sz), иначе документный умолчательный.</summary>
    public static int EffectiveHalfPoints(XElement? run, XElement paragraph, int documentDefault)
    {
        var own = ReadHalfPoints(run?.Element(W + "rPr")?.Element(W + "sz"));
        if (own is not null) return own.Value;
        var para = ReadHalfPoints(paragraph.Element(W + "pPr")?.Element(W + "rPr")?.Element(W + "sz"));
        return para ?? (documentDefault > 0 ? documentDefault : DefaultHalfPoints);
    }

    private static int? ReadHalfPoints(XElement? sz)
    {
        var raw = sz?.Attribute(W + "val")?.Value;
        if (raw is null) return null;
        // Word пишет сюда целое число половинок пункта, но встречаются и «22.0» от сторонних
        // генераторов — разбираем снисходительно, инвариантной культурой (в файле всегда точка).
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0) return v;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0) return (int)Math.Round(d);
        return null;
    }

    /// <summary>Ширина места, отведённого абзацу: ячейка таблицы — по своей ширине за вычетом полей,
    /// обычный абзац — полоса набора страницы минус его отступы. Ничего измеримого рядом нет
    /// (колонтитул, кусок документа без sectPr) — <see cref="FallbackWidthTwips"/>.</summary>
    public static int AvailableWidthTwips(XElement paragraph)
    {
        var cell = paragraph.Ancestors(W + "tc").FirstOrDefault();
        if (cell is not null && CellWidthTwips(cell) is { } cellWidth) return cellWidth;

        var width = PageTextWidthTwips(paragraph);
        var indent = paragraph.Element(W + "pPr")?.Element(W + "ind");
        width -= Twips(indent, "left") + Twips(indent, "start") + Twips(indent, "right") + Twips(indent, "end");
        return Math.Max(400, width);
    }

    private static int? CellWidthTwips(XElement cell)
    {
        var w = cell.Element(W + "tcPr")?.Element(W + "tcW");
        var type = w?.Attribute(W + "type")?.Value;
        // pct/auto — ширина в процентах таблицы или «по содержимому»: пересчитать её здесь не из
        // чего (нужна ширина самой таблицы и раскладка колонок), поэтому меряем страницей.
        if (w is null || (type is not null && !type.Equals("dxa", StringComparison.OrdinalIgnoreCase))) return null;
        if (!int.TryParse(w.Attribute(W + "w")?.Value, out var dxa) || dxa <= 0) return null;

        var margins = CellMarginTwips(cell, "left") + CellMarginTwips(cell, "right");
        return Math.Max(400, dxa - margins);
    }

    private static int CellMarginTwips(XElement cell, string side)
    {
        var own = cell.Element(W + "tcPr")?.Element(W + "tcMar")?.Element(W + side);
        if (own is not null && int.TryParse(own.Attribute(W + "w")?.Value, out var v) && v >= 0) return v;

        var table = cell.Ancestors(W + "tbl").FirstOrDefault();
        var shared = table?.Element(W + "tblPr")?.Element(W + "tblCellMar")?.Element(W + side);
        if (shared is not null && int.TryParse(shared.Attribute(W + "w")?.Value, out var t) && t >= 0) return t;

        return DefaultCellMarginTwips;
    }

    private static int PageTextWidthTwips(XElement paragraph)
    {
        // Раздел абзаца: свой (последний абзац раздела несёт sectPr внутри pPr) либо общий в конце тела.
        var sect = paragraph.Element(W + "pPr")?.Element(W + "sectPr")
                   ?? paragraph.Ancestors(W + "body").FirstOrDefault()?.Element(W + "sectPr")
                   ?? paragraph.Document?.Root?.Element(W + "body")?.Element(W + "sectPr");
        if (sect is null) return FallbackWidthTwips;

        var pgSz = sect.Element(W + "pgSz");
        if (pgSz is null || !int.TryParse(pgSz.Attribute(W + "w")?.Value, out var pageWidth) || pageWidth <= 0)
            return FallbackWidthTwips;

        var mar = sect.Element(W + "pgMar");
        var text = pageWidth - Twips(mar, "left") - Twips(mar, "right") - Twips(mar, "gutter");
        return text > 400 ? text : FallbackWidthTwips;
    }

    private static int Twips(XElement? element, string attribute)
    {
        var raw = element?.Attribute(W + attribute)?.Value;
        return raw is not null && int.TryParse(raw, out var v) ? v : 0;
    }
}
