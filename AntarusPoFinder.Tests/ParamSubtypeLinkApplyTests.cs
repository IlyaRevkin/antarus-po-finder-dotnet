using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Правка набора подтипов у УЖЕ ЗАГРУЖЕННОГО файла параметров (ParamFileLinkService.Apply) —
/// в том числе привязка к подтипам ДРУГИХ типов шкафа: один файл параметров частотника подходит сразу
/// нескольким типам, и папка на диске у каждого своя («Параметры\{тип}\{подтип}\{производитель}»).
/// Главное, что проверяется: сам файл на диске остаётся ровно одной копией и переживает отвязку.</summary>
public class ParamSubtypeLinkApplyTests : IDisposable
{
    private sealed class FakeShortcuts : IShortcutCreator
    {
        public List<(string Shortcut, string Target)> Created { get; } = new();
        public void Create(string shortcutPath, string targetPath, string description)
        {
            Created.Add((shortcutPath, targetPath));
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
            File.WriteAllText(shortcutPath, targetPath);
        }
    }

    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public ParamSubtypeLinkApplyTests()
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

    /// <summary>Кандидаты — весь справочник подтипов вместе с именем своего типа, ровно как их
    /// собирает ParamsView/EditParamSubtypesDialog.</summary>
    private List<ParamFileLinkService.SubtypeTarget> AllTargets()
    {
        var groups = _db.GetAllEquipmentGroups().ToDictionary(g => g.Id ?? 0, g => g.Name);
        return _db.GetAllEquipmentSubtypes()
            .Where(s => s.Id is not null)
            .Select(s => new ParamFileLinkService.SubtypeTarget(s, groups.TryGetValue(s.GroupId, out var n) ? n : ""))
            .ToList();
    }

    private ParamFile SeedPrimary(EquipmentSubType subtype, string groupName, string manuf)
    {
        var folder = _hierarchy.ParamsPath(Root, groupName, subtype.Name, manuf);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "params.par"), "parameters");

        var primary = new ParamFile
        {
            SubtypeId = subtype.Id,
            Manufacturer = manuf,
            Filename = "params.par",
            DiskPath = folder,
            Description = "общие параметры",
            UploadDate = "2026-07-24 10:00:00",
        };
        primary.Id = _db.AddParamFile(primary);
        return primary;
    }

    [Fact]
    public void Apply_LinksSubtypesFromOtherGroups_WithoutCopyingTheFile()
    {
        var targets = AllTargets();
        var manuf = _db.GetParamManufacturers().First();
        var pj = targets.First(t => t.GroupName == "ПЖ");
        var primary = SeedPrimary(pj.Subtype, pj.GroupName, manuf);

        // Подтип другого типа шкафа — ровно то, чего раньше нельзя было выбрать.
        var otherGroup = targets.First(t => t.GroupName != pj.GroupName);
        var shortcuts = new FakeShortcuts();

        var result = ParamFileLinkService.Apply(_db, _hierarchy, Root, primary, targets,
            new[] { otherGroup.Id }, shortcuts);

        Assert.Contains(otherGroup.FullDisplay, result.Added);
        Assert.Empty(result.Warnings);

        var row = Assert.Single(_db.GetParamFiles(otherGroup.Id));
        Assert.Equal(primary.DiskPath, row.DiskPath);

        // Ярлык лёг в папку СВОЕГО типа шкафа, а не типа основного подтипа.
        var expectedFolder = _hierarchy.ParamsPath(Root, otherGroup.GroupName, otherGroup.Subtype.Name, manuf);
        Assert.Equal(Path.Combine(expectedFolder, "params.par.lnk"), Assert.Single(shortcuts.Created).Shortcut);

        // Настоящий файл на диске по-прежнему один.
        Assert.Single(Directory.GetFiles(Root, "params.par", SearchOption.AllDirectories));
    }

    [Fact]
    public void Apply_UnlinkingSubtype_RemovesRecordAndShortcutButKeepsFile()
    {
        var targets = AllTargets();
        var manuf = _db.GetParamManufacturers().First();
        var pj = targets.First(t => t.GroupName == "ПЖ");
        var extra = targets.First(t => t.GroupName == "ПЖ" && t.Id != pj.Id);
        var primary = SeedPrimary(pj.Subtype, pj.GroupName, manuf);
        var shortcuts = new FakeShortcuts();

        ParamFileLinkService.Apply(_db, _hierarchy, Root, primary, targets, new[] { extra.Id }, shortcuts);
        var shortcutPath = shortcuts.Created.Single().Shortcut;
        Assert.True(File.Exists(shortcutPath));

        var result = ParamFileLinkService.Apply(_db, _hierarchy, Root, primary, targets,
            Array.Empty<int>(), shortcuts);

        Assert.Contains(extra.FullDisplay, result.Removed);
        Assert.Empty(_db.GetParamFiles(extra.Id));
        Assert.False(File.Exists(shortcutPath));
        // Отвязка — это снятие ссылки, а не удаление параметров: и файл, и запись основного подтипа целы.
        Assert.True(File.Exists(Path.Combine(primary.DiskPath, "params.par")));
        Assert.Single(_db.GetParamFiles(pj.Id));
    }

    [Fact]
    public void Apply_PrimarySubtypeCannotBeUnlinked_AndRepeatIsNoOp()
    {
        var targets = AllTargets();
        var manuf = _db.GetParamManufacturers().First();
        var pj = targets.First(t => t.GroupName == "ПЖ");
        var extra = targets.First(t => t.GroupName == "ПЖ" && t.Id != pj.Id);
        var primary = SeedPrimary(pj.Subtype, pj.GroupName, manuf);
        var shortcuts = new FakeShortcuts();

        // Основной подтип не передан в желаемых — он всё равно остаётся.
        ParamFileLinkService.Apply(_db, _hierarchy, Root, primary, targets, new[] { extra.Id }, shortcuts);
        Assert.Single(_db.GetParamFiles(pj.Id));

        // Повторное применение того же набора ничего не добавляет и не удаляет — иначе каждый
        // «Сохранить» без изменений плодил бы дубликаты записей.
        var again = ParamFileLinkService.Apply(_db, _hierarchy, Root, primary, targets,
            new[] { extra.Id }, shortcuts);
        Assert.False(again.Changed);
        Assert.Single(_db.GetParamFiles(extra.Id));
    }

    [Fact]
    public void CurrentLinks_ReportsPrimaryAndLinkedSubtypes()
    {
        var targets = AllTargets();
        var manuf = _db.GetParamManufacturers().First();
        var pj = targets.First(t => t.GroupName == "ПЖ");
        var extra = targets.First(t => t.GroupName != pj.GroupName);
        var primary = SeedPrimary(pj.Subtype, pj.GroupName, manuf);

        ParamFileLinkService.Apply(_db, _hierarchy, Root, primary, targets, new[] { extra.Id }, new FakeShortcuts());

        var links = ParamFileLinkService.CurrentLinks(_db, primary);
        Assert.Equal(2, links.Count);
        Assert.True(links.Single(l => l.SubtypeId == pj.Id).IsPrimary);
        Assert.False(links.Single(l => l.SubtypeId == extra.Id).IsPrimary);
    }
}
