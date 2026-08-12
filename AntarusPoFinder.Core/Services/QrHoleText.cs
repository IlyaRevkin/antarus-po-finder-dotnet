using System.Collections.Generic;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Вёрстка подписи в окошке по центру кода — на сколько строк её разложить и как именно.
///
/// <b>Зачем.</b> Плашке в центре расти некуда: её сторона ограничена тем, что восстанавливает
/// коррекция ошибок (см. QrArt.CenterHoleRatio), поэтому длинная подпись одной строкой упиралась в
/// ширину и вырождалась в нечитаемый кегль — «ИНСТРУКЦИЯ» печаталась вдвое мельче, чем «ИНСТ», хотя
/// места по высоте оставалось предостаточно. Дословная просьба Ильи: «подпись в центре чтобы могла в
/// 2-3 строки по длине, если больше 4 букв». Разложенная в три строки, та же подпись занимает
/// квадрат вместо полоски — и кегль вырастает во столько же раз.
///
/// Разбор живёт в ядре, а не рядом с отрисовкой: «во сколько строк ляжет ИНСТРУКЦИЯ» проверяется
/// тестом без единого окна, а App только меряет и рисует уже готовые строки.</summary>
public static class QrHoleText
{
    /// <summary>Больше трёх строк плашка не вмещает: строки становятся тоньше собственного
    /// межстрочного просвета и сливаются.</summary>
    public const int MaxLines = 3;

    /// <summary>Строки подписи. Короткая (до <see cref="LabelLayout.HoleWrapAfter"/> знаков) остаётся
    /// одной строкой — «ИНСТ», разорванное на «ИН» и «СТ», читается хуже, чем целиком.</summary>
    public static IReadOnlyList<string> Wrap(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return System.Array.Empty<string>();
        if (value.Length <= LabelLayout.HoleWrapAfter) return new[] { value };

        var words = value.Split(new[] { ' ', '\t', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries);
        var lines = words.Length > 1 ? ByWords(words) : null;
        return lines ?? BySyllables(value);
    }

    /// <summary>Готовая к печати подпись: строки, склеенные переводами строки.</summary>
    public static string Format(string? text) => string.Join("\n", Wrap(text));

    /// <summary>Сколько строк займёт подпись — короткий ответ для тех, кому не нужны сами строки.</summary>
    public static int LineCount(string? text) => Wrap(text).Count;

    /// <summary>Разложить по словам, выравнивая строки по длине. Пусто (null) — слов больше, чем
    /// строк, и по словам их не собрать не перекосив; такой случай уходит на разбиение по буквам.</summary>
    private static IReadOnlyList<string>? ByWords(string[] words)
    {
        if (words.Length > MaxLines) return null;
        // Слово, которое само по себе длиннее строки при равномерном делении, оставит соседние
        // строки полупустыми — тогда лучше резать по буквам.
        var perLine = (int)System.Math.Ceiling(words.Sum(w => w.Length) / (double)words.Length);
        return words.Any(w => w.Length > perLine * 2) ? null : words;
    }

    /// <summary>Разбиение одного слова: строк ровно столько, сколько нужно, чтобы в строке было не
    /// больше <see cref="LabelLayout.HoleWrapAfter"/> знаков, но не больше <see cref="MaxLines"/>.
    /// Длины строк выравниваются — «ИНСТРУКЦИЯ» (10 знаков) в три строки идёт как 4/3/3, а не
    /// 4/4/2: разница в длине строк на плашке видна сразу, она и задаёт кегль.</summary>
    private static IReadOnlyList<string> BySyllables(string value)
    {
        var lines = System.Math.Min(MaxLines,
            (int)System.Math.Ceiling(value.Length / (double)LabelLayout.HoleWrapAfter));
        var result = new List<string>(lines);
        var start = 0;
        for (var i = 0; i < lines; i++)
        {
            // Остаток делится на оставшиеся строки; целая часть с округлением вверх идёт в текущую —
            // так лишние знаки достаются верхним строкам, а не последней.
            var take = (int)System.Math.Ceiling((value.Length - start) / (double)(lines - i));
            result.Add(value.Substring(start, take));
            start += take;
        }
        return result;
    }
}
