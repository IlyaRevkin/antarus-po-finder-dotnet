using System.IO;
using System.Linq;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Окно модерации должно быть одинаковым на всех машинах — ровно как поиск и сама иерархия.
/// Жалоба, с которой начался этот набор: «на компе коллеги старые УДАЛЁННЫЕ прошивки почему-то висят
/// на модерации».
///
/// Очередь модерации (Database.GetUnreleasedFwVersionsWithNames) отбирает строки сразу по четырём
/// признакам: не удалена (deleted_at), не архивная (archived), не откатана (status), не выпущена
/// (released). Значит и синхронизироваться обязаны все четыре — расхождение по любому одному и есть
/// «у меня чисто, у коллеги висит». Здесь проверяется каждый из них плюс два пути, которыми расхождение
/// возникало на практике:
///   • досмотр диска, не знавший про надгробия, заводил удалённую прошивку заново — новой строкой,
///     released = 0, то есть прямо в очередь модерации (см. Database.GetKnownVersionRaws);
///   • правка, меняющая натуральный ключ строки (переназначение контроллера, переписывание hw), рвала
///     связь между «той же самой» записью на разных машинах, и приехавшее решение модерации применять
///     было не к чему (см. fw_versions.sync_id и Database.FindFwVersionRow).</summary>
public class ModerationSyncTests
{
    private const string VersionRaw = "1.99.7.1.20260801_1200";

    /// <summary>Прошивка на машине A: строка в БД плюс настоящая папка с файлом на общем диске — на
    /// диск смотрят и удаление, и досмотр, поэтому подделывать его нельзя.
    ///
    /// <paramref name="withFiles"/> = false — запись без файлов на диске (штатный случай, см.
    /// HierarchyService.RewriteHw: «запись без файлов правится только в БД»). Нужна там, где проверяется
    /// ПЕРЕНАЗНАЧЕНИЕ КОНТРОЛЛЕРА: оно намеренно не двигает папку на диске (см.
    /// Database.ReassignFwVersionController), поэтому осиротевшую папку под старым контроллером
    /// ближайший досмотр заводит отдельной записью — поведение давнее и к синхронизации отношения не
    /// имеющее, но в этих тестах оно только мешало бы читать результат.</summary>
    private static (int FwId, EquipmentSubType Subtype, ControllerModification Mod, string Folder) SeedFirmware(
        Database db, HierarchyService hier, string root, string versionRaw = VersionRaw, string tags = "",
        bool withFiles = true)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");

        var folder = "";
        if (withFiles)
        {
            folder = hier.FwPath(root, group.Name, subtype.Name, mod.ControllerName, versionRaw);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "fw.psl"), "test firmware");
        }

        var id = db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion, SwVersion = 1, DtStr = "20260801_1200",
            VersionRaw = versionRaw, Filename = withFiles ? "fw.psl" : "", DiskPath = folder,
            Description = "версия для проверки модерации", Changelog = "версия для проверки модерации",
            Status = "active", Tags = tags,
        });
        Assert.True(id > 0);
        return (id, subtype, mod, folder);
    }

    /// <summary>Полный круг «A отправил → B забрал». Apply, а не CheckForUpdate: он не смотрит на
    /// отметки времени экспорта (у них секундное разрешение), поэтому тесту не нужны паузы.</summary>
    private static ImportCounts SyncAtoB(TwoMachines m, string root)
    {
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        return ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(root), root).Counts;
    }

    private static bool InModerationQueue(Database db, string versionRaw) =>
        db.GetUnreleasedFwVersionsWithNames().Any(v => v.VersionRaw == versionRaw);

    /// <summary>Базовый случай: версию вывели из модерации на A — у B она из очереди тоже уходит.</summary>
    [Fact]
    public void Released_PropagatesToOtherMachine_AndLeavesItsModerationQueue()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var seed = SeedFirmware(m.DbA, m.HierA, root);
        SyncAtoB(m, root);
        Assert.True(InModerationQueue(m.DbB, VersionRaw), "до модерации версия обязана быть в очереди у B");

        m.DbA.MarkFwVersionReleased(seed.FwId);
        SyncAtoB(m, root);

        Assert.False(InModerationQueue(m.DbA, VersionRaw));
        Assert.False(InModerationQueue(m.DbB, VersionRaw));
        Assert.Equal(m.DbA.GetUnreleasedFwVersionsCount(), m.DbB.GetUnreleasedFwVersionsCount());
    }

    /// <summary>Архивирование — третий признак очереди модерации наравне со status и released, и до
    /// этого фикса единственный, который не уезжал вовсе: archived писался ТОЛЬКО при первичной вставке
    /// строки, а на уже совпавшей не обновлялся никогда. Версия, убранная в архив на A, продолжала
    /// висеть в модерации у всех остальных — ровно жалоба «у коллеги висят старые».</summary>
    [Fact]
    public void Archived_PropagatesToOtherMachine_AndLeavesItsModerationQueue()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var seed = SeedFirmware(m.DbA, m.HierA, root);
        SyncAtoB(m, root);
        Assert.True(InModerationQueue(m.DbB, VersionRaw));

        m.DbA.ArchiveFwVersion(seed.FwId);
        SyncAtoB(m, root);

        Assert.False(InModerationQueue(m.DbA, VersionRaw));
        Assert.False(InModerationQueue(m.DbB, VersionRaw));
        var archivedOnB = m.DbB.GetAllFwVersionsWithNames(includeArchived: true).Single(v => v.VersionRaw == VersionRaw);
        Assert.True(archivedOnB.Archived);
    }

    /// <summary>Откат версии тоже убирает её из очереди у всех (этот признак ездил и раньше — тест
    /// закрепляет, что перевод сопоставления строк на sync_id его не сломал).</summary>
    [Fact]
    public void RolledBack_PropagatesToOtherMachine_AndLeavesItsModerationQueue()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var seed = SeedFirmware(m.DbA, m.HierA, root);
        SyncAtoB(m, root);
        Assert.True(InModerationQueue(m.DbB, VersionRaw));

        Assert.True(m.DbA.RollbackFwVersion(seed.FwId));
        SyncAtoB(m, root);

        Assert.False(InModerationQueue(m.DbB, VersionRaw));
    }

    /// <summary>Главная жалоба целиком: удалённая на A прошивка исчезает и из очереди модерации B.
    /// Удаление едет надгробием (Database.TombstoneFwVersion), а не «отсутствием строки в снимке» —
    /// иначе приёмник читал бы её отсутствие как «эту версию мне ещё не залили», а не как «удалена».</summary>
    [Fact]
    public void Deleted_PropagatesToOtherMachine_AndLeavesItsModerationQueue()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var seed = SeedFirmware(m.DbA, m.HierA, root);
        SyncAtoB(m, root);
        Assert.True(InModerationQueue(m.DbB, VersionRaw));

        // Ровно как SettingsView.DeleteFirmware_Click: сначала файлы, потом надгробие.
        Directory.Delete(seed.Folder, recursive: true);
        m.DbA.TombstoneFwVersion(seed.FwId);

        var counts = SyncAtoB(m, root);
        Assert.True(counts.FwVersionsRemoved >= 1);
        Assert.False(InModerationQueue(m.DbB, VersionRaw));
        Assert.Equal(0, m.DbB.GetUnreleasedFwVersionsCount());
    }

    /// <summary>Корневая причина «удалил, а у коллеги висит на модерации», которую не видно в
    /// двухмашинных сценариях выше: досмотр диска брал «уже известные номера версий» запросом,
    /// отфильтровывающим удалённые строки. Стоило папке версии на диске уцелеть (удаление файлов —
    /// best effort: занятый файл, нет прав на чужую папку, шара отвалилась ровно в этот момент) — и
    /// ближайший обход заводил прошивку ЗАНОВО, новой строкой с released = 0, то есть прямо в очередь
    /// модерации. Само удаление при этом отработало правильно и даже доехало до коллег: воскрешал
    /// запись диск, уже после.</summary>
    [Fact]
    public void DiskScan_DoesNotResurrectDeletedFirmware_IntoModerationQueue()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var tempRoot = new TempRoot();
        var hier = new HierarchyService(db);
        hier.EnsureStructure(tempRoot.Path);

        var seed = SeedFirmware(db, hier, tempRoot.Path);
        Assert.True(InModerationQueue(db, VersionRaw));

        // Удаляем запись, а папку на диске НЕ трогаем — тот самый случай, когда файлы удалить не
        // удалось (или прошивку удаляли с машины, у которой нет прав на эту папку).
        db.TombstoneFwVersion(seed.FwId);
        Assert.False(InModerationQueue(db, VersionRaw));
        Assert.True(Directory.Exists(seed.Folder), "папка версии для этого теста обязана остаться на диске");

        var scan = hier.SyncFwFromDisk(tempRoot.Path);

        Assert.Equal(0, scan.Added);
        Assert.False(InModerationQueue(db, VersionRaw));
        Assert.Equal(0, db.GetUnreleasedFwVersionsCount());
        // И самой строки не задвоилось: версия по-прежнему одна и она удалена.
        Assert.Empty(db.GetFwVersions(seed.Subtype.Id, seed.Mod.ControllerId, includeArchived: true, includeRolledBack: true));
    }

    /// <summary>Второй путь расхождения: правка, меняющая НАТУРАЛЬНЫЙ КЛЮЧ строки. Оператор
    /// переназначает версию другому контроллеру (Database.ReassignFwVersionController — «завели не под
    /// тем контроллером») и выводит её из модерации. До появления fw_versions.sync_id снимок с A
    /// адресовал строку тройкой подтип+контроллер+version_raw, и у B она уже не находилась: рядом
    /// вставлялся дубликат под новым контроллером, а исходная запись B оставалась висеть в очереди
    /// модерации навсегда. Теперь строка опознаётся по sync_id и правка применяется НА МЕСТЕ.</summary>
    [Fact]
    public void ControllerReassignedThenReleased_AppliesToSameRowOnOtherMachine_NoDuplicateLeftInModeration()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var seed = SeedFirmware(m.DbA, m.HierA, root, withFiles: false);
        SyncAtoB(m, root);
        Assert.True(InModerationQueue(m.DbB, VersionRaw));

        // A: «эту версию завели не под тем контроллером» + вывод из модерации.
        var otherCtrl = m.DbA.GetAllControllerModels().First(c => c.Name == "PIXEL2");
        Assert.True(m.DbA.ReassignFwVersionController(seed.FwId, otherCtrl.Id!.Value));
        m.DbA.MarkFwVersionReleased(seed.FwId);

        SyncAtoB(m, root);

        // У B ровно ОДНА строка этой версии — переехавшая на новый контроллер, выпущенная.
        var rowsOnB = m.DbB.GetAllFwVersionsWithNames(includeArchived: true).Where(v => v.VersionRaw == VersionRaw).ToList();
        var row = Assert.Single(rowsOnB);
        Assert.Equal("PIXEL2", row.CtrlName);
        Assert.True(row.Released);
        Assert.False(InModerationQueue(m.DbB, VersionRaw));
        Assert.Equal(0, m.DbB.GetUnreleasedFwVersionsCount());
    }

    /// <summary>То же самое, но для удаления — и это ровно то, на что была жалоба. Версию
    /// переназначили другому контроллеру, потом удалили: у B надгробие обязано найти ТУ ЖЕ строку.</summary>
    [Fact]
    public void ControllerReassignedThenDeleted_TombstoneReachesSameRowOnOtherMachine()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var seed = SeedFirmware(m.DbA, m.HierA, root);
        SyncAtoB(m, root);

        var otherCtrl = m.DbA.GetAllControllerModels().First(c => c.Name == "PIXEL2");
        Assert.True(m.DbA.ReassignFwVersionController(seed.FwId, otherCtrl.Id!.Value));
        Directory.Delete(seed.Folder, recursive: true);
        m.DbA.TombstoneFwVersion(seed.FwId);

        SyncAtoB(m, root);

        Assert.DoesNotContain(m.DbB.GetAllFwVersionsWithNames(includeArchived: true), v => v.VersionRaw == VersionRaw);
        Assert.False(InModerationQueue(m.DbB, VersionRaw));
    }

    /// <summary>Идентификатор строки переносится между машинами: у B он тот же, что у A (B перенимает
    /// его при первом же совпадении по натуральному ключу — см. ImportHierarchyDataCore). Без этого
    /// всё, что проверено выше, держалось бы на одном лишь натуральном ключе.</summary>
    [Fact]
    public void FwVersionSyncId_IsAdoptedFromExportingMachine_OnFirstContact()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        // B заводит ту же самую прошивку САМ (одна общая шара, обе машины видели одну папку) — значит
        // sync_id у сторон разный, и это и есть «первый контакт двух независимых баз».
        var seedA = SeedFirmware(m.DbA, m.HierA, root);
        var seedB = SeedFirmware(m.DbB, m.HierB, root);
        var idA = m.DbA.GetFwVersionById(seedA.FwId)!.SyncId;
        var idBefore = m.DbB.GetFwVersionById(seedB.FwId)!.SyncId;
        Assert.NotEqual("", idA);
        Assert.NotEqual(idA, idBefore);

        SyncAtoB(m, root);

        var rowOnB = Assert.Single(m.DbB.GetAllFwVersionsWithNames(includeArchived: true).Where(v => v.VersionRaw == VersionRaw));
        Assert.Equal(idA, m.DbB.GetFwVersionById(rowOnB.Id!.Value)!.SyncId);
    }
}
