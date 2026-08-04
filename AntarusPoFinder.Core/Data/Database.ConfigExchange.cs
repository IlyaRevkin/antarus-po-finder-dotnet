using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AntarusPoFinder.Core.Data;

/// <summary>Одна категория предпросмотра разницы эталонной синхронизации — см.
/// <see cref="Database.PreviewAuthoritativeDiff"/>. Added/Removed — уже отсортированные списки имён
/// (не отдельные записи целиком: для предпросмотра ПЕРЕД отправкой важно только «что появится/
/// исчезнет по имени», а не полное содержимое строки).</summary>
public record AuthoritativeDiffCategory(string Label, List<string> Added, List<string> Removed)
{
    public bool HasChanges => Added.Count > 0 || Removed.Count > 0;
}

/// <summary>Результат Database.PreviewAuthoritativeDiff — показывается в AuthoritativeDiffDialog
/// администратору ПЕРЕД «Сделать это состояние эталонным для всех» (см. NetworkSyncView.
/// PushAuthoritative_Click), чтобы решение принималось не вслепую.</summary>
public record AuthoritativeSyncDiff(List<AuthoritativeDiffCategory> Categories)
{
    public int TotalAdded => Categories.Sum(c => c.Added.Count);
    public int TotalRemoved => Categories.Sum(c => c.Removed.Count);
    public bool HasChanges => Categories.Any(c => c.HasChanges);
}

public partial class Database
{
    /// <summary>Задел (Задача 7) — «сохранить у себя, не выгружать»: помечает строку fw_versions
    /// так, что ExportHierarchyData её больше не включает в общий конфиг (см. WHERE fv.is_local_only
    /// в запросе ниже). Нет своего UI-переключателя ещё (минимум по задаче — только схема и фильтр
    /// экспорта), но метод уже готов для будущей галочки в UploadView.</summary>
    public void SetFwVersionLocalOnly(int fwVersionId, bool localOnly) =>
        ExecuteNonQuery("UPDATE fw_versions SET is_local_only=@v WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@v", localOnly ? 1 : 0);
            cmd.Parameters.AddWithValue("@id", fwVersionId);
        });

    public HierarchyExportData ExportHierarchyData()
    {
        var data = new HierarchyExportData();

        using (var r = ExecuteReader("SELECT name, prefix, sort_order, sync_id, updated_at FROM equipment_groups ORDER BY sort_order"))
            while (r.Read())
                data.EquipmentGroups.Add(new ExportedGroup { Name = r.GetString(0), Prefix = r.GetInt32(1), SortOrder = r.GetInt32(2), SyncId = GetString(r, "sync_id"), UpdatedAt = GetString(r, "updated_at") });

        using (var r = ExecuteReader("""
            SELECT es.name, es.prefix, es.folder_name, es.sort_order, es.sync_id, es.updated_at, eg.name AS group_name, eg.sync_id AS group_sync_id
            FROM equipment_subtypes es JOIN equipment_groups eg ON es.group_id = eg.id
            ORDER BY es.sort_order
            """))
            while (r.Read())
                data.EquipmentSubtypes.Add(new ExportedSubType
                {
                    Name = r.GetString(0), Prefix = r.GetInt32(1), FolderName = r.GetString(2),
                    SortOrder = r.GetInt32(3), SyncId = GetString(r, "sync_id"), UpdatedAt = GetString(r, "updated_at"),
                    GroupName = GetString(r, "group_name"), GroupSyncId = GetString(r, "group_sync_id"),
                });

        using (var r = ExecuteReader("SELECT name, prefix, sort_order, sync_id, updated_at FROM controller_models ORDER BY sort_order"))
            while (r.Read())
                data.ControllerModels.Add(new ExportedController { Name = r.GetString(0), Prefix = r.GetInt32(1), SortOrder = r.GetInt32(2), SyncId = GetString(r, "sync_id"), UpdatedAt = GetString(r, "updated_at") });

        using (var r = ExecuteReader("""
            SELECT cm.display_name, cm.hw_version, cm.sort_order, cm.description, cm.sync_id, cm.updated_at,
                   c.name AS controller_name, c.sync_id AS controller_sync_id
            FROM controller_modifications cm JOIN controller_models c ON cm.controller_id = c.id
            ORDER BY c.sort_order, cm.sort_order
            """))
            while (r.Read())
                data.ControllerModifications.Add(new ExportedModification
                {
                    DisplayName = GetString(r, "display_name"), HwVersion = GetInt(r, "hw_version"),
                    SortOrder = GetInt(r, "sort_order"), Description = GetString(r, "description"),
                    SyncId = GetString(r, "sync_id"), UpdatedAt = GetString(r, "updated_at"), ControllerName = GetString(r, "controller_name"),
                    ControllerSyncId = GetString(r, "controller_sync_id"),
                });

        data.ParamManufacturers = new();
        using (var r = ExecuteReader("SELECT name, sort_order FROM param_manufacturers ORDER BY sort_order, name"))
            while (r.Read())
                data.ParamManufacturers.Add(new ExportedManufacturer { Name = r.GetString(0), SortOrder = r.GetInt32(1) });

        data.Tags = GetAllTags();
        data.AllowedExtensions = GetAllowedExtensions();
        data.AllowedExtensionsHmi = GetAllowedExtensionsHmi();
        data.AllowedExtensionsSchematic = GetAllowedExtensionsSchematic();
        data.FlatListState = GetFlatListState()
            .Select(s => new ExportedFlatListState { Kind = s.Kind, Name = s.Name, DeletedAt = s.DeletedAt, RevivedAt = s.RevivedAt })
            .ToList();

        using (var r = ExecuteReader("""
            SELECT res.hw_version, res.version_raw, res.status, res.reserved_by, res.reserved_at,
                   es.name AS subtype_name, es.sync_id AS subtype_sync_id,
                   cm.name AS ctrl_name, cm.sync_id AS controller_sync_id
            FROM fw_version_reservations res
            JOIN equipment_subtypes es ON res.subtype_id   = es.id
            JOIN controller_models  cm ON res.controller_id = cm.id
            WHERE res.status = 'reserved'
            ORDER BY res.reserved_at
            """))
            while (r.Read())
                data.Reservations.Add(new ExportedReservation
                {
                    HwVersion = GetInt(r, "hw_version"), VersionRaw = GetString(r, "version_raw"),
                    Status = GetString(r, "status"), ReservedBy = GetString(r, "reserved_by"), ReservedAt = GetString(r, "reserved_at"),
                    SubtypeName = GetString(r, "subtype_name"), SubtypeSyncId = GetString(r, "subtype_sync_id"),
                    ControllerName = GetString(r, "ctrl_name"), ControllerSyncId = GetString(r, "controller_sync_id"),
                });

        using (var r = ExecuteReader("""
            SELECT fv.version_raw, fv.hw_version, fv.sw_version, fv.eq_prefix, fv.sub_prefix,
                   fv.dt_str, fv.filename, fv.disk_path, fv.local_path, fv.description,
                   fv.changelog, fv.launch_types, fv.io_map_path, fv.instructions_path,
                   fv.is_opc, fv.request_num, fv.upload_date, fv.archived, fv.tags,
                   fv.status, fv.released, fv.hmi_path, fv.executable_hint, fv.hmi_executable_hint,
                   fv.modbus_map_path, fv.deleted_at, fv.sync_id, fv.config_name,
                   eg.name AS group_name, es.name AS subtype_name, es.sync_id AS subtype_sync_id,
                   cm.name AS ctrl_name, cm.sync_id AS controller_sync_id
            FROM fw_versions fv
            JOIN equipment_subtypes es ON fv.subtype_id  = es.id
            JOIN equipment_groups   eg ON es.group_id    = eg.id
            JOIN controller_models  cm ON fv.controller_id = cm.id
            WHERE fv.is_local_only = 0
            ORDER BY fv.id
            """))
            while (r.Read())
                data.FwVersions.Add(new ExportedFwVersion
                {
                    VersionRaw = r.GetString(0), HwVersion = r.GetInt32(1), SwVersion = r.GetInt32(2),
                    EqPrefix = r.GetInt32(3), SubPrefix = r.GetInt32(4), DtStr = r.GetString(5),
                    Filename = r.GetString(6), DiskPath = r.GetString(7), LocalPath = r.GetString(8),
                    Description = r.GetString(9), Changelog = r.GetString(10), LaunchTypes = r.GetString(11),
                    IoMapPath = r.GetString(12), InstructionsPath = r.GetString(13), IsOpc = r.GetInt32(14),
                    RequestNum = r.GetString(15), UploadDate = r.GetString(16), Archived = r.GetInt32(17),
                    Tags = r.GetString(18), Status = GetString(r, "status"), Released = GetInt(r, "released"),
                    HmiPath = GetString(r, "hmi_path"), ExecutableHint = GetString(r, "executable_hint"),
                    HmiExecutableHint = GetString(r, "hmi_executable_hint"), ModbusMapPath = GetString(r, "modbus_map_path"),
                    DeletedAt = GetString(r, "deleted_at"), SyncId = GetString(r, "sync_id"),
                    ConfigName = GetString(r, "config_name"),
                    GroupName = GetString(r, "group_name"),
                    SubtypeName = GetString(r, "subtype_name"), SubtypeSyncId = GetString(r, "subtype_sync_id"),
                    CtrlName = GetString(r, "ctrl_name"), ControllerSyncId = GetString(r, "controller_sync_id"),
                });

        // Файлы параметров выгружаются ЦЕЛИКОМ, вместе с архивными (archived=1) — они и есть
        // тумбстоуны удаления. Раньше здесь стоял «WHERE pf.archived = 0», и снятая запись просто
        // исчезала из снимка; для аддитивного импорта «исчезла» неотличимо от «эта машина о ней ещё
        // не знает», поэтому удаление никогда не доезжало до коллег (жалоба «у меня 2 записи, у
        // коллеги 4, и все не те»). Ровно та же логика, что у fw_versions.deleted_at выше.
        data.ParamFilesHaveSync = true;
        using (var r = ExecuteReader("""
            SELECT pf.filename, pf.disk_path, pf.description, pf.upload_date, pf.archived, pf.manufacturer,
                   pf.sync_id, pf.tags,
                   es.name AS subtype_name, es.sync_id AS subtype_sync_id, eg.name AS group_name
            FROM param_files pf
            JOIN equipment_subtypes es ON pf.subtype_id = es.id
            JOIN equipment_groups   eg ON es.group_id   = eg.id
            ORDER BY pf.id
            """))
            while (r.Read())
                data.ParamFiles.Add(new ExportedParamFile
                {
                    Filename = r.GetString(0), DiskPath = r.GetString(1), Description = r.GetString(2),
                    UploadDate = r.GetString(3), Archived = r.GetInt32(4), Manufacturer = r.GetString(5),
                    SyncId = GetString(r, "sync_id"), Tags = GetString(r, "tags"),
                    SubtypeName = GetString(r, "subtype_name"), SubtypeSyncId = GetString(r, "subtype_sync_id"),
                    GroupName = GetString(r, "group_name"),
                });

        // Паспорта шкафов — тоже ЦЕЛИКОМ, вместе с архивными: та же логика тумбстоунов, что у файлов
        // параметров выше (см. ExportedPassport).
        data.Passports = new List<ExportedPassport>();
        using (var r = ExecuteReader("""
            SELECT p.name, p.filename, p.disk_path, p.description, p.upload_date, p.archived, p.sync_id, p.tags,
                   es.name AS subtype_name, es.sync_id AS subtype_sync_id, eg.name AS group_name
            FROM passports p
            JOIN equipment_subtypes es ON p.subtype_id = es.id
            JOIN equipment_groups   eg ON es.group_id   = eg.id
            ORDER BY p.id
            """))
            while (r.Read())
                data.Passports.Add(new ExportedPassport
                {
                    Name = r.GetString(0), Filename = r.GetString(1), DiskPath = r.GetString(2),
                    Description = r.GetString(3), UploadDate = r.GetString(4), Archived = r.GetInt32(5),
                    SyncId = GetString(r, "sync_id"), Tags = GetString(r, "tags"),
                    SubtypeName = GetString(r, "subtype_name"), SubtypeSyncId = GetString(r, "subtype_sync_id"),
                    GroupName = GetString(r, "group_name"),
                });

        using (var r = ExecuteReader("SELECT sync_id, ad_login, role, first_login_at, last_login_at, role_updated_at FROM app_users ORDER BY ad_login"))
            while (r.Read())
                data.AppUsers.Add(new ExportedAppUser
                {
                    SyncId = GetString(r, "sync_id"), AdLogin = r.GetString(1), Role = GetString(r, "role", "naladchik"),
                    FirstLoginAt = GetString(r, "first_login_at"), LastLoginAt = GetString(r, "last_login_at"),
                    RoleUpdatedAt = GetString(r, "role_updated_at"),
                });

        // Статистика выборов прошивки — вклад этой машины и всё, что она знает о чужих (см.
        // Database.FwUsage.cs). Общий конфиг переписывается целиком, поэтому чужие вклады
        // пересылаются дальше: выгрузив только своё, машина стёрла бы в снимке остальные.
        // Свой ручной вес уезжает только если машина включила «делиться весом» (fw_weight_shared) —
        // читаем настройку прямо из БД (сырой ключ с дефолтом false), т.к. сюда ConfigService не
        // передаётся. Чужой вес ExportFwUsage пересылает независимо от флага.
        var shareOwnWeight = GetSetting("fw_weight_shared", "false") == "true";
        data.FwUsage = ExportFwUsage(UsageOriginId(), shareOwnWeight)
            .Select(u => new ExportedFwUsage
            {
                Origin = u.Origin, QueryKey = u.QueryKey, SubtypeSyncId = u.SubtypeSyncId,
                ControllerSyncId = u.ControllerSyncId, VersionRaw = u.VersionRaw,
                Uses = u.Uses, LastUsedAt = u.LastUsedAt, Weight = u.Weight,
            })
            .ToList();

        // Явные переписывания hw модификаций (см. ExportedHwRewrite / Database.HwRewriteLog.cs) —
        // журнал последних операций, чтобы приёмник проиграл ещё не применённое как переименование,
        // а не завёл дубликаты из-за смены version_raw.
        data.HwRewrites = GetRecentHwRewrites();

        // Решения модерации (см. ExportedModerationDecision / Database.ModerationLog.cs). В журнале
        // лежат и СВОИ решения, и принятые с чужих машин (AbsorbModerationDecisions на приёме) —
        // поэтому полный экспорт пересылает их дальше, а не стирает чужие, как было бы, выгружай он
        // только своё. Та же логика, что у fw_usage выше.
        data.ModerationDecisions = GetRecentModerationDecisions();

        // Переносы версий на другую модель контроллера — см. ExportedCtrlReassign.
        data.CtrlReassignments = GetRecentCtrlReassigns();

        return data;
    }

    /// <summary>Computes what an import WOULD do without writing anything — powers the config-update
    /// banner's "Подробно" view so the operator can see who changed what before committing.</summary>
    public ImportCounts PreviewImportHierarchyData(HierarchyExportData data, bool authoritative = false) => ImportHierarchyDataCore(data, apply: false, authoritative);

    /// <summary>Applies the import for real. Catalog tables (groups/subtypes/controllers/
    /// modifications) are matched by SyncId first (falls back to name for older exports/first
    /// contact) and updated IN PLACE — deleting and re-inserting a "renamed" row would silently
    /// orphan or (with foreign_keys=ON) outright fail against any locally-uploaded firmware under
    /// it. Groups/subtypes/controllers ALSO get their deletion mirrored UNCONDITIONALLY (see the
    /// three dedicated blocks below, each guarded by "nothing local still references this row") —
    /// without that, a row deleted on one machine would resurrect the moment any sync partner (or a
    /// stale JSON on a shared drive) still listed it; this is exactly what kept bringing the FORTUS
    /// controller back. This already means an absent row is removed even if the receiving machine
    /// had it and the exporting one never did — i.e. these three are effectively always "authoritative"
    /// for their own table, regardless of the <paramref name="authoritative"/> flag below.
    ///
    /// Everything else historically stayed additive-only against plain absence (see
    /// <paramref name="authoritative"/> below for what changes that): modifications had no deletion
    /// mirror at all (nothing literally FK-references a modification row — fw_versions/reservations
    /// match a modification by VALUE, controller_id+hw_version, not by id — so a stray one just sat
    /// there forever), and tags/allowed_extensions/manufacturers use the LWW timestamp scheme in
    /// ImportFlatList (a name absent from an incoming snapshot means "the other side doesn't know
    /// about it yet", not "delete it" — see that method's own doc for why blind mirroring there used
    /// to eat freshly-added entries). fw_versions/param_files themselves stay additive-only always —
    /// each install may have uploads the exporting machine never saw. fw_versions is the one
    /// exception within that: an explicit deletion (Database.TombstoneFwVersion) DOES mirror, via a
    /// deleted_at tombstone kept on the row instead of a bare DELETE — see the dedicated block below.
    ///
    /// <paramref name="authoritative"/> (Эталонная синхронизация) — true when the incoming snapshot
    /// was pushed via NetworkSyncView's "Сделать это состояние эталонным для всех" (see
    /// ConfigSyncService.PrepareExport/SharedConfigSnapshot.Authoritative). Extends the SAME
    /// full-mirror-with-FK-guard treatment groups/subtypes/controllers already always get to the
    /// remaining catalog entities — controller_modifications, param_manufacturers, tags,
    /// allowed_extensions, allowed_extensions_hmi, allowed_extensions_schematic — for exactly this
    /// one import. This is what closes the gap those tables didn't have: a junk catalog row that originated on some OTHER
    /// machine, which the "authoritative" machine never had in the first place, has nothing to
    /// tombstone against — plain absence from the admin's snapshot is the only signal there ever is.
    /// When false (the default — every existing caller), behavior is byte-for-byte what it was
    /// before this parameter existed.</summary>
    public ImportCounts ImportHierarchyData(HierarchyExportData data, bool authoritative = false) => ImportHierarchyDataCore(data, apply: true, authoritative);

    /// <summary>Задача 1 (эталонная синхронизация) — предпросмотр разницы ПЕРЕД отправкой: что
    /// добавится/удалится по каждой из восьми справочных категорий, если <paramref name="local"/>
    /// (свежий ExportHierarchyData этой машины — то, что уйдёт в эталон) станет полной заменой того,
    /// что прямо сейчас лежит в общем конфиге на диске (<paramref name="onDisk"/> — снимок,
    /// прочитанный и разобранный ConfigSyncService.ReadCurrentDiskHierarchyAsync). Администратор не
    /// видит чужие базы данных, поэтому и это сравнение — не то же самое, что реально произойдёт на
    /// каждой конкретной принимающей машине (там ещё сработает мягкий FK-предохранитель из
    /// ImportHierarchyDataCore/MirrorFlatListDeletions, которого здесь намеренно нет — эта машина не
    /// знает, что ещё используется на чужих машинах); это лучшее доступное приближение — диск как
    /// прокси для «того, что сейчас применяют получатели».
    ///
    /// Чисто по именам (без sync_id) — предпросмотр должен быть понятен человеку, читающему список
    /// перед необратимой отправкой, а не только машине; сравнение регистронезависимое, как и
    /// остальные плоские списки-справочники ниже (ImportFlatList/MirrorFlatListDeletions).</summary>
    public static AuthoritativeSyncDiff PreviewAuthoritativeDiff(HierarchyExportData local, HierarchyExportData onDisk)
    {
        var categories = new List<AuthoritativeDiffCategory>
        {
            DiffCategory("Типы шкафов",
                local.EquipmentGroups.Select(g => g.Name),
                onDisk.EquipmentGroups.Select(g => g.Name)),
            DiffCategory("Подтипы",
                local.EquipmentSubtypes.Select(s => $"{s.GroupName} / {s.Name}"),
                onDisk.EquipmentSubtypes.Select(s => $"{s.GroupName} / {s.Name}")),
            DiffCategory("Контроллеры",
                local.ControllerModels.Select(c => c.Name),
                onDisk.ControllerModels.Select(c => c.Name)),
            DiffCategory("Модификации контроллеров",
                local.ControllerModifications.Select(m => $"{m.ControllerName} / {m.DisplayName}"),
                onDisk.ControllerModifications.Select(m => $"{m.ControllerName} / {m.DisplayName}")),
            DiffCategory("Производители ПЧ/УПП",
                (local.ParamManufacturers ?? new()).Select(m => m.Name),
                (onDisk.ParamManufacturers ?? new()).Select(m => m.Name)),
            DiffCategory("Теги",
                local.Tags ?? new(),
                onDisk.Tags ?? new()),
            DiffCategory("Разрешённые расширения",
                local.AllowedExtensions ?? new(),
                onDisk.AllowedExtensions ?? new()),
            DiffCategory("Разрешённые расширения HMI",
                local.AllowedExtensionsHmi ?? new(),
                onDisk.AllowedExtensionsHmi ?? new()),
            DiffCategory("Разрешённые расширения поиска схем",
                local.AllowedExtensionsSchematic ?? new(),
                onDisk.AllowedExtensionsSchematic ?? new()),
            // Файлы параметров — единственная категория здесь, которая относится не к справочнику, а
            // к данным: эталонный снимок теперь снимает у получателей записи параметров, которых в
            // нём нет (см. ImportParamFiles). Операция обратимая (запись архивируется, файл на диске
            // остаётся), но человек обязан видеть список ДО отправки, а не узнавать постфактум.
            // Только живые записи: архивные — это уже принятые всеми тумбстоуны, показывать их как
            // «исчезнет» нечестно.
            DiffCategory("Файлы параметров",
                local.ParamFiles.Where(p => p.Archived == 0).Select(ParamFileDiffLabel),
                onDisk.ParamFiles.Where(p => p.Archived == 0).Select(ParamFileDiffLabel)),
            // Паспорта шкафов — вторая такая категория «данных, а не справочника», и по той же
            // причине: эталонный снимок снимает у получателей паспорта, которых в нём нет (см.
            // ImportPassports). Снимок старого клиента паспортов не содержит вовсе (Passports ==
            // null) — тогда категория пуста и никого не пугает.
            DiffCategory("Паспорта шкафов",
                (local.Passports ?? new()).Where(p => p.Archived == 0).Select(PassportDiffLabel),
                (onDisk.Passports ?? new()).Where(p => p.Archived == 0).Select(PassportDiffLabel)),
        };
        return new AuthoritativeSyncDiff(categories);
    }

    /// <summary>Человекочитаемый адрес файла параметров для предпросмотра эталонной синхронизации:
    /// одного имени файла мало (одноимённые файлы под разными подтипами/производителями — норма).</summary>
    private static string ParamFileDiffLabel(ExportedParamFile p) =>
        $"{p.GroupName} / {p.SubtypeName} / {p.Manufacturer} / {p.Filename}";

    /// <summary>То же для паспорта шкафа: адресуется типом/подтипом и названием — имя файла у всех
    /// паспортов подряд бывает одинаковым («Паспорт.docx»), по нему одному не разобрать, какой из
    /// них исчезнет.</summary>
    private static string PassportDiffLabel(ExportedPassport p) =>
        $"{p.GroupName} / {p.SubtypeName} / {p.Name}";

    /// <summary>Одна категория PreviewAuthoritativeDiff — множественная разница по строковым именам,
    /// регистронезависимо и с обрезкой пробелов, как и везде в плоских списках-справочниках этого
    /// файла. Added/Removed отсортированы для стабильного, предсказуемого порядка в UI.</summary>
    private static AuthoritativeDiffCategory DiffCategory(string label, IEnumerable<string> localNames, IEnumerable<string> diskNames)
    {
        var local = new HashSet<string>(localNames.Select(n => n.Trim()).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);
        var disk = new HashSet<string>(diskNames.Select(n => n.Trim()).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);

        var added = local.Where(n => !disk.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = disk.Where(n => !local.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return new AuthoritativeDiffCategory(label, added, removed);
    }

    /// <summary>Согласует один плоский список-справочник (производители/теги/расширения) по
    /// отметкам времени из flat_list_state вместо прежнего слепого зеркала (см. Database.FlatLists.cs
    /// о том, почему зеркало теряло только что добавленные записи).
    ///
    /// Правила ровно два:
    ///   • по каждому имени, о котором у входящей стороны есть отметка СВЕЖЕЕ нашей, применяем её
    ///     состояние — добавляем или удаляем имя и запоминаем чужие отметки как свои;
    ///   • имя, встреченное в списке, но без отметок ни у кого, просто добавляем, если его нет —
    ///     кроме случая, когда локально оно осознанно удалено (локальная отметка это помнит).
    /// Обратного правила «чего нет в чужом списке — удалить» больше нет: отсутствие имени у
    /// собеседника означает лишь то, что он о нём не знает, а не то, что его удалили.
    ///
    /// addLocal/removeLocal — сырые операции над самой таблицей списка. Публичные Add*/Delete*
    /// сами проставляют отметку «сейчас», поэтому чужие отметки записываются ПОСЛЕ вызова, затирая
    /// её; иначе применённое чужое удаление выглядело бы как наше собственное, только что
    /// сделанное, и поехало бы обратно уже как более свежее событие.</summary>
    private void ImportFlatList(string kind, IEnumerable<string> incomingNames, List<ExportedFlatListState>? incomingState,
        bool apply, Func<List<string>> getLocalNames, Action<string> addLocal, Action<string> removeLocal,
        Action countAdded, Action countRemoved)
    {
        var localNames = new HashSet<string>(getLocalNames(), StringComparer.OrdinalIgnoreCase);
        var localState = GetFlatListState()
            .Where(s => s.Kind == kind)
            .ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);

        var states = (incomingState ?? new()).Where(s => s.Kind == kind).ToList();
        foreach (var s in states)
        {
            var name = s.Name.Trim();
            if (name.Length == 0) continue;

            var incomingLast = string.CompareOrdinal(s.RevivedAt, s.DeletedAt) >= 0 ? s.RevivedAt : s.DeletedAt;
            var localLast = localState.TryGetValue(name, out var local) ? local.LastEventAt : "";
            if (string.CompareOrdinal(incomingLast, localLast) <= 0) continue;

            var incomingAlive = string.CompareOrdinal(s.RevivedAt, s.DeletedAt) >= 0;
            if (incomingAlive && !localNames.Contains(name))
            {
                countAdded();
                if (apply) { addLocal(name); localNames.Add(name); }
            }
            else if (!incomingAlive && localNames.Contains(name))
            {
                countRemoved();
                if (apply) { removeLocal(name); localNames.Remove(name); }
            }
            if (apply) SetFlatListState(kind, name, s.DeletedAt, s.RevivedAt);
        }

        var withState = new HashSet<string>(states.Select(s => s.Name.Trim()), StringComparer.OrdinalIgnoreCase);
        foreach (var raw in incomingNames)
        {
            var name = raw.Trim();
            if (name.Length == 0 || withState.Contains(name) || localNames.Contains(name)) continue;
            if (localState.TryGetValue(name, out var known) && !known.IsAlive) continue;

            countAdded();
            if (apply) { addLocal(name); localNames.Add(name); }
        }
    }

    /// <summary>Эталонная синхронизация (authoritative=true) — дополнительный проход поверх
    /// ImportFlatList: удаляет локальную запись плоского списка, которой нет во входящем ПОЛНОМ
    /// снимке вообще (ни живой, ни с отметкой удаления) — см. вызов в ImportHierarchyDataCore для
    /// того, почему обычный ImportFlatList намеренно этого не делает. isUsedLocally — необязательный
    /// «мягкий» предохранитель (null — не нужен, как у allowed_extensions).
    ///
    /// <paramref name="incomingStateNames"/> — имена из data.FlatListState для ЭТОГО kind (живые И
    /// удалённые, см. вызовы ниже) — БАГ-ФИКС: раньше сюда передавали только incomingNames (живой
    /// список), из-за чего имя с явной отметкой УДАЛЕНИЯ (значит уже полностью решённое чуть выше, в
    /// ImportFlatList, по LWW-таймстемпам) выглядело как «входящей стороне вообще неизвестно» и
    /// попадало под этот, более грубый, проход ВТОРОЙ раз. В preview (apply=false) это удваивало
    /// TagsRemoved/ManufacturersRemoved и т.п. для одного и того же имени (ImportFlatList уже
    /// посчитал его как удаление, потом этот проход считал снова). В реальном apply опаснее: если
    /// ImportFlatList сознательно ОСТАВИЛ имя как есть, потому что ЛОКАЛЬНАЯ отметка (например,
    /// более свежее возвращение) новее входящей отметки удаления — этот проход, не зная о состоянии
    /// вообще, всё равно удалял бы его, стирая то самое более свежее локальное решение, которое LWW-
    /// merge выше специально сохранил. Имя с любой отметкой уже полностью в ведении ImportFlatList —
    /// трогать его здесь второй раз нельзя; сюда должны попадать только имена, у которых нет вообще
    /// никакого следа во входящем снимке (ни в живом списке, ни в flat_list_state).</summary>
    private void MirrorFlatListDeletions(Func<List<string>> getLocalNames, Action<string> removeLocal,
        IEnumerable<string> incomingNames, IEnumerable<string> incomingStateNames, Func<string, bool>? isUsedLocally, bool apply,
        Action countRemoved, Action countSkipped)
    {
        var incoming = new HashSet<string>(
            incomingNames.Concat(incomingStateNames).Select(n => n.Trim()).Where(n => n.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        foreach (var name in getLocalNames())
        {
            if (incoming.Contains(name)) continue;
            if (isUsedLocally is not null && isUsedLocally(name))
            {
                countSkipped();
                continue;
            }

            countRemoved();
            if (apply) removeLocal(name);
        }
    }

    /// <summary>Слова-теги, ещё встречающиеся в fw_versions.tags/param_files.tags этой машины —
    /// «мягкий» FK-предохранитель для MirrorFlatListDeletions(тегов): удалить справочную запись тега,
    /// пока прошивка/файл параметров с этим тегом ещё локально существует, значит молча потерять то,
    /// чем он был помечен — DeleteTag сам по себе БЫ вычистил тег из этих строк (см. ReplaceTagInColumn
    /// в Database.Tags.cs), а цель эталонной синхронизации — не трогать данные вовсе, только справочник.</summary>
    private HashSet<string> CollectUsedTagWords()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(string table)
        {
            using var r = ExecuteReader($"SELECT tags FROM {table} WHERE tags IS NOT NULL AND tags != ''");
            while (r.Read())
                foreach (var w in Services.TagString.Parse(r.GetString(0)))
                    used.Add(w);
        }
        Collect("fw_versions");
        Collect("param_files");
        Collect("passports");
        return used;
    }

    /// <summary>Тот же предохранитель, что CollectUsedTagWords, только для производителей ПЧ/УПП —
    /// param_files.manufacturer хранит точное имя строкой, без разбора на слова.</summary>
    private HashSet<string> CollectUsedManufacturers()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var r = ExecuteReader("SELECT DISTINCT manufacturer FROM param_files WHERE manufacturer IS NOT NULL AND manufacturer != ''");
        while (r.Read())
            used.Add(r.GetString(0));
        return used;
    }

    private ImportCounts ImportHierarchyDataCore(HierarchyExportData data, bool apply, bool authoritative = false)
    {
        var counts = new ImportCounts();
        // Captured ONCE per import pass, not per-row — every row that successfully reconciles in
        // THIS pass (inserted, updated, or confirmed-already-matching) shares the same watermark,
        // representing "as of this sync, both sides are known to agree on this row". See
        // ClassifyHierarchyChange for how it's used to tell a genuine two-sided edit apart from a
        // normal one-sided update. Never touched when apply is false (preview must be side-effect-free).
        var syncNow = NowIso();

        // ── Groups (upsert by sync_id, fallback to name) ────────────────────────
        var groupSyncToId = new Dictionary<string, int>();
        foreach (var g in data.EquipmentGroups)
        {
            var existing = FindBySyncOrName("equipment_groups", g.SyncId, "name", g.Name);
            if (existing is null)
            {
                counts.GroupsAdded++;
                if (apply)
                {
                    var sync = string.IsNullOrEmpty(g.SyncId) ? Guid.NewGuid().ToString() : g.SyncId;
                    var updatedAt = string.IsNullOrEmpty(g.UpdatedAt) ? syncNow : g.UpdatedAt;
                    ExecuteNonQuery("INSERT INTO equipment_groups(name,prefix,sort_order,sync_id,updated_at) VALUES(@n,@p,@s,@sy,@u)", cmd =>
                    {
                        cmd.Parameters.AddWithValue("@n", g.Name); cmd.Parameters.AddWithValue("@p", g.Prefix);
                        cmd.Parameters.AddWithValue("@s", g.SortOrder); cmd.Parameters.AddWithValue("@sy", sync);
                        cmd.Parameters.AddWithValue("@u", updatedAt);
                    });
                    if (!string.IsNullOrEmpty(g.SyncId)) groupSyncToId[g.SyncId] = Convert.ToInt32(ExecuteScalar("SELECT last_insert_rowid()"));
                    SetHierarchyWatermark(sync, syncNow);
                }
                continue;
            }
            var (id, name, prefix, sort, localSyncId, localUpdatedAt) = existing.Value;
            if (!string.IsNullOrEmpty(g.SyncId)) groupSyncToId[g.SyncId] = id;
            // First contact between two independently-seeded databases: this row matched by NAME,
            // not sync_id (their sync_ids started out different, generated independently). Adopt
            // the incoming sync_id now so a FUTURE rename can correlate via sync_id instead of name.
            var adoptSyncId = !string.IsNullOrEmpty(g.SyncId) && g.SyncId != localSyncId;
            var effectiveSyncId = adoptSyncId ? g.SyncId : localSyncId;
            if (name != g.Name || prefix != g.Prefix || sort != g.SortOrder)
            {
                var (conflict, applyIncoming) = ClassifyHierarchyChange(effectiveSyncId, localUpdatedAt, g.UpdatedAt);
                if (conflict)
                {
                    counts.ConflictsFound++;
                    if (apply)
                        RecordPendingConflict("group", effectiveSyncId, id, $"Группа «{name}»",
                            JsonSerializer.Serialize(new ExportedGroup { SyncId = effectiveSyncId, Name = name, Prefix = prefix, SortOrder = sort, UpdatedAt = localUpdatedAt }),
                            JsonSerializer.Serialize(g));
                }
                else if (applyIncoming)
                {
                    counts.GroupsUpdated++;
                    if (apply)
                    {
                        ExecuteNonQuery("UPDATE equipment_groups SET name=@n, prefix=@p, sort_order=@s, sync_id=@sy, updated_at=@u WHERE id=@id", cmd =>
                        {
                            cmd.Parameters.AddWithValue("@n", g.Name); cmd.Parameters.AddWithValue("@p", g.Prefix);
                            cmd.Parameters.AddWithValue("@s", g.SortOrder); cmd.Parameters.AddWithValue("@id", id);
                            cmd.Parameters.AddWithValue("@sy", effectiveSyncId);
                            cmd.Parameters.AddWithValue("@u", string.IsNullOrEmpty(g.UpdatedAt) ? syncNow : g.UpdatedAt);
                        });
                        SetHierarchyWatermark(effectiveSyncId, syncNow);
                    }
                }
                else if (apply)
                {
                    // Local wins (unchanged, or newer than the incoming snapshot) — nothing to write
                    // to the row itself, but the sync_id may still need adopting, and the watermark
                    // still advances (both sides were compared just now, even though local kept its
                    // own value).
                    if (adoptSyncId)
                        ExecuteNonQuery("UPDATE equipment_groups SET sync_id=@sy WHERE id=@id", cmd =>
                        { cmd.Parameters.AddWithValue("@sy", g.SyncId); cmd.Parameters.AddWithValue("@id", id); });
                    SetHierarchyWatermark(effectiveSyncId, syncNow);
                }
            }
            else
            {
                if (adoptSyncId && apply)
                    ExecuteNonQuery("UPDATE equipment_groups SET sync_id=@sy WHERE id=@id", cmd =>
                    { cmd.Parameters.AddWithValue("@sy", g.SyncId); cmd.Parameters.AddWithValue("@id", id); });
                if (apply) SetHierarchyWatermark(effectiveSyncId, syncNow);
            }
        }

        // ── Subtypes (upsert by sync_id, fallback to (group,name)) ──────────────
        var subtypeSyncToId = new Dictionary<string, int>();
        foreach (var s in data.EquipmentSubtypes)
        {
            var groupId = ResolveId("equipment_groups", s.GroupSyncId, groupSyncToId, "name", s.GroupName);
            if (groupId is null) continue;

            var existing = FindSubtype(s.SyncId, groupId.Value, s.Name);
            if (existing is null)
            {
                counts.SubtypesAdded++;
                if (apply)
                {
                    var sync = string.IsNullOrEmpty(s.SyncId) ? Guid.NewGuid().ToString() : s.SyncId;
                    var updatedAt = string.IsNullOrEmpty(s.UpdatedAt) ? syncNow : s.UpdatedAt;
                    ExecuteNonQuery("INSERT INTO equipment_subtypes(group_id,name,prefix,folder_name,sort_order,sync_id,updated_at) VALUES(@g,@n,@p,@f,@s,@sy,@u)", cmd =>
                    {
                        cmd.Parameters.AddWithValue("@g", groupId.Value); cmd.Parameters.AddWithValue("@n", s.Name);
                        cmd.Parameters.AddWithValue("@p", s.Prefix); cmd.Parameters.AddWithValue("@f", string.IsNullOrEmpty(s.FolderName) ? s.Name : s.FolderName);
                        cmd.Parameters.AddWithValue("@s", s.SortOrder); cmd.Parameters.AddWithValue("@sy", sync);
                        cmd.Parameters.AddWithValue("@u", updatedAt);
                    });
                    if (!string.IsNullOrEmpty(s.SyncId)) subtypeSyncToId[s.SyncId] = Convert.ToInt32(ExecuteScalar("SELECT last_insert_rowid()"));
                    SetHierarchyWatermark(sync, syncNow);
                }
                continue;
            }
            var (id, name, prefix, folder, sort, localSyncId, localUpdatedAt) = existing.Value;
            if (!string.IsNullOrEmpty(s.SyncId)) subtypeSyncToId[s.SyncId] = id;
            var wantFolder = string.IsNullOrEmpty(s.FolderName) ? s.Name : s.FolderName;
            var adoptSyncId = !string.IsNullOrEmpty(s.SyncId) && s.SyncId != localSyncId;
            var effectiveSyncId = adoptSyncId ? s.SyncId : localSyncId;
            if (name != s.Name || prefix != s.Prefix || folder != wantFolder || sort != s.SortOrder)
            {
                var (conflict, applyIncoming) = ClassifyHierarchyChange(effectiveSyncId, localUpdatedAt, s.UpdatedAt);
                if (conflict)
                {
                    counts.ConflictsFound++;
                    if (apply)
                        RecordPendingConflict("subtype", effectiveSyncId, id, $"Подтип «{name}»",
                            JsonSerializer.Serialize(new ExportedSubType
                            {
                                SyncId = effectiveSyncId, Name = name, Prefix = prefix, FolderName = folder, SortOrder = sort,
                                UpdatedAt = localUpdatedAt, GroupName = s.GroupName, GroupSyncId = s.GroupSyncId,
                            }),
                            JsonSerializer.Serialize(s));
                }
                else if (applyIncoming)
                {
                    counts.SubtypesUpdated++;
                    if (apply)
                    {
                        ExecuteNonQuery("UPDATE equipment_subtypes SET name=@n, prefix=@p, folder_name=@f, sort_order=@s, sync_id=@sy, updated_at=@u WHERE id=@id", cmd =>
                        {
                            cmd.Parameters.AddWithValue("@n", s.Name); cmd.Parameters.AddWithValue("@p", s.Prefix);
                            cmd.Parameters.AddWithValue("@f", wantFolder); cmd.Parameters.AddWithValue("@s", s.SortOrder);
                            cmd.Parameters.AddWithValue("@id", id);
                            cmd.Parameters.AddWithValue("@sy", effectiveSyncId);
                            cmd.Parameters.AddWithValue("@u", string.IsNullOrEmpty(s.UpdatedAt) ? syncNow : s.UpdatedAt);
                        });
                        SetHierarchyWatermark(effectiveSyncId, syncNow);
                    }
                }
                else if (apply)
                {
                    if (adoptSyncId)
                        ExecuteNonQuery("UPDATE equipment_subtypes SET sync_id=@sy WHERE id=@id", cmd =>
                        { cmd.Parameters.AddWithValue("@sy", s.SyncId); cmd.Parameters.AddWithValue("@id", id); });
                    SetHierarchyWatermark(effectiveSyncId, syncNow);
                }
            }
            else
            {
                if (adoptSyncId && apply)
                    ExecuteNonQuery("UPDATE equipment_subtypes SET sync_id=@sy WHERE id=@id", cmd =>
                    { cmd.Parameters.AddWithValue("@sy", s.SyncId); cmd.Parameters.AddWithValue("@id", id); });
                if (apply) SetHierarchyWatermark(effectiveSyncId, syncNow);
            }
        }

        // ── Subtypes removed on the exporting machine — unlike groups/controllers/modifications
        //    above (upsert-only, see class doc: never deleted because fw_versions/param_files hold
        //    them by plain integer FK with no cascade), a subtype the operator explicitly deleted
        //    used to never disappear on any OTHER machine ("resurrected" subtype, reported live).
        //    Safe to mirror the deletion here because it's a full, unfiltered snapshot (same
        //    reasoning as tags/allowed_extensions) — but only for a subtype nothing local still
        //    references; one that still has uploads/params/reservations under it is left alone
        //    (SubtypesSkippedDelete) rather than orphaning that data or silently losing it.
        //    Rows without a sync_id predate this feature and are never auto-deleted — no reliable
        //    way to correlate them against the incoming set.
        var incomingSubtypeSyncIds = new HashSet<string>(
            data.EquipmentSubtypes.Where(s => !string.IsNullOrEmpty(s.SyncId)).Select(s => s.SyncId));
        var localSubtypes = new List<(int Id, string SyncId)>();
        using (var r = ExecuteReader("SELECT id, sync_id FROM equipment_subtypes WHERE sync_id IS NOT NULL AND sync_id != ''"))
            while (r.Read())
                localSubtypes.Add((r.GetInt32(0), r.GetString(1)));

        foreach (var (id, syncId) in localSubtypes)
        {
            if (incomingSubtypeSyncIds.Contains(syncId)) continue;

            var referenced = ExecuteScalar("""
                SELECT 1 WHERE EXISTS(SELECT 1 FROM fw_versions WHERE subtype_id=@id)
                   OR EXISTS(SELECT 1 FROM param_files WHERE subtype_id=@id)
                   OR EXISTS(SELECT 1 FROM passports WHERE subtype_id=@id)
                   OR EXISTS(SELECT 1 FROM fw_version_reservations WHERE subtype_id=@id)
                """, cmd => cmd.Parameters.AddWithValue("@id", id)) is not null;
            if (referenced)
            {
                counts.SubtypesSkippedDelete++;
                continue;
            }

            counts.SubtypesRemoved++;
            if (apply) ExecuteNonQuery("DELETE FROM equipment_subtypes WHERE id=@id", cmd => cmd.Parameters.AddWithValue("@id", id));
        }

        // ── Типы шкафов, удалённые на выгружавшей машине — та же дыра, что была у подтипов и
        //    контроллеров, и последняя из этой серии. Удалённый тип шкафа возвращался с любой машины
        //    (или из лежащего на диске JSON), которая ещё не знала о его удалении — «мусорный тип
        //    шкафа», который удаляешь, а он снова тут. Правила ровно те же: только строки с sync_id
        //    (по чему ещё соотносить со входящим снимком) и только если на тип локально уже ничего
        //    не ссылается. Идёт ПОСЛЕ удаления подтипов: тип, у которого только что удалили
        //    последний подтип, в этом же проходе может уехать целиком.
        var incomingGroupSyncIds = new HashSet<string>(
            data.EquipmentGroups.Where(g => !string.IsNullOrEmpty(g.SyncId)).Select(g => g.SyncId));
        var localGroups = new List<(int Id, string SyncId)>();
        using (var r = ExecuteReader("SELECT id, sync_id FROM equipment_groups WHERE sync_id IS NOT NULL AND sync_id != ''"))
            while (r.Read())
                localGroups.Add((r.GetInt32(0), r.GetString(1)));

        foreach (var (id, syncId) in localGroups)
        {
            if (incomingGroupSyncIds.Contains(syncId)) continue;

            var referenced = ExecuteScalar("SELECT 1 WHERE EXISTS(SELECT 1 FROM equipment_subtypes WHERE group_id=@id)",
                cmd => cmd.Parameters.AddWithValue("@id", id)) is not null;
            if (referenced)
            {
                counts.GroupsSkippedDelete++;
                continue;
            }

            counts.GroupsRemoved++;
            if (apply) ExecuteNonQuery("DELETE FROM equipment_groups WHERE id=@id", cmd => cmd.Parameters.AddWithValue("@id", id));
        }

        // ── Controller models (upsert by sync_id, fallback to name) ─────────────
        var controllerSyncToId = new Dictionary<string, int>();
        foreach (var c in data.ControllerModels)
        {
            var existing = FindBySyncOrName("controller_models", c.SyncId, "name", c.Name);
            if (existing is null)
            {
                counts.ControllersAdded++;
                if (apply)
                {
                    var sync = string.IsNullOrEmpty(c.SyncId) ? Guid.NewGuid().ToString() : c.SyncId;
                    var updatedAt = string.IsNullOrEmpty(c.UpdatedAt) ? syncNow : c.UpdatedAt;
                    ExecuteNonQuery("INSERT INTO controller_models(name,prefix,sort_order,sync_id,updated_at) VALUES(@n,@p,@s,@sy,@u)", cmd =>
                    {
                        cmd.Parameters.AddWithValue("@n", c.Name); cmd.Parameters.AddWithValue("@p", c.Prefix);
                        cmd.Parameters.AddWithValue("@s", c.SortOrder); cmd.Parameters.AddWithValue("@sy", sync);
                        cmd.Parameters.AddWithValue("@u", updatedAt);
                    });
                    if (!string.IsNullOrEmpty(c.SyncId)) controllerSyncToId[c.SyncId] = Convert.ToInt32(ExecuteScalar("SELECT last_insert_rowid()"));
                    SetHierarchyWatermark(sync, syncNow);
                }
                continue;
            }
            var (id, name, prefix, sort, localSyncId, localUpdatedAt) = existing.Value;
            if (!string.IsNullOrEmpty(c.SyncId)) controllerSyncToId[c.SyncId] = id;
            var adoptSyncId = !string.IsNullOrEmpty(c.SyncId) && c.SyncId != localSyncId;
            var effectiveSyncId = adoptSyncId ? c.SyncId : localSyncId;
            if (name != c.Name || prefix != c.Prefix || sort != c.SortOrder)
            {
                var (conflict, applyIncoming) = ClassifyHierarchyChange(effectiveSyncId, localUpdatedAt, c.UpdatedAt);
                if (conflict)
                {
                    counts.ConflictsFound++;
                    if (apply)
                        RecordPendingConflict("controller", effectiveSyncId, id, $"Контроллер «{name}»",
                            JsonSerializer.Serialize(new ExportedController { SyncId = effectiveSyncId, Name = name, Prefix = prefix, SortOrder = sort, UpdatedAt = localUpdatedAt }),
                            JsonSerializer.Serialize(c));
                }
                else if (applyIncoming)
                {
                    counts.ControllersUpdated++;
                    if (apply)
                    {
                        ExecuteNonQuery("UPDATE controller_models SET name=@n, prefix=@p, sort_order=@s, sync_id=@sy, updated_at=@u WHERE id=@id", cmd =>
                        {
                            cmd.Parameters.AddWithValue("@n", c.Name); cmd.Parameters.AddWithValue("@p", c.Prefix);
                            cmd.Parameters.AddWithValue("@s", c.SortOrder); cmd.Parameters.AddWithValue("@id", id);
                            cmd.Parameters.AddWithValue("@sy", effectiveSyncId);
                            cmd.Parameters.AddWithValue("@u", string.IsNullOrEmpty(c.UpdatedAt) ? syncNow : c.UpdatedAt);
                        });
                        SetHierarchyWatermark(effectiveSyncId, syncNow);
                    }
                }
                else if (apply)
                {
                    if (adoptSyncId)
                        ExecuteNonQuery("UPDATE controller_models SET sync_id=@sy WHERE id=@id", cmd =>
                        { cmd.Parameters.AddWithValue("@sy", c.SyncId); cmd.Parameters.AddWithValue("@id", id); });
                    SetHierarchyWatermark(effectiveSyncId, syncNow);
                }
            }
            else
            {
                if (adoptSyncId && apply)
                    ExecuteNonQuery("UPDATE controller_models SET sync_id=@sy WHERE id=@id", cmd =>
                    { cmd.Parameters.AddWithValue("@sy", c.SyncId); cmd.Parameters.AddWithValue("@id", id); });
                if (apply) SetHierarchyWatermark(effectiveSyncId, syncNow);
            }
        }

        // ── Controllers removed on the exporting machine — same "resurrected row" gap that
        //    subtypes had (see the block above), just never closed for controller_models. This is
        //    the actual root cause behind the FORTUS controller repeatedly reappearing: the class
        //    doc above ("Catalog tables ... updated IN PLACE — never deleted") was accurate for
        //    controllers/modifications even after subtypes got deletion propagation, so a controller
        //    deleted locally would silently come back the moment ANY sync partner (or a stale JSON
        //    on the network drive) still listed it — nothing above this point ever removed it again.
        //    Same safety rule as subtypes: only delete a controller nothing local still references
        //    (modifications/fw_versions/reservations), and only for rows that have a sync_id to
        //    correlate against the incoming snapshot.
        var incomingControllerSyncIds = new HashSet<string>(
            data.ControllerModels.Where(c => !string.IsNullOrEmpty(c.SyncId)).Select(c => c.SyncId));
        var localControllers = new List<(int Id, string SyncId)>();
        using (var r = ExecuteReader("SELECT id, sync_id FROM controller_models WHERE sync_id IS NOT NULL AND sync_id != ''"))
            while (r.Read())
                localControllers.Add((r.GetInt32(0), r.GetString(1)));

        foreach (var (id, syncId) in localControllers)
        {
            if (incomingControllerSyncIds.Contains(syncId)) continue;

            var referenced = ExecuteScalar("""
                SELECT 1 WHERE EXISTS(SELECT 1 FROM controller_modifications WHERE controller_id=@id)
                   OR EXISTS(SELECT 1 FROM fw_versions WHERE controller_id=@id)
                   OR EXISTS(SELECT 1 FROM fw_version_reservations WHERE controller_id=@id)
                """, cmd => cmd.Parameters.AddWithValue("@id", id)) is not null;
            if (referenced)
            {
                counts.ControllersSkippedDelete++;
                continue;
            }

            counts.ControllersRemoved++;
            if (apply) ExecuteNonQuery("DELETE FROM controller_models WHERE id=@id", cmd => cmd.Parameters.AddWithValue("@id", id));
        }

        // ── Controller modifications (upsert by sync_id, fallback to (controller,display_name)) ──
        foreach (var m in data.ControllerModifications)
        {
            var ctrlId = ResolveId("controller_models", m.ControllerSyncId, controllerSyncToId, "name", m.ControllerName);
            if (ctrlId is null) continue;

            var existing = FindModification(m.SyncId, ctrlId.Value, m.DisplayName);
            if (existing is null)
            {
                counts.ModificationsAdded++;
                if (apply)
                {
                    var sync = string.IsNullOrEmpty(m.SyncId) ? Guid.NewGuid().ToString() : m.SyncId;
                    var updatedAt = string.IsNullOrEmpty(m.UpdatedAt) ? syncNow : m.UpdatedAt;
                    ExecuteNonQuery(
                        "INSERT INTO controller_modifications(controller_id,display_name,hw_version,sort_order,description,sync_id,updated_at) VALUES(@c,@n,@h,@s,@d,@sy,@u)",
                        cmd =>
                        {
                            cmd.Parameters.AddWithValue("@c", ctrlId.Value); cmd.Parameters.AddWithValue("@n", m.DisplayName);
                            cmd.Parameters.AddWithValue("@h", m.HwVersion); cmd.Parameters.AddWithValue("@s", m.SortOrder);
                            cmd.Parameters.AddWithValue("@d", m.Description); cmd.Parameters.AddWithValue("@sy", sync);
                            cmd.Parameters.AddWithValue("@u", updatedAt);
                        });
                    SetHierarchyWatermark(sync, syncNow);
                }
                continue;
            }
            var (id, name, hw, sort, desc, localSyncId2, localUpdatedAt2) = existing.Value;
            var adoptSyncId2 = !string.IsNullOrEmpty(m.SyncId) && m.SyncId != localSyncId2;
            var effectiveSyncId2 = adoptSyncId2 ? m.SyncId : localSyncId2;
            if (name != m.DisplayName || hw != m.HwVersion || sort != m.SortOrder || desc != m.Description)
            {
                var (conflict, applyIncoming) = ClassifyHierarchyChange(effectiveSyncId2, localUpdatedAt2, m.UpdatedAt);
                if (conflict)
                {
                    counts.ConflictsFound++;
                    if (apply)
                        RecordPendingConflict("modification", effectiveSyncId2, id, $"Модификация «{name}»",
                            JsonSerializer.Serialize(new ExportedModification
                            {
                                SyncId = effectiveSyncId2, DisplayName = name, HwVersion = hw, SortOrder = sort, Description = desc,
                                UpdatedAt = localUpdatedAt2, ControllerName = m.ControllerName, ControllerSyncId = m.ControllerSyncId,
                            }),
                            JsonSerializer.Serialize(m));
                }
                else if (applyIncoming)
                {
                    counts.ModificationsUpdated++;
                    if (apply)
                    {
                        ExecuteNonQuery("UPDATE controller_modifications SET display_name=@n, hw_version=@h, sort_order=@s, description=@d, sync_id=@sy, updated_at=@u WHERE id=@id", cmd =>
                        {
                            cmd.Parameters.AddWithValue("@n", m.DisplayName); cmd.Parameters.AddWithValue("@h", m.HwVersion);
                            cmd.Parameters.AddWithValue("@s", m.SortOrder); cmd.Parameters.AddWithValue("@d", m.Description);
                            cmd.Parameters.AddWithValue("@id", id);
                            cmd.Parameters.AddWithValue("@sy", effectiveSyncId2);
                            cmd.Parameters.AddWithValue("@u", string.IsNullOrEmpty(m.UpdatedAt) ? syncNow : m.UpdatedAt);
                        });
                        SetHierarchyWatermark(effectiveSyncId2, syncNow);
                    }
                }
                else if (apply)
                {
                    if (adoptSyncId2)
                        ExecuteNonQuery("UPDATE controller_modifications SET sync_id=@sy WHERE id=@id", cmd =>
                        { cmd.Parameters.AddWithValue("@sy", m.SyncId); cmd.Parameters.AddWithValue("@id", id); });
                    SetHierarchyWatermark(effectiveSyncId2, syncNow);
                }
            }
            else
            {
                if (adoptSyncId2 && apply)
                    ExecuteNonQuery("UPDATE controller_modifications SET sync_id=@sy WHERE id=@id", cmd =>
                    { cmd.Parameters.AddWithValue("@sy", m.SyncId); cmd.Parameters.AddWithValue("@id", id); });
                if (apply) SetHierarchyWatermark(effectiveSyncId2, syncNow);
            }
        }

        // ── Модификации контроллеров, удалённые на выгружавшей машине — эталонная синхронизация
        //    (authoritative) ТОЛЬКО. В обычной синхронизации это единственный из справочников выше,
        //    у которого до сих пор нет зеркалирования удаления вообще (см. класс-док
        //    ImportHierarchyData) — не по недосмотру, а потому что ничем локально не ссылается на
        //    модификацию по id: fw_versions/fw_version_reservations хранят hw_version как обычное
        //    число и сверяются с controller_id+hw_version по ЗНАЧЕНИЮ, а не по строке FK на эту
        //    таблицу. Поэтому предохранитель ниже — «мягкий», того же духа, что и у подтипов/
        //    контроллеров: не удаляем модификацию, если под тем же контроллером с тем же hw_version
        //    у получателя ещё жива локальная прошивка/резерв — иначе они молча потеряют своё
        //    единственное текстовое описание/название железа.
        if (authoritative)
        {
            var incomingModSyncIds = new HashSet<string>(
                data.ControllerModifications.Where(m => !string.IsNullOrEmpty(m.SyncId)).Select(m => m.SyncId));
            var localMods = new List<(int Id, string SyncId, int ControllerId, int HwVersion)>();
            using (var r = ExecuteReader("SELECT id, sync_id, controller_id, hw_version FROM controller_modifications WHERE sync_id IS NOT NULL AND sync_id != ''"))
                while (r.Read())
                    localMods.Add((r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)));

            foreach (var (id, syncId, ctrlId, hw) in localMods)
            {
                if (incomingModSyncIds.Contains(syncId)) continue;

                var referenced = ExecuteScalar("""
                    SELECT 1 WHERE EXISTS(SELECT 1 FROM fw_versions WHERE controller_id=@c AND hw_version=@h)
                       OR EXISTS(SELECT 1 FROM fw_version_reservations WHERE controller_id=@c AND hw_version=@h)
                    """, cmd => { cmd.Parameters.AddWithValue("@c", ctrlId); cmd.Parameters.AddWithValue("@h", hw); }) is not null;
                if (referenced)
                {
                    counts.ModificationsSkippedDelete++;
                    continue;
                }

                counts.ModificationsRemoved++;
                if (apply) ExecuteNonQuery("DELETE FROM controller_modifications WHERE id=@id", cmd => cmd.Parameters.AddWithValue("@id", id));
            }
        }

        // ── Плоские списки-справочники: производители ПЧ/УПП, теги, разрешённые расширения ────
        //    Раньше каждый из трёх синхронизировался «зеркалом»: чего нет во входящем наборе — то
        //    удаляется локально. Без отметок времени это «выигрывает тот, кто последним нажал
        //    импорт», и оно съедало только что добавленные записи, стоило любой машине выгрузить
        //    свой конфиг, не забрав перед этим чужой (подробный разбор — Database.FlatLists.cs).
        //    Теперь удаление и возврат — события с отметкой времени, побеждает более позднее.
        ImportFlatList(Database.FlatKindManufacturer,
            (data.ParamManufacturers ?? new()).Select(m => m.Name),
            data.FlatListState, apply, GetParamManufacturers,
            name => ExecuteNonQuery("INSERT OR IGNORE INTO param_manufacturers(name) VALUES(@n)", cmd => cmd.Parameters.AddWithValue("@n", name)),
            DeleteParamManufacturer,
            () => counts.ManufacturersAdded++, () => counts.ManufacturersRemoved++);

        ImportFlatList(Database.FlatKindTag,
            data.Tags ?? new(),
            data.FlatListState, apply, GetAllTags, AddTag, DeleteTag,
            () => counts.TagsAdded++, () => counts.TagsRemoved++);

        ImportFlatList(Database.FlatKindExtension,
            data.AllowedExtensions ?? new(),
            data.FlatListState, apply, GetAllowedExtensions, AddAllowedExtension, RemoveAllowedExtension,
            () => counts.ExtensionsAdded++, () => counts.ExtensionsRemoved++);

        ImportFlatList(Database.FlatKindExtensionHmi,
            data.AllowedExtensionsHmi ?? new(),
            data.FlatListState, apply, GetAllowedExtensionsHmi, AddAllowedExtensionHmi, RemoveAllowedExtensionHmi,
            () => counts.ExtensionsHmiAdded++, () => counts.ExtensionsHmiRemoved++);

        ImportFlatList(Database.FlatKindExtensionSchematic,
            data.AllowedExtensionsSchematic ?? new(),
            data.FlatListState, apply, GetAllowedExtensionsSchematic, AddAllowedExtensionSchematic, RemoveAllowedExtensionSchematic,
            () => counts.ExtensionsSchematicAdded++, () => counts.ExtensionsSchematicRemoved++);

        // ── Эталонная синхронизация (authoritative) ТОЛЬКО — второй, дополнительный проход поверх
        //    четырёх ImportFlatList выше. Тот уже применил всё, что входящая сторона знает как
        //    добавленное/удалённое (по отметке времени) — но сознательно НЕ трогает имя, у которого
        //    нет отметки вовсе ни на одной стороне: «отсутствие имени у собеседника означает лишь то,
        //    что он о нём не знает, а не то, что его удалили» (см. док самого ImportFlatList). Для
        //    эталонного снимка это неверно по определению: администратор прислал ПОЛНЫЙ список,
        //    отсутствие в нём означает «не должно существовать больше нигде» — тот самый пробел из
        //    задачи («мусорная запись завелась на чужой машине, эталонная её никогда не видела,
        //    поэтому надгробия для неё нет и быть не может»). isUsedLocally — «мягкий» предохранитель
        //    того же духа, что у модификаций выше: тег/производитель — не физический FK, а текст в
        //    fw_versions.tags/param_files.manufacturer/tags, поэтому проверяем текстовое совпадение,
        //    а не EXISTS по внешнему ключу. allowed_extensions/allowed_extensions_hmi ни на что не
        //    ссылаются (это просто список допустимых расширений при загрузке, не привязанный к уже
        //    загруженным файлам) — для них предохранитель не нужен.
        if (authoritative)
        {
            // Имена с состоянием (живым ИЛИ удалённым) по каждому виду — уже полностью разобраны
            // ImportFlatList выше по LWW-таймстемпам; MirrorFlatListDeletions ниже не должен трогать
            // их повторно (см. её доку про баг-фикс двойного счёта/потери более свежей локальной
            // отметки).
            var flatState = data.FlatListState ?? new();

            var usedTags = CollectUsedTagWords();
            MirrorFlatListDeletions(GetAllTags, DeleteTag, data.Tags ?? new(),
                flatState.Where(s => s.Kind == FlatKindTag).Select(s => s.Name), usedTags.Contains,
                apply, () => counts.TagsRemoved++, () => counts.TagsSkippedDelete++);

            var usedManufacturers = CollectUsedManufacturers();
            MirrorFlatListDeletions(GetParamManufacturers, DeleteParamManufacturer,
                (data.ParamManufacturers ?? new()).Select(m => m.Name),
                flatState.Where(s => s.Kind == FlatKindManufacturer).Select(s => s.Name), usedManufacturers.Contains,
                apply, () => counts.ManufacturersRemoved++, () => counts.ManufacturersSkippedDelete++);

            MirrorFlatListDeletions(GetAllowedExtensions, RemoveAllowedExtension, data.AllowedExtensions ?? new(),
                flatState.Where(s => s.Kind == FlatKindExtension).Select(s => s.Name), null,
                apply, () => counts.ExtensionsRemoved++, () => counts.ExtensionsSkippedDelete++);

            MirrorFlatListDeletions(GetAllowedExtensionsHmi, RemoveAllowedExtensionHmi, data.AllowedExtensionsHmi ?? new(),
                flatState.Where(s => s.Kind == FlatKindExtensionHmi).Select(s => s.Name), null,
                apply, () => counts.ExtensionsHmiRemoved++, () => counts.ExtensionsHmiSkippedDelete++);

            MirrorFlatListDeletions(GetAllowedExtensionsSchematic, RemoveAllowedExtensionSchematic, data.AllowedExtensionsSchematic ?? new(),
                flatState.Where(s => s.Kind == FlatKindExtensionSchematic).Select(s => s.Name), null,
                apply, () => counts.ExtensionsSchematicRemoved++, () => counts.ExtensionsSchematicSkippedDelete++);
        }

        // ── Reservations (natural key = subtype+controller+hw_version+version_raw; status only
        //    ever advances reserved → fulfilled/cancelled, never the other way, so a local reservation
        //    that's already closed out is left alone even if the incoming copy still says "reserved") ─
        foreach (var res in data.Reservations)
        {
            var subId = ResolveId("equipment_subtypes", res.SubtypeSyncId, subtypeSyncToId, "name", res.SubtypeName);
            var ctrlId = ResolveId("controller_models", res.ControllerSyncId, controllerSyncToId, "name", res.ControllerName);
            if (subId is null || ctrlId is null) continue;

            var localStatus = ExecuteScalar(
                "SELECT status FROM fw_version_reservations WHERE subtype_id=@s AND controller_id=@c AND hw_version=@h AND version_raw=@v",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@s", subId.Value); cmd.Parameters.AddWithValue("@c", ctrlId.Value);
                    cmd.Parameters.AddWithValue("@h", res.HwVersion); cmd.Parameters.AddWithValue("@v", res.VersionRaw);
                }) as string;

            if (localStatus is null)
            {
                counts.ReservationsAdded++;
                if (apply)
                    ExecuteNonQuery("""
                        INSERT INTO fw_version_reservations(subtype_id,controller_id,hw_version,version_raw,status,reserved_by,reserved_at)
                        VALUES(@s,@c,@h,@v,@st,@by,@at)
                        """, cmd =>
                    {
                        cmd.Parameters.AddWithValue("@s", subId.Value); cmd.Parameters.AddWithValue("@c", ctrlId.Value);
                        cmd.Parameters.AddWithValue("@h", res.HwVersion); cmd.Parameters.AddWithValue("@v", res.VersionRaw);
                        cmd.Parameters.AddWithValue("@st", res.Status); cmd.Parameters.AddWithValue("@by", res.ReservedBy);
                        cmd.Parameters.AddWithValue("@at", res.ReservedAt);
                    });
            }
            else if (localStatus == "reserved" && res.Status != "reserved")
            {
                counts.ReservationsUpdated++;
                if (apply)
                    ExecuteNonQuery("""
                        UPDATE fw_version_reservations SET status=@st
                        WHERE subtype_id=@s AND controller_id=@c AND hw_version=@h AND version_raw=@v
                        """, cmd =>
                    {
                        cmd.Parameters.AddWithValue("@st", res.Status);
                        cmd.Parameters.AddWithValue("@s", subId.Value); cmd.Parameters.AddWithValue("@c", ctrlId.Value);
                        cmd.Parameters.AddWithValue("@h", res.HwVersion); cmd.Parameters.AddWithValue("@v", res.VersionRaw);
                    });
            }
        }

        // ── App users roster — see MergeAppUsersInto (Database.AppUsers.cs) for the merge rule.
        MergeAppUsersInto(data.AppUsers, counts, apply);

        // ── Статистика выборов прошивки (Database.FwUsage.cs). Намеренно НЕ учитывается в counts:
        //    она меняется от каждого поиска коллеги, и попади она в TotalChanges — плашка «Поступили
        //    изменения» дёргала бы оператора целыми днями по поводу, который его не касается.
        if (apply && data.FwUsage is not null)
            ImportFwUsage(data.FwUsage.Select(u => new SharedFwUsageRow(u.Origin, u.QueryKey, u.SubtypeSyncId,
                u.ControllerSyncId, u.VersionRaw, u.Uses, u.LastUsedAt, u.Weight)), UsageOriginId());

        // ── fw_versions / param_files: additive-only, as before — each machine may have uploads
        //    the exporting one never saw, so absence locally never means "delete it". The one
        //    exception is МОДЕРАЦИЯ on an ALREADY-matched row — status, released и archived: она
        //    только продвигается вперёд (active→rolled_back, unreleased→released, 0→archived — см.
        //    тот же довод в блоке резервов выше), поэтому копию, ушедшую дальше нашей, принимаем, а
        //    назад локальную строку не тянем никогда. Без этого версия, отмодерированная на одной
        //    машине, оставалась бы в очереди модерации у всех остальных навсегда: совпадение по ключу
        //    просто пропускало бы её как «уже есть», ничего не обновляя.
        //
        //    Опознаётся строка по sync_id (см. FindFwVersionRow) с откатом на прежний натуральный ключ
        //    подтип+контроллер+version_raw. Одного натурального ключа не хватало: он ломается от
        //    правок, которые сама же программа и разрешает (переназначение контроллера, переписывание
        //    hw меняет version_raw) — после них состояние модерации и надгробие не находили, к какой
        //    строке примениться, и запись коллеги оставалась висеть в модерации, а рядом заводился
        //    дубликат.
        //
        //    Deletion (Задача 3) is the one thing that ISN'T additive-only: TombstoneFwVersion marks
        //    a row with deleted_at instead of removing it, specifically so it keeps flowing through
        //    here as a positive "this was deleted" signal (the additive/absence-based reasoning above
        //    can't express deletion — a row missing from an incoming snapshot might just be an upload
        //    that machine hasn't made yet). Two rules, both below: (1) a LOCAL tombstone always wins
        //    and is permanent — an incoming row for the same natural key that's still "active" (from a
        //    machine that hasn't caught up on the deletion yet) must never resurrect it; (2) an
        //    INCOMING tombstone not yet applied locally gets mirrored — including a best-effort
        //    on-disk cleanup, the same as SettingsView.DeleteFirmware_Click does for a direct local
        //    delete — so the deletion actually reaches every other machine, not just the one it
        //    started on.
        foreach (var fv in data.FwVersions)
        {
            var subId = ResolveId("equipment_subtypes", fv.SubtypeSyncId, subtypeSyncToId, "name", fv.SubtypeName, fv.GroupName);
            var ctrlId = ResolveId("controller_models", fv.ControllerSyncId, controllerSyncToId, "name", fv.CtrlName);
            if (subId is null || ctrlId is null) continue;

            var existingRow = FindFwVersionRow(fv.SyncId, subId.Value, ctrlId.Value, fv.VersionRaw, fv.ConfigName);

            if (existingRow is not null)
            {
                var id = existingRow.Id;
                var (localStatus, localReleased, localArchived) = (existingRow.Status, existingRow.Released, existingRow.Archived);
                var (localIoMap, localInstr, localHmi) = (existingRow.IoMapPath, existingRow.InstructionsPath, existingRow.HmiPath);
                var (localExecHint, localHmiExecHint, localModbus) = (existingRow.ExecutableHint, existingRow.HmiExecutableHint, existingRow.ModbusMapPath);
                var (localDeletedAt, localDiskPath) = (existingRow.DeletedAt, existingRow.DiskPath);
                var (localDesc, localLaunchTypes, localTags) = (existingRow.Description, existingRow.LaunchTypes, existingRow.Tags);

                // Первый контакт двух независимо заведённых баз: строка нашлась по натуральному ключу,
                // а sync_id у сторон разные (каждая сгенерировала свой при миграции). Перенимаем чужой
                // прямо сейчас — ровно как это делают справочники выше (adoptSyncId), чтобы дальше обе
                // машины связывала уже отметка, переживающая правку контроллера/номера версии.
                if (apply && !string.IsNullOrEmpty(fv.SyncId) && fv.SyncId != existingRow.SyncId)
                    ExecuteNonQuery("UPDATE fw_versions SET sync_id=@sy WHERE id=@id", cmd =>
                    {
                        cmd.Parameters.AddWithValue("@sy", fv.SyncId);
                        cmd.Parameters.AddWithValue("@id", id);
                    });

                // Rule 1 — already deleted here: permanent, never revived by an incoming row that
                // just hasn't caught up yet (see class doc above).
                if (!string.IsNullOrEmpty(localDeletedAt)) continue;

                // Строка опознана по sync_id, но её натуральный ключ разошёлся — значит на машине-
                // источнике версию ПЕРЕИМЕНОВАЛИ (переписали hw: version_raw и имя папки на диске) или
                // ПЕРЕНАЗНАЧИЛИ другому контроллеру. Это ровно тот же случай «строку правили, а не
                // завели заново», ради которого sync_id и появился у справочников: применяем правку НА
                // МЕСТЕ. Раньше (без sync_id) такой снимок вставлял рядом вторую строку, а исходная
                // оставалась фантомом — с несуществующей папкой и навсегда в очереди модерации.
                //
                // Предохранитель: если целевой натуральный ключ у нас уже занят другой строкой (дубль
                // от старой версии приложения, гонка с проигрыванием hw-переписывания), переименование
                // пропускаем — два ряда с одним ключом хуже, чем один устаревший.
                // config_name здесь наравне с остальными полями тождества: переименование КОНФИГУРАЦИИ
                // («2 насоса» → «2 насоса + жокей») — такая же правка строки на месте, а не новая запись.
                var renamed = existingRow.VersionRaw != fv.VersionRaw ||
                              existingRow.SubtypeId != subId.Value || existingRow.ControllerId != ctrlId.Value ||
                              existingRow.ConfigName != (fv.ConfigName ?? "");
                if (renamed && FindFwVersionIdByNaturalKey(subId.Value, ctrlId.Value, fv.VersionRaw, id, fv.ConfigName ?? "") is not null)
                    renamed = false;
                if (renamed)
                {
                    counts.FwVersionsRenamed++;
                    if (apply)
                        ExecuteNonQuery("""
                            UPDATE fw_versions SET subtype_id=@s, controller_id=@c, version_raw=@v,
                                hw_version=@hw, sw_version=@sw, dt_str=@dt, eq_prefix=@eq, sub_prefix=@sub,
                                disk_path=@disk, config_name=@cfg
                            WHERE id=@id
                            """, cmd =>
                        {
                            cmd.Parameters.AddWithValue("@cfg", fv.ConfigName ?? "");
                            cmd.Parameters.AddWithValue("@s", subId.Value);
                            cmd.Parameters.AddWithValue("@c", ctrlId.Value);
                            cmd.Parameters.AddWithValue("@v", fv.VersionRaw);
                            cmd.Parameters.AddWithValue("@hw", fv.HwVersion);
                            cmd.Parameters.AddWithValue("@sw", fv.SwVersion);
                            cmd.Parameters.AddWithValue("@dt", fv.DtStr);
                            cmd.Parameters.AddWithValue("@eq", fv.EqPrefix);
                            cmd.Parameters.AddWithValue("@sub", fv.SubPrefix);
                            // Имя папки версии на диске = version_raw, поэтому вместе с номером
                            // переезжает и путь. Он абсолютный и записан в нотации отправителя —
                            // приводит его к нашему корню RemapFwPaths сразу после импорта (см.
                            // ConfigSyncService.ApplyToDatabase), тем же проходом, что и для строк,
                            // приехавших сюда впервые.
                            cmd.Parameters.AddWithValue("@disk", fv.DiskPath);
                            cmd.Parameters.AddWithValue("@id", id);
                        });
                    if (apply) localDiskPath = fv.DiskPath;
                }

                // Rule 2 — incoming tombstone not yet applied locally: mirror it.
                if (!string.IsNullOrEmpty(fv.DeletedAt))
                {
                    counts.FwVersionsRemoved++;
                    if (!apply) continue;
                    MirrorFwTombstone(id, localDiskPath, localHmi, fv.VersionRaw);
                    continue;
                }

                var incomingStatus = string.IsNullOrEmpty(fv.Status) ? "active" : fv.Status;

                // A version can be uploaded on one machine, exported/synced BEFORE its HMI project
                // (or Карта ВВ/Инструкция/Карта modbus) is attached, and only get those attachments
                // afterwards on the originating machine — without this, every OTHER machine's copy
                // of that row stays permanently blank on these fields (see root-cause note above:
                // this exact gap made a colleague's "HMI-проект" button show up while it silently
                // never appeared for machines that only ever received the row via config sync).
                // Never overwrites a locally-filled value — only fills in what's still empty here.
                string Backfill(string local, string incoming) => string.IsNullOrEmpty(local) ? incoming : local;
                var newIoMap = Backfill(localIoMap, fv.IoMapPath);
                var newInstr = Backfill(localInstr, fv.InstructionsPath);
                var newHmi = Backfill(localHmi, fv.HmiPath);
                var newExecHint = Backfill(localExecHint, fv.ExecutableHint);
                var newHmiExecHint = Backfill(localHmiExecHint, fv.HmiExecutableHint);
                var newModbus = Backfill(localModbus, fv.ModbusMapPath);

                // Описание/типы пуска — тот же Backfill, но «пустым» здесь считается ещё и заглушка
                // ChangelogFile.DiskSyncPlaceholder. Строку могло создать сканирование диска
                // (HierarchyService.SyncFwFromDisk), которое видит только папки и о настоящем
                // описании не знает; без этого исключения заглушка выигрывала у входящего настоящего
                // описания навсегда — жалоба «прошивки с другого компа приходят с описанием
                // "синхронизировано с диска" вместо моего». В обратную сторону не работает: входящую
                // заглушку локальным описанием не перетираем (Backfill сам её отбросит как «incoming
                // пустой»), и уже заполненное вручную описание тоже неприкосновенно.
                bool IsBlankDesc(string s) => string.IsNullOrWhiteSpace(s) || s.Trim() == Services.ChangelogFile.DiskSyncPlaceholder;
                var newDesc = IsBlankDesc(localDesc) && !IsBlankDesc(fv.Description) ? fv.Description : localDesc;
                bool IsBlankLaunchTypes(string s) => string.IsNullOrWhiteSpace(s) || s.Trim() is "[]" or "null";
                var newLaunchTypes = IsBlankLaunchTypes(localLaunchTypes) && !IsBlankLaunchTypes(fv.LaunchTypes)
                    ? fv.LaunchTypes : localLaunchTypes;

                // Теги — объединение, а не Backfill: тег («точное название шкафа») почти всегда
                // добавляют УЖЕ существующей, давно разошедшейся по машинам прошивке, а раньше строка
                // tags писалась только при первичном INSERT — на уже совпавшей записи её не трогали
                // вовсе, и добавленный тег к коллегам не доезжал (поиск по нему ничего не находил).
                // Объединяем множества (без учёта регистра, порядок локальных сохраняем, новые в конец),
                // чтобы добавленный где угодно тег доехал везде и ни одна машина не теряла своих.
                // Удаление тега при этом не распространяется — та же аддитивная логика, что и у всей
                // остальной синхронизации fw_versions (отсутствие ≠ «удалить»).
                var localTagList = Services.TagString.Parse(localTags);
                var haveTags = new HashSet<string>(localTagList, StringComparer.OrdinalIgnoreCase);
                var addedTags = Services.TagString.Parse(fv.Tags).Where(t => haveTags.Add(t)).ToList();
                var newTags = addedTags.Count == 0 ? localTags : Services.TagString.Join(localTagList.Concat(addedTags));

                var fieldsChanged = newIoMap != localIoMap || newInstr != localInstr || newHmi != localHmi ||
                                    newExecHint != localExecHint || newHmiExecHint != localHmiExecHint || newModbus != localModbus ||
                                    newDesc != localDesc || newLaunchTypes != localLaunchTypes || newTags != localTags;

                // Архивирование — третья составляющая состояния модерации, наравне со status и
                // released (очередь модерации отбирает строки по всем трём сразу, см.
                // GetUnreleasedFwVersionsWithNames). Раньше archived писался ТОЛЬКО при первичной
                // вставке: версия, убранная в архив на одной машине, у всех остальных так и оставалась
                // активной и продолжала висеть у них в модерации — ровно тот же класс расхождения, что
                // и с released до его появления здесь. Правило то же, монотонное: 0 → 1 применяем,
                // назад (разархивирование) не тянем — снимок мог быть собран до архивирования.
                var advances = (localStatus == "active" && incomingStatus != "active") ||
                               (localReleased == 0 && fv.Released != 0) ||
                               (localArchived == 0 && fv.Archived != 0) || fieldsChanged;
                if (!advances) continue;

                counts.FwVersions++;
                if (!apply) continue;
                ExecuteNonQuery("""
                    UPDATE fw_versions SET status=@st, released=@rel, archived=@arch, io_map_path=@io, instructions_path=@instr,
                        hmi_path=@hmi, executable_hint=@eh, hmi_executable_hint=@heh, modbus_map_path=@mb,
                        description=@desc, launch_types=@lt, tags=@tags
                    WHERE id=@id
                    """, cmd =>
                {
                    cmd.Parameters.AddWithValue("@desc", newDesc);
                    cmd.Parameters.AddWithValue("@lt", newLaunchTypes);
                    cmd.Parameters.AddWithValue("@tags", newTags);
                    cmd.Parameters.AddWithValue("@st", localStatus == "active" ? incomingStatus : localStatus);
                    cmd.Parameters.AddWithValue("@rel", localReleased != 0 ? 1 : fv.Released);
                    cmd.Parameters.AddWithValue("@arch", localArchived != 0 ? 1 : fv.Archived);
                    cmd.Parameters.AddWithValue("@io", newIoMap);
                    cmd.Parameters.AddWithValue("@instr", newInstr);
                    cmd.Parameters.AddWithValue("@hmi", newHmi);
                    cmd.Parameters.AddWithValue("@eh", newExecHint);
                    cmd.Parameters.AddWithValue("@heh", newHmiExecHint);
                    cmd.Parameters.AddWithValue("@mb", newModbus);
                    cmd.Parameters.AddWithValue("@id", id);
                });
                continue;
            }

            // No local row at all — if the source had already deleted it, there's nothing to
            // materialize: inserting it just to immediately hide it behind deleted_at would be
            // pointless (and would leave a phantom row with no matching on-disk folder on THIS
            // machine to ever clean up).
            if (!string.IsNullOrEmpty(fv.DeletedAt)) continue;

            counts.FwVersions++;
            if (!apply) continue;

            ExecuteNonQuery("""
                INSERT INTO fw_versions
                   (subtype_id, controller_id, eq_prefix, sub_prefix, hw_version, sw_version,
                    dt_str, version_raw, filename, disk_path, local_path, description, changelog,
                    launch_types, io_map_path, instructions_path, hmi_path, executable_hint, hmi_executable_hint,
                    modbus_map_path, is_opc, request_num,
                    upload_date, archived, tags, status, released, sync_id, config_name)
                VALUES(@subtype_id,@controller_id,@eq_prefix,@sub_prefix,@hw_version,@sw_version,
                    @dt_str,@version_raw,@filename,@disk_path,@local_path,@description,@changelog,
                    @launch_types,@io_map_path,@instructions_path,@hmi_path,@executable_hint,@hmi_executable_hint,
                    @modbus_map_path,@is_opc,@request_num,
                    @upload_date,@archived,@tags,@status,@released,@sync_id,@config_name)
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("@config_name", fv.ConfigName ?? "");
                // Прошивка заводится с ТЕМ ЖЕ sync_id, что у отправителя, — иначе строка «та же
                // самая», но связать её с оригиналом было бы уже нечем. Пустой (старый экспорт) —
                // заводим свой: он ничего не ломает, а следующая синхронизация с обновлённой машины
                // перенимет её значение по натуральному ключу.
                cmd.Parameters.AddWithValue("@sync_id", string.IsNullOrEmpty(fv.SyncId) ? Guid.NewGuid().ToString() : fv.SyncId);
                cmd.Parameters.AddWithValue("@subtype_id", subId.Value);
                cmd.Parameters.AddWithValue("@controller_id", ctrlId.Value);
                cmd.Parameters.AddWithValue("@eq_prefix", fv.EqPrefix);
                cmd.Parameters.AddWithValue("@sub_prefix", fv.SubPrefix);
                cmd.Parameters.AddWithValue("@hw_version", fv.HwVersion);
                cmd.Parameters.AddWithValue("@sw_version", fv.SwVersion);
                cmd.Parameters.AddWithValue("@dt_str", fv.DtStr);
                cmd.Parameters.AddWithValue("@version_raw", fv.VersionRaw);
                cmd.Parameters.AddWithValue("@filename", fv.Filename);
                cmd.Parameters.AddWithValue("@disk_path", fv.DiskPath);
                cmd.Parameters.AddWithValue("@local_path", fv.LocalPath);
                cmd.Parameters.AddWithValue("@description", fv.Description);
                cmd.Parameters.AddWithValue("@changelog", fv.Changelog);
                cmd.Parameters.AddWithValue("@launch_types", fv.LaunchTypes);
                cmd.Parameters.AddWithValue("@io_map_path", fv.IoMapPath);
                cmd.Parameters.AddWithValue("@instructions_path", fv.InstructionsPath);
                cmd.Parameters.AddWithValue("@hmi_path", fv.HmiPath);
                cmd.Parameters.AddWithValue("@executable_hint", fv.ExecutableHint);
                cmd.Parameters.AddWithValue("@hmi_executable_hint", fv.HmiExecutableHint);
                cmd.Parameters.AddWithValue("@modbus_map_path", fv.ModbusMapPath);
                cmd.Parameters.AddWithValue("@is_opc", fv.IsOpc);
                cmd.Parameters.AddWithValue("@request_num", fv.RequestNum);
                cmd.Parameters.AddWithValue("@upload_date", string.IsNullOrEmpty(fv.UploadDate) ? NowIso() : fv.UploadDate);
                cmd.Parameters.AddWithValue("@archived", fv.Archived);
                cmd.Parameters.AddWithValue("@tags", fv.Tags);
                cmd.Parameters.AddWithValue("@status", string.IsNullOrEmpty(fv.Status) ? "active" : fv.Status);
                cmd.Parameters.AddWithValue("@released", fv.Released);
            });
        }

        // ── Узкий канал доставки решений модерации (см. ExportedModerationDecision). ИДЁТ ПОСЛЕ
        //    блока fw_versions выше намеренно: решение может относиться к строке, которую этот же
        //    импорт только что и вставил.
        ApplyModerationDecisions(data.ModerationDecisions, counts, apply, subtypeSyncToId, controllerSyncToId);

        ImportParamFiles(data, subtypeSyncToId, counts, apply, authoritative);
        ImportPassports(data, subtypeSyncToId, counts, apply, authoritative);

        return counts;
    }

    /// <summary>Синхронизация шаблонов паспортов шкафов. Правила дословно те же, что у файлов
    /// параметров (см. ImportParamFiles ниже — там же разбор, почему именно так и чем кончалось
    /// «только добавлять»):
    ///   • соотнесение по sync_id, при первом контакте двух баз — откат на натуральный ключ
    ///     «подтип + название» и усыновление входящего идентификатора;
    ///   • archived=1 — положительный тумбстоун, снимает запись и здесь;
    ///   • локальная архивация постоянна: снятую здесь запись не воскрешает входящая живая копия;
    ///   • совпавшая живая строка обновляется (дата/описание/имя файла — от более свежей загрузки,
    ///     теги — объединением).
    ///
    /// disk_path у совпавшей строки НЕ трогается: он абсолютный и записан машиной-источником;
    /// открывающая сторона приводит его к своему корню (FirmwarePathLocalizer). Исключение — новое
    /// имя файла при перезаливке: оно от корня не зависит.
    ///
    /// data.Passports == null — снимок писало приложение, которое о паспортах ещё не знает: не
    /// трогаем ничего (иначе «у отправителя их нет» прочиталось бы как «удалить все»).</summary>
    private void ImportPassports(HierarchyExportData data, Dictionary<string, int> subtypeSyncToId,
        ImportCounts counts, bool apply, bool authoritative)
    {
        if (data.Passports is null) return;

        var incomingSyncIds = new HashSet<string>(StringComparer.Ordinal);
        var claimed = new HashSet<int>();

        foreach (var pp in data.Passports)
        {
            if (!string.IsNullOrEmpty(pp.SyncId)) incomingSyncIds.Add(pp.SyncId);

            var subId = ResolveId("equipment_subtypes", pp.SubtypeSyncId, subtypeSyncToId, "name", pp.SubtypeName, pp.GroupName);
            if (subId is null) continue;

            var local = FindPassportBySyncId(pp.SyncId);
            var adoptSyncId = false;
            if (local is null)
            {
                local = FindLivePassport(subId.Value, pp.Name);
                if (local is not null && local.Id is not null && claimed.Contains(local.Id.Value)) local = null;
                adoptSyncId = local is not null && !string.IsNullOrEmpty(pp.SyncId) && local.SyncId != pp.SyncId;
            }

            if (local is null)
            {
                // Строки нет, а входящая уже снята — заводить её только чтобы тут же спрятать под
                // archived значит показать коллеге фантом (та же логика, что у param_files).
                if (pp.Archived != 0) continue;

                counts.Passports++;
                if (!apply) continue;
                AddPassport(new Domain.PassportTemplate
                {
                    SubtypeId = subId.Value,
                    Name = pp.Name,
                    Filename = pp.Filename,
                    DiskPath = pp.DiskPath,
                    Description = pp.Description,
                    UploadDate = string.IsNullOrEmpty(pp.UploadDate) ? NowIso() : pp.UploadDate,
                    Tags = pp.Tags,
                    SyncId = pp.SyncId,
                });
                continue;
            }

            var localId = local.Id!.Value;
            claimed.Add(localId);
            if (apply && adoptSyncId) SetPassportSyncId(localId, pp.SyncId);

            if (local.Archived) continue;

            if (pp.Archived != 0)
            {
                counts.PassportsRemoved++;
                if (apply) DeletePassport(localId);
                continue;
            }

            var incomingNewer = string.CompareOrdinal(pp.UploadDate, local.UploadDate) > 0;
            var newDescription = local.Description;
            if (!string.IsNullOrWhiteSpace(pp.Description) &&
                (incomingNewer || string.IsNullOrWhiteSpace(local.Description)))
                newDescription = pp.Description;
            var newUploadDate = incomingNewer ? pp.UploadDate : local.UploadDate;
            // Имя файла — только от более свежей загрузки: паспорт могли перезалить в другом формате
            // (docx вместо pdf), и тогда «Открыть» обязано вести на новый файл. Назад не тянем.
            var newFilename = incomingNewer && !string.IsNullOrEmpty(pp.Filename) ? pp.Filename : local.Filename;

            var localTagList = Services.TagString.Parse(local.Tags);
            var haveTags = new HashSet<string>(localTagList, StringComparer.OrdinalIgnoreCase);
            var addedTags = Services.TagString.Parse(pp.Tags).Where(t => haveTags.Add(t)).ToList();
            var newTags = addedTags.Count == 0 ? local.Tags : Services.TagString.Join(localTagList.Concat(addedTags));

            if (newDescription == local.Description && newUploadDate == local.UploadDate &&
                newFilename == local.Filename && newTags == local.Tags)
                continue;

            counts.PassportsUpdated++;
            if (!apply) continue;
            UpdatePassportUpload(localId, local.DiskPath, newFilename, newDescription, newUploadDate);
            if (newTags != local.Tags) UpdatePassportTags(localId, newTags);
        }

        // Эталонная синхронизация: паспорта, которых в полном снимке отправителя нет вовсе (ни живых,
        // ни архивных) — тот же случай, что тумбстоуном не закрывается в принципе (мусорная строка
        // завелась на чужой машине, «эталонная» её никогда не видела). Архивируем, а не удаляем:
        // файл на диске остаётся, решение уезжает дальше тумбстоуном.
        if (!authoritative) return;

        var localLive = new List<(int Id, string SyncId)>();
        using (var r = ExecuteReader("SELECT id, sync_id FROM passports WHERE archived = 0 AND sync_id IS NOT NULL AND sync_id != ''"))
            while (r.Read())
                localLive.Add((r.GetInt32(0), r.GetString(1)));

        foreach (var (id, syncId) in localLive)
        {
            if (incomingSyncIds.Contains(syncId) || claimed.Contains(id)) continue;
            counts.PassportsRemoved++;
            if (apply) DeletePassport(id);
        }
    }

    /// <summary>Синхронизация файлов параметров. Раньше здесь было три строки: «нашли по тройке
    /// (подтип + производитель + имя файла) → пропустить, не нашли → вставить». Из-за этого
    /// (а) удаление/архивация записи не уезжали к коллегам вообще (в снимок архивные не попадали, а
    /// для аддитивного импорта «строки нет» неотличимо от «эта машина о ней ещё не знает»),
    /// (б) уже совпавшая строка не обновлялась никогда — ни свежая дата перезаливки, ни описание, ни
    /// теги до коллег не доезжали, (в) переименование файла выглядело как новая запись, а старая
    /// оставалась висеть. Итог у Ильи: «у меня 2 записи, а у коллеги 4, и все не те».
    ///
    /// Теперь ровно та же схема, что давно работает для иерархии:
    ///   • соотнесение по sync_id; при первом контакте двух независимо заведённых баз (у обеих строк
    ///     свои GUID) — откат на натуральный ключ и «усыновление» входящего sync_id, после чего
    ///     стороны говорят об одной и той же строке уже по идентификатору;
    ///   • архив (archived=1) — положительный тумбстоун: приезжает как «снято» и архивируется здесь;
    ///   • локальная архивация постоянна и не воскрешается входящей «живой» копией с машины, которая
    ///     об удалении ещё не знает (правило 1 у fw_versions, дословно);
    ///   • совпавшая живая строка обновляется: дата/описание — от более свежей загрузки, теги —
    ///     объединением (тег добавляют уже разошедшейся записи, поэтому именно union, а не замена).
    ///
    /// disk_path у совпавшей строки НЕ трогается: это абсолютный путь машины-источника, у получателя
    /// свой корень/буква диска (см. FirmwarePathLocalizer). Он копируется только при вставке новой
    /// строки — ровно как было до этой правки.</summary>
    private void ImportParamFiles(HierarchyExportData data, Dictionary<string, int> subtypeSyncToId,
        ImportCounts counts, bool apply, bool authoritative)
    {
        var incomingSyncIds = new HashSet<string>(StringComparer.Ordinal);
        // sync_id локальных строк, которые этот проход уже «занял» под конкретную входящую запись —
        // иначе две входящие строки с одинаковым натуральным ключом (такое бывает у снимка со старой
        // версии, где тройка не была уникальной) обе усыновили бы одну и ту же локальную строку.
        var claimed = new HashSet<int>();

        foreach (var pf in data.ParamFiles)
        {
            if (!string.IsNullOrEmpty(pf.SyncId)) incomingSyncIds.Add(pf.SyncId);

            var subId = ResolveId("equipment_subtypes", pf.SubtypeSyncId, subtypeSyncToId, "name", pf.SubtypeName, pf.GroupName);
            if (subId is null) continue;

            var local = FindParamFileBySyncId(pf.SyncId);
            var adoptSyncId = false;
            if (local is null)
            {
                // Первый контакт: соотносим по натуральному ключу. Тот же ключ, что и раньше, и
                // намеренно БЕЗ disk_path — он абсолютный и на разных машинах разный, из-за чего
                // старое сравнение с ним не совпадало никогда и каждый цикл синхронизации вставлял
                // те же файлы заново (178 строк на 2 реальных файла).
                local = FindLiveParamFile(subId.Value, pf.Manufacturer, pf.Filename);
                if (local is not null && local.Id is not null && claimed.Contains(local.Id.Value)) local = null;
                adoptSyncId = local is not null && !string.IsNullOrEmpty(pf.SyncId) && local.SyncId != pf.SyncId;
            }

            if (local is null)
            {
                // Строки нет и входящая уже снята — материализовать нечего: завести её только чтобы
                // тут же спрятать под archived, значит показать коллеге фантом (та же логика, что у
                // входящего tombstone'а fw_versions без локальной строки).
                if (pf.Archived != 0) continue;

                counts.ParamFiles++;
                if (!apply) continue;
                AddParamFile(new Domain.ParamFile
                {
                    SubtypeId = subId.Value,
                    Manufacturer = pf.Manufacturer,
                    Filename = pf.Filename,
                    DiskPath = pf.DiskPath,
                    Description = pf.Description,
                    UploadDate = string.IsNullOrEmpty(pf.UploadDate) ? NowIso() : pf.UploadDate,
                    Tags = pf.Tags,
                    SyncId = pf.SyncId,
                });
                continue;
            }

            var localId = local.Id!.Value;
            claimed.Add(localId);
            if (apply && adoptSyncId) SetParamFileSyncId(localId, pf.SyncId);

            // Локальная архивация постоянна: снятую здесь запись не воскрешает входящая живая копия.
            if (local.Archived) continue;

            if (pf.Archived != 0)
            {
                counts.ParamFilesRemoved++;
                if (apply) DeleteParamFile(localId);
                continue;
            }

            var incomingNewer = string.CompareOrdinal(pf.UploadDate, local.UploadDate) > 0;
            var newDescription = local.Description;
            if (!string.IsNullOrWhiteSpace(pf.Description) &&
                (incomingNewer || string.IsNullOrWhiteSpace(local.Description)))
                newDescription = pf.Description;
            var newUploadDate = incomingNewer ? pf.UploadDate : local.UploadDate;

            // Теги — объединение, а не замена: тег почти всегда навешивают уже разошедшейся по
            // машинам записи, и ни одна машина не должна терять свои (дословно как у fw_versions).
            var localTagList = Services.TagString.Parse(local.Tags);
            var haveTags = new HashSet<string>(localTagList, StringComparer.OrdinalIgnoreCase);
            var addedTags = Services.TagString.Parse(pf.Tags).Where(t => haveTags.Add(t)).ToList();
            var newTags = addedTags.Count == 0 ? local.Tags : Services.TagString.Join(localTagList.Concat(addedTags));

            if (newDescription == local.Description && newUploadDate == local.UploadDate && newTags == local.Tags)
                continue;

            counts.ParamFilesUpdated++;
            if (!apply) continue;
            UpdateParamFileUpload(localId, local.DiskPath, newDescription, newUploadDate);
            if (newTags != local.Tags) UpdateParamFileTags(localId, newTags);
        }

        // ── Эталонная синхронизация: записи параметров, которых в ПОЛНОМ снимке отправителя нет
        //    вовсе (ни живых, ни архивных) — та же механика, что уже есть у подтипов/типов/
        //    контроллеров/модификаций выше. Закрывает ровно тот случай, который тумбстоуном не
        //    закрывается в принципе: мусорная строка завелась на чужой машине, «эталонная» её никогда
        //    не видела, поэтому надгробия для неё нет и быть не может.
        //    Архивируем, а не удаляем: файл на диске остаётся, запись просто перестаёт мешаться, и
        //    решение уезжает дальше тумбстоуном.
        //    ParamFilesHaveSync — обязательный предохранитель: снимок со старой версии приложения не
        //    содержит ни sync_id, ни архивных строк, и принять его за полный означало бы вычистить у
        //    всех получателей всю таблицу параметров разом.
        if (!authoritative || !data.ParamFilesHaveSync) return;

        var localLive = new List<(int Id, string SyncId)>();
        using (var r = ExecuteReader("SELECT id, sync_id FROM param_files WHERE archived = 0 AND sync_id IS NOT NULL AND sync_id != ''"))
            while (r.Read())
                localLive.Add((r.GetInt32(0), r.GetString(1)));

        foreach (var (id, syncId) in localLive)
        {
            // claimed — строки, которые цикл выше уже соотнёс с входящей записью. Проверять только
            // incomingSyncIds нельзя: при первом контакте строка совпадает по натуральному ключу и
            // усыновляет чужой sync_id, а в режиме предпросмотра (apply=false) усыновление физически
            // не выполняется — без этой проверки предпросмотр показывал бы «будет снято» для строк,
            // которые на самом деле спокойно совпали.
            if (incomingSyncIds.Contains(syncId) || claimed.Contains(id)) continue;
            counts.ParamFilesRemoved++;
            if (apply) DeleteParamFile(id);
        }
    }

    // ── Sync-id-aware lookup helpers ─────────────────────────────────────────

    /// <summary>Локальная строка fw_versions в том объёме, который нужен импорту (см.
    /// ImportHierarchyDataCore) — всё, что он сравнивает и переносит.</summary>
    private sealed record LocalFwRow(int Id, string SyncId, string Status, int Released, int Archived,
        string IoMapPath, string InstructionsPath, string HmiPath, string ExecutableHint,
        string HmiExecutableHint, string ModbusMapPath, string DeletedAt, string DiskPath,
        string Description, string LaunchTypes, string Tags,
        int SubtypeId, int ControllerId, string VersionRaw, string ConfigName);

    /// <summary>«Та же самая» прошивка в локальной базе: СНАЧАЛА по sync_id, и только если его нет
    /// (или строка по нему не нашлась) — по прежнему натуральному ключу подтип+контроллер+version_raw.
    ///
    /// Порядок именно такой, и в этом весь смысл столбца. Натуральный ключ не переживает правок,
    /// которые программа сама же и разрешает: «переназначить версию другому контроллеру» меняет
    /// controller_id, переписывание hw модификации — version_raw (а с ним и имя папки). После любой из
    /// них снимок с машины-автора переставал находить соответствующую строку у получателя: приехавшее
    /// надгробие/вывод из модерации применять было не к чему, рядом вставлялся дубликат, а исходная
    /// запись получателя оставалась висеть в очереди модерации навсегда. sync_id переживает обе
    /// правки, поэтому состояние доезжает до той же строки. Откат на натуральный ключ обязателен для
    /// первого контакта (у сторон разные sync_id) и для снимков со старой версии приложения.</summary>
    /// <param name="configName">Имя конфигурации из снимка; null/пусто — обычная запись (снимок со
    /// старой версии приложения этого поля не содержит вовсе).</param>
    private LocalFwRow? FindFwVersionRow(string syncId, int subtypeId, int controllerId, string versionRaw,
        string? configName)
    {
        const string cols = """
            id, sync_id, status, released, archived, io_map_path, instructions_path, hmi_path,
            executable_hint, hmi_executable_hint, modbus_map_path, deleted_at, disk_path,
            description, launch_types, tags, subtype_id, controller_id, version_raw, config_name
            """;

        if (!string.IsNullOrEmpty(syncId))
        {
            using var bySync = ExecuteReader($"SELECT {cols} FROM fw_versions WHERE sync_id=@sy",
                cmd => cmd.Parameters.AddWithValue("@sy", syncId));
            if (bySync.Read()) return ReadLocalFwRow(bySync);
        }

        // config_name — полноправная часть натурального ключа: у всех КОНФИГУРАЦИЙ одной прошивки
        // (см. столбец config_name в Database.cs) подтип, контроллер и version_raw совпадают, и без
        // имени варианта приёмник соотносил бы каждую следующую конфигурацию с той же самой локальной
        // строкой — все заготовленные варианты схлопывались бы в один с объединёнными тегами. Пустое
        // имя ищет ровно обычные записи, поэтому для снимка со старой версии приложения (где поля нет
        // вовсе) сопоставление остаётся ровно прежним.
        using var byKey = ExecuteReader(
            $"""
            SELECT {cols} FROM fw_versions
            WHERE subtype_id=@s AND controller_id=@c AND version_raw=@v
              AND COALESCE(config_name,'') = @cfg
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@s", subtypeId);
                cmd.Parameters.AddWithValue("@c", controllerId);
                cmd.Parameters.AddWithValue("@v", versionRaw);
                cmd.Parameters.AddWithValue("@cfg", configName ?? "");
            });
        return byKey.Read() ? ReadLocalFwRow(byKey) : null;
    }

    private static LocalFwRow ReadLocalFwRow(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        GetInt(r, "id"), GetString(r, "sync_id"), GetString(r, "status"), GetInt(r, "released"), GetInt(r, "archived"),
        GetString(r, "io_map_path"), GetString(r, "instructions_path"), GetString(r, "hmi_path"),
        GetString(r, "executable_hint"), GetString(r, "hmi_executable_hint"), GetString(r, "modbus_map_path"),
        GetString(r, "deleted_at"), GetString(r, "disk_path"),
        GetString(r, "description"), GetString(r, "launch_types", "[]"), GetString(r, "tags"),
        GetInt(r, "subtype_id"), GetInt(r, "controller_id"), GetString(r, "version_raw"), GetString(r, "config_name"));

    private (int Id, string Name, int Prefix, int SortOrder, string SyncId, string UpdatedAt)? FindBySyncOrName(string table, string syncId, string nameCol, string name)
    {
        if (!string.IsNullOrEmpty(syncId))
        {
            using var r1 = ExecuteReader($"SELECT id, {nameCol}, prefix, sort_order, sync_id, updated_at FROM {table} WHERE sync_id=@sy", cmd => cmd.Parameters.AddWithValue("@sy", syncId));
            if (r1.Read()) return (r1.GetInt32(0), r1.GetString(1), r1.GetInt32(2), r1.GetInt32(3), GetString(r1, "sync_id"), GetString(r1, "updated_at"));
        }
        using var r2 = ExecuteReader($"SELECT id, {nameCol}, prefix, sort_order, sync_id, updated_at FROM {table} WHERE {nameCol}=@n", cmd => cmd.Parameters.AddWithValue("@n", name));
        return r2.Read() ? (r2.GetInt32(0), r2.GetString(1), r2.GetInt32(2), r2.GetInt32(3), GetString(r2, "sync_id"), GetString(r2, "updated_at")) : null;
    }

    private (int Id, string Name, int Prefix, string Folder, int SortOrder, string SyncId, string UpdatedAt)? FindSubtype(string syncId, int groupId, string name)
    {
        if (!string.IsNullOrEmpty(syncId))
        {
            using var r1 = ExecuteReader("SELECT id, name, prefix, folder_name, sort_order, sync_id, updated_at FROM equipment_subtypes WHERE sync_id=@sy AND group_id=@g",
                cmd => { cmd.Parameters.AddWithValue("@sy", syncId); cmd.Parameters.AddWithValue("@g", groupId); });
            if (r1.Read()) return (r1.GetInt32(0), r1.GetString(1), r1.GetInt32(2), r1.GetString(3), r1.GetInt32(4), GetString(r1, "sync_id"), GetString(r1, "updated_at"));
        }
        using var r2 = ExecuteReader("SELECT id, name, prefix, folder_name, sort_order, sync_id, updated_at FROM equipment_subtypes WHERE group_id=@g AND name=@n",
            cmd => { cmd.Parameters.AddWithValue("@g", groupId); cmd.Parameters.AddWithValue("@n", name); });
        return r2.Read() ? (r2.GetInt32(0), r2.GetString(1), r2.GetInt32(2), r2.GetString(3), r2.GetInt32(4), GetString(r2, "sync_id"), GetString(r2, "updated_at")) : null;
    }

    private (int Id, string Name, int HwVersion, int SortOrder, string Description, string SyncId, string UpdatedAt)? FindModification(string syncId, int controllerId, string displayName)
    {
        if (!string.IsNullOrEmpty(syncId))
        {
            using var r1 = ExecuteReader("SELECT id, display_name, hw_version, sort_order, description, sync_id, updated_at FROM controller_modifications WHERE sync_id=@sy AND controller_id=@c",
                cmd => { cmd.Parameters.AddWithValue("@sy", syncId); cmd.Parameters.AddWithValue("@c", controllerId); });
            if (r1.Read()) return (r1.GetInt32(0), r1.GetString(1), r1.GetInt32(2), r1.GetInt32(3), r1.GetString(4), GetString(r1, "sync_id"), GetString(r1, "updated_at"));
        }
        using var r2 = ExecuteReader("SELECT id, display_name, hw_version, sort_order, description, sync_id, updated_at FROM controller_modifications WHERE controller_id=@c AND display_name=@n",
            cmd => { cmd.Parameters.AddWithValue("@c", controllerId); cmd.Parameters.AddWithValue("@n", displayName); });
        return r2.Read() ? (r2.GetInt32(0), r2.GetString(1), r2.GetInt32(2), r2.GetInt32(3), r2.GetString(4), GetString(r2, "sync_id"), GetString(r2, "updated_at")) : null;
    }

    /// <summary>Зеркалит приехавшее удаление прошивки: чистит файлы (best-effort) и ставит локальный
    /// tombstone. Выделено из блока fw_versions, потому что ровно то же самое должен делать узкий
    /// канал модерации (ApplyModerationDecisions ниже) — удаление, сделанное на машине наладчика,
    /// обязано доезжать так же, как сделанное на машине администратора.
    ///
    /// Файлы — только если они больше никому не нужны. Прошивка, привязанная к нескольким подтипам
    /// шкафов, лежит на диске ОДИН раз, а записей у неё несколько (см. FirmwareSubtypeLinkService):
    /// отвязка лишнего подтипа приезжает таким же tombstone'ом, и без этой проверки она уносила бы
    /// саму прошивку у всех. Оставшаяся неудалённой папка — это нехватка места на диске, а не
    /// нарушение целостности: источник истины «версия удалена» — сам tombstone, он и продолжает
    /// разъезжаться по машинам.</summary>
    private void MirrorFwTombstone(int id, string localDiskPath, string localHmi, string versionRaw)
    {
        var filesShared = IsDiskPathSharedByOtherVersions(localDiskPath, id);

        try { if (!filesShared && !string.IsNullOrEmpty(localDiskPath) && Directory.Exists(localDiskPath)) Infrastructure.FileSystemHelpers.RmtreeSafe(localDiskPath); }
        catch { /* best-effort, same as SettingsView.DeleteFirmware_Click */ }
        try
        {
            if (!filesShared && !string.IsNullOrEmpty(localHmi) && localHmi.Contains(versionRaw, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(localHmi)) Infrastructure.FileSystemHelpers.RmtreeSafe(localHmi);
                else if (File.Exists(localHmi)) File.Delete(localHmi);
            }
        }
        catch { /* best-effort */ }

        TombstoneFwVersion(id);
    }

    /// <summary>Применяет приехавшие решения модерации (см. ExportedModerationDecision). Это ОТДЕЛЬНЫЙ
    /// от fw_versions канал, и существует он ровно потому, что fw_versions в снимке — состояние базы
    /// машины-ЭКСПОРТЁРА: полный снимок выгружает только администратор, поэтому решение, принятое
    /// наладчиком у себя, до появления этой секции физически не имело пути к остальным машинам.
    ///
    /// Правила — те же, что у блока fw_versions выше, ни на йоту не шире:
    /// • локальный tombstone постоянен: приехавшее решение никогда не воскрешает удалённую строку;
    /// • приехавший tombstone зеркалится (MirrorFwTombstone) — включая уборку файлов;
    /// • released 0→1, archived 0→1, status active→иной — только вперёд, назад никогда.
    /// Из монотонности следует главное свойство: две машины, принявшие РАЗНЫЕ решения по одной
    /// версии, сходятся на объединении решений независимо от порядка обмена, а повторное применение
    /// того же снимка ничего не меняет.
    ///
    /// Прав этот канал не выдаёт: он доставляет уже принятое решение, а кто вправе его принимать,
    /// решает роль на стороне UI (страница «Модерация прошивок», см. RolesConfig.RoleAccess).
    ///
    /// Строки, для которой решение, здесь ещё нет — пропускаем: она приедет обычным блоком
    /// fw_versions (в снимке она уже с нужным состоянием, если экспортёр его к тому моменту принял),
    /// а решение всё равно ляжет в местный журнал ниже и уедет дальше.</summary>
    private void ApplyModerationDecisions(List<ExportedModerationDecision>? decisions, ImportCounts counts, bool apply,
        Dictionary<string, int> subtypeSyncToId, Dictionary<string, int> controllerSyncToId)
    {
        if (decisions is null || decisions.Count == 0) return;

        foreach (var d in decisions)
        {
            if (string.IsNullOrEmpty(d.VersionRaw)) continue;
            var subId = ResolveId("equipment_subtypes", d.SubtypeSyncId, subtypeSyncToId, "name", d.SubtypeName, d.GroupName);
            var ctrlId = ResolveId("controller_models", d.ControllerSyncId, controllerSyncToId, "name", d.ControllerName);
            if (subId is null || ctrlId is null) continue;

            (int Id, int Released, int Archived, string Status, string DeletedAt, string DiskPath, string HmiPath)? row = null;
            using (var r = ExecuteReader("""
                SELECT id, released, archived, status, deleted_at, disk_path, hmi_path
                FROM fw_versions WHERE subtype_id=@s AND controller_id=@c AND version_raw=@v
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("@s", subId.Value);
                cmd.Parameters.AddWithValue("@c", ctrlId.Value);
                cmd.Parameters.AddWithValue("@v", d.VersionRaw);
            }))
                if (r.Read())
                    row = (r.GetInt32(0), GetInt(r, "released"), GetInt(r, "archived"),
                        GetString(r, "status", "active"), GetString(r, "deleted_at"),
                        GetString(r, "disk_path"), GetString(r, "hmi_path"));
            if (row is null) continue;

            var (id, localReleased, localArchived, localStatusRaw, localDeletedAt, localDiskPath, localHmi) = row.Value;
            if (!string.IsNullOrEmpty(localDeletedAt)) continue;

            if (!string.IsNullOrEmpty(d.DeletedAt))
            {
                counts.ModerationApplied++;
                if (apply) MirrorFwTombstone(id, localDiskPath, localHmi, d.VersionRaw);
                continue;
            }

            var localStatus = string.IsNullOrEmpty(localStatusRaw) ? "active" : localStatusRaw;
            var incomingStatus = string.IsNullOrEmpty(d.Status) ? "active" : d.Status;
            var newReleased = localReleased != 0 || d.Released != 0 ? 1 : 0;
            var newArchived = localArchived != 0 || d.Archived != 0 ? 1 : 0;
            var newStatus = localStatus == "active" ? incomingStatus : localStatus;
            if (newReleased == localReleased && newArchived == localArchived && newStatus == localStatus) continue;

            counts.ModerationApplied++;
            if (!apply) continue;
            ExecuteNonQuery("UPDATE fw_versions SET released=@rel, archived=@arch, status=@st WHERE id=@id", cmd =>
            {
                cmd.Parameters.AddWithValue("@rel", newReleased);
                cmd.Parameters.AddWithValue("@arch", newArchived);
                cmd.Parameters.AddWithValue("@st", newStatus);
                cmd.Parameters.AddWithValue("@id", id);
            });
        }

        // Принимаем решения в СВОЙ журнал — чтобы эта машина пересылала их дальше своим собственным
        // экспортом, а не теряла в момент, когда администратор перезапишет общий конфиг целиком.
        // Делается независимо от того, нашлась ли строка: версия могла ещё не доехать сюда, а решение
        // по ней всё равно должно продолжить путь.
        if (apply) AbsorbModerationDecisions(decisions);
    }

    /// <summary>Resolves a hierarchy row's local id: prefer the sync_id map built earlier in THIS
    /// import pass (covers rows renamed earlier in the same batch), else look the row up fresh by
    /// sync_id, else fall back to name (older exports, or first contact between two independently-
    /// built databases that happen to use the same names already).</summary>
    private int? ResolveId(string table, string syncId, Dictionary<string, int> syncMap, string nameCol, string name, string? groupName = null)
    {
        if (!string.IsNullOrEmpty(syncId) && syncMap.TryGetValue(syncId, out var mapped)) return mapped;
        if (!string.IsNullOrEmpty(syncId))
        {
            var byId = ExecuteScalar($"SELECT id FROM {table} WHERE sync_id=@sy", cmd => cmd.Parameters.AddWithValue("@sy", syncId));
            if (byId is long l) return (int)l;
        }
        if (table == "equipment_subtypes" && groupName is not null)
        {
            var byName = ExecuteScalar("""
                SELECT es.id FROM equipment_subtypes es JOIN equipment_groups eg ON es.group_id = eg.id
                WHERE eg.name=@g AND es.name=@n
                """, cmd => { cmd.Parameters.AddWithValue("@g", groupName); cmd.Parameters.AddWithValue("@n", name); });
            return byName is long l2 ? (int)l2 : null;
        }
        var byName2 = ExecuteScalar($"SELECT id FROM {table} WHERE {nameCol}=@n", cmd => cmd.Parameters.AddWithValue("@n", name));
        return byName2 is long l3 ? (int)l3 : null;
    }

    /// <summary>Replace old_root prefix with new_root in fw_versions/param_files path columns
    /// (normalizes separators so mixed forward/backward slashes still match).</summary>
    public void RemapFwPaths(string oldRoot, string newRoot) => RemapPathPrefix(oldRoot, newRoot);

    /// <summary>Same prefix-replace as RemapFwPaths but for an arbitrary path segment — used after
    /// renaming an equipment group/subtype's disk folder (see SettingsView RenameGroup/RenameSubtype),
    /// where only the group/subtype segment of the path changes, not the whole root. Returns how many
    /// rows were touched, so the caller can report it.</summary>
    public int RemapPathPrefix(string oldPrefix, string newPrefix)
    {
        var oldNorm = Path.TrimEndingDirectorySeparator(oldPrefix);
        var newNorm = Path.TrimEndingDirectorySeparator(newPrefix);
        if (string.IsNullOrEmpty(oldNorm) || oldNorm == newNorm) return 0;

        (string Value, bool Changed) Remap(string val)
        {
            if (string.IsNullOrEmpty(val)) return (val, false);
            var norm = Path.GetFullPath(val);
            if (string.Equals(norm, oldNorm, StringComparison.OrdinalIgnoreCase))
                return (newNorm, true);
            if (norm.StartsWith(oldNorm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return (newNorm + norm[oldNorm.Length..], true);
            return (val, false);
        }

        var changedCount = 0;

        var fwRows = new List<(int Id, string DiskPath, string IoMapPath, string InstructionsPath, string HmiPath, string ModbusMapPath)>();
        using (var r = ExecuteReader("SELECT id, disk_path, io_map_path, instructions_path, hmi_path, modbus_map_path FROM fw_versions"))
            while (r.Read())
                fwRows.Add((r.GetInt32(0), GetString(r, "disk_path"), GetString(r, "io_map_path"), GetString(r, "instructions_path"),
                    GetString(r, "hmi_path"), GetString(r, "modbus_map_path")));

        foreach (var row in fwRows)
        {
            var (disk, c1) = Remap(row.DiskPath);
            var (io, c2) = Remap(row.IoMapPath);
            var (instr, c3) = Remap(row.InstructionsPath);
            var (hmi, c4) = Remap(row.HmiPath);
            var (modbus, c5) = Remap(row.ModbusMapPath);
            if (!c1 && !c2 && !c3 && !c4 && !c5) continue;
            ExecuteNonQuery("""
                UPDATE fw_versions SET disk_path=@d, io_map_path=@io, instructions_path=@instr,
                    hmi_path=@hmi, modbus_map_path=@mb WHERE id=@id
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("@d", disk);
                cmd.Parameters.AddWithValue("@io", io);
                cmd.Parameters.AddWithValue("@instr", instr);
                cmd.Parameters.AddWithValue("@hmi", hmi);
                cmd.Parameters.AddWithValue("@mb", modbus);
                cmd.Parameters.AddWithValue("@id", row.Id);
            });
            changedCount++;
        }

        var pfRows = new List<(int Id, string DiskPath)>();
        using (var r = ExecuteReader("SELECT id, disk_path FROM param_files"))
            while (r.Read())
                pfRows.Add((r.GetInt32(0), GetString(r, "disk_path")));

        foreach (var row in pfRows)
        {
            var (disk, changed) = Remap(row.DiskPath);
            if (!changed) continue;
            ExecuteNonQuery("UPDATE param_files SET disk_path=@d WHERE id=@id", cmd =>
            {
                cmd.Parameters.AddWithValue("@d", disk);
                cmd.Parameters.AddWithValue("@id", row.Id);
            });
            changedCount++;
        }

        // Паспорта лежат в дереве ПО (ПО\<тип>[\<подтип>]\Паспорт\<название>), поэтому их путь
        // задевает и смена корня, и переименование папки типа/подтипа — ровно как у прошивок выше.
        var passportRows = new List<(int Id, string DiskPath)>();
        using (var r = ExecuteReader("SELECT id, disk_path FROM passports"))
            while (r.Read())
                passportRows.Add((r.GetInt32(0), GetString(r, "disk_path")));

        foreach (var row in passportRows)
        {
            var (disk, changed) = Remap(row.DiskPath);
            if (!changed) continue;
            ExecuteNonQuery("UPDATE passports SET disk_path=@d WHERE id=@id", cmd =>
            {
                cmd.Parameters.AddWithValue("@d", disk);
                cmd.Parameters.AddWithValue("@id", row.Id);
            });
            changedCount++;
        }

        return changedCount;
    }
}
