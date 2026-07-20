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

    /// <summary>Builds a "@p0,@p1,..." placeholder list for an IN(...) clause — pair with
    /// ExecWithIntParams, which binds the same names. Microsoft.Data.Sqlite has no positional-'?'
    /// binding (an unnamed SqliteParameter throws "ParameterName must be set" the moment the
    /// command actually runs), so every placeholder needs an explicit matching name.</summary>
    private static string IntParamPlaceholders(List<int> ids)
    {
        var names = new string[ids.Count];
        for (int i = 0; i < ids.Count; i++)
            names[i] = $"@p{i}";
        return string.Join(",", names);
    }

    /// <summary>Executes SQL built with IntParamPlaceholders, binding @p0,@p1,... to ids in order.</summary>
    private void ExecWithIntParams(string sql, List<int> ids)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"@p{i}", ids[i]);
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
