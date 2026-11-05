using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>ОДНА ПРОШИВКА — ОДНА СТРОКА в очереди модерации.
///
/// Откуда взялась беда. У одной залитой прошивки заводится СВОЯ ЗАПИСЬ НА КАЖДЫЙ ПОДТИП шкафа,
/// которому она подходит (FirmwareUploadService.LinkToExtraSubtypes и FirmwareSubtypeLinkService).
/// Файлы при этом одни и те же. В выдаче поиска такие строки схлопываются (Database.Deduplicate),
/// а очередь модерации перечисляла их по одной — и человек, заливающий прошивки сразу на пять
/// подтипов, видел у себя «20+» там, где у соседа «2–3». Расходились не счётчик со списком
/// (диагностика показала, что они совпадают всегда), а МАШИНЫ: в наборе с машины NALADKA1 номера
/// версий прямо повторялись — «1.1.0005.0004» дважды, «2.4.1321.0009» дважды.
///
/// По какому признаку схлопываем. Ровно по тому, по которому эти записи И ЕСТЬ одна прошивка, —
/// тому же, что давно используют выпуск группы и доставка решения коллегам
/// (Database.MarkFwVersionReleasedWithLinked, Database.GetFwVersionIdsSharingFiles). Признака
/// два, потому что родство хранилось двумя способами в разные годы:
///
/// <list type="number">
/// <item><description>общая папка на диске — пара disk_path + version_raw (прежние записи-ярлыки);
/// </description></item>
/// <item><description>явное родство — copy_of = sync_id оригинала (нынешние копии, у которых своя
/// папка и свой номер).</description></item>
/// </list>
///
/// Второго признака не придумывается: номер версии сам по себе в ключ НЕ входит и в одиночку
/// ничего не склеивает — две разные прошивки разных шкафов вполне могут носить один номер, и
/// склеив их, человек одним нажатием выпустил бы чужую.
///
/// Родство транзитивно (объединение множеств): если A и B делят папку, а C — копия A, то все трое
/// одна прошивка. Поэтому группа может оказаться ШИРЕ, чем то, что захватывает один вызов
/// MarkFwVersionReleasedWithLinked, и выпускать её надо по всем членам сразу — см.
/// Database.ReleaseModerationGroup.</summary>
public static class ModerationQueue
{
    /// <summary>Сравнение путей — ТОЧНОЕ (Ordinal), как в SQL (disk_path = @d). Расходиться с
    /// запросом нельзя: иначе одно место считало бы две записи одной прошивкой, а другое — разными,
    /// и выпуск «всей группы» снова оставлял бы хвосты.</summary>
    public static List<ModerationQueueItem> Group(IEnumerable<FwVersionRecord> rows)
    {
        var list = rows.ToList();
        var parent = Enumerable.Range(0, list.Count).ToArray();

        int Find(int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }

        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb) return;
            // Младший индекс — всегда корень: так состав групп не зависит от порядка обхода.
            if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
        }

        var buckets = new Dictionary<string, int>(StringComparer.Ordinal);
        void Link(int i, string key)
        {
            if (key.Length == 0) return;
            if (buckets.TryGetValue(key, out var first)) Union(i, first);
            else buckets[key] = i;
        }

        for (var i = 0; i < list.Count; i++)
        {
            var r = list[i];
            // Пустой disk_path не связывает ничего: иначе «одной прошивкой» стали бы все записи без
            // файлов на диске разом.
            if (!string.IsNullOrWhiteSpace(r.DiskPath)) Link(i, "d|" + r.DiskPath + "|" + r.VersionRaw);
            Link(i, "s|" + FamilyRoot(r));
        }

        // Порядок групп — по первому появлению, то есть тот же, что у входного списка (очередь
        // отсортирована по дате загрузки): свежее сверху, как и было.
        var order = new List<int>();
        var members = new Dictionary<int, List<FwVersionRecord>>();
        for (var i = 0; i < list.Count; i++)
        {
            var root = Find(i);
            if (!members.TryGetValue(root, out var bag))
            {
                members[root] = bag = new List<FwVersionRecord>();
                order.Add(root);
            }
            bag.Add(list[i]);
        }

        return order.Select(root => new ModerationQueueItem(members[root])).ToList();
    }

    /// <summary>Корень семьи копий: у копии это sync_id оригинала (copy_of), у оригинала — свой
    /// sync_id. Пусто — родства нет, связывать не с чем.</summary>
    public static string FamilyRoot(FwVersionRecord r) =>
        !string.IsNullOrEmpty(r.CopyOf) ? r.CopyOf : r.SyncId ?? "";
}
