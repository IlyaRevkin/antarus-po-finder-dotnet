using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Тикет kiselyov.a: «Убрать из общих тикетов или добавить фильтр для возможности
/// отключения отображения автоматически сформированных отчётов (по умолчанию автоотчёты скрыты, для
/// более читаемого просмотра предложенных тем)».
///
/// Отчёты о падениях программа заводит сама (App.ReportCrashAsTicket) — их было 7 из 28, и каждый
/// со стектрейсом в поле «Текст». Удалять их нельзя: для администратора это единственный след
/// аварии, не требующий похода на чужой компьютер. Поэтому отбор.</summary>
public class TicketAutoReportsTests
{
    private static Ticket Auto(string id = "a") => new()
    {
        Id = id,
        Type = TicketType.Bug,
        Text = TicketAutoReports.TextPrefix + "\nSystem.NullReferenceException: ...",
        CreatedBy = "ivanov",
        CreatedByRole = TicketAutoReports.SystemRole,
    };

    private static Ticket Human(string id, string text) => new()
    {
        Id = id,
        Type = TicketType.Suggestion,
        Text = text,
        CreatedBy = "ivanov",
        CreatedByRole = "naladchik",
    };

    [Fact]
    public void HiddenByDefault_HumanTicketsStay()
    {
        var all = new List<Ticket>
        {
            Auto("1"),
            Human("2", "Хочется поиск по тегам"),
            Auto("3"),
            Human("4", "Кнопка не влезает"),
        };

        var visible = TicketAutoReports.Visible(all, showAutoReports: false);

        Assert.Equal(2, visible.Count);
        Assert.All(visible, t => Assert.False(TicketAutoReports.IsAutoReport(t)));
        Assert.Equal(2, TicketAutoReports.Count(all));
    }

    [Fact]
    public void ShowingThem_GivesEverythingBackInTheSameOrder()
    {
        var all = new List<Ticket> { Auto("1"), Human("2", "тема"), Auto("3") };

        var visible = TicketAutoReports.Visible(all, showAutoReports: true);

        Assert.Equal(new[] { "1", "2", "3" }, visible.Select(t => t.Id));
    }

    /// <summary>Роль — основной признак: по ней автоотчёт узнаётся даже если текст когда-нибудь
    /// перепишут.</summary>
    [Fact]
    public void RoleAlone_IsEnough()
    {
        var ticket = Human("5", "любой текст без пометки");
        ticket.CreatedByRole = TicketAutoReports.SystemRole;

        Assert.True(TicketAutoReports.IsAutoReport(ticket));
    }

    /// <summary>Текст — запасной признак: у старых записей и у приехавших с чужой машины роль могла
    /// не проставиться, а начало текста никуда не девалось.</summary>
    [Fact]
    public void TextPrefixAlone_IsEnough()
    {
        var ticket = Human("6", TicketAutoReports.TextPrefix + "\nSystem.IO.IOException: ...");

        Assert.True(TicketAutoReports.IsAutoReport(ticket));
    }

    /// <summary>Обычный тикет человека автоотчётом не считается — даже если в нём есть слово
    /// «отчёт».</summary>
    [Fact]
    public void OrdinaryTicket_IsNotAnAutoReport()
    {
        Assert.False(TicketAutoReports.IsAutoReport(Human("7", "Сделайте отчёт по прошивкам за месяц")));
        Assert.False(TicketAutoReports.IsAutoReport(Human("8", "")));
    }

    /// <summary>Ничего не спрятано — счётчик ноль, и галке нечего показывать.</summary>
    [Fact]
    public void NoAutoReports_CountIsZero()
    {
        var all = new List<Ticket> { Human("1", "раз"), Human("2", "два") };

        Assert.Equal(0, TicketAutoReports.Count(all));
        Assert.Equal(2, TicketAutoReports.Visible(all, showAutoReports: false).Count);
    }
}
