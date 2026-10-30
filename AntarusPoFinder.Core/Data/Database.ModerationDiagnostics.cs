using System.Collections.Generic;

namespace AntarusPoFinder.Core.Data;

/// <summary>Срезы очереди модерации — чтобы понять, почему у одного человека в ней 2–3 версии, а у
/// другого 20+ (см. Core/Services/ModerationDiagnostics).
///
/// Главное решение здесь: снимается НЕ одна итоговая цифра, а одно и то же число, посчитанное на
/// разных уровнях отбора, плюс сами номера версий. Причина в том, что жалоба допускает два
/// совершенно разных объяснения, и по одной цифре их не различить:
///
/// <list type="number">
/// <item><description>на одной машине расходятся счётчик и список — тогда виновато условие отбора,
/// и видно это по тому, на каком уровне срез меняет значение;</description></item>
/// <item><description>на разных машинах РАЗНЫЕ НАБОРЫ версий — тогда дело не в счёте вовсе, а в
/// том, что отметка «выведена из модерации» не доехала, и видно это по номерам версий, которых у
/// соседа нет.</description></item>
/// </list>
///
/// Поэтому сюда попадают и срезы, и номера. Имён файлов, описаний и путей здесь нет: номер версии
/// и так виден всем, кто работает с программой, а остальное к расхождению счётчика отношения не
/// имеет.</summary>
public partial class Database
{
    /// <summary>Сколько строк остаётся на каждом уровне отбора. Ключи — по-русски и по порядку
    /// сужения: читать это будет человек, а не программа.</summary>
    public Dictionary<string, int> ModerationSlices()
    {
        var slices = new Dictionary<string, int>
        {
            ["всего версий"] = Count("SELECT COUNT(*) FROM fw_versions"),
            ["не выпущено"] = Count("SELECT COUNT(*) FROM fw_versions WHERE released = 0"),
            ["+ не в архиве"] = Count("SELECT COUNT(*) FROM fw_versions WHERE released = 0 AND archived = 0"),
            ["+ не откатано и не выключено"] = Count("""
                SELECT COUNT(*) FROM fw_versions
                WHERE released = 0 AND archived = 0 AND (status IS NULL OR status = 'active')
                """),
            ["+ не удалено"] = Count($"""
                SELECT COUNT(*) FROM fw_versions fv
                WHERE fv.released = 0 AND fv.archived = 0 AND (fv.status IS NULL OR fv.status = 'active')
                  AND {NotDeleted("fv")}
                """),
            ["+ не конфигурация"] = Count($"""
                SELECT COUNT(*) FROM fw_versions fv
                WHERE fv.released = 0 AND fv.archived = 0 AND (fv.status IS NULL OR fv.status = 'active')
                  AND {NotDeleted("fv")} AND {NotConfig("fv")}
                """),
            ["+ не заменена свежей"] = Count($"""
                SELECT COUNT(*) FROM fw_versions fv
                WHERE fv.released = 0 AND fv.archived = 0 AND (fv.status IS NULL OR fv.status = 'active')
                  AND {NotDeleted("fv")} AND {NotConfig("fv")} AND {NotSuperseded("fv")}
                """),
            ["итог (как в счётчике и списке)"] = GetUnreleasedFwVersionsCount(),
        };

        // Разница между предпоследним и последним — это строки, потерянные на JOIN'ах справочника
        // (подтип, группа, контроллер). Их не должно быть при включённых внешних ключах, и если они
        // вдруг появятся, это и будет ответом.
        slices["потеряно на справочнике"] =
            slices["+ не заменена свежей"] - slices["итог (как в счётчике и списке)"];

        return slices;
    }

    /// <summary>Номера версий, стоящих в очереди модерации. Нужны, чтобы сравнить ДВЕ машины: если
    /// наборы разные, дело не в счёте, а в том, что отметка «выведена из модерации» не доехала.</summary>
    public List<string> ModerationVersionNumbers()
    {
        var result = new List<string>();
        using var reader = ExecuteReader($"""
            SELECT fv.version_raw
            {UnreleasedFromWhere()}
            ORDER BY fv.version_raw
            """);
        while (reader.Read()) result.Add(GetString(reader, "version_raw"));
        return result;
    }

    private int Count(string sql) => ExecuteScalar(sql) is long l ? (int)l : 0;
}
