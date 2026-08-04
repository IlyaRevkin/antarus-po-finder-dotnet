using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AntarusPoFinder.Core.Loader;

/// <summary>Как наладчик подключён к ПЛК прямо сейчас.</summary>
public enum PlcConnectionMode
{
    /// <summary>Выбор не сделан — что настроено в самом Segnetics Loader, то и остаётся. Это
    /// поведение по умолчанию: пока наладчик не выбрал режим у нас, мы в чужие настройки не лезем.</summary>
    Unspecified,
    Usb,
    Ethernet,
}

/// <summary>Что получилось из попытки перенести выбор в настройки Loader.</summary>
/// <param name="Applied">Хоть что-то реально записано.</param>
/// <param name="ChangedKeys">Какие именно поля настроек Loader изменены — для лога окна загрузки.</param>
/// <param name="Message">Человекочитаемое объяснение, если применить не удалось (или null).</param>
public sealed record LoaderConnectionApplyResult(bool Applied, IReadOnlyList<string> ChangedKeys, string? Message)
{
    public static LoaderConnectionApplyResult Skipped(string? message = null) =>
        new(false, Array.Empty<string>(), message);
}

/// <summary>Перенос выбора «USB / Ethernet + адрес + сетевой адаптер» в настройки Segnetics Loader.
///
/// ПОЧЕМУ ТАК, А НЕ ПАРАМЕТРОМ ЗАПРОСА: Automation-процесс параметры подключения в запросе не
/// принимает вообще — «Параметры подключения, учётные данные и путь к прошивке не входят в запрос.
/// Automation читает их из настроек Loader: %LOCALAPPDATA%\SegneticsLoader\settings.json»
/// (docs/loader/LOADER_AUTOMATION_API.md). Значит единственный способ дать наладчику выбирать
/// подключение из карточки — записать выбор в этот файл перед запуском операции.
///
/// ГЛАВНОЕ ПРАВИЛО: файл ЧУЖОЙ, его схема нами не документирована и может меняться от версии к
/// версии Loader. Поэтому мы **никогда не придумываем структуру**: правим только те поля, которые в
/// файле УЖЕ есть (имя ищется без учёта регистра среди известных синонимов, на любой глубине), и
/// только если тип значения подходит. Нет файла или нет подходящего поля — ничего не пишем и честно
/// говорим об этом наладчику: «выберите режим один раз в самом Loader». Так мы физически не можем
/// сломать его настройки, даже если схема окажется другой.
///
/// Перед первой правкой рядом кладётся резервная копия <c>settings.json.antarus-backup</c>.</summary>
public static class LoaderConnectionSettings
{
    /// <summary>Синонимы имени поля режима подключения. Порядок важен только для сообщений.</summary>
    private static readonly string[] ModeKeys =
        { "connectionMode", "connection_mode", "mode", "plcConnectionMode", "connectionType" };

    private static readonly string[] IpKeys =
        { "ipAddress", "ip", "host", "address", "plcIp", "plcAddress", "targetIp" };

    private static readonly string[] AdapterKeys =
        { "networkAdapter", "adapter", "networkInterface", "interface", "nic", "adapterName" };

    public static string DefaultSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SegneticsLoader", "settings.json");

    public static PlcConnectionMode ParseMode(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "usb" => PlcConnectionMode.Usb,
        "ethernet" or "eth" or "lan" => PlcConnectionMode.Ethernet,
        _ => PlcConnectionMode.Unspecified,
    };

    public static string ModeToConfig(PlcConnectionMode mode) => mode switch
    {
        PlcConnectionMode.Usb => "usb",
        PlcConnectionMode.Ethernet => "ethernet",
        _ => "",
    };

    public static string ModeCaption(PlcConnectionMode mode) => mode switch
    {
        PlcConnectionMode.Usb => "USB",
        PlcConnectionMode.Ethernet => "Ethernet",
        _ => "Как в Loader",
    };

    /// <summary>Записывает выбор в настройки Loader. Ничего не бросает: любая неудача — это
    /// Applied=false с объяснением, потому что из-за настроек подключения нельзя рушить заливку.</summary>
    public static LoaderConnectionApplyResult Apply(PlcConnectionMode mode, string? ip, string? adapter,
        string? settingsPath = null)
    {
        if (mode == PlcConnectionMode.Unspecified && string.IsNullOrWhiteSpace(ip) && string.IsNullOrWhiteSpace(adapter))
            return LoaderConnectionApplyResult.Skipped();

        var path = string.IsNullOrWhiteSpace(settingsPath) ? DefaultSettingsPath : settingsPath!;
        if (!File.Exists(path))
            return LoaderConnectionApplyResult.Skipped(
                "Настройки Segnetics Loader ещё не созданы — выберите режим подключения один раз в самом Loader, " +
                "дальше программа будет переключать его сама.");

        JsonNode? root;
        string original;
        try
        {
            original = File.ReadAllText(path);
            root = JsonNode.Parse(original, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (Exception ex)
        {
            return LoaderConnectionApplyResult.Skipped($"Не удалось прочитать настройки Loader: {ex.Message}");
        }

        if (root is not JsonObject obj)
            return LoaderConnectionApplyResult.Skipped("Настройки Loader в неожиданном формате — не трогаем их.");

        var changed = new List<string>();
        if (mode != PlcConnectionMode.Unspecified)
            TrySetString(obj, ModeKeys, ModeToConfig(mode), changed);
        if (!string.IsNullOrWhiteSpace(ip) && mode != PlcConnectionMode.Usb)
            TrySetString(obj, IpKeys, ip!.Trim(), changed);
        if (!string.IsNullOrWhiteSpace(adapter))
            TrySetString(obj, AdapterKeys, adapter!.Trim(), changed);

        if (changed.Count == 0)
            return LoaderConnectionApplyResult.Skipped(
                "В настройках Segnetics Loader не нашлось полей подключения — выберите режим в самом Loader, " +
                "программа его не переопределяет.");

        try
        {
            var backup = path + ".antarus-backup";
            if (!File.Exists(backup)) File.Copy(path, backup);
            File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            return LoaderConnectionApplyResult.Skipped($"Не удалось сохранить настройки Loader: {ex.Message}");
        }

        return new LoaderConnectionApplyResult(true, changed, null);
    }

    /// <summary>Ставит значение первому найденному полю из списка синонимов — на любой глубине, но
    /// только если поле уже существует И сейчас хранит строку (иначе мы не понимаем, что это за поле,
    /// и правильнее не трогать). Возвращает true, если что-то изменилось.</summary>
    private static bool TrySetString(JsonObject obj, string[] names, string value, List<string> changed)
    {
        foreach (var (parent, key) in FindStringProperties(obj))
        {
            if (!names.Any(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase))) continue;
            if (string.Equals(parent[key]?.GetValue<string>(), value, StringComparison.OrdinalIgnoreCase)) return false;
            parent[key] = value;
            changed.Add(key);
            return true;
        }
        return false;
    }

    /// <summary>Все строковые свойства объекта и вложенных объектов, сверху вниз: настройки Loader
    /// могут быть как плоскими, так и с секцией «connection», а гадать мы не хотим.</summary>
    private static IEnumerable<(JsonObject Parent, string Key)> FindStringProperties(JsonObject obj)
    {
        foreach (var pair in obj)
            if (pair.Value is JsonValue v && v.TryGetValue<string>(out _))
                yield return (obj, pair.Key);

        foreach (var pair in obj)
            if (pair.Value is JsonObject nested)
                foreach (var found in FindStringProperties(nested))
                    yield return found;
    }
}
