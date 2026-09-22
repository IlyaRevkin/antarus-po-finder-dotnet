using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Data;

public partial class Database
{
    public const string FlatKindExecution = "fw_execution";

    /// <summary>Справочник исполнений — заведённых заранее, а не выведенных из уже загруженного.
    ///
    /// Раньше список исполнений собирался из самих прошивок (GetFwExecutions), и получался замкнутый
    /// круг: чтобы исполнение появилось в списке, его надо было сперва кому-то вписать руками при
    /// загрузке, а вписать было негде — «не могу туда добавлять пункты». Теперь набор ведётся
    /// отдельно, как теги и виды доп. материалов, и заполняется до первой загрузки.
    ///
    /// Регистр сворачивается в .NET, а не в SQL: SQLite COLLATE NOCASE кириллицу не трогает, и
    /// «3 насоса» с «3 Насоса» стали бы двумя строками справочника — а исполнения сравниваются
    /// ТОЧНО (см. FwExecution), то есть это были бы две разные линейки.</summary>
    public List<string> GetExecutionCatalog()
    {
        var result = new List<string>();
        using var reader = ExecuteReader("SELECT name FROM fw_executions ORDER BY sort_order, name");
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public void AddExecutionToCatalog(string name)
    {
        name = FwExecution.Normalize(name);
        if (name.Length == 0) return;

        var existing = GetExecutionCatalog().FirstOrDefault(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            MarkFlatListAlive(FlatKindExecution, existing);
            return;
        }

        var order = Convert.ToInt32(ExecuteScalar("SELECT COALESCE(MAX(sort_order), 0) + 1 FROM fw_executions") ?? 1);
        ExecuteNonQuery("INSERT OR IGNORE INTO fw_executions(name, sort_order) VALUES(@n, @s)", cmd =>
        {
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@s", order);
        });
        MarkFlatListAlive(FlatKindExecution, name);
    }

    /// <summary>Убрать исполнение из справочника. Уже загруженные прошивки своё исполнение
    /// СОХРАНЯЮТ — как и вид доп. материала при чистке справочника: пометка описывает линейку, и
    /// потерять её из-за уборки списка значило бы слить две линейки в одну.</summary>
    public void DeleteExecutionFromCatalog(string name)
    {
        name = FwExecution.Normalize(name);
        if (name.Length == 0) return;
        ExecuteNonQuery("DELETE FROM fw_executions WHERE name = @n COLLATE NOCASE",
            cmd => cmd.Parameters.AddWithValue("@n", name));
        MarkFlatListDeleted(FlatKindExecution, name);
    }

    /// <summary>Что предлагать в поле «Исполнение»: справочник плюс то, что уже реально встречается
    /// у этого шкафа. Второе — чтобы прошивки, размеченные до появления справочника, не выпадали из
    /// списка и их линейку можно было продолжить, не заводя пометку заново.</summary>
    public List<string> GetExecutionChoices(int subtypeId, int controllerId) =>
        GetExecutionCatalog()
            .Concat(GetFwExecutions(subtypeId, controllerId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
