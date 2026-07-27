using System.Collections.Generic;
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

    public int AddParamFile(ParamFile pf)
    {
        ExecuteNonQuery("""
            INSERT INTO param_files (subtype_id, manufacturer, filename, disk_path, description, upload_date)
            VALUES (@s, @m, @f, @d, @desc, @u)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@s", (object?)pf.SubtypeId ?? System.DBNull.Value);
            cmd.Parameters.AddWithValue("@m", pf.Manufacturer);
            cmd.Parameters.AddWithValue("@f", pf.Filename);
            cmd.Parameters.AddWithValue("@d", pf.DiskPath);
            cmd.Parameters.AddWithValue("@desc", pf.Description);
            cmd.Parameters.AddWithValue("@u", pf.UploadDate);
        });
        var id = ExecuteScalar("SELECT last_insert_rowid()");
        return id is long l ? (int)l : -1;
    }

    public List<ParamFile> GetParamFiles(int? subtypeId = null, string? manufacturer = null)
    {
        var sql = """
            SELECT pf.*, es.name AS subtype_name, es.folder_name, eg.name AS group_name
            FROM param_files pf
            LEFT JOIN equipment_subtypes es ON pf.subtype_id = es.id
            LEFT JOIN equipment_groups   eg ON es.group_id   = eg.id
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
        using var reader = ExecuteReader("""
            SELECT pf.*, es.name AS subtype_name, es.folder_name, eg.name AS group_name
            FROM param_files pf
            LEFT JOIN equipment_subtypes es ON pf.subtype_id = es.id
            LEFT JOIN equipment_groups   eg ON es.group_id   = eg.id
            WHERE pf.archived = 0 AND pf.disk_path = @d AND pf.filename = @f
            ORDER BY pf.id
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@d", diskPath);
            cmd.Parameters.AddWithValue("@f", filename);
        });
        while (reader.Read())
            result.Add(ReadParamFile(reader));
        return result;
    }

    public void DeleteParamFile(int fileId) =>
        ExecuteNonQuery("UPDATE param_files SET archived=1 WHERE id=@id", cmd => cmd.Parameters.AddWithValue("@id", fileId));

    /// <summary>Updates the tags of a param file — tags are shared with fw_versions via the same
    /// `tags` table (see Database.Tags.cs), just stored per-entity as a space-separated string.</summary>
    public void UpdateParamFileTags(int fileId, string tags) =>
        ExecuteNonQuery("UPDATE param_files SET tags=@t WHERE id=@id",
            cmd => { cmd.Parameters.AddWithValue("@t", tags); cmd.Parameters.AddWithValue("@id", fileId); });

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
