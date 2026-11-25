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
/// <param name="TransferProblem">Почему выбор НЕ удалось перенести в settings.json самого Loader —
/// или null, если переносить было нечего либо перенос удался. Отдельно от
/// <paramref name="Agreement"/>, потому что это разные беды: «не прочитали чужой файл» и «прочитали,
/// но своего поля там нет, и выбор физически никуда не уехал». Второе и привело к повторной жалобе
/// 24.11.2026: в карточке стоял Ethernet, Loader грузил по USB, а программа уверенно писала про USB
/// так, будто это и был выбор человека.</param>
public sealed record LoaderConnectionState(
    PlcConnectionMode Chosen,
    PlcConnectionMode InLoader,
    LoaderConnectionAgreement Agreement,
    string? TransferProblem = null)
{
    /// <summary>Как ПЛК будет заливаться на самом деле. Если настройки Loader прочитаны — по ним;
    /// иначе остаётся наш выбор, но тогда <see cref="Agreement"/> честно говорит «неизвестно».</summary>
    public PlcConnectionMode Effective =>
        InLoader != PlcConnectionMode.Unspecified ? InLoader : Chosen;

    /// <summary>Выбор человека подтверждённо действует. «Как в Loader» — тоже да: там выбора и не
    /// делали, спорить не с чем.</summary>
    public bool Honored => Agreement == LoaderConnectionAgreement.Agreed && TransferProblem is null;

    /// <summary>Противоречие словами — или null, если его нет. Это же текст предупреждения.</summary>
    public string? Conflict => Agreement != LoaderConnectionAgreement.Conflict
        ? null
        : $"В программе выбрано подключение {LoaderConnectionSettings.ModeCaption(Chosen)}, а сам "
          + $"Segnetics Loader настроен на {LoaderConnectionSettings.ModeCaption(InLoader)} — заливка "
          + $"пойдёт по {LoaderConnectionSettings.ModeCaption(InLoader)}. Перенести выбор в его "
          + "настройки не удалось: выберите режим один раз в самом Loader.";

    /// <summary>ЧЕСТНЫЙ ТЕКСТ ПРО «ВЫБОР НИКУДА НЕ УЕХАЛ» — или null, если уехал.
    ///
    /// Случай 24.11.2026, машина NALADKA1: в карточке выбран Ethernet, в settings.json Loader
    /// знакомого поля нет, запись не состоялась, Loader пошёл по своему USB и вернул «адаптер не
    /// найден». Программа при этом показывала два разных ответа — «выбрано Ethernet» и «Loader
    /// настроен на USB», — и ни один не говорил главного: выбор не действует, и нажимать «повторить»
    /// бессмысленно, пока режим не переключён в самом Loader.</summary>
    public string? Trouble => Conflict ?? (TransferProblem is null or "" ? null
        : $"Подключение {LoaderConnectionSettings.ModeCaption(Chosen)} выбрано в программе, но "
          + $"перенести выбор в настройки самого Segnetics Loader не удалось: {TransferProblem} "
          + "Заливка пойдёт по тому, что настроено в самом Loader.");

    /// <summary>Строка состояния — ровно одна и непротиворечивая. Её и показываем всюду, где
    /// раньше писали «Подключение: USB», не сверяясь с действительностью.</summary>
    public string Summary => TransferProblem is { Length: > 0 } && Agreement != LoaderConnectionAgreement.Conflict
        ? Trouble!
        : Agreement switch
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
    /// <remarks>Список пополнен 24.11.2026: на машине наладчика (Loader 2.9.13) ни одно из прежних
    /// имён не нашлось, и выбор не переносился вообще. Лишние имена здесь безопасны — поле всё
    /// равно правится, только если в нём УЖЕ лежит строка, узнаваемая как режим подключения
    /// (см. <see cref="LooksLikeMode"/>).</remarks>
    private static readonly string[] ModeKeys =
    {
        "connectionMode", "connection_mode", "mode", "plcConnectionMode", "connectionType",
        "connection", "connectionKind", "connectionMethod", "connect", "connectionName",
        "linkMode", "link", "transport", "plcConnection", "plcLink", "interfaceMode",
    };

    private static readonly string[] IpKeys =
        { "ipAddress", "ip", "host", "address", "plcIp", "plcAddress", "targetIp" };

    private static readonly string[] AdapterKeys =
        { "networkAdapter", "adapter", "networkInterface", "interface", "nic", "adapterName" };

    public static string DefaultSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SegneticsLoader", "settings.json");

    /// <summary>Разбор значения режима. ПО ВХОЖДЕНИЮ, а не по точному совпадению: наши собственные
    /// настройки хранят ровно «usb»/«ethernet», а вот чужой settings.json пишет режим так, как ему
    /// удобно — «UsbRndis», «Ethernet_DHCP», «EthernetStatic». Прежнее точное сравнение на таких
    /// значениях отвечало «не знаю», то есть мы не понимали даже то, что успели прочитать.
    ///
    /// Сначала USB: «UsbRndis» — это USB, хотя RNDIS и является Ethernet-адаптером, и слово
    /// ethernet в значении тоже может оказаться.</summary>
    public static PlcConnectionMode ParseMode(string? raw)
    {
        var value = raw?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value)) return PlcConnectionMode.Unspecified;
        if (value.Contains("usb") || value.Contains("rndis")) return PlcConnectionMode.Usb;
        if (value.Contains("ethernet") || value.Contains("dhcp") || value is "eth" or "lan" or "tcp")
            return PlcConnectionMode.Ethernet;
        return PlcConnectionMode.Unspecified;
    }

    /// <summary>Похоже ли значение поля на режим подключения. Пустая строка — тоже «похоже»: поле
    /// есть, его просто ещё не заполнили. Это единственная защита от того, что расширенный список
    /// имён (<see cref="ModeKeys"/>) попадёт в чужое поле вроде <c>"connection": "192.168.1.10"</c>:
    /// не узнали значение — не трогаем.</summary>
    private static bool LooksLikeMode(string? raw) =>
        string.IsNullOrWhiteSpace(raw) || ParseMode(raw) != PlcConnectionMode.Unspecified;

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
        var state = StateFor(mode, settingsPath);

        // Неудачу переноса НЕЛЬЗЯ терять по дороге: именно она и есть причина, по которой заливка
        // сейчас пойдёт не тем способом, который человек выбрал. Раньше сообщение уходило только
        // строкой в журнал, а состояние об этом не знало — и все экраны продолжали говорить
        // «выбрано Ethernet», как будто выбор действует.
        if (!applied.Applied && mode != PlcConnectionMode.Unspecified
            && applied.Message is { Length: > 0 } problem
            && state.Agreement != LoaderConnectionAgreement.Agreed)
            state = state with { TransferProblem = problem };

        return (applied, state);
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

        // РЕЖИМ — ОТДЕЛЬНО ОТ ОСТАЛЬНОГО. Адрес и адаптер — уточнения, а режим это и есть «через
        // что грузим»: не нашли его поля — перенос ПРОВАЛЕН, сколько бы ни записалось остального.
        // Прежний код мерил успех одним счётчиком изменений, и из-за этого путал две совершенно
        // разные вещи: «поля нет» и «там уже стоит ровно то, что нужно» (второе возвращало
        // Applied=false с текстом «не нашлось полей» — то есть программа пугала расхождением,
        // которого не было).
        var modeOutcome = mode == PlcConnectionMode.Unspecified
            ? FieldOutcome.AlreadySet
            : SetMode(obj, mode, changed);

        if (modeOutcome == FieldOutcome.NotFound)
            return LoaderConnectionApplyResult.Skipped(
                "в settings.json самого Loader нет поля режима подключения, которое программа умеет " +
                "править" + FieldsTail(obj) + " — выберите режим один раз в самом Loader, программа " +
                "его не переопределяет.");

        if (!string.IsNullOrWhiteSpace(ip) && mode != PlcConnectionMode.Usb)
            TrySetString(obj, IpKeys, ip!.Trim(), changed);
        if (!string.IsNullOrWhiteSpace(adapter))
            TrySetString(obj, AdapterKeys, adapter!.Trim(), changed);

        // Писать нечего — всё уже стоит как надо. Это УСПЕХ: выбор действует.
        if (changed.Count == 0)
            return new LoaderConnectionApplyResult(mode != PlcConnectionMode.Unspecified,
                Array.Empty<string>(), null);

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
                // Поле с подходящим именем, но непонятным значением — это НЕ ответ «не знаю»: имена
                // мы берём широко («connection», «link»), и попасть можно во что угодно. Такое поле
                // пропускаем и ищем дальше; раньше первое же совпадение имени обрывало поиск.
                var found = ParseMode(parent[key]?.GetValue<string>());
                if (found != PlcConnectionMode.Unspecified) return found;
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
            var old = parent[key]?.GetValue<string>();
            if (string.Equals(old, value, StringComparison.OrdinalIgnoreCase)) return false;
            parent[key] = value;
            changed.Add(Describe(key, old, value));
            return true;
        }
        return false;
    }

    /// <summary>Что стало с полем настроек Loader.</summary>
    private enum FieldOutcome
    {
        /// <summary>Поля нет (или его значение не похоже на режим) — трогать нечего.</summary>
        NotFound,

        /// <summary>Поле есть, и там уже ровно то, что нужно. Писать не надо, выбор действует.</summary>
        AlreadySet,

        /// <summary>Поле переписано.</summary>
        Changed,
    }

    /// <summary>Переписать поле режима подключения. Отдельно от <see cref="TrySetString"/>, потому
    /// что здесь другое правило поиска: поле должно не просто называться знакомо, но и СОДЕРЖАТЬ
    /// узнаваемый режим. Иначе расширенный список имён («connection», «link») рано или поздно
    /// попал бы в чужое поле.</summary>
    private static FieldOutcome SetMode(JsonObject obj, PlcConnectionMode mode, List<string> changed)
    {
        foreach (var (parent, key) in FindStringProperties(obj))
        {
            if (!ModeKeys.Any(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase))) continue;
            var old = parent[key]?.GetValue<string>();
            if (!LooksLikeMode(old)) continue;
            if (ParseMode(old) == mode) return FieldOutcome.AlreadySet;

            var value = ModeToConfig(mode);
            parent[key] = value;
            changed.Add(Describe(key, old, value));
            return FieldOutcome.Changed;
        }
        return FieldOutcome.NotFound;
    }

    /// <summary>«connectionMode: usb → ethernet». Старое значение в журнале нужно: если Loader
    /// окажется разборчив к написанию («UsbRndis» вместо «usb»), по журналу заливки это будет
    /// видно сразу, а не ещё через один круг переписки.</summary>
    private static string Describe(string key, string? old, string value) =>
        string.IsNullOrWhiteSpace(old) ? $"{key}: → {value}" : $"{key}: {old} → {value}";

    /// <summary>ИМЕНА полей чужого файла — для сообщения и журнала заливки. Только имена, без
    /// значений: в settings.json лежат и SSH-учётные данные, а знать нам надо ровно одно — как
    /// в этой версии Loader называется поле режима. Пока этого не видно, перенос выбора остаётся
    /// угадыванием (24.11.2026: ни одно знакомое имя не подошло).</summary>
    private static string FieldsTail(JsonObject obj)
    {
        var names = FindStringProperties(obj)
            .Select(p => p.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToList();
        return names.Count == 0 ? "" : $" (строковые поля файла: {string.Join(", ", names)})";
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
