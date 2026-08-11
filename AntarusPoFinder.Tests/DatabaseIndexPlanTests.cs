using System;
using System.Collections.Generic;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Проверяет ПЛАНОМ ВЫПОЛНЕНИЯ, а не наличием строки в sqlite_master, что запросы, которые
/// приложение делает В ЦИКЛЕ, идут по индексу (см. Database.EnsureIndexes). Именно план и важен:
/// индекс можно завести и не попасть в него из-за формы условия, а «полный перебор в цикле по всем
/// версиям» — это перебор в квадрате, то самое «программа задумалась на минуту» при перестройке
/// диска и при приёме конфига.</summary>
public class DatabaseIndexPlanTests : IDisposable
{
    private readonly TempDb _file = new();
    private readonly Database _db;
    private readonly SqliteConnection _raw;

    public DatabaseIndexPlanTests()
    {
        _db = new Database(_file.Path);
        _raw = new SqliteConnection($"Data Source={_file.Path}");
        _raw.Open();
    }

    public void Dispose()
    {
        _raw.Dispose();
        _db.Dispose();
        _file.Dispose();
    }

    private List<string> Plan(string sql)
    {
        using var cmd = _raw.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using var reader = cmd.ExecuteReader();
        var steps = new List<string>();
        while (reader.Read()) steps.Add(reader.GetString(3));
        return steps;
    }

    private void AssertUsesIndex(string sql, string index)
    {
        var steps = Plan(sql);
        Assert.Contains(steps, s => s.Contains("SEARCH") && s.Contains(index, StringComparison.Ordinal));
    }

    /// <summary>«Кто ещё ссылается на эти файлы» (IsDiskPathSharedByOtherVersions) спрашивается на
    /// каждую снимаемую тумбстоуном версию при приёме конфига, а RenameFirmwareFileRecords делает два
    /// UPDATE по тому же условию на каждую операцию перестройки/чистки диска.</summary>
    [Fact]
    public void DiskPathLookups_GoThroughAnIndex()
    {
        AssertUsesIndex("SELECT COUNT(*) FROM fw_versions WHERE disk_path='x' AND id<>1",
            "idx_fw_versions_disk_path");
        AssertUsesIndex("UPDATE fw_versions SET filename='a' WHERE disk_path='x' AND filename='b'",
            "idx_fw_versions_disk_path");
    }

    /// <summary>Проверка «можно ли удалить контроллер/модификацию, которых нет в чужом снимке» —
    /// в цикле по всем локальным контроллерам и модификациям (Database.ConfigExchange.cs).</summary>
    [Fact]
    public void ControllerLookups_GoThroughAnIndex()
    {
        AssertUsesIndex("SELECT 1 FROM fw_versions WHERE controller_id=1", "idx_fw_versions_ctrl_hw");
        AssertUsesIndex("SELECT 1 FROM fw_versions WHERE controller_id=1 AND hw_version=2",
            "idx_fw_versions_ctrl_hw");
    }

    /// <summary>Два запроса, которыми выдача поиска заменила поштучные (см.
    /// SearchCardBatchedLookupsTests): они и так по одному на выдачу, но идти полным перебором таблицы
    /// им всё равно незачем.</summary>
    [Fact]
    public void SearchCardBatchedLookups_GoThroughAnIndex()
    {
        Assert.Contains(Plan("SELECT DISTINCT subtype_id FROM param_files WHERE archived = 0 AND subtype_id IS NOT NULL"),
            s => s.Contains("idx_param_files_subtype", StringComparison.Ordinal));
        Assert.Contains(Plan("SELECT fw_version_id, COUNT(*) FROM fw_attachments WHERE deleted_at='' GROUP BY fw_version_id"),
            s => s.Contains("idx_fw_attachments_version", StringComparison.Ordinal));
    }
}
