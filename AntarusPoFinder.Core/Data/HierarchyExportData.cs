using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AntarusPoFinder.Core.Data;

public class ExportedGroup
{
    [JsonPropertyName("sync_id")] public string SyncId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("prefix")] public int Prefix { get; set; }
    [JsonPropertyName("sort_order")] public int SortOrder { get; set; }
    // Missing (older export) deserializes to "" — treated as "no edit history known" by the conflict
    // detector, which falls back to the pre-conflict-detection behavior for that row. See
    // Database.ConflictResolution.ClassifyHierarchyChange.
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
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
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public class ExportedController
{
    [JsonPropertyName("sync_id")] public string SyncId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("prefix")] public int Prefix { get; set; }
    [JsonPropertyName("sort_order")] public int SortOrder { get; set; }
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
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
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
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
    /// <summary>Переносимый идентификатор строки прошивки (fw_versions.sync_id) — по нему импорт
    /// в первую очередь и опознаёт «ту же самую» запись, откатываясь на прежний натуральный ключ
    /// (подтип + контроллер + version_raw) только когда его нет. Пустая строка = экспорт со старой
    /// версии приложения, поведение тогда ровно прежнее. Подробности — Database.cs, столбец sync_id.</summary>
    [JsonPropertyName("sync_id")] public string SyncId { get; set; } = "";
    /// <summary>Имя конфигурации шкафа (fw_versions.config_name) — '' у обычной записи. Входит в
    /// натуральный ключ сопоставления наравне с подтипом/контроллером/version_raw: у всех конфигураций
    /// одной прошивки эти три поля совпадают, и без имени приёмник считал бы их одной строкой и
    /// схлопывал бы заготовленные варианты в один. Пусто в снимке со старой версии приложения — тогда
    /// поведение ровно прежнее.</summary>
    [JsonPropertyName("config_name")] public string ConfigName { get; set; } = "";
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
    [JsonPropertyName("hmi_path")] public string HmiPath { get; set; } = "";
    [JsonPropertyName("executable_hint")] public string ExecutableHint { get; set; } = "";
    [JsonPropertyName("hmi_executable_hint")] public string HmiExecutableHint { get; set; } = "";
    [JsonPropertyName("modbus_map_path")] public string ModbusMapPath { get; set; } = "";
    [JsonPropertyName("is_opc")] public int IsOpc { get; set; }
    [JsonPropertyName("request_num")] public string RequestNum { get; set; } = "";
    [JsonPropertyName("upload_date")] public string UploadDate { get; set; } = "";
    [JsonPropertyName("archived")] public int Archived { get; set; }
    [JsonPropertyName("tags")] public string Tags { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "active";
    [JsonPropertyName("released")] public int Released { get; set; }
    // Deletion tombstone (Задача 3) — '' means not deleted. Present on every export (never omitted
    // like Tags/AllowedExtensions above) so an older exporting app version simply sends '' for every
    // row, which ImportHierarchyDataCore correctly reads as "nothing deleted here" rather than
    // wiping anything.
    [JsonPropertyName("deleted_at")] public string DeletedAt { get; set; } = "";
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
    /// <summary>1 — запись снята (архивирована) на выгружавшей машине. Именно ТУМБСТОУН, а не
    /// «строки просто нет в снимке»: param_files аддитивна по своей природе (у каждой машины бывают
    /// свои загрузки), поэтому отсутствие строки никогда не означает удаление — его нужно передавать
    /// положительным сигналом, ровно как fw_versions.deleted_at.</summary>
    [JsonPropertyName("archived")] public int Archived { get; set; }
    [JsonPropertyName("manufacturer")] public string Manufacturer { get; set; } = "";
    /// <summary>Стабильный идентификатор строки. Пусто у экспорта со старой версии приложения —
    /// импорт тогда соотносит по натуральному ключу, как раньше.</summary>
    [JsonPropertyName("sync_id")] public string SyncId { get; set; } = "";
    /// <summary>Теги файла параметров. Раньше в общий конфиг вообще не выгружались — тег, навешенный
    /// на одной машине, до коллег не доезжал, и поиск по нему у них ничего не находил.</summary>
    [JsonPropertyName("tags")] public string Tags { get; set; } = "";
    [JsonPropertyName("subtype_sync_id")] public string SubtypeSyncId { get; set; } = "";
    [JsonPropertyName("subtype_name")] public string SubtypeName { get; set; } = "";
    [JsonPropertyName("group_name")] public string GroupName { get; set; } = "";
}

/// <summary>Шаблон паспорта шкафа в общем конфиге. Устроен дословно как ExportedParamFile выше и по
/// тем же причинам: archived=1 едет ПОЛОЖИТЕЛЬНЫМ тумбстоуном (таблица аддитивная — у каждой машины
/// бывают свои загрузки, поэтому «строки нет в снимке» никогда не означает «удалили»), подтип
/// адресуется переносимо (sync_id + запасной ключ по именам на первом контакте двух баз).</summary>
public class ExportedPassport
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("disk_path")] public string DiskPath { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("upload_date")] public string UploadDate { get; set; } = "";
    [JsonPropertyName("archived")] public int Archived { get; set; }
    [JsonPropertyName("sync_id")] public string SyncId { get; set; } = "";
    [JsonPropertyName("tags")] public string Tags { get; set; } = "";
    [JsonPropertyName("subtype_sync_id")] public string SubtypeSyncId { get; set; } = "";
    [JsonPropertyName("subtype_name")] public string SubtypeName { get; set; } = "";
    [JsonPropertyName("group_name")] public string GroupName { get; set; } = "";

    /// <summary>1 — типовой паспорт: бланк, не привязанный ни к какому шкафу (НКУ, Щит СПЛ, ШР —
    /// см. PassportService). Признак явный, а не «подтип пустой»: подтип у такой записи пуст
    /// НЕИЗБЕЖНО, и без флага получатель не отличил бы «это бланк» от «подтип не доехал/потерялся»
    /// и молча выбросил бы запись. Старый снимок флага не содержит — там 0, и всё как раньше.</summary>
    [JsonPropertyName("general")] public int General { get; set; }
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

/// <summary>Одно переписывание hw модификации контроллера (напр. PIXEL2 044 → 1321), сделанное
/// оператором на своей машине и разосланное остальным как ЯВНАЯ операция-переименование (см.
/// Database.HwRewriteLog.cs и ConfigSyncService.ReplayHwRewrites). Без этого события смена hw ехала
/// бы как обычный дифф строк fw_versions: hw зашит в version_raw (натуральный ключ синхронизации),
/// поэтому у получателя старая строка (044) оставалась фантомом «нет папки на диске», а новая (1321)
/// вставлялась дублем. Событие адресует контроллер переносимо (sync_id — локальные id на машинах
/// разные) и несёт отметку времени, по которой каждая машина проигрывает только то, чего ещё не
/// применяла (watermark hw_rewrite_applied_at). Nullable-список без дефолта в HierarchyExportData по
/// той же причине, что Tags/FwUsage: экспорт со старой версии приложения ключа не содержит вовсе.</summary>
public class ExportedHwRewrite
{
    [JsonPropertyName("controller_sync_id")] public string ControllerSyncId { get; set; } = "";
    [JsonPropertyName("controller_name")] public string ControllerName { get; set; } = "";
    [JsonPropertyName("old_hw")] public int OldHw { get; set; }
    [JsonPropertyName("new_hw")] public int NewHw { get; set; }
    [JsonPropertyName("ts")] public string Ts { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
}

/// <summary>Одно решение модерации (вывод из модерации, архивирование, откат, удаление), принятое на
/// какой-то машине — см. Database.ModerationLog.cs и ConfigSyncService.PushModerationOnly. Отдельная
/// секция общего конфига именно потому, что полный снимок выгружает только администратор: решение
/// наладчика/программиста иначе физически не могло уехать к остальным, ведь fw_versions в снимке —
/// это состояние БД машины-экспортёра, а не чужой.
///
/// Прошивка адресуется переносимо (sync_id подтипа и модели контроллера + version_raw), как и
/// ExportedFwUsage: локальные id прошивок на разных машинах разные. Имена (group/subtype/controller)
/// едут рядом запасным ключом ровно как у ExportedFwVersion — на самом первом контакте двух
/// независимо собранных баз sync_id ещё не совпадают.
///
/// Все четыре признака монотонные («только вперёд»): released 0→1, archived 0→1, status active→иной,
/// deleted_at ''→отметка. Поэтому применение идемпотентно, порядок решений не важен, а две машины,
/// принявшие РАЗНЫЕ решения по одной версии, сходятся на объединении (см.
/// Database.ApplyModerationDecisions).</summary>
public class ExportedModerationDecision
{
    [JsonPropertyName("subtype_sync_id")] public string SubtypeSyncId { get; set; } = "";
    [JsonPropertyName("subtype_name")] public string SubtypeName { get; set; } = "";
    [JsonPropertyName("group_name")] public string GroupName { get; set; } = "";
    [JsonPropertyName("controller_sync_id")] public string ControllerSyncId { get; set; } = "";
    [JsonPropertyName("controller_name")] public string ControllerName { get; set; } = "";
    [JsonPropertyName("version_raw")] public string VersionRaw { get; set; } = "";
    [JsonPropertyName("released")] public int Released { get; set; }
    [JsonPropertyName("archived")] public int Archived { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("deleted_at")] public string DeletedAt { get; set; } = "";
    [JsonPropertyName("ts")] public string Ts { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";

    /// <summary>Ключ дедупликации при склейке журналов разных машин (см.
    /// ConfigSyncService.PushModerationOnly и Database.AbsorbModerationDecisions): одно и то же
    /// решение приезжает обратно на машину-автора при каждом полном экспорте, и без ключа журнал
    /// разрастался бы копиями. Отметка времени входит в ключ намеренно — два РАЗНЫХ решения по одной
    /// версии (сначала выпустили, потом откатили) обязаны остаться двумя записями.</summary>
    public string DedupKey() =>
        string.Join("|", SubtypeSyncId, SubtypeName, ControllerSyncId, ControllerName, VersionRaw,
            Released, Archived, Status, DeletedAt, Ts);
}

/// <summary>Один перенос версии прошивки на другую модель контроллера, сделанный оператором на своей
/// машине (см. Database.CtrlReassignLog.cs и ConfigSyncService.ReplayCtrlReassigns). Ровно та же
/// логика, что у ExportedHwRewrite: контроллер входит и в натуральный ключ синхронизации, и в путь
/// папки на диске, поэтому без явного события перенос выглядел бы у коллег как «удалили + завели
/// заново». Nullable-список без дефолта в HierarchyExportData — экспорт со старой версии приложения
/// ключа не содержит вовсе.</summary>
public class ExportedCtrlReassign
{
    [JsonPropertyName("subtype_sync_id")] public string SubtypeSyncId { get; set; } = "";
    [JsonPropertyName("subtype_name")] public string SubtypeName { get; set; } = "";
    [JsonPropertyName("group_name")] public string GroupName { get; set; } = "";
    [JsonPropertyName("old_controller_sync_id")] public string OldControllerSyncId { get; set; } = "";
    [JsonPropertyName("old_controller_name")] public string OldControllerName { get; set; } = "";
    [JsonPropertyName("new_controller_sync_id")] public string NewControllerSyncId { get; set; } = "";
    [JsonPropertyName("new_controller_name")] public string NewControllerName { get; set; } = "";
    [JsonPropertyName("version_raw")] public string VersionRaw { get; set; } = "";
    [JsonPropertyName("ts")] public string Ts { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
}

/// <summary>Вклад одной машины в общую статистику выборов прошивки — см. Database.FwUsage.cs.
/// Прошивка адресуется переносимо (sync_id подтипа и модели контроллера + version_raw): локальные id
/// на разных машинах разные.</summary>
public class ExportedFwUsage
{
    [JsonPropertyName("origin")] public string Origin { get; set; } = "";
    [JsonPropertyName("query_key")] public string QueryKey { get; set; } = "";
    [JsonPropertyName("subtype_sync_id")] public string SubtypeSyncId { get; set; } = "";
    [JsonPropertyName("controller_sync_id")] public string ControllerSyncId { get; set; } = "";
    [JsonPropertyName("version_raw")] public string VersionRaw { get; set; } = "";
    [JsonPropertyName("uses")] public int Uses { get; set; }
    [JsonPropertyName("last_used_at")] public string LastUsedAt { get; set; } = "";
    /// <summary>Ручной вес выдачи (см. Database.FwUsage.cs weight). Ноль, если машина-источник не
    /// делится своим весом (ConfigService.FwWeightShared) — счётчик открытий при этом уезжает всё
    /// равно. Дефолт 0 и по той же причине, что и остальные новые поля: экспорт со старой версии
    /// приложения ключа не содержит вовсе, и это «источник о нём не знает», а не «у источника вес нулевой».</summary>
    [JsonPropertyName("weight")] public int Weight { get; set; }
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
    // Независимый список расширений HMI-проектов — та же логика nullable-без-дефолта, что и у
    // AllowedExtensions выше (старый экспорт ключа просто не содержит).
    [JsonPropertyName("allowed_extensions_hmi")] public List<string>? AllowedExtensionsHmi { get; set; }
    // Третий независимый список — расширения поиска схем на втором диске (SchematicService), та же
    // nullable-без-дефолта логика: старый экспорт ключа не содержит вовсе.
    [JsonPropertyName("allowed_extensions_schematic")] public List<string>? AllowedExtensionsSchematic { get; set; }
    [JsonPropertyName("fw_version_reservations")] public List<ExportedReservation> Reservations { get; set; } = new();
    [JsonPropertyName("fw_versions")] public List<ExportedFwVersion> FwVersions { get; set; } = new();
    [JsonPropertyName("param_files")] public List<ExportedParamFile> ParamFiles { get; set; } = new();

    /// <summary>true — снимок писало приложение, которое уже умеет sync_id/тумбстоуны у файлов
    /// параметров, т.е. список param_files выше ПОЛНЫЙ (включая архивные) и каждая строка адресуема.
    /// Отдельный явный флаг, а не «посмотрим, есть ли хоть у одной строки sync_id»: эталонная
    /// синхронизация архивирует у получателей всё, чего в снимке нет, и снимок со старой версии
    /// приложения (где param_files выгружались без sync_id и без архивных) не должен даже случайно
    /// быть принят за полный — иначе одна синхронизация со старого клиента вычистила бы у всех
    /// остальных всю таблицу параметров. Старый экспорт ключа не содержит → false → прежнее
    /// поведение (только добавлять) сохраняется байт в байт.</summary>
    [JsonPropertyName("param_files_have_sync")] public bool ParamFilesHaveSync { get; set; }

    /// <summary>Шаблоны паспортов шкафов — полный список, вместе с архивными (они и есть тумбстоуны).
    /// Nullable без дефолта: экспорт со старой версии приложения ключа не содержит вовсе, и импорт
    /// тогда паспорта просто не трогает (у получателя они остаются как были), а не считает, что
    /// «у отправителя их ноль».</summary>
    [JsonPropertyName("passports")] public List<ExportedPassport>? Passports { get; set; }
    // Always present with a default empty list (unlike Tags/AllowedExtensions/ParamManufacturers
    // above) — an export from an older app version without this feature simply carries zero users,
    // which correctly means "nothing to add/update", never "delete everyone" (app_users is
    // additive + last-writer-wins-on-role only, see Database.ConfigExchange — nobody is ever
    // removed from the roster via sync).
    [JsonPropertyName("app_users")] public List<ExportedAppUser> AppUsers { get; set; } = new();
    /// <summary>Статистика выборов прошивки со всех известных машин (Database.FwUsage.cs). Nullable
    /// без дефолта по той же причине, что Tags/AllowedExtensions: экспорт со старой версии приложения
    /// ключа не содержит вовсе, и это «источник о ней не знает», а не «у источника её ноль».</summary>
    [JsonPropertyName("fw_usage")] public List<ExportedFwUsage>? FwUsage { get; set; }

    /// <summary>Явные переписывания hw модификаций контроллеров (см. ExportedHwRewrite) — журнал
    /// последних операций, чтобы каждая машина проиграла у себя ещё не применённые (по отметке
    /// времени) и переименовала свои строки/папки прошивок, а не завела дубли. Nullable без дефолта:
    /// экспорт со старой версии приложения ключа не содержит, и приём тогда просто ничего не
    /// проигрывает (fw_versions едут как раньше).</summary>
    [JsonPropertyName("hw_rewrites")] public List<ExportedHwRewrite>? HwRewrites { get; set; }

    /// <summary>Решения модерации, принятые на ЛЮБОЙ машине (см. ExportedModerationDecision). Едут
    /// отдельной секцией, потому что fw_versions в снимке — это состояние базы машины-экспортёра, а
    /// полный снимок выгружает только администратор: без этой секции решение наладчика или
    /// программиста не имело физической возможности доехать до остальных. Nullable без дефолта по той
    /// же причине, что Tags/HwRewrites: экспорт со старой версии приложения ключа не содержит вовсе, и
    /// импорт тогда просто ничего не применяет — прежнее поведение один в один.</summary>
    [JsonPropertyName("moderation_decisions")] public List<ExportedModerationDecision>? ModerationDecisions { get; set; }

    /// <summary>Переносы версий прошивок на другую модель контроллера (см. ExportedCtrlReassign) —
    /// журнал последних операций, чтобы приёмник проиграл ещё не применённое как ПЕРЕНОС (запись +
    /// папка на диске), а не получил фантом под старым контроллером и дубль под новым. Nullable без
    /// дефолта — как и hw_rewrites рядом.</summary>
    [JsonPropertyName("ctrl_reassignments")] public List<ExportedCtrlReassign>? CtrlReassignments { get; set; }

    /// <summary>Отметки времени удаления/возврата для трёх плоских списков выше (производители,
    /// теги, расширения) — см. Database.FlatLists.cs. Nullable по той же причине: экспорт со старой
    /// версии приложения ключа не содержит, и импорт тогда откатывается на прежнее чисто additive
    /// поведение вместо того, чтобы считать «раз отметок нет, значит ничего никогда не удаляли».</summary>
    [JsonPropertyName("flat_list_state")] public List<ExportedFlatListState>? FlatListState { get; set; }
}

/// <summary>Одна строка flat_list_state в выгрузке. Живым элемент считается, когда RevivedAt не
/// меньше DeletedAt — сравнение строковое, отметки в ISO-формате (см. Database.NowIso).</summary>
public class ExportedFlatListState
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("deleted_at")] public string DeletedAt { get; set; } = "";
    [JsonPropertyName("revived_at")] public string RevivedAt { get; set; } = "";
}

/// <summary>Per-category added/updated counts — drives both the "Экспортировано/Импортировано"
/// summary dialogs and the config-update banner's "Подробно" breakdown.</summary>
public class ImportCounts
{
    public int GroupsAdded { get; set; }
    public int GroupsUpdated { get; set; }
    /// <summary>Тип шкафа, удалённый на выгружавшей машине — зеркалится так же, как подтипы и
    /// контроллеры (см. ImportHierarchyDataCore). Без этого удалённый мусорный тип возвращался с
    /// любой машины/старого JSON, которые о его удалении ещё не знали.</summary>
    public int GroupsRemoved { get; set; }
    public int GroupsSkippedDelete { get; set; }
    public int SubtypesAdded { get; set; }
    public int SubtypesUpdated { get; set; }
    public int SubtypesRemoved { get; set; }
    public int SubtypesSkippedDelete { get; set; }
    public int ControllersAdded { get; set; }
    public int ControllersUpdated { get; set; }
    public int ControllersRemoved { get; set; }
    public int ControllersSkippedDelete { get; set; }
    public int ModificationsAdded { get; set; }
    public int ModificationsUpdated { get; set; }
    /// <summary>Модификация контроллера, удалённая на выгружавшей машине — зеркалится только
    /// эталонной синхронизацией (authoritative=true), см. ImportHierarchyDataCore. В обычной
    /// синхронизации модификации остаются upsert-only, как и всегда.</summary>
    public int ModificationsRemoved { get; set; }
    /// <summary>Модификация, которую эталонный снимок хотел бы удалить, но под тем же контроллером
    /// с тем же hw_version на этой машине ещё жива локальная прошивка/резерв — оставлена (мягкий
    /// FK-предохранитель, см. ImportHierarchyDataCore).</summary>
    public int ModificationsSkippedDelete { get; set; }
    public int ManufacturersAdded { get; set; }
    public int ManufacturersRemoved { get; set; }
    /// <summary>Производитель, которого эталонный снимок хотел бы удалить, но им ещё помечен
    /// локальный файл параметров — оставлен (см. MirrorFlatListDeletions/CollectUsedManufacturers).</summary>
    public int ManufacturersSkippedDelete { get; set; }
    public int TagsAdded { get; set; }
    public int TagsRemoved { get; set; }
    /// <summary>Тег, которого эталонный снимок хотел бы удалить, но им ещё помечена локальная
    /// прошивка/файл параметров — оставлен (см. MirrorFlatListDeletions/CollectUsedTagWords).</summary>
    public int TagsSkippedDelete { get; set; }
    public int ExtensionsAdded { get; set; }
    public int ExtensionsRemoved { get; set; }
    public int ExtensionsSkippedDelete { get; set; }
    /// <summary>Тот же счётчик, что ExtensionsAdded/Removed выше, но для независимого списка
    /// расширений HMI-проектов (allowed_extensions_hmi).</summary>
    public int ExtensionsHmiAdded { get; set; }
    public int ExtensionsHmiRemoved { get; set; }
    public int ExtensionsHmiSkippedDelete { get; set; }
    /// <summary>Тот же счётчик, что ExtensionsAdded/Removed выше, но для третьего независимого
    /// списка — расширений поиска схем (allowed_extensions_schematic).</summary>
    public int ExtensionsSchematicAdded { get; set; }
    public int ExtensionsSchematicRemoved { get; set; }
    public int ExtensionsSchematicSkippedDelete { get; set; }
    public int ReservationsAdded { get; set; }
    public int ReservationsUpdated { get; set; }
    public int FwVersions { get; set; }
    /// <summary>fw_versions rows tombstone-deleted here because the incoming snapshot already had
    /// them marked deleted_at (see TombstoneFwVersion/ImportHierarchyDataCore) — mirrors
    /// SubtypesRemoved/ControllersRemoved above, just for a table that can't use plain absence to
    /// mean "deleted" (fw_versions is additive-only otherwise).</summary>
    public int FwVersionsRemoved { get; set; }
    /// <summary>Строки fw_versions, опознанные по sync_id, у которых разошёлся натуральный ключ, —
    /// то есть версию на машине-источнике переименовали (переписали hw) или переназначили другому
    /// контроллеру, и мы применили это НА МЕСТЕ вместо того, чтобы завести рядом дубликат (см.
    /// ImportHierarchyDataCore). Отдельно от FwVersions: это правка тождества строки, а не её
    /// содержимого, и в отчёте синхронизации её полезно видеть отдельно.</summary>
    public int FwVersionsRenamed { get; set; }
    public int ParamFiles { get; set; }
    /// <summary>Записи файлов параметров, снятые (архивированные) здесь по входящему тумбстоуну либо
    /// вычищенные эталонной синхронизацией как отсутствующие в полном снимке отправителя. Полный
    /// аналог FwVersionsRemoved — до появления sync_id у param_files удаление между машинами не
    /// разъезжалось вовсе.</summary>
    public int ParamFilesRemoved { get; set; }
    /// <summary>Файлы параметров, у которых входящий снимок обновил дату загрузки/описание/теги —
    /// раньше уже совпавшая строка не обновлялась никогда (импорт был строго «только добавлять»).</summary>
    public int ParamFilesUpdated { get; set; }
    /// <summary>Шаблоны паспортов шкафов: заведённые здесь по входящему снимку / снятые по входящему
    /// тумбстоуну (или эталонной синхронизацией) / обновлённые (дата, описание, теги). Полные аналоги
    /// трёх счётчиков ParamFiles* выше — таблица устроена так же.</summary>
    public int Passports { get; set; }
    public int PassportsRemoved { get; set; }
    public int PassportsUpdated { get; set; }
    public int AppUsersAdded { get; set; }
    public int AppUsersUpdated { get; set; }

    /// <summary>Строк fw_versions, которые продвинуло вперёд приехавшее РЕШЕНИЕ МОДЕРАЦИИ с другой
    /// машины (см. ExportedModerationDecision / Database.ApplyModerationDecisions). Считается отдельно
    /// от FwVersions/FwVersionsRemoved: те отражают дифф самих строк снимка, а это — узкий канал
    /// доставки решений, который работает даже когда снимок целиком собран не той машиной, что
    /// приняла решение. Входит в TotalChanges — иначе плашка «Поступили изменения» промолчала бы, и
    /// решение так и не применилось бы (Analyze выходит раньше, когда применять «нечего»).</summary>
    public int ModerationApplied { get; set; }

    /// <summary>Hierarchy rows where BOTH the local copy and the incoming one were edited since they
    /// last agreed — held back, NOT applied, NOT counted in TotalChanges (nothing was actually
    /// changed). See Database.ConflictResolution.cs — the caller checks
    /// Database.PendingHierarchyConflictCount()/GetPendingHierarchyConflicts() after a real Apply()
    /// to find out what's waiting on the operator.</summary>
    public int ConflictsFound { get; set; }

    public int TotalChanges =>
        GroupsAdded + GroupsUpdated + GroupsRemoved + SubtypesAdded + SubtypesUpdated + SubtypesRemoved + ControllersAdded + ControllersUpdated + ControllersRemoved +
        ModificationsAdded + ModificationsUpdated + ModificationsRemoved + ManufacturersAdded + ManufacturersRemoved + TagsAdded + TagsRemoved +
        ExtensionsAdded + ExtensionsRemoved + ExtensionsHmiAdded + ExtensionsHmiRemoved +
        ExtensionsSchematicAdded + ExtensionsSchematicRemoved +
        ReservationsAdded + ReservationsUpdated + FwVersions + FwVersionsRemoved + FwVersionsRenamed +
        ParamFiles + ParamFilesRemoved + ParamFilesUpdated +
        Passports + PassportsRemoved + PassportsUpdated +
        AppUsersAdded + AppUsersUpdated + ModerationApplied;
}
