using System.Text;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что изменилось между двумя ревизиями таблицы параметров — СЧИТАЕТ ПРОГРАММА.
///
/// Человека об этом не спрашивают намеренно. Просьба «перечисли, что поменял» даёт «поправил
/// параметры» и половину забытых строк; при этом ровно эта строчка — единственное, ради чего
/// открывают предыдущую редакцию. Человек пишет только «зачем» (ParamTableRevision.Reason), а
/// «что» выводится сравнением снимков.
///
/// <b>Ключ строки — код + применимость + условие, БЕЗ группы.</b> Один и тот же код честно
/// встречается в файле дважды с разной применимостью («P0-10 для всех» и «P0-10 (55) для 55 Гц») —
/// без этих двух полей в ключе они схлопнулись бы в одну строку, и половина изменений исчезла бы из
/// разбора. Группа в ключ НЕ входит: перенос строки в другую группу — это изменение строки, а не
/// «удалили одну и завели другую».</summary>
public static class ParamTableDiff
{
    public enum ChangeKind
    {
        /// <summary>Строки не было — появилась.</summary>
        Added,

        /// <summary>Строка была — исчезла.</summary>
        Removed,

        /// <summary>Поменялось ЗНАЧЕНИЕ. Показывается отдельно от всего прочего: значение — это то,
        /// что человек реально вобьёт в частотник, а название и описание он даже не прочтёт.</summary>
        ValueChanged,

        /// <summary>Поменялось что-то, кроме значения: название, описание, заводское, единица,
        /// группа.</summary>
        Edited,
    }

    /// <summary>Одно изменение. <paramref name="Before"/> у добавленной строки и
    /// <paramref name="After"/> у убранной — null.</summary>
    public record Change(ChangeKind Kind, string Key, ParamTableRow? Before, ParamTableRow? After)
    {
        public string Code => (After ?? Before)?.Code ?? "";
        public string Title => (After ?? Before)?.Title ?? "";
    }

    public record Result(List<Change> Changes)
    {
        public int Added => Changes.Count(c => c.Kind == ChangeKind.Added);
        public int Removed => Changes.Count(c => c.Kind == ChangeKind.Removed);
        public int ValueChanged => Changes.Count(c => c.Kind == ChangeKind.ValueChanged);
        public int Edited => Changes.Count(c => c.Kind == ChangeKind.Edited);
        public bool Any => Changes.Count > 0;

        /// <summary>Ключи строк по виду изменения — по ним таблица подсвечивает строки. Словарём, а
        /// не поиском по списку на каждую строку: строк в таблице сотни, изменений единицы.</summary>
        public Dictionary<string, ChangeKind> ByKey { get; } =
            Changes.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First().Kind, StringComparer.Ordinal);

        /// <summary>Что стало с ЭТОЙ строкой, или null, если ничего. Ровно то, что нужно показу:
        /// таблица идёт сверху вниз и про каждую строку спрашивает один раз. Ключ строки при этом
        /// наружу не выставляется — он служебный, и знать его правила показу незачем.</summary>
        public ChangeKind? KindOf(ParamTableRow? row) =>
            row is not null && ByKey.TryGetValue(KeyOf(row), out var kind) ? kind : null;
    }

    /// <summary>Ключ строки для сравнения. Регистр и лишние пробелы свёрнуты в .NET — <b>не через
    /// СУБД</b>: у SQLite COLLATE NOCASE сворачивает только латиницу, а применимость и условие здесь
    /// кириллические («Только для ПЧ №1»), и «ПЧ» с «пч» оказались бы разными строками (см. CLAUDE.md
    /// и Database.FileKey).
    ///
    /// У пояснения (Kind=Note) кода нет, и ключом ему служит собственный текст: два разных пояснения
    /// в одной подгруппе иначе считались бы одной и той же строкой.</summary>
    internal static string KeyOf(ParamTableRow row)
    {
        var head = row.Kind == ParamRowKind.Note
            ? "note:" + Norm(row.Title)
            : "code:" + Norm(row.Code);
        return string.Join(KeySeparator, head, Norm(row.Applicability), Norm(row.AppliesWhen));
    }

    /// <summary>Разделитель частей ключа. Служебный символ, а не пробел и не дефис: и
    /// применимость, и условие пишут человеческим текстом, в котором есть и то и другое, и с
    /// обычным разделителем пара («A B», «C») дала бы тот же ключ, что и пара («A», «B C»).</summary>
    private const char KeySeparator = '\u001F';

    private static string Norm(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }

    public static Result Compare(IEnumerable<ParamTableRow>? before, IEnumerable<ParamTableRow>? after)
    {
        var changes = new List<Change>();
        var old = Index(before);
        var fresh = Index(after);

        // Порядок обхода — по НОВОЙ ревизии: разбор изменений читают сверху вниз вместе с таблицей,
        // и добавленная строка должна стоять там же, где она стоит в самой таблице.
        foreach (var (key, row) in fresh)
        {
            if (!old.TryGetValue(key, out var was))
            {
                changes.Add(new Change(ChangeKind.Added, key, null, row));
                continue;
            }

            if (!SameValue(was, row))
            {
                changes.Add(new Change(ChangeKind.ValueChanged, key, was, row));
                continue;
            }

            if (!SameRest(was, row))
                changes.Add(new Change(ChangeKind.Edited, key, was, row));
        }

        foreach (var (key, row) in old)
            if (!fresh.ContainsKey(key))
                changes.Add(new Change(ChangeKind.Removed, key, row, null));

        return new Result(changes);
    }

    /// <summary>Строки по ключу, в порядке следования. Дубликат ключа (одна и та же строка записана
    /// в файле дважды) НЕ роняет разбор и не теряется молча — побеждает первая, как её и увидит
    /// человек сверху вниз.</summary>
    private static Dictionary<string, ParamTableRow> Index(IEnumerable<ParamTableRow>? rows)
    {
        var result = new Dictionary<string, ParamTableRow>(StringComparer.Ordinal);
        if (rows is null) return result;
        foreach (var row in rows)
        {
            var key = KeyOf(row);
            if (!result.ContainsKey(key)) result[key] = row;
        }
        return result;
    }

    /// <summary>Значение считается тем же, только когда совпало И само значение, И его состояние:
    /// «пусто, потому что снимается с шильдика» и «пусто, потому что надо уточнить по ПЛК» — разные
    /// вещи, и переход между ними обязан попасть в разбор.</summary>
    private static bool SameValue(ParamTableRow a, ParamTableRow b) =>
        Norm(a.Value) == Norm(b.Value) && ParamValueState.Normalize(a.ValueState) == ParamValueState.Normalize(b.ValueState);

    private static bool SameRest(ParamTableRow a, ParamTableRow b) =>
        Norm(a.Title) == Norm(b.Title)
        && Norm(a.Factory) == Norm(b.Factory)
        && Norm(a.Unit) == Norm(b.Unit)
        && Norm(a.Description) == Norm(b.Description)
        && Norm(a.GroupName) == Norm(b.GroupName)
        && (a.Extra ?? "") == (b.Extra ?? "");

    /// <summary>Разбор изменений человеческим текстом — он и ложится в
    /// ParamTableRevision.Summary, и показывается под таблицей.
    ///
    /// Изменения значений перечисляются поимённо и со стрелкой («P0-10: 50 → 55»): это ровно то, что
    /// наладчик ищет глазами. Добавленные и убранные — списком кодов. Длинные перечни обрезаются
    /// хвостом «и ещё N»: строка в списке ревизий должна оставаться строкой.</summary>
    public static string Describe(Result diff, int maxNamed = 8)
    {
        if (!diff.Any) return "Изменений нет.";

        var parts = new List<string>();

        var valueChanges = diff.Changes.Where(c => c.Kind == ChangeKind.ValueChanged).ToList();
        if (valueChanges.Count > 0)
        {
            var text = new StringBuilder("Изменены значения (").Append(valueChanges.Count).Append("): ");
            text.Append(string.Join("; ", valueChanges.Take(maxNamed).Select(c =>
                $"{Label(c)}: {Show(c.Before!)} → {Show(c.After!)}")));
            if (valueChanges.Count > maxNamed) text.Append("; и ещё ").Append(valueChanges.Count - maxNamed);
            parts.Add(text.Append('.').ToString());
        }

        AppendList(parts, diff.Changes.Where(c => c.Kind == ChangeKind.Added).ToList(), "Добавлено", maxNamed);
        AppendList(parts, diff.Changes.Where(c => c.Kind == ChangeKind.Removed).ToList(), "Убрано", maxNamed);

        var edited = diff.Changes.Count(c => c.Kind == ChangeKind.Edited);
        if (edited > 0) parts.Add($"Поправлены описания: {edited}.");

        return string.Join(" ", parts);
    }

    private static void AppendList(List<string> parts, List<Change> changes, string label, int maxNamed)
    {
        if (changes.Count == 0) return;
        var text = new StringBuilder(label).Append(" (").Append(changes.Count).Append("): ");
        text.Append(string.Join(", ", changes.Take(maxNamed).Select(Label)));
        if (changes.Count > maxNamed) text.Append(" и ещё ").Append(changes.Count - maxNamed);
        parts.Add(text.Append('.').ToString());
    }

    /// <summary>Чем назвать строку в тексте разбора. У параметра это код; у пояснения кода нет, и
    /// вместо него берётся начало текста — «строка без кода» человеку ничего не сказала бы.</summary>
    private static string Label(Change change)
    {
        var row = change.After ?? change.Before!;
        if (row.Kind == ParamRowKind.Note)
        {
            var text = row.Title.Trim();
            return "«" + (text.Length > 40 ? text[..40].TrimEnd() + "…" : text) + "»";
        }
        return row.Code;
    }

    private static string Show(ParamTableRow row) => ParamValueState.Normalize(row.ValueState) switch
    {
        ParamValueState.Ask => "?",
        ParamValueState.OnSite => "по месту",
        _ => row.Value.Length == 0 ? "пусто" : row.Value,
    };
}
