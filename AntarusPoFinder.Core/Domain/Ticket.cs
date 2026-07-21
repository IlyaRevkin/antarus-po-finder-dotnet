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
}

public static class TicketType
{
    public const string Bug = "bug";
    public const string Suggestion = "suggestion";
    public const string Other = "other";

    public static readonly (string Id, string Label)[] All =
    [
        (Bug, "Баг"),
        (Suggestion, "Предложение"),
        (Other, "Другое"),
    ];

    public static string Label(string id) => id switch
    {
        Bug => "Баг",
        Suggestion => "Предложение",
        _ => "Другое",
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
}
