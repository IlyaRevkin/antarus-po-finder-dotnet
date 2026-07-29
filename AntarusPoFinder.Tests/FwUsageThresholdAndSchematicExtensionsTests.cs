using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Две независимые задачи из одного захода: порог статистики выборов, влияющий на
/// ранжирование поиска (Database.Search.cs — EffectiveUsage/Rank, ConfigService.FwUsageThreshold),
/// и настраиваемый список расширений поиска схем на втором диске (allowed_extensions_schematic,
/// Database.Params.cs, SchematicService) — раньше был захардкожен в SchematicService.SchematicExtensions.</summary>
public class FwUsageThresholdAndSchematicExtensionsTests
{
    // ── Порог статистики выборов ──────────────────────────────────────────

    private static int AddVersion(Database db, string subtypeName, int sw, string tags)
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

    /// <summary>Без порога (или с порогом 1, как раньше) два выбора уже поднимали бы версию выше
    /// более свежей записи с тем же счётом релевантности. С порогом 3 те же два выбора не должны
    /// сдвигать порядок вовсе — ниже порога бонус за частоту не начисляется.</summary>
    [Fact]
    public void UsageBelowThreshold_DoesNotOutrankAnUnusedVersion()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var older = AddVersion(db, "КНС", 1, "НГР");
        var newer = AddVersion(db, "УПД", 2, "НГР");

        var key = SearchService.UsageKey("НГР");
        for (var i = 0; i < 2; i++) db.RecordFwUsage(key, older); // 2 выбора

        // Порог по умолчанию (1) — двух выборов достаточно, чтобы обогнать более свежую запись.
        var defaultHits = SearchService.Search(db, "НГР");
        Assert.Equal(older, defaultHits[0].FwVersionId);

        // Порог 3 — двух выборов НЕ достаточно: бонус за частоту не начисляется, порядок при
        // равном счёте определяет обычный тай-брейк (свежая запись, больший id).
        var thresholdHits = SearchService.Search(db, "НГР", usageThreshold: 3);
        Assert.Equal(newer, thresholdHits[0].FwVersionId);

        // Сырое число выборов при этом видно как есть — порог обрезает только влияние на порядок,
        // а не саму цифру на карточке («по этому запросу выбирали N раз»).
        Assert.Equal(2, thresholdHits.Single(h => h.FwVersionId == older).UsageCount);
    }

    /// <summary>Симметричная проверка: набравшая ровно порог частота ДОЛЖНА двигать выдачу — порог
    /// это «не меньше», а не «строго больше».</summary>
    [Fact]
    public void UsageAtOrAboveThreshold_StillAffectsRanking()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var chosen = AddVersion(db, "КНС", 1, "НГР");
        AddVersion(db, "УПД", 2, "НГР");

        var key = SearchService.UsageKey("НГР");
        for (var i = 0; i < 3; i++) db.RecordFwUsage(key, chosen);

        var hits = SearchService.Search(db, "НГР", usageThreshold: 3);
        Assert.Equal(chosen, hits[0].FwVersionId);
    }

    [Fact]
    public void ConfigService_FwUsageThreshold_DefaultsAndRoundTrips()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        Assert.Equal(2, cfg.FwUsageThreshold()); // дефолт, пока никто не менял

        cfg.SetFwUsageThreshold(5);
        Assert.Equal(5, cfg.FwUsageThreshold());

        // 0 и отрицательные — бессмысленный порог, сеттер поджимает к минимум 1.
        cfg.SetFwUsageThreshold(0);
        Assert.Equal(1, cfg.FwUsageThreshold());
    }

    /// <summary>Таблица просмотра в Настройках читает статистику через GetAllFwUsage — свой вклад и
    /// уже известный чужой должны сложиться на одной строке ровно как в GetFwUsageForQuery.</summary>
    [Fact]
    public void GetAllFwUsage_AggregatesOwnAndSharedContributions()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var id = AddVersion(db, "КНС", 1, "НГР");
        var key = SearchService.UsageKey("НГР");
        db.RecordFwUsage(key, id);
        db.RecordFwUsage(key, id);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");
        var ctrlSyncId = db.GetAllControllerModels().First(c => c.Id == mod.ControllerId).SyncId;
        db.ImportFwUsage(new[]
        {
            new SharedFwUsageRow("другая-машина", key, subtype.SyncId, ctrlSyncId,
                "2.1.001.0001.20260101_0000", 5, ""),
        }, db.UsageOriginId());

        var row = Assert.Single(db.GetAllFwUsage());
        Assert.Equal(key, row.QueryKey);
        Assert.Equal("КНС", row.SubtypeName);
        Assert.Equal(7, row.Uses);      // 2 свой + 5 чужой
        Assert.Equal(2, row.LocalUses); // редактор правит только свой вклад — чужие 5 из показанного 7
                                        // сдвинуть нельзя (SharedUses = Uses - LocalUses = 5).
    }

    /// <summary>Правка «Выборов» в таблице статистики бьёт по своему вкладу, а не по чужому снимку:
    /// вводя итоговое число, ниже чужой доли не опустить — точь-в-точь логика редактора в SettingsView
    /// (typed − SharedUses, не ниже нуля). Проверяем именно то, из-за чего казалось, что «не
    /// сохраняется»: при чужом вкладе показанное после правки — свой + чужой.</summary>
    [Fact]
    public void GetAllFwUsage_LocalEditKeepsSharedFloor()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var id = AddVersion(db, "КНС", 1, "НГР");
        var key = SearchService.UsageKey("НГР");
        db.RecordFwUsage(key, id); // свой вклад = 1

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");
        var ctrlSyncId = db.GetAllControllerModels().First(c => c.Id == mod.ControllerId).SyncId;
        db.ImportFwUsage(new[]
        {
            new SharedFwUsageRow("другая-машина", key, subtype.SyncId, ctrlSyncId,
                "2.1.001.0001.20260101_0000", 5, ""),
        }, db.UsageOriginId());

        var before = Assert.Single(db.GetAllFwUsage());
        var shared = before.Uses - before.LocalUses; // = 5

        // Оператор вводит итог 8 → свой вклад = 8 − 5(чужой) = 3, показано снова 8.
        db.SetLocalFwUsage(key, id, Math.Max(0, 8 - shared));
        Assert.Equal(8, db.GetAllFwUsage().Single().Uses);

        // Пытается опустить итог до 2 (ниже чужих 5) → свой уходит в 0, показано ровно чужие 5.
        db.SetLocalFwUsage(key, id, Math.Max(0, 2 - shared));
        var after = db.GetAllFwUsage().Single();
        Assert.Equal(5, after.Uses);
        Assert.Equal(0, after.LocalUses);
    }

    // ── Настраиваемые расширения поиска схем ──────────────────────────────

    [Fact]
    public void AllowedExtensionsSchematic_SeedsTheOldHardcodedDefaults()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        Assert.Equal(
            new[] { "bmp", "dwg", "dxf", "jpeg", "jpg", "pdf", "png", "tif", "tiff" },
            db.GetAllowedExtensionsSchematic().OrderBy(e => e, StringComparer.Ordinal));
    }

    [Fact]
    public void AllowedExtensionsSchematic_AddRemove_RoundTrips()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        db.AddAllowedExtensionSchematic(".XLSX");
        Assert.Contains("xlsx", db.GetAllowedExtensionsSchematic());

        db.RemoveAllowedExtensionSchematic("xlsx");
        Assert.DoesNotContain("xlsx", db.GetAllowedExtensionsSchematic());
    }

    /// <summary>Тот же LWW flat-list механизм, что у allowed_extensions/allowed_extensions_hmi —
    /// добавленное расширение уезжает в экспорт и применяется на другой машине, повторный импорт
    /// того же снимка ничего не меняет (идемпотентность).</summary>
    [Fact]
    public void AllowedExtensionsSchematic_SyncsThroughTheSameFlatListMechanism()
    {
        var pathA = Path.Combine(Path.GetTempPath(), $"antarus_ext_schematic_sync_{Guid.NewGuid():N}.db");
        var pathB = Path.Combine(Path.GetTempPath(), $"antarus_ext_schematic_sync_{Guid.NewGuid():N}.db");
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);

            dbA.AddAllowedExtensionSchematic("xlsx");
            var exported = dbA.ExportHierarchyData();
            Assert.Contains("xlsx", exported.AllowedExtensionsSchematic!);

            var counts = dbB.ImportHierarchyData(exported);
            Assert.Equal(1, counts.ExtensionsSchematicAdded);
            Assert.Contains("xlsx", dbB.GetAllowedExtensionsSchematic());

            // Повторный импорт того же снимка — без изменений (LWW-отметка уже применена).
            var second = dbB.ImportHierarchyData(exported);
            Assert.Equal(0, second.ExtensionsSchematicAdded);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { pathA, pathB })
                foreach (var ff in new[] { f, f + "-wal", f + "-shm" })
                    if (File.Exists(ff)) File.Delete(ff);
        }
    }

    // ── SchematicService с настраиваемым набором расширений ───────────────

    [Fact]
    public void Matches_DefaultExtensions_IgnoresXlsx()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "ПЖ-101"));
        File.WriteAllText(Path.Combine(root.Path, "ПЖ-101", "схема.pdf"), "x");
        File.WriteAllText(Path.Combine(root.Path, "ПЖ-101", "спецификация.xlsx"), "x");

        var hits = new SchematicService().Matches(root.Path, "ПЖ-101");
        Assert.Single(hits);
        Assert.EndsWith("схема.pdf", hits[0].Path);
    }

    /// <summary>SearchView передаёт в SchematicService настроенный из БД список — здесь он
    /// подставляется напрямую, без БД, ровно как это делает ActiveSchemaScanExtensions().</summary>
    [Fact]
    public void Matches_CustomScanExtensions_FindsXlsxWhenConfigured()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "ПЖ-101"));
        File.WriteAllText(Path.Combine(root.Path, "ПЖ-101", "схема.pdf"), "x");
        File.WriteAllText(Path.Combine(root.Path, "ПЖ-101", "спецификация.xlsx"), "x");

        var scanExtensions = new[] { ".xlsx" };
        var hits = new SchematicService().Matches(root.Path, "ПЖ-101", scanExtensions: scanExtensions);
        Assert.Single(hits);
        Assert.EndsWith("спецификация.xlsx", hits[0].Path);
    }

    /// <summary>Кэш SchematicService теперь привязан не только к пути диска, но и к набору
    /// расширений сканирования — иначе только что добавленное в Настройках расширение молча не
    /// находилось бы до перезапуска программы.</summary>
    [Fact]
    public void EnsureScanned_ChangingScanExtensions_InvalidatesTheWarmCache()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "ПЖ-101"));
        File.WriteAllText(Path.Combine(root.Path, "ПЖ-101", "схема.pdf"), "x");
        File.WriteAllText(Path.Combine(root.Path, "ПЖ-101", "спецификация.xlsx"), "x");

        var service = new SchematicService();
        service.EnsureScanned(root.Path); // прогрев дефолтным набором — только .pdf
        Assert.True(service.IsScanned(root.Path));
        Assert.False(service.IsScanned(root.Path, new[] { ".xlsx" })); // другой набор — кэш холодный

        Assert.Single(service.CabinetHits(root.Path));
        Assert.Single(service.CabinetHits(root.Path, new[] { ".xlsx" }));
    }
}
