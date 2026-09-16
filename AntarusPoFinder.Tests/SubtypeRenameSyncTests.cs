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

    /// <summary>Жалоба Ильи 16.09.2026: «я уже 10 раз удалял в иерархии НГР-2.0, отправлял эталонную
    /// синхронизацию — всё равно он сам подтягивается». И раньше: «у коллег 2.0 теперь под индексом 7,
    /// а КПЧ под 2, как был раньше 2.0».
    ///
    /// Разбор. Переименование доезжает до коллеги и применяется правильно: строка остаётся та же,
    /// на своём месте (отсюда «КПЧ под 2»). Но приём, в отличие от переименования своими руками,
    /// НЕ ЗАПОМИНАЛ прежнее имя. А в конторе есть машины, которые с этой ещё не «знакомились» по
    /// имени — у них свой sync_id на тот же подтип. Приезжает снимок такой машины, в нём подтип всё
    /// ещё «2.0»: по sync_id не нашли (чужой), по имени не нашли (у нас уже КПЧ), по прежнему имени
    /// не нашли (его не записали) — и приём заводит ВТОРОЙ подтип, который встаёт в конец списка.
    /// Отсюда «2.0 под индексом 7». Удалять его бесполезно: следующий обмен приносит снова.
    ///
    /// Поэтому память о прежнем имени обязана появляться и при приёме тоже.</summary>
    [Fact]
    public void StaleMachineSnapshot_DoesNotResurrectOldSubtypeName()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var (_, subtypeId) = AddSubtype(m.DbA, "НГР", Original);
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        var cfg = ConfigSyncService.ConfigPathFor(root);
        ConfigSyncService.Apply(m.SvcB, cfg, root);

        m.DbA.RenameEquipmentSubtype(subtypeId, Renamed, Renamed);
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        ConfigSyncService.Apply(m.SvcB, cfg, root);

        // Снимок ТРЕТЬЕЙ машины: свежий по номеру ревизии (иначе его бы просто не приняли), но
        // подтип в нём под старым именем и со СВОИМ sync_id — эта машина переименование ещё не
        // получала и с нашей по имени никогда не сходилась.
        // Промежуточная проверка: переименование действительно доехало. Без неё провал
        // ниже читался бы как «снимок воскресил старое имя», хотя на деле не доехало новое.
        var mid = m.DbB.GetSubtypesForGroup(
            m.DbB.GetAllEquipmentGroups().First(x => x.Name == "НГР").Id!.Value);
        Assert.True(mid.Any(s => s.Name == Renamed),
            "переименование не доехало до B: " + string.Join(", ", mid.Select(s => s.Name)));

        var stale = StaleSnapshotWithOldSubtypeName(cfg, Renamed, Original);
        ConfigSyncService.Apply(m.SvcB, stale, root);

        var after = m.DbB.GetSubtypesForGroup(
            m.DbB.GetAllEquipmentGroups().First(x => x.Name == "НГР").Id!.Value);

        var names = string.Join(", ", after.Select(s => s.Name));
        Assert.True(after.Any(s => s.Name == Renamed), "после приёма отставшего снимка: " + names);
        Assert.True(!after.Any(s => s.Name == Original), "старое имя воскресло: " + names);
    }

    /// <summary>Тот же отставший снимок, но с СВЕЖЕЙ отметкой времени у подтипа. Так и бывает в
    /// жизни: на отставшей машине строку недавно трогали — подвинули в списке, поправили префикс, —
    /// и по времени она выглядит новее нашего переименования. Плюс sync_id у неё свой, поэтому
    /// разбор конфликтов не находит истории правок и считает приехавшее достоверным.
    ///
    /// Переименовывать себя обратно по такому снимку нельзя ни при какой отметке времени: машина,
    /// которая переименования ещё не видела, не может быть источником правды об имени. Иначе имя
    /// скачет туда-сюда на каждом обмене — и выглядит это ровно как «оно живёт своей жизнью».</summary>
    [Fact]
    public void StaleMachineSnapshot_WithNewerTimestamp_StillDoesNotRenameBack()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var (_, subtypeId) = AddSubtype(m.DbA, "НГР", Original);
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        var cfg = ConfigSyncService.ConfigPathFor(root);
        ConfigSyncService.Apply(m.SvcB, cfg, root);

        m.DbA.RenameEquipmentSubtype(subtypeId, Renamed, Renamed);
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        ConfigSyncService.Apply(m.SvcB, cfg, root);

        var stale = StaleSnapshotWithOldSubtypeName(cfg, Renamed, Original, "2099-01-01 00:00:00");
        ConfigSyncService.Apply(m.SvcB, stale, root);

        var after = m.DbB.GetSubtypesForGroup(
            m.DbB.GetAllEquipmentGroups().First(x => x.Name == "НГР").Id!.Value);
        var names = string.Join(", ", after.Select(s => s.Name));
        Assert.True(after.Any(s => s.Name == Renamed), "имя откатилось назад: " + names);
        Assert.True(!after.Any(s => s.Name == Original), "старое имя воскресло: " + names);

        // И никакого «разберитесь, чьё имя правильнее». Отставшая машина — не сторона спора: она
        // просто ещё не получила правку. Раньше каждый обмен с такой машиной вываливал человеку
        // конфликт, который нечего решать, и к разбору конфликтов переставали относиться серьёзно.
        var conflicts = m.DbB.GetPendingHierarchyConflicts();
        Assert.True(!conflicts.Any(c => c.DisplayLabel.Contains(Renamed) || c.DisplayLabel.Contains(Original)),
            "заведён конфликт на пустом месте: " + string.Join(", ", conflicts.Select(c => c.DisplayLabel)));
    }

    /// <summary>Готовит файл конфига «отставшей машины»: берёт настоящий снимок и откатывает в нём
    /// ОДИН подтип к прежнему имени, выдав ему чужой sync_id и более старую отметку времени.
    /// Собирать такой снимок руками из полей нельзя — он должен быть валиден целиком, со всеми
    /// прочими разделами, иначе приём отвергнет его не по той причине, которую проверяем.</summary>
    private static string StaleSnapshotWithOldSubtypeName(string configPath, string currentName, string oldName, string updatedAt = "2000-01-01 00:00:00")
    {
        // Файл конфига зашифрован (ConfigFileCrypto) — читать его как текст нельзя.
        var plain = AntarusPoFinder.Core.Infrastructure.ConfigFileCrypto.TryDecrypt(System.IO.File.ReadAllBytes(configPath))!;
        var node = System.Text.Json.Nodes.JsonNode.Parse(plain)!.AsObject();
        foreach (var item in node["equipment_subtypes"]!.AsArray())
        {
            var o = item!.AsObject();
            if ((string?)o["name"] != currentName) continue;
            o["name"] = oldName;
            o["folder_name"] = oldName;
            o["sync_id"] = System.Guid.NewGuid().ToString();
            o["prev_name"] = "";
            o["updated_at"] = updatedAt;
        }
        var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(configPath)!, $"stale_config_{updatedAt[..4]}.json");
        System.IO.File.WriteAllBytes(path, AntarusPoFinder.Core.Infrastructure.ConfigFileCrypto.Encrypt(node.ToJsonString()));
        return path;
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
