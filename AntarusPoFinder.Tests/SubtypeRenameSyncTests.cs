using System.Linq;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Переименование подтипа между машинами.
///
/// Случай Ильи (09.09.2026): «переименовал в НГР подтип 2.0 в КПЧ — локально норм, а у коллег всё
/// посыпалось: привязки, названия, начало 2.0 вылазить из ниоткуда, поиск сломался».
///
/// Причина: подтип опознаётся на приёме по sync_id, а если он ещё не согласован между базами
/// (каждая сгенерировала свой при миграции) — по имени группы и подтипа. Переименование ломает
/// вторую дорогу: по НОВОМУ имени подтип не находится, и приём заводит второй, оставляя старый
/// рядом. Прошивки остаются висеть на старом, поиск показывает оба.
///
/// Лечится памятью о прежнем имени: отправитель кладёт его в снимок, приёмник узнаёт по нему свою
/// строку и правит её, а не плодит новую.</summary>
public class SubtypeRenameSyncTests
{
    // Свой подтип, а не из справочника: наборы seed на машинах различаются, и тест, опирающийся
    // на них, проверял бы состояние справочника, а не перенос переименования.
    private const string Original = "ТЕСТ-ИСХОДНЫЙ";
    private const string Renamed = "ТЕСТ-НОВЫЙ";

    private static (int GroupId, int SubtypeId) AddSubtype(Database db, string group, string name)
    {
        var g = db.GetAllEquipmentGroups().First(x => x.Name == group);
        var id = db.UpsertEquipmentSubtype(new AntarusPoFinder.Core.Domain.EquipmentSubType
        {
            GroupId = g.Id!.Value, Name = name, FolderName = name, Prefix = 91, SortOrder = 900,
        });
        return (g.Id!.Value, id);
    }

    /// <summary>Главный случай: sync_id у машин РАЗНЫЕ (независимо заведённые базы), подтип
    /// переименован. У коллеги должен остаться ОДИН подтип с новым именем, а не два.
    /// Имя берём свободное: «КПЧ» в справочнике уже есть, и переименование в него слило бы две
    /// строки — проверка прошла бы вхолостую.</summary>
    [Fact]
    public void RenamedSubtype_WithUnsyncedIds_DoesNotDuplicateOnOtherMachine()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var (groupId, subtypeId) = AddSubtype(m.DbA, "НГР", Original);

        // Сначала подтип должен ДОЕХАТЬ до второй машины — иначе ломать там нечего и проверка
        // проходит вхолостую (на этом я уже попался: тест зеленел и без починки).
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(root), root);

        // И только теперь разводим sync_id — то состояние, в котором базы «не познакомились»:
        // каждая сгенерировала свой при миграции, и опознать строку можно лишь по имени.
        ScrambleSubtypeSyncId(m.PathB, Original);

        m.DbA.RenameEquipmentSubtype(subtypeId, Renamed, Renamed);
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(root), root);

        var after = m.DbB.GetSubtypesForGroup(
            m.DbB.GetAllEquipmentGroups().First(x => x.Name == "НГР").Id!.Value);

        // Проверяем суть, а не число строк: seed у разных схем может отличаться, и сравнение
        // количеств ловило бы этот шум, а не дубликат. Дубликат — это когда старое имя осталось
        // рядом с новым; именно так у коллег и «вылезало 2.0 из ниоткуда».
        Assert.Contains(after, s => s.Name == Renamed);
        Assert.DoesNotContain(after, s => s.Name == Original);
        Assert.Single(after.Where(s => s.Name == Renamed));
    }

    /// <summary>Привязки не должны разъехаться: прошивка, заведённая до переименования, обязана
    /// остаться у того же подтипа — иначе она пропадёт из поиска по новому имени.</summary>
    [Fact]
    public void RenamedSubtype_KeepsFirmwareAttached()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var (_, subtypeId) = AddSubtype(m.DbA, "НГР", Original);
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(root), root);

        ScrambleSubtypeSyncId(m.PathB, Original);

        m.DbA.RenameEquipmentSubtype(subtypeId, Renamed, Renamed);
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(root), root);

        var groupB = m.DbB.GetAllEquipmentGroups().First(x => x.Name == "НГР").Id!.Value;
        var subs = m.DbB.GetSubtypesForGroup(groupB);
        var renamed = subs.SingleOrDefault(s => s.Name == Renamed);
        Assert.NotNull(renamed);
    }

    /// <summary>Переименование в обычном случае (sync_id уже общий) как работало, так и работает —
    /// проверяем, что новая дорога ничего не сломала.</summary>
    [Fact]
    public void RenamedSubtype_WithSharedSyncId_StillRenames()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var (_, subtypeId) = AddSubtype(m.DbA, "НГР", Original);
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(root), root);

        m.DbA.RenameEquipmentSubtype(subtypeId, Renamed, Renamed);
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(root), root);

        var groupB = m.DbB.GetAllEquipmentGroups().First(x => x.Name == "НГР").Id!.Value;
        var subs = m.DbB.GetSubtypesForGroup(groupB);
        Assert.Contains(subs, s => s.Name == Renamed);
        Assert.DoesNotContain(subs, s => s.Name == Original);
    }

    /// <summary>Правка одного лишь префикса не должна затирать память о прежнем имени: иначе
    /// подсказка приёмнику пропадёт впустую, и следующее переименование снова задвоит подтип.</summary>
    [Fact]
    public void RenameWithSameName_DoesNotWipePrevName()
    {
        using var m = new TwoMachines();
        var (_, subtypeId) = AddSubtype(m.DbA, "НГР", Original);

        m.DbA.RenameEquipmentSubtype(subtypeId, Renamed, Renamed);
        Assert.Equal(Original, PrevNameOf(m.PathA, subtypeId));

        m.DbA.RenameEquipmentSubtype(subtypeId, Renamed, "другая-папка");
        Assert.Equal(Original, PrevNameOf(m.PathA, subtypeId));
    }

    /// <summary>Главная поломка из жалобы: у КОЛЛЕГ разъезжаются привязки. Папку на общем диске
    /// переименовывает та машина, где правили, и там же переписывает пути у себя. К остальным
    /// доезжает только новое имя подтипа — а их прошивки продолжают указывать на папку, которой
    /// больше нет. Отсюда «привязки посыпались», «2.0 вылазит из ниоткуда» и сломанный поиск.</summary>
    [Fact]
    public void RenamedSubtype_RepairsFirmwarePathsOnOtherMachine()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var (_, subtypeId) = AddSubtype(m.DbA, "НГР", Original);
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(root), root);

        // Прошивка на машине B, чей путь ведёт в папку подтипа со СТАРЫМ именем.
        var oldDir = System.IO.Path.Combine(root, "ПО", "НГР", Original, "SMH4", "9.9.9999.0001");
        SeedFwWithPath(m.PathB, oldDir);

        m.DbA.RenameEquipmentSubtype(subtypeId, Renamed, Renamed);
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(root), root);

        var path = FwPathOf(m.PathB, "9.9.9999.0001");
        Assert.DoesNotContain(Original, path);
        Assert.Contains(Renamed, path);
    }

    private static void SeedFwWithPath(string dbPath, string diskPath)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO fw_versions (subtype_id, controller_id, eq_prefix, sub_prefix, hw_version,
                                     sw_version, dt_str, version_raw, filename, disk_path, sync_id,
                                     upload_date)
            VALUES (1, 1, 2, 1, 4, 1, '20260909_1200', '9.9.9999.0001', 'fw.psl', @d, @sy,
                    '2026-09-09 12:00:00')
            """;
        cmd.Parameters.AddWithValue("@d", diskPath);
        cmd.Parameters.AddWithValue("@sy", System.Guid.NewGuid().ToString());
        cmd.ExecuteNonQuery();
    }

    private static string FwPathOf(string dbPath, string versionRaw)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT disk_path FROM fw_versions WHERE version_raw=@v";
        cmd.Parameters.AddWithValue("@v", versionRaw);
        return (string)(cmd.ExecuteScalar() ?? "");
    }

    private static void ScrambleSubtypeSyncId(string dbPath, string subtypeName)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE equipment_subtypes SET sync_id=@sy WHERE name=@n";
        cmd.Parameters.AddWithValue("@sy", System.Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@n", subtypeName);
        cmd.ExecuteNonQuery();
    }

    private static string PrevNameOf(string dbPath, int subtypeId)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(prev_name,'') FROM equipment_subtypes WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", subtypeId);
        return (string)(cmd.ExecuteScalar() ?? "");
    }
}
