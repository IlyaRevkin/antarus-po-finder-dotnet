using System;
using System.Collections.Generic;
using System.Linq;

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
    /// <summary>Разрешённые расширения поиска схем на втором диске (SchematicService) — третий
    /// независимый список того же вида, что и два выше, своя строка kind. См.
    /// allowed_extensions_schematic в Database.cs и Database.Params.cs.</summary>
    public const string FlatKindExtensionSchematic = "extension_schematic";

    /// <summary>Теги КОНКРЕТНОЙ ЗАПИСИ (прошивки или файла параметров) — тот же LWW-регистр, что и у
    /// справочников выше, только ключом идёт не «список», а «список этой строки»: kind =
    /// <c>rowtag:&lt;sync_id записи&gt;</c>, name = сам тег.
    ///
    /// <b>Зачем.</b> Строки fw_versions/param_files синхронизируются АДДИТИВНО (отсутствие ≠ удаление),
    /// а теги на них до сих пор просто ОБЪЕДИНЯЛИСЬ множествами. Объединение появилось не зря — тег
    /// вешают на давно разошедшуюся по машинам прошивку, и без него добавленный тег к коллегам не
    /// доезжал. Но у объединения нет обратного хода: снятый тег возвращался с первой же машины,
    /// которая о снятии ещё не знала, — дословная жалоба «удаляю теги, а они снова появляются». А
    /// поскольку синхронизация идёт в обе стороны, воскресший тег ехал обратно и к тому, кто его
    /// снял, — снять его не мог уже никто.
    ///
    /// Теперь снятие тега — такое же положительно распространяемое событие с отметкой времени, как
    /// удаление в справочнике: выигрывает более поздняя отметка, а не тот, кто позже нажал импорт.
    /// Записей ровно столько, сколько тегов реально трогали руками, и уезжают они в общий конфиг той
    /// же секцией flat_list_state — старый клиент их не понимает, но и не теряет: свой снимок он
    /// отдаёт целиком, вместе с чужими kind.</summary>
    public const string FlatKindRowTagPrefix = "rowtag:";

    public static string RowTagKind(string? rowSyncId) => FlatKindRowTagPrefix + (rowSyncId ?? "").Trim();

    /// <summary>Запомнить, что у записи <paramref name="rowSyncId"/> набор тегов сменился с
    /// <paramref name="oldTags"/> на <paramref name="newTags"/>: снятые помечаются удалёнными,
    /// добавленные — живыми. Без sync_id (строка ещё не размечена) писать некуда — молча выходим, у
    /// такой строки и синхронизации пока нет.</summary>
    internal void RecordRowTagChange(string? rowSyncId, string? oldTags, string? newTags)
    {
        if (string.IsNullOrWhiteSpace(rowSyncId)) return;

        var kind = RowTagKind(rowSyncId);
        var before = new HashSet<string>(Services.TagString.Parse(oldTags ?? ""), StringComparer.OrdinalIgnoreCase);
        var after = new HashSet<string>(Services.TagString.Parse(newTags ?? ""), StringComparer.OrdinalIgnoreCase);

        foreach (var tag in before)
            if (!after.Contains(tag))
                MarkFlatListDeleted(kind, tag);

        foreach (var tag in after)
            if (!before.Contains(tag))
                MarkFlatListAlive(kind, tag);
    }

    /// <summary>Отметки по тегам записей одним чтением — импорт перебирает тысячи строк, и ходить в
    /// таблицу за состоянием на каждую было бы квадратично.</summary>
    internal Dictionary<string, FlatListState> GetRowTagState()
    {
        var result = new Dictionary<string, FlatListState>(StringComparer.OrdinalIgnoreCase);
        using var reader = ExecuteReader(
            "SELECT kind, name, deleted_at, revived_at FROM flat_list_state WHERE kind LIKE @p",
            cmd => cmd.Parameters.AddWithValue("@p", FlatKindRowTagPrefix + "%"));
        while (reader.Read())
        {
            var state = new FlatListState(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
            result[state.Kind + "\n" + state.Name] = state;
        }
        return result;
    }

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

    /// <summary>Сведение наборов тегов ОДНОЙ записи с чужим снимком по отметкам времени.
    ///
    /// Правила ровно два, зеркально ImportFlatList (см. его доку — там та же мысль про справочники):
    /// <list type="bullet">
    /// <item>чужой тег, которого у нас нет, добавляем — КРОМЕ случая, когда мы его осознанно сняли и
    ///       наша отметка о снятии свежее чужой отметки о навешивании. Именно это и чинит жалобу
    ///       «удаляю теги, а они снова появляются»;</item>
    /// <item>наш тег, который чужая сторона осознанно СНЯЛА позже нашей последней отметки, снимаем и
    ///       у себя — иначе снятие жило бы только на той машине, где его сделали.</item>
    /// </list>
    /// Отметок нет ни у кого (старый клиент, тег навешен до этой версии) — работает прежнее чистое
    /// объединение: ни одна машина не должна терять теги из-за того, что коллега на старой версии.
    ///
    /// Порядок локальных тегов сохраняется, новые уходят в конец — строка тегов не должна
    /// перетасовываться на каждой синхронизации, иначе она вечно «менялась» бы для ContentSignature.</summary>
    internal sealed class RowTagMerger
    {
        private readonly Database _db;
        private readonly Dictionary<string, FlatListState> _local;
        private readonly Dictionary<string, FlatListState> _incoming;

        public RowTagMerger(Database db, List<ExportedFlatListState>? incomingState)
        {
            _db = db;
            _local = db.GetRowTagState();
            _incoming = new Dictionary<string, FlatListState>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in incomingState ?? new List<ExportedFlatListState>())
            {
                if (s.Kind is null || !s.Kind.StartsWith(FlatKindRowTagPrefix, StringComparison.Ordinal)) continue;
                _incoming[Key(s.Kind, s.Name ?? "")] =
                    new FlatListState(s.Kind, s.Name ?? "", s.DeletedAt ?? "", s.RevivedAt ?? "");
            }
        }

        private static string Key(string kind, string name) => kind + "\n" + name.Trim();

        private FlatListState? Find(Dictionary<string, FlatListState> from, string? syncA, string? syncB, string tag)
        {
            foreach (var sync in new[] { syncA, syncB })
            {
                if (string.IsNullOrWhiteSpace(sync)) continue;
                if (from.TryGetValue(Key(RowTagKind(sync), tag), out var found)) return found;
            }
            return null;
        }

        public string Merge(string? localSyncId, string? incomingSyncId, string? localTags, string? incomingTags, bool apply)
        {
            var localList = Services.TagString.Parse(localTags ?? "");
            var incomingList = Services.TagString.Parse(incomingTags ?? "");
            var have = new HashSet<string>(localList, StringComparer.OrdinalIgnoreCase);
            var incomingSet = new HashSet<string>(incomingList, StringComparer.OrdinalIgnoreCase);

            var result = new List<string>(localList);
            var adopt = new List<FlatListState>();

            // Чужие теги, которых у нас нет.
            foreach (var tag in incomingList)
            {
                if (have.Contains(tag)) continue;

                var mine = Find(_local, localSyncId, incomingSyncId, tag);
                var theirs = Find(_incoming, incomingSyncId, localSyncId, tag);
                // Наше снятие свежее их навешивания — тег не возвращаем. Отметок нет вовсе — прежнее
                // поведение (просто добавить).
                if (mine is { IsAlive: false } &&
                    string.CompareOrdinal(mine.LastEventAt, theirs?.LastEventAt ?? "") > 0)
                    continue;

                result.Add(tag);
                have.Add(tag);
                if (theirs is not null) adopt.Add(theirs);
            }

            // Наши теги, которые ОНИ сняли позже нашей последней отметки.
            var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in localList)
            {
                if (incomingSet.Contains(tag)) continue;

                var theirs = Find(_incoming, incomingSyncId, localSyncId, tag);
                if (theirs is null || theirs.IsAlive) continue;

                var mine = Find(_local, localSyncId, incomingSyncId, tag);
                if (string.CompareOrdinal(theirs.LastEventAt, mine?.LastEventAt ?? "") <= 0) continue;

                removed.Add(tag);
                adopt.Add(theirs);
            }
            if (removed.Count > 0) result = result.Where(t => !removed.Contains(t)).ToList();

            // Чужие отметки запоминаем как свои — иначе применённое чужое снятие выглядело бы у нас
            // как ничьё, и первая же машина со старым набором вернула бы тег обратно.
            if (apply)
                foreach (var state in adopt)
                {
                    _db.SetFlatListState(state.Kind, state.Name, state.DeletedAt, state.RevivedAt);
                    if (!string.IsNullOrWhiteSpace(localSyncId) &&
                        !string.Equals(state.Kind, RowTagKind(localSyncId), StringComparison.OrdinalIgnoreCase))
                        _db.SetFlatListState(RowTagKind(localSyncId), state.Name, state.DeletedAt, state.RevivedAt);
                }

            return result.Count == localList.Count && removed.Count == 0
                ? localTags ?? ""
                : Services.TagString.Join(result);
        }
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
