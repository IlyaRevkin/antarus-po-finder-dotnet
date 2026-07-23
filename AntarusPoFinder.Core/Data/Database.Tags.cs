using System;
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

    /// <summary>Renames a tag everywhere it's used — the tags table entry, every fw_versions.tags
    /// AND every param_files.tags space-separated string that contains it as a whole word. The tag
    /// pool is shared between firmware and ПЧ/УПП parameter files.</summary>
    public void RenameTag(string oldName, string newName)
    {
        oldName = oldName.Trim();
        newName = newName.Trim();
        if (newName.Length == 0 || string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)) return;

        ExecuteNonQuery("UPDATE tags SET name = @n WHERE name = @o COLLATE NOCASE",
            cmd => { cmd.Parameters.AddWithValue("@n", newName); cmd.Parameters.AddWithValue("@o", oldName); });
        // Переименование = старого больше нет, новый появился — обе отметки нужны, иначе импорт с
        // машины, ещё не знающей о переименовании, вернёт старое имя обратно.
        MarkFlatListDeleted(FlatKindTag, oldName);
        MarkFlatListAlive(FlatKindTag, newName);
        ReplaceTagInColumn("fw_versions", oldName, newName);
        ReplaceTagInColumn("param_files", oldName, newName);
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
        var updates = new List<(int Id, string Tags)>();
        using (var reader = ExecuteReader($"SELECT id, tags FROM {table} WHERE tags IS NOT NULL AND tags != ''"))
        {
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                // По целым тегам, а не по словам строки: тег «шкаф управления пожарными насосами»
                // — это ОДИН тег, и переименование/удаление обязано трогать его целиком (см. TagList).
                var words = Services.TagString.Parse(reader.GetString(1));
                if (!words.Any(w => w.Equals(oldName, StringComparison.OrdinalIgnoreCase))) continue;

                var newWords = newName is null
                    ? words.Where(w => !w.Equals(oldName, StringComparison.OrdinalIgnoreCase))
                    : words.Select(w => w.Equals(oldName, StringComparison.OrdinalIgnoreCase) ? newName : w);
                updates.Add((id, Services.TagString.Join(newWords)));
            }
        }

        foreach (var (id, tags) in updates)
            ExecuteNonQuery($"UPDATE {table} SET tags = @t WHERE id = @id",
                cmd => { cmd.Parameters.AddWithValue("@t", tags); cmd.Parameters.AddWithValue("@id", id); });
    }
}
