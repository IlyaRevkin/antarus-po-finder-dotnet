using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    /// <summary>Группа параметров — такой же плоский список-справочник, как виды доп. материалов и
    /// производители ПЧ/УПП, и синхронизируется тем же LWW-механизмом (см. Database.FlatLists.cs).</summary>
    public const string FlatKindParamGroup = "param_group";

    // ── Справочник групп ──────────────────────────────────────────────────────

    /// <summary>Группы в ПОРЯДКЕ ПОКАЗА, а не по алфавиту — весь смысл справочника в том, что
    /// «Основные настройки» идут первыми, а «Сброс до заводских» последним.</summary>
    public List<string> GetParamGroups()
    {
        var result = new List<string>();
        using var reader = ExecuteReader("SELECT name FROM param_groups ORDER BY sort_order, name");
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Имя группы → её место в порядке. Нужен показу таблицы: строки группируются и
    /// сортируются по этому числу, а не по названию.</summary>
    public Dictionary<string, int> GetParamGroupOrder()
    {
        // Словарь с игнором регистра в .NET — но заполняется он аккуратно, через TryAdd: «Двигатель»
        // и «двигатель» для SQLite РАЗНЫЕ строки (COLLATE NOCASE у него сворачивает только латиницу),
        // и обе могли доехать сюда с разных машин. Простое присваивание по ключу здесь не упало бы,
        // а вот ToDictionary(..., OrdinalIgnoreCase) упал бы на дубликате — ровно та же грабля, что
        // описана у Database.ConfigExchange.ImportFlatList.
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var reader = ExecuteReader("SELECT name, sort_order FROM param_groups ORDER BY sort_order, name");
        while (reader.Read())
            result.TryAdd(reader.GetString(0), reader.GetInt32(1));
        return result;
    }

    /// <summary>Завести группу (или подтвердить, что она живая). Порядок по умолчанию — в конец, но
    /// ПЕРЕД «Сбросом до заводских»: своя группа, вставшая ниже сброса, означала бы, что человек,
    /// идущий по таблице сверху вниз, сперва обнулит частотник, а потом станет что-то выставлять.
    ///
    /// ⚠️ Регистр сворачиваем В .NET. У param_groups.name объявлен COLLATE NOCASE, и полагаться на
    /// него нельзя: SQLite сворачивает им только ASCII, а все группы здесь кириллические. Имя, уже
    /// известное списку в другом написании, второй раз не заводится — берётся существующее
    /// (дословно приём из AddFwAttachmentKind).</summary>
    public void AddParamGroup(string name, int? sortOrder = null)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return;

        var existing = GetParamGroups().FirstOrDefault(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (sortOrder is not null)
                ExecuteNonQuery("UPDATE param_groups SET sort_order=@s WHERE name=@n COLLATE NOCASE", cmd =>
                {
                    cmd.Parameters.AddWithValue("@s", sortOrder.Value);
                    cmd.Parameters.AddWithValue("@n", existing);
                });
            MarkFlatListAlive(FlatKindParamGroup, existing);
            return;
        }

        var order = sortOrder ?? NextParamGroupOrder();
        ExecuteNonQuery("INSERT OR IGNORE INTO param_groups(name, sort_order) VALUES(@n, @s)", cmd =>
        {
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@s", order);
        });
        MarkFlatListAlive(FlatKindParamGroup, name);
    }

    /// <summary>Группы вместе с их местом в порядке — то, что правит раздел «Группы параметров
    /// ПЧ/УПП» в Настройках. Показу таблицы хватает <see cref="GetParamGroupOrder"/>, а вот
    /// перестановке нужен именно список: соседа по списку по словарю не найти.</summary>
    public List<(string Name, int SortOrder)> GetParamGroupsWithOrder()
    {
        var result = new List<(string, int)>();
        using var reader = ExecuteReader("SELECT name, sort_order FROM param_groups ORDER BY sort_order, name");
        while (reader.Read())
            result.Add((reader.GetString(0), reader.GetInt32(1)));
        return result;
    }

    /// <summary>Куда встанет группа, заведённая без явного места: на десятку выше самой поздней из
    /// ОСТАЛЬНЫХ, но всегда ВЫШЕ «Сброса до заводских» — он обязан остаться последним, чем бы список
    /// ни пополняли. Своя группа, вставшая ниже сброса, означала бы, что человек, идущий по таблице
    /// сверху вниз, сперва обнулит частотник, а потом станет что-то выставлять.
    ///
    /// ⚠️ Считается по СПИСКУ, а не запросом «где sort_order &lt; 1000»: с тех пор как порядок
    /// правят руками (ParamGroupEditing.Move перенумеровывает весь список десятками), число 1000
    /// перестало быть меткой заключительной группы — ею стало само название.</summary>
    private int NextParamGroupOrder()
    {
        var groups = GetParamGroupsWithOrder();
        var reset = groups.FirstOrDefault(g =>
            string.Equals(g.Name, ParamGroupCatalog.FactoryReset, StringComparison.OrdinalIgnoreCase));

        var others = groups
            .Where(g => !string.Equals(g.Name, ParamGroupCatalog.FactoryReset, StringComparison.OrdinalIgnoreCase))
            .Select(g => g.SortOrder).ToList();
        var next = (others.Count == 0 ? 0 : others.Max()) + 10;

        // Место кончилось — двигаем сброс вниз, а не втискиваем новую группу под него.
        if (reset.Name is not null && next >= reset.SortOrder)
            AddParamGroup(reset.Name, next + 10);

        return next;
    }

    /// <summary>Сколько ЖИВЫХ строк помечено этой группой — вопрос «а на ней что-нибудь держится?»
    /// перед удалением группы из справочника. Сравнение в .NET по той же причине, что везде:
    /// COLLATE NOCASE кириллицу не сворачивает.</summary>
    public int CountParamRowsInGroup(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return 0;

        var count = 0;
        using var reader = ExecuteReader("""
            SELECT r.group_name
            FROM param_table_rows r
            JOIN param_table_revisions rev ON r.revision_id = rev.id
            JOIN param_tables t            ON rev.table_id  = t.id
            WHERE rev.deleted_at = '' AND t.deleted_at = ''
            """);
        while (reader.Read())
            if (string.Equals(reader.GetString(0), name, StringComparison.OrdinalIgnoreCase))
                count++;
        return count;
    }

    /// <summary>Переименовать группу В СТРОКАХ уже сохранённых редакций.
    ///
    /// ⚠️ Единственное место во всей подсистеме, где строки ревизии переписываются, и это осознанно:
    /// группа — ПОДПИСЬ строки, а не её содержимое, и оставить полсотни строк с именем, которого в
    /// справочнике больше нет, значит выкинуть их из порядка показа (см.
    /// ParamGroupCatalog.OrderOf — незнакомая группа уходит в конец).
    ///
    /// ⚠️ И ровно поэтому же переименование ЛОКАЛЬНОЕ: строки ревизий между машинами не
    /// переписываются никогда (ImportParamTableRevisions правит только «зачем»), так что у коллег в
    /// их копиях этих же редакций останется прежнее имя. Об этом сказано и человеку — в окне
    /// правки справочника.</summary>
    public int RenameParamGroupInRows(string from, string to)
    {
        from = (from ?? "").Trim();
        to = (to ?? "").Trim();
        if (from.Length == 0 || to.Length == 0) return 0;

        // Сравнение в .NET, а не COLLATE NOCASE: имена кириллические, а NOCASE у SQLite сворачивает
        // только латиницу (см. CLAUDE.md).
        var ids = new List<int>();
        using (var reader = ExecuteReader("SELECT id, group_name FROM param_table_rows"))
            while (reader.Read())
                if (string.Equals(reader.GetString(1), from, StringComparison.OrdinalIgnoreCase))
                    ids.Add(reader.GetInt32(0));

        foreach (var id in ids)
            ExecuteNonQuery("UPDATE param_table_rows SET group_name=@to WHERE id=@id", cmd =>
            {
                cmd.Parameters.AddWithValue("@to", to);
                cmd.Parameters.AddWithValue("@id", id);
            });
        return ids.Count;
    }

    /// <summary>Убрать группу из справочника. Уже сохранённые ревизии свою группу СОХРАНЯЮТ: в
    /// param_table_rows она лежит строкой, а не ссылкой — потерять подпись «Двигатель» у полусотни
    /// строк из-за чистки справочника значило бы обесценить сам документ (то же правило, что у
    /// видов доп. материалов и производителей).</summary>
    public void DeleteParamGroup(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return;

        // Отметка ставится на ТО ЖЕ написание, что лежит в списке, — см. DeleteFwAttachmentKind.
        var stored = GetParamGroups().FirstOrDefault(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase)) ?? name;
        ExecuteNonQuery("DELETE FROM param_groups WHERE name=@n COLLATE NOCASE",
            cmd => cmd.Parameters.AddWithValue("@n", stored));
        MarkFlatListDeleted(FlatKindParamGroup, stored);
    }

    /// <summary>Стартовый набор групп с осмысленным порядком (см. ParamGroupCatalog). Разовым
    /// флагом, а не «сеем, пока таблица пуста»: сид применяется только к НОВОЙ базе, а таблица
    /// появляется и у давно установленных копий — им набор нужен ровно один раз. Осознанно удалённую
    /// потом группу миграция не воскрешает.</summary>
    private void SeedParamGroupsOnce()
    {
        const string doneFlag = "migration_param_groups_seeded";
        if (GetSetting(doneFlag) == "true") return;

        foreach (var (name, order) in ParamGroupCatalog.Defaults)
            AddParamGroup(name, order);
        SetSetting(doneFlag, "true");
    }

    // ── Документы ─────────────────────────────────────────────────────────────

    private const string ParamTableColumns =
        "id, disk_path, filename, name, manufacturer, tags, created_at, updated_at, deleted_at, sync_id";

    private static ParamTable ReadParamTable(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = GetInt(r, "id"),
        DiskPath = GetString(r, "disk_path"),
        Filename = GetString(r, "filename"),
        Name = GetString(r, "name"),
        Manufacturer = GetString(r, "manufacturer"),
        Tags = GetString(r, "tags"),
        CreatedAt = GetString(r, "created_at"),
        UpdatedAt = GetString(r, "updated_at"),
        DeletedAt = GetString(r, "deleted_at"),
        SyncId = GetString(r, "sync_id"),
    };

    /// <summary>Все живые документы, привязанные к ФАЙЛУ параметров (папка + имя).
    ///
    /// ⚠️ Отбор идёт в .NET через <see cref="FileKey"/>, а не сравнением в SQL, и это не
    /// оптимизация наоборот. Один и тот же файл в базе адресуется по-разному: у param_files на него
    /// приходится по строке на каждый привязанный подтип, пути записаны с буквой диска той машины,
    /// что заливала, а имена файлов на Windows регистр не различают. Двоичное сравнение здесь уже
    /// однажды расплодило дубли записей (см. док FileKey) — повторять эту ошибку в новой таблице
    /// незачем. Документов на файл единицы, полный проход дешевле неверного ответа.</summary>
    public List<ParamTable> GetParamTablesForFile(string diskPath, string filename)
    {
        var result = new List<ParamTable>();
        var wantedFolder = FileKey(diskPath);
        var wantedName = FileKey(filename);
        if (wantedName.Length == 0) return result;

        using var reader = ExecuteReader(
            $"SELECT {ParamTableColumns} FROM param_tables WHERE deleted_at='' ORDER BY id");
        while (reader.Read())
        {
            var row = ReadParamTable(reader);
            if (FileKey(row.DiskPath) == wantedFolder && FileKey(row.Filename) == wantedName)
                result.Add(row);
        }
        return result;
    }

    /// <summary>Все живые документы вообще — списку на странице «Параметры» и поиску.</summary>
    public List<ParamTable> GetParamTables()
    {
        var result = new List<ParamTable>();
        using var reader = ExecuteReader(
            $"SELECT {ParamTableColumns} FROM param_tables WHERE deleted_at='' ORDER BY name, id");
        while (reader.Read())
            result.Add(ReadParamTable(reader));
        return result;
    }

    /// <summary>Все документы, ВКЛЮЧАЯ СНЯТЫЕ — только для выгрузки конфига. Снятые и есть
    /// тумбстоуны: не отправь их, и удаление осталось бы жить на одной машине, а у коллег документ
    /// висел бы вечно (ровно жалоба «у меня 2 записи, у коллеги 4»).</summary>
    internal List<ParamTable> AllParamTablesIncludingDeleted()
    {
        var result = new List<ParamTable>();
        using var reader = ExecuteReader($"SELECT {ParamTableColumns} FROM param_tables ORDER BY id");
        while (reader.Read())
            result.Add(ReadParamTable(reader));
        return result;
    }

    /// <summary>Ревизии документа, ВКЛЮЧАЯ СНЯТЫЕ, в порядке номеров — тоже только для выгрузки.</summary>
    internal List<ParamTableRevision> AllParamTableRevisionsIncludingDeleted(int tableId)
    {
        var result = new List<ParamTableRevision>();
        using var reader = ExecuteReader(
            $"SELECT {RevisionColumns} FROM param_table_revisions WHERE table_id=@t ORDER BY number, id",
            cmd => cmd.Parameters.AddWithValue("@t", tableId));
        while (reader.Read())
            result.Add(ReadRevision(reader));
        return result;
    }

    /// <summary>Названия групп, которыми помечена хоть одна ЖИВАЯ строка таблиц параметров. Мягкий
    /// предохранитель эталонной синхронизации: группу, на которой что-то держится, чужой полный
    /// снимок не убирает (тот же приём, что у видов доп. материалов — CollectUsedAttachmentKinds).
    /// Группа хранится в строке ТЕКСТОМ, а не внешним ключом, поэтому и проверка текстовая.</summary>
    internal HashSet<string> CollectUsedParamGroups()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = ExecuteReader("""
            SELECT DISTINCT r.group_name
            FROM param_table_rows r
            JOIN param_table_revisions rev ON r.revision_id = rev.id
            JOIN param_tables t            ON rev.table_id  = t.id
            WHERE r.group_name <> '' AND rev.deleted_at = '' AND t.deleted_at = ''
            """);
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    public ParamTable? GetParamTable(int id)
    {
        using var reader = ExecuteReader($"SELECT {ParamTableColumns} FROM param_tables WHERE id=@id",
            cmd => cmd.Parameters.AddWithValue("@id", id));
        return reader.Read() ? ReadParamTable(reader) : null;
    }

    /// <summary>Документ по межмашинному идентификатору. Снятые ВОЗВРАЩАЮТСЯ намеренно: локальное
    /// удаление постоянно, и импорт обязан его увидеть, чтобы не воскресить документ входящей
    /// «живой» копией с машины, которая об удалении ещё не знает (как у param_files).</summary>
    internal ParamTable? FindParamTableBySyncId(string? syncId)
    {
        if (string.IsNullOrWhiteSpace(syncId)) return null;
        using var reader = ExecuteReader($"SELECT {ParamTableColumns} FROM param_tables WHERE sync_id=@sy",
            cmd => cmd.Parameters.AddWithValue("@sy", syncId));
        return reader.Read() ? ReadParamTable(reader) : null;
    }

    /// <summary>Заводит документ. sync_id и отметки времени проставляются ПРЯМО ЗДЕСЬ, а не
    /// откладываются до следующего старта приложения: документ может уехать в общий конфиг в ту же
    /// минуту, а без sync_id получатель не соотнесёт его ни с чем (та же причина, что у
    /// AddParamFile и AddFwAttachment). Готовые отметки уважаются — ими пользуется импорт.</summary>
    public int AddParamTable(ParamTable table)
    {
        var syncId = string.IsNullOrEmpty(table.SyncId) ? Guid.NewGuid().ToString() : table.SyncId;
        var createdAt = string.IsNullOrEmpty(table.CreatedAt) ? NowIso() : table.CreatedAt;
        var updatedAt = string.IsNullOrEmpty(table.UpdatedAt) ? NowIsoPrecise() : table.UpdatedAt;

        ExecuteNonQuery("""
            INSERT INTO param_tables (disk_path, filename, name, manufacturer, tags, created_at, updated_at, deleted_at, sync_id)
            VALUES (@d, @f, @n, @m, @tg, @c, @u, @del, @sy)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@d", table.DiskPath);
            cmd.Parameters.AddWithValue("@f", table.Filename);
            cmd.Parameters.AddWithValue("@n", table.Name);
            cmd.Parameters.AddWithValue("@m", table.Manufacturer);
            cmd.Parameters.AddWithValue("@tg", table.Tags ?? "");
            cmd.Parameters.AddWithValue("@c", createdAt);
            cmd.Parameters.AddWithValue("@u", updatedAt);
            cmd.Parameters.AddWithValue("@del", table.DeletedAt);
            cmd.Parameters.AddWithValue("@sy", syncId);
        });

        table.SyncId = syncId;
        table.CreatedAt = createdAt;
        table.UpdatedAt = updatedAt;
        table.Id = Convert.ToInt32(ExecuteScalar("SELECT last_insert_rowid()"));
        return table.Id.Value;
    }

    /// <summary>Правка «шапки» документа — название и производитель. <paramref name="updatedAt"/>
    /// задаётся только импортом конфига: там нужна ЧУЖАЯ отметка, иначе применённая правка коллеги
    /// выглядела бы как наша собственная и поехала бы обратно как более свежая.</summary>
    public void UpdateParamTable(int id, string name, string manufacturer, string? updatedAt = null) =>
        ExecuteNonQuery("UPDATE param_tables SET name=@n, manufacturer=@m, updated_at=@u WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@n", name ?? "");
            cmd.Parameters.AddWithValue("@m", manufacturer ?? "");
            cmd.Parameters.AddWithValue("@u", string.IsNullOrEmpty(updatedAt) ? NowIsoPrecise() : updatedAt);
            cmd.Parameters.AddWithValue("@id", id);
        });

    /// <summary>Теги документа — по ним он находится поиском. Отдельным методом, а не полем в
    /// UpdateParamTable: теги правят из своего окна (EditParamTagsDialog), и тащить туда название с
    /// производителем незачем. <paramref name="updatedAt"/> задаётся только импортом конфига — там
    /// нужна ЧУЖАЯ отметка, иначе применённая правка коллеги поехала бы обратно как более свежая.</summary>
    public void UpdateParamTableTags(int id, string tags, string? updatedAt = null) =>
        ExecuteNonQuery("UPDATE param_tables SET tags=@t, updated_at=@u WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@t", tags ?? "");
            cmd.Parameters.AddWithValue("@u", string.IsNullOrEmpty(updatedAt) ? NowIsoPrecise() : updatedAt);
            cmd.Parameters.AddWithValue("@id", id);
        });

    /// <summary>Путь к файлу параметров у документа — обновляется, когда файл перезалили с машины с
    /// другой буквой диска (как UpdateParamFileUpload делает для самой записи файла).</summary>
    public void UpdateParamTableFile(int id, string diskPath, string filename) =>
        ExecuteNonQuery("UPDATE param_tables SET disk_path=@d, filename=@f, updated_at=@u WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@d", diskPath ?? "");
            cmd.Parameters.AddWithValue("@f", filename ?? "");
            cmd.Parameters.AddWithValue("@u", NowIsoPrecise());
            cmd.Parameters.AddWithValue("@id", id);
        });

    /// <summary>Снятие документа — отметкой, а не DELETE: строка обязана продолжать ездить по машинам
    /// как положительный сигнал «это удалили». Ревизии и строки остаются на месте: документ можно
    /// будет прочитать, если удаление окажется ошибкой.</summary>
    public void TombstoneParamTable(int id, string? deletedAt = null)
    {
        var stamp = string.IsNullOrEmpty(deletedAt) ? NowIsoPrecise() : deletedAt;
        ExecuteNonQuery("UPDATE param_tables SET deleted_at=@d, updated_at=@d WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@d", stamp);
            cmd.Parameters.AddWithValue("@id", id);
        });
    }

    // ── Произвольные столбцы документа ────────────────────────────────────────

    private const string ColumnFields = "id, table_id, col_key, title, sort_order, updated_at, deleted_at";

    private static ParamTableColumn ReadColumn(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = GetInt(r, "id"),
        TableId = GetInt(r, "table_id"),
        Key = GetString(r, "col_key"),
        Title = GetString(r, "title"),
        SortOrder = GetInt(r, "sort_order"),
        UpdatedAt = GetString(r, "updated_at"),
        DeletedAt = GetString(r, "deleted_at"),
    };

    /// <summary>Живые свои столбцы документа в порядке показа.</summary>
    public List<ParamTableColumn> GetParamTableColumns(int tableId)
    {
        var result = new List<ParamTableColumn>();
        using var reader = ExecuteReader(
            $"SELECT {ColumnFields} FROM param_table_columns WHERE table_id=@t AND deleted_at='' ORDER BY sort_order, id",
            cmd => cmd.Parameters.AddWithValue("@t", tableId));
        while (reader.Read())
            result.Add(ReadColumn(reader));
        return result;
    }

    /// <summary>Столбцы ВМЕСТЕ СО СНЯТЫМИ. Нужны двоим: выгрузке конфига (без тумбстоуна снятие не
    /// доедет до коллег) и показу старой ревизии — по снятому столбцу в ней осталось содержимое, и
    /// без его заголовка колонка называлась бы служебным ключом.</summary>
    public List<ParamTableColumn> AllParamTableColumnsIncludingDeleted(int tableId)
    {
        var result = new List<ParamTableColumn>();
        using var reader = ExecuteReader(
            $"SELECT {ColumnFields} FROM param_table_columns WHERE table_id=@t ORDER BY sort_order, id",
            cmd => cmd.Parameters.AddWithValue("@t", tableId));
        while (reader.Read())
            result.Add(ReadColumn(reader));
        return result;
    }

    /// <summary>Столбец по ключу, включая снятый, — ключ и есть его межмашинная опознавалка.
    /// Сравнение с игнором регистра идёт в .NET: у SQLite COLLATE NOCASE сворачивает только
    /// латиницу, а заголовки здесь кириллические (см. CLAUDE.md).</summary>
    public ParamTableColumn? FindParamTableColumn(int tableId, string? key) =>
        AllParamTableColumnsIncludingDeleted(tableId)
            .FirstOrDefault(c => string.Equals(c.Key, (key ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Завести свой столбец. Ключом становится сам заголовок — так две машины, независимо
    /// заведшие «Диапазон», получают ОДИН столбец, а не два одинаковых с виду (см.
    /// ParamTableColumn.Key). Повтор ключа молча возвращает уже заведённый; СНЯТЫЙ столбец с тем же
    /// ключом при этом воскресает — «завёл снова тот же столбец» и значит ровно это, а завести
    /// рядом второй с тем же ключом нельзя: содержимое в extra у них было бы общим.</summary>
    public int AddParamTableColumn(int tableId, string title, string? key = null, string? updatedAt = null)
    {
        title = (title ?? "").Trim();
        var wantedKey = string.IsNullOrWhiteSpace(key) ? title : key.Trim();
        if (title.Length == 0 || wantedKey.Length == 0) return -1;

        var stamp = string.IsNullOrEmpty(updatedAt) ? NowIsoPrecise() : updatedAt;
        var existing = FindParamTableColumn(tableId, wantedKey);
        if (existing?.Id is int existingId)
        {
            if (existing.DeletedAt.Length > 0)
                ExecuteNonQuery("UPDATE param_table_columns SET deleted_at='', title=@n, updated_at=@u WHERE id=@id", cmd =>
                {
                    cmd.Parameters.AddWithValue("@n", title);
                    cmd.Parameters.AddWithValue("@u", stamp);
                    cmd.Parameters.AddWithValue("@id", existingId);
                });
            return existingId;
        }

        var order = Convert.ToInt32(ExecuteScalar(
            "SELECT COALESCE(MAX(sort_order), 0) + 1 FROM param_table_columns WHERE table_id=@t",
            cmd => cmd.Parameters.AddWithValue("@t", tableId)) ?? 1);

        ExecuteNonQuery("""
            INSERT INTO param_table_columns(table_id, col_key, title, sort_order, updated_at, deleted_at)
            VALUES(@t, @k, @n, @s, @u, '')
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@t", tableId);
            cmd.Parameters.AddWithValue("@k", wantedKey);
            cmd.Parameters.AddWithValue("@n", title);
            cmd.Parameters.AddWithValue("@s", order);
            cmd.Parameters.AddWithValue("@u", stamp);
        });
        return Convert.ToInt32(ExecuteScalar("SELECT last_insert_rowid()"));
    }

    /// <summary>Переименовать столбец и/или переставить его. Ключ НЕ трогается — на нём держится
    /// содержимое в уже сохранённых ревизиях.</summary>
    public void UpdateParamTableColumn(int columnId, string title, int sortOrder, string? updatedAt = null) =>
        ExecuteNonQuery("UPDATE param_table_columns SET title=@n, sort_order=@s, updated_at=@u WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@n", (title ?? "").Trim());
            cmd.Parameters.AddWithValue("@s", sortOrder);
            cmd.Parameters.AddWithValue("@u", string.IsNullOrEmpty(updatedAt) ? NowIsoPrecise() : updatedAt);
            cmd.Parameters.AddWithValue("@id", columnId);
        });

    /// <summary>Убрать свой столбец — ОТМЕТКОЙ, а не DELETE. Строка обязана продолжать ездить по
    /// машинам как положительный сигнал «этот столбец убрали»: без неё он воскресал бы с первым же
    /// снимком с машины, которая об удалении ещё не знает. Содержимое в уже сохранённых ревизиях
    /// (ParamTableRow.Extra) не вычищается: ревизия — снимок, переписывать её задним числом нельзя.</summary>
    public void TombstoneParamTableColumn(int columnId, string? deletedAt = null)
    {
        var stamp = string.IsNullOrEmpty(deletedAt) ? NowIsoPrecise() : deletedAt;
        ExecuteNonQuery("UPDATE param_table_columns SET deleted_at=@d, updated_at=@d WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@d", stamp);
            cmd.Parameters.AddWithValue("@id", columnId);
        });
    }

    // ── Ревизии ───────────────────────────────────────────────────────────────

    private const string RevisionColumns =
        "id, table_id, number, reason, summary, author, created_at, deleted_at, sync_id, updated_at";

    private static ParamTableRevision ReadRevision(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = GetInt(r, "id"),
        TableId = GetInt(r, "table_id"),
        Number = GetInt(r, "number"),
        Reason = GetString(r, "reason"),
        Summary = GetString(r, "summary"),
        Author = GetString(r, "author"),
        CreatedAt = GetString(r, "created_at"),
        DeletedAt = GetString(r, "deleted_at"),
        SyncId = GetString(r, "sync_id"),
        UpdatedAt = GetString(r, "updated_at"),
    };

    /// <summary>Живые ревизии документа по УБЫВАНИЮ ХРАНИМОГО НОМЕРА. Строки не тянутся: список
    /// ревизий их не показывает, а строк в каждой сотни.
    ///
    /// ⚠️ <b>Показу человеку не годится.</b> Хранимый номер присвоила заведшая машина, между
    /// машинами он не уникален, и после обмена конфигом этот порядок ни истории, ни свежести не
    /// отражает. Окну документа нужен ParamTableNumbering.LiveRevisions — он раскладывает ревизии
    /// по времени заведения и проставляет номера показа.</summary>
    public List<ParamTableRevision> GetParamTableRevisions(int tableId)
    {
        var result = new List<ParamTableRevision>();
        using var reader = ExecuteReader(
            $"SELECT {RevisionColumns} FROM param_table_revisions WHERE table_id=@t AND deleted_at='' ORDER BY number DESC, id DESC",
            cmd => cmd.Parameters.AddWithValue("@t", tableId));
        while (reader.Read())
            result.Add(ReadRevision(reader));
        return result;
    }

    public ParamTableRevision? GetParamTableRevision(int revisionId)
    {
        using var reader = ExecuteReader($"SELECT {RevisionColumns} FROM param_table_revisions WHERE id=@id",
            cmd => cmd.Parameters.AddWithValue("@id", revisionId));
        return reader.Read() ? ReadRevision(reader) : null;
    }

    internal ParamTableRevision? FindParamTableRevisionBySyncId(string? syncId)
    {
        if (string.IsNullOrWhiteSpace(syncId)) return null;
        using var reader = ExecuteReader($"SELECT {RevisionColumns} FROM param_table_revisions WHERE sync_id=@sy",
            cmd => cmd.Parameters.AddWithValue("@sy", syncId));
        return reader.Read() ? ReadRevision(reader) : null;
    }

    /// <summary>Следующий свободный номер ревизии НА ЭТОЙ МАШИНЕ. От максимального, включая снятые:
    /// переиспользовать номер удалённой значит завести двух разных «третьих» хотя бы у себя.
    ///
    /// ⚠️ Уникален он только здесь: соседняя машина, не видевшая наших правок, выдаст ровно такой
    /// же. Человеку показывается не он, а место ревизии в истории — см. ParamTableNumbering.</summary>
    public int NextParamTableRevisionNumber(int tableId) =>
        Convert.ToInt32(ExecuteScalar("SELECT COALESCE(MAX(number), 0) + 1 FROM param_table_revisions WHERE table_id=@t",
            cmd => cmd.Parameters.AddWithValue("@t", tableId)) ?? 1);

    /// <summary>Строки ревизии — В ПОРЯДКЕ ИСХОДНИКА (sort_order). По группам их раскладывает показ,
    /// у которого под рукой порядок справочника; хранение об этом порядке не знает намеренно, иначе
    /// переставленная в справочнике группа переписывала бы уже сохранённые ревизии.</summary>
    public List<ParamTableRow> GetParamTableRows(int revisionId)
    {
        var result = new List<ParamTableRow>();
        using var reader = ExecuteReader("""
            SELECT id, revision_id, sort_order, kind, group_name, code, title, value, value_state,
                   factory, unit, description, applicability, applies_when, extra
            FROM param_table_rows WHERE revision_id=@r ORDER BY sort_order, id
            """, cmd => cmd.Parameters.AddWithValue("@r", revisionId));
        while (reader.Read())
            result.Add(new ParamTableRow
            {
                Id = GetInt(reader, "id"),
                RevisionId = GetInt(reader, "revision_id"),
                SortOrder = GetInt(reader, "sort_order"),
                Kind = GetString(reader, "kind"),
                GroupName = GetString(reader, "group_name"),
                Code = GetString(reader, "code"),
                Title = GetString(reader, "title"),
                Value = GetString(reader, "value"),
                ValueState = GetString(reader, "value_state"),
                Factory = GetString(reader, "factory"),
                Unit = GetString(reader, "unit"),
                Description = GetString(reader, "description"),
                Applicability = GetString(reader, "applicability"),
                AppliesWhen = GetString(reader, "applies_when"),
                Extra = GetString(reader, "extra"),
            });
        return result;
    }

    /// <summary>Заводит ревизию вместе с её строками — ОДНОЙ транзакцией. Полуприехавшая ревизия
    /// (строка в param_table_revisions без строк в param_table_rows) выглядела бы как «в этой
    /// редакции стёрли всё», и разбор изменений честно бы это и показал.</summary>
    public int AddParamTableRevision(ParamTableRevision revision)
    {
        var syncId = string.IsNullOrEmpty(revision.SyncId) ? Guid.NewGuid().ToString() : revision.SyncId;
        var createdAt = string.IsNullOrEmpty(revision.CreatedAt) ? NowIso() : revision.CreatedAt;
        var updatedAt = string.IsNullOrEmpty(revision.UpdatedAt) ? NowIsoPrecise() : revision.UpdatedAt;

        // Транзакция ЯВНЫМ SQL, а не _conn.BeginTransaction(). Не из любви к сырым командам:
        // Microsoft.Data.Sqlite сверяет Transaction у команды с транзакцией соединения и бросает
        // «TransactionRequired» на первой же команде, созданной мимо неё, — а мимо неё создаются
        // ВСЕ команды этого класса (см. ExecuteNonQuery в Database.Helpers.cs). Переделывать общие
        // помощники ради одного метода — правка куда крупнее этих трёх строк.
        int id;
        Exec("BEGIN");
        try
        {
            ExecuteNonQuery("""
                INSERT INTO param_table_revisions
                    (table_id, number, reason, summary, author, created_at, deleted_at, sync_id, updated_at)
                VALUES(@t, @n, @r, @s, @a, @c, @del, @sy, @u)
                """, cmd =>
            {
                cmd.Parameters.AddWithValue("@t", revision.TableId);
                cmd.Parameters.AddWithValue("@n", revision.Number);
                cmd.Parameters.AddWithValue("@r", revision.Reason);
                cmd.Parameters.AddWithValue("@s", revision.Summary);
                cmd.Parameters.AddWithValue("@a", revision.Author);
                cmd.Parameters.AddWithValue("@c", createdAt);
                cmd.Parameters.AddWithValue("@del", revision.DeletedAt);
                cmd.Parameters.AddWithValue("@sy", syncId);
                cmd.Parameters.AddWithValue("@u", updatedAt);
            });

            id = Convert.ToInt32(ExecuteScalar("SELECT last_insert_rowid()"));

            var order = 0;
            foreach (var row in revision.Rows)
            {
                var current = row;
                var currentOrder = order++;
                ExecuteNonQuery("""
                    INSERT INTO param_table_rows
                        (revision_id, sort_order, kind, group_name, code, title, value, value_state,
                         factory, unit, description, applicability, applies_when, extra)
                    VALUES(@r, @so, @k, @g, @c, @t, @v, @vs, @f, @un, @d, @ap, @aw, @e)
                    """, cmd =>
                {
                    cmd.Parameters.AddWithValue("@r", id);
                    cmd.Parameters.AddWithValue("@so", currentOrder);
                    cmd.Parameters.AddWithValue("@k", string.IsNullOrEmpty(current.Kind) ? ParamRowKind.Param : current.Kind);
                    cmd.Parameters.AddWithValue("@g", current.GroupName);
                    cmd.Parameters.AddWithValue("@c", current.Code);
                    cmd.Parameters.AddWithValue("@t", current.Title);
                    cmd.Parameters.AddWithValue("@v", current.Value);
                    cmd.Parameters.AddWithValue("@vs", ParamValueState.Normalize(current.ValueState));
                    cmd.Parameters.AddWithValue("@f", current.Factory);
                    cmd.Parameters.AddWithValue("@un", current.Unit);
                    cmd.Parameters.AddWithValue("@d", current.Description);
                    cmd.Parameters.AddWithValue("@ap", current.Applicability);
                    cmd.Parameters.AddWithValue("@aw", current.AppliesWhen);
                    cmd.Parameters.AddWithValue("@e", current.Extra);
                });
            }

            Exec("COMMIT");
        }
        catch
        {
            Exec("ROLLBACK");
            throw;
        }

        revision.Id = id;
        revision.SyncId = syncId;
        revision.CreatedAt = createdAt;
        revision.UpdatedAt = updatedAt;
        return id;
    }

    /// <summary>Правка «зачем» у уже сохранённой ревизии. Правится ТОЛЬКО это поле: сами строки —
    /// снимок, и менять их задним числом значит рассказывать про прошлое неправду.</summary>
    public void UpdateParamTableRevisionReason(int revisionId, string reason, string? updatedAt = null) =>
        ExecuteNonQuery("UPDATE param_table_revisions SET reason=@r, updated_at=@u WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@r", reason ?? "");
            cmd.Parameters.AddWithValue("@u", string.IsNullOrEmpty(updatedAt) ? NowIsoPrecise() : updatedAt);
            cmd.Parameters.AddWithValue("@id", revisionId);
        });

    public void TombstoneParamTableRevision(int revisionId, string? deletedAt = null)
    {
        var stamp = string.IsNullOrEmpty(deletedAt) ? NowIsoPrecise() : deletedAt;
        ExecuteNonQuery("UPDATE param_table_revisions SET deleted_at=@d, updated_at=@d WHERE id=@id", cmd =>
        {
            cmd.Parameters.AddWithValue("@d", stamp);
            cmd.Parameters.AddWithValue("@id", revisionId);
        });
    }
}
