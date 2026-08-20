namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    public string GetSetting(string key, string fallback = "")
    {
        var result = ExecuteScalar("SELECT value FROM settings WHERE key=@k", cmd => cmd.Parameters.AddWithValue("@k", key));
        return result as string ?? fallback;
    }

    /// <summary>Ключ ЕСТЬ в таблице — пусть даже с пустым значением. Нужен настройкам, у которых
    /// пустая строка сама по себе осмысленна («подписи на этикетке нет»): без этой проверки её
    /// нельзя отличить от «настройку не трогали», и стёртая подпись возвращалась бы из умолчания.</summary>
    public bool HasSetting(string key) =>
        ExecuteScalar("SELECT 1 FROM settings WHERE key=@k", cmd => cmd.Parameters.AddWithValue("@k", key)) is not null;

    /// <summary>Raw dump of the whole settings table — used by Settings→Общие config export,
    /// which needs every key/value pair, not just the typed subset ConfigService knows about.</summary>
    public Dictionary<string, string> GetAllSettings()
    {
        var result = new Dictionary<string, string>();
        using var reader = ExecuteReader("SELECT key, value FROM settings");
        while (reader.Read())
            result[GetString(reader, "key")] = GetString(reader, "value");
        return result;
    }

    public void SetSetting(string key, string value) =>
        ExecuteNonQuery(
            "INSERT INTO settings(key,value) VALUES(@k,@v) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@k", key);
                cmd.Parameters.AddWithValue("@v", value);
            });

    // ── «Это правили здесь и вот когда» ──────────────────────────────────────
    // Зачем нужно — см. таблицу settings_local_edits в Database.cs. Коротко: приём общего конфига
    // писал чужое значение поверх своего вслепую, и сохранённое оформление этикетки жило до
    // ближайшей автоматической подтяжки — то есть минуты. Отметка даёт приёму основание отличить
    // свою свежую правку от своей же устаревшей.

    /// <summary>Формат отметки — тот же, что у exported_at в общем конфиге
    /// (ConfigSyncService.PrepareExport). Строки в нём сравниваются как строки и совпадают с
    /// порядком времени, поэтому приёму достаточно обычного сравнения строк.</summary>
    public const string LocalEditStampFormat = "yyyy-MM-ddTHH:mm:ss";

    /// <summary>Отметить, что перечисленные настройки только что сохранил человек на этой машине.
    /// Вызывать ТОЛЬКО из мест, где значение задал человек: служебные записи (watermark'и
    /// синхронизации, счётчики) отмечать не надо и незачем — они и так per-machine.</summary>
    public void MarkSettingsEditedLocally(IEnumerable<string> keys, DateTime at)
    {
        var stamp = at.ToString(LocalEditStampFormat);
        foreach (var key in keys)
            ExecuteNonQuery(
                "INSERT INTO settings_local_edits(key,edited_at) VALUES(@k,@t) " +
                "ON CONFLICT(key) DO UPDATE SET edited_at=excluded.edited_at",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@k", key);
                    cmd.Parameters.AddWithValue("@t", stamp);
                });
    }

    /// <summary>Все отметки разом: приём перебирает сотни ключей, и спрашивать базу по каждому
    /// отдельно незачем.</summary>
    public Dictionary<string, string> LocalSettingEdits()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = ExecuteReader("SELECT key, edited_at FROM settings_local_edits");
        while (reader.Read())
            result[GetString(reader, "key")] = GetString(reader, "edited_at");
        return result;
    }

    /// <summary>Снять отметку — её уже перекрыл более свежий общий конфиг, и держать её дальше
    /// значило бы сравнивать с ней каждый следующий приём впустую.</summary>
    public void ClearLocalSettingEdit(string key) =>
        ExecuteNonQuery("DELETE FROM settings_local_edits WHERE key=@k",
            cmd => cmd.Parameters.AddWithValue("@k", key));
}
