using System.Collections.Generic;
using System.Linq;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    public List<string> GetAllTags()
    {
        var result = new List<string>();
        using var reader = ExecuteReader("SELECT name FROM tags ORDER BY name COLLATE NOCASE");
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Adds a tag to the shared tag list if it doesn't already exist (case-insensitive).
    /// Called both from the Settings→Теги CRUD tab and whenever a firmware/upload editor is saved
    /// with a brand-new tag word, so the tag becomes available for autocomplete elsewhere.</summary>
    public void AddTag(string name)
    {
        // Схлопывание внутренних пробелов ровно то же, что при записи в fw_versions.tags —
        // иначе «шкаф  управления» в справочнике и «шкаф управления» на прошивке считались бы
        // разными тегами (см. TagList).
        name = Services.TagString.Decode(Services.TagString.Encode(name ?? ""));
        if (name.Length == 0) return;
        ExecuteNonQuery("INSERT OR IGNORE INTO tags (name) VALUES (@n)", cmd => cmd.Parameters.AddWithValue("@n", name));
        MarkFlatListAlive(FlatKindTag, name);
    }

    /// <summary>Чем закончилось переименование тега — нужно вызывающему, чтобы сказать человеку
    /// правду. Раньше интерфейс рапортовал «переименован» даже там, где не менялось ничего.</summary>
    public enum TagRenameOutcome
    {
        /// <summary>Ничего не поменялось: пустое имя, такое же имя или тега уже нет.</summary>
        Unchanged,
        /// <summary>Обычное переименование, имя свободно.</summary>
        Renamed,
        /// <summary>Имя было занято — старый тег влит в существующий.</summary>
        Merged,
    }

    /// <summary>Итог переименования вместе с именем, которое реально получилось. При слиянии это
    /// написание УЖЕ СУЩЕСТВУЮЩЕГО тега, а не то, что набрал пользователь.</summary>
    public readonly record struct TagRenameResult(TagRenameOutcome Outcome, string Name);

    /// <summary>Renames a tag everywhere it's used — the tags table entry, every fw_versions.tags
    /// AND every param_files.tags space-separated string that contains it as a whole word. The tag
    /// pool is shared between firmware and ПЧ/УПП parameter files.
    ///
    /// Занятое имя — это не ошибка, а слияние: два тега про одно и то же становятся одним. Голый
    /// UPDATE тут падал в «UNIQUE constraint failed: tags.name» и выдавал оператору отчёт о сбое.
    ///
    /// Всё сравнение имён — в .NET и по загруженному списку, а НЕ через SQL. Причина в том, что
    /// COLLATE NOCASE у SQLite сворачивает только ASCII: для базы «ПИ» и «пи» — разные строки и
    /// обе спокойно живут в таблице, хотя для StringComparer.OrdinalIgnoreCase они равны. Из-за
    /// этого расхождения прежний код на «пи» → «ПИ» молча выходил, ничего не переименовав.</summary>
    public TagRenameResult RenameTag(string oldName, string newName)
    {
        // Та же нормализация, что в AddTag: иначе «шкаф  управления» с двумя пробелами разъедется
        // со справочником.
        oldName = Services.TagString.Decode(Services.TagString.Encode(oldName ?? ""));
        newName = Services.TagString.Decode(Services.TagString.Encode(newName ?? ""));
        if (oldName.Length == 0 || newName.Length == 0) return new TagRenameResult(TagRenameOutcome.Unchanged, oldName);
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return new TagRenameResult(TagRenameOutcome.Unchanged, oldName);

        var all = GetAllTags();
        // Приводим к тому написанию, которое реально лежит в таблице, — дальше сравниваем точно.
        var stored = all.FirstOrDefault(t => string.Equals(t, oldName, StringComparison.Ordinal))
                     ?? all.FirstOrDefault(t => string.Equals(t, oldName, StringComparison.OrdinalIgnoreCase));
        if (stored is null) return new TagRenameResult(TagRenameOutcome.Unchanged, oldName);
        oldName = stored;

        // Занято ли новое имя ДРУГИМ тегом. Сам переименовываемый тег из проверки исключён:
        // «пи» → «ПИ» при единственном «пи» — это смена регистра, а не столкновение.
        var existing = all.FirstOrDefault(t =>
            !string.Equals(t, oldName, StringComparison.Ordinal) &&
            string.Equals(t, newName, StringComparison.OrdinalIgnoreCase));

        var target = existing ?? newName;
        if (existing is not null)
            ExecuteNonQuery("DELETE FROM tags WHERE name = @o", cmd => cmd.Parameters.AddWithValue("@o", oldName));
        else
            ExecuteNonQuery("UPDATE tags SET name = @n WHERE name = @o",
                cmd => { cmd.Parameters.AddWithValue("@n", target); cmd.Parameters.AddWithValue("@o", oldName); });

        // Переименование = старого больше нет, новый появился — обе отметки нужны, иначе импорт с
        // машины, ещё не знающей о переименовании, вернёт старое имя обратно.
        MarkFlatListDeleted(FlatKindTag, oldName);
        MarkFlatListAlive(FlatKindTag, target);
        // Повтор внутри одной строки тегов схлопнет TagString.Join — строка, у которой были и
        // старый, и новый тег, не получит его дважды.
        ReplaceTagInColumn("fw_versions", oldName, target);
        ReplaceTagInColumn("param_files", oldName, target);
        return new TagRenameResult(existing is not null ? TagRenameOutcome.Merged : TagRenameOutcome.Renamed, target);
    }

    /// <summary>Deletes a tag from the shared list and strips it out of every fw_versions.tags and
    /// param_files.tags string that used it.</summary>
    public void DeleteTag(string name)
    {
        name = name.Trim();
        ExecuteNonQuery("DELETE FROM tags WHERE name = @n COLLATE NOCASE", cmd => cmd.Parameters.AddWithValue("@n", name));
        MarkFlatListDeleted(FlatKindTag, name);
        ReplaceTagInColumn("fw_versions", name, null);
        ReplaceTagInColumn("param_files", name, null);
    }

    private void ReplaceTagInColumn(string table, string oldName, string? newName)
    {
        var updates = new List<(int Id, string? SyncId, string OldTags, string Tags)>();
        using (var reader = ExecuteReader($"SELECT id, sync_id, tags FROM {table} WHERE tags IS NOT NULL AND tags != ''"))
        {
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                var syncId = reader.IsDBNull(1) ? null : reader.GetString(1);
                var raw = reader.GetString(2);
                // По целым тегам, а не по словам строки: тег «шкаф управления пожарными насосами»
                // — это ОДИН тег, и переименование/удаление обязано трогать его целиком (см. TagList).
                var words = Services.TagString.Parse(raw);
                if (!words.Any(w => w.Equals(oldName, StringComparison.OrdinalIgnoreCase))) continue;

                var newWords = newName is null
                    ? words.Where(w => !w.Equals(oldName, StringComparison.OrdinalIgnoreCase))
                    : words.Select(w => w.Equals(oldName, StringComparison.OrdinalIgnoreCase) ? newName : w);
                updates.Add((id, syncId, raw, Services.TagString.Join(newWords)));
            }
        }

        foreach (var (id, syncId, oldTags, tags) in updates)
        {
            // Удаление/переименование тега В СПРАВОЧНИКЕ вычищает его и из самих записей — а значит,
            // это такое же снятие тега со строки, как правка карточки, и без отметки оно откатилось бы
            // назад при первой же синхронизации с машиной, которая ещё держит старый набор.
            RecordRowTagChange(syncId, oldTags, tags);
            ExecuteNonQuery($"UPDATE {table} SET tags = @t WHERE id = @id",
                cmd => { cmd.Parameters.AddWithValue("@t", tags); cmd.Parameters.AddWithValue("@id", id); });
        }
    }
}
