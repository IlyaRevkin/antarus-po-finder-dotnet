using System.Linq;
using AntarusPoFinder.Core.Domain;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Порядок статусов в списке тикетов.
///
/// Тикет: «пересмотреть сортировку тикетов по статусу, сейчас в алфавитном порядке: В работе →
/// Закрыт → Открыт». Закрытые оказывались посередине — между тем, чем заняты, и тем, что ждёт
/// очереди. Нужен порядок по близости к вниманию.</summary>
public class TicketSortOrderTests
{
    [Fact]
    public void Statuses_AreOrderedByAttention_NotAlphabetically()
    {
        var byName = new[] { TicketStatus.Open, TicketStatus.InProgress, TicketStatus.Closed }
            .OrderBy(TicketStatus.Label, System.StringComparer.Ordinal)
            .Select(TicketStatus.Label)
            .ToArray();
        // Так было: закрытые между работой и ожиданием.
        Assert.Equal(new[] { "В работе", "Закрыт", "Открыт" }, byName);

        var byMeaning = new[] { TicketStatus.Closed, TicketStatus.Open, TicketStatus.InProgress }
            .OrderBy(TicketStatus.SortOrder)
            .Select(TicketStatus.Label)
            .ToArray();
        Assert.Equal(new[] { "В работе", "Открыт", "Закрыт" }, byMeaning);
    }

    /// <summary>Неизвестный статус считается открытым — и в подписи, и в порядке. Иначе строка от
    /// более новой версии программы уехала бы в непонятное место списка.</summary>
    [Fact]
    public void UnknownStatus_BehavesLikeOpen()
    {
        Assert.Equal(TicketStatus.SortOrder(TicketStatus.Open), TicketStatus.SortOrder("что-то новое"));
        Assert.Equal(TicketStatus.Label(TicketStatus.Open), TicketStatus.Label("что-то новое"));
    }
}
