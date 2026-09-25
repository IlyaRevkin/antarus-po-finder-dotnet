namespace AntarusPoFinder.Core.Domain;

/// <summary>A bug report / suggestion raised by any role from the "Тикеты" page. Synced between
/// machines as an append-only event log on the shared network drive (see TicketSyncService in
/// AntarusPoFinder.App) rather than through the whole-snapshot config channel (ConfigSyncService) —
/// several people can file tickets from different PCs around the same time, and a snapshot export
/// would silently drop whichever machine's ticket wasn't included in the last write.</summary>
public class Ticket
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = TicketType.Other;
    public string Text { get; set; } = "";
    public string Status { get; set; } = TicketStatus.Open;
    /// <summary>Whoever created the ticket — AppServices.CurrentUserName, which is the AD login if
    /// this session authenticated via AD (see RoleSwitchDialog.AdAuth_Click), else the shared
    /// Windows/machine login (roles themselves are still shared passwords, not per-person accounts).
    /// Same source used elsewhere for "who" (UploadView reservations/authors, ConfigSyncService's
    /// exported_by). "Свои тикеты" for наладчик/программист means tickets with this value equal to
    /// the current session's CurrentUserName.</summary>
    public string CreatedBy { get; set; } = "";
    public string CreatedByRole { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    /// <summary>Критичность — только у тикетов про баг В ПРОШИВКЕ (<see cref="TicketType.FwBug"/>),
    /// у остальных пусто. Наладчик, нашедший баг на объекте, единственный, кто может сказать,
    /// насколько всё плохо: «шкаф не запускается» и «в названии режима опечатка» приезжают
    /// программисту одинаковым текстом, и разбирать их приходилось чтением. См. <see cref="FwBugSeverity"/>.</summary>
    public string Severity { get; set; } = "";

    /// <summary>ПЕРЕНОСИМЫЙ идентификатор прошивки, к которой относится тикет (fw_versions.sync_id).
    ///
    /// Именно sync_id, а не id: тикеты ездят между машинами, а id — локальный автоинкремент, у
    /// коллеги под тем же числом лежит другая прошивка. Ссылка по id молча показывала бы жалобу на
    /// чужую прошивку — той же породы ошибка, что и призрак подтипа.</summary>
    public string FwSyncId { get; set; } = "";

    /// <summary>Как прошивка называлась в момент создания тикета — человеческим текстом
    /// («НГР 2.0 / КПЧ / ATV310 / 1.74.0»).
    ///
    /// Дублирует ссылку намеренно: прошивки с таким sync_id может не быть на машине, куда приехал
    /// тикет (ещё не синхронизировались, или её удалили). Без подписи там осталась бы жалоба
    /// неизвестно на что. Подпись не обновляется вслед за переименованием — это слепок на момент
    /// жалобы, и он должен совпадать с тем, что человек видел на экране.</summary>
    public string FwLabel { get; set; } = "";
}

/// <summary>Одна реплика в переписке по тикету.
///
/// Заведено потому, что обсуждать тикет было негде: правка текста самого тикета затирала то, что
/// человек написал раньше, а договариваться приходилось словами мимо программы. Реплики только
/// ДОБАВЛЯЮТСЯ и никогда не редактируются и не удаляются — это переписка, а не документ. Из этого
/// же следует и правило слияния между машинами: объединение по идентификатору, без разбора, чья
/// версия новее (см. TicketStorageSync).</summary>
public class TicketComment
{
    /// <summary>Свой идентификатор, а не порядковый номер: реплики появляются на разных машинах
    /// независимо, и нумеровать их по порядку значит гарантированно столкнуться номерами.</summary>
    public string Id { get; set; } = "";
    public string TicketId { get; set; } = "";
    public string Author { get; set; } = "";
    public string AuthorRole { get; set; } = "";
    public string Text { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}

public static class TicketType
{
    public const string Bug = "bug";
    public const string Suggestion = "suggestion";
    public const string Other = "other";

    /// <summary>Баг В ПРОШИВКЕ, а не в программе. Отдельный тип, потому что у него другой
    /// адресат (программист, а не тот, кто чинит приложение), своя критичность и ссылка на
    /// конкретную прошивку. Смешанные в одну кучу, они терялись среди жалоб на интерфейс.</summary>
    public const string FwBug = "fw_bug";

    public static readonly (string Id, string Label)[] All =
    [
        (FwBug, "Баг прошивки"),
        (Bug, "Баг"),
        (Suggestion, "Предложение"),
        (Other, "Другое"),
    ];

    public static string Label(string id) => id switch
    {
        FwBug => "Баг прошивки",
        Bug => "Баг",
        Suggestion => "Предложение",
        _ => "Другое",
    };

    /// <summary>Жалоба ли это вообще — в отличие от предложения. По этому же признаку выбирается цвет:
    /// баги красные, предложения зелёные.</summary>
    public static bool IsBug(string id) => id == Bug || id == FwBug;
}

/// <summary>Критичность бага в прошивке — три уровня, красный / оранжевый / жёлтый.
///
/// Три, а не пять и не десять: различать надо не оттенки беды, а решение — бросать ли всё и чинить
/// сейчас, чинить ли к следующей версии, или записать и жить дальше. Шкала из десяти пунктов превращается
/// в спор о том, седьмой это уровень или восьмой.</summary>
public static class FwBugSeverity
{
    /// <summary>Красный: оборудование не работает или работает опасно.</summary>
    public const string Critical = "critical";
    /// <summary>Оранжевый: работает, но не так, как должно — есть обходной путь.</summary>
    public const string Major = "major";
    /// <summary>Жёлтый: мелочь, на работу не влияет.</summary>
    public const string Minor = "minor";

    public static readonly (string Id, string Label)[] All =
    [
        (Critical, "Критичный — не работает"),
        (Major, "Серьёзный — работает неверно"),
        (Minor, "Мелкий — не мешает"),
    ];

    public static string Label(string id) => id switch
    {
        Critical => "Критичный",
        Major => "Серьёзный",
        Minor => "Мелкий",
        _ => "",
    };

    /// <summary>Приводит приехавшее значение к известному. Неизвестное — в пустое, а не в
    /// «критичный»: опечатка в файле хранилища не должна поднимать тревогу.</summary>
    public static string Normalize(string? id) => (id ?? "").Trim().ToLowerInvariant() switch
    {
        Critical => Critical,
        Major => Major,
        Minor => Minor,
        _ => "",
    };

    /// <summary>Порядок для сортировки: самое страшное сверху. Без критичности (не баг прошивки) —
    /// в конец, чтобы не разбавлять собой шкалу.</summary>
    public static int SortOrder(string id) => id switch
    {
        Critical => 0,
        Major => 1,
        Minor => 2,
        _ => 3,
    };
}

public static class TicketStatus
{
    public const string Open = "open";
    public const string InProgress = "in_progress";
    public const string Closed = "closed";

    public static string Label(string id) => id switch
    {
        InProgress => "В работе",
        Closed => "Закрыт",
        _ => "Открыт",
    };

    /// <summary>Порядок статусов ПО СМЫСЛУ, а не по алфавиту: «В работе» → «Открыт» → «Закрыт».
    ///
    /// Сортировка по названию статуса даёт «В работе, Закрыт, Открыт» — закрытые оказываются
    /// посередине, между тем, что делается, и тем, что ещё ждёт. Это и была жалоба: «пересмотреть
    /// сортировку тикетов по статусу, сейчас в алфавитном порядке». Осмысленный порядок —
    /// по близости к вниманию: сначала то, чем заняты прямо сейчас, потом то, что ждёт очереди,
    /// потом то, о чём можно не думать.</summary>
    public static int SortOrder(string id) => id switch
    {
        InProgress => 0,
        Closed => 2,
        _ => 1,
    };
}
