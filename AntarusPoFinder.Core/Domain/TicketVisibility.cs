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

    public static bool CanSee(Ticket t, string role, string userName) =>
        role == Administrator ||
        (role == Programmer && t.Type == TicketType.FwBug) ||
        string.Equals(t.CreatedBy, userName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Менять статус может администратор кому угодно, а программист — только багам
    /// прошивок. Своей жалобе на интерфейс автор статус не меняет: «закрыть» значит «починено», и
    /// решает это тот, кто чинит.</summary>
    public static bool CanModerate(Ticket t, string role) =>
        role == Administrator || (role == Programmer && t.Type == TicketType.FwBug);

    public static List<Ticket> Visible(IEnumerable<Ticket> all, string role, string userName) =>
        all.Where(t => CanSee(t, role, userName)).ToList();
}
