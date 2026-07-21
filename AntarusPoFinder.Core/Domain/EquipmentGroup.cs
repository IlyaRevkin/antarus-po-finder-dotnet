namespace AntarusPoFinder.Core.Domain;

/// <summary>Top-level cabinet type: ПЖ, НГР, ТГР.</summary>
public class EquipmentGroup
{
    public int? Id { get; set; }
    public string Name { get; set; } = "";
    public int Prefix { get; set; }
    public int SortOrder { get; set; }

    /// <summary>Stable cross-machine identity for config sync — survives renames, unlike Name.</summary>
    public string SyncId { get; set; } = "";

    /// <summary>Last local edit timestamp (Database.NowIso() format — "yyyy-MM-dd HH:mm:ss", the same
    /// space-separated format as fw_version_reservations.expires_at) — used by the config-sync
    /// conflict detector to tell an actual concurrent edit apart from a normal one-sided update. Set
    /// automatically by every Database method that changes this row; never set by hand.</summary>
    public string UpdatedAt { get; set; } = "";
}
