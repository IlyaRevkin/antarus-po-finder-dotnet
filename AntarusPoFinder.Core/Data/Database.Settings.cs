using System.Collections.Generic;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    public string GetSetting(string key, string fallback = "")
    {
        var result = ExecuteScalar("SELECT value FROM settings WHERE key=@k", cmd => cmd.Parameters.AddWithValue("@k", key));
        return result as string ?? fallback;
    }

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
}
