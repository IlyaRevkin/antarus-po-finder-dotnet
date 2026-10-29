using System;
using System.Collections.Generic;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что вообще лежит в базе прошивок — счётом, а не на глаз.
///
/// <b>Откуда взялось.</b> Тикет: «Оптимизация базы прошивок и настройка актуальных. Есть много
/// откатанных и не все (касаемо kinco) рабочие». Разговор про «много» и «не все» нельзя вести, пока
/// никто не видит чисел: сколько версий всего, сколько из них откатано, сколько выключено, у скольких
/// пропал файл на диске. Эта сводка и есть первый шаг — она ничего не чинит и ничего не трогает,
/// она даёт факты, по которым можно решать.
///
/// <b>Почему «откатано» и «выключено» считаются отдельно.</b> Это разные вещи, и путать их дорого:
/// откат говорит «версии не должно было быть» и ОСВОБОЖДАЕТ номер ПО, выключение говорит «пока не
/// нужна» и номер за ней сохраняет (см. <see cref="FwStatus"/> и Database.GetNextSwVersion). Одна
/// общая цифра «неактивных» скрыла бы ровно ту разницу, ради которой выключение и заводили.
///
/// <b>Почему отсутствующий файл — не ошибка.</b> Версия может лежать на сетевом диске, который
/// сейчас отвалился, или на втором диске, который не подключён. Поэтому счёт ведётся только по тем
/// записям, путь которых проверять было чем, а остальные идут отдельной строкой «не проверено» —
/// иначе отключённый диск превращался бы в отчёт «пропало всё».</summary>
public sealed record FirmwareInventory(
    int Total,
    int Active,
    int RolledBack,
    int Disabled,
    int Unreleased,
    int FilesChecked,
    int FilesMissing)
{
    /// <summary>Сколько записей проверить не удалось — путь пуст или диск недоступен.</summary>
    public int FilesUnchecked => Total - FilesChecked;

    /// <summary>Строка для человека. Собирается здесь, а не в разметке: тот же текст уходит и в
    /// журнал самопроверки, и расходиться они не должны.</summary>
    public string Summary()
    {
        var parts = new List<string>
        {
            $"всего версий {Total}",
            $"в работе {Active}",
        };
        if (RolledBack > 0) parts.Add($"откатано {RolledBack}");
        if (Disabled > 0) parts.Add($"выключено {Disabled}");
        if (Unreleased > 0) parts.Add($"ждут модерации {Unreleased}");
        if (FilesMissing > 0) parts.Add($"файл не найден у {FilesMissing}");
        if (FilesUnchecked > 0) parts.Add($"не проверено {FilesUnchecked}");
        return string.Join(", ", parts);
    }

    /// <summary>Посчитать сводку.
    /// </summary>
    /// <param name="statuses">Статус каждой версии (пусто или «active» — в работе).</param>
    /// <param name="released">Выпущена ли версия: невыпущенные ждут модерации.</param>
    /// <param name="fileExists">Проверка «лежит ли файл» для записи с данным номером. Возвращает
    /// null, если проверить было нечем — путь пуст или диск недоступен.</param>
    public static FirmwareInventory Count(
        IReadOnlyList<string?> statuses,
        IReadOnlyList<bool> released,
        Func<int, bool?> fileExists)
    {
        if (statuses is null) throw new ArgumentNullException(nameof(statuses));
        if (fileExists is null) throw new ArgumentNullException(nameof(fileExists));

        int active = 0, rolled = 0, disabled = 0, unreleased = 0, checkedCount = 0, missing = 0;

        for (var i = 0; i < statuses.Count; i++)
        {
            var status = statuses[i];
            if (FwStatus.IsRolledBack(status)) rolled++;
            else if (FwStatus.IsDisabled(status)) disabled++;
            else active++;

            if (released is not null && i < released.Count && !released[i]) unreleased++;

            var exists = fileExists(i);
            if (exists is null) continue;
            checkedCount++;
            if (exists == false) missing++;
        }

        return new FirmwareInventory(statuses.Count, active, rolled, disabled, unreleased, checkedCount, missing);
    }
}
