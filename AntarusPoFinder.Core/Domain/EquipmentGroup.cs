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
}
