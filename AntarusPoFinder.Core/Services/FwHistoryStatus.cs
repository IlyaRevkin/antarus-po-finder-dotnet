using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Статус версии внутри истории ОДНОГО шкафа (подтип + контроллер).
///
/// Поле status в БД знает ровно два состояния: 'active' и 'rolled_back' — то есть «активны» там все
/// версии, которые не откатывали, включая давно замененные. В окне истории это выглядело так, будто
/// в работе одновременно вся история («загружаю прошивку — а в истории все активные»). Актуальна же
/// всегда одна версия, поэтому статус для показа считается по самой истории, а не берётся из поля.
///
/// Оговорка про hw: версии с разным hw_version — это разные аппаратные исполнения шкафа, и свежая
/// версия под другое железо ничего не заменяет. Поэтому самая новая версия каждого hw, кроме
/// абсолютно самой новой, помечается «Актуальная (HW n)» — наладчик со старой платой видит, что
/// актуально ему, и не считает свою строку устаревшей.</summary>
public static class FwHistoryStatus
{
    public const string RolledBack = "Откатана";
    public const string Current = "Актуальная";
    public const string Superseded = "Заменена";

    public static string CurrentForHw(int hwVersion) => $"{Current} (HW {hwVersion})";

    /// <summary>Метки в том же порядке, что и <paramref name="newestFirst"/> — версии должны быть
    /// отсортированы от новых к старым (как их отдаёт Database.GetFwVersionsHistory).</summary>
    public static List<string> Labels(IReadOnlyList<FwVersionRecord> newestFirst)
    {
        var alive = newestFirst.Where(v => v.Status != "rolled_back").ToList();
        var newest = alive.FirstOrDefault();
        var newestPerHw = alive.GroupBy(v => v.HwVersion).ToDictionary(g => g.Key, g => g.First());

        return newestFirst.Select(v =>
            v.Status == "rolled_back" ? RolledBack
            : ReferenceEquals(v, newest) ? Current
            : newestPerHw.TryGetValue(v.HwVersion, out var hwNewest) && ReferenceEquals(v, hwNewest)
                ? CurrentForHw(v.HwVersion)
                : Superseded).ToList();
    }
}
