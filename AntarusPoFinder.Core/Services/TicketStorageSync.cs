using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Двусторонняя синхронизация тикетов через хранилище на хостинге (S3).
///
/// <b>Зачем отдельный канал, когда тикеты уже синхронизируются.</b> Между машинами в конторе они
/// ездят событийным журналом по сетевому диску (TicketSyncService в приложении), и это работает —
/// но ровно внутри офисной сети. Тот, кто эти тикеты чинит, до сетевого диска не достаёт: до сих
/// пор ему отдавали РАЗОВЫЙ архив («Тикеты» → «Выгрузить…»), то есть снимок на минуту выгрузки.
/// Обратной дороги у снимка нет вовсе — закрытый тикет оставался закрытым только у чинящего, а в
/// конторе он продолжал висеть открытым. Дословная просьба владельца: «сделай синхру тикетов через
/// хранилище, чтобы ты их мог закрывать и они соответственно с хранилища подтягивались, а то сложно
/// так отслеживать что открыто что нет».
///
/// <b>Почему не общим конфигом.</b> Обмен конфигом (Database.ConfigExchange) — это ЦЕЛЫЙ снимок,
/// который одна машина перезаписывает целиком. Тикеты же заводят все роли со всех машин в любой
/// момент, и снимок с машины А неизбежно не содержит тикета, который до неё ещё не доехал (ровно
/// поэтому у тикетов с самого начала свой журнал, см. Ticket). Вдобавок конфиг лежит на том же
/// офисном сетевом диске — то есть даже идеально слитый он не решал бы ту беду, из-за которой
/// задачу и поставили: снаружи конторы его не видно.
///
/// <b>Раскладка в бакете — объект НА ТИКЕТ, а не журнал событий.</b>
/// <code>
/// tickets/state/&lt;id тикета&gt;.json     ← живые темы от людей
/// tickets/crashes/&lt;id тикета&gt;.json   ← автоотчёты о сбоях (см. TicketAutoReports)
/// tickets/tickets_&lt;дата&gt;_&lt;хвост&gt;.zip ← прежняя разовая выгрузка, этим кодом не трогается
/// </code>
/// Журнал событий пришлось бы проигрывать целиком, чтобы ответить на вопрос «что сейчас открыто», и
/// закрытие тикета снаружи означало бы «сочинить файл события с правильным именем». Объект на тикет
/// — это и есть ответ на вопрос владельца: содержимое папки И ЕСТЬ список тикетов, а закрыть тикет
/// значит положить туда же его json со <c>status: closed</c> и более поздним <c>updatedAt</c>.
/// Растёт такая раскладка по числу тикетов, а не по числу действий над ними.
///
/// <b>Слияние — по тикету, а не по файлу целиком.</b> У каждого тикета свой объект, поэтому правки
/// РАЗНЫХ тикетов с двух машин не встречаются вовсе. Один и тот же тикет разрешается по
/// <c>updatedAt</c> (позже — побеждает), тем же правилом, что и в базе
/// (Database.ApplyTicketStatusIfNewer), и тем же, что у журнала на сетевом диске. Если в бакете
/// оказалось СТАРШЕЕ состояние (две машины записали почти одновременно, и вторым лёг тот, у кого
/// время раньше), это чинится само: победитель на следующем проходе видит, что в хранилище лежит не
/// его состояние, и дописывает своё — см. <see cref="RunAsync"/>.
///
/// ⚠️ <b>Время — местное, без часового пояса</b> («2026-09-11T14:03:55.123»), как оно лежит в базе с
/// первого дня, и сравнивается строкой посимвольно. Перевод на UTC здесь означал бы, что старые
/// записи и новые сравниваются по разным шкалам. Тот, кто правит объект в хранилище руками, обязан
/// поставить <c>updatedAt</c> ПОЗЖЕ текущего — иначе правку сочтут устаревшей и не применят.
///
/// ⚠️ <b>Вложения (скриншоты) этим каналом НЕ ездят.</b> Они лежат файлами на сетевом диске
/// (TicketSyncService.AttachmentsDir), и здесь возится только сам тикет. Так и сказано человеку на
/// странице «Тикеты»: чтобы скриншот увидел тот, кто вне сети, есть «Выгрузить…» с архивом.
///
/// ⚠️ <b>Удаления нет и не предполагается.</b> Тикет в этой программе удалить нельзя ни с какой
/// стороны (в Database.Tickets нет ни одного DELETE), а автоотчёт о сбое удалять прямо запрещено —
/// для администратора это единственный след аварии. Терминальное состояние — «Закрыт», и именно оно
/// ездит. Поэтому ни надгробий, ни колонки <c>deleted_at</c> здесь нет: их нечем было бы ставить, а
/// пустой механизм удаления — это дыра, через которую тикет теряется молча. Пропавший из бакета
/// объект (стёрли руками через страницу «Хранилище») считается не удалением, а недостачей: он
/// выкладывается заново с ближайшей синхронизацией.</summary>
public static class TicketStorageSync
{
    /// <summary>Корневая папка тикетов в бакете. Та же, куда кладётся разовая выгрузка архивом
    /// (TicketExportService.StorageFolder) — это одно и то же хозяйство, разделять его на две
    /// верхние папки незачем.</summary>
    public const string RootFolder = TicketExportService.StorageFolder;

    /// <summary>Живые темы от людей.</summary>
    public const string StateFolder = RootFolder + "/state";

    /// <summary>Автоотчёты о сбоях — ОТДЕЛЬНОЙ папкой, а не вперемешку.
    ///
    /// Возить их надо: стектрейс это самое полезное, что вообще попадает к тому, кто чинит, и
    /// по-другому он его не увидит. Но их бывает разом семь из двадцати восьми, они машинные, и
    /// вопрос владельца — «что открыто, что нет» — они забивают. В самой программе это уже решено
    /// ровно так же: автоотчёты в списке спрятаны за галку (TicketAutoReports), а не выброшены.
    /// Здесь та же мысль средствами хранилища: список того, что ждёт человека, читается одной
    /// папкой, аварии лежат соседней. Правила синхронизации у обеих папок одинаковые — закрыть
    /// автоотчёт можно точно так же.</summary>
    public const string CrashFolder = RootFolder + "/crashes";

    /// <summary>Версия раскладки объекта. Пишется в каждый файл, чтобы разбор на той стороне, где
    /// файл читают руками или чужой программой, не гадал, чего ждать.</summary>
    public const int Schema = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Тот же довод, что в TicketExportService: без этого кириллица уезжает в \uXXXX, а эти
        // файлы читают и правят глазами.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Имена полей — как в выгрузке архивом (TicketExportService.BuildJson): «createdAt», а не
        // «CreatedAt». Один и тот же тикет не должен выглядеть по-разному в двух файлах, которые
        // читает один и тот же человек.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Разбор без учёта регистра: файл правят руками, и «Status» вместо «status» не повод
        // молча потерять правку.
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Как тикет лежит в хранилище. Отдельный тип, а не сериализованный <see cref="Ticket"/>:
    /// у доменного класса имена свойств C#-ные, а этот файл — договор с внешней стороной, и менять
    /// его переименованием свойства в домене нельзя.</summary>
    public sealed record Payload(
        int Schema, string Id, string Type, string Text, string Status,
        string CreatedBy, string CreatedByRole, string CreatedAt, string UpdatedAt,
        List<CommentPayload>? Comments = null);

    /// <summary>Реплика переписки внутри объекта тикета. Отдельным объектом в хранилище реплики не
    /// лежат намеренно: их всегда читают вместе с тикетом, а один объект вместо десятка — это и
    /// меньше запросов, и невозможность увидеть тикет без половины обсуждения.</summary>
    public sealed record CommentPayload(string Id, string Author, string Role, string Text, string CreatedAt);

    public static string RelativeKeyFor(Ticket t) =>
        $"{(TicketAutoReports.IsAutoReport(t) ? CrashFolder : StateFolder)}/{t.Id}.json";

    /// <summary>Наш ли это объект. Всё прочее под <c>tickets/</c> (архивы разовой выгрузки, чужие
    /// опыты) обходится стороной — бакет общий, и «всё, что лежит под нашим префиксом, наше» тут
    /// неверно.</summary>
    public static bool IsTicketObject(string key) =>
        key.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
        (key.StartsWith(StateFolder + "/", StringComparison.Ordinal) ||
         key.StartsWith(CrashFolder + "/", StringComparison.Ordinal));

    public static string Serialize(Ticket t) => Serialize(t, System.Array.Empty<TicketComment>());

    public static string Serialize(Ticket t, IReadOnlyList<TicketComment> comments) => JsonSerializer.Serialize(
        new Payload(Schema, t.Id, t.Type, t.Text, t.Status, t.CreatedBy, t.CreatedByRole, t.CreatedAt, t.UpdatedAt,
            comments.Count == 0 ? null
                : comments.Select(c => new CommentPayload(c.Id, c.Author, c.AuthorRole, c.Text, c.CreatedAt)).ToList()),
        JsonOptions);

    /// <summary>Разбор объекта из хранилища. Терпимый: незнакомые поля игнорируются, отсутствующий
    /// <c>updatedAt</c> берётся из <c>createdAt</c> (иначе правка, сделанная руками без этого поля,
    /// считалась бы бесконечно старой и не применялась никогда), неизвестный статус приводится к
    /// известному — файл правит человек, и опечатка в статусе не должна попадать в базу как новое,
    /// нигде не предусмотренное состояние.</summary>
    public static Ticket? TryParse(string json)
    {
        try
        {
            var p = JsonSerializer.Deserialize<Payload>(json, JsonOptions);
            if (p is null || string.IsNullOrWhiteSpace(p.Id)) return null;

            var createdAt = p.CreatedAt ?? "";
            return new Ticket
            {
                Id = p.Id.Trim(),
                Type = NormalizeType(p.Type),
                Text = p.Text ?? "",
                Status = NormalizeStatus(p.Status),
                CreatedBy = p.CreatedBy ?? "",
                CreatedByRole = p.CreatedByRole ?? "",
                CreatedAt = createdAt,
                UpdatedAt = string.IsNullOrWhiteSpace(p.UpdatedAt) ? createdAt : p.UpdatedAt.Trim(),
            };
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Реплики из объекта. Отдельным разбором, а не полем в <see cref="TryParse"/>, чтобы
    /// не менять его подпись: тикет и переписка нужны в разных местах и по отдельности.
    ///
    /// Реплики без идентификатора или без текста пропускаются молча: файл правят руками, и
    /// недописанная строка не повод ни падать, ни заводить пустую реплику.</summary>
    public static List<TicketComment> TryParseComments(string json)
    {
        var list = new List<TicketComment>();
        try
        {
            var p = JsonSerializer.Deserialize<Payload>(json, JsonOptions);
            if (p is null || string.IsNullOrWhiteSpace(p.Id) || p.Comments is null) return list;
            foreach (var c in p.Comments)
            {
                if (c is null || string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Text)) continue;
                list.Add(new TicketComment
                {
                    Id = c.Id.Trim(), TicketId = p.Id.Trim(), Author = c.Author ?? "",
                    AuthorRole = c.Role ?? "", Text = c.Text, CreatedAt = c.CreatedAt ?? "",
                });
            }
        }
        catch (JsonException) { /* битый объект — тикет уже разобран отдельно, переписку просто не берём */ }
        return list;
    }

    private static string NormalizeStatus(string? status) => (status ?? "").Trim().ToLowerInvariant() switch
    {
        TicketStatus.Closed => TicketStatus.Closed,
        TicketStatus.InProgress => TicketStatus.InProgress,
        // «resolved», «done», «закрыт» — не наши слова, но намерение очевидно; всё прочее (включая
        // пустое) — открытый тикет: потерять его из виду хуже, чем лишний раз показать.
        "resolved" or "done" or "fixed" or "закрыт" or "закрыто" => TicketStatus.Closed,
        "in progress" or "в работе" => TicketStatus.InProgress,
        _ => TicketStatus.Open,
    };

    private static string NormalizeType(string? type) => (type ?? "").Trim().ToLowerInvariant() switch
    {
        TicketType.Bug => TicketType.Bug,
        TicketType.Suggestion => TicketType.Suggestion,
        _ => TicketType.Other,
    };

    /// <summary>Отпечаток состояния объекта в бакете — по нему решается, ходить ли за содержимым.
    /// ETag (это md5 тела) точнее всего; хостинг может его не прислать, тогда годится пара
    /// «время изменения + размер». Пустая строка означает «состояние неизвестно» — тогда объект
    /// заведомо перечитывается.</summary>
    public static string TagOf(TicketStorageEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.ETag)) return entry.ETag.Trim('"');
        var when = entry.Modified?.ToUniversalTime().ToString("O") ?? "";
        return when.Length > 0 ? $"{when}/{entry.Size}" : "";
    }

    public sealed record Result(bool Ok, string? Error, int Pulled, int Pushed, int Failed)
    {
        public static Result Fail(string error) => new(false, error, 0, 0, 0);
        public bool Quiet => Pulled == 0 && Pushed == 0 && Failed == 0;
    }

    /// <summary>Один проход обмена: сперва принять чужое, потом отдать своё.
    ///
    /// Порядок важен и обратным быть не может: отдай мы сначала своё, закрытие тикета, сделанное в
    /// хранилище, было бы затёрто нашим устаревшим состоянием ещё до того, как мы про него узнали.
    ///
    /// ⚠️ <b>Здесь НЕТ ConfigureAwait(false), и это намеренно — противоположно правилу S3Client.</b>
    /// Между await'ами метод трогает базу, а соединение SQLite в приложении одно и не
    /// потокобезопасно: вызывают его с потока интерфейса (см. MainWindowViewModel.SyncTicketsNow —
    /// там же объяснено, почему DB-часть синхронизации тикетов идёт именно там), и продолжения
    /// обязаны возвращаться туда же. С ConfigureAwait(false) работа с базой уехала бы на поток
    /// пула. Сам поход в сеть внутри S3Client контекст не держит, так что интерфейс не стоит.</summary>
    public static async Task<Result> RunAsync(Database db, ITicketStorage storage, CancellationToken ct = default)
    {
        var listing = await storage.ListAsync(RootFolder + "/", ct);
        if (!listing.Ok) return Result.Fail(listing.Error ?? "хранилище не ответило");

        var remote = new Dictionary<string, TicketStorageEntry>(StringComparer.Ordinal);
        foreach (var entry in listing.Entries)
            if (IsTicketObject(entry.Key)) remote[entry.Key] = entry;

        int pulled = 0, pushed = 0, failed = 0;

        // ── Приём ───────────────────────────────────────────────────────────
        foreach (var key in remote.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var tag = TagOf(remote[key]);
            var seen = db.GetTicketStorageSeen(key);
            // Отпечаток совпал — содержимое то же, что мы уже разбирали. Пустой отпечаток (наш
            // только что положенный объект или хостинг без ETag и без времени) означает «не знаем»:
            // тогда идём за содержимым, это дешевле ошибки.
            if (seen is not null && tag.Length > 0 && string.Equals(seen.RemoteTag, tag, StringComparison.Ordinal))
                continue;

            var (json, error) = await storage.GetAsync(key, ct);
            // Объекта уже нет (стёрли между перечислением и чтением) — это не сбой: наш тикет, если
            // он у нас есть, выложится заново ниже, а чужого мы просто не увидели.
            if (json is null && error is null) continue;
            if (json is null) { failed++; continue; }

            var ticket = TryParse(json);
            if (ticket is null) { failed++; continue; }

            var changed = db.ApplyRemoteTicket(ticket);
            // Переписка приезжает ВСЕГДА, даже если сам тикет не изменился: реплику могли добавить
            // к тикету, у которого больше ничего не поменялось, и отбрасывать её вместе с «тикет
            // прежний» значило бы терять ровно то, ради чего переписка и заводилась.
            foreach (var comment in TryParseComments(json))
                if (db.AddTicketCommentIfMissing(comment)) changed = true;

            if (changed) pulled++;
            db.SaveTicketStorageSeen(key, ticket.Id, ticket.UpdatedAt, tag);
        }

        // ── Отдача ──────────────────────────────────────────────────────────
        // Список перечитывается ПОСЛЕ приёма: только что применённые чужие изменения не должны
        // уехать обратно как наши.
        foreach (var t in db.GetTickets())
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(t.Id)) continue;

            var key = RelativeKeyFor(t);
            var seen = db.GetTicketStorageSeen(key);
            // Выкладываем, если объекта в бакете нет вовсе (в том числе первый обмен этой машины —
            // так же доезжают все накопленные до появления канала тикеты) или если наше состояние
            // новее того, которое мы в бакете видели.
            var upToDate = seen is not null && remote.ContainsKey(key) &&
                           string.CompareOrdinal(t.UpdatedAt, seen.RemoteUpdatedAt) <= 0;
            if (upToDate) continue;

            var error = await storage.PutAsync(key, Serialize(t, db.GetTicketComments(t.Id)), ct);
            if (error is not null) { failed++; continue; }

            pushed++;
            // Отпечаток нового объекта нам неизвестен (PUT его не возвращает), поэтому пустой: на
            // следующем проходе объект будет перечитан один раз, применится вхолостую и отпечаток
            // запомнится. Дешевле, чем ещё один запрос ради заголовка.
            db.SaveTicketStorageSeen(key, t.Id, t.UpdatedAt, "");
        }

        return new Result(true, null, pulled, pushed, failed);
    }
}

/// <summary>Объект в хранилище так, как его видно в перечислении.</summary>
public sealed record TicketStorageEntry(string Key, long Size, DateTime? Modified, string ETag = "");

public sealed record TicketStorageListing(bool Ok, string? Error, IReadOnlyList<TicketStorageEntry> Entries)
{
    public static TicketStorageListing Fail(string error) => new(false, error, Array.Empty<TicketStorageEntry>());
    public static TicketStorageListing Success(IReadOnlyList<TicketStorageEntry> entries) => new(true, null, entries);
}

/// <summary>Хранилище глазами обмена тикетами: перечислить, прочитать, положить. Отдельный
/// интерфейс, а не прямой вызов S3Client, — ровно затем, чтобы слияние проверялось тестами без
/// сети и без настоящих ключей (ключи S3 в тестах не участвуют вообще, см. правило про секреты).
/// Ключи здесь ОТНОСИТЕЛЬНЫЕ («tickets/state/…»); префикс предприятия в бакете дописывает
/// реализация, см. S3TicketStorage.</summary>
public interface ITicketStorage
{
    Task<TicketStorageListing> ListAsync(string relativePrefix, CancellationToken ct = default);

    /// <summary>Содержимое объекта или причина, по которой его не прочитать. Отсутствие объекта —
    /// не исключение: он мог быть удалён между перечислением и чтением.</summary>
    Task<(string? Json, string? Error)> GetAsync(string relativeKey, CancellationToken ct = default);

    /// <summary>null — положили; иначе текст ошибки.</summary>
    Task<string?> PutAsync(string relativeKey, string json, CancellationToken ct = default);
}
