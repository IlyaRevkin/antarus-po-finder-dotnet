using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    // ── Param Manufacturers ───────────────────────────────────────────────────

    public List<string> GetParamManufacturers()
    {
        var result = new List<string>();
        using var reader = ExecuteReader("SELECT name FROM param_manufacturers ORDER BY sort_order, name");
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Отметка в flat_list_state — чтобы добавленный здесь производитель не был стёрт
    /// импортом конфига с машины, которая о нём ещё не знает (см. Database.FlatLists.cs).</summary>
    public void AddParamManufacturer(string name)
    {
        ExecuteNonQuery("INSERT OR IGNORE INTO param_manufacturers(name) VALUES(@n)", cmd => cmd.Parameters.AddWithValue("@n", name));
        MarkFlatListAlive(FlatKindManufacturer, name);
    }

    public void DeleteParamManufacturer(string name)
    {
        ExecuteNonQuery("DELETE FROM param_manufacturers WHERE name=@n", cmd => cmd.Parameters.AddWithValue("@n", name));
        MarkFlatListDeleted(FlatKindManufacturer, name);
    }

    // ── Param Files ───────────────────────────────────────────────────────────

    /// <summary>Начало любого чтения файлов параметров. Именами группы/подтипа, которые дотягивает
    /// этот JOIN, пользуется ReadParamFile — расходись список колонок между четырьмя запросами ниже,
    /// один из них молча отдавал бы записи с пустым названием подтипа. LEFT JOIN, а не JOIN: подтип у
    /// файла может быть не проставлен, и такая запись обязана остаться в выдаче.</summary>
    private const string ParamFileSelect = """
        SELECT pf.*, es.name AS subtype_name, es.folder_name, eg.name AS group_name
        FROM param_files pf
        LEFT JOIN equipment_subtypes es ON pf.subtype_id = es.id
        LEFT JOIN equipment_groups   eg ON es.group_id   = eg.id
        """;

    /// <summary>Заводит новую запись файла параметров. sync_id проставляется ПРЯМО ЗДЕСЬ, а не
    /// откладывается до следующего BackfillSyncIds на старте приложения: строка может уехать в общий
    /// конфиг в ту же минуту (экспорт делается вручную сразу после загрузки), а строка без sync_id у
    /// получателя не соотносится ни с чем и не умеет ни архивироваться тумбстоуном, ни обновляться.
    /// Готовый SyncId в объекте уважается — им пользуется импорт конфига, когда переносит чужую
    /// строку вместе с её идентификатором.</summary>
    public int AddParamFile(ParamFile pf)
    {
        var syncId = string.IsNullOrEmpty(pf.SyncId) ? System.Guid.NewGuid().ToString() : pf.SyncId;
        ExecuteNonQuery("""
            INSERT INTO param_files (subtype_id, manufacturer, filename, disk_path, description, upload_date, tags, archived, sync_id)
            VALUES (@s, @m, @f, @d, @desc, @u, @tags, @arch, @sync)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@s", (object?)pf.SubtypeId ?? System.DBNull.Value);
            cmd.Parameters.AddWithValue("@m", pf.Manufacturer);
            cmd.Parameters.AddWithValue("@f", pf.Filename);
            cmd.Parameters.AddWithValue("@d", pf.DiskPath);
            cmd.Parameters.AddWithValue("@desc", pf.Description);
            cmd.Parameters.AddWithValue("@u", pf.UploadDate);
            cmd.Parameters.AddWithValue("@tags", pf.Tags);
            cmd.Parameters.AddWithValue("@arch", pf.Archived ? 1 : 0);
            cmd.Parameters.AddWithValue("@sync", syncId);
        });
        var id = ExecuteScalar("SELECT last_insert_rowid()");
        pf.SyncId = syncId;
        return id is long l ? (int)l : -1;
    }

    public List<ParamFile> GetParamFiles(int? subtypeId = null, string? manufacturer = null)
    {
        var sql = $"""
            {ParamFileSelect}
            WHERE pf.archived = 0
            """;
        var binds = new List<(string, object)>();
        if (subtypeId is not null) { sql += " AND pf.subtype_id = @s"; binds.Add(("@s", subtypeId.Value)); }
        if (!string.IsNullOrEmpty(manufacturer)) { sql += " AND pf.manufacturer = @m"; binds.Add(("@m", manufacturer)); }
        sql += " ORDER BY pf.upload_date DESC";

        var result = new List<ParamFile>();
        using var reader = ExecuteReader(sql, cmd =>
        {
            foreach (var (name, value) in binds)
                cmd.Parameters.AddWithValue(name, value);
        });
        while (reader.Read())
            result.Add(ReadParamFile(reader));
        return result;
    }

    /// <summary>Подтипы, у которых есть хоть один живой файл параметров, — ОДНИМ запросом на всю
    /// выдачу поиска. Раньше карточка отвечала на «показывать ли кнопку „Параметры“» вызовом
    /// GetParamFiles(subtypeId), то есть запросом с двумя JOIN'ами и полным разбором строк ради
    /// одного Count > 0, и таких запросов было по числу найденных версий (см. SearchView.BuildCard).</summary>
    public HashSet<int> GetSubtypeIdsWithParamFiles()
    {
        var result = new HashSet<int>();
        using var reader = ExecuteReader(
            "SELECT DISTINCT subtype_id FROM param_files WHERE archived = 0 AND subtype_id IS NOT NULL");
        while (reader.Read())
            result.Add(reader.GetInt32(0));
        return result;
    }

    private static ParamFile ReadParamFile(Microsoft.Data.Sqlite.SqliteDataReader reader) => new()
    {
        Id = GetInt(reader, "id"),
        SubtypeId = GetIntOrNull(reader, "subtype_id"),
        Manufacturer = GetString(reader, "manufacturer"),
        Filename = GetString(reader, "filename"),
        DiskPath = GetString(reader, "disk_path"),
        Description = GetString(reader, "description"),
        UploadDate = GetString(reader, "upload_date"),
        Archived = GetBool(reader, "archived"),
        Tags = GetString(reader, "tags"),
        SyncId = GetString(reader, "sync_id"),
        SubtypeName = GetString(reader, "subtype_name"),
        FolderName = GetString(reader, "folder_name"),
        GroupName = GetString(reader, "group_name"),
    };

    /// <summary>Все записи, стоящие за ОДНИМ и тем же файлом на диске — по одной на каждый подтип, к
    /// которому файл привязан (см. ParamFileLinkService: копии заводятся с тем же disk_path, файл
    /// физически лежит один раз). Именно этот набор правит диалог «Подтипы» у уже загруженного файла.
    /// Архивные (удалённые) записи не возвращаются — иначе снятый когда-то подтип считался бы
    /// привязанным до сих пор.</summary>
    public List<ParamFile> GetParamFilesSharingFile(string diskPath, string filename)
    {
        var result = new List<ParamFile>();
        if (string.IsNullOrWhiteSpace(diskPath) || string.IsNullOrWhiteSpace(filename)) return result;

        var wantedFolder = FileKey(diskPath);
        var wantedName = FileKey(filename);
        // Отбор в .NET, а не в SQL: см. FileKey — двоичное сравнение путей и имён и было причиной
        // расплодившихся записей. Строк тут сотни, полный проход дешевле неверного ответа.
        using var reader = ExecuteReader($"""
            {ParamFileSelect}
            WHERE pf.archived = 0
            ORDER BY pf.id
            """);
        while (reader.Read())
        {
            var row = ReadParamFile(reader);
            if (FileKey(row.DiskPath) == wantedFolder && FileKey(row.Filename) == wantedName)
                result.Add(row);
        }
        return result;
    }

    /// <summary>Имя файла, папки или производителя, приведённое к виду, в котором их МОЖНО
    /// сравнивать. Регистр свёрнут, пробелы по краям убраны, повторы пробелов схлопнуты, хвостовой
    /// слеш у пути отброшен.
    ///
    /// Тикет: «Создаются дубликаты файлов параметров. Пример: x2 XL Chint». Причина — сравнение
    /// было двоичным (в схеме у param_files.filename и .manufacturer нет COLLATE NOCASE), а
    /// файловая система Windows регистр не различает. Оператор перезаливает тот же файл, назвав
    /// его «Chint XL.par» вместо «chint xl.par»: на диске это ОДИН файл, прежний уезжает в
    /// «Прежние редакции», а в базе поиск существующей записи промахивается и заводит вторую
    /// живую строку на тот же файл.
    ///
    /// Свёртка регистра сделана через ToUpperInvariant, а не COLLATE NOCASE в SQL, намеренно:
    /// NOCASE у SQLite сворачивает только ASCII, и на кириллических именах («Параметры X2.par»)
    /// он бы снова промахнулся. Это та же ловушка, что описана у Database.ConfigExchange.ImportFlatList.</summary>
    internal static string FileKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.TrimEnd('\\', '/').ToUpperInvariant();
    }

    /// <summary>Живая (не архивная) запись по натуральному ключу «подтип + производитель + имя
    /// файла» — тот же ключ, по которому импорт конфига соотносит строки при первом контакте, и по
    /// которому перезаливка находит запись, чтобы ОБНОВИТЬ её вместо заведения дубля.</summary>
    public ParamFile? FindLiveParamFile(int subtypeId, string manufacturer, string filename)
    {
        var wantedManufacturer = FileKey(manufacturer);
        var wantedName = FileKey(filename);
        if (wantedName.Length == 0) return null;

        // Сужаем по подтипу в SQL, а производителя и имя сверяем в .NET через FileKey: именно
        // двоичное сравнение этих двух полей и плодило дубликаты (см. FileKey).
        using var reader = ExecuteReader($"""
            {ParamFileSelect}
            WHERE pf.archived = 0 AND pf.subtype_id = @s
            ORDER BY pf.id
            """, cmd => cmd.Parameters.AddWithValue("@s", subtypeId));
        while (reader.Read())
        {
            var row = ReadParamFile(reader);
            if (FileKey(row.Manufacturer) == wantedManufacturer && FileKey(row.Filename) == wantedName)
                return row;
        }
        return null;
    }

    /// <summary>Запись по её межмашинному идентификатору — основной путь импорта конфига (см.
    /// Database.ConfigExchange.cs). Архивные ВОЗВРАЩАЮТСЯ намеренно: локальная архивация постоянна,
    /// и импорт обязан её увидеть, чтобы не воскресить снятую запись входящей «живой» копией с
    /// машины, которая об удалении ещё не знает.</summary>
    public ParamFile? FindParamFileBySyncId(string syncId)
    {
        if (string.IsNullOrEmpty(syncId)) return null;
        using var reader = ExecuteReader($"""
            {ParamFileSelect}
            WHERE pf.sync_id = @sy
            ORDER BY pf.id
            """, cmd => cmd.Parameters.AddWithValue("@sy", syncId));
        return reader.Read() ? ReadParamFile(reader) : null;
    }

    /// <summary>«Усыновление» чужого sync_id при первом контакте двух независимо заведённых баз:
    /// строка совпала по натуральному ключу, но идентификаторы у сторон разные (каждая сгенерировала
    /// свой). Тот же приём, что у типов/подтипов/контроллеров (см. adoptSyncId в
    /// ImportHierarchyDataCore) — со следующей синхронизации строки соотносятся уже по sync_id, и
    /// переименование файла/смена производителя перестают выглядеть как «удалили и завели новую».</summary>
    public void SetParamFileSyncId(int fileId, string syncId) =>
        ExecuteNonQuery("UPDATE param_files SET sync_id=@sy WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@sy", syncId);
            cmd.Parameters.AddWithValue("@id", fileId);
        });

    /// <summary>Перезаливка файла под тем же именем: запись ОБНОВЛЯЕТСЯ (свежая дата, дописанный
    /// журнал изменений в описании), а не плодится новой строкой — см. ParamFileUploadService.
    /// Путь на диске тоже обновляется: тот же файл могли перезалить с машины с другой буквой диска.</summary>
    public void UpdateParamFileUpload(int fileId, string diskPath, string description, string uploadDate) =>
        ExecuteNonQuery("UPDATE param_files SET disk_path=@d, description=@desc, upload_date=@u WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@d", diskPath);
            cmd.Parameters.AddWithValue("@desc", description);
            cmd.Parameters.AddWithValue("@u", uploadDate);
            cmd.Parameters.AddWithValue("@id", fileId);
        });

    /// <summary>Архивация записи (мягкое удаление). Именно архивация, а не DELETE: строка остаётся в
    /// таблице и уезжает в общий конфиг ТУМБСТОУНОМ — иначе удаление жило бы только на той машине,
    /// где его сделали, а у коллег снятая запись оставалась бы навсегда (жалоба «у меня 2 записи, у
    /// коллеги 4»). Файл на диске не трогается никогда.</summary>
    public void DeleteParamFile(int fileId) =>
        ExecuteNonQuery("UPDATE param_files SET archived=1 WHERE id=@id", cmd => cmd.Parameters.AddWithValue("@id", fileId));

    /// <summary>Updates the tags of a param file — tags are shared with fw_versions via the same
    /// `tags` table (see Database.Tags.cs), just stored per-entity as a space-separated string.</summary>
    public void UpdateParamFileTags(int fileId, string tags)
    {
        // Та же отметка о снятии тега, что и у прошивок (см. Database.FlatLists.RecordRowTagChange):
        // без неё снятый тег вернулся бы с чужой машины. Читаем прежний набор ДО записи нового.
        string? syncId = null;
        var oldTags = "";
        using (var reader = ExecuteReader("SELECT sync_id, tags FROM param_files WHERE id=@id",
                   cmd => cmd.Parameters.AddWithValue("@id", fileId)))
        {
            if (reader.Read())
            {
                syncId = reader.IsDBNull(0) ? null : reader.GetString(0);
                oldTags = reader.IsDBNull(1) ? "" : reader.GetString(1);
            }
        }
        RecordRowTagChange(syncId, oldTags, tags);

        ExecuteNonQuery("UPDATE param_files SET tags=@t WHERE id=@id",
            cmd => { cmd.Parameters.AddWithValue("@t", tags); cmd.Parameters.AddWithValue("@id", fileId); });
    }

    // ── Allowed Upload Extensions ─────────────────────────────────────────────

    public List<string> GetAllowedExtensions()
    {
        var result = new List<string>();
        using var reader = ExecuteReader("SELECT ext FROM allowed_extensions ORDER BY ext");
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    public void AddAllowedExtension(string ext)
    {
        ext = ext.Trim().ToLowerInvariant().TrimStart('.');
        if (string.IsNullOrEmpty(ext)) return;
        ExecuteNonQuery("INSERT OR IGNORE INTO allowed_extensions(ext) VALUES(@e)", cmd => cmd.Parameters.AddWithValue("@e", ext));
        MarkFlatListAlive(FlatKindExtension, ext);
    }

    public void RemoveAllowedExtension(string ext)
    {
        ext = ext.Trim().ToLowerInvariant().TrimStart('.');
        ExecuteNonQuery("DELETE FROM allowed_extensions WHERE ext=@e", cmd => cmd.Parameters.AddWithValue("@e", ext));
        MarkFlatListDeleted(FlatKindExtension, ext);
    }

    // ── Allowed HMI Upload Extensions ───────────────────────────────────────────
    // Полный аналог блока выше, но для отдельного справочника расширений HMI-проектов
    // (allowed_extensions_hmi) — независимая таблица, свой FlatKind, своя проверка при загрузке
    // HMI-вложения (см. FirmwareUploadService.Prepare).

    public List<string> GetAllowedExtensionsHmi()
    {
        var result = new List<string>();
        using var reader = ExecuteReader("SELECT ext FROM allowed_extensions_hmi ORDER BY ext");
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    public void AddAllowedExtensionHmi(string ext)
    {
        ext = ext.Trim().ToLowerInvariant().TrimStart('.');
        if (string.IsNullOrEmpty(ext)) return;
        ExecuteNonQuery("INSERT OR IGNORE INTO allowed_extensions_hmi(ext) VALUES(@e)", cmd => cmd.Parameters.AddWithValue("@e", ext));
        MarkFlatListAlive(FlatKindExtensionHmi, ext);
    }

    public void RemoveAllowedExtensionHmi(string ext)
    {
        ext = ext.Trim().ToLowerInvariant().TrimStart('.');
        ExecuteNonQuery("DELETE FROM allowed_extensions_hmi WHERE ext=@e", cmd => cmd.Parameters.AddWithValue("@e", ext));
        MarkFlatListDeleted(FlatKindExtensionHmi, ext);
    }

    // ── Allowed Schematic Search Extensions ─────────────────────────────────────
    // Тот же CRUD, что и у двух блоков выше, но для расширений, которые поиск по схемам на втором
    // диске (SchematicService) вообще считает схемой — раньше был захардкожен в
    // SchematicService.SchematicExtensions, теперь настраивается здесь (allowed_extensions_schematic),
    // свой независимый список, свой FlatKind.

    public List<string> GetAllowedExtensionsSchematic()
    {
        var result = new List<string>();
        using var reader = ExecuteReader("SELECT ext FROM allowed_extensions_schematic ORDER BY ext");
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    public void AddAllowedExtensionSchematic(string ext)
    {
        ext = ext.Trim().ToLowerInvariant().TrimStart('.');
        if (string.IsNullOrEmpty(ext)) return;
        ExecuteNonQuery("INSERT OR IGNORE INTO allowed_extensions_schematic(ext) VALUES(@e)", cmd => cmd.Parameters.AddWithValue("@e", ext));
        MarkFlatListAlive(FlatKindExtensionSchematic, ext);
    }

    public void RemoveAllowedExtensionSchematic(string ext)
    {
        ext = ext.Trim().ToLowerInvariant().TrimStart('.');
        ExecuteNonQuery("DELETE FROM allowed_extensions_schematic WHERE ext=@e", cmd => cmd.Parameters.AddWithValue("@e", ext));
        MarkFlatListDeleted(FlatKindExtensionSchematic, ext);
    }
}
