using System;
using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>СТРАНИЦА ТИКЕТОВ ОТВЕЧАЕТ НА ВОПРОС «ЗА ЧТО БРАТЬСЯ».
///
/// В выгрузке на 29.10.2026 — 55 тикетов, закрытых 40. Список строился «по дате создания, новые
/// сверху», и пятнадцать живых тем были размазаны между сорока сделанными. Здесь закреплено то,
/// что из этого следует: закрытые убираются с глаз (но считаются), порядок идёт от состояния, а не
/// от даты, и число на вкладке означает «сколько ещё чинить».</summary>
public class TicketBoardTests
{
    private static Ticket T(string status, string type = TicketType.Bug, string created = "2026-10-01T10:00:00",
        string severity = "", string role = "naladchik", string text = "жалоба") => new()
        {
            Id = Guid.NewGuid().ToString(),
            Type = type,
            Status = status,
            Severity = severity,
            CreatedAt = created,
            UpdatedAt = created,
            CreatedBy = "kiselyov.a",
            CreatedByRole = role,
            Text = text,
        };

    [Fact]
    public void Закрытые_не_считаются_живыми()
    {
        var all = new List<Ticket>
        {
            T(TicketStatus.Open), T(TicketStatus.InProgress), T(TicketStatus.Closed), T(TicketStatus.Closed),
        };

        Assert.Equal(2, TicketBoard.ActiveCount(all));
        Assert.Equal(2, TicketBoard.ClosedCount(all));
    }

    [Fact]
    public void Без_галки_закрытых_в_списке_нет()
    {
        var all = new List<Ticket> { T(TicketStatus.Open), T(TicketStatus.Closed) };

        var hidden = TicketBoard.Visible(all, showAutoReports: false, showClosed: false);
        var shown = TicketBoard.Visible(all, showAutoReports: false, showClosed: true);

        Assert.Single(hidden);
        Assert.Equal(TicketStatus.Open, hidden[0].Status);
        Assert.Equal(2, shown.Count);
    }

    /// <summary>Две галки не должны обманывать друг друга: число закрытых считается по тому, что
    /// человек в принципе видит, — иначе «показать закрытые» обещало бы больше, чем покажет.</summary>
    [Fact]
    public void Автоотчёты_отсеиваются_раньше_закрытых()
    {
        var all = new List<Ticket>
        {
            T(TicketStatus.Closed),
            T(TicketStatus.Closed, role: TicketAutoReports.SystemRole, text: TicketAutoReports.TextPrefix + " упало"),
        };

        var withoutAuto = TicketBoard.Visible(all, showAutoReports: false, showClosed: true);
        var withAuto = TicketBoard.Visible(all, showAutoReports: true, showClosed: true);

        Assert.Single(withoutAuto);
        Assert.Equal(2, withAuto.Count);
    }

    /// <summary>Порядок: сперва то, чем заняты, потом то, что ждёт, потом закрытое.</summary>
    [Fact]
    public void Порядок_идёт_от_состояния_а_не_от_даты()
    {
        var all = new List<Ticket>
        {
            T(TicketStatus.Closed, created: "2026-10-28T10:00:00"),
            T(TicketStatus.Open, created: "2026-09-01T10:00:00"),
            T(TicketStatus.InProgress, created: "2026-08-01T10:00:00"),
        };

        var ordered = TicketBoard.Order(all).Select(t => t.Status).ToList();

        Assert.Equal(new[] { TicketStatus.InProgress, TicketStatus.Open, TicketStatus.Closed }, ordered);
    }

    /// <summary>Внутри одного состояния страшное выше свежего: вчерашнее «шкаф не запускается»
    /// важнее сегодняшней опечатки в названии режима.</summary>
    [Fact]
    public void Критичность_важнее_свежести()
    {
        var all = new List<Ticket>
        {
            T(TicketStatus.Open, TicketType.FwBug, "2026-10-28T10:00:00", FwBugSeverity.Minor),
            T(TicketStatus.Open, TicketType.FwBug, "2026-10-01T10:00:00", FwBugSeverity.Critical),
        };

        var ordered = TicketBoard.Order(all).Select(t => t.Severity).ToList();

        Assert.Equal(new[] { FwBugSeverity.Critical, FwBugSeverity.Minor }, ordered);
    }

    /// <summary>При равном состоянии и критичности — новое сверху: прежняя привычка списка.</summary>
    [Fact]
    public void При_прочих_равных_новое_сверху()
    {
        var all = new List<Ticket>
        {
            T(TicketStatus.Open, created: "2026-09-01T10:00:00"),
            T(TicketStatus.Open, created: "2026-10-20T10:00:00"),
        };

        var ordered = TicketBoard.Order(all).Select(t => t.CreatedAt).ToList();

        Assert.Equal(new[] { "2026-10-20T10:00:00", "2026-09-01T10:00:00" }, ordered);
    }

    /// <summary>Бейдж на пункте меню считается ТЕМ ЖЕ правилом доступа, что и страница. Своя копия
    /// правила здесь уже жила и уже разъехалась: чужой баг прошивки наладчик на странице видел, а в
    /// цифре — нет.</summary>
    [Fact]
    public void Чужой_баг_прошивки_попадает_в_счётчик_наладчика()
    {
        var foreign = T(TicketStatus.Open, TicketType.FwBug, "2026-10-20T10:00:00");
        foreign.CreatedBy = "belyi.a";
        var all = new List<Ticket> { foreign };

        var unseen = TicketBoard.UnseenCount(all, TicketVisibility.Naladchik, "kiselyov.a",
            "2026-10-01T00:00:00", fwBugsHaveOwnPage: false);

        Assert.Equal(1, unseen);
    }

    /// <summary>Там, где у багов прошивок свой пункт меню со своим счётчиком, второй раз их не
    /// считаем: одно и то же число в двух местах заставляет искать новое дважды.</summary>
    [Fact]
    public void Баги_прошивок_не_считаются_дважды()
    {
        var all = new List<Ticket>
        {
            T(TicketStatus.Open, TicketType.FwBug, "2026-10-20T10:00:00"),
            T(TicketStatus.Open, created: "2026-10-20T10:00:00"),
        };

        Assert.Equal(1, TicketBoard.UnseenCount(all, TicketVisibility.Administrator, "ilia",
            "2026-10-01T00:00:00", fwBugsHaveOwnPage: true));
        Assert.Equal(2, TicketBoard.UnseenCount(all, TicketVisibility.Administrator, "ilia",
            "2026-10-01T00:00:00", fwBugsHaveOwnPage: false));
    }

    /// <summary>Просмотренное не считается: отметка времени двигается при каждом открытии
    /// страницы.</summary>
    [Fact]
    public void Старое_в_счётчик_не_идёт()
    {
        var all = new List<Ticket> { T(TicketStatus.Open, created: "2026-09-01T10:00:00") };

        Assert.Equal(0, TicketBoard.UnseenCount(all, TicketVisibility.Administrator, "ilia",
            "2026-10-01T00:00:00", fwBugsHaveOwnPage: false));
    }

    /// <summary>Неразобранная дата не притворяется сегодняшней и уезжает вниз — то же правило, что
    /// у столбца «Создан» (TicketsView.TicketRow.CreatedAtSort).</summary>
    [Fact]
    public void Битая_дата_уезжает_вниз()
    {
        var all = new List<Ticket>
        {
            T(TicketStatus.Open, created: "непонятно что"),
            T(TicketStatus.Open, created: "2026-01-01T10:00:00"),
        };

        Assert.Equal("2026-01-01T10:00:00", TicketBoard.Order(all)[0].CreatedAt);
    }
}
