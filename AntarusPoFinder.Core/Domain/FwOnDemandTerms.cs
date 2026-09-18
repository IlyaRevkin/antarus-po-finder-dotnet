using System;
using System.Collections.Generic;
using System.Linq;

namespace AntarusPoFinder.Core.Domain;

/// <summary>Правило «не показывать, пока не спросили».
///
/// Просьба Ильи 17.09.2026: «когда я ищу шкаф НГР-ПП-2-(2.5-4А)-Рх, я ищу Рх и всё ок, а когда
/// НГР-ПП-2-(2.5-4А), мне выдаёт Рх тоже, а он мне не нужен». И следом уточнение: «лучше слова
/// исключения добавить».
///
/// Совпадение в таком поиске честное: короткий запрос — префикс длинного названия. Догадкой прятать
/// нельзя, иногда нужен как раз длинный вариант. Поэтому решает человек, и решает ОДИН РАЗ СЛОВОМ,
/// а не пометкой у каждой прошивки: «Рх» относится ко всему, где оно встречается, включая то, что
/// загрузят завтра. Список слов общий и ездит между машинами (Database.SearchWords.cs).
///
/// Правило: если в описании прошивки встретилось слово-исключение, а в запросе его нет, прошивку не
/// показываем. Есть в запросе — показываем как обычно.</summary>
public static class FwOnDemandTerms
{
    /// <summary>Скрывать ли строку.
    ///
    /// <paramref name="haystack"/> — всё, по чему прошивка опознаётся глазами (название подтипа,
    /// папка, контроллер, теги, исполнение). <paramref name="query"/> — запрос целиком, как его
    /// написал человек: «Рх» в «НГР-ПП-2-(2.5-4А)-Рх» не отдельное слово, разделителем там дефис,
    /// поэтому разбор на слова тут не годится — сравниваем подстрокой.
    ///
    /// Сравнение без учёта регистра делается на стороне .NET: в SQLite COLLATE NOCASE кириллицу не
    /// сворачивает, и правило «днём работает, ночью нет» появилось бы ровно здесь.</summary>
    public static bool ShouldHide(IReadOnlyList<string> words, string? haystack, string? query)
    {
        if (words.Count == 0 || string.IsNullOrEmpty(haystack)) return false;

        foreach (var word in words)
        {
            if (string.IsNullOrWhiteSpace(word)) continue;
            var w = word.Trim();
            if (!haystack.Contains(w, StringComparison.OrdinalIgnoreCase)) continue;
            // Слово есть у прошивки. Показываем, только если человек его и спросил.
            if (string.IsNullOrEmpty(query) || !query.Contains(w, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Привести введённое к хранимому виду: обрезать края, схлопнуть пробелы. Больше
    /// ничего — само слово принадлежит человеку.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return string.Join(" ", raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
