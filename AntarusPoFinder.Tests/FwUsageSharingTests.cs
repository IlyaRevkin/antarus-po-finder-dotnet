using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Статистика выборов прошивки стала общей: свой вклад уезжает в общий конфиг, чужой
/// приезжает оттуда и складывается с местным. Здесь — то, что раньше не проверялось вовсе, потому
/// что таблица была чисто локальной: перенос между машинами, отсутствие двойного счёта при повторной
/// синхронизации, переносимость ключа прошивки (локальные id на машинах разные) и сброс.</summary>
public class FwUsageSharingTests
{
    private static int AddVersion(Database db, string subtypeName, int sw, string tags = "НГР")
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == subtypeName);
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");
        return db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix, HwVersion = mod.HwVersion, SwVersion = sw,
            DtStr = $"2026010{sw}_0000", VersionRaw = $"2.1.001.000{sw}.2026010{sw}_0000",
            Filename = "fw.psl", LaunchTypes = new List<string> { "ПЧ" }, Tags = tags, Status = "active",
        });
    }

    /// <summary>Две машины с ОДИНАКОВЫМИ прошивками, но разными локальными id — вклад одной должен
    /// доехать до другой и лечь на правильную версию. Разные id получаются естественно: на второй
    /// машине версии заведены в другом порядке.</summary>
    [Fact]
    public void UsageFromAnotherMachine_LandsOnTheMatchingVersion()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        try
        {
            var aTarget = AddVersion(m.DbA, "КНС", 1);
            AddVersion(m.DbA, "УПД", 2);
            // На B тот же набор, но заведён в обратном порядке — локальные id не совпадают с A.
            AddVersion(m.DbB, "УПД", 2);
            var bTarget = AddVersion(m.DbB, "КНС", 1);
            Assert.NotEqual(aTarget, bTarget);

            var key = SearchService.UsageKey("НГР");
            for (var i = 0; i < 4; i++) m.DbA.RecordFwUsage(key, aTarget);

            ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");
            ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(m.Root.Path), m.Root.Path);

            Assert.Equal(4, m.DbB.GetFwUsageForQuery(key)[bTarget].Uses);
            Assert.Equal(4, m.DbB.GetFwUsageTotal(bTarget));
            // Своя статистика при этом складывается с чужой, а не заменяется ею.
            m.DbB.RecordFwUsage(key, bTarget);
            Assert.Equal(5, m.DbB.GetFwUsageForQuery(key)[bTarget].Uses);
        }
        finally { ConfigSyncService.TransportFactory = r => new FileShareTransport(r); }
    }

    /// <summary>Вклад каждой машины — снимок, а не приращение: сколько бы раз один и тот же конфиг ни
    /// применили, число не растёт. Именно поэтому чужой вклад лежит отдельно от своего, а не
    /// прибавляется к общему счётчику.</summary>
    [Fact]
    public void ReapplyingTheSameSnapshot_DoesNotDoubleCount()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        try
        {
            var a = AddVersion(m.DbA, "КНС", 1);
            var b = AddVersion(m.DbB, "КНС", 1);

            var key = SearchService.UsageKey("НГР");
            for (var i = 0; i < 3; i++) m.DbA.RecordFwUsage(key, a);
            ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");

            var configPath = ConfigSyncService.ConfigPathFor(m.Root.Path);
            ConfigSyncService.Apply(m.SvcB, configPath, m.Root.Path);
            ConfigSyncService.Apply(m.SvcB, configPath, m.Root.Path);
            ConfigSyncService.Apply(m.SvcB, configPath, m.Root.Path);

            Assert.Equal(3, m.DbB.GetFwUsageTotal(b));
        }
        finally { ConfigSyncService.TransportFactory = r => new FileShareTransport(r); }
    }

    /// <summary>Своя строка из чужого снимка игнорируется: источник истины по собственному вкладу —
    /// местная таблица. Иначе устаревшая копия себя же, гулявшая по общему диску, «воскрешала» бы
    /// старые числа поверх свежих.</summary>
    [Fact]
    public void OwnContributionComingBackFromTheShare_IsIgnored()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = AddVersion(db, "КНС", 1);
        var key = SearchService.UsageKey("НГР");
        db.RecordFwUsage(key, id);

        var self = db.UsageOriginId();
        var mine = Assert.Single(db.ExportFwUsage(self));
        // Вернувшийся собственный вклад, да ещё с завышенным числом — не должен ничего изменить.
        db.ImportFwUsage(new[] { mine with { Uses = 99 } }, self);

        Assert.Equal(1, db.GetFwUsageTotal(id));
    }

    [Fact]
    public void Export_CarriesForeignContributionsOnward()
    {
        // Общий конфиг переписывается целиком: если бы машина выгружала только свой вклад, вклад
        // остальных исчезал бы из снимка после первой же её отправки.
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        AddVersion(db, "КНС", 1);

        db.ImportFwUsage(new[] { ForeignRow(db, 6) }, db.UsageOriginId());

        Assert.Contains(db.ExportFwUsage(db.UsageOriginId()), r => r.Origin == "чужая-машина" && r.Uses == 6);
    }

    [Fact]
    public void Reset_ClearsBothOwnAndForeignContributions()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = AddVersion(db, "КНС", 1);
        db.RecordFwUsage(SearchService.UsageKey("НГР"), id);
        db.ImportFwUsage(new[] { ForeignRow(db, 6) }, db.UsageOriginId());
        Assert.True(db.TotalFwUsageCount() > 1);

        db.ResetAllFwUsage();

        Assert.Equal(0, db.TotalFwUsageCount());
        Assert.Equal(0, db.GetFwUsageTotal(id));
    }

    /// <summary>Строка «как будто от другой машины» для той же прошивки, что заводит AddVersion(КНС, 1) —
    /// с переносимыми ключами, ровно как её прислал бы общий конфиг.</summary>
    private static SharedFwUsageRow ForeignRow(Database db, int uses)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");
        var ctrlSyncId = db.GetAllControllerModels().First(c => c.Id == mod.ControllerId).SyncId;
        return new SharedFwUsageRow("чужая-машина", SearchService.UsageKey("НГР"),
            subtype.SyncId, ctrlSyncId, "2.1.001.0001.20260101_0000", uses, "");
    }

    /// <summary>Сброс, сделанный на одной машине, обязан дойти до остальных: пустой чужой снимок сам
    /// по себе ничего не удаляет (он означает «мне нечего добавить»), обнуляет именно отметка времени
    /// сброса, приехавшая в настройках.</summary>
    [Fact]
    public void ResetOnOneMachine_ReachesTheOther()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        try
        {
            var b = AddVersion(m.DbB, "КНС", 1);
            m.DbB.RecordFwUsage(SearchService.UsageKey("НГР"), b);
            Assert.Equal(1, m.DbB.GetFwUsageTotal(b));

            var now = "2026-07-24T12:00:00";
            m.DbA.ResetAllFwUsage();
            m.CfgA.SetFwUsageResetAt(now);
            m.CfgA.SetFwUsageResetAppliedAt(now);
            ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");
            ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(m.Root.Path), m.Root.Path);

            Assert.Equal(0, m.DbB.GetFwUsageTotal(b));
            Assert.Equal(now, m.CfgB.FwUsageResetAppliedAt());
        }
        finally { ConfigSyncService.TransportFactory = r => new FileShareTransport(r); }
    }

    // ── «Это та прошивка, которую вы искали?» ────────────────────────────────

    [Fact]
    public void ConfirmPrompt_StartsByAsking()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        Assert.Equal(UsageConfirmDecision.Ask, db.GetFwUsageConfirmDecision());
    }

    [Fact]
    public void ConsistentYes_StopsAsking_AndKeepsCounting()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        for (var i = 0; i < Database.UsageConfirmDecisionThreshold; i++)
        {
            Assert.Equal(UsageConfirmDecision.Ask, db.GetFwUsageConfirmDecision());
            db.RecordFwUsageConfirmFeedback(confirmed: true);
        }

        Assert.Equal(UsageConfirmDecision.Always, db.GetFwUsageConfirmDecision());
    }

    [Fact]
    public void ConsistentNo_StopsAskingAndStopsCounting()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        for (var i = 0; i < Database.UsageConfirmDecisionThreshold; i++)
            db.RecordFwUsageConfirmFeedback(confirmed: false);

        Assert.Equal(UsageConfirmDecision.Never, db.GetFwUsageConfirmDecision());
    }

    [Fact]
    public void MixedAnswers_KeepAsking()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        db.RecordFwUsageConfirmFeedback(confirmed: true);
        db.RecordFwUsageConfirmFeedback(confirmed: false);
        db.RecordFwUsageConfirmFeedback(confirmed: true);

        Assert.Equal(UsageConfirmDecision.Ask, db.GetFwUsageConfirmDecision());
    }

    [Fact]
    public void ResetLearning_BringsTheQuestionBack()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        db.SetFwUsageConfirmDecision(UsageConfirmDecision.Never);
        Assert.Equal(UsageConfirmDecision.Never, db.GetFwUsageConfirmDecision());

        db.ResetFwUsageConfirmLearning();

        Assert.Equal(UsageConfirmDecision.Ask, db.GetFwUsageConfirmDecision());
    }
}
