using System;
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
    // Те же слова, что во вкладке «Прошивки» в Настройках — одно и то же состояние версии не должно
    // называться на двух экранах по-разному. (В «Модерации прошивок» столбца статуса больше нет:
    // откатанные и заменённые туда не попадают вовсе, см. Database.GetUnreleasedFwVersionsWithNames.)
    public const string RolledBack = "Откатана";
    public const string Current = "Текущая";
    public const string Superseded = "Заменена";

    public static string CurrentForHw(int hwVersion) => $"{Current} (HW {hwVersion})";

    /// <summary>Метки в том же порядке, что и <paramref name="newestFirst"/> — версии должны быть
    /// отсортированы от новых к старым (как их отдаёт Database.GetFwVersionsHistory).
    ///
    /// ManualCurrent (см. FwVersionRecord.ManualCurrent / Database.SetFwVersionManualCurrent) даёт
    /// оператору ручной оверрайд: «текущей» в своей hw-группе считается версия, отмеченная вручную,
    /// а не автоматически самая свежая по sw_version — например, когда более новую по номеру версию
    /// на практике забраковали и вернулись к прежней, не откатывая её формально. Без единой отметки в
    /// группе (обычный случай) результат в точности совпадает с прежним поведением: «текущая» —
    /// первая живая версия своей hw-группы по естественному порядку.</summary>
    public static List<string> Labels(IReadOnlyList<FwVersionRecord> newestFirst)
    {
        var alive = newestFirst.Where(v => v.Status != "rolled_back").ToList();

        // «Текущая» внутри каждой hw-группы — обычно первая (самая свежая) живая версия этой группы,
        // но если оператор вручную отметил другую версию как текущую, используется она.
        var newestPerHw = new Dictionary<int, FwVersionRecord>();
        foreach (var group in alive.GroupBy(v => v.HwVersion))
        {
            var manual = group.FirstOrDefault(v => v.ManualCurrent);
            newestPerHw[group.Key] = manual ?? group.First();
        }

        // Общая «Текущая» (без пометки HW) — первая по естественному порядку версия среди отобранных
        // выше кандидатов каждой hw-группы. Без ручных отметок это тривиально совпадает с прежним
        // "alive.FirstOrDefault()": самый первый элемент отсортированного списка неизбежно является
        // и первым элементом своей же hw-подгруппы.
        var newest = alive.FirstOrDefault(v => newestPerHw.TryGetValue(v.HwVersion, out var pick) && ReferenceEquals(pick, v));

        return newestFirst.Select(v =>
            v.Status == "rolled_back" ? RolledBack
            : ReferenceEquals(v, newest) ? Current
            : newestPerHw.TryGetValue(v.HwVersion, out var hwNewest) && ReferenceEquals(v, hwNewest)
                ? CurrentForHw(v.HwVersion)
                : Superseded).ToList();
    }

    /// <summary>То же самое, что Labels(...), но для СМЕШАННОГО списка версий сразу нескольких шкафов
    /// — как в таблице «Прошивки» (Настройки), где показаны версии всех подтипов/контроллеров разом,
    /// а не история одного шкафа. Группирует записи по (подтип, контроллер) — ровно тот же ключ, что
    /// и Database.GetFwVersionsHistory для истории ОДНОГО шкафа — сортирует каждую группу в её
    /// собственном порядке (важно: Labels ожидает версии от новых к старым) и возвращает словарь
    /// id → метка. До этой правки таблица «Прошивки» показывала сырое поле status ('active'/
    /// 'rolled_back'), из-за чего несколько версий одного шкафа с разными sw_version все разом
    /// значились «Активна» (реальная жалоба). Версии без Id (ещё не сохранённые в БД) в результат не
    /// попадают — им попросту некуда сохраниться в словаре по id.</summary>
    public static Dictionary<int, string> LabelsByGroup(IEnumerable<FwVersionRecord> versions)
    {
        var result = new Dictionary<int, string>();
        foreach (var group in versions.GroupBy(v => (v.SubtypeId, v.ControllerId)))
        {
            // Тот же порядок сортировки, что и в SQL у Database.GetFwVersionsHistory:
            // dt_str DESC, hw_version DESC, sw_version DESC, id DESC.
            var sorted = group
                .OrderByDescending(v => v.DtStr, StringComparer.Ordinal)
                .ThenByDescending(v => v.HwVersion)
                .ThenByDescending(v => v.SwVersion)
                .ThenByDescending(v => v.Id)
                .ToList();
            var labels = Labels(sorted);
            for (int i = 0; i < sorted.Count; i++)
                if (sorted[i].Id is int id)
                    result[id] = labels[i];
        }
        return result;
    }
}
