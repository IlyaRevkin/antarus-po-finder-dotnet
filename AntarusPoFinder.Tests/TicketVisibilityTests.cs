using AntarusPoFinder.Core.Domain;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Кто видит баги прошивок.
///
/// Просьба владельца дословно: «вкладка программиста». Смысл её в том, что баг прошивки заводит
/// один человек, а чинит другой: по прежнему правилу «свои тикеты видит только автор» программист
/// не увидел бы чужую жалобу никогда, и жучок на карточке отправлял бы её в никуда.</summary>
public class TicketVisibilityTests
{
    private static Ticket FwBug(string author = "ivanov") => new()
    { Id = "1", Type = TicketType.FwBug, CreatedBy = author, Severity = FwBugSeverity.Critical };

    private static Ticket AppBug(string author = "ivanov") => new()
    { Id = "2", Type = TicketType.Bug, CreatedBy = author };

    [Fact]
    public void Программист_видит_чужой_баг_прошивки()
    {
        Assert.True(TicketVisibility.CanSee(FwBug("ivanov"), TicketVisibility.Programmer, "petrov"));
    }

    /// <summary>Расширение прав — только на баги прошивок. Чужие жалобы на интерфейс программист
    /// по-прежнему не видит: об этом никто не просил, а тихо расширенные права потом никто не
    /// сужает.</summary>
    [Fact]
    public void Программисту_не_показывают_заодно_чужие_жалобы_на_программу()
    {
        Assert.False(TicketVisibility.CanSee(AppBug("ivanov"), TicketVisibility.Programmer, "petrov"));
    }

    [Fact]
    public void Свой_тикет_видит_автор_в_любой_роли()
    {
        Assert.True(TicketVisibility.CanSee(AppBug("petrov"), TicketVisibility.Programmer, "petrov"));
        Assert.True(TicketVisibility.CanSee(AppBug("petrov"), "naladchik", "PETROV"));
    }

    [Fact]
    public void Администратор_видит_всё()
    {
        Assert.True(TicketVisibility.CanSee(AppBug("ivanov"), TicketVisibility.Administrator, "petrov"));
        Assert.True(TicketVisibility.CanSee(FwBug("ivanov"), TicketVisibility.Administrator, "petrov"));
    }

    [Fact]
    public void Наладчик_не_видит_чужой_баг_прошивки()
    {
        Assert.False(TicketVisibility.CanSee(FwBug("ivanov"), "naladchik", "petrov"));
    }

    /// <summary>Закрыть баг прошивки может тот, кто его чинит, а не тот, кто нашёл.</summary>
    [Fact]
    public void Статус_бага_прошивки_меняет_программист_а_не_автор()
    {
        Assert.True(TicketVisibility.CanModerate(FwBug(), TicketVisibility.Programmer));
        Assert.False(TicketVisibility.CanModerate(FwBug(), "naladchik"));
        Assert.False(TicketVisibility.CanModerate(AppBug(), TicketVisibility.Programmer));
    }
}
