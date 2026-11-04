using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Data;

/// <summary>Строка справочника исполнений ШКАФА: как называется и когда заведено. Пустая
/// <paramref name="CreatedAt"/> — «заведено до того, как дату начали записывать».</summary>
public record SubtypeExecutionRow(int SubtypeId, string Name, int SortOrder, string CreatedAt);

public partial class Database
{
    /// <summary>Вид плоского списка для синхронизации исполнений ШКАФА. Ключ строки —
    /// «&lt;группа&gt;/&lt;подтип&gt;|&lt;исполнение&gt;» (см. <see cref="SubtypeExecutionKey"/>): id подтипа
    /// на каждой машине свой, и синхронизировать его значило бы раздать чужие исполнения чужим
    /// шкафам.</summary>
    public const string FlatKindSubtypeExecution = "fw_subtype_execution";

    /// <summary>Разделитель подтипа и исполнения в ключе синхронизации. Вертикальная черта в имени
    /// шкафа и в имени исполнения невозможна: в именах папок она запрещена Windows, а исполнение
    /// именем папки и становится (VersionLayout.ExecutionFolderName).</summary>
    private const char SubtypeExecutionKeySeparator = '|';

    /// <summary>Исполнения ЭТОГО шкафа — то, из чего состоит выбор в карточке и что предлагается при
    /// загрузке. Пустой список — обычный шкаф без исполнений, всё работает как до этой задачи.
    ///
    /// Порядок заведения, а не алфавит: первым заведено самое ходовое.</summary>
    public List<string> GetSubtypeExecutions(int subtypeId)
    {
        var result = new List<string>();
        using var reader = ExecuteReader(
            "SELECT name FROM fw_subtype_executions WHERE subtype_id=@s ORDER BY sort_order, name",
            cmd => cmd.Parameters.AddWithValue("@s", subtypeId));
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Весь справочник строками — для таблицы в иерархии и для выгрузки конфига.</summary>
    public List<SubtypeExecutionRow> GetSubtypeExecutionRows(int? subtypeId = null)
    {
        var result = new List<SubtypeExecutionRow>();
        var where = subtypeId is null ? "" : "WHERE subtype_id=@s";
        using var reader = ExecuteReader(
            $"SELECT subtype_id, name, sort_order, created_at FROM fw_subtype_executions {where} " +
            "ORDER BY subtype_id, sort_order, name",
            cmd => { if (subtypeId is not null) cmd.Parameters.AddWithValue("@s", subtypeId.Value); });
        while (reader.Read())
            result.Add(new SubtypeExecutionRow(
                reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), GetString(reader, "created_at")));
        return result;
    }

    /// <summary>Завести исполнение у шкафа. Повторный вызов с тем же именем (в любом регистре)
    /// ничего не меняет и даты заведения не переписывает: «когда завели» — это когда завели.
    ///
    /// Регистр сводится в .NET, а не в SQL: SQLite COLLATE NOCASE кириллицу не сворачивает
    /// (см. CLAUDE.md), и «3 насоса» с «3 Насоса» стали бы двумя строками справочника — а на диске
    /// это ОДНА папка, то есть два пункта в выборе вели бы в одно место.</summary>
    public void AddSubtypeExecution(int subtypeId, string name)
    {
        name = FwExecution.Normalize(name);
        if (name.Length == 0) return;

        var existing = GetSubtypeExecutions(subtypeId)
            .FirstOrDefault(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            MarkFlatListAlive(FlatKindSubtypeExecution, SubtypeExecutionKey(subtypeId, existing));
            return;
        }

        var order = Convert.ToInt32(ExecuteScalar(
            "SELECT COALESCE(MAX(sort_order), 0) + 1 FROM fw_subtype_executions WHERE subtype_id=@s",
            cmd => cmd.Parameters.AddWithValue("@s", subtypeId)) ?? 1);
        ExecuteNonQuery(
            "INSERT OR IGNORE INTO fw_subtype_executions(subtype_id, name, sort_order, created_at) " +
            "VALUES(@s, @n, @o, @c)", cmd =>
            {
                cmd.Parameters.AddWithValue("@s", subtypeId);
                cmd.Parameters.AddWithValue("@n", name);
                cmd.Parameters.AddWithValue("@o", order);
                cmd.Parameters.AddWithValue("@c", DateTime.Now.ToString("yyyy-MM-dd"));
            });
        MarkFlatListAlive(FlatKindSubtypeExecution, SubtypeExecutionKey(subtypeId, name));
    }

    /// <summary>Убрать исполнение у шкафа — «КПЧ больше не выпускают на 3 насоса».
    ///
    /// Файлы на диске НЕ трогаются и уже выпущенные версии не меняются: исполнение перестаёт
    /// предлагаться и перестаёт попадать в выдачу, но история по нему читается. Это ровно то
    /// разделение, ради которого справочник и заведён отдельно от содержимого папок: удаление —
    /// явное действие человека, а не вывод машины из того, что папку в свежей версии не положили.</summary>
    public void DeleteSubtypeExecution(int subtypeId, string name)
    {
        name = FwExecution.Normalize(name);
        if (name.Length == 0) return;
        var existing = GetSubtypeExecutions(subtypeId)
            .FirstOrDefault(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)) ?? name;
        ExecuteNonQuery("DELETE FROM fw_subtype_executions WHERE subtype_id=@s AND name=@n", cmd =>
        {
            cmd.Parameters.AddWithValue("@s", subtypeId);
            cmd.Parameters.AddWithValue("@n", existing);
        });
        MarkFlatListDeleted(FlatKindSubtypeExecution, SubtypeExecutionKey(subtypeId, existing));
    }

    /// <summary>Переименовать исполнение у шкафа. Папка на диске при этом НЕ переименовывается — и
    /// это намеренно: папка принадлежит уже выпущенной версии, трогать её значит переписывать
    /// историю задним числом на сетевой шаре, где рядом работают чужие клиенты. Последствие честное
    /// и видимое: у прежних версий исполнение перестаёт считаться собранным, и карточка скажет
    /// «конфигурации нет — исходная сборка». Поэтому переименование — средство исправить опечатку в
    /// только что заведённом исполнении, а не способ переразметить историю.</summary>
    public bool RenameSubtypeExecution(int subtypeId, string oldName, string newName)
    {
        oldName = FwExecution.Normalize(oldName);
        newName = FwExecution.Normalize(newName);
        if (oldName.Length == 0 || newName.Length == 0) return false;
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return true;

        var current = GetSubtypeExecutions(subtypeId);
        if (current.Any(x => string.Equals(x, newName, StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(x, oldName, StringComparison.OrdinalIgnoreCase)))
            return false;
        var existing = current.FirstOrDefault(x => string.Equals(x, oldName, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return false;

        ExecuteNonQuery("UPDATE fw_subtype_executions SET name=@new WHERE subtype_id=@s AND name=@old", cmd =>
        {
            cmd.Parameters.AddWithValue("@new", newName);
            cmd.Parameters.AddWithValue("@old", existing);
            cmd.Parameters.AddWithValue("@s", subtypeId);
        });
        MarkFlatListDeleted(FlatKindSubtypeExecution, SubtypeExecutionKey(subtypeId, existing));
        MarkFlatListAlive(FlatKindSubtypeExecution, SubtypeExecutionKey(subtypeId, newName));
        return true;
    }

    // ── Ключи синхронизации ──────────────────────────────────────────────────────────────────────

    /// <summary>Ключ строки для плоского списка: «&lt;группа&gt;/&lt;подтип&gt;|&lt;исполнение&gt;».
    /// Пустая строка — подтипа с таким id нет (его успели удалить): такую строку не синхронизируем,
    /// завести исполнение несуществующему шкафу нельзя.</summary>
    public string SubtypeExecutionKey(int subtypeId, string execution)
    {
        var path = SubtypePathById(subtypeId);
        return path.Length == 0 ? "" : path + SubtypeExecutionKeySeparator + FwExecution.Normalize(execution);
    }

    /// <summary>Разбор ключа обратно. null — ключ не разбирается или шкафа с таким именем на этой
    /// машине нет: исполнение чужого шкафа не заводим молча — завести его было бы хуже, чем не
    /// завести, потому что оно появилось бы в выборе там, где шкафа не существует.</summary>
    public (int SubtypeId, string Execution)? ParseSubtypeExecutionKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var at = key!.LastIndexOf(SubtypeExecutionKeySeparator);
        if (at <= 0 || at == key.Length - 1) return null;
        var execution = FwExecution.Normalize(key[(at + 1)..]);
        if (execution.Length == 0) return null;
        var subtypeId = SubtypeIdByPath(key[..at]);
        return subtypeId is null ? null : (subtypeId.Value, execution);
    }

    /// <summary>Все ключи для выгрузки конфига.</summary>
    public List<string> GetSubtypeExecutionKeys() =>
        GetSubtypeExecutionRows()
            .Select(r => SubtypeExecutionKey(r.SubtypeId, r.Name))
            .Where(k => k.Length > 0)
            .ToList();

    /// <summary>Завести исполнение по ключу — точка входа синхронизации.</summary>
    public void AddSubtypeExecutionByKey(string key)
    {
        if (ParseSubtypeExecutionKey(key) is not { } parsed) return;
        AddSubtypeExecution(parsed.SubtypeId, parsed.Execution);
    }

    /// <summary>Убрать исполнение по ключу — точка входа синхронизации.</summary>
    public void DeleteSubtypeExecutionByKey(string key)
    {
        if (ParseSubtypeExecutionKey(key) is not { } parsed) return;
        DeleteSubtypeExecution(parsed.SubtypeId, parsed.Execution);
    }

    private string SubtypePathById(int subtypeId)
    {
        using var reader = ExecuteReader(
            "SELECT g.name, s.name FROM equipment_subtypes s " +
            "JOIN equipment_groups g ON g.id = s.group_id WHERE s.id=@s",
            cmd => cmd.Parameters.AddWithValue("@s", subtypeId));
        return reader.Read() ? reader.GetString(0) + "/" + reader.GetString(1) : "";
    }

    private int? SubtypeIdByPath(string path)
    {
        var at = path.IndexOf('/');
        if (at <= 0 || at == path.Length - 1) return null;
        var group = path[..at];
        var subtype = path[(at + 1)..];
        // Регистр сводится в .NET: имена шкафов кириллические, а NOCASE у SQLite кириллицу не
        // трогает (CLAUDE.md) — иначе ключ с чужой машины, набранный иначе по регистру, завёл бы
        // исполнение «никому».
        using var reader = ExecuteReader(
            "SELECT s.id, g.name, s.name FROM equipment_subtypes s JOIN equipment_groups g ON g.id = s.group_id");
        while (reader.Read())
            if (string.Equals(reader.GetString(1), group, StringComparison.OrdinalIgnoreCase)
                && string.Equals(reader.GetString(2), subtype, StringComparison.OrdinalIgnoreCase))
                return reader.GetInt32(0);
        return null;
    }
}
