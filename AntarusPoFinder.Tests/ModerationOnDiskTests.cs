using System.IO;
using System.Linq;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>РЕШЕНИЕ МОДЕРАЦИИ, КОТОРОЕ ЕДЕТ ДИСКОМ.
///
/// Откуда взялось. 14.10.2026 Илья: «проверь почему у коллеги 21 прошивок на модерации висит а у меня
/// 4». Слепки двух машин ответили точно: 46 общих прошивок, у 28 из них РАЗНЫЕ sync_id (каждая машина
/// завела строку сама, насканировав общий диск), и 16 версий, выпущенных у Ильи, у коллеги всё ещё
/// числились в очереди. То есть обмен конфигом между этими двумя машинами не работал — и не работал
/// МОЛЧА: каталоги выглядели одинаковыми именно потому, что обе собрали их с диска самостоятельно.
///
/// Диск при этом работал заведомо: 10 854 файла против 10 858, разница — одна свежая версия. Поэтому
/// решение теперь едет и там, в CHANGELOG.md папки версии, рядом с описанием, типом пуска и тегами.
///
/// Здесь проверяется ровно то, что делает этот канал пригодным: отметка переживает чужой файл и
/// ручные правки, принимается монотонно, и молчание старого файла не считается решением.</summary>
public class ModerationOnDiskTests
{
    private const string VersionRaw = "2.1.0004.0009.20261014_1000";

    private static (int FwId, string Folder) SeedFirmware(Database db, HierarchyService hier, string root)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");

        var folder = hier.FwPath(root, group.Name, subtype.Name, mod.ControllerName, VersionRaw);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "fw.psl"), "test firmware");
        ChangelogFile.Write(folder, FwVersionNumber.Parse(VersionRaw)!, new[] { "Ручной" },
            "описание версии", new[] { "насос" });

        var id = db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion, SwVersion = 9, DtStr = "20261014_1000",
            VersionRaw = VersionRaw, Filename = "fw.psl", DiskPath = folder,
            Description = "описание версии", Changelog = "описание версии", Status = "active",
        });
        return (id, folder);
    }

    private static bool InQueue(Database db) =>
        db.GetUnreleasedFwVersionsWithNames().Any(v => v.VersionRaw == VersionRaw);

    /// <summary>Главный случай, ради которого всё затевалось: обмен конфигом между машинами НЕ
    /// работает (здесь он просто не вызывается ни разу), но версия, выпущенная на одной машине, у
    /// второй из очереди модерации уходит — через общий диск.</summary>
    [Fact]
    public void Решение_с_диска_убирает_версию_из_очереди_на_второй_машине()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var a = SeedFirmware(m.DbA, m.HierA, root);
        var b = SeedFirmware(m.DbB, m.HierB, root);   // та же папка, но своя строка со своим sync_id
        Assert.True(InQueue(m.DbB));

        m.DbA.MarkFwVersionReleased(a.FwId);
        ModerationOnDisk.Write(m.DbA, new[] { a.FwId }, "Иванов");

        var absorbed = ModerationOnDisk.Absorb(m.DbB);

        Assert.Equal(1, absorbed.Released);
        Assert.False(InQueue(m.DbB), "решение, записанное на диск, обязано доехать без обмена конфигом");
        Assert.True(m.DbB.GetFwVersionById(b.FwId)!.Released);
    }

    /// <summary>Канал обязан работать ровно там, где не работает прежний: у двух машин РАЗНЫЕ sync_id
    /// на одну и ту же папку — так и было на практике, у 28 прошивок из 46. Отметка на диске привязана
    /// к папке, а не к sync_id, поэтому расхождение ей не мешает.</summary>
    [Fact]
    public void Разные_sync_id_на_одну_папку_каналу_не_мешают()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var a = SeedFirmware(m.DbA, m.HierA, root);
        var b = SeedFirmware(m.DbB, m.HierB, root);

        var syncA = m.DbA.GetFwVersionById(a.FwId)!.SyncId;
        var syncB = m.DbB.GetFwVersionById(b.FwId)!.SyncId;
        Assert.NotEqual(syncA, syncB);

        m.DbA.MarkFwVersionReleased(a.FwId);
        ModerationOnDisk.Write(m.DbA, new[] { a.FwId }, "Иванов");

        Assert.Equal(1, ModerationOnDisk.Absorb(m.DbB).Released);
    }

    /// <summary>Монотонность: «в очереди» на диске не снимает уже принятого здесь решения. Снимок мог
    /// быть записан ДО выпуска, и слушаться его значило бы возвращать в модерацию выпущенное.</summary>
    [Fact]
    public void Диск_не_возвращает_выпущенную_версию_в_очередь()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var a = SeedFirmware(m.DbA, m.HierA, root);
        m.DbA.MarkFwVersionReleased(a.FwId);
        ChangelogFile.TryMarkModeration(a.Folder, new FwModerationMark(false, false, "2026-10-01T00:00:00", "Петров"));

        var absorbed = ModerationOnDisk.Absorb(m.DbA);

        Assert.Equal(0, absorbed.Released);
        Assert.True(m.DbA.GetFwVersionById(a.FwId)!.Released, "выпущенная версия обратно в очередь не возвращается");
    }

    /// <summary>Старый CHANGELOG.md строки «Модерация:» не содержит вовсе, и это НЕ «в очереди».
    /// Принять молчание за решение значило бы снимать чужие выпуски при первом же досмотре диска.</summary>
    [Fact]
    public void Файл_без_отметки_решением_не_считается()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var a = SeedFirmware(m.DbA, m.HierA, root);
        m.DbA.MarkFwVersionReleased(a.FwId);
        var before = File.ReadAllText(Path.Combine(a.Folder, ChangelogFile.FileName));
        Assert.DoesNotContain(ChangelogFile.ModerationPrefix, before);

        var absorbed = ModerationOnDisk.Absorb(m.DbA);

        Assert.Equal(0, absorbed.Released);
        Assert.True(m.DbA.GetFwVersionById(a.FwId)!.Released);
    }

    /// <summary>Архив — тот же третий признак очереди, что и у обмена конфигом: версия, убранная в
    /// архив на одной машине, не должна висеть в модерации у остальных.</summary>
    [Fact]
    public void Архив_тоже_едет_диском()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var a = SeedFirmware(m.DbA, m.HierA, root);
        var b = SeedFirmware(m.DbB, m.HierB, root);

        m.DbA.ArchiveFwVersion(a.FwId);
        ModerationOnDisk.Write(m.DbA, new[] { a.FwId }, "Иванов");

        Assert.Equal(1, ModerationOnDisk.Absorb(m.DbB).Archived);
        Assert.False(InQueue(m.DbB));
    }

    /// <summary>Отметка не съедает файл: описание, тип пуска и теги, ради которых CHANGELOG.md и
    /// существует, остаются на месте. Файл этот люди правят руками прямо на сетевом диске.</summary>
    [Fact]
    public void Отметка_не_затирает_остальной_файл()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var a = SeedFirmware(m.DbA, m.HierA, root);
        m.DbA.MarkFwVersionReleased(a.FwId);
        ModerationOnDisk.Write(m.DbA, new[] { a.FwId }, "Иванов");

        var back = ChangelogFile.TryRead(a.Folder)!;

        Assert.Equal("описание версии", back.Description);
        Assert.Contains("Ручной", back.LaunchTypes);
        Assert.Contains("насос", back.Tags);
        Assert.True(back.Moderation!.Released);
        Assert.Equal("Иванов", back.Moderation.By);
    }

    /// <summary>Повторное решение заменяет прежнюю строку, а не дописывает вторую: источник истины
    /// один — последнее решение. Иначе файл обрастал бы историей, а разбор читал бы первую попавшуюся.</summary>
    [Fact]
    public void Повторная_отметка_не_плодит_строк()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var a = SeedFirmware(m.DbA, m.HierA, root);
        ChangelogFile.TryMarkModeration(a.Folder, new FwModerationMark(false, false, "2026-10-01T00:00:00", "Петров"));
        ChangelogFile.TryMarkModeration(a.Folder, new FwModerationMark(true, false, "2026-10-14T10:00:00", "Иванов"));

        var text = File.ReadAllText(Path.Combine(a.Folder, ChangelogFile.FileName));
        var count = text.Split(ChangelogFile.ModerationPrefix).Length - 1;

        Assert.Equal(1, count);
        Assert.True(ChangelogFile.TryRead(a.Folder)!.Moderation!.Released);
    }

    /// <summary>Папки на диске нет (версия только в базе) — канал молчит, а не падает: на чужой шаре
    /// прав может не быть вовсе, и ронять из-за этого модерацию нельзя.</summary>
    [Fact]
    public void Недоступная_папка_не_роняет_модерацию()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var a = SeedFirmware(m.DbA, m.HierA, root);
        Directory.Delete(a.Folder, recursive: true);

        Assert.Equal(0, ModerationOnDisk.Write(m.DbA, new[] { a.FwId }, "Иванов"));
        Assert.Equal(0, ModerationOnDisk.Absorb(m.DbA).Released);
    }
}
