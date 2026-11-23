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

/// <summary>Сходятся ли выбор в программе и настройки самого Segnetics Loader.</summary>
public enum LoaderConnectionAgreement
{
    /// <summary>Loader настроен ровно так, как выбрано у нас.</summary>
    Agreed,

    /// <summary>Loader настроен ИНАЧЕ: выбор до него не доехал, и заливка пойдёт по настройкам Loader.</summary>
    Conflict,

    /// <summary>Настройки Loader прочитать не удалось — сказать, сходятся они или нет, нельзя.</summary>
    Unknown,
}

/// <summary>ОДИН ОТВЕТ НА ВОПРОС «ЧЕРЕЗ ЧТО МЫ СЕЙЧАС ГРУЗИМ».
///
/// <b>Откуда взялось.</b> Тикет наладчика от 19.11.2026: «Статуст подключение противоречит тексту
/// причины, что делает некоторые догадки о том, что переключатель "через что загружаем" выполняет
/// декоративную функцию». И он был прав дважды:
/// <list type="bullet">
/// <item>в карточке стоял USB, а окно подготовки ПЛК в ту же секунду писало «Loader сейчас настроен
///       на Ethernet» — потому что выбор переносился в настройки Loader ПОЗЖЕ, уже внутри заливки,
///       а вопрос про подготовку задавался раньше и читал ещё старый файл;</item>
/// <item>если в settings.json Loader'а не нашлось знакомого поля, выбор не переносился вообще
///       никогда, и переключатель действительно оставался украшением — молча.</item>
/// </list>
///
/// Поэтому ответ считается ОДНИМ местом и из ДВУХ источников сразу: что выбрал человек
/// (<paramref name="Chosen"/>, настройка loader_connection_mode) и что реально стоит в настройках
/// Loader (<paramref name="InLoader"/>, <see cref="LoaderConnectionSettings.ReadMode"/>). Любой
/// текст на экране берётся отсюда, а не собирается на месте — два места дали бы два ответа, с чего
/// всё и началось.
///
/// <b>Решающий голос у Loader.</b> Заливку выполняет он и читает свои собственные настройки
/// (docs/loader/LOADER_AUTOMATION_API.md), поэтому <see cref="Effective"/> — это то, что стоит у
/// НЕГО; наш выбор становится действительностью только после успешного переноса.</summary>
public sealed record LoaderConnectionState(
    PlcConnectionMode Chosen,
    PlcConnectionMode InLoader,
    LoaderConnectionAgreement Agreement)
{
    /// <summary>Как ПЛК будет заливаться на самом деле. Если настройки Loader прочитаны — по ним;
    /// иначе остаётся наш выбор, но тогда <see cref="Agreement"/> честно говорит «неизвестно».</summary>
    public PlcConnectionMode Effective =>
        InLoader != PlcConnectionMode.Unspecified ? InLoader : Chosen;

    /// <summary>Выбор человека подтверждённо действует. «Как в Loader» — тоже да: там выбора и не
    /// делали, спорить не с чем.</summary>
    public bool Honored => Agreement == LoaderConnectionAgreement.Agreed;

    /// <summary>Противоречие словами — или null, если его нет. Это же текст предупреждения.</summary>
    public string? Conflict => Agreement != LoaderConnectionAgreement.Conflict
        ? null
        : $"В программе выбрано подключение {LoaderConnectionSettings.ModeCaption(Chosen)}, а сам "
          + $"Segnetics Loader настроен на {LoaderConnectionSettings.ModeCaption(InLoader)} — заливка "
          + $"пойдёт по {LoaderConnectionSettings.ModeCaption(InLoader)}. Перенести выбор в его "
          + "настройки не удалось: выберите режим один раз в самом Loader.";

    /// <summary>Строка состояния — ровно одна и непротиворечивая. Её и показываем всюду, где
    /// раньше писали «Подключение: USB», не сверяясь с действительностью.</summary>
    public string Summary => Agreement switch
    {
        LoaderConnectionAgreement.Conflict => Conflict!,
        LoaderConnectionAgreement.Agreed when Chosen == PlcConnectionMode.Unspecified =>
            $"Подключение: как настроено в самом Loader — сейчас {LoaderConnectionSettings.ModeCaption(InLoader)}.",
        LoaderConnectionAgreement.Agreed =>
            $"Подключение: {LoaderConnectionSettings.ModeCaption(Chosen)} — так же настроен и сам Loader.",
        _ when Chosen == PlcConnectionMode.Unspecified =>
            "Подключение: как настроено в самом Loader. Прочитать его настройки не удалось, поэтому "
            + "какое именно — программа не знает.",
        _ =>
            $"Подключение: выбрано {LoaderConnectionSettings.ModeCaption(Chosen)}. Прочитать настройки "
            + "самого Loader не удалось — если в нём стоит другое, заливка пойдёт по нему.",
    };
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

    /// <summary>Свести выбор человека и настройки Loader в одно состояние. Чистая функция — чтобы
    /// проверялась тестами без чужого файла на диске; читающая обёртка рядом
    /// (<see cref="StateFor"/>).</summary>
    public static LoaderConnectionState Describe(PlcConnectionMode chosen, PlcConnectionMode inLoader)
    {
        var agreement = inLoader == PlcConnectionMode.Unspecified
            // Настройки Loader не прочитались. Для «как в Loader» это тоже «неизвестно», а не
            // «согласны»: сказать, через что пойдёт заливка, мы по-прежнему не можем.
            ? LoaderConnectionAgreement.Unknown
            : chosen == PlcConnectionMode.Unspecified || chosen == inLoader
                ? LoaderConnectionAgreement.Agreed
                : LoaderConnectionAgreement.Conflict;
        return new LoaderConnectionState(chosen, inLoader, agreement);
    }

    /// <summary>То же, но настройки Loader читаются с диска прямо сейчас. Звать ПОСЛЕ
    /// <see cref="Apply"/>: до переноса файл отвечает за прошлую заливку, и именно на этом
    /// расхождении выросла жалоба про «декоративный переключатель».</summary>
    public static LoaderConnectionState StateFor(PlcConnectionMode chosen, string? settingsPath = null) =>
        Describe(chosen, ReadMode(settingsPath));

    /// <summary>Перенести выбор в настройки Loader и сразу сказать, что из этого вышло: состояние
    /// считается ПО ФАКТУ, перечитыванием файла, а не по тому, что мы собирались записать.
    ///
    /// Одна точка входа на всех: окно загрузки, страница настроек и карточка версии обязаны
    /// говорить одно и то же, иначе переключатель снова начнёт жить своей жизнью.</summary>
    public static (LoaderConnectionApplyResult Apply, LoaderConnectionState State) ApplyAndDescribe(
        PlcConnectionMode mode, string? ip, string? adapter, string? settingsPath = null)
    {
        var applied = Apply(mode, ip, adapter, settingsPath);
        return (applied, StateFor(mode, settingsPath));
    }

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
    /// <summary>ЧТО НА САМОМ ДЕЛЕ СТОИТ В НАСТРОЙКАХ LOADER.
    ///
    /// Зачем читать, если мы же их и пишем. Потому что пишем мы их УГАДЫВАЯ: подменяется строковое
    /// свойство с одним из знакомых имён (см. ModeKeys), и если в этой версии Loader режим хранится
    /// иначе — числом, галочкой, другим именем, — подмена молча не происходит. Снаружи это выглядит
    /// так: в программе выбран USB, а Loader работает по Ethernet.
    ///
    /// Так и поймали 15.10.2026 на машине коллеги: заливка с подготовкой упала с «Обновление ядра
    /// поддерживается только при USB-подключении», хотя в карточке стоял USB.
    ///
    /// <see cref="PlcConnectionMode.Unspecified"/> — «прочитать не удалось»: файла нет, он не
    /// разобрался или знакомого ключа в нём не оказалось. Это НЕ «значит Ethernet» — выводов из
    /// этого делать нельзя, можно только предупредить человека.</summary>
    public static PlcConnectionMode ReadMode(string? settingsPath = null)
    {
        var path = string.IsNullOrWhiteSpace(settingsPath) ? DefaultSettingsPath : settingsPath!;
        try
        {
            if (!File.Exists(path)) return PlcConnectionMode.Unspecified;
            var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (root is not JsonObject obj) return PlcConnectionMode.Unspecified;

            foreach (var (parent, key) in FindStringProperties(obj))
            {
                if (!ModeKeys.Any(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase))) continue;
                return ParseMode(parent[key]?.GetValue<string>());
            }
        }
        catch { /* чужой файл — любые неожиданности здесь означают «не знаем», а не ошибку */ }
        return PlcConnectionMode.Unspecified;
    }

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
