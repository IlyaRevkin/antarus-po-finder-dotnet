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
        ModerationOnDisk.Write(m.DbA, new[] { a.FwId }, "Иванов", m.Root.Path);

        var absorbed = ModerationOnDisk.Absorb(m.DbB, m.Root.Path);

        Assert.Equal(1, absorbed.Released);
        Assert.False(InQueue(m.DbB), "решение, записанное на диск, обязано доехать без обмена конфигом");
        Assert.True(m.DbB.GetFwVersionById(b.FwId)!.Released);
    }

    /// <summary>У КОЛЛЕГИ ДИСК ПОДКЛЮЧЁН ПО-ДРУГОМУ — и канал обязан работать всё равно.
    ///
    /// Жалоба Ильи 21.10.2026: «у коллеги всё ещё много на модерации висит» — уже ПОСЛЕ того, как
    /// канал через диск был сделан и выложен. Причина оказалась обидной: в fw_versions.disk_path
    /// лежит путь ТОЙ машины, что заливала прошивку (у кого буква диска, у кого сетевой адрес), а
    /// отметки читались и писались ровно по нему. На машине с другим подключением общей папки такой
    /// путь не существует — Directory.Exists ложен, и канал молчал целиком: ни прочитать чужое, ни
    /// выложить своё.
    ///
    /// Здесь это воспроизведено так же, как бывает в конторе: у второй машины в базе лежит путь с
    /// чужим корнем («Z:\Software\…»), а общий диск подключён в другое место.</summary>
    [Fact]
    public void Решение_доезжает_даже_если_диск_подключён_в_другое_место()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var a = SeedFirmware(m.DbA, m.HierA, root);
        var b = SeedFirmware(m.DbB, m.HierB, root);

        // Подменяем путь второй машины на «чужой» — такой, какого на этой машине нет.
        var stored = m.DbB.GetFwVersionById(b.FwId)!.DiskPath;
        var foreign = @"Z:\Software\Antarus Finder" + stored[(stored.IndexOf("ПО", StringComparison.Ordinal) - 1)..];
        m.DbB.RepointFwVersionDiskPath(b.FwId, foreign);
        Assert.False(Directory.Exists(foreign), "путь должен быть заведомо недоступен");

        m.DbA.MarkFwVersionReleased(a.FwId);
        ModerationOnDisk.Write(m.DbA, new[] { a.FwId }, "Иванов", root);

        var absorbed = ModerationOnDisk.Absorb(m.DbB, root);

        Assert.Equal(1, absorbed.Released);
        Assert.False(InQueue(m.DbB),
            "решение не доехало: путь из базы не привели к своему корню — именно из-за этого у коллеги и висела очередь");
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
        ModerationOnDisk.Write(m.DbA, new[] { a.FwId }, "Иванов", m.Root.Path);

        Assert.Equal(1, ModerationOnDisk.Absorb(m.DbB, m.Root.Path).Released);
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

        var absorbed = ModerationOnDisk.Absorb(m.DbA, m.Root.Path);

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

        var absorbed = ModerationOnDisk.Absorb(m.DbA, m.Root.Path);

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
        ModerationOnDisk.Write(m.DbA, new[] { a.FwId }, "Иванов", m.Root.Path);

        Assert.Equal(1, ModerationOnDisk.Absorb(m.DbB, m.Root.Path).Archived);
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
        ModerationOnDisk.Write(m.DbA, new[] { a.FwId }, "Иванов", m.Root.Path);

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

        Assert.Equal(0, ModerationOnDisk.Write(m.DbA, new[] { a.FwId }, "Иванов", m.Root.Path));
        Assert.Equal(0, ModerationOnDisk.Absorb(m.DbA, m.Root.Path).Released);
    }

    /// <summary>⚠️ ТО, ЧЕГО НЕ ХВАТИЛО В 1.75.0.0.
    ///
    /// Отметка писалась ТОЛЬКО в момент нажатия «вывести из модерации». Версии, выпущенные раньше,
    /// остались на диске без неё — и у коллеги продолжили висеть в очереди. Жалоба Ильи сразу после
    /// выхода: «обращаю внимание модерации не подчинилась».
    ///
    /// Теперь машина выкладывает на диск ВСЁ, что у неё уже решено, — и только после этого канал
    /// действительно начинает работать.</summary>
    [Fact]
    public void Ранее_выпущенные_версии_тоже_попадают_на_диск()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var a = SeedFirmware(m.DbA, m.HierA, root);
        var b = SeedFirmware(m.DbB, m.HierB, root);
        // Выпущено «давно», без всякой отметки на диске — ровно как у всех версий до 1.75.0.0.
        m.DbA.MarkFwVersionReleased(a.FwId);
        Assert.Null(ChangelogFile.TryRead(a.Folder)!.Moderation);

        Assert.Equal(1, ModerationOnDisk.Publish(m.DbA, "Иванов", m.Root.Path));

        Assert.True(ChangelogFile.TryRead(a.Folder)!.Moderation!.Released);
        Assert.Equal(1, ModerationOnDisk.Absorb(m.DbB, m.Root.Path).Released);
        Assert.False(InQueue(m.DbB));
    }

    /// <summary>Выкладка не трогает файл, если на диске уже стоит не менее сильная отметка: каждая
    /// лишняя запись — это обращение к чужой папке на общей шаре и повод для отказа в доступе.</summary>
    [Fact]
    public void Повторная_выкладка_ничего_не_переписывает()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var a = SeedFirmware(m.DbA, m.HierA, root);
        m.DbA.MarkFwVersionReleased(a.FwId);
        Assert.Equal(1, ModerationOnDisk.Publish(m.DbA, "Иванов", m.Root.Path));

        Assert.Equal(0, ModerationOnDisk.Publish(m.DbA, "Иванов", m.Root.Path));
    }

    /// <summary>Выкладка не ослабляет чужое решение: если на диске «выпущена», а здесь версия ещё и
    /// в архиве, на диск уходит и то и другое, а не замена одного другим.</summary>
    [Fact]
    public void Выкладка_не_ослабляет_отметку_на_диске()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var a = SeedFirmware(m.DbA, m.HierA, root);
        ChangelogFile.TryMarkModeration(a.Folder, new FwModerationMark(true, false, "2026-10-01T00:00:00", "Петров"));
        m.DbA.ArchiveFwVersion(a.FwId);

        ModerationOnDisk.Publish(m.DbA, "Иванов", m.Root.Path);

        var mark = ChangelogFile.TryRead(a.Folder)!.Moderation!;
        Assert.True(mark.Released, "чужое «выпущена» обязано остаться");
        Assert.True(mark.Archived);
    }

    /// <summary>Невыпущенную версию выкладывать нечего: «в очереди» — это отсутствие решения, а не
    /// решение, и записывать его на общий диск значило бы спорить с теми, кто уже решил.</summary>
    [Fact]
    public void Версия_в_очереди_на_диск_не_выкладывается()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        SeedFirmware(m.DbA, m.HierA, root);

        Assert.Equal(0, ModerationOnDisk.Publish(m.DbA, "Иванов", m.Root.Path));
    }
}
