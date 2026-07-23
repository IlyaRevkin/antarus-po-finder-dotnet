using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Жалоба: «позагружал прошивки на компе коллеги, у себя не вижу». Досмотр диска
/// (HierarchyService.PlanFwSync → ScanFwDisk → ImportFwCandidates) — единственный путь, которым
/// чужая версия попадает в эту базу без участия администратора, и он обязан донести версию целиком:
/// с тегами и описанием из CHANGELOG.md, а нестандартную (ОПЦ) — ещё и с признаком ОПЦ, номером
/// заявки и заводским SN, которые на диске записаны только в имени файла.</summary>
public class DiskScanColleagueUploadsTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public DiskScanColleagueUploadsTests()
    {
        _db = new Database(_dbFile.Path);
        _hierarchy = new HierarchyService(_db);
        _hierarchy.EnsureStructure(Root);
    }

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
        _tempRoot.Dispose();
    }

    private (EquipmentGroup group, EquipmentSubType subtype, ControllerModification mod) Cabinet()
    {
        var group = _db.GetAllEquipmentGroups().Single(g => g.Name == "ТГР");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).Single();
        var mod = _db.GetAllModifications().Single(m => m.ControllerName == "SMH5" && m.DisplayName == "SMH5");
        return (group, subtype, mod);
    }

    /// <summary>Кладёт на диск папку версии ровно так, как её оставила бы загрузка с машины коллеги.</summary>
    private string SeedOnDisk(string versionRaw, bool isOpc, string filename, params string[] tags)
    {
        var (group, subtype, mod) = Cabinet();
        var dir = _hierarchy.FwPath(Root, group.Name, subtype.Name, mod.ControllerName, versionRaw, isOpc);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, filename), "dummy");
        ChangelogFile.Write(dir, FwVersionNumber.Parse(versionRaw)!, new[] { "УПП" }, "правки коллеги", tags);
        return dir;
    }

    private SyncFromDiskResult Scan() =>
        _hierarchy.ImportFwCandidates(HierarchyService.ScanFwDisk(_hierarchy.PlanFwSync(Root)));

    private FwVersionRecord Stored(string versionRaw) =>
        _db.GetFwVersions(includeArchived: true, includeRolledBack: true).Single(v => v.VersionRaw == versionRaw);

    // ── Папка ОПЦ ─────────────────────────────────────────────────────────

    /// <summary>Папка «ОПЦ» лежит рядом с папками контроллеров и общая на все — досмотр её вообще не
    /// открывал, и нестандартная версия коллеги не появлялась здесь никогда.</summary>
    [Fact]
    public void OpcFolder_IsScanned_AndControllerComesFromTheHwNumber()
    {
        var (_, _, mod) = Cabinet();
        SeedOnDisk("3.0.005.0777", isOpc: true, "3.0.005.0777_20260101_1200.PSL");

        var result = Scan();

        Assert.Equal(1, result.Added);
        var stored = Stored("3.0.005.0777");
        Assert.Equal(mod.ControllerId, stored.ControllerId);
        Assert.True(stored.IsOpc);
    }

    [Fact]
    public void OpcVersion_KeepsRequestNumberAndCabinetSerialFromTheFilename()
    {
        SeedOnDisk("3.0.005.0778", isOpc: true, "3.0.005.0778_(01312)_SN00042_20260101_1200.PSL");

        Scan();

        var stored = Stored("3.0.005.0778");
        Assert.Equal("01312", stored.RequestNum);
        Assert.Equal("00042", stored.CabinetSn);
    }

    /// <summary>Контроллер у общей папки выводится из hw-номера версии. Незнакомый hw — версия
    /// пропускается: завести её не тому контроллеру хуже, чем не завести вовсе.</summary>
    [Fact]
    public void OpcVersion_WithUnknownHwNumber_IsSkippedRatherThanGuessed()
    {
        SeedOnDisk("3.0.999.0001", isOpc: true, "3.0.999.0001.PSL");

        var result = Scan();

        Assert.Equal(0, result.Added);
        Assert.Empty(_db.GetFwVersions(includeArchived: true, includeRolledBack: true));
    }

    /// <summary>Обычная версия под папкой контроллера нестандартной не становится — иначе признак ОПЦ
    /// проставился бы всему, что приехало с диска.</summary>
    [Fact]
    public void OrdinaryVersion_IsNotMarkedAsOpc()
    {
        SeedOnDisk("3.0.005.0779", isOpc: false, "3.0.005.0779_20260101_1200.PSL");

        Scan();

        var stored = Stored("3.0.005.0779");
        Assert.False(stored.IsOpc);
        Assert.Equal("", stored.RequestNum);
        Assert.Equal("", stored.CabinetSn);
    }

    // ── Теги из CHANGELOG.md ──────────────────────────────────────────────

    [Fact]
    public void TagsWrittenByTheOtherMachine_ArriveWithTheVersion()
    {
        SeedOnDisk("3.0.005.0780", isOpc: false, "3.0.005.0780.PSL", "ТГР", "SMH5");

        Scan();

        var tags = TagString.Parse(Stored("3.0.005.0780").Tags);
        Assert.Contains("ТГР", tags);
        Assert.Contains("SMH5", tags);
    }

    /// <summary>Многословный тег (полное название шкафа) обязан пережить дорогу через диск целиком —
    /// ради него в CHANGELOG.md разделитель «; », а не пробел.</summary>
    [Fact]
    public void MultiWordTag_SurvivesTheTripThroughTheDisk_AndIsSearchable()
    {
        const string cabinet = "шкаф управления пожарными насосами Антарус ПЖ-ПП-2";
        SeedOnDisk("3.0.005.0781", isOpc: false, "3.0.005.0781.PSL", cabinet, "ТГР");

        Scan();

        Assert.Contains(cabinet, TagString.Parse(Stored("3.0.005.0781").Tags));
        var hit = Assert.Single(SearchService.Search(_db, cabinet, exactWord: true));
        Assert.Equal("3.0.005.0781", hit.VersionRaw);
    }

    /// <summary>Тег доезжает и в справочник тегов — иначе он не появился бы ни в фильтре, ни в
    /// подсказках при вводе, и найти версию можно было бы только напечатав тег вслепую.</summary>
    [Fact]
    public void TagsFromDisk_JoinTheTagDictionary()
    {
        SeedOnDisk("3.0.005.0782", isOpc: false, "3.0.005.0782.PSL", "пожарный резерв");

        Scan();

        Assert.Contains("пожарный резерв", _db.GetAllTags());
        Assert.Contains("пожарный резерв", _db.GetTagsInUse());
    }

    [Fact]
    public void DescriptionFromChangelog_ReplacesThePlaceholder()
    {
        SeedOnDisk("3.0.005.0783", isOpc: false, "3.0.005.0783.PSL");

        Scan();

        Assert.Equal("правки коллеги", Stored("3.0.005.0783").Description);
    }
}
