using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Баг В ПРОШИВКЕ, заведённый жучком на карточке выдачи.
///
/// Просьба владельца дословно: «если в прошивке найден баг, то мы как наладчики нажав на карточке
/// на значек жучка могли бы создать тикет о баге… критичность бага, как красный оранжевый жёлтый,
/// 3 уровня».
///
/// Главное, что здесь проверяется, — не то, что поля сохранились, а то, что они ДОЕЗЖАЮТ до
/// программиста на другой машине. Тикет заводит наладчик на объекте, чинит его другой человек за
/// другим компьютером; критичность, потерянная по дороге, превращает «шкаф не запускается» в
/// безымянную строку в общем списке — ровно та беда, от которой жучок и заводился.
///
/// И отдельно — чем тикет привязан к прошивке. Не локальным id: тикеты ездят между машинами, а id
/// у коллеги свой, и ссылка молча указала бы на чужую версию.</summary>
public class FwBugTicketTests
{
    private static Ticket SampleBug() => new()
    {
        Id = "t-1",
        Type = TicketType.FwBug,
        Text = "Насос не выходит на режим после аварии",
        Status = TicketStatus.Open,
        CreatedBy = "ivanov",
        CreatedByRole = "наладчик",
        CreatedAt = "2026-09-24T10:00:00.000",
        UpdatedAt = "2026-09-24T10:00:00.000",
        Severity = FwBugSeverity.Critical,
        FwSyncId = "fw-sync-abc",
        FwLabel = "НГР 2.0 / КПЧ / ATV310 / 1.74.0",
    };

    [Fact]
    public void Критичность_и_прошивка_доезжают_через_хранилище()
    {
        var json = TicketStorageSync.Serialize(SampleBug());

        var back = TicketStorageSync.TryParse(json);

        Assert.NotNull(back);
        Assert.Equal(TicketType.FwBug, back!.Type);
        Assert.Equal(FwBugSeverity.Critical, back.Severity);
        Assert.Equal("fw-sync-abc", back.FwSyncId);
        Assert.Equal("НГР 2.0 / КПЧ / ATV310 / 1.74.0", back.FwLabel);
    }

    /// <summary>Тип «баг прошивки» не должен схлопываться в «Другое».
    ///
    /// Разбор нарочно терпимый — неизвестный тип приводится к «Другое», чтобы опечатка в файле не
    /// заводила невиданное состояние. Ровно поэтому забыть добавить новый тип в этот список — ошибка
    /// молчаливая: тикеты доедут, но все как «Другое», и вкладка программиста окажется пустой.</summary>
    [Fact]
    public void Тип_бага_прошивки_не_превращается_в_другое()
    {
        var back = TicketStorageSync.TryParse(TicketStorageSync.Serialize(SampleBug()));

        Assert.NotEqual(TicketType.Other, back!.Type);
    }

    /// <summary>Чужая или испорченная критичность не должна поднимать тревогу.
    ///
    /// Файлы в хранилище правят руками. «critical» с опечаткой, приведённое к «критично», подняло бы
    /// мелкую жалобу в самый верх списка программиста — врать в сторону паники хуже, чем промолчать.</summary>
    [Fact]
    public void Неизвестная_критичность_становится_пустой_а_не_критичной()
    {
        var json = TicketStorageSync.Serialize(SampleBug()).Replace("\"critical\"", "\"КРИТИЧНО!!\"");

        var back = TicketStorageSync.TryParse(json);

        Assert.Equal("", back!.Severity);
    }

    /// <summary>Обычный тикет в хранилище выглядит ровно как до появления багов прошивок.
    ///
    /// Пустые поля не пишутся вовсе. Это не экономия места: файлы читают глазами, и три пустые
    /// строки в каждой жалобе на интерфейс — мусор, который со временем начинают не замечать.</summary>
    [Fact]
    public void Обычный_тикет_не_обрастает_пустыми_полями()
    {
        var plain = new Ticket { Id = "t-2", Type = TicketType.Suggestion, Text = "Кнопку бы побольше", CreatedAt = "2026-09-24T10:00:00.000" };

        var json = TicketStorageSync.Serialize(plain);

        Assert.DoesNotContain("severity", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fwSyncId", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Критичность_и_прошивка_переживают_запись_в_базу()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fwbug-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new Database(path))
            {
                db.InsertTicketIfMissing(SampleBug());

                var stored = db.GetTickets().Single(t => t.Id == "t-1");

                Assert.Equal(FwBugSeverity.Critical, stored.Severity);
                Assert.Equal("fw-sync-abc", stored.FwSyncId);
                Assert.Equal("НГР 2.0 / КПЧ / ATV310 / 1.74.0", stored.FwLabel);
            }
        }
        finally { TempDbFiles.Delete(path); }
    }

    /// <summary>Порядок критичности — по тревоге, а не по алфавиту.
    ///
    /// По алфавиту вышло бы «critical, major, minor» случайно правильно, а по-русски «Критичный,
    /// Мелкий, Серьёзный» — уже нет. Порядок задан смыслом: сначала то, из-за чего стоит шкаф.</summary>
    [Fact]
    public void Самое_страшное_сверху()
    {
        Assert.True(FwBugSeverity.SortOrder(FwBugSeverity.Critical) < FwBugSeverity.SortOrder(FwBugSeverity.Major));
        Assert.True(FwBugSeverity.SortOrder(FwBugSeverity.Major) < FwBugSeverity.SortOrder(FwBugSeverity.Minor));
        Assert.True(FwBugSeverity.SortOrder(FwBugSeverity.Minor) < FwBugSeverity.SortOrder(""));
    }

    /// <summary>Баг прошивки — это баг, и красится как баг; предложение — нет.</summary>
    [Fact]
    public void Баг_прошивки_считается_багом()
    {
        Assert.True(TicketType.IsBug(TicketType.FwBug));
        Assert.True(TicketType.IsBug(TicketType.Bug));
        Assert.False(TicketType.IsBug(TicketType.Suggestion));
        Assert.False(TicketType.IsBug(TicketType.Other));
    }
}
