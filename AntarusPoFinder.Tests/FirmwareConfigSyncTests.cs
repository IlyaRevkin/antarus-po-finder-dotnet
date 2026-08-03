using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Конфигурации шкафов между машинами (см. FirmwareConfigService). Заготовленный ряд
/// комплектаций бесполезен, если он живёт только на машине программиста: ищет по названию шкафа
/// НАЛАДЧИК, у себя.
///
/// Главная ловушка, которую здесь и проверяем: у всех конфигураций одной прошивки натуральный ключ
/// синхронизации совпадает — подтип, контроллер и version_raw у них одни и те же (файлы-то общие).
/// Пока в ключ не входило имя варианта, приёмник соотносил КАЖДУЮ приехавшую конфигурацию с одной и
/// той же локальной строкой: весь ряд схлопывался в одну запись с объединёнными тегами, и поиск по
/// названию конкретного шкафа переставал различать комплектации.</summary>
public class FirmwareConfigSyncTests
{
    private const string Cabinet2Pumps = "Шкаф управления пожарными насосами АМПЕРУС ПЖ-ПП-2-(9-14А)-АВР-FD-Ст";
    private const string CabinetJockey = "Шкаф управления пожарными насосами АМПЕРУС ПЖ-ПП-2-(24-32А)/Пд-(6А)/Зд1(4А)-АВР-FD-Ст";

    /// <summary>Прошивка на машине с настоящей папкой на общем диске — конфигурации ссылаются на неё.</summary>
    private static FwVersionRecord SeedFirmware(Database db, HierarchyService hier, string root,
        string versionRaw = "1.99.7.1.20260801_1200")
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");

        var folder = hier.FwPath(root, group.Name, subtype.Name, mod.ControllerName, versionRaw);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "fw.psl"), "test firmware");

        var record = new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value, ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix, SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion, SwVersion = 1, DtStr = "20260801_1200",
            VersionRaw = versionRaw, Filename = "fw.psl", DiskPath = folder,
            Description = "прошивка с конфигурациями", Changelog = "прошивка с конфигурациями",
            Status = "active", Tags = TagString.Join(new[] { "НГР", "SMH4" }),
        };
        record.Id = db.AddFwVersion(record);
        Assert.True(record.Id > 0);
        return record;
    }

    private static void SyncAtoB(TwoMachines m, string root)
    {
        ConfigSyncService.Export(m.SvcA, root, "profileA");
        ConfigSyncService.Apply(m.SvcB, ConfigSyncService.ConfigPathFor(root), root);
    }

    private static void ApplyConfigs(Database db, FwVersionRecord primary, string bulk) =>
        FirmwareConfigService.Apply(db, primary, FirmwareConfigService.ParseBulk(bulk));

    /// <summary>Базовый случай: программист заготовил ряд комплектаций у себя — у наладчика он такой же,
    /// и поиск по названию конкретного шкафа находит нужный вариант.</summary>
    [Fact]
    public void Configs_TravelToOtherMachine_AndStayDistinct()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var primary = SeedFirmware(m.DbA, m.HierA, root);
        ApplyConfigs(m.DbA, primary, $"2 насоса | {Cabinet2Pumps}\nЖокей и задвижка | {CabinetJockey}");

        SyncAtoB(m, root);

        var primaryOnB = m.DbB.GetAllFwVersionsWithNames(includeArchived: true)
            .Single(v => v.VersionRaw == primary.VersionRaw && v.ConfigName.Length == 0);
        var configsOnB = m.DbB.GetFwVersionConfigs(primaryOnB.DiskPath, primaryOnB.VersionRaw);
        Assert.Equal(new[] { "2 насоса", "Жокей и задвижка" }, configsOnB.Select(c => c.ConfigName).ToArray());

        // И главное — поиск у наладчика различает комплектации.
        Assert.Equal("Жокей и задвижка", Assert.Single(SearchService.Search(m.DbB, CabinetJockey)).ConfigName);
        Assert.Equal("2 насоса", Assert.Single(SearchService.Search(m.DbB, Cabinet2Pumps)).ConfigName);
    }

    /// <summary>Та самая ловушка: у наладчика прошивка УЖЕ есть — её завёл его собственный досмотр
    /// общего диска (папка на шаре одна). Значит приехавшие конфигурации сопоставляются с уже
    /// существующей строкой по натуральному ключу, а не вставляются с нуля. Без имени варианта в ключе
    /// все они схлопывались в эту одну строку, и ряд комплектаций пропадал.</summary>
    [Fact]
    public void Configs_DoNotCollapse_WhenReceiverAlreadyKnowsFirmwareFromDiskScan()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var primary = SeedFirmware(m.DbA, m.HierA, root);
        // Наладчик нашёл прошивку сам, обходом общего диска — своя строка, свой sync_id.
        m.HierB.SyncFwFromDisk(root);
        Assert.Single(m.DbB.GetAllFwVersionsWithNames(includeArchived: true).Where(v => v.VersionRaw == primary.VersionRaw));

        ApplyConfigs(m.DbA, primary, $"2 насоса | {Cabinet2Pumps}\nЖокей и задвижка | {CabinetJockey}");
        SyncAtoB(m, root);

        var rows = m.DbB.GetAllFwVersionsWithNames(includeArchived: true)
            .Where(v => v.VersionRaw == primary.VersionRaw).ToList();
        Assert.Equal(3, rows.Count); // сама прошивка + две конфигурации
        Assert.Single(rows.Where(r => r.ConfigName.Length == 0));
        Assert.Equal(new[] { "2 насоса", "Жокей и задвижка" },
            rows.Where(r => r.ConfigName.Length > 0).Select(r => r.ConfigName).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        // Теги не свалены в кучу: у каждой конфигурации своё название шкафа, и чужого в ней нет.
        var jockey = rows.Single(r => r.ConfigName == "Жокей и задвижка");
        Assert.Contains(CabinetJockey, TagString.Parse(jockey.Tags));
        Assert.DoesNotContain(Cabinet2Pumps, TagString.Parse(jockey.Tags));
    }

    /// <summary>Убранная у программиста конфигурация исчезает и у наладчика — удаление едет надгробием,
    /// как и всё остальное удаление прошивок.</summary>
    [Fact]
    public void RemovedConfig_DisappearsOnOtherMachine()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var primary = SeedFirmware(m.DbA, m.HierA, root);
        ApplyConfigs(m.DbA, primary, $"2 насоса | {Cabinet2Pumps}\nЖокей и задвижка | {CabinetJockey}");
        SyncAtoB(m, root);
        Assert.Equal(2, m.DbB.GetFwVersionConfigs(primary.DiskPath, primary.VersionRaw).Count);

        ApplyConfigs(m.DbA, primary, $"2 насоса | {Cabinet2Pumps}");
        SyncAtoB(m, root);

        var left = Assert.Single(m.DbB.GetFwVersionConfigs(primary.DiskPath, primary.VersionRaw));
        Assert.Equal("2 насоса", left.ConfigName);
        // Файлы прошивки при этом на месте: конфигурация — ссылка на них, а не они сами.
        Assert.True(Directory.Exists(primary.DiskPath));
    }

    /// <summary>Переименование конфигурации — правка той же строки, а не новая запись: у наладчика
    /// вариант должен остаться ОДИН, с новым именем.</summary>
    [Fact]
    public void RenamedConfig_IsUpdatedInPlaceOnOtherMachine()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        var primary = SeedFirmware(m.DbA, m.HierA, root);
        ApplyConfigs(m.DbA, primary, $"2 насоса | {Cabinet2Pumps}");
        SyncAtoB(m, root);

        // Переименование именно СТРОКИ, а не «удалить + завести». Массовый редактор сопоставляет
        // конфигурации по имени, поэтому такой правки в интерфейсе пока нет — правим столбец напрямую
        // вторым подключением к тому же файлу БД (обычный для этого набора приём, см. TwoMachines.PathA
        // и DeleteSubtype в ConfigSyncTests): проверяется здесь именно приём на стороне наладчика.
        var configOnA = Assert.Single(m.DbA.GetFwVersionConfigs(primary.DiskPath, primary.VersionRaw));
        RenameConfigDirectly(m.PathA, configOnA.Id!.Value, "Два насоса");
        SyncAtoB(m, root);

        var configOnB = Assert.Single(m.DbB.GetFwVersionConfigs(primary.DiskPath, primary.VersionRaw));
        Assert.Equal("Два насоса", configOnB.ConfigName);
    }

    private static void RenameConfigDirectly(string dbPath, int fwVersionId, string newName)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE fw_versions SET config_name=@n WHERE id=@id";
        cmd.Parameters.AddWithValue("@n", newName);
        cmd.Parameters.AddWithValue("@id", fwVersionId);
        cmd.ExecuteNonQuery();
    }
}
