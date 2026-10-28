using System.Text.RegularExpressions;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Назначение строки таблицы параметров — «для наладки» и «для сервиса»: хранение,
/// отбор, угадка по содержимому и перенос разметки при перезаливке.
///
/// Зачем деление вообще — см. ParamRowPurpose в Domain/ParamTable.cs и docs/param-purpose-plan.md.
/// Здесь только правила, и живут они в ядре, а не в коде окна, по той же причине, что и остальная
/// работа с таблицей: проверяются тестами, а не глазами на живом прогоне.</summary>
public static class ParamPurposes
{
    public const string AnyLabel = "Все строки";
    public const string CommissioningLabel = "Для наладки";
    public const string ServiceLabel = "Для сервиса";

    /// <summary>Подпись назначения для показа в ячейке и в списке.</summary>
    public static string Label(string? purpose)
    {
        var set = Parse(purpose);
        if (set.Count == 0) return "";
        var parts = new List<string>();
        if (set.Contains(ParamRowPurpose.Commissioning)) parts.Add("наладка");
        if (set.Contains(ParamRowPurpose.Service)) parts.Add("сервис");
        return string.Join(" · ", parts);
    }

    /// <summary>Разобрать хранимую строку в набор токенов. Неизвестные токены ОТБРАСЫВАЮТСЯ: поле
    /// ездит в общем конфиге, и назначение, заведённое будущей версией, у этой превратилось бы в
    /// пункт отбора без подписи. Потерять его не страшно — строка остаётся «не размеченной», а
    /// значит видна при любом отборе.</summary>
    public static HashSet<string> Parse(string? raw)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in (raw ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part.Trim().ToLowerInvariant();
            if (token == ParamRowPurpose.Commissioning || token == ParamRowPurpose.Service)
                result.Add(token);
        }
        return result;
    }

    /// <summary>Привести к хранимому виду. Порядок токенов ЗАКРЕПЛЁН (сначала наладка): иначе
    /// «service,commissioning» и «commissioning,service» — один и тот же набор — разошлись бы в
    /// разборе изменений как правка строки.</summary>
    public static string Format(IEnumerable<string>? purposes)
    {
        var set = Parse(string.Join(",", purposes ?? Enumerable.Empty<string>()));
        var parts = new List<string>();
        if (set.Contains(ParamRowPurpose.Commissioning)) parts.Add(ParamRowPurpose.Commissioning);
        if (set.Contains(ParamRowPurpose.Service)) parts.Add(ParamRowPurpose.Service);
        return string.Join(",", parts);
    }

    public static string Normalize(string? raw) => Format(Parse(raw));

    public static bool Has(string? raw, string purpose) => Parse(raw).Contains(purpose);

    /// <summary>Пункты отбора. Список, а не три кнопки: рядом стоит отбор по аппарату таким же
    /// списком, и два разных по виду отбора в одном ряду читались бы как разные по смыслу.</summary>
    public static List<string> Options() => new() { AnyLabel, CommissioningLabel, ServiceLabel };

    /// <summary>Токен по подписи из <see cref="Options"/>; пусто — «все строки».</summary>
    public static string TokenOf(string? option) => (option ?? "").Trim() switch
    {
        CommissioningLabel => ParamRowPurpose.Commissioning,
        ServiceLabel => ParamRowPurpose.Service,
        _ => "",
    };

    /// <summary>Отбор по назначению. <b>Неразмеченная строка видна всегда</b> — ровно как строка без
    /// пометки аппарата в ParamTableEditing.FilterByApplicability. Спрятать неразмеченное значило бы
    /// показать наладчику на объекте три строки из ста (а разметка у всех накопленных таблиц
    /// пустая), и он выставил бы частотник по ним одним. Пояснения (Kind=Note) не прячутся никогда:
    /// это указания вроде «в ПЛК выставить частоту 55 Гц», и без них подгруппа теряет смысл.</summary>
    public static List<ParamTableRow> Filter(IEnumerable<ParamTableRow>? rows, string? option)
    {
        var all = (rows ?? Enumerable.Empty<ParamTableRow>()).ToList();
        var wanted = TokenOf(option);
        if (wanted.Length == 0) return all;

        return all.Where(r =>
        {
            if (r.Kind == ParamRowKind.Note) return true;
            var own = Parse(r.Purpose);
            return own.Count == 0 || own.Contains(wanted);
        }).ToList();
    }

    /// <summary>Сколько строк отбора ещё не размечено — окно показывает это числом под таблицей.
    /// Молчать об этом нельзя: человек, выбравший «для сервиса» и увидевший всю таблицу, решит, что
    /// отбор сломан, хотя на деле таблицу просто не разметили.</summary>
    public static int Unmarked(IEnumerable<ParamTableRow>? rows) =>
        (rows ?? Enumerable.Empty<ParamTableRow>())
            .Count(r => r.Kind != ParamRowKind.Note && Parse(r.Purpose).Count == 0);

    // ── Угадка по содержимому ────────────────────────────────────────────────────────────────

    /// <summary>Раздел и название, по которым строка опознаётся как СЕРВИСНАЯ: всё, что снимается с
    /// шильдика конкретного двигателя и меняется вместе с ним.</summary>
    private static readonly Regex ServiceRe = new(
        @"двигател|мотор|шильд|номинальн|ном\.\s*ток|тепловая защита|перегруз|cos|автонастрой|автотюн|идентификац|полюс|скольжен|обмотк|сопротивлен",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>То, без чего шкаф не поедет: каналы команд, связь, пуск, разгон, входы-выходы.</summary>
    private static readonly Regex CommissioningRe = new(
        @"modbus|связ|адрес|скорост[ьи] обмена|протокол|канал|команд|пуск|стоп|разгон|торможен|частот|задани|вход|выход|di\d|do\d|ai\d|ao\d|реверс|сброс до заводск|источник",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Что это за строка по её же содержимому. Угадка намеренно осторожная: не узнала —
    /// возвращает пусто, и строка остаётся «не размеченной», то есть видной при любом отборе.
    /// Угадать неверно опаснее, чем не угадать: спрятанная от наладчика строка — это невыставленный
    /// параметр на объекте.
    ///
    /// Значение «снимается по месту» (ParamValueState.OnSite) — сервисный признак сам по себе: по
    /// месту снимают как раз с шильдика установленного двигателя.</summary>
    public static string Guess(ParamTableRow? row)
    {
        if (row is null || row.Kind == ParamRowKind.Note) return "";

        var text = string.Join(" ", new[] { row.GroupName, row.Title, row.Description }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        var purposes = new List<string>();
        if (ServiceRe.IsMatch(text) || ParamValueState.Normalize(row.ValueState) == ParamValueState.OnSite)
            purposes.Add(ParamRowPurpose.Service);
        if (CommissioningRe.IsMatch(text)) purposes.Add(ParamRowPurpose.Commissioning);
        return Format(purposes);
    }

    /// <summary>Разметить по угадке. <b>Уже проставленное не трогается</b> (если не сказано
    /// <paramref name="overwrite"/>): кнопку жмут на полуразмеченной таблице, и переписать ручную
    /// разметку регулярками значит обесценить работу, которую человек уже сделал.
    ///
    /// Правит переданные строки на месте и возвращает, скольким назначение проставлено, — окно
    /// говорит это человеку: «разметилось 40 из 120» сразу показывает, что остальное за ним.</summary>
    public static int GuessAll(IEnumerable<ParamTableRow>? rows, bool overwrite = false)
    {
        var marked = 0;
        foreach (var row in rows ?? Enumerable.Empty<ParamTableRow>())
        {
            if (row is null) continue;
            if (!overwrite && Parse(row.Purpose).Count > 0) continue;
            var guess = Guess(row);
            if (guess.Length == 0) continue;
            row.Purpose = guess;
            marked++;
        }
        return marked;
    }

    /// <summary>Перенести разметку из ПРЕЖНЕЙ редакции в заново разобранные строки.
    ///
    /// Без этого первая же перезаливка txt молча стирала бы всю ручную разметку: разбор текста о
    /// назначениях не знает и знать не может — в исходном файле их нет. Сопоставление идёт по тому
    /// же ключу, что и разбор изменений (ParamTableDiff.KeyOf: код + применимость + условие), то
    /// есть «та же строка» здесь значит ровно то же, что и в Summary ревизии.
    ///
    /// Своя разметка у новой строки сильнее перенесённой: перезаливка могла идти уже из правленого
    /// файла. Возвращает число перенесённых.</summary>
    public static int CarryOver(IEnumerable<ParamTableRow>? previous, IEnumerable<ParamTableRow>? fresh)
    {
        var old = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in previous ?? Enumerable.Empty<ParamTableRow>())
        {
            if (row is null) continue;
            var purpose = Normalize(row.Purpose);
            if (purpose.Length == 0) continue;
            old.TryAdd(ParamTableDiff.KeyOf(row), purpose);
        }
        if (old.Count == 0) return 0;

        var carried = 0;
        foreach (var row in fresh ?? Enumerable.Empty<ParamTableRow>())
        {
            if (row is null || Parse(row.Purpose).Count > 0) continue;
            if (!old.TryGetValue(ParamTableDiff.KeyOf(row), out var purpose)) continue;
            row.Purpose = purpose;
            carried++;
        }
        return carried;
    }
}
