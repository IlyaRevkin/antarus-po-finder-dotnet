using System.IO;
using System.Linq;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Доставка решений модерации С ЛЮБОЙ машины, а не только с администраторской. Найденное
/// ограничение: полный снимок на общий диск выгружает только администратор (ConfigSyncService.Export
/// / MainWindowViewModel.SendPendingChangesNow), и выгружает он СВОЮ базу — поэтому «вывести из
/// модерации», «откатить» или «удалить», сделанные на машине наладчика, физически не имели пути к
/// остальным. Механизм: журнал moderation_log + секция moderation_decisions в общем конфиге, которую
/// любая машина дописывает узким каналом ConfigSyncService.PushModerationOnly, не трогая ничего
/// больше, а приём применяет монотонно (Database.ApplyModerationDecisions).</summary>
public class ModerationDeliveryTests
{
    private const string Raw = "2.1.0044.0001.20260101_1200";

    /// <summary>Заводит на машине прошивку, ожидающую модерации (released = 0), вместе с папкой на
    /// общем диске — ровно то состояние, в котором версия попадает в очередь модерации.</summary>
    private static int SeedUnreleased(Database db, string root, out string versionDir)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var ctrl = db.GetAllControllerModels().First(c => c.Name == "SMH4");

        versionDir = Path.Combine(root, "ПО", "НГР", "КНС", "SMH4", Raw);
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "fw.psl"), "test");

        return db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = ctrl.Id!.Value,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = 44, SwVersion = 1, DtStr = "20260101_1200",
            VersionRaw = Raw, Filename = "fw.psl", DiskPath = versionDir,
            Description = "ожидает модерации", Status = "active",
        });
    }

    private static FwVersionRecord Row(Database db) =>
        db.GetAllFwVersionsWithNames(includeArchived: true).Single(v => v.VersionRaw == Raw);

    /// <summary>Первый обмен: администратор выгружает справочник, наладчик его принимает. Дальше во
    /// всех сценариях правит именно наладчик.</summary>
    private static void SeedAndShare(TwoMachines m, out int idOnB, out string versionDir)
    {
        var root = m.Root.Path;
        SeedUnreleased(m.DbA, root, out versionDir);
        ConfigSyncService.Export(m.SvcA, root, "администратор");

        var first = ConfigSyncService.CheckForUpdate(m.SvcB, out var err);
        Assert.True(err is null, err);
        Assert.NotNull(first);
        ConfigSyncService.Apply(m.SvcB, first!.ConfigPath, root);
        idOnB = Row(m.DbB).Id!.Value;
    }

    /// <summary>Главный сценарий задачи: решение «вывести из модерации» принято на машине НЕ
    /// администратора — и всё равно доезжает до остальных. До появления узкого канала здесь ничего не
    /// происходило вовсе: своего экспорта у наладчика нет, а в снимке администратора его решения
    /// быть не может.</summary>
    [Fact]
    public async System.Threading.Tasks.Task ReleaseOnNonAdminMachine_ReachesEveryoneElse()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        SeedAndShare(m, out var idOnB, out _);
        Assert.False(Row(m.DbA).Released);
        Assert.False(Row(m.DbB).Released);

        // Ключевая деталь сценария: в общем конфиге лежит снимок АДМИНИСТРАТОРА, где версия ещё не
        // выпущена — обычный дифф строк fw_versions решение наладчика донести не может в принципе.
        var onDisk = (await ConfigSyncService.ReadCurrentDiskHierarchyAsync(root)).OnDisk!;
        Assert.Equal(0, onDisk.FwVersions.Single(f => f.VersionRaw == Raw).Released);

        // Наладчик выводит версию из модерации у себя и отправляет решение узким каналом.
        m.DbB.MarkFwVersionReleasedWithLinked(idOnB);
        Assert.True(ConfigSyncService.RecordAndPushModeration(m.SvcB,
            m.DbB.GetFwVersionIdsSharingFiles(idOnB), "наладчик"));

        // Администратор забирает — его строка тоже становится выпущенной, и версия уходит из очереди.
        var incoming = ConfigSyncService.CheckForUpdate(m.SvcA, out var err);
        Assert.True(err is null, err);
        Assert.NotNull(incoming);
        Assert.Equal(1, incoming!.Diff.ModerationApplied);
        // Узкий канал не притащил с чужой машины ни настроек, ни иерархии — только решение.
        Assert.Equal(0, incoming.SettingsChanged);
        Assert.Equal(1, incoming.Diff.TotalChanges);

        ConfigSyncService.Apply(m.SvcA, incoming.ConfigPath, root);
        Assert.True(Row(m.DbA).Released);
        Assert.Equal(0, m.DbA.GetUnreleasedFwVersionsCount());

        // Повторный приём того же снимка ничего не меняет (признаки монотонные).
        Assert.Equal(0, m.DbA.PreviewImportHierarchyData(m.DbB.ExportHierarchyData()).ModerationApplied);
    }

    /// <summary>Узкий канал ОБЯЗАН поднимать маркер ревизии, иначе получатели до самого конфига даже
    /// не доходят (revision-гейт в ConfigSyncService.ReadShared) и решение лежало бы в файле мёртвым
    /// грузом до ближайшего экспорта администратора — то есть задача «работает у всех» не решалась бы.
    /// Здесь машина B уже дотянулась до текущей ревизии (ей «нечего применять»), и увидеть решение
    /// она может ТОЛЬКО если ревизия выросла.</summary>
    [Fact]
    public void PushModerationOnly_BumpsRevision_SoReceiversActuallyLook()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        SeedAndShare(m, out _, out _);
        Assert.Null(ConfigSyncService.CheckForUpdate(m.SvcB, out _)); // B догнала диск — нового нет

        var idOnA = Row(m.DbA).Id!.Value;
        m.DbA.MarkFwVersionReleased(idOnA);
        Assert.True(ConfigSyncService.RecordAndPushModeration(m.SvcA, idOnA, "администратор"));

        var incoming = ConfigSyncService.CheckForUpdate(m.SvcB, out var err);
        Assert.True(err is null, err);
        Assert.NotNull(incoming);
        Assert.Equal(1, incoming!.Diff.ModerationApplied);
        ConfigSyncService.Apply(m.SvcB, incoming.ConfigPath, root);
        Assert.True(Row(m.DbB).Released);
    }

    /// <summary>Общего конфига на диске ещё нет — узкий канал НЕ создаёт его сам. Снимок с пустой
    /// иерархией получатель честно прочитал бы как «на источнике этих типов/подтипов/контроллеров
    /// нет» и зеркалил бы их удаление (блоки *Removed в ImportHierarchyDataCore безусловные). Решение
    /// при этом не теряется: оно уже в местном журнале и уедет первым же полным экспортом.</summary>
    [Fact]
    public void PushModerationOnly_NoSharedConfigYet_WritesNothing()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var id = SeedUnreleased(m.DbB, root, out _);
        m.DbB.MarkFwVersionReleased(id);

        Assert.False(ConfigSyncService.RecordAndPushModeration(m.SvcB, id, "наладчик"));
        Assert.False(File.Exists(Path.Combine(root, "Конфиг", "po_finder_config.json")),
            "узкий канал не должен заводить общий конфиг с одной только своей секцией");
        // Решение сохранено локально и уедет обычным экспортом.
        Assert.Contains(m.DbB.GetRecentModerationDecisions(), d => d.VersionRaw == Raw && d.Released == 1);
    }

    /// <summary>Удаление — такое же решение модерации и точно так же обязано доезжать с любой машины:
    /// tombstone уезжает узким каналом, а получатель зеркалит его вместе с уборкой файлов.</summary>
    [Fact]
    public void DeleteOnNonAdminMachine_ReachesEveryoneElse()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        SeedAndShare(m, out var idOnB, out var versionDir);

        m.DbB.TombstoneFwVersion(idOnB);
        Assert.True(ConfigSyncService.RecordAndPushModeration(m.SvcB, idOnB, "наладчик"));

        var incoming = ConfigSyncService.CheckForUpdate(m.SvcA, out var err);
        Assert.True(err is null, err);
        Assert.NotNull(incoming);
        Assert.Equal(1, incoming!.Diff.ModerationApplied);

        ConfigSyncService.Apply(m.SvcA, incoming.ConfigPath, root);
        Assert.Empty(m.DbA.GetAllFwVersionsWithNames(includeArchived: true).Where(v => v.VersionRaw == Raw));
        Assert.False(Directory.Exists(versionDir), "папка удалённой версии должна быть убрана с общего диска");
    }

    /// <summary>Две машины приняли РАЗНЫЕ решения по одной версии (одна выпустила, другая
    /// заархивировала). Признаки монотонные, поэтому сходятся предсказуемо — на объединении решений,
    /// независимо от порядка обмена, и ни одно решение не откатывает другое.</summary>
    [Fact]
    public void TwoMachinesDifferentDecisions_ConvergeOnUnion()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        SeedAndShare(m, out var idOnB, out _);
        var idOnA = Row(m.DbA).Id!.Value;

        // A выпускает, B архивирует — оба дописывают своё решение в общий конфиг.
        m.DbA.MarkFwVersionReleased(idOnA);
        Assert.True(ConfigSyncService.RecordAndPushModeration(m.SvcA, idOnA, "администратор"));

        m.DbB.ArchiveFwVersion(idOnB);
        Assert.True(ConfigSyncService.RecordAndPushModeration(m.SvcB, idOnB, "наладчик"));

        // Каждый забирает чужое — и оба приходят к released = 1 И archived = 1.
        var toA = ConfigSyncService.CheckForUpdate(m.SvcA, out var errA);
        Assert.True(errA is null, errA);
        Assert.NotNull(toA);
        ConfigSyncService.Apply(m.SvcA, toA!.ConfigPath, root);

        var toB = ConfigSyncService.CheckForUpdate(m.SvcB, out var errB);
        Assert.True(errB is null, errB);
        Assert.NotNull(toB);
        ConfigSyncService.Apply(m.SvcB, toB!.ConfigPath, root);

        foreach (var db in new[] { m.DbA, m.DbB })
        {
            var row = Row(db);
            Assert.True(row.Released, "решение «выпустить» должно доехать до обеих машин");
            Assert.True(row.Archived, "решение «архивировать» должно доехать до обеих машин");
        }
    }

    /// <summary>Снимок, собранный СТАРОЙ версией приложения, секции moderation_decisions не содержит
    /// вовсе (null, а не пустой список) — импорт тогда обязан вести себя ровно как раньше: ничего не
    /// применять и ничего не трогать. Пустой список — тоже «нечего применять», а не «сбросить всё».</summary>
    [Fact]
    public void SnapshotWithoutModerationSection_BehavesExactlyAsBefore()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        SeedAndShare(m, out var idOnB, out _);
        m.DbB.MarkFwVersionReleased(idOnB);

        var legacy = m.DbB.ExportHierarchyData();
        Assert.NotNull(legacy.ModerationDecisions); // текущая версия секцию пишет…
        legacy.ModerationDecisions = null;          // …а старая её не писала вовсе

        var preview = m.DbA.PreviewImportHierarchyData(legacy);
        Assert.Equal(0, preview.ModerationApplied);
        m.DbA.ImportHierarchyData(legacy);
        // released у A продвинулся обычным дифом строк fw_versions (это поведение было и раньше),
        // но не узким каналом — канал в старом снимке отсутствует.
        Assert.Equal(0, m.DbA.ImportHierarchyData(legacy).ModerationApplied);

        legacy.ModerationDecisions = new();
        Assert.Equal(0, m.DbA.PreviewImportHierarchyData(legacy).ModerationApplied);
    }

    /// <summary>Локальный tombstone постоянен: приехавшее «эта версия выпущена/активна» никогда не
    /// воскрешает удалённую здесь строку — то же правило, что у обычного блока fw_versions.</summary>
    [Fact]
    public void LocalTombstone_IsNeverRevivedByIncomingDecision()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        SeedAndShare(m, out var idOnB, out _);
        var idOnA = Row(m.DbA).Id!.Value;

        // A удалил у себя, B (ещё не знает об этом) выпускает.
        m.DbA.TombstoneFwVersion(idOnA);
        m.DbB.MarkFwVersionReleased(idOnB);
        m.DbB.RecordModerationDecision(idOnB, "наладчик");

        var counts = m.DbA.ImportHierarchyData(m.DbB.ExportHierarchyData());
        Assert.Equal(0, counts.ModerationApplied);
        Assert.Empty(m.DbA.GetAllFwVersionsWithNames(includeArchived: true).Where(v => v.VersionRaw == Raw));
    }

    /// <summary>Решение, принятое на машине, которая сама снимок не выгружает, не теряется при
    /// ближайшем полном экспорте администратора: получатель принимает чужие решения в свой журнал
    /// (Database.AbsorbModerationDecisions) и пересылает их дальше собственным экспортом. Без этого
    /// машина, не успевшая синхронизироваться до перезаписи конфига, решения бы уже не увидела.</summary>
    [Fact]
    public void AbsorbedDecision_IsForwardedByTheNextFullExport()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        SeedAndShare(m, out var idOnB, out _);
        m.DbB.MarkFwVersionReleased(idOnB);
        m.DbB.RecordModerationDecision(idOnB, "наладчик");

        // Администратор принял решение наладчика…
        m.DbA.ImportHierarchyData(m.DbB.ExportHierarchyData());
        Assert.True(Row(m.DbA).Released);

        // …и его собственный полный экспорт несёт это решение дальше, а не стирает его.
        var exported = m.DbA.ExportHierarchyData();
        Assert.Contains(exported.ModerationDecisions!, d => d.VersionRaw == Raw && d.Released == 1);

        // Дедупликация: повторный приём того же журнала не плодит копий.
        var before = m.DbA.GetRecentModerationDecisions().Count;
        m.DbA.ImportHierarchyData(m.DbB.ExportHierarchyData());
        Assert.Equal(before, m.DbA.GetRecentModerationDecisions().Count);

        ConfigSyncService.Export(m.SvcA, root, "администратор");
    }
}
