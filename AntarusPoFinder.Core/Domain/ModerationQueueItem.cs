using System;
using System.Collections.Generic;
using System.Linq;

namespace AntarusPoFinder.Core.Domain;

/// <summary>Одна строка очереди модерации — ОДНА ПРОШИВКА целиком, со всеми своими записями под
/// разные подтипы шкафов (как они опознаются — см. Core/Services/ModerationQueue).
///
/// Зачем вообще отдельный тип, а не просто список записей: решение модерации принимается по
/// прошивке, а не по записи. Выпустить одну запись из пяти — это наполовину выпущенная прошивка,
/// которая хуже невыпущенной: в поиске она уже видна, а в очереди висят четыре хвоста, и следующий
/// человек не понимает, что с ними делать. Поэтому в строке лежит вся группа (<see cref="Ids"/>), а
/// в таблице видно, скольких подтипов она касается (<see cref="SubtypesDisplay"/>) — чтобы человек
/// ЗНАЛ, что одним нажатием выпускает несколько записей, а не догадывался об этом потом.</summary>
public sealed class ModerationQueueItem
{
    public ModerationQueueItem(IReadOnlyList<FwVersionRecord> records)
    {
        if (records.Count == 0) throw new ArgumentException("Группа модерации не может быть пустой", nameof(records));
        Records = records;
        // Главная запись — оригинал (copy_of пуст), а не копия: её подтип и контроллер человек и
        // считает «тем самым шкафом», для которого прошивка делалась. При равенстве — младший id,
        // чтобы выбор не зависел от порядка строк.
        Primary = records
            .OrderBy(r => string.IsNullOrEmpty(r.CopyOf) ? 0 : 1)
            .ThenBy(r => r.Id ?? int.MaxValue)
            .First();
        Ids = records.Where(r => r.Id is int).Select(r => r.Id!.Value).OrderBy(x => x).ToList();
        SubtypeNames = records
            .Select(r => r.SubtypeName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public FwVersionRecord Primary { get; }

    /// <summary>Все записи этой прошивки, стоящие в очереди.</summary>
    public IReadOnlyList<FwVersionRecord> Records { get; }

    /// <summary>id всех записей группы — то, что должно быть выпущено ОДНИМ действием.</summary>
    public IReadOnlyList<int> Ids { get; }

    /// <summary>Подтипы шкафов, которых касается прошивка, без повторов. Регистр при сравнении
    /// игнорируется по-человечески (CurrentCultureIgnoreCase): по-русски «ПИ» и «пи» — один и тот
    /// же подтип, а Ordinal посчитал бы их разными и нарисовал «+1 подтип» на пустом месте.</summary>
    public IReadOnlyList<string> SubtypeNames { get; }

    public int RecordCount => Records.Count;

    /// <summary>Касается ли прошивка больше чем одного подтипа — по этому признаку строка и
    /// объясняет человеку, что выпуск заденет несколько записей.</summary>
    public bool IsMulti => Records.Count > 1;

    /// <summary>Подтипы в таблице: до трёх — перечислением («НГР, ПЖ, КНС»), дальше первые два и
    /// «и ещё N подтипов». Обрывать совсем в «+5 подтипов» нельзя: человеку нужен якорь, по
    /// которому он узнаёт свою прошивку, а голое число такого якоря не даёт.</summary>
    public string SubtypesDisplay
    {
        get
        {
            if (SubtypeNames.Count == 0) return "—";
            if (SubtypeNames.Count <= 3) return string.Join(", ", SubtypeNames);
            var rest = SubtypeNames.Count - 2;
            return string.Join(", ", SubtypeNames.Take(2)) + $" и ещё {rest} {Plural(rest)}";
        }
    }

    /// <summary>Полный список подтипов — во всплывающую подсказку строки, где обрезать нечего.</summary>
    public string SubtypesFull => SubtypeNames.Count > 0 ? string.Join(", ", SubtypeNames) : "—";

    /// <summary>Русское склонение после числа: 1 подтип, 2 подтипа, 5 подтипов. Отдельным методом,
    /// потому что то же самое нужно и в тексте подтверждения выпуска.</summary>
    public static string Plural(int n)
    {
        var tens = n % 100;
        if (tens is >= 11 and <= 14) return "подтипов";
        return (n % 10) switch
        {
            1 => "подтип",
            2 or 3 or 4 => "подтипа",
            _ => "подтипов",
        };
    }
}
