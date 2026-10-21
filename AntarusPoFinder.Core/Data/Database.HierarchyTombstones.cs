using System;
using System.Collections.Generic;

namespace AntarusPoFinder.Core.Data;

/// <summary>ЧТО УДАЛИЛИ ИЗ СПРАВОЧНИКА — И КОГДА.
///
/// <b>Из-за чего заведено.</b> Удалённый подтип возвращался. Жалоба Ильи повторялась трижды: «всё
/// ещё не удаляется НГР 2.0, которую заменили НГР КПЧ». Механизм возврата такой:
/// <list type="number">
/// <item>оператор удаляет подтип у себя — локально его больше нет;</item>
/// <item>на машине коллеги под этим подтипом ещё лежат версии или параметры, поэтому обмен конфигом
/// удаление там НЕ повторяет (Database.ConfigExchange, счётчик SubtypesSkippedDelete) — и правильно
/// делает: стирать чужие записи по чужому снимку нельзя;</item>
/// <item>коллега выгружает свой конфиг, где подтип ЖИВОЙ;</item>
/// <item>подтип возвращается к тому, кто его удалил, — и так по кругу.</item>
/// </list>
///
/// <b>Что меняет надгробие.</b> Факт удаления перестаёт быть «отсутствием записи» и становится
/// записью со своей датой, которая ездит в общем конфиге наравне со справочником. Входящий «живой»
/// подтип, удалённый ПОЗЖЕ своей последней правки, обратно не заводится. Это та же мысль, по которой
/// уже живут прошивки и файлы параметров (fw_versions.deleted_at, param_files) — просто у иерархии
/// её до сих пор не было.
///
/// <b>Чего надгробие НЕ делает.</b> Оно не стирает чужие данные: если у коллеги под подтипом лежат
/// версии, подтип у него останется вместе с ними, и обмен по-прежнему посчитает это пропущенным
/// удалением. Надгробие лечит ровно возврат к тому, кто удалял, а не «удалить везде любой ценой».
///
/// <b>Почему отдельная таблица, а не колонка deleted_at.</b> Колонка потребовала бы фильтра
/// <c>deleted_at=''</c> в каждом запросе к справочнику — а их десятки, от выдачи поиска до сборки
/// путей на диске. Забытый фильтр означал бы «удалённый подтип снова виден», то есть ровно ту беду,
/// которую чиним. Отдельная таблица на такие запросы не влияет вовсе.</summary>
public partial class Database
{
    public const string TombstoneGroup = "group";
    public const string TombstoneSubtype = "subtype";

    /// <summary>Записать факт удаления. Вызывается ДО физического удаления строки — ей нужен
    /// sync_id, по которому надгробие потом и узнаётся на других машинах.
    ///
    /// Пустой sync_id — не ошибка: строки, заведённые до появления обмена, его не имеют, соотнести
    /// их со входящим снимком всё равно нечем, и надгробие для них бессмысленно.</summary>
    public void MarkHierarchyDeleted(string kind, string? syncId, string label, string? deletedAt = null)
    {
        if (string.IsNullOrWhiteSpace(syncId)) return;

        ExecuteNonQuery("""
            INSERT INTO hierarchy_tombstones(kind, sync_id, label, deleted_at)
            VALUES(@k, @s, @l, @d)
            ON CONFLICT(kind, sync_id) DO UPDATE SET label=excluded.label, deleted_at=excluded.deleted_at
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@k", kind);
            cmd.Parameters.AddWithValue("@s", syncId!.Trim());
            cmd.Parameters.AddWithValue("@l", label ?? "");
            cmd.Parameters.AddWithValue("@d", deletedAt ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        });
    }

    /// <summary>Надгробие снимается, когда запись заводят заново ПОД ТЕМ ЖЕ sync_id. Бывает при
    /// разборе конфликтов: оператор решает «нет, пусть живёт». Без снятия обмен вечно считал бы её
    /// удалённой и выбрасывал при каждом приходе.</summary>
    public void ForgetHierarchyDeletion(string kind, string? syncId)
    {
        if (string.IsNullOrWhiteSpace(syncId)) return;

        ExecuteNonQuery("DELETE FROM hierarchy_tombstones WHERE kind=@k AND sync_id=@s", cmd =>
        {
            cmd.Parameters.AddWithValue("@k", kind);
            cmd.Parameters.AddWithValue("@s", syncId!.Trim());
        });
    }

    public sealed record HierarchyTombstone(string Kind, string SyncId, string Label, string DeletedAt);

    public List<HierarchyTombstone> GetHierarchyTombstones()
    {
        var result = new List<HierarchyTombstone>();
        using var reader = ExecuteReader(
            "SELECT kind, sync_id, label, deleted_at FROM hierarchy_tombstones ORDER BY deleted_at, sync_id");
        while (reader.Read())
            result.Add(new HierarchyTombstone(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return result;
    }

    /// <summary>Удалена ли запись ПОЗЖЕ, чем её правка во входящем снимке.
    ///
    /// Сравнение по дате, а не «есть надгробие — значит нельзя»: подтип могли удалить, а потом
    /// коллега осознанно завёл его заново и переименовал. Если его правка новее удаления — побеждает
    /// правка, как и везде в обмене (LWW). Даты местные и сравниваются строкой: так они лежат в базе
    /// с первого дня, и переводить одну из них на UTC значило бы сравнивать по разным шкалам.</summary>
    public bool DeletedLaterThan(string kind, string? syncId, string? incomingUpdatedAt)
    {
        if (string.IsNullOrWhiteSpace(syncId)) return false;

        var deletedAt = ExecuteScalar(
            "SELECT deleted_at FROM hierarchy_tombstones WHERE kind=@k AND sync_id=@s", cmd =>
            {
                cmd.Parameters.AddWithValue("@k", kind);
                cmd.Parameters.AddWithValue("@s", syncId!.Trim());
            }) as string;

        if (string.IsNullOrWhiteSpace(deletedAt)) return false;
        if (string.IsNullOrWhiteSpace(incomingUpdatedAt)) return true;   // дата правки неизвестна — верим удалению

        return string.CompareOrdinal(deletedAt, incomingUpdatedAt) >= 0;
    }
}
