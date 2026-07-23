using System;
using System.Collections.Generic;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Единственное место, где теги превращаются в строку хранения и обратно.
///
/// Теги лежат в fw_versions.tags/param_files.tags ОДНОЙ строкой через пробел, и весь обмен между
/// машинами (config-синхронизация, переименование/удаление тега, автотеги при загрузке) работает
/// именно с этим форматом. Поэтому тег из нескольких слов — «шкаф управления пожарными насосами
/// Антарус ПЖ-ПП-2» — при первом же сохранении рассыпался на отдельные слова и переставал
/// существовать как тег: карточка показывала пять пузырей вместо одного, а поиск с галочкой «точное
/// совпадение» по названию шкафа целиком не мог найти ровно ту прошивку, которой этот тег поставили
/// (сравнивать было не с чем — фразы в базе уже не было).
///
/// Формат хранения не меняется (иначе пришлось бы трогать и обмен, и все старые базы): пробелы
/// ВНУТРИ одного тега кодируются неразрывным пробелом U+00A0, разделителем остаётся обычный пробел.
/// Старые базы читаются как были — там, где многословных тегов нет, кодирование ничего не меняет.</summary>
public static class TagString
{
    /// <summary>Чем заменяется пробел внутри одного тега. Неразрывный пробел выбран потому, что в
    /// живом тексте тегов его не набирают, а при показе он выглядит обычным пробелом — даже если
    /// строка тегов попадёт куда-то мимо Parse (лог, чужой инструмент), читается она нормально.</summary>
    public const char InnerSpace = ' ';

    /// <summary>Один тег в том виде, в котором он ложится в строку хранения: обрезанный по краям,
    /// со схлопнутыми и закодированными внутренними пробелами.</summary>
    public static string Encode(string tag)
    {
        var parts = (tag ?? "").Split(new[] { ' ', '\t', InnerSpace }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(InnerSpace, parts);
    }

    /// <summary>Обратно к человеческому виду — с обычными пробелами.</summary>
    public static string Decode(string tag) => (tag ?? "").Replace(InnerSpace, ' ').Trim();

    /// <summary>Строка хранения → список тегов (каждый с обычными пробелами внутри).</summary>
    public static List<string> Parse(string? stored) =>
        (stored ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(Decode)
            .Where(t => t.Length > 0)
            .ToList();

    /// <summary>Список тегов → строка хранения. Пустые отбрасываются, повторы (без учёта регистра)
    /// схлопываются — иначе один и тот же тег легко попадал дважды через автотеги.</summary>
    public static string Join(IEnumerable<string> tags)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();
        foreach (var raw in tags ?? Enumerable.Empty<string>())
        {
            var encoded = Encode(raw);
            if (encoded.Length == 0 || !seen.Add(encoded)) continue;
            parts.Add(encoded);
        }
        return string.Join(' ', parts);
    }

    /// <summary>Есть ли такой тег в строке хранения — сравнение целым тегом, не подстрокой.</summary>
    public static bool Contains(string? stored, string tag) =>
        Parse(stored).Any(t => string.Equals(t, (tag ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
}
