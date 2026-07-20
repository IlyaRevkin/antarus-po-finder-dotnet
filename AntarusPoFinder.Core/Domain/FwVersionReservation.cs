namespace AntarusPoFinder.Core.Domain;

/// <summary>
/// A locked-in "next number" for a firmware version, reserved before the firmware binary is
/// even built (so the programmer can embed the exact number in the compiled firmware, then
/// later upload the file and have it saved under that same number — see
/// Database.FwVersionReservations for the reservation/fulfillment flow).
/// </summary>
public class FwVersionReservation
{
    public int? Id { get; set; }
    public int SubtypeId { get; set; }
    public int ControllerId { get; set; }
    public int HwVersion { get; set; }
    public string VersionRaw { get; set; } = "";
    public string Status { get; set; } = "reserved";
    public string ReservedBy { get; set; } = "";
    public string ReservedAt { get; set; } = "";
    public int? FulfilledFwVersionId { get; set; }

    /// <summary>ISO datetime after which an open (status='reserved') reservation gets auto-cancelled
    /// by Database.ExpireStaleReservations — see ConfigService.ReservationTtlHours for the default
    /// interval and Database.ReserveNextVersion's ttlHours param for a per-reservation override.
    /// Empty string = never expires.</summary>
    public string ExpiresAt { get; set; } = "";

    // Populated by joins for display purposes (not stored on this table).
    public string GroupName { get; set; } = "";
    public string SubtypeName { get; set; } = "";
    public string CtrlName { get; set; } = "";
}
