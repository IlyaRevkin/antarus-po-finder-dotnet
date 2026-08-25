using System.Collections.Generic;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    /// <summary>Пути к файлам, как они лежат в базе, — сгруппированные, с числом записей на каждый.
    /// Нужны ровно одной проверке: «сколько записей ссылается на диск, которого на этой машине нет»
    /// (см. <see cref="StoredPathAudit"/>).
    ///
    /// Группами, а не строками: у тысячи прошивок пара сотен уникальных папок, и в отчёт человеку
    /// идут именно папки. Удалённые и архивные записи не считаются — они и так никуда не открываются,
    /// а в отчёте раздували бы цифру и уводили от настоящей поломки.</summary>
    public List<StoredPathGroup> GetStoredDiskPathGroups()
    {
        var result = new List<StoredPathGroup>();
        Collect("""
            SELECT disk_path, COUNT(*) FROM fw_versions
            WHERE disk_path <> '' AND deleted_at = '' AND archived = 0
            GROUP BY disk_path
            """);
        Collect("""
            SELECT disk_path, COUNT(*) FROM param_files
            WHERE disk_path <> '' AND archived = 0
            GROUP BY disk_path
            """);
        return result;

        void Collect(string sql)
        {
            using var r = ExecuteReader(sql);
            while (r.Read())
                result.Add(new StoredPathGroup(r.GetString(0), r.GetInt32(1)));
        }
    }
}
