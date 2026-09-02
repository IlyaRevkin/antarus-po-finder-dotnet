using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Автоматические отчёты о сбоях среди тикетов.
///
/// Тикет kiselyov.a: «Убрать из общих тикетов или добавить фильтр для возможности отключения
/// отображения автоматически сформированных отчётов (по умолчанию автоотчёты скрыты, для более
/// читаемого просмотра предложенных тем)». На момент жалобы их было 7 из 28 — четверть списка, и
/// каждый со стектрейсом в поле «Текст».
///
/// Убирать совсем нельзя: отчёт о падении это единственный след аварии, доехавший до
/// администратора без похода на чужой компьютер (см. App.ReportCrashAsTicket). Поэтому не удаление,
/// а отбор с показом по требованию.</summary>
public static class TicketAutoReports
{
    /// <summary>Роль, которой помечен автор автоотчёта. Настоящий пользователь Windows остаётся в
    /// CreatedBy — по нему видно, у кого именно упало.</summary>
    public const string SystemRole = "system";

    /// <summary>Начало текста автоотчёта. Живёт здесь, а не в App.ReportCrashAsTicket, чтобы
    /// «как пишем» и «как узнаём» нельзя было разъехать: поменяли фразу в одном месте — отбор
    /// перестал бы находить старые отчёты молча.</summary>
    public const string TextPrefix = "[Автоматический отчёт о сбое]";

    /// <summary>Автоотчёт ли это.
    ///
    /// ⚠️ Двумя признаками, а не одним. Роль — основной и надёжный, но она приезжает в тикете с
    /// чужой машины и у совсем старых записей могла не проставиться; текст — запасной. Ложное
    /// срабатывание тут дешевле пропуска: человек, который сам начнёт тикет с этой фразы, всего
    /// лишь увидит его под галкой «показать автоотчёты».</summary>
    public static bool IsAutoReport(Ticket ticket) =>
        string.Equals(ticket.CreatedByRole, SystemRole, StringComparison.OrdinalIgnoreCase) ||
        ticket.Text.TrimStart().StartsWith(TextPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Что показывать в списке. <paramref name="showAutoReports"/> = false (умолчание в
    /// интерфейсе) — только живые темы от людей.</summary>
    public static List<Ticket> Visible(IEnumerable<Ticket> tickets, bool showAutoReports) =>
        showAutoReports ? tickets.ToList() : tickets.Where(t => !IsAutoReport(t)).ToList();

    /// <summary>Сколько автоотчётов спрятано — число рядом с галкой. Без него «скрыты» выглядит как
    /// «потерялись»: человек не знает, есть ли там вообще что-нибудь.</summary>
    public static int Count(IEnumerable<Ticket> tickets) => tickets.Count(IsAutoReport);
}
