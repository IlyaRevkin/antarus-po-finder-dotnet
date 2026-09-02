using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Media;

namespace AntarusPoFinder.App;

/// <summary>Цвет оформления. Задаётся ЛЮБОЙ — палитрой или кодом вида #1E66F5, а не выбором из
/// заранее заготовленного списка.
///
/// Из одного выбранного цвета выводится всё остальное: оттенок под курсором, подсветка выбранной
/// строки и — главное — цвет надписи НА этом цвете. Последнее здесь не украшение, а условие
/// работоспособности: подпись на цветной кнопке раньше всегда была белой, поэтому список цветов
/// приходилось ограничивать тёмными — на светлом фоне белый текст исчезал. Теперь цвет надписи
/// считается от яркости фона (см. <see cref="TextOn"/>), и потому можно отдать выбор целиком
/// человеку: жёлтый получит тёмную подпись, синий — белую, и читаемо будет в обоих случаях.
///
/// Тема (светлая/тёмная) и цвет независимы: тема отвечает за фон и текст страницы, цвет — только за
/// акцент. Поэтому производные считаются с оглядкой на тему: на тёмном фоне тот же цвет нужно
/// приглушить, иначе он выжигает глаза.</summary>
public static class AccentPalette
{
    /// <summary>Синий — исходный цвет программы. Он же значение по умолчанию: у тех, кто цвет не
    /// выбирал, ничего измениться не должно.</summary>
    public const string DefaultHex = "#1E66F5";

    /// <summary>Готовые образцы для палитры — чтобы не подбирать цвет вручную, если не хочется.
    /// Это НЕ ограничение выбора: рядом есть поле для любого своего кода цвета.</summary>
    public static readonly IReadOnlyList<(string Hex, string Name)> Samples = new[]
    {
        ("#1E66F5", "Синий"),
        ("#0F7A80", "Бирюзовый"),
        ("#1F7A4C", "Зелёный"),
        ("#6B3FC9", "Фиолетовый"),
        ("#C2255C", "Малиновый"),
        ("#C74A1C", "Кирпичный"),
        ("#B7791F", "Охра"),
        ("#3E4C59", "Графитовый"),
    };

    /// <summary>Разбирает код цвета. Мусор и пустоту не считает ошибкой: настройка едет в общем
    /// конфиге и может прийти с чужой машины, а программа обязана открыться, а не встать на старте.</summary>
    public static Color Parse(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            var s = hex.Trim();
            if (!s.StartsWith("#", StringComparison.Ordinal)) s = "#" + s;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(s);
                return Color.FromRgb(c.R, c.G, c.B);
            }
            catch { /* не разобрали — ниже вернём цвет по умолчанию */ }
        }
        var d = (Color)ColorConverter.ConvertFromString(DefaultHex);
        return Color.FromRgb(d.R, d.G, d.B);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Относительная яркость по формуле WCAG. Именно она, а не «среднее из R, G и B»:
    /// глаз видит зелёный заметно светлее синего той же величины, и наивное среднее ошибается как раз
    /// на цветах вроде насыщенного жёлтого.</summary>
    public static double Luminance(Color c)
    {
        static double Ch(byte v)
        {
            var x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    public static double Contrast(Color a, Color b)
    {
        var (l1, l2) = (Luminance(a), Luminance(b));
        if (l1 < l2) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    /// <summary>Цвет надписи НА выбранном цвете: белый или почти чёрный — что даёт больший контраст.
    /// Благодаря этому выбор цвета можно не ограничивать: светлый фон сам получит тёмную подпись.</summary>
    public static Color TextOn(Color accent)
    {
        var dark = Color.FromRgb(0x11, 0x14, 0x18);
        return Contrast(accent, Colors.White) >= Contrast(accent, dark) ? Colors.White : dark;
    }

    /// <summary>Оттенок под курсором. Светлый цвет затемняем, тёмный осветляем — сдвиг всегда в
    /// сторону контраста с фоном, иначе на краях палитры (почти белый, почти чёрный) наведение
    /// становится незаметным и кнопка выглядит залипшей.</summary>
    public static Color Hover(Color accent, bool darkTheme)
    {
        var lum = Luminance(accent);

        // Края палитры разбираем отдельно: почти чёрный затемнять некуда (умножение на долю оставляет
        // ноль нулём), почти белый — осветлять. Без этих двух случаев на крайних цветах наведение
        // не давало вообще никакого отклика, и кнопка выглядела залипшей.
        if (lum < 0.02) return Shift(accent, 0.22);
        if (lum > 0.92) return Shift(accent, -0.12);

        var factor = lum > 0.5 ? -0.14 : (darkTheme ? -0.10 : 0.16);
        return Shift(accent, factor);
    }

    /// <summary>Подсветка выбранной строки — тот же цвет, сильно разбавленный фоном темы: заливать
    /// строку полным акцентом нельзя, текст на ней задаётся темой и потеряется.</summary>
    public static Color SelectedBackground(Color accent, bool darkTheme)
    {
        var bg = darkTheme ? Color.FromRgb(0x1E, 0x1E, 0x2E) : Colors.White;
        return Mix(accent, bg, darkTheme ? 0.72 : 0.74);
    }

    private static Color Shift(Color c, double f)
    {
        static byte Cl(double v) => (byte)Math.Clamp(v, 0, 255);
        return f >= 0
            ? Color.FromRgb(Cl(c.R + (255 - c.R) * f), Cl(c.G + (255 - c.G) * f), Cl(c.B + (255 - c.B) * f))
            : Color.FromRgb(Cl(c.R * (1 + f)), Cl(c.G * (1 + f)), Cl(c.B * (1 + f)));
    }

    private static Color Mix(Color a, Color b, double towardsB)
    {
        static byte Cl(double v) => (byte)Math.Clamp(v, 0, 255);
        var t = Math.Clamp(towardsB, 0, 1);
        return Color.FromRgb(Cl(a.R + (b.R - a.R) * t), Cl(a.G + (b.G - a.G) * t), Cl(a.B + (b.B - a.B) * t));
    }

    /// <summary>Совместимость с 1.74.9.1, где цвет хранился именем из короткого списка. Без этого у
    /// тех, кто уже выбрал цвет, настройка не прочиталась бы и молча вернулась к синему.</summary>
    public static string NormalizeStored(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return DefaultHex;
        var s = stored.Trim();
        if (s.StartsWith("#", StringComparison.Ordinal)) return s;
        return s.ToLowerInvariant() switch
        {
            "blue" => "#1E66F5",
            "green" => "#1F7A4C",
            "purple" => "#6B3FC9",
            "teal" => "#0F7A80",
            "crimson" => "#C2255C",
            _ => "#" + s,
        };
    }
}
