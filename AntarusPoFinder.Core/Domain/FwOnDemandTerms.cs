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
            if (!ContainsWord(haystack, w)) continue;
            // Слово есть у прошивки. Показываем, только если человек его и спросил.
            if (!ContainsWord(query, w)) return true;
        }

        return false;
    }

    /// <summary>Слово встречается КАК СЛОВО, а не куском другого слова.
    ///
    /// ⚠️ Здесь была настоящая беда, и ровно та, на которой в этом коде уже обжигались с типами
    /// пуска («ПЧ» внутри «КПЧ»). Сравнение шло простой подстрокой, и слово-исключение «Рх»
    /// совпадало внутри «а-Рх-ив», «све-рх», «ве-рх-ний» — то есть пряталась уйма прошивок, к
    /// которым оно никакого отношения не имеет, и найти их не получалось уже ничем. Отсюда жалоба
    /// «добавляю исключение — нужная прошивка перестаёт находиться при любых запросах».
    ///
    /// Границей считается всё, что не буква и не цифра: пробел, дефис, точка, скобка, край строки.
    /// Поэтому «Рх» находится в «НГР-ПП-2-(2.5-4А)-Рх» (после дефиса и до конца) и не находится
    /// в «архив». Подчёркивание границей НЕ считается: в именах файлов оно работает как часть
    /// слова, а не как разделитель.</summary>
    private static bool ContainsWord(string? text, string word)
    {
        if (string.IsNullOrEmpty(text) || word.Length == 0) return false;

        var from = 0;
        while (true)
        {
            var at = text.IndexOf(word, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;

            var beforeOk = at == 0 || IsBoundary(text[at - 1]);
            var end = at + word.Length;
            var afterOk = end >= text.Length || IsBoundary(text[end]);
            if (beforeOk && afterOk) return true;

            from = at + 1;
        }
    }

    private static bool IsBoundary(char c) => !char.IsLetterOrDigit(c) && c != '_';

    /// <summary>Привести введённое к хранимому виду: обрезать края, схлопнуть пробелы. Больше
    /// ничего — само слово принадлежит человеку.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return string.Join(" ", raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
