using System.Linq;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>УДАЛЁННЫЙ ПОДТИП НЕ ВОЗВРАЩАЕТСЯ.
///
/// Жалоба Ильи, повторённая трижды: «всё ещё не удаляется НГР 2.0, которую заменили НГР КПЧ».
/// Удаление срабатывало — и тут же откатывалось назад. Круг был такой:
/// <list type="number">
/// <item>оператор удаляет подтип у себя;</item>
/// <item>у коллеги под этим подтипом лежат версии, поэтому обмен конфигом удаление там НЕ повторяет
/// (стирать чужие записи по чужому снимку нельзя) — счётчик SubtypesSkippedDelete;</item>
/// <item>коллега выгружает конфиг, где подтип ЖИВОЙ;</item>
/// <item>подтип возвращается к тому, кто его удалял.</item>
/// </list>
///
/// Лечится надгробием: факт удаления перестаёт быть «отсутствием записи» и ездит в общем конфиге
/// своей строкой с датой (Database.HierarchyTombstones). Тесты закрепляют и то, что возврат
/// прекращён, и то, что надгробие не мешает завести подтип заново.</summary>
public class HierarchyTombstoneTests
{
    private static int AddSubtype(Database db, int groupId, string name) =>
        db.UpsertEquipmentSubtype(new EquipmentSubType
        {
            GroupId = groupId,
            Name = name,
            FolderName = name,
            SortOrder = 10,
        });

    private static int GroupId(Database db, string name) =>
        db.GetAllEquipmentGroups().First(g => g.Name == name).Id!.Value;

    private static bool HasSubtype(Database db, string group, string subtype) =>
        db.GetSubtypesForGroup(GroupId(db, group)).Any(s => s.Name == subtype);

    /// <summary>Главный случай целиком: две машины, под подтипом у второй лежит версия, первая его
    /// удаляет — и он НЕ приезжает обратно с конфигом второй.</summary>
    [Fact]
    public void Удалённый_подтип_не_возвращается_с_чужой_машины()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        // Обе машины знают подтип под одним sync_id — как после обычной синхронизации.
        var groupA = GroupId(m.DbA, "НГР");
        var subtypeA = AddSubtype(m.DbA, groupA, "2.0-тест");
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(root), root);
        Assert.True(HasSubtype(m.DbB, "НГР", "2.0-тест"), "подтип не доехал до второй машины");

        // У второй машины под подтипом появляется версия — из-за неё удаление там и пропускалось.
        var subtypeB = m.DbB.GetSubtypesForGroup(GroupId(m.DbB, "НГР")).First(s => s.Name == "2.0-тест");
        var modification = m.DbB.GetAllModifications().First(x => x.ControllerName == "SMH4");
        m.DbB.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtypeB.Id!.Value,
            ControllerId = modification.ControllerId,
            HwVersion = modification.HwVersion,
            SwVersion = 1,
            DtStr = "20260101_0000",
            VersionRaw = "9.9.9.1.20260101_0000",
            Filename = "занято.psl",
            DiskPath = "",
            Description = "чтобы подтип считался занятым",
            Status = "active",
        });

        // Первая машина удаляет подтип и рассказывает об этом общим конфигом.
        m.DbA.DeleteEquipmentSubtype(subtypeA);
        Assert.False(HasSubtype(m.DbA, "НГР", "2.0-тест"));
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(root), root);

        // У второй машины подтип остаётся — под ним данные, и это правильно.
        Assert.True(HasSubtype(m.DbB, "НГР", "2.0-тест"));

        // А вот обратно он больше не едет: её конфиг знает, что подтип удалён.
        ConfigSyncService.Export(m.SvcB, root, "profileB");
        ConfigSyncService.Apply(m.SvcA, ConfigSyncService.ConfigPathFor(root), root);

        Assert.False(HasSubtype(m.DbA, "НГР", "2.0-тест"),
            "подтип вернулся с чужой машины — ровно то, на что жаловались трижды");
    }

    /// <summary>ЭТАЛОННАЯ СИНХРОНИЗАЦИЯ СНОСИТ ПОДТИП ВМЕСТЕ С ЕГО ВЕРСИЯМИ.
    ///
    /// Уточнение Ильи 21.10.2026: «я удалял 2.0 и потом делал эталонную синхронизацию и всё равно не
    /// затиралось у остальных и возвращался 2.0». Так и было: мягкий предохранитель оставлял подтип
    /// у того, под чьим подтипом лежали версии, — и в обычном обмене это правильно, чужие записи по
    /// чужому снимку стирать нельзя. Но эталонная синхронизация означает ровно обратное:
    /// администратор объявляет полное состояние справочника для всех, а подтип он уже удалил у себя
    /// вместе с версиями.
    ///
    /// Снос происходит ТОЛЬКО по надгробию (явному удалению), а не по простому отсутствию записи во
    /// входящем снимке: иначе эталон сносил бы данные под подтипом, про который он просто не
    /// знает.</summary>
    [Fact]
    public void Эталонная_синхронизация_сносит_удалённый_подтип_вместе_с_версиями()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var subtypeA = AddSubtype(m.DbA, GroupId(m.DbA, "НГР"), "2.0-тест");
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(root), root);

        var subtypeB = m.DbB.GetSubtypesForGroup(GroupId(m.DbB, "НГР")).First(s => s.Name == "2.0-тест");
        var modification = m.DbB.GetAllModifications().First(x => x.ControllerName == "SMH4");
        m.DbB.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtypeB.Id!.Value,
            ControllerId = modification.ControllerId,
            HwVersion = modification.HwVersion,
            SwVersion = 1,
            DtStr = "20260101_0000",
            VersionRaw = "9.9.9.1.20260101_0000",
            Filename = "старая.psl",
            DiskPath = "",
            Description = "версия под заменённым подтипом",
            Status = "active",
        });

        m.DbA.DeleteEquipmentSubtype(subtypeA);

        // ОБЫЧНЫЙ обмен подтип у второй машины не трогает — под ним данные.
        var ordinary = m.DbB.ImportHierarchyData(m.DbA.ExportHierarchyData());
        Assert.Equal(1, ordinary.SubtypesSkippedDelete);
        Assert.True(HasSubtype(m.DbB, "НГР", "2.0-тест"));

        // ЭТАЛОННЫЙ — сносит вместе с версиями, и это видно отдельным счётчиком.
        var authoritative = m.DbB.ImportHierarchyData(m.DbA.ExportHierarchyData(), authoritative: true);

        Assert.Equal(1, authoritative.SubtypesRemovedWithData);
        Assert.False(HasSubtype(m.DbB, "НГР", "2.0-тест"),
            "эталонная синхронизация не убрала подтип — ровно то, на что жаловались");
        Assert.DoesNotContain(m.DbB.GetAllFwVersionsWithNames(includeArchived: true), v => v.Filename == "старая.psl");
    }

    /// <summary>А вот подтип, которого в эталонном снимке просто НЕТ (и надгробия на него тоже нет),
    /// эталон не трогает: его данные ни в чём не виноваты. Это предохранитель от сноса работы той
    /// машины, которая завела подтип сама и ещё не поделилась им.</summary>
    [Fact]
    public void Эталон_без_надгробия_данные_не_сносит()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        var onlyOnB = AddSubtype(m.DbB, GroupId(m.DbB, "НГР"), "только-у-B");
        var modification = m.DbB.GetAllModifications().First(x => x.ControllerName == "SMH4");
        m.DbB.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = onlyOnB,
            ControllerId = modification.ControllerId,
            HwVersion = modification.HwVersion,
            SwVersion = 1,
            DtStr = "20260101_0000",
            VersionRaw = "9.9.9.2.20260101_0000",
            Filename = "своя.psl",
            DiskPath = "",
            Description = "работа, про которую эталон не знает",
            Status = "active",
        });

        var counts = m.DbB.ImportHierarchyData(m.DbA.ExportHierarchyData(), authoritative: true);

        Assert.Equal(0, counts.SubtypesRemovedWithData);
        Assert.True(HasSubtype(m.DbB, "НГР", "только-у-B"));
        Assert.Contains(m.DbB.GetAllFwVersionsWithNames(includeArchived: true), v => v.Filename == "своя.psl");
    }

    /// <summary>Надгробие не запрещает завести подтип заново: у новой записи свой sync_id, и старое
    /// удаление к ней отношения не имеет. Иначе «удалил по ошибке» стало бы необратимым.</summary>
    [Fact]
    public void Завести_подтип_заново_надгробие_не_мешает()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);

        var group = GroupId(db, "НГР");
        var id = AddSubtype(db, group, "2.0-тест");
        db.DeleteEquipmentSubtype(id);

        AddSubtype(db, group, "2.0-тест");

        Assert.True(HasSubtype(db, "НГР", "2.0-тест"));
    }

    /// <summary>Правка ПОЗЖЕ удаления побеждает: коллега мог осознанно вернуть подтип и переименовать
    /// его. Это то же правило «кто позже, тот и прав», по которому живёт весь обмен.</summary>
    [Fact]
    public void Правка_позже_удаления_сильнее_удаления()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);

        db.MarkHierarchyDeleted(Database.TombstoneSubtype, "какой-то-sync-id", "НГР / 2.0",
            deletedAt: "2026-10-21 10:00:00");

        Assert.True(db.DeletedLaterThan(Database.TombstoneSubtype, "какой-то-sync-id", "2026-10-20 09:00:00"),
            "удаление новее правки — должно побеждать");
        Assert.False(db.DeletedLaterThan(Database.TombstoneSubtype, "какой-то-sync-id", "2026-10-22 09:00:00"),
            "правка новее удаления — должна побеждать");
    }

    /// <summary>Строки без sync_id надгробий не получают: соотнести их со входящим снимком всё равно
    /// нечем, а пустой ключ склеил бы в одно надгробие все такие записи разом.</summary>
    [Fact]
    public void Без_sync_id_надгробие_не_заводится()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);

        db.MarkHierarchyDeleted(Database.TombstoneSubtype, "", "НГР / без идентификатора");
        db.MarkHierarchyDeleted(Database.TombstoneSubtype, null, "НГР / тоже без");

        Assert.Empty(db.GetHierarchyTombstones());
    }

    /// <summary>Удаление типа шкафа кладёт надгробия и на его подтипы: они уходят вместе с ним
    /// (ON DELETE CASCADE) и без собственных надгробий вернулись бы поодиночке.</summary>
    [Fact]
    public void Удаление_типа_помечает_и_его_подтипы()
    {
        using var tmp = new TempDb();
        using var db = new Database(tmp.Path);

        var group = GroupId(db, "НГР");
        AddSubtype(db, group, "2.0-тест");
        db.DeleteEquipmentGroup(group);

        var tombstones = db.GetHierarchyTombstones();
        Assert.Contains(tombstones, t => t.Kind == Database.TombstoneGroup);
        Assert.Contains(tombstones, t => t.Kind == Database.TombstoneSubtype);
    }
}
