using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Правила своих столбцов документа: как их называть, в каком порядке показывать и какие
/// вообще показывать у выбранной ревизии.
///
/// В ядре, а не в код-behind окна, по той же причине, что и всё остальное про таблицы параметров:
/// правил тут больше, чем кажется (снятые столбцы с уцелевшим содержимым, повтор названия с чужим
/// регистром, перестановка), и проверять их надо тестами.</summary>
public static class ParamTableColumnEditing
{
    /// <summary>Ключ, который получит столбец с таким заголовком. Ключом служит сам заголовок —
    /// так две машины, независимо заведшие «Диапазон», получают ОДИН столбец (см.
    /// ParamTableColumn.Key).</summary>
    public static string KeyFor(string? title) => (title ?? "").Trim();

    /// <summary>Что не так с названием столбца, или null, если всё в порядке.
    /// <paramref name="exceptId"/> — столбец, который сейчас переименовывают: сам себе он не
    /// помеха.</summary>
    public static string? WhyTitleWontDo(IEnumerable<ParamTableColumn>? existing, string? title, int? exceptId = null)
    {
        var wanted = (title ?? "").Trim();
        if (wanted.Length == 0) return "У столбца должно быть название — оно и будет заголовком в таблице.";

        // Обязательные столбцы документа заведены в самой программе; свой столбец с тем же именем
        // читался бы как второй такой же, а разошлись бы они при первой правке.
        foreach (var builtin in BuiltInTitles)
            if (string.Equals(builtin, wanted, StringComparison.OrdinalIgnoreCase))
                return $"«{builtin}» — это встроенный столбец таблицы, свой такой же завести нельзя.";

        foreach (var column in existing ?? Enumerable.Empty<ParamTableColumn>())
        {
            if (column.Id is not null && column.Id == exceptId) continue;
            if (column.DeletedAt.Length > 0) continue;
            if (string.Equals(column.Title, wanted, StringComparison.OrdinalIgnoreCase))
                return $"Столбец «{column.Title}» в этом документе уже есть.";
        }

        return null;
    }

    /// <summary>Заголовки обязательных столбцов — ровно то, что стоит в шапке окна документа.</summary>
    public static IReadOnlyList<string> BuiltInTitles { get; } = new[]
    {
        "Группа", "Код", "Название", "Значение", "Заводское", "Ед.", "Описание", "Только для", "Когда нужно",
    };

    /// <summary>Переставить столбец на <paramref name="delta"/> позиций. Возвращает НОВЫЙ порядок
    /// списка; за край не выходит. Сама перестановка — чистая, запись в базу отдельным шагом
    /// (см. ApplyOrder): так её можно проверить тестом, не заводя базу.</summary>
    public static List<ParamTableColumn> Moved(IEnumerable<ParamTableColumn>? columns, int index, int delta)
    {
        var list = (columns ?? Enumerable.Empty<ParamTableColumn>()).ToList();
        var to = index + delta;
        if (index < 0 || index >= list.Count || to < 0 || to >= list.Count) return list;

        var moved = list[index];
        list.RemoveAt(index);
        list.Insert(to, moved);
        return list;
    }

    /// <summary>Записать порядок, в котором столбцы лежат в списке. Номера ставятся подряд от
    /// единицы, а не «поменять местами два соседних»: после нескольких перестановок и приёма чужого
    /// конфига номера всё равно расходятся, и переписать их целиком дешевле, чем чинить по одному.
    /// Столбец, у которого номер и так верный, не трогается — иначе каждая перестановка обновляла бы
    /// отметку правки у ВСЕХ столбцов и они бы вечно перебивали чужие переименования.</summary>
    public static void ApplyOrder(Database db, IEnumerable<ParamTableColumn>? columns)
    {
        var order = 1;
        foreach (var column in columns ?? Enumerable.Empty<ParamTableColumn>())
        {
            if (column.Id is not int id) continue;
            if (column.SortOrder != order)
            {
                db.UpdateParamTableColumn(id, column.Title, order);
                column.SortOrder = order;
            }
            order++;
        }
    }

    /// <summary>Какие свои столбцы показать у ЭТОЙ ревизии: живые столбцы документа плюс снятые, по
    /// которым в её строках осталось содержимое.
    ///
    /// Снятые тянутся намеренно. Столбец убирают «на будущее», а ревизия — снимок прошлого: спрячь
    /// её содержимое вместе со столбцом, и человек, открывший позапрошлую редакцию, увидит меньше,
    /// чем в ней было записано, и не узнает об этом. Заголовок у снятого берётся его собственный,
    /// поэтому тумбстоун и не удаляется.</summary>
    public static List<ParamTableColumn> Visible(IEnumerable<ParamTableColumn>? allIncludingDeleted,
        IEnumerable<ParamTableRow>? rows)
    {
        var all = (allIncludingDeleted ?? Enumerable.Empty<ParamTableColumn>()).ToList();
        var used = UsedKeys(rows);

        var result = all
            .Where(c => c.DeletedAt.Length == 0 || used.Contains(c.Key))
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Id ?? 0)
            .ToList();

        // Ключ, которого в списке столбцов нет вовсе: строки приехали с машины, где столбец
        // заводили, а сам столбец до нас ещё не доехал (секции конфига приезжают порознь). Показать
        // содержимое всё равно надо — заголовком послужит ключ, он и был когда-то заголовком.
        foreach (var key in used)
            if (!result.Any(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)))
                result.Add(new ParamTableColumn { Key = key, Title = key, SortOrder = int.MaxValue });

        return result;
    }

    /// <summary>Ключи своих столбцов, по которым в этих строках есть хоть одно непустое
    /// значение.</summary>
    public static HashSet<string> UsedKeys(IEnumerable<ParamTableRow>? rows)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows ?? Enumerable.Empty<ParamTableRow>())
            foreach (var key in ParamRowExtra.Parse(row.Extra).Keys)
                used.Add(key);
        return used;
    }
}
