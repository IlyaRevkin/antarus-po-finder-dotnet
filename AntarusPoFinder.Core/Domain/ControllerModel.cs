namespace AntarusPoFinder.Core.Domain;

/// <summary>Controller family: SMH4, SMH5, KINCO, PIXEL2, PIXEL.</summary>
public class ControllerModel
{
    public int? Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Unused by the UI (no longer surfaced — <see cref="ControllerModification.HwVersion"/>
    /// is the only per-controller number now) but kept so <c>Database</c>'s shared sync/export
    /// plumbing, written generically against a "prefix" column shared with equipment_groups, keeps
    /// working unchanged. Always 0 going forward.</summary>
    public int Prefix { get; set; }

    public int SortOrder { get; set; }

    /// <summary>Stable cross-machine identity for config sync — survives renames, unlike Name.</summary>
    public string SyncId { get; set; } = "";

    /// <summary>Last local edit timestamp — see EquipmentGroup.UpdatedAt for format/purpose.</summary>
    public string UpdatedAt { get; set; } = "";
}
