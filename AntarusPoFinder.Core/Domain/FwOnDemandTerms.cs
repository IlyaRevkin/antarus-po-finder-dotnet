using System;
using System.Collections.Generic;
using System.Linq;

namespace AntarusPoFinder.Core.Domain;

/// <summary>Слова, без которых прошивка в выдаче не показывается.
///
/// Просьба Ильи 17.09.2026 дословно: «нужно добавить, какие теги или ключевые слова для прошивки
/// удалить из поисковой выдачи. Условно, когда я ищу шкаф НГР-ПП-2-(2.5-4А)-Рх, я ищу Рх и всё ок,
/// а когда НГР-ПП-2-(2.5-4А), мне выдаёт Рх тоже, а он мне не нужен».
///
/// Причина понятна: короткий запрос — префикс длинного названия, и совпадение честное. Прятать
/// такие строки «умом» нельзя: иногда именно длинное название и нужно. Значит решать должен тот,
/// кто заводил прошивку, — и решение это про КОНКРЕТНУЮ прошивку, а не про запрос.
///
/// Правило простое: если у прошивки заданы такие слова, она попадает в выдачу, только когда хотя бы
/// одно из них есть в запросе. Не «спросили» — значит не показываем. Пустой список (а он пустой у
/// всех, кроме тех, кому это нужно) ничего не меняет.
///
/// <b>Хотя бы одно, а не все.</b> Слова здесь — синонимы одной особенности («Рх», «резерв», «Px»
/// латиницей), и требовать их все значило бы требовать от человека помнить, каким именно словом он
/// эту прошивку когда-то пометил.
///
/// Сравнение БЕЗ учёта регистра делается на стороне .NET (StringComparer.OrdinalIgnoreCase), а не
/// в SQL: SQLite COLLATE NOCASE не сворачивает кириллицу, и «РХ» с «рх» в базе разные строки, а в
/// .NET одинаковые — см. CLAUDE.md.</summary>
public static class FwOnDemandTerms
{
    private const char Separator = ',';

    /// <summary>Разобрать хранимую строку в набор слов. Пустые куски и повторы отбрасываются.</summary>
    public static List<string> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return new List<string>();
        return stored.Split(Separator, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Как набор слов хранится: через запятую, в том виде, в котором их написал человек.</summary>
    public static string Format(IEnumerable<string>? terms) =>
        terms is null ? "" : string.Join(", ", terms.Select(t => (t ?? "").Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>Привести введённое человеком к хранимому виду.</summary>
    public static string Normalize(string? raw) => Format(Parse(raw));

    /// <summary>Спросили ли про эту прошивку.
    ///
    /// Сверяем со ВСЕМ текстом запроса, а не с разобранными словами: «Рх» в запросе
    /// «НГР-ПП-2-(2.5-4А)-Рх» не отдельное слово — разделителем там дефис, и разбор на слова зависит
    /// от того, как именно человек написал запрос. Подстрока по всему запросу отвечает на вопрос
    /// «упомянул ли он это» одинаково при любом написании.</summary>
    public static bool AskedFor(string? stored, string? query)
    {
        var terms = Parse(stored);
        if (terms.Count == 0) return true; // ничего не требуется — показываем всегда
        if (string.IsNullOrWhiteSpace(query)) return false;
        return terms.Any(t => query.Contains(t, StringComparison.OrdinalIgnoreCase));
    }
}
