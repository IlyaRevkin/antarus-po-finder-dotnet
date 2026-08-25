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

        /// <summary>Поменялось значение в СВОЁМ столбце документа — том, что человек завёл сам
        /// («Диапазон», «Кем проверено»). Отдельно от Edited: свои столбцы заводят как раз под то,
        /// что нужно выставлять и сверять, и потерять их правку в общей куче «поправлены описания»
        /// значило бы завести столбец и не увидеть, что в нём меняли.</summary>
        ExtraChanged,

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

    /// <summary>Свой столбец, появившийся или исчезнувший между ревизиями. Ключ, а не заголовок:
    /// заголовок переименовывают, а сравниваем мы содержимое строк (см. ParamTableColumn.Key).
    /// Заголовком для показа служит сам ключ — им столбец и назывался, когда его завели.</summary>
    public record ColumnChange(string Key, bool Added, int Filled);

    public record Result(List<Change> Changes, List<ColumnChange>? Columns = null)
    {
        public int Added => Changes.Count(c => c.Kind == ChangeKind.Added);
        public int Removed => Changes.Count(c => c.Kind == ChangeKind.Removed);
        public int ValueChanged => Changes.Count(c => c.Kind == ChangeKind.ValueChanged);
        public int ExtraChanged => Changes.Count(c => c.Kind == ChangeKind.ExtraChanged);
        public int Edited => Changes.Count(c => c.Kind == ChangeKind.Edited);

        /// <summary>Свои столбцы, заведённые и убранные в этой редакции. Пустой список — не null:
        /// показу и тексту разбора не должно приходиться про это помнить.</summary>
        public List<ColumnChange> ColumnChanges { get; } = Columns ?? new();

        public bool Any => Changes.Count > 0 || ColumnChanges.Count > 0;

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

            // Свой столбец — раньше обычной правки: строку с изменившимся «Диапазоном» ищут
            // глазами так же, как строку с изменившимся значением, а не в списке «поправлены
            // описания». Оба сразу быть не могут — у строки одна пометка, и значение старше.
            if (!SameExtra(was, row))
            {
                changes.Add(new Change(ChangeKind.ExtraChanged, key, was, row));
                continue;
            }

            if (!SameRest(was, row))
                changes.Add(new Change(ChangeKind.Edited, key, was, row));
        }

        foreach (var (key, row) in old)
            if (!fresh.ContainsKey(key))
                changes.Add(new Change(ChangeKind.Removed, key, row, null));

        return new Result(changes, CompareColumns(before, after));
    }

    /// <summary>Свои столбцы, появившиеся и исчезнувшие между ревизиями.
    ///
    /// ⚠️ Считается по СОДЕРЖИМОМУ строк, а не по списку столбцов документа, и это осознанно.
    /// Список столбцов у документа один на все ревизии — он не снимок, и «какие столбцы были у
    /// позапрошлой редакции» из него не узнать. Зато содержимое лежит в самих строках, то есть в
    /// снимке: столбец, в котором в этой редакции впервые что-то заполнили, ею и заведён — с точки
    /// зрения человека, читающего документ, это одно и то же. Заведённый и оставленный пустым
    /// столбец в разбор не попадает: в документе от него пока ничего не изменилось.</summary>
    private static List<ColumnChange> CompareColumns(IEnumerable<ParamTableRow>? before, IEnumerable<ParamTableRow>? after)
    {
        var was = ColumnFill(before);
        var now = ColumnFill(after);
        var result = new List<ColumnChange>();

        foreach (var (key, filled) in now)
            if (!was.ContainsKey(key))
                result.Add(new ColumnChange(key, Added: true, Filled: filled));

        foreach (var (key, filled) in was)
            if (!now.ContainsKey(key))
                result.Add(new ColumnChange(key, Added: false, Filled: filled));

        return result;
    }

    /// <summary>Ключ своего столбца → в скольких строках он заполнен.</summary>
    private static Dictionary<string, int> ColumnFill(IEnumerable<ParamTableRow>? rows)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows ?? Enumerable.Empty<ParamTableRow>())
            foreach (var key in ParamRowExtra.Parse(row.Extra).Keys)
                result[key] = result.TryGetValue(key, out var count) ? count + 1 : 1;
        return result;
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

    /// <summary>Свои столбцы строки. Сравниваются РАЗОБРАННЫЕ наборы, а не текст ячейки: тот же
    /// набор, записанный в другом порядке ключей или с лишним пробелом, — это не правка документа,
    /// а разный вывод сериализатора, и показывать его человеку как изменение нельзя.</summary>
    private static bool SameExtra(ParamTableRow a, ParamTableRow b)
    {
        var one = ParamRowExtra.Parse(a.Extra);
        var two = ParamRowExtra.Parse(b.Extra);
        if (one.Count != two.Count) return false;
        foreach (var (key, value) in one)
            if (!two.TryGetValue(key, out var other) || Norm(value) != Norm(other))
                return false;
        return true;
    }

    private static bool SameRest(ParamTableRow a, ParamTableRow b) =>
        Norm(a.Title) == Norm(b.Title)
        && Norm(a.Factory) == Norm(b.Factory)
        && Norm(a.Unit) == Norm(b.Unit)
        && Norm(a.Description) == Norm(b.Description)
        && Norm(a.GroupName) == Norm(b.GroupName);

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

        var extraChanges = diff.Changes.Where(c => c.Kind == ChangeKind.ExtraChanged).ToList();
        if (extraChanges.Count > 0)
        {
            var text = new StringBuilder("Изменены свои столбцы (").Append(extraChanges.Count).Append("): ");
            text.Append(string.Join("; ", extraChanges.Take(maxNamed).Select(c =>
                $"{Label(c)} — {ExtraDiff(c.Before!, c.After!)}")));
            if (extraChanges.Count > maxNamed) text.Append("; и ещё ").Append(extraChanges.Count - maxNamed);
            parts.Add(text.Append('.').ToString());
        }

        AppendList(parts, diff.Changes.Where(c => c.Kind == ChangeKind.Added).ToList(), "Добавлено", maxNamed);
        AppendList(parts, diff.Changes.Where(c => c.Kind == ChangeKind.Removed).ToList(), "Убрано", maxNamed);

        var edited = diff.Changes.Count(c => c.Kind == ChangeKind.Edited);
        if (edited > 0) parts.Add($"Поправлены описания: {edited}.");

        // Столбцы — В КОНЦЕ и отдельной фразой: это правка САМОГО ДОКУМЕНТА, а не его строк, и
        // мешать её со списком кодов значило бы спрятать. Первым делом всё равно ищут значения.
        foreach (var column in diff.ColumnChanges.Where(c => c.Added))
            parts.Add($"Заведён свой столбец «{column.Key}» (заполнен в строках: {column.Filled}).");
        foreach (var column in diff.ColumnChanges.Where(c => !c.Added))
            parts.Add($"Убран свой столбец «{column.Key}» (был заполнен в строках: {column.Filled}).");

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

    /// <summary>Что именно поменялось в своих столбцах строки — по столбцу за раз, со стрелкой:
    /// «Диапазон: 0…50 → 0…60». Строкой без разбивки по столбцам разбор был бы бесполезен ровно
    /// там, где столбцов несколько, а это единственный случай, ради которого их и заводят.</summary>
    private static string ExtraDiff(ParamTableRow before, ParamTableRow after)
    {
        var was = ParamRowExtra.Parse(before.Extra);
        var now = ParamRowExtra.Parse(after.Extra);
        var keys = was.Keys.Concat(now.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

        var parts = new List<string>();
        foreach (var key in keys)
        {
            var left = was.TryGetValue(key, out var a) ? a : "";
            var right = now.TryGetValue(key, out var b) ? b : "";
            if (Norm(left) == Norm(right)) continue;
            parts.Add($"{key}: {(left.Length == 0 ? "пусто" : left)} → {(right.Length == 0 ? "пусто" : right)}");
        }
        return parts.Count > 0 ? string.Join(", ", parts) : "без изменений";
    }

    private static string Show(ParamTableRow row) => ParamValueState.Normalize(row.ValueState) switch
    {
        ParamValueState.Ask => "?",
        ParamValueState.OnSite => "по месту",
        _ => row.Value.Length == 0 ? "пусто" : row.Value,
    };
}
