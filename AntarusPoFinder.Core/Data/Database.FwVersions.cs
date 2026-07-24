using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    public int AddFwVersion(FwVersionRecord v)
    {
        ExecuteNonQuery("""
            INSERT INTO fw_versions
               (subtype_id,controller_id,eq_prefix,sub_prefix,hw_version,sw_version,
                dt_str,version_raw,filename,disk_path,local_path,description,changelog,
                launch_types,io_map_path,instructions_path,hmi_path,executable_hint,hmi_executable_hint,
                modbus_map_path,
                is_opc,request_num,cabinet_sn,archived,
                upload_date,tags,author_id,status)
            VALUES(@subtype_id,@controller_id,@eq_prefix,@sub_prefix,@hw_version,@sw_version,
                @dt_str,@version_raw,@filename,@disk_path,@local_path,@description,@changelog,
                @launch_types,@io_map_path,@instructions_path,@hmi_path,@executable_hint,@hmi_executable_hint,
                @modbus_map_path,
                @is_opc,@request_num,@cabinet_sn,0,
                @upload_date,@tags,@author_id,@status)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@subtype_id", v.SubtypeId);
            cmd.Parameters.AddWithValue("@controller_id", v.ControllerId);
            cmd.Parameters.AddWithValue("@eq_prefix", v.EqPrefix);
            cmd.Parameters.AddWithValue("@sub_prefix", v.SubPrefix);
            cmd.Parameters.AddWithValue("@hw_version", v.HwVersion);
            cmd.Parameters.AddWithValue("@sw_version", v.SwVersion);
            cmd.Parameters.AddWithValue("@dt_str", v.DtStr);
            cmd.Parameters.AddWithValue("@version_raw", v.VersionRaw);
            cmd.Parameters.AddWithValue("@filename", v.Filename);
            cmd.Parameters.AddWithValue("@disk_path", v.DiskPath);
            cmd.Parameters.AddWithValue("@local_path", v.LocalPath);
            cmd.Parameters.AddWithValue("@description", v.Description);
            cmd.Parameters.AddWithValue("@changelog", v.Changelog);
            cmd.Parameters.AddWithValue("@launch_types", JsonSerializer.Serialize(v.LaunchTypes));
            cmd.Parameters.AddWithValue("@io_map_path", v.IoMapPath);
            cmd.Parameters.AddWithValue("@instructions_path", v.InstructionsPath);
            cmd.Parameters.AddWithValue("@hmi_path", v.HmiPath);
            cmd.Parameters.AddWithValue("@executable_hint", v.ExecutableHint);
            cmd.Parameters.AddWithValue("@hmi_executable_hint", v.HmiExecutableHint);
            cmd.Parameters.AddWithValue("@modbus_map_path", v.ModbusMapPath);
            cmd.Parameters.AddWithValue("@is_opc", v.IsOpc ? 1 : 0);
            cmd.Parameters.AddWithValue("@request_num", v.RequestNum);
            cmd.Parameters.AddWithValue("@cabinet_sn", v.CabinetSn);
            cmd.Parameters.AddWithValue("@upload_date", NowIso());
            cmd.Parameters.AddWithValue("@tags", v.Tags);
            cmd.Parameters.AddWithValue("@author_id", (object?)v.AuthorId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@status", v.Status);
        });
        var id = ExecuteScalar("SELECT last_insert_rowid()");
        return id is long l ? (int)l : -1;
    }

    /// <summary>Update editable fields (description, tags, launch_types, исполняемые файлы ПЛК/HMI)
    /// of a fw_version. Любой параметр null — «не трогать это поле».</summary>
    public void UpdateFwVersion(int versionId, string? description = null, string? tags = null, List<string>? launchTypes = null,
        string? hmiExecutableHint = null, string? executableHint = null)
    {
        var sets = new List<string>();
        var values = new List<(string, object)>();
        if (description is not null) { sets.Add("description=@description"); values.Add(("@description", description)); }
        if (tags is not null) { sets.Add("tags=@tags"); values.Add(("@tags", tags)); }
        if (launchTypes is not null) { sets.Add("launch_types=@launch_types"); values.Add(("@launch_types", JsonSerializer.Serialize(launchTypes))); }
        if (hmiExecutableHint is not null) { sets.Add("hmi_executable_hint=@hmi_executable_hint"); values.Add(("@hmi_executable_hint", hmiExecutableHint)); }
        if (executableHint is not null) { sets.Add("executable_hint=@executable_hint"); values.Add(("@executable_hint", executableHint)); }
        if (sets.Count == 0) return;

        ExecuteNonQuery($"UPDATE fw_versions SET {string.Join(", ", sets)} WHERE id=@id", cmd =>
        {
            foreach (var (name, value) in values)
                cmd.Parameters.AddWithValue(name, value);
            cmd.Parameters.AddWithValue("@id", versionId);
        });
    }

    /// <summary>Пути доп. файлов (Карта ВВ / Инструкция / Карта modbus / HMI) — отдельным методом от
    /// UpdateFwVersion, т.к. меняются другим сценарием: «доложить файлы к уже загруженной прошивке»
    /// (см. FirmwareAttachmentsService), а не правкой описания/тегов. null — «не трогать поле»,
    /// пустая строка — «убрать ссылку» (файлы на диске остаются).</summary>
    public void UpdateFwVersionAttachments(int versionId, string? ioMapPath = null, string? instructionsPath = null,
        string? modbusMapPath = null, string? hmiPath = null)
    {
        var sets = new List<string>();
        var values = new List<(string, object)>();
        if (ioMapPath is not null) { sets.Add("io_map_path=@io"); values.Add(("@io", ioMapPath)); }
        if (instructionsPath is not null) { sets.Add("instructions_path=@instr"); values.Add(("@instr", instructionsPath)); }
        if (modbusMapPath is not null) { sets.Add("modbus_map_path=@modbus"); values.Add(("@modbus", modbusMapPath)); }
        if (hmiPath is not null) { sets.Add("hmi_path=@hmi"); values.Add(("@hmi", hmiPath)); }
        if (sets.Count == 0) return;

        ExecuteNonQuery($"UPDATE fw_versions SET {string.Join(", ", sets)} WHERE id=@id", cmd =>
        {
            foreach (var (name, value) in values)
                cmd.Parameters.AddWithValue(name, value);
            cmd.Parameters.AddWithValue("@id", versionId);
        });
    }

    /// <summary>Названия группы/подтипа/контроллера для версии — нужны, чтобы построить пути общих
    /// папок контроллера (Карта ВВ, Инструкция, HMI…) там, где на руках только сама запись версии
    /// (например EditFirmwareDialog, открытый из поиска, где join'а с именами не было).</summary>
    public (string GroupName, string SubtypeName, string ControllerName)? GetFwVersionNames(int versionId)
    {
        using var reader = ExecuteReader("""
            SELECT eg.name AS group_name, es.name AS subtype_name, cm.name AS ctrl_name
            FROM fw_versions fv
            JOIN equipment_subtypes es ON fv.subtype_id   = es.id
            JOIN equipment_groups   eg ON es.group_id     = eg.id
            JOIN controller_models  cm ON fv.controller_id = cm.id
            WHERE fv.id=@id
            """, cmd => cmd.Parameters.AddWithValue("@id", versionId));
        return reader.Read()
            ? (GetString(reader, "group_name"), GetString(reader, "subtype_name"), GetString(reader, "ctrl_name"))
            : null;
    }

    /// <summary>Hard-removes a firmware version row outright — no tombstone, no sync propagation.
    /// Kept for completeness/tests; Настройки → Прошивки → «Удалить прошивку» uses
    /// <see cref="TombstoneFwVersion"/> instead (see there for why a bare DELETE isn't enough for
    /// that button anymore).</summary>
    public void DeleteFwVersion(int id)
    {
        // Статистику выбора уносим вместе с записью: SQLite переиспользует rowid, и счётчик
        // «эту ставили 10 раз» иначе достался бы следующей загруженной версии (см. Database.FwUsage.cs).
        ForgetFwUsage(id);
        ExecuteNonQuery("DELETE FROM fw_versions WHERE id=@id", cmd => cmd.Parameters.AddWithValue("@id", id));
    }

    /// <summary>Administrator/programmer removing a firmware version from Настройки → Прошивки (Round
    /// 43 originally used a bare DELETE here — see DeleteFwVersion above — which meant the deletion
    /// itself never left this machine: any other machine that hadn't synced since would happily
    /// re-insert the "missing" row on its NEXT export, resurrecting it right back (reported live,
    /// Задача 3). This instead marks the row with a deletion tombstone (deleted_at) and leaves it in
    /// place: every read query in this file/Database.Search.cs filters deleted_at out, so it
    /// disappears from every listing/search on THIS machine immediately, exactly like a real delete —
    /// but the row itself keeps flowing through ExportHierarchyData/ImportHierarchyDataCore as a
    /// tombstone, so every other machine that syncs afterwards mirrors the deletion (including a
    /// best-effort removal of its own copy of the on-disk folder) instead of resurrecting the row.
    /// Same "caller removes the local files, this only touches the database" split as before — see
    /// SettingsView.DeleteFirmware_Click for the local disk cleanup, and the fw_versions block in
    /// ImportHierarchyDataCore for the mirrored one.</summary>
    public void TombstoneFwVersion(int id) =>
        ExecuteNonQuery("UPDATE fw_versions SET deleted_at=@d WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@d", NowIso());
            cmd.Parameters.AddWithValue("@id", id);
        });

    public int DuplicateFwVersion(int versionId)
    {
        var row = GetFwVersionById(versionId);
        if (row is null) return -1;

        ExecuteNonQuery("""
            INSERT INTO fw_versions
               (subtype_id,controller_id,eq_prefix,sub_prefix,hw_version,sw_version,
                dt_str,version_raw,filename,disk_path,local_path,description,changelog,
                launch_types,io_map_path,instructions_path,hmi_path,executable_hint,hmi_executable_hint,
                modbus_map_path,
                is_opc,request_num,cabinet_sn,archived,
                upload_date,tags)
            VALUES(@subtype_id,@controller_id,@eq_prefix,@sub_prefix,@hw_version,@sw_version,
                @dt_str,@version_raw,@filename,@disk_path,@local_path,@description,@changelog,
                @launch_types,@io_map_path,@instructions_path,@hmi_path,@executable_hint,@hmi_executable_hint,
                @modbus_map_path,
                @is_opc,@request_num,@cabinet_sn,0,
                @upload_date,@tags)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@subtype_id", row.SubtypeId);
            cmd.Parameters.AddWithValue("@controller_id", row.ControllerId);
            cmd.Parameters.AddWithValue("@eq_prefix", row.EqPrefix);
            cmd.Parameters.AddWithValue("@sub_prefix", row.SubPrefix);
            cmd.Parameters.AddWithValue("@hw_version", row.HwVersion);
            cmd.Parameters.AddWithValue("@sw_version", row.SwVersion);
            cmd.Parameters.AddWithValue("@dt_str", row.DtStr);
            cmd.Parameters.AddWithValue("@version_raw", row.VersionRaw);
            cmd.Parameters.AddWithValue("@filename", row.Filename);
            cmd.Parameters.AddWithValue("@disk_path", row.DiskPath);
            cmd.Parameters.AddWithValue("@local_path", row.LocalPath);
            cmd.Parameters.AddWithValue("@description", row.Description);
            cmd.Parameters.AddWithValue("@changelog", row.Changelog);
            cmd.Parameters.AddWithValue("@launch_types", JsonSerializer.Serialize(row.LaunchTypes));
            cmd.Parameters.AddWithValue("@io_map_path", row.IoMapPath);
            cmd.Parameters.AddWithValue("@instructions_path", row.InstructionsPath);
            cmd.Parameters.AddWithValue("@hmi_path", row.HmiPath);
            cmd.Parameters.AddWithValue("@executable_hint", row.ExecutableHint);
            cmd.Parameters.AddWithValue("@hmi_executable_hint", row.HmiExecutableHint);
            cmd.Parameters.AddWithValue("@modbus_map_path", row.ModbusMapPath);
            cmd.Parameters.AddWithValue("@is_opc", row.IsOpc ? 1 : 0);
            cmd.Parameters.AddWithValue("@request_num", row.RequestNum);
            cmd.Parameters.AddWithValue("@cabinet_sn", row.CabinetSn);
            cmd.Parameters.AddWithValue("@upload_date", NowIso());
            cmd.Parameters.AddWithValue("@tags", row.Tags);
        });
        var id = ExecuteScalar("SELECT last_insert_rowid()");
        return id is long l ? (int)l : -1;
    }

    public FwVersionRecord? GetFwVersionById(int id)
    {
        using var reader = ExecuteReader("SELECT * FROM fw_versions WHERE id=@id", cmd => cmd.Parameters.AddWithValue("@id", id));
        return reader.Read() ? ReadFwVersion(reader) : null;
    }

    /// <summary>fw_versions rows with a deletion tombstone (see TombstoneFwVersion) are excluded from
    /// every read below, unconditionally — deleted means gone from this machine's view, the same as a
    /// hard delete used to look, regardless of any includeArchived-style toggle. Takes an optional
    /// table alias ("fv" for the queries that JOIN and alias fw_versions, unqualified for the ones
    /// that query it bare) since "alias.(...)" isn't valid SQL — a bare "{NotDeleted()}" interpolation
    /// with the alias baked into the condition text does not work for both cases at once.</summary>
    private static string NotDeleted(string alias = "") =>
        $"({(alias.Length > 0 ? alias + "." : "")}deleted_at IS NULL OR {(alias.Length > 0 ? alias + "." : "")}deleted_at = '')";

    public List<FwVersionRecord> GetAllFwVersionsWithNames(bool includeArchived = false)
    {
        var sql = $"""
            SELECT fv.*, eg.name AS group_name, es.name AS subtype_name, cm.name AS ctrl_name
            FROM fw_versions fv
            JOIN equipment_subtypes es ON fv.subtype_id   = es.id
            JOIN equipment_groups   eg ON es.group_id     = eg.id
            JOIN controller_models  cm ON fv.controller_id = cm.id
            WHERE {NotDeleted("fv")}
            """;
        if (!includeArchived) sql += " AND fv.archived = 0";
        sql += " ORDER BY eg.name, es.name, cm.name, fv.hw_version DESC, fv.sw_version DESC, fv.dt_str DESC";

        var result = new List<FwVersionRecord>();
        using var reader = ExecuteReader(sql);
        while (reader.Read())
        {
            var rec = ReadFwVersion(reader);
            rec.GroupName = GetString(reader, "group_name");
            rec.SubtypeName = GetString(reader, "subtype_name");
            rec.CtrlName = GetString(reader, "ctrl_name");
            result.Add(rec);
        }
        return result;
    }

    /// <summary>Non-archived, non-rolled-back versions still awaiting moderation (released = 0) —
    /// feeds both the Settings→Прошивки→Модерация tab and the sidebar "Модерация тегов" page.
    /// A version leaves this list only when a user explicitly confirms "release from moderation"
    /// (see MarkFwVersionReleased) — adding tags alone no longer moves it out on its own.</summary>
    public List<FwVersionRecord> GetUnreleasedFwVersionsWithNames()
    {
        var sql = $"""
            SELECT fv.*, eg.name AS group_name, es.name AS subtype_name, cm.name AS ctrl_name
            FROM fw_versions fv
            JOIN equipment_subtypes es ON fv.subtype_id   = es.id
            JOIN equipment_groups   eg ON es.group_id     = eg.id
            JOIN controller_models  cm ON fv.controller_id = cm.id
            WHERE fv.archived = 0 AND (fv.status IS NULL OR fv.status = 'active') AND fv.released = 0 AND {NotDeleted("fv")}
            ORDER BY fv.upload_date DESC
            """;

        var result = new List<FwVersionRecord>();
        using var reader = ExecuteReader(sql);
        while (reader.Read())
        {
            var rec = ReadFwVersion(reader);
            rec.GroupName = GetString(reader, "group_name");
            rec.SubtypeName = GetString(reader, "subtype_name");
            rec.CtrlName = GetString(reader, "ctrl_name");
            result.Add(rec);
        }
        return result;
    }

    /// <summary>Все записи ОДНОЙ И ТОЙ ЖЕ прошивки — своя запись на каждый подтип шкафа, которому она
    /// подходит (см. FirmwareUploadService.LinkToExtraSubtypes: файлы на диске одни, disk_path у всех
    /// записей общий, в папке «чужого» подтипа лежит только ярлык). Именно по паре disk_path +
    /// version_raw они и опознаются: номер версии физически вписан внутрь файла прошивки, поэтому у
    /// копий он общий, а разные версии в одной папке лежать не могут.
    ///
    /// Пустой disk_path (запись без файлов на диске) не связывает ничего — иначе «связанными» стали бы
    /// все такие записи разом.</summary>
    public List<FwVersionRecord> GetFwVersionsSharingFiles(string diskPath, string versionRaw)
    {
        if (string.IsNullOrWhiteSpace(diskPath)) return new();
        var result = new List<FwVersionRecord>();
        using var reader = ExecuteReader($"""
            SELECT * FROM fw_versions
            WHERE disk_path=@d AND version_raw=@v AND archived=0 AND {NotDeleted()}
            ORDER BY id
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@d", diskPath);
            cmd.Parameters.AddWithValue("@v", versionRaw);
        });
        while (reader.Read())
            result.Add(ReadFwVersion(reader));
        return result;
    }

    /// <summary>Ссылается ли на ЭТИ ЖЕ файлы на диске кто-то ещё, кроме указанной записи. Ровно один
    /// вопрос, но от него зависит сохранность прошивки: у прошивки, привязанной к нескольким подтипам
    /// шкафов, записей несколько, а папка на диске ОДНА и общая (см. FirmwareSubtypeLinkService).
    /// Удаление одной такой записи — это удаление ссылки, а не прошивки, и трогать файлы нельзя:
    /// иначе «убрал лишний подтип» уносило бы саму прошивку у всех — и на этой машине
    /// (SettingsView.DeleteFirmware_Click), и на всех остальных, куда tombstone доедет
    /// синхронизацией (ImportHierarchyDataCore, там же удаляются файлы). Файлы удаляются только
    /// вместе с последней записью, которая на них ссылается.
    ///
    /// Архивные записи тоже считаются: они всё ещё указывают на эти файлы, и «архивная» — не повод
    /// вынести папку из-под неё.</summary>
    public bool IsDiskPathSharedByOtherVersions(string diskPath, int exceptId)
    {
        if (string.IsNullOrWhiteSpace(diskPath)) return false;
        var count = ExecuteScalar($"""
            SELECT COUNT(*) FROM fw_versions
            WHERE disk_path=@d AND id<>@id AND {NotDeleted()}
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@d", diskPath);
            cmd.Parameters.AddWithValue("@id", exceptId);
        });
        return count is long l && l > 0;
    }

    public int GetUnreleasedFwVersionsCount()
    {
        var result = ExecuteScalar($"""
            SELECT COUNT(*) FROM fw_versions
            WHERE archived = 0 AND (status IS NULL OR status = 'active') AND released = 0 AND {NotDeleted()}
            """);
        return result is long l ? (int)l : 0;
    }

    /// <summary>Marks a version as released from moderation — set only after the user explicitly
    /// confirms the "вывести из модерации и сделать релизной?" prompt.</summary>
    public void MarkFwVersionReleased(int versionId) =>
        ExecuteNonQuery("UPDATE fw_versions SET released = 1 WHERE id = @id", cmd => cmd.Parameters.AddWithValue("@id", versionId));

    public List<FwVersionRecord> GetFwVersions(int? subtypeId = null, int? controllerId = null,
        bool includeArchived = false, bool includeRolledBack = false)
    {
        var sql = $"SELECT * FROM fw_versions WHERE {NotDeleted()}";
        var binds = new List<(string, object)>();
        if (subtypeId is not null) { sql += " AND subtype_id=@s"; binds.Add(("@s", subtypeId.Value)); }
        if (controllerId is not null) { sql += " AND controller_id=@c"; binds.Add(("@c", controllerId.Value)); }
        if (!includeArchived) sql += " AND archived=0";
        if (!includeRolledBack) sql += " AND (status IS NULL OR status='active')";
        // dt_str is empty when a version was created with "Добавлять дату/время" unchecked — id DESC
        // as the final tiebreak keeps recency ordering correct even when dt_str ties (e.g. all empty).
        sql += " ORDER BY dt_str DESC, hw_version DESC, sw_version DESC, id DESC";

        var result = new List<FwVersionRecord>();
        using var reader = ExecuteReader(sql, cmd =>
        {
            foreach (var (name, value) in binds)
                cmd.Parameters.AddWithValue(name, value);
        });
        while (reader.Read())
            result.Add(ReadFwVersion(reader));
        return result;
    }

    /// <summary>Next free sw_version: MAX+1 across BOTH already-uploaded (active) fw_versions AND
    /// currently-open reservations (see Database.FwVersionReservations.cs) for this exact
    /// (subtype, controller, hw_version) combo. Including reservations here is what makes the live
    /// preview (before any reservation exists) never suggest a number someone else already locked in.</summary>
    public int GetNextSwVersion(int subtypeId, int controllerId, int hwVersion)
    {
        var result = ExecuteScalar($"""
            SELECT MAX(sw_version) FROM fw_versions
            WHERE subtype_id=@s AND controller_id=@c AND hw_version=@h
            AND (status IS NULL OR status='active') AND {NotDeleted()}
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@s", subtypeId);
            cmd.Parameters.AddWithValue("@c", controllerId);
            cmd.Parameters.AddWithValue("@h", hwVersion);
        });
        int activeMax = result is long l ? (int)l : 0;
        int reservedMax = GetReservedMaxSwVersion(subtypeId, controllerId, hwVersion);
        return System.Math.Max(activeMax, reservedMax) + 1;
    }

    public User GetOrCreateUser(string windowsLogin, string name)
    {
        using (var reader = ExecuteReader("SELECT * FROM users WHERE windows_login=@w",
                   cmd => cmd.Parameters.AddWithValue("@w", windowsLogin)))
        {
            if (reader.Read())
            {
                return new User
                {
                    Id = GetInt(reader, "id"),
                    Name = GetString(reader, "name"),
                    WindowsLogin = GetString(reader, "windows_login"),
                    CreatedAt = GetString(reader, "created_at"),
                };
            }
        }

        ExecuteNonQuery("INSERT INTO users (name, windows_login, created_at) VALUES (@n,@w,@c)", cmd =>
        {
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@w", windowsLogin);
            cmd.Parameters.AddWithValue("@c", NowIso());
        });
        var id = ExecuteScalar("SELECT last_insert_rowid()");
        return new User { Id = id is long l2 ? (int)l2 : -1, Name = name, WindowsLogin = windowsLogin };
    }

    /// <summary>Marks a version rolled back and renames its on-disk firmware folder / HMI project
    /// (if any) with a "_ОТКАТАНО" marker — see FileSystemHelpers.MarkRolledBackOnDisk. Without this,
    /// a later upload reusing the sw_version this rollback frees up would land on the exact same
    /// version_raw-named path and silently merge into (or overwrite) the rolled-back version's files.
    /// The rename is best-effort: a locked file or unmounted share must not block the DB rollback.</summary>
    public bool RollbackFwVersion(int fwVersionId)
    {
        var v = GetFwVersionById(fwVersionId);
        if (v is null) return false;

        ExecuteNonQuery("UPDATE fw_versions SET status='rolled_back' WHERE id=@id", cmd => cmd.Parameters.AddWithValue("@id", fwVersionId));

        string newDiskPath = v.DiskPath, newHmiPath = v.HmiPath;
        try { newDiskPath = Infrastructure.FileSystemHelpers.MarkRolledBackOnDisk(v.DiskPath); } catch { /* best effort */ }
        try { newHmiPath = Infrastructure.FileSystemHelpers.MarkRolledBackOnDisk(v.HmiPath); } catch { /* best effort */ }

        if (newDiskPath != v.DiskPath || newHmiPath != v.HmiPath)
        {
            ExecuteNonQuery("UPDATE fw_versions SET disk_path=@d, hmi_path=@h WHERE id=@id", cmd =>
            {
                cmd.Parameters.AddWithValue("@d", newDiskPath);
                cmd.Parameters.AddWithValue("@h", newHmiPath);
                cmd.Parameters.AddWithValue("@id", fwVersionId);
            });
        }
        return true;
    }

    public FwVersionRecord? GetLastActiveFwVersion(int subtypeId, int controllerId, int hwVersion)
    {
        using var reader = ExecuteReader($"""
            SELECT * FROM fw_versions
            WHERE subtype_id=@s AND controller_id=@c AND hw_version=@h
            AND (status IS NULL OR status='active') AND archived=0 AND {NotDeleted()}
            ORDER BY sw_version DESC, dt_str DESC LIMIT 1
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@s", subtypeId);
            cmd.Parameters.AddWithValue("@c", controllerId);
            cmd.Parameters.AddWithValue("@h", hwVersion);
        });
        return reader.Read() ? ReadFwVersion(reader) : null;
    }

    /// <summary>Последний известный HMI-проект этого шкафа: путь, подсказка исполняемого файла и
    /// номер версии, к которой он был приложен.
    ///
    /// ПЛК и панель обновляются независимо — правку в программе ПЛК выкладывают, панель при этом не
    /// трогают и в загрузке не указывают. До этого такая версия оставалась вообще без HMI: кнопка
    /// «Открыть HMI проект» на карточке пропадала, хотя панель у шкафа никуда не делась и лежит
    /// рядом с предыдущей версией (жалоба «загрузил ПЛК без HMI — старая HMI не подтянулась»).
    /// Ищется по паре подтип/контроллер без привязки к hw_version: панель принадлежит шкафу, а не
    /// конкретному номеру версии программы. Откатанные и удалённые версии не в счёт — их файлы на
    /// диске переименованы (см. RollbackFwVersion) либо удалены.</summary>
    public (string HmiPath, string HmiExecutableHint, string VersionRaw)? GetLatestHmiForFirmware(int subtypeId, int controllerId)
    {
        using var reader = ExecuteReader($"""
            SELECT hmi_path, hmi_executable_hint, version_raw FROM fw_versions
            WHERE subtype_id=@s AND controller_id=@c
              AND hmi_path IS NOT NULL AND hmi_path != ''
              AND (status IS NULL OR status='active') AND archived=0 AND {NotDeleted()}
            ORDER BY hw_version DESC, sw_version DESC, dt_str DESC, id DESC LIMIT 1
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@s", subtypeId);
            cmd.Parameters.AddWithValue("@c", controllerId);
        });
        return reader.Read()
            ? (GetString(reader, "hmi_path"), GetString(reader, "hmi_executable_hint"), GetString(reader, "version_raw"))
            : null;
    }

    public List<FwVersionRecord> GetFwVersionsHistory(int subtypeId, int controllerId, bool includeArchived = false)
    {
        var sql = $"""
            SELECT fv.*, cm.name AS ctrl_name
            FROM fw_versions fv
            JOIN controller_models cm ON fv.controller_id = cm.id
            WHERE fv.subtype_id=@s AND fv.controller_id=@c AND {NotDeleted("fv")}
            """;
        if (!includeArchived) sql += " AND fv.archived=0";
        sql += " ORDER BY fv.dt_str DESC, fv.hw_version DESC, fv.sw_version DESC, fv.id DESC";

        var result = new List<FwVersionRecord>();
        using var reader = ExecuteReader(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("@s", subtypeId);
            cmd.Parameters.AddWithValue("@c", controllerId);
        });
        while (reader.Read())
        {
            var rec = ReadFwVersion(reader);
            rec.CtrlName = GetString(reader, "ctrl_name");
            result.Add(rec);
        }
        return result;
    }

    /// <summary>The newest active fw_version per (subtype_id, controller_id) — one row per firmware,
    /// same grouping key as SearchFwVersionsByTokens but without the token/score filter. Feeds the
    /// background firmware-update scan, which needs "what's the latest on the server" for every
    /// firmware the naladchik has ever downloaded, not just ones matching a search query.</summary>
    public List<FwVersionRecord> GetLatestActiveFwVersions()
    {
        var rows = new List<FwVersionRecord>();
        using (var reader = ExecuteReader($"""
            SELECT fv.*,
                   eg.name AS group_name,
                   es.name AS subtype_name,
                   es.folder_name AS subtype_folder,
                   cm.name AS ctrl_name
            FROM fw_versions fv
            JOIN equipment_subtypes es ON fv.subtype_id  = es.id
            JOIN equipment_groups   eg ON es.group_id    = eg.id
            JOIN controller_models  cm ON fv.controller_id = cm.id
            WHERE fv.archived = 0 AND (fv.status IS NULL OR fv.status = 'active') AND {NotDeleted("fv")}
            ORDER BY fv.id DESC
            """))
        {
            while (reader.Read())
            {
                var rec = ReadFwVersion(reader);
                rec.GroupName = GetString(reader, "group_name");
                rec.SubtypeName = GetString(reader, "subtype_name");
                rec.SubtypeFolder = GetString(reader, "subtype_folder");
                rec.CtrlName = GetString(reader, "ctrl_name");
                rows.Add(rec);
            }
        }

        var seen = new HashSet<(int, int)>();
        var result = new List<FwVersionRecord>();
        foreach (var row in rows)
        {
            if (seen.Add((row.SubtypeId, row.ControllerId)))
                result.Add(row);
        }
        return result;
    }

    public void ArchiveFwVersion(int versionId) =>
        ExecuteNonQuery("UPDATE fw_versions SET archived=1 WHERE id=@id", cmd => cmd.Parameters.AddWithValue("@id", versionId));

    private static FwVersionRecord ReadFwVersion(SqliteDataReader r)
    {
        var launchTypesJson = GetString(r, "launch_types", "[]");
        List<string> launchTypes;
        // Corrupted/pre-migration value in this column falls back to "no launch types recorded" — a
        // display-only field (which icons show next to a version), not something the row's identity
        // or moderation status depends on.
        try { launchTypes = JsonSerializer.Deserialize<List<string>>(launchTypesJson) ?? new(); }
        catch { launchTypes = new(); }

        return new FwVersionRecord
        {
            Id = GetInt(r, "id"),
            SubtypeId = GetInt(r, "subtype_id"),
            ControllerId = GetInt(r, "controller_id"),
            EqPrefix = GetInt(r, "eq_prefix"),
            SubPrefix = GetInt(r, "sub_prefix"),
            HwVersion = GetInt(r, "hw_version"),
            SwVersion = GetInt(r, "sw_version"),
            DtStr = GetString(r, "dt_str"),
            VersionRaw = GetString(r, "version_raw"),
            Filename = GetString(r, "filename"),
            DiskPath = GetString(r, "disk_path"),
            LocalPath = GetString(r, "local_path"),
            Description = GetString(r, "description"),
            Changelog = GetString(r, "changelog"),
            LaunchTypes = launchTypes,
            IoMapPath = GetString(r, "io_map_path"),
            InstructionsPath = GetString(r, "instructions_path"),
            HmiPath = GetString(r, "hmi_path"),
            ExecutableHint = GetString(r, "executable_hint"),
            HmiExecutableHint = GetString(r, "hmi_executable_hint"),
            ModbusMapPath = GetString(r, "modbus_map_path"),
            IsOpc = GetBool(r, "is_opc"),
            RequestNum = GetString(r, "request_num"),
            CabinetSn = GetString(r, "cabinet_sn"),
            Archived = GetBool(r, "archived"),
            UploadDate = GetString(r, "upload_date"),
            Tags = GetString(r, "tags"),
            AuthorId = GetIntOrNull(r, "author_id"),
            Status = GetString(r, "status", "active"),
            Released = GetBool(r, "released"),
        };
    }
}
