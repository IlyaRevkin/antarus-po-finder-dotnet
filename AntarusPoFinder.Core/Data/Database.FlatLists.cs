using System;
using System.Collections.Generic;

namespace AntarusPoFinder.Core.Data;

/// <summary>Одна запись «когда этот элемент плоского списка удаляли и когда возвращали».
/// Живым считается элемент, у которого RevivedAt не меньше DeletedAt (пустая строка — «никогда»).</summary>
public record FlatListState(string Kind, string Name, string DeletedAt, string RevivedAt)
{
    public bool IsAlive => string.CompareOrdinal(RevivedAt, DeletedAt) >= 0;

    /// <summary>Последнее по времени событие с этим элементом — им и сравниваются две машины.</summary>
    public string LastEventAt => string.CompareOrdinal(RevivedAt, DeletedAt) >= 0 ? RevivedAt : DeletedAt;
}

public partial class Database
{
    /// <summary>Плоские списки-справочники (производители ПЧ/УПП, теги, разрешённые расширения) не
    /// имеют ни sync_id, ни updated_at — до этой таблицы config-обмен синхронизировал их «зеркалом»:
    /// чего нет во входящем наборе, то удаляется локально. Зеркало без отметок времени — это
    /// «выигрывает тот, кто последним нажал импорт», и оно ломалось ровно так, как жаловался
    /// пользователь:
    ///   • ПК A добавил производителей и выгрузил конфиг; ПК B, ещё не забравший этот конфиг,
    ///     выгружает свой (без новых производителей) поверх — A импортирует и ТЕРЯЕТ то, что сам же
    ///     добавил («добавил производителей ПЧ/УПП, а они не синхронизировались»);
    ///   • симметрично, удалённый мусорный элемент возвращался с любой машины, которая ещё не знала
    ///     о его удалении («залил новые настройки, а с какого-то компа опять мусорное название»).
    ///
    /// Теперь удаление и возврат — положительно распространяемые события с отметкой времени, а не
    /// вывод из отсутствия в чужом списке. По каждому имени хранится последний известный факт
    /// (LWW-регистр): выигрывает более поздняя отметка, независимо от порядка импортов.</summary>
    public const string FlatKindManufacturer = "manufacturer";
    public const string FlatKindTag = "tag";
    public const string FlatKindExtension = "extension";
    /// <summary>Разрешённые расширения HMI-проектов — независимый список от FlatKindExtension (ПЛК),
    /// своя строка kind в flat_list_state, чтобы удаление/возврат в одном списке не задевало другой.</summary>
    public const string FlatKindExtensionHmi = "extension_hmi";

    /// <summary>Секундной точности обычного NowIso здесь не хватает: «удалил и тут же вернул» (или
    /// два разных элемента списка подряд) укладывается в одну секунду, и события со строково равными
    /// отметками становятся неразличимыми — побеждала бы та сторона, которая просто позже нажала
    /// импорт, т.е. ровно то, от чего эта таблица и заводилась. Строковое сравнение с секундными
    /// отметками остаётся корректным: более длинная строка с дробной частью больше.</summary>
    private static string NowIsoPrecise() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffffff");

    internal void MarkFlatListAlive(string kind, string name) => SetFlatListState(kind, name, deletedAt: null, revivedAt: NowIsoPrecise());

    internal void MarkFlatListDeleted(string kind, string name) => SetFlatListState(kind, name, deletedAt: NowIsoPrecise(), revivedAt: null);

    /// <summary>null в любом из полей означает «не трогать это поле» — так пометка об удалении не
    /// стирает историю возврата и наоборот.</summary>
    internal void SetFlatListState(string kind, string name, string? deletedAt, string? revivedAt)
    {
        name = name.Trim();
        if (name.Length == 0) return;

        ExecuteNonQuery("""
            INSERT INTO flat_list_state(kind, name, deleted_at, revived_at)
            VALUES(@k, @n, COALESCE(@d, ''), COALESCE(@r, ''))
            ON CONFLICT(kind, name) DO UPDATE SET
                deleted_at = COALESCE(@d, deleted_at),
                revived_at = COALESCE(@r, revived_at)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@k", kind);
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@d", (object?)deletedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@r", (object?)revivedAt ?? DBNull.Value);
        });
    }

    public List<FlatListState> GetFlatListState()
    {
        var result = new List<FlatListState>();
        using var reader = ExecuteReader("SELECT kind, name, deleted_at, revived_at FROM flat_list_state ORDER BY kind, name");
        while (reader.Read())
            result.Add(new FlatListState(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return result;
    }

    public FlatListState? GetFlatListState(string kind, string name)
    {
        using var reader = ExecuteReader(
            "SELECT kind, name, deleted_at, revived_at FROM flat_list_state WHERE kind=@k AND name=@n COLLATE NOCASE", cmd =>
            {
                cmd.Parameters.AddWithValue("@k", kind);
                cmd.Parameters.AddWithValue("@n", name.Trim());
            });
        return reader.Read() ? new FlatListState(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)) : null;
    }
}
