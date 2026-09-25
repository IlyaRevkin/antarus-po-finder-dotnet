namespace AntarusPoFinder.Core.Domain;

/// <summary>Кто какие тикеты видит и кому можно менять им статус.
///
/// Правило было простым: администратор видит всё, остальные — только свои. Для жалоб на программу
/// это верно, а для багов В ПРОШИВКЕ ломается сразу: баг находит наладчик на объекте, а чинит его
/// программист за другим компьютером. По прежнему правилу программист не увидел бы такой тикет
/// никогда — он же не его автор, — и жучок на карточке оказался бы кнопкой, отправляющей жалобу
/// в никуда.
///
/// Поэтому исключение ровно одно и ровно для багов прошивок: программист видит их все, чьи бы они
/// ни были, и может ими распоряжаться. Жалобы на саму программу он по-прежнему видит только свои —
/// расширять заодно и их значило бы менять договорённость, о которой никто не просил.</summary>
public static class TicketVisibility
{
    public const string Administrator = "administrator";
    public const string Programmer = "programmer";
    public const string Naladchik = "naladchik";

    /// <summary>Баги прошивок видят ВСЕ три роли, и каждая — целиком, чьи бы они ни были.
    ///
    /// Программист и администратор — потому что чинят. Наладчик — ради отслеживания: едет на объект
    /// с той же прошивкой и обязан заранее знать, что за ней уже числится. Показывать ему только СВОИ
    /// жалобы значило бы, что двое наладчиков независимо найдут один и тот же баг и заведут его дважды.
    ///
    /// Жалобы на саму программу — по-прежнему только свои (кроме администратора).</summary>
    public static bool CanSee(Ticket t, string role, string userName) =>
        role == Administrator ||
        t.Type == TicketType.FwBug ||
        string.Equals(t.CreatedBy, userName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Кто может менять статус.
    ///
    /// Администратор — кому угодно. Программист — любому багу прошивки: он их чинит.
    ///
    /// Наладчик — только СВОЕМУ багу прошивки, и ровно затем, зачем просили: «закрыть,
    /// если ошибочно баг». Закрывать ЧУЖУЮ жалобу он не может: «закрыто» там означало бы
    /// «починено», а решает это тот, кто чинит — иначе найденный на объекте баг можно было бы убрать с
    /// глаз до того, как его увидел программист.</summary>
    public static bool CanModerate(Ticket t, string role, string userName = "") =>
        role == Administrator ||
        (role == Programmer && t.Type == TicketType.FwBug) ||
        (role == Naladchik && t.Type == TicketType.FwBug &&
         string.Equals(t.CreatedBy, userName, StringComparison.OrdinalIgnoreCase));

    public static List<Ticket> Visible(IEnumerable<Ticket> all, string role, string userName) =>
        all.Where(t => CanSee(t, role, userName)).ToList();
}
