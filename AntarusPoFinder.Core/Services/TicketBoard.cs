using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>ЧТО ПОКАЗЫВАТЬ НА СТРАНИЦЕ ТИКЕТОВ И В КАКОМ ПОРЯДКЕ.
///
/// <b>Откуда взялось.</b> В выгрузке на 29.10.2026 — 55 тикетов, из них закрытых 40. Список
/// строился одним запросом «по дате создания, новые сверху», и пятнадцать живых тем были
/// размазаны между сорока уже сделанными. Страница, на которую заходят посмотреть «что ещё
/// чинить», отвечала на другой вопрос — «что когда заводили».
///
/// <b>Решение то же, что уже принято для автоотчётов о сбоях</b> (см. <see cref="TicketAutoReports"/>):
/// не удалять и не прятать навсегда, а убрать с глаз по умолчанию и показать число рядом с галкой.
/// Число обязательно: «скрыты» без числа читается как «потерялись».
///
/// <b>Почему отбор и порядок живут в ядре, а не в странице.</b> Ровно затем же, зачем там живёт
/// TicketVisibility: правило, проверяемое глазами по живому экрану, проверяется ровно один раз — в
/// тот день, когда его написали.</summary>
public static class TicketBoard
{
    /// <summary>Тикет, который ещё чего-то требует: открыт или взят в работу. Закрытый — нет.</summary>
    public static bool IsActive(Ticket t) => !string.Equals(t.Status, TicketStatus.Closed, StringComparison.Ordinal);

    /// <summary>Сколько тикетов ещё чинить. Это и есть то число, которое имеет смысл показывать
    /// человеку: «сколько было всего» не говорит ни о чём.</summary>
    public static int ActiveCount(IEnumerable<Ticket> tickets) => tickets.Count(IsActive);

    public static int ClosedCount(IEnumerable<Ticket> tickets) => tickets.Count(t => !IsActive(t));

    /// <summary>Что остаётся в списке после обеих галок: «показывать автоотчёты» и «показывать
    /// закрытые».
    ///
    /// Порядок отбора важен и выбран не случайно: автоотчёты отсеиваются ПЕРВЫМИ, чтобы число
    /// закрытых считалось по тому, что человек в принципе видит. Иначе галка «показать закрытые»
    /// обещала бы сорок тикетов, а показывала тридцать три — остальные остались бы спрятаны второй
    /// галкой, и выглядело бы это поломкой.</summary>
    public static List<Ticket> Visible(IEnumerable<Ticket> tickets, bool showAutoReports, bool showClosed)
    {
        var afterAuto = TicketAutoReports.Visible(tickets, showAutoReports);
        return showClosed ? afterAuto : afterAuto.Where(IsActive).ToList();
    }

    /// <summary>Сколько непросмотренного — то число, что висит на пункте меню «Тикеты».
    ///
    /// Считается ровно по тому же правилу доступа, по которому строится сама страница
    /// (<see cref="TicketVisibility.CanSee"/>): своя копия правила здесь уже жила и уже разъехалась
    /// — баги прошивок видны всем ролям, а в бейдж наладчику чужой баг не попадал, и на странице
    /// тикет был, а цифры не было.
    ///
    /// <paramref name="fwBugsHaveOwnPage"/> — есть ли у багов прошивок отдельный пункт меню со
    /// своим счётчиком. Есть — здесь они не считаются: одно и то же число в двух местах меню
    /// означает «что-то новое» дважды, и человек ищет его дважды.</summary>
    public static int UnseenCount(IEnumerable<Ticket> tickets, string role, string userName,
        string lastSeenAt, bool fwBugsHaveOwnPage) => tickets
        .Where(t => TicketVisibility.CanSee(t, role, userName))
        .Where(t => !fwBugsHaveOwnPage || t.Type != TicketType.FwBug)
        .Count(t => string.CompareOrdinal(t.UpdatedAt, lastSeenAt) > 0);

    /// <summary>Порядок по умолчанию: СНАЧАЛА ТО, ЧЕМ ЗАНЯТЫ, потом то, что ждёт, потом закрытое;
    /// внутри — сначала страшное, потом свежее.
    ///
    /// По дате создания (как было) список отвечал на вопрос «когда заводили», а приходят на него с
    /// вопросом «за что браться». Критичность учитывается до даты, потому что у багов прошивок она
    /// и значит «браться ли сейчас» (см. FwBugSeverity): свежая опечатка в названии режима не
    /// важнее вчерашнего «шкаф не запускается».
    ///
    /// Человек, который щёлкнул по заголовку столбца, получает свой порядок — этот только
    /// начальный (см. TicketsView.ReloadGrid, он сохраняет выбранную сортировку).</summary>
    public static List<Ticket> Order(IEnumerable<Ticket> tickets) => tickets
        .OrderBy(t => TicketStatus.SortOrder(t.Status))
        .ThenBy(t => FwBugSeverity.SortOrder(t.Severity))
        .ThenByDescending(t => DateTime.TryParse(t.CreatedAt, out var dt) ? dt : DateTime.MinValue)
        .ToList();

    /// <summary>Каким цветом строка в списке. Возвращается ИМЯ КИСТИ темы, а не цвет: в приложении
    /// пять тем, и число здесь означало бы, что в «Стекле» список выглядит мусором.
    ///
    /// Два правила, и оба про одно — чтобы по списку было видно, за что браться:
    ///
    /// <b>Закрытый тикет гасится в серый</b> (просьба Ильи: «сделай их по цвету мб серые чтобы можно
    /// было понимать что закрыто что нет»). До этого закрытый красился как обычный текст и в списке
    /// был неотличим от живого — а их в выгрузке 40 из 55. Столбец «Статус» эту разницу, конечно,
    /// показывал, но читать его приходилось построчно; цвет видно всем списком сразу.
    ///
    /// <b>Живой тикет красится по сути беды</b>: предложение — зелёным, баг прошивки — по
    /// критичности (красный/оранжевый/жёлтый), прочий баг — красным. У бага прошивки цвет именно по
    /// критичности, иначе все три уровня выглядели бы одинаково красными и шкала не значила бы ничего.
    ///
    /// Закрытый проверяется ПЕРВЫМ: красная строка о решённой беде — ложная тревога, а от неё
    /// цветные списки и перестают читать.</summary>
    public static string RowBrushKey(Ticket ticket, bool muteClosed = true)
    {
        if (ticket is null) return "TextBrush";
        if (!IsActive(ticket)) return muteClosed ? "TextMutedBrush" : "TextBrush";
        if (ticket.Type == TicketType.Suggestion) return "SuccessBrush";
        if (ticket.Type == TicketType.FwBug)
            return ticket.Severity switch
            {
                FwBugSeverity.Critical => "ErrorBrush",
                FwBugSeverity.Major => "WarningBrush",
                FwBugSeverity.Minor => "CautionBrush",
                _ => "ErrorBrush",
            };
        if (ticket.Type == TicketType.Bug) return "ErrorBrush";
        return "TextBrush";
    }
}
