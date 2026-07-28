using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Ручной вес выдачи, отделённый от авто-счётчика открытий (Database.FwUsage.cs weight), и
/// синхронизируемый множитель популярности (ConfigService.FwUsageMultiplier, Database.Search.cs
/// AutoUsageBonus). Раньше «ручной вес» и «счётчик» были одним числом (uses) и правка веса стирала
/// статистику; теперь они складываются. Множитель задаёт, насколько сильно частота двигает выдачу.</summary>
public class FwWeightAndMultiplierTests
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

    // ── Разделение веса и счётчика ────────────────────────────────────────

    /// <summary>Главное отличие от прежней схемы: ручной вес и счётчик открытий — два независимых
    /// числа на одной строке, и они СКЛАДЫВАЮТСЯ, а не перезатирают друг друга.</summary>
    [Fact]
    public void ManualWeightAndAutoUses_AreSeparateAndAddUp()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var id = AddVersion(db, "КНС", 1);
        var key = SearchService.UsageKey("НГР");

        for (var i = 0; i < 2; i++) db.RecordFwUsage(key, id); // счётчик открытий = 2
        db.SetLocalFwWeight(key, id, 3);                        // ручной вес = 3

        var (uses, weight) = db.GetFwUsageForQuery(key)[id];
        Assert.Equal(2, uses);
        Assert.Equal(3, weight);
    }

    /// <summary>Правка одного числа не задевает второе: снять вес — счётчик на месте; обнулить
    /// счётчик — вес на месте (строка выживает, пока на ней есть хоть одно ненулевое число).</summary>
    [Fact]
    public void EditingOne_LeavesTheOtherIntact()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var id = AddVersion(db, "КНС", 1);
        var key = SearchService.UsageKey("НГР");

        db.RecordFwUsage(key, id);
        db.SetLocalFwWeight(key, id, 4);

        db.SetLocalFwWeight(key, id, 0); // снять только вес
        Assert.Equal(1, db.GetFwUsageForQuery(key)[id].Uses);
        Assert.Equal(0, db.GetFwUsageForQuery(key)[id].Weight);

        db.SetLocalFwWeight(key, id, 4); // вернуть вес
        db.SetLocalFwUsage(key, id, 0);  // обнулить только счётчик
        Assert.Equal(0, db.GetFwUsageForQuery(key)[id].Uses);
        Assert.Equal(4, db.GetFwUsageForQuery(key)[id].Weight);

        // Снять и то, и другое — строка исчезает совсем.
        db.SetLocalFwWeight(key, id, 0);
        Assert.False(db.GetFwUsageForQuery(key).ContainsKey(id));
    }

    /// <summary>Ручной вес показывается в редакторе модерации (GetFwUsageQueriesForVersion), а
    /// счётчик открытий — нет: это разные вещи, и вес правится отдельно от статистики.</summary>
    [Fact]
    public void ModerationEditor_ShowsWeightRowsOnly()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var id = AddVersion(db, "КНС", 1);
        var withWeight = SearchService.UsageKey("НГР");
        var usesOnly = SearchService.UsageKey("КНС");

        db.SetLocalFwWeight(withWeight, id, 5);
        db.RecordFwUsage(usesOnly, id); // только счётчик, без веса

        var rows = db.GetFwUsageQueriesForVersion(id);
        var row = Assert.Single(rows);
        Assert.Equal(withWeight, row.QueryKey);
        Assert.Equal(5, row.Weight);
    }

    // ── Влияние веса на ранжирование ──────────────────────────────────────

    /// <summary>Ручной вес поднимает версию в выдаче и, в отличие от счётчика, НЕ ограничен порогом:
    /// заданный вес действует сразу, даже при высоком пороге статистики.</summary>
    [Fact]
    public void ManualWeight_OutranksFresherVersion_IgnoringThreshold()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var older = AddVersion(db, "КНС", 1);
        var newer = AddVersion(db, "УПД", 2);

        var key = SearchService.UsageKey("НГР");
        db.SetLocalFwWeight(key, older, 4);

        // Даже с заведомо недостижимым порогом статистики вес двигает выдачу — порог его не касается.
        var hits = SearchService.Search(db, "НГР", usageThreshold: 100);
        Assert.Equal(older, hits[0].FwVersionId);
    }

    // ── Множитель популярности ────────────────────────────────────────────

    /// <summary>Множитель масштабирует вклад счётчика: при 1 ручной вес соперника перевешивает
    /// частоту, при 2 та же частота обходит тот же вес — порядок выдачи меняется от одного множителя.</summary>
    [Fact]
    public void Multiplier_ScalesPopularityAgainstManualWeight()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var popular = AddVersion(db, "КНС", 1);   // берут часто
        var weighted = AddVersion(db, "УПД", 2);  // поднят вручную

        var key = SearchService.UsageKey("НГР");
        for (var i = 0; i < 3; i++) db.RecordFwUsage(key, popular); // счётчик 3
        db.SetLocalFwWeight(key, weighted, 4);                      // вес 4

        // Множитель 1: вклад частоты 3 < вес 4 — сверху версия с ручным весом.
        var atOne = SearchService.Search(db, "НГР", usageThreshold: 1, usageMultiplier: 1);
        Assert.Equal(weighted, atOne[0].FwVersionId);

        // Множитель 2: вклад частоты 3×2=6 > вес 4 — теперь сверху популярная.
        var atTwo = SearchService.Search(db, "НГР", usageThreshold: 1, usageMultiplier: 2);
        Assert.Equal(popular, atTwo[0].FwVersionId);
    }

    /// <summary>Множитель 0 полностью отключает влияние счётчика открытий, оставляя ранжирование на
    /// релевантности и ручном весе — популярная версия перестаёт всплывать, а поднятая вручную нет.</summary>
    [Fact]
    public void MultiplierZero_DisablesPopularity_ButNotManualWeight()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var popular = AddVersion(db, "КНС", 1);
        var weighted = AddVersion(db, "УПД", 2);

        var key = SearchService.UsageKey("НГР");
        for (var i = 0; i < 5; i++) db.RecordFwUsage(key, popular);
        db.SetLocalFwWeight(key, weighted, 1);

        var hits = SearchService.Search(db, "НГР", usageThreshold: 1, usageMultiplier: 0);
        Assert.Equal(weighted, hits[0].FwVersionId);
    }

    // ── Настройки ──────────────────────────────────────────────────────────

    [Fact]
    public void ConfigService_Multiplier_DefaultsRoundTripsAndClamps()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        Assert.Equal(1, cfg.FwUsageMultiplier());
        Assert.Equal(5, cfg.FwUsageMaxAutoBonus()); // 5×1

        cfg.SetFwUsageMultiplier(1.5); // дробное — и читается инвариантно, не зависит от локали
        Assert.Equal(1.5, cfg.FwUsageMultiplier());

        cfg.SetFwUsageMultiplier(2);
        Assert.Equal(10, cfg.FwUsageMaxAutoBonus());

        cfg.SetFwUsageMultiplier(-3); // отрицательный множитель бессмыслен — поджимается к 0
        Assert.Equal(0, cfg.FwUsageMultiplier());
        Assert.Equal(0, cfg.FwUsageMaxAutoBonus());
    }

    [Fact]
    public void ConfigService_WeightShared_DefaultsFalseAndRoundTrips()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        Assert.False(cfg.FwWeightShared());
        cfg.SetFwWeightShared(true);
        Assert.True(cfg.FwWeightShared());
    }

    // ── Обмен весом между машинами ─────────────────────────────────────────

    /// <summary>Свой ручной вес уезжает в экспорт только при включённой «делиться весом»: выключено —
    /// в снимке вес нулевой (счётчик открытий уезжает всё равно), включено — реальный.</summary>
    [Fact]
    public void OwnWeight_ExportedOnlyWhenSharingEnabled()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        var id = AddVersion(db, "КНС", 1);
        var key = SearchService.UsageKey("НГР");
        db.SetLocalFwWeight(key, id, 7);

        cfg.SetFwWeightShared(false);
        var off = db.ExportHierarchyData().FwUsage!.Single(u => u.QueryKey == key);
        Assert.Equal(0, off.Weight);

        cfg.SetFwWeightShared(true);
        var on = db.ExportHierarchyData().FwUsage!.Single(u => u.QueryKey == key);
        Assert.Equal(7, on.Weight);
    }

    /// <summary>Сквозная проверка: включённый на A вес доезжает до B через общий конфиг и
    /// складывается с собственным весом B на той же версии.</summary>
    [Fact]
    public void SharedWeight_LandsOnMatchingVersionAndAddsToLocal()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        try
        {
            var aTarget = AddVersion(m.DbA, "КНС", 1);
            AddVersion(m.DbB, "УПД", 2);
            var bTarget = AddVersion(m.DbB, "КНС", 1);

            var key = SearchService.UsageKey("НГР");
            m.DbA.SetLocalFwWeight(key, aTarget, 6);
            m.CfgA.SetFwWeightShared(true);
            m.DbB.SetLocalFwWeight(key, bTarget, 2); // у B свой вес на той же версии

            ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");
            ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(m.Root.Path), m.Root.Path);

            // 2 своих + 6 приехавших с A.
            Assert.Equal(8, m.DbB.GetFwUsageForQuery(key)[bTarget].Weight);
        }
        finally { ConfigSyncService.TransportFactory = r => new FileShareTransport(r); }
    }
}
