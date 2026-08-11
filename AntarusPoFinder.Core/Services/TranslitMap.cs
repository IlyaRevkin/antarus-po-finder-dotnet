using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AntarusPoFinder.Core.Services;

/// <summary>Справочник «имя папки на диске → как оно называется в адресе на хостинге».
///
/// Автоматический перевод (<see cref="Transliteration.Auto"/>) справляется сам почти везде, но не
/// везде: у части имён есть принятое в компании английское написание («Инструкция» — «manual», а не
/// «Instrukciya»), а у аббревиатур бывает своё («ПЖ» = «PZH» правильно, но кто-то захочет «fire»).
/// Поэтому решение такое: перевод считается автоматически, а человек может переопределить его для
/// любого имени — и переопределение уезжает на все машины обычной синхронизацией настроек.
///
/// <b>Почему справочник обязан быть общим.</b> Ссылку под QR печатает одна машина, а файл на хостинг
/// кладёт другая — если у них разойдётся перевод хоть одного сегмента пути, наклейка будет вести в
/// пустоту. Отсюда и хранение одной строкой настройки (<c>translit_map</c>), которая ходит вместе с
/// остальным общим конфигом, а не локальным файлом у каждого.
///
/// Ключи сравниваются без учёта регистра; хранится справочник как обычный JSON-объект, чтобы его
/// можно было прочитать и починить руками, не запуская программу.</summary>
public sealed class TranslitMap
{
    private readonly Dictionary<string, string> _overrides;

    public static TranslitMap Empty { get; } = new(new Dictionary<string, string>());

    private TranslitMap(Dictionary<string, string> overrides) => _overrides = overrides;

    public IReadOnlyDictionary<string, string> Overrides => _overrides;
    public int Count => _overrides.Count;

    /// <summary>Разбор сохранённого справочника. Битый JSON — пустой справочник, а не исключение:
    /// испорченная настройка не должна валить выкладку целиком, автоперевод отработает и без неё.</summary>
    public static TranslitMap Parse(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return new TranslitMap(result);

        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (raw is not null)
                foreach (var (key, value) in raw)
                {
                    var name = (key ?? "").Trim();
                    var latin = (value ?? "").Trim();
                    if (name.Length == 0 || latin.Length == 0) continue;
                    result[name] = latin;
                }
        }
        catch (Exception) { /* см. доку: справочник необязателен */ }

        return new TranslitMap(result);
    }

    public static TranslitMap FromPairs(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs)
        {
            var name = (key ?? "").Trim();
            var latin = (value ?? "").Trim();
            if (name.Length == 0 || latin.Length == 0) continue;
            result[name] = latin;
        }
        return new TranslitMap(result);
    }

    /// <summary>Сохраняемый вид. Ключи отсортированы, чтобы одна и та же таблица давала один и тот же
    /// текст: иначе каждое открытие настроек выглядело бы для синхронизации как правка.</summary>
    public string ToJson() =>
        JsonSerializer.Serialize(
            _overrides.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                      .ToDictionary(p => p.Key, p => p.Value));

    /// <summary>Как называется в адресе одно имя папки или файла.</summary>
    public string Segment(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return "";
        if (_overrides.TryGetValue(trimmed, out var manual)) return manual;

        // У файла переводится только само имя — расширение остаётся расширением. Иначе «.pdf» после
        // общей обработки остался бы «.pdf», а вот «.РУС» превратилось бы в мусор, и файл перестал бы
        // открываться по ссылке тем приложением, которым должен.
        var ext = System.IO.Path.GetExtension(trimmed);
        if (ext.Length > 1 && !Transliteration.HasCyrillic(ext))
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(trimmed);
            if (_overrides.TryGetValue(stem, out var manualStem)) return manualStem + ext.ToLowerInvariant();
            return Transliteration.Auto(stem) + ext.ToLowerInvariant();
        }

        return Transliteration.Auto(trimmed);
    }

    /// <summary>Путь относительно корня диска — в путь внутри бакета и в хвост веб-ссылки. Посегментно:
    /// разделители остаются разделителями, иначе из пути получилась бы одна длинная строка.</summary>
    public string Path(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return "";
        var segments = relativePath
            .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Segment)
            .Where(s => s.Length > 0);
        return string.Join("/", segments);
    }

    /// <summary>Справочник с добавленным (или изменённым) переопределением. Пустая латиница — это
    /// «вернуть автоперевод», то есть убрать строку.</summary>
    public TranslitMap With(string name, string? latin)
    {
        var copy = new Dictionary<string, string>(_overrides, StringComparer.OrdinalIgnoreCase);
        var key = (name ?? "").Trim();
        if (key.Length == 0) return this;

        var value = (latin ?? "").Trim();
        if (value.Length == 0) copy.Remove(key);
        else copy[key] = value;
        return new TranslitMap(copy);
    }
}
