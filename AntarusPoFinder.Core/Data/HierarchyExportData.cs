using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AntarusPoFinder.Core.Data;

public class ExportedGroup
{
    [JsonPropertyName("sync_id")] public string SyncId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("prefix")] public int Prefix { get; set; }
    [JsonPropertyName("sort_order")] public int SortOrder { get; set; }
}

public class ExportedSubType
{
    [JsonPropertyName("sync_id")] public string SyncId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("prefix")] public int Prefix { get; set; }
    [JsonPropertyName("folder_name")] public string FolderName { get; set; } = "";
    [JsonPropertyName("sort_order")] public int SortOrder { get; set; }
    [JsonPropertyName("group_sync_id")] public string GroupSyncId { get; set; } = "";
    [JsonPropertyName("group_name")] public string GroupName { get; set; } = "";
}

public class ExportedController
{
    [JsonPropertyName("sync_id")] public string SyncId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("prefix")] public int Prefix { get; set; }
    [JsonPropertyName("sort_order")] public int SortOrder { get; set; }
}

public class ExportedModification
{
    [JsonPropertyName("sync_id")] public string SyncId { get; set; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("hw_version")] public int HwVersion { get; set; }
    [JsonPropertyName("sort_order")] public int SortOrder { get; set; }
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("controller_sync_id")] public string ControllerSyncId { get; set; } = "";
    [JsonPropertyName("controller_name")] public string ControllerName { get; set; } = "";
}

public class ExportedManufacturer
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("sort_order")] public int SortOrder { get; set; }
}

public class ExportedReservation
{
    [JsonPropertyName("subtype_sync_id")] public string SubtypeSyncId { get; set; } = "";
    [JsonPropertyName("subtype_name")] public string SubtypeName { get; set; } = "";
    [JsonPropertyName("controller_sync_id")] public string ControllerSyncId { get; set; } = "";
    [JsonPropertyName("controller_name")] public string ControllerName { get; set; } = "";
    [JsonPropertyName("hw_version")] public int HwVersion { get; set; }
    [JsonPropertyName("version_raw")] public string VersionRaw { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "reserved";
    [JsonPropertyName("reserved_by")] public string ReservedBy { get; set; } = "";
    [JsonPropertyName("reserved_at")] public string ReservedAt { get; set; } = "";
}

public class ExportedFwVersion
{
    [JsonPropertyName("version_raw")] public string VersionRaw { get; set; } = "";
    [JsonPropertyName("hw_version")] public int HwVersion { get; set; }
    [JsonPropertyName("sw_version")] public int SwVersion { get; set; }
    [JsonPropertyName("eq_prefix")] public int EqPrefix { get; set; }
    [JsonPropertyName("sub_prefix")] public int SubPrefix { get; set; }
    [JsonPropertyName("dt_str")] public string DtStr { get; set; } = "";
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("disk_path")] public string DiskPath { get; set; } = "";
    [JsonPropertyName("local_path")] public string LocalPath { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("changelog")] public string Changelog { get; set; } = "";
    [JsonPropertyName("launch_types")] public string LaunchTypes { get; set; } = "[]";
    [JsonPropertyName("io_map_path")] public string IoMapPath { get; set; } = "";
    [JsonPropertyName("instructions_path")] public string InstructionsPath { get; set; } = "";
    [JsonPropertyName("is_opc")] public int IsOpc { get; set; }
    [JsonPropertyName("request_num")] public string RequestNum { get; set; } = "";
    [JsonPropertyName("upload_date")] public string UploadDate { get; set; } = "";
    [JsonPropertyName("archived")] public int Archived { get; set; }
    [JsonPropertyName("tags")] public string Tags { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "active";
    [JsonPropertyName("released")] public int Released { get; set; }
    [JsonPropertyName("group_name")] public string GroupName { get; set; } = "";
    [JsonPropertyName("subtype_sync_id")] public string SubtypeSyncId { get; set; } = "";
    [JsonPropertyName("subtype_name")] public string SubtypeName { get; set; } = "";
    [JsonPropertyName("controller_sync_id")] public string ControllerSyncId { get; set; } = "";
    [JsonPropertyName("ctrl_name")] public string CtrlName { get; set; } = "";
}

public class ExportedParamFile
{
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("disk_path")] public string DiskPath { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("upload_date")] public string UploadDate { get; set; } = "";
    [JsonPropertyName("archived")] public int Archived { get; set; }
    [JsonPropertyName("manufacturer")] public string Manufacturer { get; set; } = "";
    [JsonPropertyName("subtype_sync_id")] public string SubtypeSyncId { get; set; } = "";
    [JsonPropertyName("subtype_name")] public string SubtypeName { get; set; } = "";
    [JsonPropertyName("group_name")] public string GroupName { get; set; } = "";
}

public class ExportedAppUser
{
    [JsonPropertyName("sync_id")] public string SyncId { get; set; } = "";
    [JsonPropertyName("ad_login")] public string AdLogin { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "naladchik";
    [JsonPropertyName("first_login_at")] public string FirstLoginAt { get; set; } = "";
    [JsonPropertyName("last_login_at")] public string LastLoginAt { get; set; } = "";
    [JsonPropertyName("role_updated_at")] public string RoleUpdatedAt { get; set; } = "";
}

public class HierarchyExportData
{
    [JsonPropertyName("equipment_groups")] public List<ExportedGroup> EquipmentGroups { get; set; } = new();
    [JsonPropertyName("equipment_subtypes")] public List<ExportedSubType> EquipmentSubtypes { get; set; } = new();
    [JsonPropertyName("controller_models")] public List<ExportedController> ControllerModels { get; set; } = new();
    [JsonPropertyName("controller_modifications")] public List<ExportedModification> ControllerModifications { get; set; } = new();
    // Deliberately nullable with NO default: an export written by an older app version simply omits
    // these keys, which System.Text.Json leaves as null (vs. an empty array, which means "the
    // source genuinely has zero of these"). Database.ConfigExchange relies on telling those two
    // cases apart before doing a full-mirror delete of what's missing locally.
    [JsonPropertyName("param_manufacturers")] public List<ExportedManufacturer>? ParamManufacturers { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("allowed_extensions")] public List<string>? AllowedExtensions { get; set; }
    [JsonPropertyName("fw_version_reservations")] public List<ExportedReservation> Reservations { get; set; } = new();
    [JsonPropertyName("fw_versions")] public List<ExportedFwVersion> FwVersions { get; set; } = new();
    [JsonPropertyName("param_files")] public List<ExportedParamFile> ParamFiles { get; set; } = new();
    // Always present with a default empty list (unlike Tags/AllowedExtensions/ParamManufacturers
    // above) — an export from an older app version without this feature simply carries zero users,
    // which correctly means "nothing to add/update", never "delete everyone" (app_users is
    // additive + last-writer-wins-on-role only, see Database.ConfigExchange — nobody is ever
    // removed from the roster via sync).
    [JsonPropertyName("app_users")] public List<ExportedAppUser> AppUsers { get; set; } = new();
}

/// <summary>Per-category added/updated counts — drives both the "Экспортировано/Импортировано"
/// summary dialogs and the config-update banner's "Подробно" breakdown.</summary>
public class ImportCounts
{
    public int GroupsAdded { get; set; }
    public int GroupsUpdated { get; set; }
    public int SubtypesAdded { get; set; }
    public int SubtypesUpdated { get; set; }
    public int ControllersAdded { get; set; }
    public int ControllersUpdated { get; set; }
    public int ModificationsAdded { get; set; }
    public int ModificationsUpdated { get; set; }
    public int ManufacturersAdded { get; set; }
    public int ManufacturersRemoved { get; set; }
    public int TagsAdded { get; set; }
    public int TagsRemoved { get; set; }
    public int ExtensionsAdded { get; set; }
    public int ExtensionsRemoved { get; set; }
    public int ReservationsAdded { get; set; }
    public int ReservationsUpdated { get; set; }
    public int FwVersions { get; set; }
    public int ParamFiles { get; set; }
    public int AppUsersAdded { get; set; }
    public int AppUsersUpdated { get; set; }

    public int TotalChanges =>
        GroupsAdded + GroupsUpdated + SubtypesAdded + SubtypesUpdated + ControllersAdded + ControllersUpdated +
        ModificationsAdded + ModificationsUpdated + ManufacturersAdded + ManufacturersRemoved + TagsAdded + TagsRemoved +
        ExtensionsAdded + ExtensionsRemoved + ReservationsAdded + ReservationsUpdated + FwVersions + ParamFiles +
        AppUsersAdded + AppUsersUpdated;
}
