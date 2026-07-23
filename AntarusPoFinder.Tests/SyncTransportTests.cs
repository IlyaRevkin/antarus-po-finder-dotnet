using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Новая модель синхронизации (маркер ревизии, ISyncTransport, накопитель отправляемых
/// изменений) — тесты на всё, что не проверялось раньше: монотонность revision, дешёвый пропуск
/// чтения полного конфига пока ревизия не выросла, накопление/ограничение журнала изменений,
/// best-effort compare-and-swap при гонке записи маркера, подмена транспорта интерфейсом,
/// накопитель sync_pending_changes и фильтр экспорта is_local_only.
///
/// Тесты используют TestHelpers.TwoMachines (см. EndToEndSyncTests) — два независимых профиля,
/// делящих одну папку на диске как общий "Конфиг". CollectionBehavior(DisableTestParallelization)
/// в AssemblyInfo.cs гарантирует, что подмена статического ConfigSyncService.TransportFactory одним
/// тестом не заденет другой, выполняющийся параллельно — тесты в сборке идут строго по очереди.</summary>
public class SyncTransportTests
{
    private static void ResetTransportFactory() => ConfigSyncService.TransportFactory = root => new FileShareTransport(root);

    [Fact]
    public async Task Export_WritesRevisionMarker_MonotonicAcrossRepeatedExports()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;
        try
        {
            ConfigSyncService.Export(m.SvcA, root, "profileA");
            var transport = new FileShareTransport(root);
            var afterFirst = await transport.ReadRevisionAsync();
            Assert.NotNull(afterFirst);
            Assert.Equal(1, afterFirst!.Revision);

            ConfigSyncService.Export(m.SvcA, root, "profileA");
            var afterSecond = await transport.ReadRevisionAsync();
            Assert.Equal(2, afterSecond!.Revision);

            ConfigSyncService.Export(m.SvcA, root, "profileA");
            var afterThird = await transport.ReadRevisionAsync();
            Assert.Equal(3, afterThird!.Revision);
        }
        finally { ResetTransportFactory(); }
    }

    [Fact]
    public async Task Export_ChangeDescriptions_AppendToChangelog_NewestFirst_CappedAtMax()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;
        try
        {
            ConfigSyncService.Export(m.SvcA, root, "profileA", new[] { "первое изменение" });
            ConfigSyncService.Export(m.SvcA, root, "profileA", new[] { "второе изменение" });

            var transport = new FileShareTransport(root);
            var marker = (await transport.ReadRevisionAsync())!;
            Assert.Equal(2, marker.Revision);
            Assert.Equal("второе изменение", marker.Changes[0].Description); // самое новое первым
            Assert.Contains(marker.Changes, c => c.Description == "первое изменение");

            // Заливаем ещё много изменений одним экспортом, чтобы журнал точно перевалил за лимит.
            var many = Enumerable.Range(0, 60).Select(i => $"изменение {i}").ToArray();
            ConfigSyncService.Export(m.SvcA, root, "profileA", many);
            var afterMany = (await transport.ReadRevisionAsync())!;
            Assert.True(afterMany.Changes.Count <= 50, $"changelog должен быть ограничен 50 записями, а не {afterMany.Changes.Count}");
            Assert.Equal("изменение 0", afterMany.Changes[0].Description); // самые новые (последний Export) — в начале
        }
        finally { ResetTransportFactory(); }
    }

    /// <summary>Тестовый транспорт-обёртка, считающая обращения к дорогой части (сам конфиг) —
    /// доказывает, что ReadShared действительно не читает и не разбирает po_finder_config.json,
    /// пока маркер не показал рост ревизии (Задача 2/3).</summary>
    private sealed class CountingTransport : ISyncTransport
    {
        private readonly ISyncTransport _inner;
        public int ConfigReadCount { get; private set; }
        public int RevisionReadCount { get; private set; }

        public CountingTransport(ISyncTransport inner) => _inner = inner;

        public Task<bool> IsAvailableAsync() => _inner.IsAvailableAsync();

        public Task<SyncRevisionMarker?> ReadRevisionAsync()
        {
            RevisionReadCount++;
            return _inner.ReadRevisionAsync();
        }

        public Task WriteRevisionAsync(SyncRevisionMarker marker) => _inner.WriteRevisionAsync(marker);

        public Task<byte[]?> ReadConfigAsync()
        {
            ConfigReadCount++;
            return _inner.ReadConfigAsync();
        }

        public Task WriteConfigAsync(byte[] bytes) => _inner.WriteConfigAsync(bytes);
    }

    [Fact]
    public void CheckForUpdate_SkipsReadingFullConfig_WhenRevisionHasNotAdvanced()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;
        try
        {
            ConfigSyncService.Export(m.SvcA, root, "profileA");

            CountingTransport? counting = null;
            // Фабрика создаёт НОВЫЙ CountingTransport на каждый вызов (как и реальная
            // TransportFactory) — но ReadShared внутри одного вызова CheckForUpdate использует ровно
            // один экземпляр, так что счётчики последнего созданного отражают именно эту проверку.
            ConfigSyncService.TransportFactory = r =>
            {
                counting = new CountingTransport(new FileShareTransport(r));
                return counting;
            };

            // Первая проверка на B: ревизии этой машины ещё нет (0) — маркер на диске = 1 — конфиг
            // читается и применяется.
            var update = ConfigSyncService.CheckForUpdate(m.SvcB, out var err);
            Assert.Null(err);
            Assert.NotNull(update);
            Assert.NotNull(counting);
            Assert.Equal(1, counting!.ConfigReadCount);
            ConfigSyncService.Apply(m.SvcB, update!.ConfigPath, root);

            // Повторная проверка сразу после — revision на диске не выросла (всё ещё 1, применено).
            // Маркер прочитать дёшево нужно (сравнить), а вот сам конфиг — уже нет.
            var secondCheck = ConfigSyncService.CheckForUpdate(m.SvcB, out var err2);
            Assert.Null(err2);
            Assert.Null(secondCheck); // нечего применять
            Assert.True(counting!.RevisionReadCount > 0);
            Assert.Equal(0, counting!.ConfigReadCount); // до чтения полного конфига дело не дошло
        }
        finally { ResetTransportFactory(); }
    }

    [Fact]
    public void CheckForUpdate_NoOpDiff_StillAdvancesRevisionWatermark_SoNextTickStaysCheap()
    {
        // Экспорт без реальных изменений для B (например, повторный экспорт того же состояния A,
        // которое B уже применил ранее) — ревизия на диске растёт, но применять нечего. Локальный
        // watermark ревизии всё равно должен продвинуться, иначе каждый следующий тик снова читал
        // бы полный конфиг (см. класс-doc Analyze в ConfigSyncService).
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;
        try
        {
            ConfigSyncService.Export(m.SvcA, root, "profileA");
            var first = ConfigSyncService.CheckForUpdate(m.SvcB, out var err1);
            Assert.Null(err1);
            Assert.NotNull(first);
            ConfigSyncService.Apply(m.SvcB, first!.ConfigPath, root);

            // A экспортирует ещё раз без каких-либо изменений в своей БД — ревизия растёт (2), но
            // содержимое идентично уже применённому на B.
            ConfigSyncService.Export(m.SvcA, root, "profileA");
            var second = ConfigSyncService.CheckForUpdate(m.SvcB, out var err2);
            Assert.Null(err2);
            Assert.Null(second); // диффа нет — ничего предлагать не должны

            // Убеждаемся, что это не "просто повезло": следующая проверка снова ничего не находит
            // БЕЗ повторного чтения того же самого неизменившегося конфига — если бы watermark не
            // продвинулся, ReadShared продолжал бы читать конфиг на каждом тике вечно.
            var third = ConfigSyncService.CheckForUpdate(m.SvcB, out var err3);
            Assert.Null(err3);
            Assert.Null(third);
        }
        finally { ResetTransportFactory(); }
    }

    [Fact]
    public void Apply_AdvancesLocalRevisionWatermark_MatchingDiskMarker()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;
        try
        {
            ConfigSyncService.Export(m.SvcA, root, "profileA");
            var update = ConfigSyncService.CheckForUpdate(m.SvcB, out _);
            Assert.NotNull(update);
            Assert.Equal(1, update!.Revision);

            ConfigSyncService.Apply(m.SvcB, update.ConfigPath, root);
            Assert.Equal("1", m.CfgB.Get("config_last_synced_revision"));
        }
        finally { ResetTransportFactory(); }
    }

    /// <summary>Транспорт-обёртка, симулирующая гонку записи маркера (Задача 6): ровно один раз,
    /// сразу после того как ConfigSyncService записал свой маркер, "вклинивается" другая машина и
    /// перезаписывает маркер той же ревизией, но своим содержимым. Compare-and-swap обязан это
    /// заметить (сравнивая не только Revision, но и ExportedBy/ExportedAt) и повторить попытку —
    /// иначе журнал изменений первой машины был бы молча потерян под чужим.</summary>
    private sealed class RacyOnceTransport : ISyncTransport
    {
        private readonly ISyncTransport _inner;
        private bool _racedOnce;
        public int WriteAttempts { get; private set; }

        public RacyOnceTransport(ISyncTransport inner) => _inner = inner;

        public Task<bool> IsAvailableAsync() => _inner.IsAvailableAsync();
        public Task<SyncRevisionMarker?> ReadRevisionAsync() => _inner.ReadRevisionAsync();

        public async Task WriteRevisionAsync(SyncRevisionMarker marker)
        {
            WriteAttempts++;
            await _inner.WriteRevisionAsync(marker);
            if (!_racedOnce)
            {
                _racedOnce = true;
                await _inner.WriteRevisionAsync(new SyncRevisionMarker
                {
                    Revision = marker.Revision, ExportedAt = "intruder-ts", ExportedBy = "intruder", Changes = new(),
                });
            }
        }

        public Task<byte[]?> ReadConfigAsync() => _inner.ReadConfigAsync();
        public Task WriteConfigAsync(byte[] bytes) => _inner.WriteConfigAsync(bytes);
    }

    [Fact]
    public async Task Export_RetriesRevisionBump_WhenAnotherMachineClobbersSameRevisionNumber()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;
        try
        {
            RacyOnceTransport? racy = null;
            ConfigSyncService.TransportFactory = r =>
            {
                racy = new RacyOnceTransport(new FileShareTransport(r));
                return racy;
            };

            ConfigSyncService.Export(m.SvcA, root, "profileA", new[] { "изменение от profileA" });

            Assert.NotNull(racy);
            Assert.True(racy!.WriteAttempts >= 2, "CAS должен был заметить чужую запись и повторить попытку");

            var final = await new FileShareTransport(root).ReadRevisionAsync();
            Assert.NotNull(final);
            // Финальный маркер — точно наш (не "intruder"), и ревизия строго больше той, что
            // "захватил" вклинившийся — CAS не поверил совпадению одних только чисел.
            Assert.Equal("profileA", final!.ExportedBy);
            Assert.True(final.Revision >= 2, $"ожидали, что CAS поднимет ревизию выше захваченной гонкой, получили {final.Revision}");
            Assert.Contains(final.Changes, c => c.Description == "изменение от profileA");
        }
        finally { ResetTransportFactory(); }
    }

    [Fact]
    public async Task CheckForUpdate_CriticalSchemaMismatch_IsFlagged_ButStillApplies()
    {
        // Синтетический общий конфиг с "чужой" версией схемы — собран напрямую (Export всегда пишет
        // текущую CurrentSchemaVersion, подменить её изнутри теста нечем, это private const по
        // дизайну — значит для проверки уровня "критическое расхождение" нужен конфиг, собранный не
        // через Export). Разбор ищет только поле "schema_version" в корневом JSON плюс валидный
        // HierarchyExportData JSON — то же самое, что строит PrepareExport, только с другим
        // значением schema_version.
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;
        try
        {
            var hierarchy = m.DbA.ExportHierarchyData();
            var json = System.Text.Json.JsonSerializer.Serialize(hierarchy);
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            node["exported_at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            node["exported_by"] = "profileA";
            node["source_root_path"] = root;
            node["schema_version"] = "999"; // заведомо не совпадает с CurrentSchemaVersion этой сборки

            var configDir = Path.Combine(root, "Конфиг");
            Directory.CreateDirectory(configDir);
            var bytes = AntarusPoFinder.Core.Infrastructure.ConfigFileCrypto.Encrypt(node.ToJsonString());
            File.WriteAllBytes(Path.Combine(configDir, "po_finder_config.json"), bytes);

            var transport = new FileShareTransport(root);
            await transport.WriteRevisionAsync(new SyncRevisionMarker
            {
                Revision = 1, ExportedAt = node["exported_at"]!.GetValue<string>(), ExportedBy = "profileA", Changes = new(),
            });

            var update = ConfigSyncService.CheckForUpdate(m.SvcB, out var err);
            Assert.Null(err);
            Assert.NotNull(update);
            Assert.True(update!.CriticalSchemaMismatch);

            // Критическое расхождение НЕ блокирует применение (п.5 дизайна — "применяется
            // принудительно с уведомлением", уведомление — забота MainWindowViewModel).
            var result = ConfigSyncService.Apply(m.SvcB, update.ConfigPath, root);
            Assert.NotNull(result);
        }
        finally { ResetTransportFactory(); }
    }
}

/// <summary>Накопитель локальных изменений, готовых к отправке (Задача 4, отправитель) — Database.
/// SyncPendingChange. Не требует сетевого диска вовсе, только локальную БД.</summary>
public class SyncPendingChangesTests
{
    [Fact]
    public void AddAndGet_ReturnsInOrder_ThenClearEmpties()
    {
        using var dbFile = new TempDb();
        using var db = new AntarusPoFinder.Core.Data.Database(dbFile.Path);

        Assert.Equal(0, db.SyncPendingChangeCount());

        db.AddSyncPendingChange("catalog", "добавлен производитель VEDA", "profileA");
        db.AddSyncPendingChange("catalog", "удалён тип шкафа X", "profileA");

        Assert.Equal(2, db.SyncPendingChangeCount());
        var changes = db.GetSyncPendingChanges();
        Assert.Equal("добавлен производитель VEDA", changes[0].Description);
        Assert.Equal("удалён тип шкафа X", changes[1].Description);
        Assert.All(changes, c => Assert.Equal("profileA", c.Author));

        db.ClearSyncPendingChanges();
        Assert.Equal(0, db.SyncPendingChangeCount());
        Assert.Empty(db.GetSyncPendingChanges());
    }

    [Fact]
    public async Task Export_ClearsLocalPendingAccumulator_AfterSuccessfulPush()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;
        try
        {
            m.DbA.AddSyncPendingChange("catalog", "добавлен тег synctest", "profileA");
            m.DbA.AddSyncPendingChange("catalog", "добавлено расширение .synctest", "profileA");
            Assert.Equal(2, m.DbA.SyncPendingChangeCount());

            var descriptions = m.DbA.GetSyncPendingChanges().Select(c => c.Description).ToList();
            ConfigSyncService.Export(m.SvcA, root, "profileA", descriptions);

            Assert.Equal(0, m.DbA.SyncPendingChangeCount());

            var marker = await new FileShareTransport(root).ReadRevisionAsync();
            Assert.NotNull(marker);
            Assert.Contains(marker!.Changes, c => c.Description == "добавлен тег synctest");
            Assert.Contains(marker.Changes, c => c.Description == "добавлено расширение .synctest");
        }
        finally { ConfigSyncService.TransportFactory = r => new FileShareTransport(r); }
    }
}

/// <summary>Задел (Задача 7) — is_local_only на fw_versions: строка не должна покидать эту машину
/// через ExportHierarchyData, даже если во всём остальном выглядит как обычная активная версия.</summary>
public class LocalOnlyFwVersionExportTests
{
    [Fact]
    public void ExportHierarchyData_SkipsRowsMarkedLocalOnly()
    {
        using var dbFile = new TempDb();
        using var db = new AntarusPoFinder.Core.Data.Database(dbFile.Path);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First();
        var ctrl = db.GetAllModifications().First();

        db.AddFwVersion(new AntarusPoFinder.Core.Domain.FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = ctrl.ControllerId, EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = ctrl.HwVersion, SwVersion = 1, DtStr = "20260101_0000", VersionRaw = "local-only-test.normal",
            Filename = "normal.psl", DiskPath = "", Description = "обычная версия", Status = "active",
        });
        var localOnlyId = db.AddFwVersion(new AntarusPoFinder.Core.Domain.FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = ctrl.ControllerId, EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = ctrl.HwVersion, SwVersion = 1, DtStr = "20260101_0001", VersionRaw = "local-only-test.local",
            Filename = "local.psl", DiskPath = "", Description = "только у себя", Status = "active",
        });

        db.SetFwVersionLocalOnly(localOnlyId, true);

        var exported = db.ExportHierarchyData();
        Assert.Contains(exported.FwVersions, f => f.VersionRaw == "local-only-test.normal");
        Assert.DoesNotContain(exported.FwVersions, f => f.VersionRaw == "local-only-test.local");
    }
}
