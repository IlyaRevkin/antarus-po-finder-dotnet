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
                   fv.modbus_map_path, fv.deleted_at,
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
                    DeletedAt = GetString(r, "deleted_at"),
                    GroupName = GetString(r, "group_name"),
                    SubtypeName = GetString(r, "subtype_name"), SubtypeSyncId = GetString(r, "subtype_sync_id"),
                    CtrlName = GetString(r, "ctrl_name"), ControllerSyncId = GetString(r, "controller_sync_id"),
                });

        using (var r = ExecuteReader("""
            SELECT pf.filename, pf.disk_path, pf.description, pf.upload_date, pf.archived, pf.manufacturer,
                   es.name AS subtype_name, es.sync_id AS subtype_sync_id, eg.name AS group_name
            FROM param_files pf
            JOIN equipment_subtypes es ON pf.subtype_id = es.id
            JOIN equipment_groups   eg ON es.group_id   = eg.id
            WHERE pf.archived = 0
            ORDER BY pf.id
            """))
            while (r.Read())
                data.ParamFiles.Add(new ExportedParamFile
                {
                    Filename = r.GetString(0), DiskPath = r.GetString(1), Description = r.GetString(2),
                    UploadDate = r.GetString(3), Archived = r.GetInt32(4), Manufacturer = r.GetString(5),
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
    /// allowed_extensions, allowed_extensions_hmi — for exactly this one import. This is what closes
    /// the gap those three tables didn't have: a junk catalog row that originated on some OTHER
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
        };
        return new AuthoritativeSyncDiff(categories);
    }

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

        // ── fw_versions / param_files: additive-only, as before — each machine may have uploads
        //    the exporting one never saw, so absence locally never means "delete it". The one
        //    exception is status/released on an ALREADY-matched row: moderation only ever advances
        //    (active→rolled_back, unreleased→released — see the reservations block above for the
        //    same reasoning), so if the incoming copy is further along than the local row we adopt
        //    it; we never move a local row backwards to active/unreleased. Without this, a version
        //    moderated (tags added + released) on one machine before its FIRST export would insert
        //    fine there, but land back in every OTHER machine's moderation queue forever, since the
        //    natural-key match would just skip it as "already exists" without ever updating it.
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

            var existing = ExecuteReader(
                """
                SELECT id, status, released, io_map_path, instructions_path, hmi_path,
                       executable_hint, hmi_executable_hint, modbus_map_path, deleted_at, disk_path,
                       description, launch_types
                FROM fw_versions WHERE subtype_id=@s AND controller_id=@c AND version_raw=@v
                """, cmd =>
                {
                    cmd.Parameters.AddWithValue("@s", subId.Value);
                    cmd.Parameters.AddWithValue("@c", ctrlId.Value);
                    cmd.Parameters.AddWithValue("@v", fv.VersionRaw);
                });
            (int Id, string Status, int Released, string IoMapPath, string InstructionsPath, string HmiPath,
                string ExecutableHint, string HmiExecutableHint, string ModbusMapPath, string DeletedAt, string DiskPath,
                string Description, string LaunchTypes)? existingRow = null;
            using (existing)
                if (existing.Read())
                    existingRow = (existing.GetInt32(0), GetString(existing, "status"), GetInt(existing, "released"),
                        GetString(existing, "io_map_path"), GetString(existing, "instructions_path"), GetString(existing, "hmi_path"),
                        GetString(existing, "executable_hint"), GetString(existing, "hmi_executable_hint"), GetString(existing, "modbus_map_path"),
                        GetString(existing, "deleted_at"), GetString(existing, "disk_path"),
                        GetString(existing, "description"), GetString(existing, "launch_types", "[]"));

            if (existingRow is not null)
            {
                var (id, localStatus, localReleased, localIoMap, localInstr, localHmi, localExecHint, localHmiExecHint, localModbus, localDeletedAt, localDiskPath, localDesc, localLaunchTypes) = existingRow.Value;

                // Rule 1 — already deleted here: permanent, never revived by an incoming row that
                // just hasn't caught up yet (see class doc above).
                if (!string.IsNullOrEmpty(localDeletedAt)) continue;

                // Rule 2 — incoming tombstone not yet applied locally: mirror it.
                if (!string.IsNullOrEmpty(fv.DeletedAt))
                {
                    counts.FwVersionsRemoved++;
                    if (!apply) continue;

                    // Файлы — только если они больше никому не нужны. Прошивка, привязанная к
                    // нескольким подтипам шкафов, лежит на диске ОДИН раз, а записей у неё несколько
                    // (см. FirmwareSubtypeLinkService): отвязка лишнего подтипа приезжает сюда таким
                    // же tombstone'ом, и без этой проверки она уносила бы саму прошивку у всех.
                    var filesShared = IsDiskPathSharedByOtherVersions(localDiskPath, id);

                    try { if (!filesShared && !string.IsNullOrEmpty(localDiskPath) && Directory.Exists(localDiskPath)) Infrastructure.FileSystemHelpers.RmtreeSafe(localDiskPath); }
                    catch { /* best-effort, same as SettingsView.DeleteFirmware_Click */ }
                    try
                    {
                        if (!filesShared && !string.IsNullOrEmpty(localHmi) && localHmi.Contains(fv.VersionRaw, StringComparison.OrdinalIgnoreCase))
                        {
                            if (Directory.Exists(localHmi)) Infrastructure.FileSystemHelpers.RmtreeSafe(localHmi);
                            else if (File.Exists(localHmi)) File.Delete(localHmi);
                        }
                    }
                    // Same reasoning as the disk_path cleanup above: TombstoneFwVersion(id) right
                    // below is the actual source of truth for "this version is deleted" (it's what
                    // keeps propagating the tombstone to every other machine) — a leftover HMI folder
                    // that failed to delete is a disk-space nit, not a correctness problem.
                    catch { /* best-effort */ }

                    TombstoneFwVersion(id);
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

                var fieldsChanged = newIoMap != localIoMap || newInstr != localInstr || newHmi != localHmi ||
                                    newExecHint != localExecHint || newHmiExecHint != localHmiExecHint || newModbus != localModbus ||
                                    newDesc != localDesc || newLaunchTypes != localLaunchTypes;

                var advances = (localStatus == "active" && incomingStatus != "active") ||
                               (localReleased == 0 && fv.Released != 0) || fieldsChanged;
                if (!advances) continue;

                counts.FwVersions++;
                if (!apply) continue;
                ExecuteNonQuery("""
                    UPDATE fw_versions SET status=@st, released=@rel, io_map_path=@io, instructions_path=@instr,
                        hmi_path=@hmi, executable_hint=@eh, hmi_executable_hint=@heh, modbus_map_path=@mb,
                        description=@desc, launch_types=@lt
                    WHERE id=@id
                    """, cmd =>
                {
                    cmd.Parameters.AddWithValue("@desc", newDesc);
                    cmd.Parameters.AddWithValue("@lt", newLaunchTypes);
                    cmd.Parameters.AddWithValue("@st", localStatus == "active" ? incomingStatus : localStatus);
                    cmd.Parameters.AddWithValue("@rel", localReleased != 0 ? 1 : fv.Released);
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
                    upload_date, archived, tags, status, released)
                VALUES(@subtype_id,@controller_id,@eq_prefix,@sub_prefix,@hw_version,@sw_version,
                    @dt_str,@version_raw,@filename,@disk_path,@local_path,@description,@changelog,
                    @launch_types,@io_map_path,@instructions_path,@hmi_path,@executable_hint,@hmi_executable_hint,
                    @modbus_map_path,@is_opc,@request_num,
                    @upload_date,@archived,@tags,@status,@released)
                """, cmd =>
            {
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

        foreach (var pf in data.ParamFiles)
        {
            var subId = ResolveId("equipment_subtypes", pf.SubtypeSyncId, subtypeSyncToId, "name", pf.SubtypeName, pf.GroupName);
            if (subId is null) continue;

            // Matched by (subtype, manufacturer, filename) only — NOT disk_path, which is an
            // absolute path baked in on the EXPORTING machine (see HierarchyService.ParamsPath):
            // two machines almost never share the exact same root path/drive letter, so a
            // disk_path-inclusive match never hit and every sync cycle re-inserted the same file
            // as a "new" row (178 rows for 2 real files, one per sync round). Mirrors how
            // fw_versions is matched below (subtype_id+controller_id+version_raw, no path).
            var exists = ExecuteScalar(
                "SELECT 1 FROM param_files WHERE subtype_id=@s AND manufacturer=@m AND filename=@f", cmd =>
                {
                    cmd.Parameters.AddWithValue("@s", subId.Value);
                    cmd.Parameters.AddWithValue("@m", pf.Manufacturer);
                    cmd.Parameters.AddWithValue("@f", pf.Filename);
                });
            if (exists is not null) continue;

            counts.ParamFiles++;
            if (!apply) continue;

            ExecuteNonQuery("""
                INSERT INTO param_files(subtype_id, manufacturer, filename, disk_path, description, upload_date, archived)
                VALUES(@s,@m,@f,@d,@desc,@u,@a)
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("@s", subId.Value);
                cmd.Parameters.AddWithValue("@m", pf.Manufacturer);
                cmd.Parameters.AddWithValue("@f", pf.Filename);
                cmd.Parameters.AddWithValue("@d", pf.DiskPath);
                cmd.Parameters.AddWithValue("@desc", pf.Description);
                cmd.Parameters.AddWithValue("@u", string.IsNullOrEmpty(pf.UploadDate) ? NowIso() : pf.UploadDate);
                cmd.Parameters.AddWithValue("@a", pf.Archived);
            });
        }

        return counts;
    }

    // ── Sync-id-aware lookup helpers ─────────────────────────────────────────

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

        return changedCount;
    }
}
