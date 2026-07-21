namespace AntarusPoFinder.Core.Domain;

/// <summary>Hardware variant of a controller (e.g. PIXEL2-1020). HwVersion is the 3rd digit of the version string.</summary>
public class ControllerModification
{
    public int? Id { get; set; }
    public int ControllerId { get; set; }
    public string DisplayName { get; set; } = "";
    public int HwVersion { get; set; }
    public int SortOrder { get; set; }
    public string Description { get; set; } = "";

    /// <summary>Stable cross-machine identity for config sync — survives renames, unlike DisplayName.</summary>
    public string SyncId { get; set; } = "";

    /// <summary>Last local edit timestamp — see EquipmentGroup.UpdatedAt for format/purpose.</summary>
    public string UpdatedAt { get; set; } = "";

    /// <summary>Populated by DB join, not stored in this table.</summary>
    public string ControllerName { get; set; } = "";
}
