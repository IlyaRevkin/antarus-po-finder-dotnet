using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    private static string NowIso() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private int ExecuteNonQuery(string sql, Action<SqliteCommand>? bind = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        return cmd.ExecuteNonQuery();
    }

    private object? ExecuteScalar(string sql, Action<SqliteCommand>? bind = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        return cmd.ExecuteScalar();
    }

    private SqliteDataReader ExecuteReader(string sql, Action<SqliteCommand>? bind = null)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        return cmd.ExecuteReader();
    }

    private List<int> QueryInts(string sql)
    {
        var result = new List<int>();
        using var reader = ExecuteReader(sql);
        while (reader.Read())
            result.Add(reader.GetInt32(0));
        return result;
    }

    /// <summary>Executes SQL whose only parameters are literal '?' placeholders (e.g. an IN(...) clause) bound positionally.</summary>
    private void ExecWithIntParams(string sql, List<int> ids)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var id in ids)
            cmd.Parameters.Add(new SqliteParameter { Value = id });
        cmd.ExecuteNonQuery();
    }

    private static string? GetStringOrNull(SqliteDataReader r, string col)
    {
        int idx = r.GetOrdinal(col);
        return r.IsDBNull(idx) ? null : r.GetString(idx);
    }

    private static string GetString(SqliteDataReader r, string col, string fallback = "")
    {
        int idx = r.GetOrdinal(col);
        return r.IsDBNull(idx) ? fallback : r.GetString(idx);
    }

    private static int GetInt(SqliteDataReader r, string col, int fallback = 0)
    {
        int idx = r.GetOrdinal(col);
        return r.IsDBNull(idx) ? fallback : r.GetInt32(idx);
    }

    private static int? GetIntOrNull(SqliteDataReader r, string col)
    {
        int idx = r.GetOrdinal(col);
        return r.IsDBNull(idx) ? null : r.GetInt32(idx);
    }

    private static bool GetBool(SqliteDataReader r, string col)
    {
        int idx = r.GetOrdinal(col);
        return !r.IsDBNull(idx) && r.GetInt32(idx) != 0;
    }
}
