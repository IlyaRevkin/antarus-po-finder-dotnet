using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Две вещи, которые обязаны работать НА КАЖДОЙ машине отдельно, а не приезжать
/// синхронизацией (docs/hierarchy-rework-plan.md, этапы 3 и 5):
///
/// • <b>структура на третьем диске</b> — раньше папки там появлялись только в момент, когда туда
///   впервые клали файл, отсюда жалоба «на третьем структура не создалась»;
/// • <b>починка путей ОПЦ</b> — перенос ОПЦ внутрь контроллера единственный меняет disk_path, а он у
///   совпавшей записи импортом конфига не обновляется никогда.</summary>
public class OpcRepairAndThirdDiskStructureTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public OpcRepairAndThirdDiskStructureTests()
    {
        _db = new Database(_dbFile.Path);
        _hierarchy = new HierarchyService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
        _tempRoot.Dispose();
    }

    // ── Третий диск: зеркало папок «Инструкция» ──────────────────────────────

    /// <summary>План структуры зеркалит на третий диск ТОЛЬКО папки «Инструкция»: больше он ни подо
    /// что не заведён, и тащить туда пустое дерево контроллеров целиком незачем.</summary>
    [Fact]
    public void PlanStructure_MirrorsOnlyInstructionFoldersOntoThirdDisk()
    {
        using var third = new TempRoot();

        var withoutThird = _hierarchy.PlanStructure(Root);
        var withThird = _hierarchy.PlanStructure(Root, third.Path);

        var mirrored = withThird.Folders.Where(f => f.StartsWith(third.Path, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(mirrored);
        Assert.All(mirrored, f => Assert.Equal(HierarchyFolders.Instructions, Path.GetFileName(f)));
        // Первый диск от появления третьего не меняется ни на одну папку.
        Assert.Equal(withoutThird.Folders.Count, withThird.Folders.Count - mirrored.Count);

        // И они действительно создаются, а не только планируются.
        HierarchyService.ApplyStructurePlan(withThird);
        Assert.All(mirrored, f => Assert.True(Directory.Exists(f), f));
    }

    /// <summary>Третий диск не настроен — план ровно прежний, ни одной лишней папки.</summary>
    [Fact]
    public void PlanStructure_WithoutThirdDisk_IsUnchanged()
    {
        Assert.Equal(_hierarchy.PlanStructure(Root).Folders, _hierarchy.PlanStructure(Root, "").Folders);
        Assert.Equal(_hierarchy.PlanStructure(Root).Folders, _hierarchy.PlanStructure(Root, null).Folders);
    }

    /// <summary>Папка «ОПЦ» теперь планируется внутри контроллера, а прежняя, на уровне подтипа,
    /// заново не заводится: она остаётся на диске со всем содержимым и читается, но плодить её
    /// пустой под каждый подтип больше не надо.</summary>
    [Fact]
    public void PlanStructure_CreatesOpcInsideControllers_NotUnderSubtypes()
    {
        var plan = _hierarchy.PlanStructure(Root);

        var opcFolders = plan.Folders.Where(f => Path.GetFileName(f) == HierarchyFolders.Opc).ToList();
        Assert.NotEmpty(opcFolders);
        // У каждой «ОПЦ» рядом с родителем лежат папки документов контроллера — значит она внутри него.
        Assert.All(opcFolders, f =>
        {
            var parent = Path.GetDirectoryName(f)!;
            Assert.Contains(Path.Combine(parent, HierarchyFolders.Instructions), plan.Folders);
        });
    }

    // ── Починка путей ОПЦ после переезда ─────────────────────────────────────

    [Fact]
    public void RepairOpcDiskPaths_FindsMovedFolderByVersion_AndLeavesOthersAlone()
    {
        _hierarchy.EnsureStructure(Root);
        var group = _db.GetAllEquipmentGroups().Single(g => g.Name == "ТГР");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).Single();
        var mod = _db.GetAllModifications().Single(m => m.ControllerName == "SMH5" && m.DisplayName == "SMH5");

        // Запись указывает на прежнее место — общую папку «ОПЦ» подтипа, которой на диске уже нет
        // (её перенёс коллега, запустивший перестройку).
        var stalePath = Path.Combine(HierarchyService.LegacyOpcFolder(Root, group.Name, subtype.Name), "3.0.005.0777");
        var id = _db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            VersionRaw = "3.0.005.0777",
            DiskPath = stalePath,
            IsOpc = true,
            RequestNum = "01312",
        });

        // Новое место на диске — с журналом, по которому и опознаётся номер версии.
        var ctrlFolder = Path.Combine(HierarchyService.GroupSubFolder(Root, group.Name, subtype.Name), mod.ControllerName);
        var moved = Path.Combine(OpcLayout.ControllerOpcFolder(ctrlFolder), "01312");
        Directory.CreateDirectory(moved);
        ChangelogFile.Write(moved, FwVersionNumber.Parse("3.0.005.0777")!, new[] { "УПП" }, "перестройка", Array.Empty<string>());

        var result = _hierarchy.RepairOpcDiskPaths(Root);

        Assert.Equal(1, result.Repaired);
        Assert.Equal(0, result.Unresolved);
        Assert.Equal(moved, _db.GetAllFwVersionsWithNames(includeArchived: true).Single(v => v.Id == id).DiskPath);

        // Повторный проход идемпотентен: путь теперь существует, чинить нечего.
        var again = _hierarchy.RepairOpcDiskPaths(Root);
        Assert.Equal(0, again.Repaired);
    }

    /// <summary>Не нашли новое место — путь НЕ трогаем: выдуманный путь хуже устаревшего, а человек
    /// должен увидеть такую запись в отчёте.</summary>
    [Fact]
    public void RepairOpcDiskPaths_UnresolvedRecord_KeepsItsOldPath()
    {
        _hierarchy.EnsureStructure(Root);
        var group = _db.GetAllEquipmentGroups().Single(g => g.Name == "ТГР");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).Single();
        var mod = _db.GetAllModifications().Single(m => m.ControllerName == "SMH5" && m.DisplayName == "SMH5");

        var stalePath = Path.Combine(HierarchyService.LegacyOpcFolder(Root, group.Name, subtype.Name), "3.0.005.0999");
        var id = _db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            VersionRaw = "3.0.005.0999",
            DiskPath = stalePath,
            IsOpc = true,
        });

        var result = _hierarchy.RepairOpcDiskPaths(Root);

        Assert.Equal(0, result.Repaired);
        Assert.Equal(1, result.Unresolved);
        Assert.Contains(result.Details, d => d.Contains("3.0.005.0999"));
        Assert.Equal(stalePath, _db.GetAllFwVersionsWithNames(includeArchived: true).Single(v => v.Id == id).DiskPath);
    }

    /// <summary>Диск недоступен — выходим сразу и ничего не правим: иначе «шара отвалилась»
    /// выглядело бы как «все ОПЦ переехали».</summary>
    [Fact]
    public void RepairOpcDiskPaths_UnreachableDisk_DoesNothing()
    {
        var result = _hierarchy.RepairOpcDiskPaths(Path.Combine(Root, "нет-такой-шары"));

        Assert.Equal(0, result.Repaired);
        Assert.Equal(0, result.Unresolved);
    }
}
