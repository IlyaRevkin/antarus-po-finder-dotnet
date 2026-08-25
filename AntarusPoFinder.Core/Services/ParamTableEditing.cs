using System.Text.RegularExpressions;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Всё, что происходит с таблицей параметров МЕЖДУ разбором текста и записью в базу:
/// предпросмотр импорта, приведение строк в порядок, сохранение новой ревизии и отбор по
/// применимости.
///
/// Живёт в ядре, а не в код-behind окна, по обычной причине: правил тут больше, чем кажется
/// (нормализация групп по кириллице, порядок показа, разбор изменений, кто вправе править), и
/// проверять их надо тестами, а не глазами на живом прогоне.</summary>
public static class ParamTableEditing
{
    /// <summary>Что показать в предпросмотре импорта. Текст отдаётся целиком намеренно: когда разбор
    /// промахнулся, первое, что делает человек, — смотрит на исходные строки рядом с таблицей.</summary>
    public record ImportPreview(string Text, string EncodingName, string SuggestedName,
        List<ParamTableRow> Rows, List<string> Warnings);

    /// <summary>Кому можно править таблицу. Наладчику — чтение и отбор: он работает по документу на
    /// объекте, и правка «по месту, чтобы сходилось» разошлась бы с тем, что заложил программист,
    /// молча и у всех сразу (документ ездит в общем конфиге).</summary>
    public static bool CanEdit(string? role) => role is "administrator" or "programmer";

    /// <summary>Разобрать выбранный файл, не записывая ничего. <paramref name="encodingChoice"/> —
    /// то, что человек выбрал в списке кодировок; null или «Определить сама» отдаёт решение
    /// TextFileEncoding.</summary>
    public static ImportPreview Preview(byte[] bytes, string fileName, string? encodingChoice = null)
    {
        var (text, encodingName) = TextFileEncoding.DecodeAs(bytes ?? Array.Empty<byte>(), encodingChoice);
        var parsed = ParamTextParser.Parse(text);
        var warnings = new List<string>(parsed.Warnings);
        // Считаем именно ПАРАМЕТРЫ, а не строки вообще: разбор прощает всё и любую непонятную строку
        // кладёт пояснением, поэтому «строк ноль» не бывает почти никогда, а вот «ни одного кода» —
        // это и есть «выбран не тот файл» или «не та кодировка».
        if (!parsed.Rows.Any(r => r.Kind == ParamRowKind.Param))
            warnings.Insert(0, "В файле не нашлось ни одного параметра — возможно, выбран не тот файл или не та кодировка.");
        return new ImportPreview(text, encodingName, SuggestName(fileName, parsed.Title), parsed.Rows, warnings);
    }

    private static readonly Regex InBracketsRe = new(@"\(([^()]+)\)", RegexOptions.Compiled);

    /// <summary>Как назвать документ по имени файла и заголовку внутри него.
    ///
    /// Правило одно и взято с живых файлов: у «ESQ-230 2025 - КПЧ(Задание Modbus).txt» назначение
    /// документа записано ровно в скобках — «Задание Modbus». Всё остальное в имени (модель, год,
    /// «КПЧ») повторяет то, что и так известно из иерархии, и в названии документа только мешает.
    /// Скобок нет — берём имя файла без расширения: угадывать дальше не по чему.</summary>
    public static string SuggestName(string? fileName, string? parsedTitle)
    {
        foreach (var candidate in new[] { fileName, parsedTitle })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var matches = InBracketsRe.Matches(candidate);
            if (matches.Count > 0)
            {
                var inner = matches[^1].Groups[1].Value.Trim();
                // «Задание Modbus» — назначение документа, а «(2)» у «файл (2).txt» — след того,
                // что файл когда-то скопировали рядом. Назвать документ цифрой нельзя.
                if (inner.Length > 0 && !inner.All(char.IsDigit)) return inner;
            }
        }

        var name = (fileName ?? "").Trim();
        if (name.Length > 0)
        {
            var dot = name.LastIndexOf('.');
            return dot > 0 ? name[..dot] : name;
        }

        return (parsedTitle ?? "").Trim();
    }

    /// <summary>Написание группы, принятое в справочнике. «Двигатель» и «двигатель» для SQLite —
    /// разные строки (COLLATE NOCASE сворачивает только латиницу), поэтому свести их к одному
    /// написанию обязан .NET, и обязательно ДО записи: иначе в таблице заведутся две одинаковые с
    /// виду группы, а порядок показа у них будет разный.</summary>
    public static string NormalizeGroup(IEnumerable<string> catalog, string? name)
    {
        var wanted = (name ?? "").Trim();
        if (wanted.Length == 0) return ParamGroupCatalog.Main;
        foreach (var known in catalog)
            if (string.Equals(known, wanted, StringComparison.OrdinalIgnoreCase))
                return known;
        return wanted;
    }

    /// <summary>Привести строки к тому виду, в котором их можно записывать: обрезать пробелы,
    /// свести группы к написанию справочника, пронумеровать по порядку и выбросить пустые.
    ///
    /// Пустая строка — не мусор из ниоткуда: таблицу правят в сетке, и «добавил строку, передумал»
    /// оставляет ровно её. Записать такую значит показать наладчику пустую строку без кода и
    /// названия, а разбору изменений — «добавлена строка ""».</summary>
    public static List<ParamTableRow> Tidy(IEnumerable<ParamTableRow>? rows, IEnumerable<string>? catalog = null)
    {
        var known = catalog?.ToList() ?? new List<string>();
        var result = new List<ParamTableRow>();
        var order = 0;

        foreach (var row in rows ?? Enumerable.Empty<ParamTableRow>())
        {
            if (row is null) continue;
            var tidy = row.Clone();
            tidy.Code = (tidy.Code ?? "").Trim();
            tidy.Title = (tidy.Title ?? "").Trim();
            tidy.Value = (tidy.Value ?? "").Trim();
            tidy.Factory = (tidy.Factory ?? "").Trim();
            tidy.Unit = (tidy.Unit ?? "").Trim();
            tidy.Description = (tidy.Description ?? "").Trim();
            tidy.Applicability = (tidy.Applicability ?? "").Trim();
            tidy.AppliesWhen = (tidy.AppliesWhen ?? "").Trim();
            tidy.GroupName = NormalizeGroup(known, tidy.GroupName);
            tidy.ValueState = ParamValueState.Normalize(tidy.ValueState);
            // Строка без кода параметром быть не может: код — это то, что человек ищет глазами и
            // вбивает в частотник. Осталось название — значит это пояснение.
            tidy.Kind = tidy.Code.Length == 0 ? ParamRowKind.Note : ParamRowKind.Param;
            if (tidy.Kind == ParamRowKind.Note && tidy.Title.Length == 0) continue;

            tidy.SortOrder = order++;
            result.Add(tidy);
        }

        return result;
    }

    /// <summary>Строки в ПОРЯДКЕ ПОКАЗА: группы по своему месту в справочнике, внутри группы — как
    /// в исходнике. Хранение об этом порядке не знает намеренно (см. Database.GetParamTableRows):
    /// переставленная в справочнике группа не должна переписывать уже сохранённые ревизии.</summary>
    public static List<ParamTableRow> Ordered(IEnumerable<ParamTableRow>? rows,
        IReadOnlyDictionary<string, int> groupOrder)
    {
        return (rows ?? Enumerable.Empty<ParamTableRow>())
            .OrderBy(r => ParamGroupCatalog.OrderOf(r.GroupName, groupOrder))
            // Вторым ключом — само название группы: у двух групп может совпасть место в справочнике
            // (порядок задают руками), и без него они перемешались бы построчно.
            .ThenBy(r => r.GroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.SortOrder)
            .ToList();
    }

    /// <summary>Пункты отбора по применимости для наладчика: «все» плюс каждый аппарат, упомянутый
    /// в документе.</summary>
    public const string AnyApplicability = "Все строки";

    public static List<string> Applicabilities(IEnumerable<ParamTableRow>? rows)
    {
        var result = new List<string> { AnyApplicability };
        foreach (var row in rows ?? Enumerable.Empty<ParamTableRow>())
        {
            var value = (row.Applicability ?? "").Trim();
            if (value.Length == 0) continue;
            if (!result.Any(r => string.Equals(r, value, StringComparison.OrdinalIgnoreCase)))
                result.Add(value);
        }
        return result;
    }

    /// <summary>Отбор по применимости. Строка БЕЗ пометки остаётся видна всегда: «годится всем» —
    /// это и есть большинство таблицы, спрятав его, мы показали бы наладчику три строки из ста и
    /// он выставил бы частотник по ним одним.</summary>
    public static List<ParamTableRow> FilterByApplicability(IEnumerable<ParamTableRow>? rows, string? applicability)
    {
        var wanted = (applicability ?? "").Trim();
        var all = (rows ?? Enumerable.Empty<ParamTableRow>()).ToList();
        if (wanted.Length == 0 || wanted == AnyApplicability) return all;

        return all.Where(r =>
        {
            var own = (r.Applicability ?? "").Trim();
            return own.Length == 0 || string.Equals(own, wanted, StringComparison.OrdinalIgnoreCase);
        }).ToList();
    }

    /// <summary>Записать НОВУЮ ревизию документа. «Что изменилось» считается здесь же сравнением с
    /// предыдущей ревизией и кладётся в Summary — человек пишет только «зачем»
    /// (<paramref name="reason"/>).
    ///
    /// Возвращает id ревизии и посчитанную разницу: окно показывает её сразу, не перечитывая базу.</summary>
    public static (int RevisionId, ParamTableDiff.Result Diff) SaveRevision(Database db, int tableId,
        IEnumerable<ParamTableRow>? rows, string reason, string author)
    {
        var catalog = db.GetParamGroups();
        var tidy = Tidy(rows, catalog);

        // Группа, которой в справочнике ещё нет, заводится вместе с ревизией. Иначе она осталась бы
        // только в строках: показ разложил бы её «в конец, но перед сбросом» и в списке групп её
        // никто бы не увидел.
        foreach (var group in tidy.Select(r => r.GroupName).Distinct(StringComparer.OrdinalIgnoreCase))
            db.AddParamGroup(group);

        var previous = db.GetParamTableRevisions(tableId).FirstOrDefault();
        var before = previous?.Id is null ? null : db.GetParamTableRows(previous.Id.Value);
        var diff = ParamTableDiff.Compare(before, tidy);

        var revision = new ParamTableRevision
        {
            TableId = tableId,
            Number = db.NextParamTableRevisionNumber(tableId),
            Reason = (reason ?? "").Trim(),
            Summary = previous is null ? $"Первая редакция: строк {tidy.Count}." : ParamTableDiff.Describe(diff),
            Author = author ?? "",
            Rows = tidy,
        };

        return (db.AddParamTableRevision(revision), diff);
    }

    /// <summary>Завести документ по разобранному файлу и сразу положить в него первую ревизию.
    /// Одним вызовом, потому что документ без единой ревизии — пустая строка в списке: показать в
    /// нём нечего, а завестись он успел бы и уехать к коллегам тоже.</summary>
    public static (int TableId, int RevisionId) CreateFromImport(Database db, ParamTable table,
        IEnumerable<ParamTableRow>? rows, string reason, string author)
    {
        var tableId = db.AddParamTable(table);
        var (revisionId, _) = SaveRevision(db, tableId, rows, reason, author);
        return (tableId, revisionId);
    }
}
