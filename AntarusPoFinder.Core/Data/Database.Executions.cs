using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Data;

/// <summary>Строка справочника исполнений для таблицы: как называется, в каком порядке, включено ли
/// и когда заведено. Пустая <paramref name="CreatedAt"/> — «заведено до того, как дату начали
/// записывать», и показывается прочерком, а не сегодняшним числом.</summary>
public record FwExecutionRow(string Name, int SortOrder, bool Active, string CreatedAt);

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
    public List<string> GetExecutionCatalog(bool activeOnly = false)
    {
        var result = new List<string>();
        var where = activeOnly ? "WHERE active = 1" : "";
        using var reader = ExecuteReader($"SELECT name FROM fw_executions {where} ORDER BY sort_order, name");
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Справочник исполнений целиком, строками — для таблицы в иерархии. Просьба Ильи:
    /// «добавь возможность редактировать исполнения, также делать из активными или нет дату
    /// создания исполнения и та и отображение таблицей уже удобней будет».</summary>
    public List<FwExecutionRow> GetExecutionRows()
    {
        var result = new List<FwExecutionRow>();
        using var reader = ExecuteReader(
            "SELECT name, sort_order, active, created_at FROM fw_executions ORDER BY sort_order, name");
        while (reader.Read())
            result.Add(new FwExecutionRow(
                reader.GetString(0), reader.GetInt32(1),
                reader.GetInt32(2) != 0, GetString(reader, "created_at")));
        return result;
    }

    /// <summary>Переименовать исполнение В СПРАВОЧНИКЕ И У ПРОШИВОК СРАЗУ.
    ///
    /// Иначе переименование разорвало бы линейку надвое: исполнения сравниваются ТОЧНО, строка в
    /// строку (см. FwExecution), и прошивки со старым написанием мгновенно стали бы отдельной
    /// линейкой — то есть перестали бы заменять друг друга и повисли бы в модерации все разом.
    /// Поэтому это одна операция, а не «удалить и завести заново».
    ///
    /// Возвращает false, если новое имя пустое или такое исполнение уже есть: склеивать две линейки
    /// переименованием нельзя — это молча перепутало бы прошивки разных исполнений.</summary>
    public bool RenameExecution(string oldName, string newName)
    {
        oldName = FwExecution.Normalize(oldName);
        newName = FwExecution.Normalize(newName);
        if (oldName.Length == 0 || newName.Length == 0) return false;
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return true;

        var catalog = GetExecutionCatalog();
        if (catalog.Any(x => string.Equals(x, newName, StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(x, oldName, StringComparison.OrdinalIgnoreCase)))
            return false;

        ExecuteNonQuery("UPDATE fw_executions SET name = @new WHERE name = @old COLLATE NOCASE", cmd =>
        {
            cmd.Parameters.AddWithValue("@new", newName);
            cmd.Parameters.AddWithValue("@old", oldName);
        });
        ExecuteNonQuery("UPDATE fw_versions SET execution = @new WHERE execution = @old", cmd =>
        {
            cmd.Parameters.AddWithValue("@new", newName);
            cmd.Parameters.AddWithValue("@old", oldName);
        });
        MarkFlatListDeleted(FlatKindExecution, oldName);
        MarkFlatListAlive(FlatKindExecution, newName);
        return true;
    }

    /// <summary>Включить/выключить исполнение. Выключенное не предлагается при загрузке, но у уже
    /// загруженных прошивок остаётся — тем и отличается от удаления: линейку закрыли, а история по
    /// ней читается.</summary>
    public void SetExecutionActive(string name, bool active)
    {
        name = FwExecution.Normalize(name);
        if (name.Length == 0) return;
        ExecuteNonQuery("UPDATE fw_executions SET active = @a WHERE name = @n COLLATE NOCASE", cmd =>
        {
            cmd.Parameters.AddWithValue("@a", active ? 1 : 0);
            cmd.Parameters.AddWithValue("@n", name);
        });
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
        ExecuteNonQuery("INSERT OR IGNORE INTO fw_executions(name, sort_order, active, created_at) VALUES(@n, @s, 1, @c)", cmd =>
        {
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@s", order);
            // Дата заведения записывается здесь и больше не меняется никогда — это «когда завели», а
            // не «когда правили». У строк, заведённых до появления столбца, она пустая, и таблица
            // честно показывает прочерк вместо выдуманного числа.
            cmd.Parameters.AddWithValue("@c", DateTime.Now.ToString("yyyy-MM-dd"));
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
        // activeOnly: выключенную линейку не предлагаем заводить заново — ради этого выключатель и
        // нужен. Уже размеченные прошивки подтягиваются вторым слагаемым, поэтому продолжить
        // существующую линейку по-прежнему можно.
        GetExecutionCatalog(activeOnly: true)
            .Concat(GetFwExecutions(subtypeId, controllerId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
