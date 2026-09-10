using System.IO;
using System.Linq;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Доставка САМОЙ прошивки — продолжение того же разбора, что и ModerationDeliveryTests, но
/// на шаг глубже. Узкий канал нёс РЕШЕНИЕ по версии и молчал про саму версию, поэтому у коллеги,
/// который прошивку ещё не видел, решение применять было не к чему (Database.ApplyModerationDecisions
/// честно пропускала его), а строка приезжала только полным экспортом администратора — который по
/// умолчанию не делается вовсе (config_push_interval_min = 0). Отсюда жалоба «залил две прошивки,
/// отмодерировал — у коллеги их наотрез нет».
///
/// Теперь канал (ConfigSyncService.PushFirmwareAndModerationOnly) дописывает в чужой снимок и строки
/// fw_versions, и их доп. материалы, не трогая ничего больше.</summary>
public class FirmwareDeliveryTests
{
    private const string Raw = "2.1.0044.0007.20260301_0900";
    private const string AdminRaw = "2.1.0044.0001.20260101_1200";

    private static FwVersionRecord? Row(Database db, string raw) =>
        db.GetAllFwVersionsWithNames(includeArchived: true).FirstOrDefault(v => v.VersionRaw == raw);

    /// <summary>Заводит прошивку с папкой на общем диске под указанным корнем — ровно то, что делает
    /// загрузка (FirmwareUploadService), только без самого копирования файлов.</summary>
    private static int Upload(Database db, string root, string raw, string description = "новая прошивка")
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var ctrl = db.GetAllControllerModels().First(c => c.Name == "SMH4");

        var versionDir = Path.Combine(root, "ПО", "НГР", "КНС", "SMH4", raw);
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "fw.psl"), "test");

        return db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = ctrl.Id!.Value,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = 44, SwVersion = 7, DtStr = raw[^13..],
            VersionRaw = raw, Filename = "fw.psl", DiskPath = versionDir,
            Description = description, Status = "active",
        });
    }

    /// <summary>Первый обмен: у администратора уже есть своя прошивка, он выгружает полный снимок,
    /// наладчик его принимает. Дальше во всех сценариях работает именно наладчик — машина, которая
    /// полный экспорт делать не вправе.</summary>
    private static void SeedAndShare(TwoMachines m)
    {
        Upload(m.DbA, m.Root.Path, AdminRaw, "прошивка администратора");
        ConfigSyncService.Export(m.SvcA, m.Root.Path, "администратор");

        var first = ConfigSyncService.CheckForUpdate(m.SvcB, out var err);
        Assert.True(err is null, err);
        Assert.NotNull(first);
        ConfigSyncService.Apply(m.SvcB, first!.ConfigPath, m.Root.Path);
    }

    /// <summary>Главный сценарий задачи. Прошивку заливает НЕ администратор — и она доезжает до
    /// остальных сама, без чьего-либо «Отправить всё». До расширения канала здесь не происходило
    /// ничего: своего полного экспорта у наладчика нет, а в снимке администратора этой строки быть
    /// не может.</summary>
    [Fact]
    public void UploadOnNonAdminMachine_ReachesEveryoneElse()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        SeedAndShare(m);

        var idOnB = Upload(m.DbB, m.Root.Path, Raw);
        Assert.Null(Row(m.DbA, Raw));

        Assert.True(ConfigSyncService.PushFirmwareChange(m.SvcB, new[] { idOnB }, "наладчик",
            new[] { $"Загружена прошивка {Raw}" }));

        var incoming = ConfigSyncService.CheckForUpdate(m.SvcA, out var err);
        Assert.True(err is null, err);
        Assert.NotNull(incoming);
        ConfigSyncService.Apply(m.SvcA, incoming!.ConfigPath, m.Root.Path);

        var arrived = Row(m.DbA, Raw);
        Assert.NotNull(arrived);
        Assert.Equal("новая прошивка", arrived!.Description);
    }

    /// <summary>Тот самый корень, ради которого канал и расширяли: решение модерации по прошивке,
    /// которой у коллеги ЕЩЁ НЕТ. Раньше решение приезжало в одиночку и на приёме отбрасывалось
    /// («строки нет — применять не к чему»), а сама версия ждала полного экспорта администратора.
    /// Теперь строка и решение едут одним снимком, и порядок разбора на приёме верный: блок
    /// fw_versions идёт ПЕРЕД решениями.</summary>
    [Fact]
    public void ModerationOfAVersionTheOthersNeverSaw_CarriesTheVersionItself()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        SeedAndShare(m);

        var idOnB = Upload(m.DbB, m.Root.Path, Raw);
        m.DbB.MarkFwVersionReleased(idOnB);
        Assert.True(ConfigSyncService.RecordAndPushModeration(m.SvcB, idOnB, "наладчик"));

        var incoming = ConfigSyncService.CheckForUpdate(m.SvcA, out var err);
        Assert.True(err is null, err);
        Assert.NotNull(incoming);
        ConfigSyncService.Apply(m.SvcA, incoming!.ConfigPath, m.Root.Path);

        var arrived = Row(m.DbA, Raw);
        Assert.NotNull(arrived);
        Assert.True(arrived!.Released, "решение «выпустить» должно доехать вместе с самой версией");
        Assert.Equal(0, m.DbA.GetUnreleasedFwVersionsCount());
    }

    /// <summary>Канал узкий и обязан таким остаться: в общий конфиг уходят названные строки и больше
    /// ничего. Ни справочник наладчика, ни его настройки, ни чужие строки прошивок в файле не
    /// шелохнулись.</summary>
    [Fact]
    public async System.Threading.Tasks.Task NarrowPush_TouchesNothingButTheNamedRows()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        SeedAndShare(m);

        // Наладчик завёл у себя свой тег и свою прошивку. Уехать должна только прошивка.
        m.DbB.AddTag("тег-наладчика");
        var idOnB = Upload(m.DbB, m.Root.Path, Raw);
        Assert.True(ConfigSyncService.PushFirmwareChange(m.SvcB, new[] { idOnB }, "наладчик"));

        var onDisk = (await ConfigSyncService.ReadCurrentDiskHierarchyAsync(m.Root.Path)).OnDisk!;
        Assert.Contains(onDisk.FwVersions, f => f.VersionRaw == Raw);
        Assert.Contains(onDisk.FwVersions, f => f.VersionRaw == AdminRaw);
        Assert.DoesNotContain("тег-наладчика", onDisk.Tags ?? new());

        // И на приёме у администратора нет ни одной чужой настройки.
        var incoming = ConfigSyncService.CheckForUpdate(m.SvcA, out _);
        Assert.NotNull(incoming);
        Assert.Equal(0, incoming!.SettingsChanged);
    }

    /// <summary>Повторная отправка той же строки не плодит в файле дублей: строки в массиве снимка
    /// заменяются по своему ключу (sync_id), а не дописываются вслепую. Дубль в снимке — это дубль
    /// прошивки у каждого получателя.</summary>
    [Fact]
    public async System.Threading.Tasks.Task PushingTheSameRowTwice_DoesNotDuplicateItInTheSnapshot()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        SeedAndShare(m);

        var idOnB = Upload(m.DbB, m.Root.Path, Raw);
        Assert.True(ConfigSyncService.PushFirmwareChange(m.SvcB, new[] { idOnB }, "наладчик"));
        Assert.True(ConfigSyncService.PushFirmwareChange(m.SvcB, new[] { idOnB }, "наладчик"));

        var onDisk = (await ConfigSyncService.ReadCurrentDiskHierarchyAsync(m.Root.Path)).OnDisk!;
        Assert.Single(onDisk.FwVersions.Where(f => f.VersionRaw == Raw));
        Assert.Single(onDisk.FwVersions.Where(f => f.VersionRaw == AdminRaw));
    }

    /// <summary>Правка прошивки почти всегда задевает и её документы, поэтому канал несёт обе секции
    /// сразу. Уехала бы одна строка — доставка обещала бы больше, чем делает: у коллеги появилась бы
    /// версия без приложенных к ней материалов.</summary>
    [Fact]
    public void NarrowPush_CarriesTheFirmwareDocumentsToo()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        SeedAndShare(m);

        var idOnB = Upload(m.DbB, m.Root.Path, Raw);
        m.DbB.AddFwAttachment(new FwAttachment
        {
            FwVersionId = idOnB, Filename = "руководство.pdf",
            DiskPath = Path.Combine(m.Root.Path, "ПО", "НГР", "КНС", "SMH4", Raw, "руководство.pdf"),
            Kind = FwAttachmentKinds.SetupGuide, Comment = "как запускать", AddedBy = "наладчик",
        });

        Assert.True(ConfigSyncService.PushFirmwareChange(m.SvcB, new[] { idOnB }, "наладчик"));

        var incoming = ConfigSyncService.CheckForUpdate(m.SvcA, out _);
        Assert.NotNull(incoming);
        ConfigSyncService.Apply(m.SvcA, incoming!.ConfigPath, m.Root.Path);

        var arrived = Row(m.DbA, Raw);
        Assert.NotNull(arrived);
        var docs = m.DbA.GetFwAttachments(arrived!.Id!.Value);
        Assert.Single(docs);
        Assert.Equal("руководство.pdf", docs[0].Filename);
    }

    /// <summary>Общего конфига на диске ещё нет — канал НЕ создаёт его сам, ровно как и в случае с
    /// одним решением модерации. Снимок с пустой иерархией получатель честно прочитал бы как «на
    /// источнике этих типов/подтипов/контроллеров нет» и зеркалил бы их удаление.</summary>
    [Fact]
    public void NarrowPush_NoSharedConfigYet_WritesNothing()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        var id = Upload(m.DbB, m.Root.Path, Raw);
        Assert.False(ConfigSyncService.PushFirmwareChange(m.SvcB, new[] { id }, "наладчик"));
        Assert.False(File.Exists(Path.Combine(m.Root.Path, "Конфиг", "po_finder_config.json")),
            "узкий канал не должен заводить общий конфиг с одними своими секциями");
    }

    /// <summary>Пути в снимке записаны в нотации корня, названного в source_root_path, — получатель
    /// переставляет на свой именно этот префикс. Мы дописываем свои строки в ЧУЖОЙ файл и его
    /// source_root_path не трогаем, значит обязаны сами переписать свои пути в его нотацию. Иначе у
    /// коллеги папка нашей прошивки указывает в никуда: одна и та же шара у машин бывает названа
    /// по-разному.</summary>
    [Fact]
    public async System.Threading.Tasks.Task NarrowPush_RewritesOwnPathsIntoTheSnapshotRootNotation()
    {
        using var m = new TwoMachines();
        var sharedRoot = m.Root.Path;
        // Вторая нотация того же самого физического каталога — как буква диска против UNC-адреса.
        var sameFolderOtherName = Path.Combine(sharedRoot, "ПО", "..");
        Directory.CreateDirectory(Path.Combine(sharedRoot, "ПО"));

        m.CfgA.SetRootPath(sharedRoot);
        m.CfgB.SetRootPath(sameFolderOtherName);

        Upload(m.DbA, sharedRoot, AdminRaw, "прошивка администратора");
        ConfigSyncService.Export(m.SvcA, sharedRoot, "администратор");
        ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(sharedRoot), sameFolderOtherName);

        var idOnB = Upload(m.DbB, sameFolderOtherName, Raw);
        Assert.Contains("..", m.DbB.GetAllFwVersionsWithNames().Single(v => v.VersionRaw == Raw).DiskPath);

        Assert.True(ConfigSyncService.PushFirmwareChange(m.SvcB, new[] { idOnB }, "наладчик"));

        var onDisk = (await ConfigSyncService.ReadCurrentDiskHierarchyAsync(sharedRoot)).OnDisk!;
        var pushed = onDisk.FwVersions.Single(f => f.VersionRaw == Raw);
        Assert.StartsWith(sharedRoot, pushed.DiskPath, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("..", pushed.DiskPath);
    }

    /// <summary>Прошивка, заведённая под несколькими подтипами шкафа, физически одна — и приехать к
    /// коллеге половиной записей она не должна. Канал сам добирает копии-ссылки
    /// (Database.GetFwVersionIdsSharingFiles), как это давно делает доставка решений модерации.</summary>
    [Fact]
    public async System.Threading.Tasks.Task NarrowPush_CarriesTheCopiesUnderOtherSubtypes()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        SeedAndShare(m);

        var idOnB = Upload(m.DbB, m.Root.Path, Raw);
        var main = m.DbB.GetAllFwVersionsWithNames().Single(v => v.VersionRaw == Raw);
        var group = m.DbB.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var otherSubtype = m.DbB.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name != "КНС");

        // Копия-ссылка: те же файлы на диске, своя строка под другим подтипом шкафа.
        var copyId = m.DbB.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = otherSubtype.Id!.Value, ControllerId = main.ControllerId,
            EqPrefix = group.Prefix, SubPrefix = otherSubtype.Prefix,
            HwVersion = 44, SwVersion = 7, DtStr = main.DtStr,
            VersionRaw = Raw, Filename = "fw.psl", DiskPath = main.DiskPath,
            Description = "копия под другой подтип", Status = "active",
        });
        Assert.Contains(copyId, m.DbB.GetFwVersionIdsSharingFiles(idOnB));

        Assert.True(ConfigSyncService.PushFirmwareChange(m.SvcB, new[] { idOnB }, "наладчик"));

        var onDisk = (await ConfigSyncService.ReadCurrentDiskHierarchyAsync(m.Root.Path)).OnDisk!;
        Assert.Equal(2, onDisk.FwVersions.Count(f => f.VersionRaw == Raw));
    }
}
