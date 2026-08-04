using System.Collections.Generic;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Data;

/// <summary>Шаблоны паспортов шкафов (таблица passports) — см. Domain.PassportTemplate о том, почему
/// это отдельная сущность, а не вложение прошивки. CRUD устроен дословно как у param_files
/// (Database.Params.cs): sync_id проставляется при вставке, удаление — АРХИВАЦИЯ (тумбстоун, чтобы
/// уехало к коллегам), натуральный ключ — «подтип + название».</summary>
public partial class Database
{
    public int AddPassport(PassportTemplate p)
    {
        var syncId = string.IsNullOrEmpty(p.SyncId) ? System.Guid.NewGuid().ToString() : p.SyncId;
        ExecuteNonQuery("""
            INSERT INTO passports (subtype_id, name, filename, disk_path, description, upload_date, tags, archived, sync_id)
            VALUES (@s, @n, @f, @d, @desc, @u, @tags, @arch, @sync)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@s", (object?)p.SubtypeId ?? System.DBNull.Value);
            cmd.Parameters.AddWithValue("@n", p.Name);
            cmd.Parameters.AddWithValue("@f", p.Filename);
            cmd.Parameters.AddWithValue("@d", p.DiskPath);
            cmd.Parameters.AddWithValue("@desc", p.Description);
            cmd.Parameters.AddWithValue("@u", p.UploadDate);
            cmd.Parameters.AddWithValue("@tags", p.Tags);
            cmd.Parameters.AddWithValue("@arch", p.Archived ? 1 : 0);
            cmd.Parameters.AddWithValue("@sync", syncId);
        });
        var id = ExecuteScalar("SELECT last_insert_rowid()");
        p.SyncId = syncId;
        return id is long l ? (int)l : -1;
    }

    private const string PassportSelect = """
        SELECT p.*, es.name AS subtype_name, es.folder_name, eg.name AS group_name
        FROM passports p
        LEFT JOIN equipment_subtypes es ON p.subtype_id = es.id
        LEFT JOIN equipment_groups   eg ON es.group_id   = eg.id
        """;

    public List<PassportTemplate> GetPassports(int? subtypeId = null)
    {
        var sql = PassportSelect + " WHERE p.archived = 0";
        if (subtypeId is not null) sql += " AND p.subtype_id = @s";
        sql += " ORDER BY p.upload_date DESC";

        var result = new List<PassportTemplate>();
        using var reader = ExecuteReader(sql, cmd =>
        {
            if (subtypeId is not null) cmd.Parameters.AddWithValue("@s", subtypeId.Value);
        });
        while (reader.Read())
            result.Add(ReadPassport(reader));
        return result;
    }

    private static PassportTemplate ReadPassport(Microsoft.Data.Sqlite.SqliteDataReader reader) => new()
    {
        Id = GetInt(reader, "id"),
        SubtypeId = GetIntOrNull(reader, "subtype_id"),
        Name = GetString(reader, "name"),
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

    /// <summary>Только типовые паспорта — те, что не привязаны ни к какому шкафу (см.
    /// PassportService про «Конфиг\Паспорта»). Отдельный метод, потому что null-фильтр по подтипу
    /// нельзя выразить через необязательный параметр GetPassports: там «не задан» уже значит
    /// «любой».</summary>
    public List<PassportTemplate> GetGeneralPassports()
    {
        var result = new List<PassportTemplate>();
        using var reader = ExecuteReader(PassportSelect +
            " WHERE p.archived = 0 AND p.subtype_id IS NULL ORDER BY p.name COLLATE NOCASE");
        while (reader.Read())
            result.Add(ReadPassport(reader));
        return result;
    }

    /// <summary>Живая запись по натуральному ключу «подтип + название» (без учёта регистра — папка
    /// на диске у Windows тоже без регистра, и два паспорта «ПЖ ПИ» и «пж пи» были бы одной папкой).
    /// Тот же ключ использует перезаливка, чтобы ОБНОВИТЬ запись вместо заведения дубля, и импорт
    /// конфига при первом контакте двух баз.
    ///
    /// <paramref name="subtypeId"/> = null — типовой паспорт: у него половина ключа не «какой-то
    /// подтип», а именно «подтипа нет», и сравнение через <c>= NULL</c> в SQL не сработало бы
    /// никогда (NULL не равен ничему, включая себя) — отсюда отдельная ветка IS NULL.</summary>
    public PassportTemplate? FindLivePassport(int? subtypeId, string name)
    {
        var condition = subtypeId is null ? "p.subtype_id IS NULL" : "p.subtype_id = @s";
        using var reader = ExecuteReader(PassportSelect +
            $" WHERE p.archived = 0 AND {condition} AND p.name = @n COLLATE NOCASE ORDER BY p.id", cmd =>
        {
            if (subtypeId is not null) cmd.Parameters.AddWithValue("@s", subtypeId.Value);
            cmd.Parameters.AddWithValue("@n", name);
        });
        return reader.Read() ? ReadPassport(reader) : null;
    }

    /// <summary>Запись по межмашинному идентификатору. Архивные ВОЗВРАЩАЮТСЯ намеренно: локальная
    /// архивация постоянна, и импорт обязан её увидеть, чтобы не воскресить снятую запись входящей
    /// «живой» копией с машины, которая об удалении ещё не знает (см. Database.Params.cs).</summary>
    public PassportTemplate? FindPassportBySyncId(string syncId)
    {
        if (string.IsNullOrEmpty(syncId)) return null;
        using var reader = ExecuteReader(PassportSelect + " WHERE p.sync_id = @sy ORDER BY p.id",
            cmd => cmd.Parameters.AddWithValue("@sy", syncId));
        return reader.Read() ? ReadPassport(reader) : null;
    }

    /// <summary>«Усыновление» чужого sync_id при первом контакте двух независимо заведённых баз —
    /// см. SetParamFileSyncId.</summary>
    public void SetPassportSyncId(int id, string syncId) =>
        ExecuteNonQuery("UPDATE passports SET sync_id=@sy WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@sy", syncId);
            cmd.Parameters.AddWithValue("@id", id);
        });

    /// <summary>Перезаливка паспорта: запись ОБНОВЛЯЕТСЯ (свежая дата, дописанный журнал в описании,
    /// возможно другое имя файла — сменили docx на pdf), а не плодится новой строкой.</summary>
    public void UpdatePassportUpload(int id, string diskPath, string filename, string description, string uploadDate) =>
        ExecuteNonQuery("UPDATE passports SET disk_path=@d, filename=@f, description=@desc, upload_date=@u WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@d", diskPath);
            cmd.Parameters.AddWithValue("@f", filename);
            cmd.Parameters.AddWithValue("@desc", description);
            cmd.Parameters.AddWithValue("@u", uploadDate);
            cmd.Parameters.AddWithValue("@id", id);
        });

    /// <summary>Архивация (мягкое удаление): строка остаётся и уезжает в общий конфиг ТУМБСТОУНОМ,
    /// иначе удаление жило бы только на той машине, где его сделали. Файл на диске не трогается.</summary>
    public void DeletePassport(int id) =>
        ExecuteNonQuery("UPDATE passports SET archived=1 WHERE id=@id", cmd => cmd.Parameters.AddWithValue("@id", id));

    public void UpdatePassportTags(int id, string tags) =>
        ExecuteNonQuery("UPDATE passports SET tags=@t WHERE id=@id",
            cmd => { cmd.Parameters.AddWithValue("@t", tags); cmd.Parameters.AddWithValue("@id", id); });

    /// <summary>Есть ли у подтипов паспорта — одним запросом на всю выдачу поиска. Карточка прошивки
    /// спрашивает это на каждый результат, и отдельный GetPassports(subtypeId) на карточку означал бы
    /// N запросов вместо одного.</summary>
    public HashSet<int> GetSubtypeIdsWithPassports()
    {
        var result = new HashSet<int>();
        using var reader = ExecuteReader("SELECT DISTINCT subtype_id FROM passports WHERE archived = 0 AND subtype_id IS NOT NULL");
        while (reader.Read())
            result.Add(reader.GetInt32(0));
        return result;
    }
}
