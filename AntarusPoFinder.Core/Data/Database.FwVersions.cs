using System.Collections.Generic;
using System.Linq;
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
                upload_date,tags,author_id,status,sync_id,config_name,copy_of)
            VALUES(@subtype_id,@controller_id,@eq_prefix,@sub_prefix,@hw_version,@sw_version,
                @dt_str,@version_raw,@filename,@disk_path,@local_path,@description,@changelog,
                @launch_types,@io_map_path,@instructions_path,@hmi_path,@executable_hint,@hmi_executable_hint,
                @modbus_map_path,
                @is_opc,@request_num,@cabinet_sn,0,
                @upload_date,@tags,@author_id,@status,@sync_id,@config_name,@copy_of)
            """, cmd =>
        {
            // Пусто у обычной загрузки; непустым его заводит только FirmwareConfigService (вариант
            // шкафа) — см. столбец config_name в Database.cs.
            cmd.Parameters.AddWithValue("@config_name", v.ConfigName ?? "");
            cmd.Parameters.AddWithValue("@copy_of", v.CopyOf ?? "");
            // sync_id проставляется сразу при заведении строки, а не откладывается до ближайшего
            // BackfillSyncIds на старте приложения: между загрузкой прошивки и следующим запуском
            // помещается и синхронизация, и вывод из модерации, и удаление — всё то, чему этот
            // идентификатор и нужен. Своё значение снаружи (v.SyncId) задаёт только импорт конфига,
            // чтобы строка у всех машин осталась одной и той же.
            cmd.Parameters.AddWithValue("@sync_id", string.IsNullOrEmpty(v.SyncId) ? Guid.NewGuid().ToString() : v.SyncId);
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
        // Снятие тега обязано пережить синхронизацию: без явной отметки об удалении тег вернулся бы с
        // первой машины, которая о снятии ещё не знает (см. Database.FlatLists.RecordRowTagChange —
        // жалоба «удаляю теги, а они снова появляются»). Отметку ставим ДО записи, пока в базе ещё
        // лежит прежний набор.
        if (tags is not null) RecordFwTagChange(versionId, tags);

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

    /// <summary>Отметить снятые/добавленные теги одной прошивки. Читает прежний набор и sync_id прямо
    /// перед записью нового — вызывать после UPDATE поздно, прежнего набора уже не будет.</summary>
    private void RecordFwTagChange(int versionId, string newTags)
    {
        string? syncId = null;
        var oldTags = "";
        using (var reader = ExecuteReader("SELECT sync_id, tags FROM fw_versions WHERE id=@id",
                   cmd => cmd.Parameters.AddWithValue("@id", versionId)))
        {
            if (reader.Read())
            {
                syncId = reader.IsDBNull(0) ? null : reader.GetString(0);
                oldTags = reader.IsDBNull(1) ? "" : reader.GetString(1);
            }
        }

        RecordRowTagChange(syncId, oldTags, newTags);
    }

    /// <summary>Файл прошивки переименовали прямо на диске (разовая операция «Перестроить структуру
    /// диска», см. DiskLayoutMigrator) — поправить все записи, которые на него ссылались. Записей
    /// может быть несколько: конфигурации одного шкафа делят одну папку версии.
    ///
    /// <c>executable_hint</c> правится ТОЛЬКО там, где он указывал ровно на старое имя: подсказка
    /// может указывать на другой файл в той же папке, и переписывать её вслепую значило бы менять
    /// выбор оператора «чем открывать».</summary>
    /// <returns>Сколько строк затронуто — для журнала миграции.</returns>
    public int RenameFirmwareFileRecords(string diskPath, string oldName, string newName)
    {
        var affected = ExecuteNonQuery(
            "UPDATE fw_versions SET filename=@new WHERE disk_path=@dir AND filename=@old",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@new", newName);
                cmd.Parameters.AddWithValue("@dir", diskPath);
                cmd.Parameters.AddWithValue("@old", oldName);
            });
        affected += ExecuteNonQuery(
            "UPDATE fw_versions SET executable_hint=@new WHERE disk_path=@dir AND executable_hint=@old",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@new", newName);
                cmd.Parameters.AddWithValue("@dir", diskPath);
                cmd.Parameters.AddWithValue("@old", oldName);
            });
        return affected;
    }

    /// <summary>Перецелить запись на другую папку версии на диске, ничего больше не трогая. Нужно
    /// ровно двум операциям, и обе — про перестройку диска: перенос ОПЦ внутрь контроллера
    /// (docs/hierarchy-rework-plan.md, этап 5 — единственный переезд, меняющий disk_path) и его
    /// локальная починка на машинах, которые перестройку не запускали
    /// (HierarchyService.RepairOpcDiskPaths). Отдельно от UpdateFwVersion, потому что там правится
    /// карточка версии целиком, а здесь — один столбец, и правит его не человек, а операция над
    /// диском. Пустой путь игнорируется: «потерять» disk_path такой правкой нельзя.</summary>
    public void RepointFwVersionDiskPath(int versionId, string newDiskPath)
    {
        if (string.IsNullOrWhiteSpace(newDiskPath)) return;
        ExecuteNonQuery("UPDATE fw_versions SET disk_path=@d WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@d", newDiskPath);
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

    /// <summary>Свободное имя конфигурации для этой прошивки — «Конфигурация 2», «Конфигурация 3»…
    /// Нумерация с двойки осознанно: сама прошивка (строка с пустым config_name) и есть «конфигурация
    /// 1», поэтому первый заведённый вариант — второй по счёту.</summary>
    private string NextConfigName(string diskPath, string versionRaw)
    {
        var taken = new HashSet<string>(
            GetFwVersionConfigs(diskPath, versionRaw).Select(c => c.ConfigName), StringComparer.OrdinalIgnoreCase);
        for (var n = 2; ; n++)
        {
            var candidate = $"Конфигурация {n}";
            if (taken.Add(candidate)) return candidate;
        }
    }

    /// <summary>«Дублировать» из Настройки → Прошивки. Копия — это ВАРИАНТ той же самой прошивки:
    /// файлы на диске не копируются (disk_path и version_raw общие), отличаться копия будет только
    /// тегами, которые оператор ей потом проставит. Ровно то, ради чего дублирование и заводили:
    /// «одна прошивка с разными настройками — типа 1 или 2 задвижки, или вообще нет, а прошивка та же».
    ///
    /// Поэтому копия получает НЕПУСТОЕ имя конфигурации (см. столбец config_name в Database.cs и
    /// FirmwareConfigService). Без него две записи с одинаковым натуральным ключом (подтип+контроллер+
    /// version_raw) были бы для синхронизации ОДНОЙ И ТОЙ ЖЕ строкой: у коллеги они схлопывались в одну
    /// с объединёнными тегами, и все заготовленные варианты пропадали. Заодно копия перестаёт засорять
    /// историю версий и очередь модерации десятком одинаковых строк (см. NotConfig).
    ///
    /// Копия конфигурации — тоже конфигурация, со своим свободным именем.</summary>
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
                upload_date,tags,sync_id,config_name,released)
            VALUES(@subtype_id,@controller_id,@eq_prefix,@sub_prefix,@hw_version,@sw_version,
                @dt_str,@version_raw,@filename,@disk_path,@local_path,@description,@changelog,
                @launch_types,@io_map_path,@instructions_path,@hmi_path,@executable_hint,@hmi_executable_hint,
                @modbus_map_path,
                @is_opc,@request_num,@cabinet_sn,0,
                @upload_date,@tags,@sync_id,@config_name,@released)
            """, cmd =>
        {
            // Копия — самостоятельная строка, поэтому свой собственный sync_id, а не унаследованный
            // от оригинала: иначе синхронизация считала бы их одной и той же записью.
            cmd.Parameters.AddWithValue("@sync_id", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("@config_name", NextConfigName(row.DiskPath, row.VersionRaw));
            // Состояние модерации наследуется от исходной записи: вариант уже выпущенной прошивки —
            // та же самая прошивка, проверять в нём нечего, и всплывать в модерации он не должен.
            cmd.Parameters.AddWithValue("@released", row.Released ? 1 : 0);
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

    /// <summary>«Только основные записи прошивок, без строк-конфигураций» (см. столбец config_name в
    /// Database.cs). Конфигурация — это НЕ отдельная прошивка и не отдельная версия: та же папка на
    /// диске, тот же номер, отличается только набор тегов. Поэтому там, где перечисляются ПРОШИВКИ или
    /// ВЕРСИИ — очередь модерации и история версий, — конфигурации показывать нельзя: десять вариантов
    /// одного шкафа превратили бы историю в десять одинаковых строк, а модерацию — в десять записей,
    /// которые всё равно выпускаются одним действием (MarkFwVersionReleasedWithLinked снимает модерацию
    /// со всех записей, делящих файлы). Управляются конфигурации там, где их и заводят, — в модерации
    /// самой прошивки (EditFirmwareDialog, FirmwareConfigService).
    ///
    /// Поиску, наоборот, нужны именно они — там каждая конфигурация ищется своими тегами (см.
    /// Database.Search.cs), а выдача схлопывает их в одну строку сама.</summary>
    private static string NotConfig(string alias = "")
    {
        var prefix = alias.Length > 0 ? alias + "." : "";
        return $"({prefix}config_name IS NULL OR {prefix}config_name = '')";
    }

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

    /// <summary>«Эту версию уже сменила более свежая» — под тем же шкафом/контроллером/hw есть живая
    /// версия с бо́льшим номером (порядок ровно тот же, что у GetLastActiveFwVersion). Модерации такие
    /// строки не нужны: размечать теги у версии, которую уже никто не поставит, — работа впустую, и
    /// именно они забивали список (жалоба «замененные и откаченные в модерации смысла отображать
    /// нет»; откатанные отсекает условие по status выше).</summary>
    private static string NotSuperseded(string alias) => $"""
        NOT EXISTS (
            SELECT 1 FROM fw_versions newer
            WHERE newer.subtype_id = {alias}.subtype_id
              AND newer.controller_id = {alias}.controller_id
              AND newer.hw_version = {alias}.hw_version
              AND newer.archived = 0 AND (newer.status IS NULL OR newer.status = 'active')
              AND {NotDeleted("newer")}
              AND (newer.sw_version > {alias}.sw_version
                   OR (newer.sw_version = {alias}.sw_version AND newer.dt_str > {alias}.dt_str))
        )
        """;

    /// <summary>Non-archived, non-rolled-back versions still awaiting moderation (released = 0) —
    /// feeds both the Settings→Прошивки→Модерация tab and the sidebar "Модерация прошивок" page.
    /// A version leaves this list only when a user explicitly confirms "release from moderation"
    /// (see MarkFwVersionReleased) — adding tags alone no longer moves it out on its own.
    /// Заменённые более свежей версией сюда не попадают вовсе — см. NotSuperseded.</summary>
    public List<FwVersionRecord> GetUnreleasedFwVersionsWithNames()
    {
        var sql = $"""
            SELECT fv.*, eg.name AS group_name, es.name AS subtype_name, cm.name AS ctrl_name
            FROM fw_versions fv
            JOIN equipment_subtypes es ON fv.subtype_id   = es.id
            JOIN equipment_groups   eg ON es.group_id     = eg.id
            JOIN controller_models  cm ON fv.controller_id = cm.id
            WHERE fv.archived = 0 AND (fv.status IS NULL OR fv.status = 'active') AND fv.released = 0 AND {NotDeleted("fv")}
              AND {NotConfig("fv")}
              AND {NotSuperseded("fv")}
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

    /// <summary>Та же самая сборка, залитая в ДРУГИЕ подтипы шкафа отдельными версиями (см.
    /// FirmwareSubtypeLinkService.LinkExtras: с уходом от ярлыков копия получает свою папку, свои
    /// файлы и свой номер). Общего disk_path у них больше нет, родство хранится явно — в столбце
    /// copy_of лежит sync_id исходной версии.
    ///
    /// Возвращается вся семья, кроме самой переданной записи: и копии этой версии, и — если передана
    /// копия — её оригинал вместе с остальными копиями. Спрашивать «кто ещё есть» может любая из них.</summary>
    public List<FwVersionRecord> GetFwVersionSiblings(int versionId)
    {
        var result = new List<FwVersionRecord>();
        using var reader = ExecuteReader($"""
            WITH me AS (SELECT sync_id, copy_of FROM fw_versions WHERE id = @id),
                 root AS (SELECT CASE WHEN (SELECT copy_of FROM me) <> ''
                                      THEN (SELECT copy_of FROM me)
                                      ELSE (SELECT sync_id FROM me) END AS sync_id)
            SELECT * FROM fw_versions
            WHERE id <> @id AND archived = 0 AND {NotConfig()} AND {NotDeleted()}
              AND (SELECT sync_id FROM root) <> ''
              AND (copy_of = (SELECT sync_id FROM root) OR sync_id = (SELECT sync_id FROM root))
            ORDER BY id
            """, cmd => cmd.Parameters.AddWithValue("@id", versionId));
        while (reader.Read())
            result.Add(ReadFwVersion(reader));
        return result;
    }

    /// <summary>sync_id записи — им копия ссылается на оригинал (столбец copy_of). Пусто, если строки
    /// нет: вызывающий в этом случае просто не проставляет родство.</summary>
    public string GetFwVersionSyncId(int versionId) =>
        ExecuteScalar("SELECT sync_id FROM fw_versions WHERE id = @id",
            cmd => cmd.Parameters.AddWithValue("@id", versionId)) as string ?? "";

    /// <summary>Строки-КОНФИГУРАЦИИ этой прошивки — заранее заготовленные варианты одного и того же
    /// ПО под разные комплектации шкафа (см. столбец config_name в Database.cs и FirmwareConfigService).
    /// Опознаются той же парой disk_path + version_raw, что и все записи, делящие файлы, но в отличие
    /// от копий под другие подтипы шкафа (FirmwareSubtypeLinkService — у тех config_name пуст) несут
    /// непустое имя варианта.
    ///
    /// Только живые: удалённая конфигурация — это конфигурация, которую убрали, и возвращать её как
    /// действующую нельзя. Порядок по id — тот же, в каком их заводили.</summary>
    public List<FwVersionRecord> GetFwVersionConfigs(string diskPath, string versionRaw)
    {
        if (string.IsNullOrWhiteSpace(diskPath)) return new();
        var result = new List<FwVersionRecord>();
        using var reader = ExecuteReader($"""
            SELECT * FROM fw_versions
            WHERE disk_path=@d AND version_raw=@v AND archived=0 AND {NotDeleted()}
              AND config_name IS NOT NULL AND config_name <> ''
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

    /// <summary>Ровно то, что покажет GetUnreleasedFwVersionsWithNames — счётчик на бейдже сайдбара и
    /// на вкладке Настроек обязан совпадать со списком, иначе «Модерация (7)» открывается и показывает
    /// три строки.</summary>
    public int GetUnreleasedFwVersionsCount()
    {
        var result = ExecuteScalar($"""
            SELECT COUNT(*) FROM fw_versions fv
            WHERE fv.archived = 0 AND (fv.status IS NULL OR fv.status = 'active') AND fv.released = 0
              AND {NotDeleted("fv")} AND {NotConfig("fv")} AND {NotSuperseded("fv")}
            """);
        return result is long l ? (int)l : 0;
    }

    /// <summary>Marks a version as released from moderation — set only after the user explicitly
    /// confirms the "вывести из модерации и сделать релизной?" prompt.</summary>
    public void MarkFwVersionReleased(int versionId) =>
        ExecuteNonQuery("UPDATE fw_versions SET released = 1 WHERE id = @id", cmd => cmd.Parameters.AddWithValue("@id", versionId));

    /// <summary>Вывести из модерации не одну запись, а ВСЮ прошивку целиком — вместе с записями-
    /// копиями, которые заведены тем же файлам под другими подтипами шкафа (см.
    /// FirmwareSubtypeLinkService: файлы общие, disk_path один). Иначе получалось ровно то, на что
    /// жаловался оператор: отметил в модерации лишний подтип, выпустил версию — и она тут же
    /// вернулась в модерацию, потому что копия, заведённая прямо в этом же диалоге, осталась
    /// released = 0. Проверять там нечего: это та же самая прошивка с теми же тегами.</summary>
    public void MarkFwVersionReleasedWithLinked(int versionId)
    {
        MarkFwVersionReleased(versionId);
        ExecuteNonQuery($"""
            UPDATE fw_versions SET released = 1
            WHERE {NotDeleted()} AND disk_path <> ''
              AND disk_path   = (SELECT disk_path   FROM fw_versions WHERE id = @id)
              AND version_raw = (SELECT version_raw FROM fw_versions WHERE id = @id)
            """, cmd => cmd.Parameters.AddWithValue("@id", versionId));

        // С отказом от ярлыков копия под другой подтип — это отдельная версия со своей папкой и
        // своим номером, общего disk_path у неё больше нет (см. FirmwareSubtypeLinkService). Родство
        // хранится явно, в copy_of. Без этого жалоба «отметил подтип — прошивка снова прилетела на
        // модерацию» вернулась бы в прежнем виде: проверять в копии нечего, это та же самая сборка.
        ExecuteNonQuery($"""
            WITH me AS (SELECT sync_id, copy_of FROM fw_versions WHERE id = @id),
                 root AS (SELECT CASE WHEN (SELECT copy_of FROM me) <> ''
                                      THEN (SELECT copy_of FROM me)
                                      ELSE (SELECT sync_id FROM me) END AS sync_id)
            UPDATE fw_versions SET released = 1
            WHERE {NotDeleted()} AND (SELECT sync_id FROM root) <> ''
              AND (copy_of = (SELECT sync_id FROM root) OR sync_id = (SELECT sync_id FROM root))
            """, cmd => cmd.Parameters.AddWithValue("@id", versionId));
    }

    /// <summary>id самой записи и всех её копий-ссылок на те же файлы (та же прошивка, заведённая под
    /// другими подтипами шкафа — см. MarkFwVersionReleasedWithLinked выше и FirmwareSubtypeLinkService).
    /// Нужен там, где решение модерации касается ВСЕХ этих записей сразу и его надо записать в журнал
    /// доставки по каждой (см. ConfigSyncService.RecordAndPushModeration): выпустили одну — выпущены
    /// все, значит и у коллег должны стать выпущенными все, а не одна.</summary>
    public List<int> GetFwVersionIdsSharingFiles(int versionId)
    {
        var ids = new List<int> { versionId };
        // Два вида «той же прошивки под другим подтипом»: прежние записи-ссылки (общий disk_path) и
        // нынешние самостоятельные копии со своим номером — у тех родство лежит в copy_of. Решение
        // модерации касается всех разом, значит и доехать должно до всех.
        using var reader = ExecuteReader($"""
            WITH me AS (SELECT sync_id, copy_of, disk_path, version_raw FROM fw_versions WHERE id = @id),
                 root AS (SELECT CASE WHEN (SELECT copy_of FROM me) <> ''
                                      THEN (SELECT copy_of FROM me)
                                      ELSE (SELECT sync_id FROM me) END AS sync_id)
            SELECT id FROM fw_versions
            WHERE {NotDeleted()} AND id <> @id
              AND (
                    (disk_path <> '' AND disk_path = (SELECT disk_path FROM me)
                     AND version_raw = (SELECT version_raw FROM me))
                 OR ((SELECT sync_id FROM root) <> ''
                     AND (copy_of = (SELECT sync_id FROM root) OR sync_id = (SELECT sync_id FROM root)))
                  )
            ORDER BY id
            """, cmd => cmd.Parameters.AddWithValue("@id", versionId));
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        return ids;
    }

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

    /// <summary>Номера версий, которые для этой пары подтип/контроллер уже ЗАВОДИЛИСЬ — включая
    /// удалённые (deleted_at) и откатанные. Именно этот список досмотр диска (HierarchyService.
    /// PlanFwSync/ScanFwDisk) считает «уже известными» и не заводит по ним новых записей.
    ///
    /// Ключевое отличие от GetFwVersions — надгробия. Раньше досмотр брал известные номера через
    /// GetFwVersions, а тот удалённые строки отфильтровывает: удалённая прошивка, папка которой на
    /// сетевом диске по любой причине уцелела (удаление файлов — best effort: занятый файл, нет прав
    /// на чужую папку, шара отвалилась ровно в этот момент), заводилась ближайшим досмотром ЗАНОВО —
    /// новой строкой, с released = 0, то есть прямиком в очередь модерации. Со стороны это и выглядело
    /// как жалоба «старые удалённые прошивки висят на модерации у коллеги»: удаление уехало и
    /// применилось правильно, а следом диск воскресил запись. Надгробие постоянно (см.
    /// ImportHierarchyDataCore, правило 1) — значит и досмотр обязан его уважать.
    ///
    /// <paramref name="controllerId"/> = null — все контроллеры подтипа (нужно для ОПЦ-папки, которая
    /// общая на весь подтип, см. PlanFwSync).</summary>
    public HashSet<string> GetKnownVersionRaws(int subtypeId, int? controllerId)
    {
        var sql = "SELECT version_raw FROM fw_versions WHERE subtype_id=@s";
        if (controllerId is not null) sql += " AND controller_id=@c";

        var result = new HashSet<string>(StringComparer.Ordinal);
        using var reader = ExecuteReader(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("@s", subtypeId);
            if (controllerId is not null) cmd.Parameters.AddWithValue("@c", controllerId.Value);
        });
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Теги последней известной версии этого же шкафа — ровно тот же приём и тот же порядок,
    /// что у GetLatestHmiForFirmware выше, только для тегов.
    ///
    /// Теги описывают ШКАФ («Шкаф управления пожарными насосами АМПЕРУС ПЖ-ПП-2-…»), а не конкретную
    /// сборку программы: новая версия того же ПЛК ставится в те же самые шкафы. До этого каждая новая
    /// загрузка начинала с чистого листа — программист заново набивал десяток названий шкафов или (что
    /// и происходило) не набивал, и свежая версия переставала находиться по тем запросам, по которым
    /// находилась предыдущая. Ищется по паре подтип/контроллер без привязки к hw_version — по той же
    /// причине, что и панель: шкаф один, ревизия железа у него может смениться.
    ///
    /// Откатанные, архивные и удалённые версии не в счёт: их теги — это как раз то, от чего отказались.
    /// Строки-КОНФИГУРАЦИИ тоже не в счёт (NotConfig): их теги — названия конкретных комплектаций
    /// шкафа, и на новую версию они переносятся не «в общую кучу», а целыми конфигурациями (см.
    /// FirmwareConfigService.CarryOver). Здесь нужны базовые теги самой прошивки.
    /// Возвращает null, если у шкафа ещё не было ни одной версии с тегами.</summary>
    /// <summary>Последняя живая ОСНОВНАЯ запись этого шкафа (подтип+контроллер) — та, с которой новая
    /// загрузка наследует набор конфигураций (см. FirmwareConfigService.CarryOver). Порядок и отборы
    /// те же, что у GetLatestTagsForFirmware/GetLatestHmiForFirmware рядом: шкаф один, ревизия железа
    /// у него может смениться, а откатанные/архивные/удалённые версии не в счёт.
    /// <paramref name="exceptId"/> — исключить строку, которую только что завели сами (иначе новая
    /// версия оказалась бы «предыдущей» сама себе).</summary>
    public FwVersionRecord? GetLatestPrimaryFwVersion(int subtypeId, int controllerId, int exceptId = 0)
    {
        using var reader = ExecuteReader($"""
            SELECT * FROM fw_versions
            WHERE subtype_id=@s AND controller_id=@c AND id<>@x
              AND (status IS NULL OR status='active') AND archived=0 AND {NotDeleted()} AND {NotConfig()}
            ORDER BY hw_version DESC, sw_version DESC, dt_str DESC, id DESC LIMIT 1
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@s", subtypeId);
            cmd.Parameters.AddWithValue("@c", controllerId);
            cmd.Parameters.AddWithValue("@x", exceptId);
        });
        return reader.Read() ? ReadFwVersion(reader) : null;
    }

    public (string Tags, string VersionRaw)? GetLatestTagsForFirmware(int subtypeId, int controllerId)
    {
        using var reader = ExecuteReader($"""
            SELECT tags, version_raw FROM fw_versions
            WHERE subtype_id=@s AND controller_id=@c
              AND tags IS NOT NULL AND TRIM(tags) != ''
              AND (status IS NULL OR status='active') AND archived=0 AND {NotDeleted()} AND {NotConfig()}
            ORDER BY hw_version DESC, sw_version DESC, dt_str DESC, id DESC LIMIT 1
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@s", subtypeId);
            cmd.Parameters.AddWithValue("@c", controllerId);
        });
        return reader.Read() ? (GetString(reader, "tags"), GetString(reader, "version_raw")) : null;
    }

    /// <summary>Все уже загруженные прошивки одного контроллера с заданным hw_version — любого статуса
    /// (активные, откатанные, архивные), кроме удалённых. Нужно переписыванию hw
    /// (HierarchyService.RewriteControllerHwVersion), когда оператор правит hw модификации на рабочем
    /// месте: все версии со старым hw переезжают на новый вместе с папками на диске.</summary>
    public List<FwVersionRecord> GetFwVersionsByControllerAndHw(int controllerId, int hwVersion)
    {
        var result = new List<FwVersionRecord>();
        using var reader = ExecuteReader(
            $"SELECT * FROM fw_versions WHERE controller_id=@c AND hw_version=@h AND {NotDeleted()}",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@c", controllerId);
                cmd.Parameters.AddWithValue("@h", hwVersion);
            });
        while (reader.Read())
            result.Add(ReadFwVersion(reader));
        return result;
    }

    /// <summary>Переписывает hw_version одной записи вместе с зависящими от него полями строки версии
    /// (version_raw) и путём к папке на диске (disk_path) — их пересчитывает
    /// HierarchyService.RewriteControllerHwVersion, здесь только атомарная запись в БД.</summary>
    public void UpdateFwVersionHw(int fwVersionId, int hwVersion, string versionRaw, string diskPath) =>
        ExecuteNonQuery("UPDATE fw_versions SET hw_version=@h, version_raw=@v, disk_path=@d WHERE id=@id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@h", hwVersion);
                cmd.Parameters.AddWithValue("@v", versionRaw);
                cmd.Parameters.AddWithValue("@d", diskPath);
                cmd.Parameters.AddWithValue("@id", fwVersionId);
            });

    /// <summary>Перецелить hmi_path со старой папки/файла HMI на новую — используется при правке hw
    /// (HierarchyService.RewriteControllerHwVersion): папка HMI лежит НЕ внутри папки версии, а в общей
    /// папке HMI контроллера под именем «{версия}_hmi», поэтому переименование папки версии её не
    /// задевает и hmi_path надо переписать отдельно. Обновляет ВСЕ записи, ссылающиеся ровно на старый
    /// путь, — не только «свою» версию, но и те, что унаследовали эту же панель (их hmi_path указывает
    /// на ту же папку), иначе после переименования папки их кнопка «Открыть HMI проект» упрётся в
    /// несуществующий путь.</summary>
    public void RepointHmiPath(string oldHmiPath, string newHmiPath) =>
        ExecuteNonQuery("UPDATE fw_versions SET hmi_path=@n WHERE hmi_path=@o",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@n", newHmiPath);
                cmd.Parameters.AddWithValue("@o", oldHmiPath);
            });

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

        // manual_current снимается вместе с откатом: «откатана» и «текущая» — взаимоисключающие
        // состояния (FwHistoryStatus.Labels и так не рассматривает откатанные версии в качестве
        // текущих, но без явного сброса отметка «висела» бы на записи и молча ожила бы, если её
        // потом вернуть в активные через UnrollbackFwVersion).
        ExecuteNonQuery("UPDATE fw_versions SET status='rolled_back', manual_current=0 WHERE id=@id",
            cmd => cmd.Parameters.AddWithValue("@id", fwVersionId));

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

    /// <summary>Обратное действие RollbackFwVersion — возвращает откатанную версию в обычный статус
    /// («Настройки → Прошивки → Вернуть в активные», жалоба «откатали версию по ошибке, а обратного
    /// пути нет»). Меняет только status в БД: папку на диске, переименованную RollbackFwVersion
    /// (маркер «_ОТКАТАНО», см. FileSystemHelpers.MarkRolledBackOnDisk), обратно не переименовываем —
    /// пока версия была откатана, освободившийся sw-номер мог достаться следующей загрузке, и слепой
    /// возврат имени рисковал бы конфликтовать с её файлами на диске. При необходимости оператор
    /// переименует папку вручную. Возвращает false, если версии нет или она не была откатана.</summary>
    public bool UnrollbackFwVersion(int fwVersionId)
    {
        var v = GetFwVersionById(fwVersionId);
        if (v is null || v.Status != "rolled_back") return false;

        ExecuteNonQuery("UPDATE fw_versions SET status='active' WHERE id=@id", cmd => cmd.Parameters.AddWithValue("@id", fwVersionId));
        return true;
    }

    /// <summary>Оператор вручную назначает ЭТУ версию «текущей» в её hw-группе (подтип+контроллер+hw),
    /// в обход обычного правила «текущая = версия с максимальным sw_version» (см. FwHistoryStatus.
    /// Labels) — например, когда более новую по номеру версию забраковали и по факту в шкафах стоит
    /// версия постарше, но формально откатывать её не хочется (история версий должна остаться видна
    /// целиком). В группе может быть отмечена только одна версия: перед установкой отметка снимается
    /// со всех остальных версий той же группы. На откатанной версии отметку поставить нельзя —
    /// «откатана» и «текущая» взаимоисключающие состояния. Возвращает false, если версия не найдена
    /// или откатана (тогда ничего не меняется).</summary>
    public bool SetFwVersionManualCurrent(int fwVersionId)
    {
        var v = GetFwVersionById(fwVersionId);
        if (v is null || v.Status == "rolled_back") return false;

        ExecuteNonQuery("""
            UPDATE fw_versions SET manual_current=0
            WHERE subtype_id=@s AND controller_id=@c AND hw_version=@h
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@s", v.SubtypeId);
            cmd.Parameters.AddWithValue("@c", v.ControllerId);
            cmd.Parameters.AddWithValue("@h", v.HwVersion);
        });
        ExecuteNonQuery("UPDATE fw_versions SET manual_current=1 WHERE id=@id", cmd => cmd.Parameters.AddWithValue("@id", fwVersionId));
        return true;
    }

    /// <summary>Переназначить версию другому контроллеру (модели) — правка атрибуции в истории версий,
    /// когда прошивку по ошибке завели под не тем контроллером. Версия переезжает в другую hw-группу
    /// (подтип+контроллер+hw), поэтому manual_current сбрасываем — прежняя отметка «текущая»
    /// относилась к старой группе.
    ///
    /// Это ТОЛЬКО БД-часть. Папку версии на диске переносит HierarchyService.
    /// ReassignFwVersionToController — он же и является нормальной точкой входа для операции:
    /// имя контроллера входит в путь папки (ПО\&lt;тип&gt;\&lt;подтип&gt;\&lt;контроллер&gt;\&lt;версия&gt;), поэтому
    /// правка одной лишь записи осиротила бы папку, и ближайший досмотр диска завёл бы её ОТДЕЛЬНОЙ
    /// записью-фантомом под старым контроллером (ровно то, что и происходило). <paramref
    /// name="newDiskPath"/> — новое расположение папки; null означает «путь не меняется» (запись без
    /// файлов, ОПЦ-версия, чей путь от контроллера не зависит, либо прямой вызов из тестов).
    ///
    /// Возвращает false, если версии нет, контроллер не задан или уже такой.</summary>
    public bool ReassignFwVersionController(int fwVersionId, int newControllerId, string? newDiskPath = null)
    {
        var v = GetFwVersionById(fwVersionId);
        if (v is null || newControllerId <= 0 || v.ControllerId == newControllerId) return false;

        ExecuteNonQuery("UPDATE fw_versions SET controller_id=@c, manual_current=0 WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@c", newControllerId);
            cmd.Parameters.AddWithValue("@id", fwVersionId);
        });
        if (newDiskPath is not null && newDiskPath != v.DiskPath)
            ExecuteNonQuery("UPDATE fw_versions SET disk_path=@d WHERE id=@id", cmd =>
            {
                cmd.Parameters.AddWithValue("@d", newDiskPath);
                cmd.Parameters.AddWithValue("@id", fwVersionId);
            });
        return true;
    }

    /// <summary>Все живые версии, чьи файлы лежат на диске, с именами группы/подтипа/контроллера —
    /// для обхода, убирающего осиротевшие ярлыки (HierarchyService.PruneOrphanedFirmwareShortcuts).
    /// Откатанные тоже включаем: их ярлык (если был) точно так же повисает, когда исчезают файлы.</summary>
    public List<FwShortcutTarget> GetFwVersionShortcutTargets()
    {
        var result = new List<FwShortcutTarget>();
        using var reader = ExecuteReader($"""
            SELECT fv.version_raw, fv.disk_path, fv.is_opc,
                   eg.name AS group_name, es.name AS subtype_name, cm.name AS ctrl_name
            FROM fw_versions fv
            JOIN equipment_subtypes es ON fv.subtype_id   = es.id
            JOIN equipment_groups   eg ON es.group_id     = eg.id
            JOIN controller_models  cm ON fv.controller_id = cm.id
            WHERE fv.archived=0 AND {NotDeleted("fv")}
              AND fv.disk_path IS NOT NULL AND fv.disk_path <> ''
            """);
        while (reader.Read())
            result.Add(new FwShortcutTarget(
                GetString(reader, "version_raw"), GetString(reader, "disk_path"),
                GetString(reader, "group_name"), GetString(reader, "subtype_name"),
                GetString(reader, "ctrl_name"), GetInt(reader, "is_opc") != 0));
        return result;
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
            WHERE fv.subtype_id=@s AND fv.controller_id=@c AND {NotDeleted("fv")} AND {NotConfig("fv")}
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
              AND {NotConfig("fv")}
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
            ManualCurrent = GetBool(r, "manual_current"),
            SyncId = GetString(r, "sync_id"),
            ConfigName = GetString(r, "config_name"),
            CopyOf = GetString(r, "copy_of"),
        };
    }
}
