using System;
using System.Collections.Generic;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что именно отдать человеку, когда он выбрал исполнение: его конфигурацию или базовую
/// сборку с пометкой. Чистое правило, отделённое от диска, — ради того, чтобы «что увидит наладчик»
/// можно было проверить тестом, а не живым прогоном по сетевой шаре.
///
/// <see cref="Execution"/> — ЧТО ПРОСИЛИ (пусто = исходная сборка), <see cref="Configured"/> — есть
/// ли в этой версии конфигурация под это. Отдаваемый файл в обоих случаях ЕСТЬ: когда конфигурации
/// нет, выдаётся базовая сборка. Это прямое решение Ильи: «лучше выдать на 2 насоса и наладчик сам
/// сконфигурирует, чем будет искать на 3 насоса, не найдёт и загрузит старую» — устаревшая прошивка
/// опаснее несконфигурированной.
///
/// Единственное условие, на котором это решение держится: подмена обязана быть ПОДПИСАНА. Поэтому
/// <see cref="Warning"/> не «может быть показано», а должно показываться везде, где показана сама
/// версия, — в выдаче, на карточке, перед заливкой («в поисковой выдаче… просто предупреждать, что
/// конфигурации нет, будет залит исходный файл, при необходимости удалите или создайте
/// конфигурацию»). Заливку это НЕ блокирует.</summary>
public sealed record FirmwareExecutionPick(string Execution, bool Configured)
{
    /// <summary>Исходная (базовая) сборка — ничего не выбирали, отдаём то же, что и всегда.</summary>
    public static readonly FirmwareExecutionPick Base = new("", true);

    /// <summary>Выбрано исполнение, а отдаётся базовая сборка. Ровно тот случай, который обязан быть
    /// подписан.</summary>
    public bool FallsBackToBase => Execution.Length > 0 && !Configured;

    /// <summary>Текст предупреждения для карточки — или пусто, когда предупреждать не о чем.
    ///
    /// Оба выхода названы намеренно: создать конфигурацию ИЛИ убрать исполнение из справочника
    /// подтипа. Это и есть два настоящих варианта, и «не успели собрать» от «так больше не
    /// выпускаем» отличает только человек (docs/firmware-executions.md, § «Как исполнение
    /// исчезает»).</summary>
    public string Warning => FallsBackToBase
        ? $"Под исполнение «{Execution}» в этой версии конфигурации нет — будет залита исходная " +
          "сборка. Настройте по месту, либо создайте конфигурацию, либо уберите исполнение у этого шкафа."
        : "";

    /// <summary>Короткая подпись к выбору в карточке.</summary>
    public string ShortNote => FallsBackToBase ? "конфигурации нет — исходная сборка" : "";

    /// <summary>Решение по просьбе и по тому, что в версии реально собрано.
    ///
    /// <paramref name="builtExecutions"/> — результат VersionLayout.BuiltExecutions: имена
    /// исполнений, под которые в этой версии ЛЕЖИТ файл. Сравнение имён — без учёта регистра, в
    /// .NET: на диске «3 Насоса» и «3 насоса» это одна и та же папка (Windows), и делать вид, что
    /// это разные исполнения, значило бы обещать конфигурацию, которой не существует отдельно.</summary>
    public static FirmwareExecutionPick Pick(string? requested, IEnumerable<string>? builtExecutions)
    {
        var execution = Domain.FwExecution.Normalize(requested);
        if (execution.Length == 0) return Base;
        var built = builtExecutions is null
            ? Array.Empty<string>()
            : builtExecutions.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        var configured = built.Any(x => string.Equals(
            Domain.FwExecution.Normalize(x), execution, StringComparison.OrdinalIgnoreCase));
        return new FirmwareExecutionPick(execution, configured);
    }

    /// <summary>Что предлагать в выборе исполнения на карточке: «исходная» плюс весь СПРАВОЧНИК
    /// подтипа — не только собранное.
    ///
    /// Показывать лишь собранное было бы ошибкой того же рода, что и выводить справочник из папок:
    /// наладчик, не увидевший своего исполнения, решит, что ищет не там, и пойдёт в прошлую версию —
    /// то есть получит устаревшую прошивку, ровно то, чего избегаем. Он обязан увидеть своё
    /// исполнение И честную пометку, что конфигурации под него в этой версии нет.</summary>
    public static IReadOnlyList<string> Choices(IEnumerable<string>? catalog)
    {
        var result = new List<string> { "" };
        if (catalog is null) return result;
        foreach (var raw in catalog)
        {
            var name = Domain.FwExecution.Normalize(raw);
            if (name.Length == 0) continue;
            if (result.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(name);
        }
        return result;
    }
}
