using System.Text.Json;
using System.Text.Json.Nodes;

namespace AntarusPoFinder.Core.Services;

/// <summary>Содержимое СВОИХ столбцов документа: «ключ столбца» → «значение», сложенное в одну
/// текстовую ячейку ParamTableRow.Extra.
///
/// JSON'ом в одном поле, а не столбцом в param_table_rows на каждый заведённый столбец, потому что
/// требование владельца звучало как «чтобы можно было добавлять столбцы», а ALTER TABLE на каждый
/// добавленный столбец упирает это в выпуск новой версии программы — то есть требование не
/// выполняет.
///
/// ⚠️ <b>Ключ — не заголовок, а ключ столбца</b> (ParamTableColumn.Key): заголовок переименовывают,
/// а строки ревизии неизменяемы, и переписать их задним числом нельзя. Сравнение ключей всюду с
/// игнором регистра силами .NET — SQLite здесь ни при чём, но заголовки кириллические, а ключ
/// когда-то был заголовком, и «Диапазон»/«ДИАПАЗОН» с двух машин обязаны сойтись.</summary>
public static class ParamRowExtra
{
    public static IReadOnlyDictionary<string, string> Empty { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Разобрать ячейку. Мусор вместо JSON — пустой набор, а не исключение: строка могла
    /// приехать с чужой машины или из чиненной руками базы, и ронять на ней ПОКАЗ ДОКУМЕНТА нельзя.
    /// Числа и логические значения читаются как текст: столбцы у нас текстовые, а снаружи их мог
    /// записать кто угодно.</summary>
    public static Dictionary<string, string> Parse(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj) return result;
            foreach (var (key, value) in obj)
            {
                if (string.IsNullOrWhiteSpace(key) || value is null) continue;
                var text = value is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : value.ToJsonString().Trim('"');
                if (text.Length == 0) continue;
                result[key.Trim()] = text;
            }
        }
        catch (JsonException)
        {
            return result;
        }

        return result;
    }

    /// <summary>Сложить обратно. Пустые значения не пишутся вовсе, ключи идут по порядку — оба
    /// правила ради разбора изменений: иначе «стёрли значение» и «переставили столбец» выглядели бы
    /// как правка строки (см. ParamTableDiff, где сравниваются уже разобранные наборы, а не текст).
    /// Пустой набор даёт ПУСТУЮ СТРОКУ, а не «{}»: так у строки без своих столбцов ячейка остаётся
    /// такой же, какой её кладёт разбор txt.</summary>
    public static string Format(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0) return "";

        var kept = values
            .Where(p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value))
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (kept.Count == 0) return "";

        var obj = new JsonObject();
        foreach (var (key, value) in kept) obj[key.Trim()] = value.Trim();
        return obj.ToJsonString();
    }

    /// <summary>Значение одного столбца или пустая строка. Отдельным методом, чтобы показу не
    /// приходилось помнить про игнор регистра.</summary>
    public static string Get(string? json, string? key) =>
        string.IsNullOrWhiteSpace(key) ? "" : Parse(json).TryGetValue(key.Trim(), out var v) ? v : "";

    /// <summary>Записать одно значение в ячейку, не трогая остальные столбцы.</summary>
    public static string With(string? json, string? key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key)) return json ?? "";
        var values = Parse(json);
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0) values.Remove(key.Trim());
        else values[key.Trim()] = trimmed;
        return Format(values);
    }
}
