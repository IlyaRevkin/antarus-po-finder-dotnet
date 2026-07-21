using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

public record EnsureStructureResult(bool Ok, int CreatedCount, List<string> Errors, int MovedCount);
public record SyncFromDiskResult(bool Ok, int Added, int Skipped, List<string> AddedItems, List<string> Errors);
public record UnknownEntry(string Path, string Name, string Type, string Section);
public record MoveNamedResult(int Moved, List<string> MovedPaths, List<string> Errors);

/// <summary>Builds/maintains the on-disk folder tree that mirrors the DB hierarchy.
/// 1:1 port of app/services/hierarchy_service.py.</summary>
public class HierarchyService
{
    private const string FolderPo = "ПО";
    private const string FolderParams = "Параметры";
    private const string FolderConfig = "Конфиг";

    private readonly Database _db;

    public HierarchyService(Database db) => _db = db;

    // ── Path builders ─────────────────────────────────────────────────────────

    private static string CtrlOrOpcFolder(string controller, bool isOpc) => isOpc ? HierarchyFolders.Opc : controller;

    private string PoCtrlFolder(string root, string groupName, string subName, string controller, bool isOpc)
    {
        var parts = new List<string> { root, FolderPo, groupName };
        if (subName != "—") parts.Add(subName);
        parts.Add(CtrlOrOpcFolder(controller, isOpc));
        return Path.Combine(parts.ToArray());
    }

    public string FwPath(string root, string groupName, string subName, string controller, string versionStr, bool isOpc = false) =>
        Path.Combine(PoCtrlFolder(root, groupName, subName, controller, isOpc), versionStr);

    /// <summary>Public wrapper over PoCtrlFolder — the controller (or ОПЦ, when isOpc) folder itself,
    /// with no version segment appended. Used by the "reassign" action in UnknownFilesDialog to drop
    /// a formerly-unknown folder/file straight into its correct place on disk.</summary>
    public string ControllerFolder(string root, string groupName, string subName, string controller, bool isOpc = false) =>
        PoCtrlFolder(root, groupName, subName, controller, isOpc);

    public string InstrPath(string root, string groupName, string subName, string controller) =>
        Path.Combine(PoCtrlFolder(root, groupName, subName, controller, false), HierarchyFolders.Instructions);

    public string IoMapPath(string root, string groupName, string subName, string controller) =>
        Path.Combine(PoCtrlFolder(root, groupName, subName, controller, false), HierarchyFolders.IoMap);

    public string ModbusMapPath(string root, string groupName, string subName, string controller) =>
        Path.Combine(PoCtrlFolder(root, groupName, subName, controller, false), HierarchyFolders.Modbus);

    public string HmiPath(string root, string groupName, string subName, string controller) =>
        Path.Combine(PoCtrlFolder(root, groupName, subName, controller, false), HierarchyFolders.Hmi);

    public string ParamsPath(string root, string groupName, string subName, string manufacturer)
    {
        var parts = new List<string> { root, FolderParams, groupName };
        if (subName != "—") parts.Add(subName);
        parts.Add(manufacturer);
        return Path.Combine(parts.ToArray());
    }

    // ── Rename group/subtype folders ─────────────────────────────────────────

    public record RenameFolderResult(bool Ok, string? Error, int RemappedRows);

    /// <summary>Renames a group's on-disk folders (both the ПО and Параметры trees) in place and
    /// remaps any already-stored fw_versions/param_files paths that pointed inside them — group/
    /// subtype names aren't a stable id, they're read live off Name every time EnsureStructure/
    /// FwPath/ParamsPath run, so a DB-only rename would silently orphan the old folder (the next
    /// EnsureStructure/scan would sweep it into Неизвестное) and break "Открыть" for every firmware
    /// already uploaded under the old name.</summary>
    public RenameFolderResult RenameGroupFolder(string root, string oldName, string newName)
    {
        if (oldName == newName) return new RenameFolderResult(true, null, 0);

        var oldPo = Path.Combine(root, FolderPo, oldName);
        var newPo = Path.Combine(root, FolderPo, newName);
        var oldParams = Path.Combine(root, FolderParams, oldName);
        var newParams = Path.Combine(root, FolderParams, newName);

        var errors = new List<string>();
        TryRenameFolder(oldPo, newPo, errors);
        TryRenameFolder(oldParams, newParams, errors);
        if (errors.Count > 0) return new RenameFolderResult(false, string.Join("\n", errors), 0);

        var remapped = _db.RemapPathPrefix(oldPo, newPo) + _db.RemapPathPrefix(oldParams, newParams);
        return new RenameFolderResult(true, null, remapped);
    }

    /// <summary>Same as RenameGroupFolder but for a subtype's folder nested under its group. Not
    /// meaningful for the "—" placeholder subtype (Database.EnsureEveryGroupHasSubtype) — that one
    /// has no folder segment of its own, the group's own folder stands in for it; the caller (see
    /// SettingsView.RenameSubtype_Click) refuses to even offer this for that row.</summary>
    public RenameFolderResult RenameSubtypeFolder(string root, string groupName, string oldSubName, string newSubName)
    {
        if (oldSubName == newSubName) return new RenameFolderResult(true, null, 0);

        var oldPo = Path.Combine(root, FolderPo, groupName, oldSubName);
        var newPo = Path.Combine(root, FolderPo, groupName, newSubName);
        var oldParams = Path.Combine(root, FolderParams, groupName, oldSubName);
        var newParams = Path.Combine(root, FolderParams, groupName, newSubName);

        var errors = new List<string>();
        TryRenameFolder(oldPo, newPo, errors);
        TryRenameFolder(oldParams, newParams, errors);
        if (errors.Count > 0) return new RenameFolderResult(false, string.Join("\n", errors), 0);

        var remapped = _db.RemapPathPrefix(oldPo, newPo) + _db.RemapPathPrefix(oldParams, newParams);
        return new RenameFolderResult(true, null, remapped);
    }

    private static void TryRenameFolder(string oldPath, string newPath, List<string> errors)
    {
        if (!Directory.Exists(oldPath)) return; // nothing on disk yet — EnsureStructure will create it under the new name
        if (Directory.Exists(newPath))
        {
            errors.Add($"Папка «{newPath}» уже существует — переименование отменено.");
            return;
        }
        try { Directory.Move(oldPath, newPath); }
        catch (Exception e) { errors.Add($"{oldPath}: {e.Message}"); }
    }

    // ── Ensure structure ──────────────────────────────────────────────────────

    public EnsureStructureResult EnsureStructure(string root)
    {
        var errors = new List<string>();
        int created = 0;

        void EnsureDir(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                    created++;
                }
            }
            catch (Exception e)
            {
                errors.Add($"{path}: {e.Message}");
            }
        }

        var groups = _db.GetAllEquipmentGroups();
        foreach (var g in groups)
        {
            var subtypes = _db.GetSubtypesForGroup(g.Id!.Value);
            if (subtypes.Count == 0)
            {
                EnsureDir(Path.Combine(root, FolderPo, g.Name));
            }
            foreach (var s in subtypes)
            {
                var groupSubPath = s.Name == "—"
                    ? Path.Combine(root, FolderPo, g.Name)
                    : Path.Combine(root, FolderPo, g.Name, s.Name);
                EnsureDir(groupSubPath);

                foreach (var ctrl in _db.GetAllControllerModels())
                {
                    var ctrlPath = Path.Combine(groupSubPath, ctrl.Name);
                    EnsureDir(ctrlPath);
                    EnsureDir(Path.Combine(ctrlPath, HierarchyFolders.Instructions));
                    EnsureDir(Path.Combine(ctrlPath, HierarchyFolders.IoMap));
                    EnsureDir(Path.Combine(ctrlPath, HierarchyFolders.Modbus));
                    EnsureDir(Path.Combine(ctrlPath, HierarchyFolders.Hmi));
                }
                EnsureDir(Path.Combine(groupSubPath, HierarchyFolders.Opc));

                var paramsGroupSubPath = s.Name == "—"
                    ? Path.Combine(root, FolderParams, g.Name)
                    : Path.Combine(root, FolderParams, g.Name, s.Name);
                foreach (var manufacturer in _db.GetParamManufacturers())
                    EnsureDir(Path.Combine(paramsGroupSubPath, manufacturer));
            }
        }

        EnsureDir(Path.Combine(root, FolderPo, HierarchyFolders.UnknownFw));
        EnsureDir(Path.Combine(root, FolderParams, HierarchyFolders.UnknownParams));
        EnsureDir(Path.Combine(root, FolderConfig));

        var movedCount = CollectUnknowns(root).Moved;

        return new EnsureStructureResult(errors.Count == 0, created, errors, movedCount);
    }

    // ── Collect / scan unknown files ─────────────────────────────────────────

    private HashSet<string> KnownPoNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in _db.GetAllEquipmentGroups()) names.Add(g.Name);
        foreach (var s in _db.GetAllEquipmentSubtypes())
            if (s.Name != "—") names.Add(s.Name);
        foreach (var c in _db.GetAllControllerModels()) names.Add(c.Name);
        names.Add(HierarchyFolders.Opc);
        names.Add(HierarchyFolders.UnknownFw);
        names.Add(HierarchyFolders.Instructions);
        names.Add(HierarchyFolders.IoMap);
        names.Add(HierarchyFolders.Modbus);
        names.Add(HierarchyFolders.Hmi);
        return names;
    }

    private HashSet<string> KnownParamNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in _db.GetAllEquipmentGroups()) names.Add(g.Name);
        foreach (var s in _db.GetAllEquipmentSubtypes())
            if (s.Name != "—") names.Add(s.Name);
        foreach (var m in _db.GetParamManufacturers()) names.Add(m);
        names.Add(HierarchyFolders.UnknownParams);
        return names;
    }

    public MoveNamedResult CollectUnknowns(string root)
    {
        var moved = new List<string>();
        var errors = new List<string>();

        var poRoot = Path.Combine(root, FolderPo);
        var poUnknown = Path.Combine(poRoot, HierarchyFolders.UnknownFw);
        MoveUnrecognizedTopLevel(poRoot, poUnknown, KnownPoNames(), moved, errors);

        var paramsRoot = Path.Combine(root, FolderParams);
        var paramsUnknown = Path.Combine(paramsRoot, HierarchyFolders.UnknownParams);
        MoveUnrecognizedTopLevel(paramsRoot, paramsUnknown, KnownParamNames(), moved, errors);

        return new MoveNamedResult(moved.Count, moved, errors);
    }

    private static void MoveUnrecognizedTopLevel(string scanRoot, string unknownDir, HashSet<string> known,
        List<string> moved, List<string> errors)
    {
        if (!Directory.Exists(scanRoot)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(scanRoot))
        {
            var name = Path.GetFileName(entry);
            if (known.Contains(name)) continue;
            if (string.Equals(Path.GetFullPath(entry), Path.GetFullPath(unknownDir), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                Directory.CreateDirectory(unknownDir);
                var dest = SafeDestination(unknownDir, name);
                if (Directory.Exists(entry)) Directory.Move(entry, dest);
                else File.Move(entry, dest);
                moved.Add(dest);
            }
            catch (Exception e)
            {
                errors.Add($"{entry}: {e.Message}");
            }
        }
    }

    private static string SafeDestination(string destDir, string name)
    {
        var dest = Path.Combine(destDir, name);
        int suffix = 1;
        while (Directory.Exists(dest) || File.Exists(dest))
        {
            dest = Path.Combine(destDir, $"{name}_{suffix}");
            suffix++;
        }
        return dest;
    }

    /// <summary>Explicitly hunts a specific folder name (e.g. after a group/subtype/controller is
    /// deleted in Settings) across all group/subtype locations and moves it to Неизвестное — more
    /// robust than a scandir-based sweep on flaky WebDAV mounts.</summary>
    public MoveNamedResult MoveNamedFolders(string root, string folderName)
    {
        var moved = new List<string>();
        var errors = new List<string>();
        var poUnknown = Path.Combine(root, FolderPo, HierarchyFolders.UnknownFw);

        foreach (var g in _db.GetAllEquipmentGroups())
        {
            TryMove(Path.Combine(root, FolderPo, g.Name, folderName), poUnknown, folderName, moved, errors);
            foreach (var s in _db.GetSubtypesForGroup(g.Id!.Value))
            {
                if (s.Name == "—") continue;
                TryMove(Path.Combine(root, FolderPo, g.Name, s.Name, folderName), poUnknown, folderName, moved, errors);
            }
        }

        return new MoveNamedResult(moved.Count, moved, errors);
    }

    private static void TryMove(string candidate, string unknownDir, string name, List<string> moved, List<string> errors)
    {
        if (!Directory.Exists(candidate) && !File.Exists(candidate)) return;
        try
        {
            Directory.CreateDirectory(unknownDir);
            var dest = SafeDestination(unknownDir, name);
            if (Directory.Exists(candidate)) Directory.Move(candidate, dest);
            else File.Move(candidate, dest);
            moved.Add(dest);
        }
        catch (Exception e)
        {
            errors.Add($"{candidate}: {e.Message}");
        }
    }

    /// <summary>Was originally a single flat pass over the ПО/Параметры top level only — an unknown
    /// GROUP folder (e.g. a mistyped cabinet-type name dropped straight under "ПО\") was caught, but
    /// an unknown SUBTYPE folder nested under a real group, or an unknown CONTROLLER folder nested
    /// under a real group/subtype, was silently invisible to this scan (only top-level names were
    /// ever checked against KnownPoNames/KnownParamNames). Now recurses through group → subtype →
    /// controller, using the same flat known-name set at every level (see KnownPoNames — some groups,
    /// e.g. ВЗУ, mix a "—" placeholder subtype with a real one, so a group folder's *own* children can
    /// legitimately be a blend of controller folders and subtype folders side by side; a strict
    /// per-level schema would misclassify that as "unknown"). Recursion stops the moment it reaches a
    /// controller/ОПЦ/manufacturer folder (see poLeafNames/paramsLeafNames) — those hold free-form
    /// version-numbered subfolders or files that were never meant to be checked against a fixed name
    /// list, so descending into them would misreport every real firmware version as "unknown".</summary>
    public List<UnknownEntry> ScanUnknownFiles(string root)
    {
        var result = new List<UnknownEntry>();

        var poRoot = Path.Combine(root, FolderPo);
        var poUnknown = Path.Combine(poRoot, HierarchyFolders.UnknownFw);
        var poLeafNames = new HashSet<string>(_db.GetAllControllerModels().Select(c => c.Name), StringComparer.OrdinalIgnoreCase)
        {
            HierarchyFolders.Opc, HierarchyFolders.Instructions, HierarchyFolders.IoMap, HierarchyFolders.Modbus, HierarchyFolders.Hmi, HierarchyFolders.UnknownFw,
        };
        CollectEntriesRecursive(poRoot, poUnknown, KnownPoNames(), poLeafNames, "ПО", result, depth: 0);

        var paramsRoot = Path.Combine(root, FolderParams);
        var paramsUnknown = Path.Combine(paramsRoot, HierarchyFolders.UnknownParams);
        var paramsLeafNames = new HashSet<string>(_db.GetParamManufacturers(), StringComparer.OrdinalIgnoreCase)
        {
            HierarchyFolders.UnknownParams,
        };
        CollectEntriesRecursive(paramsRoot, paramsUnknown, KnownParamNames(), paramsLeafNames, "Параметры", result, depth: 0);

        return result;
    }

    /// <summary>depth is capped defensively (pathological/symlinked trees) — the real hierarchy is at
    /// most 3 levels deep (group → subtype → controller) so this never gets close to the cap in
    /// practice.</summary>
    private static void CollectEntriesRecursive(string dir, string unknownDir, HashSet<string> known,
        HashSet<string> leafNames, string section, List<UnknownEntry> result, int depth)
    {
        if (depth > 6 || !Directory.Exists(dir)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            var name = Path.GetFileName(entry);
            if (string.Equals(Path.GetFullPath(entry), Path.GetFullPath(unknownDir), StringComparison.OrdinalIgnoreCase)) continue;
            var isDir = Directory.Exists(entry);
            if (!known.Contains(name))
            {
                result.Add(new UnknownEntry(entry, name, isDir ? "dir" : "file", section));
                continue;
            }
            if (isDir && !leafNames.Contains(name))
                CollectEntriesRecursive(entry, unknownDir, known, leafNames, section, result, depth + 1);
        }
    }

    // ── Sync fw_versions from disk ────────────────────────────────────────────

    public SyncFromDiskResult SyncFwFromDisk(string root)
    {
        var errors = new List<string>();
        var addedItems = new List<string>();
        int added = 0, skipped = 0;

        var groups = _db.GetAllEquipmentGroups();
        foreach (var g in groups)
        {
            // Every group is guaranteed at least one subtype row (Database.EnsureEveryGroupHasSubtype) —
            // "—" is the placeholder for "no real subtype division", so this no longer needs a
            // null-subtype fallback branch that used to make sync silently skip such groups entirely.
            foreach (var sub in _db.GetSubtypesForGroup(g.Id!.Value))
            {
                var groupSubPath = sub.Name == "—" ? Path.Combine(root, FolderPo, g.Name) : Path.Combine(root, FolderPo, g.Name, sub.Name);
                if (!Directory.Exists(groupSubPath)) continue;

                foreach (var ctrl in _db.GetAllControllerModels())
                {
                    var ctrlPath = Path.Combine(groupSubPath, ctrl.Name);
                    if (!Directory.Exists(ctrlPath)) continue;

                    foreach (var versionDir in Directory.EnumerateDirectories(ctrlPath))
                    {
                        var versionName = Path.GetFileName(versionDir);
                        var parsed = FwVersionNumber.Parse(versionName);
                        if (parsed is null) continue;

                        var exists = _db.GetFwVersions(sub.Id, ctrl.Id, includeArchived: true, includeRolledBack: true)
                            .Any(v => v.VersionRaw == parsed.Raw);
                        if (exists) { skipped++; continue; }

                        var label = sub.Name == "—" ? $"{g.Name}/{ctrl.Name}/{parsed.Raw}" : $"{g.Name}/{sub.Name}/{ctrl.Name}/{parsed.Raw}";
                        try
                        {
                            var filename = Directory.EnumerateFiles(versionDir).FirstOrDefault();
                            _db.AddFwVersion(new Domain.FwVersionRecord
                            {
                                SubtypeId = sub.Id!.Value,
                                ControllerId = ctrl.Id!.Value,
                                EqPrefix = parsed.EqPrefix,
                                SubPrefix = parsed.SubPrefix,
                                HwVersion = parsed.HwVersion,
                                SwVersion = parsed.SwVersion,
                                DtStr = parsed.DtStr,
                                VersionRaw = parsed.Raw,
                                Filename = filename is null ? "" : Path.GetFileName(filename),
                                DiskPath = versionDir,
                                Description = "(синхронизировано с диска)",
                                Status = "active",
                            });
                            added++;
                            addedItems.Add(label);
                        }
                        catch (Exception e)
                        {
                            errors.Add($"{label}: {e.Message}");
                        }
                    }
                }
            }
        }

        return new SyncFromDiskResult(errors.Count == 0, added, skipped, addedItems, errors);
    }
}
