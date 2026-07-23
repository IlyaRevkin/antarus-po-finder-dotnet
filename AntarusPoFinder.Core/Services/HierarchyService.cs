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

// ── Двухфазные операции «БД → диск» ───────────────────────────────────────────
// Каждая операция этого сервиса, которая ходит на сетевой диск, разбита на три отделимых куска:
// «спросить справочники у БД» → «сходить на диск» → «записать результат в БД». Так сделано ровно
// затем, чтобы вызывающий (см. MainWindowViewModel) мог выполнить середину — единственную реально
// медленную часть, потому что диск сетевой и регулярно отвечает через раз, — в фоновом потоке, а
// обе БД-части оставить на потоке UI. Соединение SQLite здесь одно на всё приложение и НЕ
// потокобезопасно, поэтому «просто обернуть всё целиком в Task.Run» было бы не оптимизацией, а
// гонкой: фоновая синхронизация и любой клик пользователя (поиск, открытие страницы) полезли бы в
// одно соединение одновременно.
//
// Однофазные методы (EnsureStructure/ScanUnknownFiles/SyncFwFromDisk) сохранены как обёртки —
// они и остаются нормальным способом вызова там, где блокировать некого (тесты, консольные пути).

/// <summary>Имена из справочников, которые нужны обходу диска, чтобы отличить «наше» от «чужого».
/// Снимок: читается из БД один раз перед обходом.</summary>
public record HierarchyNames(
    HashSet<string> PoNames, HashSet<string> ParamNames,
    HashSet<string> PoLeafNames, HashSet<string> ParamLeafNames);

/// <summary>Полный список папок, которые должны существовать на диске, плюс снимок имён для
/// последующего разбора «неизвестного». Считается по БД, применяется без неё.</summary>
public record StructurePlan(string Root, List<string> Folders, HierarchyNames Names);

/// <summary>Одна папка контроллера, которую нужно просмотреть на предмет новых версий, вместе с уже
/// известными БД номерами версий для этой пары подтип/контроллер.</summary>
public record FwSyncTarget(int SubtypeId, int ControllerId, string GroupName, string SubtypeName,
    string ControllerName, string ControllerPath, HashSet<string> KnownVersions);

/// <summary>Общая папка «ОПЦ» одного подтипа: у неё нет своего контроллера (она одна на все), поэтому
/// вместо него — карта «hw-номер версии → контроллер», по которой найденная версия и раскладывается.</summary>
public record FwOpcSyncTarget(int SubtypeId, string GroupName, string SubtypeName, string OpcPath,
    Dictionary<int, (int ControllerId, string ControllerName)> ControllerByHw, HashSet<string> KnownVersions);

public record FwSyncPlan(List<FwSyncTarget> Targets, List<FwOpcSyncTarget>? OpcTargets = null);

/// <summary>Найденная на диске папка версии, которой ещё нет в БД, со всем, что удалось вычитать
/// рядом (имя файла прошивки, CHANGELOG.md). Ничего не записывает — запись делает ImportFwCandidates.</summary>
public record FwDiskCandidate(FwSyncTarget Target, FwVersionNumber Version, string VersionDir,
    string Filename, ChangelogContent? Changelog, bool IsOpc = false)
{
    public string Label => SubtypeName == "—"
        ? $"{Target.GroupName}/{Target.ControllerName}/{Version.Raw}"
        : $"{Target.GroupName}/{SubtypeName}/{Target.ControllerName}/{Version.Raw}";

    private string SubtypeName => Target.SubtypeName;
}

public record FwDiskScan(List<FwDiskCandidate> Candidates, int Skipped, List<string> Errors);

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

    public EnsureStructureResult EnsureStructure(string root) => ApplyStructurePlan(PlanStructure(root));

    /// <summary>БД-фаза: какие папки должны быть на диске. Ни одного обращения к файловой системе —
    /// её можно вызывать на потоке UI, даже когда сам диск не отвечает.</summary>
    public StructurePlan PlanStructure(string root)
    {
        var folders = new List<string>();
        var controllers = _db.GetAllControllerModels();
        var manufacturers = _db.GetParamManufacturers();

        foreach (var g in _db.GetAllEquipmentGroups())
        {
            var subtypes = _db.GetSubtypesForGroup(g.Id!.Value);
            if (subtypes.Count == 0)
                folders.Add(Path.Combine(root, FolderPo, g.Name));

            foreach (var s in subtypes)
            {
                var groupSubPath = s.Name == "—"
                    ? Path.Combine(root, FolderPo, g.Name)
                    : Path.Combine(root, FolderPo, g.Name, s.Name);
                folders.Add(groupSubPath);

                foreach (var ctrl in controllers)
                {
                    var ctrlPath = Path.Combine(groupSubPath, ctrl.Name);
                    folders.Add(ctrlPath);
                    folders.Add(Path.Combine(ctrlPath, HierarchyFolders.Instructions));
                    folders.Add(Path.Combine(ctrlPath, HierarchyFolders.IoMap));
                    folders.Add(Path.Combine(ctrlPath, HierarchyFolders.Modbus));
                    folders.Add(Path.Combine(ctrlPath, HierarchyFolders.Hmi));
                }
                folders.Add(Path.Combine(groupSubPath, HierarchyFolders.Opc));

                var paramsGroupSubPath = s.Name == "—"
                    ? Path.Combine(root, FolderParams, g.Name)
                    : Path.Combine(root, FolderParams, g.Name, s.Name);
                foreach (var manufacturer in manufacturers)
                    folders.Add(Path.Combine(paramsGroupSubPath, manufacturer));
            }
        }

        folders.Add(Path.Combine(root, FolderPo, HierarchyFolders.UnknownFw));
        folders.Add(Path.Combine(root, FolderParams, HierarchyFolders.UnknownParams));
        folders.Add(Path.Combine(root, FolderConfig));

        return new StructurePlan(root, folders, SnapshotNames());
    }

    /// <summary>Дисковая фаза: создаёт недостающие папки и уносит нераспознанное в «Неизвестное».
    /// В БД не ходит вообще — безопасно выполнять в фоновом потоке.</summary>
    public static EnsureStructureResult ApplyStructurePlan(StructurePlan plan)
    {
        var errors = new List<string>();
        int created = 0;

        foreach (var path in plan.Folders)
        {
            try
            {
                if (Directory.Exists(path)) continue;
                Directory.CreateDirectory(path);
                created++;
            }
            catch (Exception e)
            {
                errors.Add($"{path}: {e.Message}");
            }
        }

        var movedCount = CollectUnknowns(plan.Root, plan.Names).Moved;

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

    /// <summary>БД-фаза для всего, что потом ходит по диску: имена справочников одним снимком.</summary>
    public HierarchyNames SnapshotNames() => new(
        KnownPoNames(),
        KnownParamNames(),
        new HashSet<string>(_db.GetAllControllerModels().Select(c => c.Name), StringComparer.OrdinalIgnoreCase)
        {
            HierarchyFolders.Opc, HierarchyFolders.Instructions, HierarchyFolders.IoMap,
            HierarchyFolders.Modbus, HierarchyFolders.Hmi, HierarchyFolders.UnknownFw,
        },
        new HashSet<string>(_db.GetParamManufacturers(), StringComparer.OrdinalIgnoreCase)
        {
            HierarchyFolders.UnknownParams,
        });

    public MoveNamedResult CollectUnknowns(string root) => CollectUnknowns(root, SnapshotNames());

    public static MoveNamedResult CollectUnknowns(string root, HierarchyNames names)
    {
        var moved = new List<string>();
        var errors = new List<string>();

        var poRoot = Path.Combine(root, FolderPo);
        var poUnknown = Path.Combine(poRoot, HierarchyFolders.UnknownFw);
        MoveUnrecognizedTopLevel(poRoot, poUnknown, names.PoNames, moved, errors);

        var paramsRoot = Path.Combine(root, FolderParams);
        var paramsUnknown = Path.Combine(paramsRoot, HierarchyFolders.UnknownParams);
        MoveUnrecognizedTopLevel(paramsRoot, paramsUnknown, names.ParamNames, moved, errors);

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
    public List<UnknownEntry> ScanUnknownFiles(string root) => ScanUnknownFiles(root, SnapshotNames());

    /// <summary>Дисковая фаза скана: имена справочников уже сняты (SnapshotNames), в БД не ходим —
    /// значит обход сетевого диска можно унести в фоновый поток.</summary>
    public static List<UnknownEntry> ScanUnknownFiles(string root, HierarchyNames names)
    {
        var result = new List<UnknownEntry>();

        var poRoot = Path.Combine(root, FolderPo);
        var poUnknown = Path.Combine(poRoot, HierarchyFolders.UnknownFw);
        CollectEntriesRecursive(poRoot, poUnknown, names.PoNames, names.PoLeafNames, "ПО", result, depth: 0);

        var paramsRoot = Path.Combine(root, FolderParams);
        var paramsUnknown = Path.Combine(paramsRoot, HierarchyFolders.UnknownParams);
        CollectEntriesRecursive(paramsRoot, paramsUnknown, names.ParamNames, names.ParamLeafNames, "Параметры", result, depth: 0);

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

    public SyncFromDiskResult SyncFwFromDisk(string root) => ImportFwCandidates(ScanFwDisk(PlanFwSync(root)));

    /// <summary>БД-фаза: какие папки контроллеров смотреть и какие номера версий там уже известны.
    /// Известные версии берутся ОДНИМ запросом на пару подтип/контроллер (раньше запрос уходил на
    /// каждую найденную папку версии) — тот же ответ, но без похода в БД посреди обхода диска.</summary>
    public FwSyncPlan PlanFwSync(string root)
    {
        var targets = new List<FwSyncTarget>();
        var opcTargets = new List<FwOpcSyncTarget>();
        var controllers = _db.GetAllControllerModels();

        foreach (var g in _db.GetAllEquipmentGroups())
        {
            // Every group is guaranteed at least one subtype row (Database.EnsureEveryGroupHasSubtype) —
            // "—" is the placeholder for "no real subtype division", so this no longer needs a
            // null-subtype fallback branch that used to make sync silently skip such groups entirely.
            foreach (var sub in _db.GetSubtypesForGroup(g.Id!.Value))
            {
                var groupSubPath = sub.Name == "—" ? Path.Combine(root, FolderPo, g.Name) : Path.Combine(root, FolderPo, g.Name, sub.Name);
                foreach (var ctrl in controllers)
                {
                    var known = new HashSet<string>(
                        _db.GetFwVersions(sub.Id, ctrl.Id, includeArchived: true, includeRolledBack: true).Select(v => v.VersionRaw),
                        StringComparer.Ordinal);
                    targets.Add(new FwSyncTarget(sub.Id!.Value, ctrl.Id!.Value, g.Name, sub.Name, ctrl.Name,
                        Path.Combine(groupSubPath, ctrl.Name), known));
                }

                // Нестандартные (ОПЦ) версии лежат не в папке контроллера, а в общей папке «ОПЦ»
                // рядом с ней (см. PoCtrlFolder) — досмотр диска не открывал её вообще, и версия,
                // загруженная с номером заявки/SN на другой машине, не появлялась здесь никогда,
                // сколько ни синхронизируй. Контроллер по самой папке не определить (она общая на
                // все контроллеры подтипа), поэтому он выводится из hw-номера версии — ровно того
                // числа, которым модификация контроллера и опознаётся (controller_modifications.
                // hw_version). Неоднозначный или незнакомый hw пропускается: завести версию не тому
                // контроллеру хуже, чем не завести вовсе.
                var byHw = new Dictionary<int, (int ControllerId, string ControllerName)>();
                var ambiguousHw = new HashSet<int>();
                foreach (var ctrl in controllers)
                    foreach (var m in _db.GetModificationsForController(ctrl.Id!.Value))
                    {
                        if (byHw.TryGetValue(m.HwVersion, out var known2) && known2.ControllerId != ctrl.Id!.Value)
                            ambiguousHw.Add(m.HwVersion);
                        else
                            byHw[m.HwVersion] = (ctrl.Id!.Value, ctrl.Name);
                    }
                foreach (var hw in ambiguousHw) byHw.Remove(hw);

                var knownInSubtype = new HashSet<string>(
                    _db.GetFwVersions(sub.Id, null, includeArchived: true, includeRolledBack: true).Select(v => v.VersionRaw),
                    StringComparer.Ordinal);
                opcTargets.Add(new FwOpcSyncTarget(sub.Id!.Value, g.Name, sub.Name,
                    Path.Combine(groupSubPath, HierarchyFolders.Opc), byHw, knownInSubtype));
            }
        }

        return new FwSyncPlan(targets, opcTargets);
    }

    /// <summary>Дисковая фаза: обход папок версий и чтение CHANGELOG.md. Ничего не пишет и в БД не
    /// ходит — это та самая часть, которая на сетевом диске занимает секунды-минуты и должна идти
    /// в фоновом потоке.</summary>
    public static FwDiskScan ScanFwDisk(FwSyncPlan plan)
    {
        var candidates = new List<FwDiskCandidate>();
        var errors = new List<string>();
        int skipped = 0;

        foreach (var target in plan.Targets)
        {
            if (!Directory.Exists(target.ControllerPath)) continue;

            IEnumerable<string> versionDirs;
            try { versionDirs = Directory.EnumerateDirectories(target.ControllerPath).ToList(); }
            catch (Exception e) { errors.Add($"{target.ControllerPath}: {e.Message}"); continue; }

            foreach (var versionDir in versionDirs)
            {
                var parsed = FwVersionNumber.Parse(Path.GetFileName(versionDir));
                if (parsed is null) continue;
                if (target.KnownVersions.Contains(parsed.Raw)) { skipped++; continue; }

                string filename = "";
                ChangelogContent? changelog = null;
                try
                {
                    // Имя файла — первый файл в папке, но НЕ служебный CHANGELOG.md: при
                    // перечислении он часто оказывается первым по алфавиту, и строка
                    // получала filename="CHANGELOG.md" вместо самой прошивки.
                    var found = Directory.EnumerateFiles(versionDir)
                        .FirstOrDefault(f => !string.Equals(Path.GetFileName(f), ChangelogFile.FileName, StringComparison.OrdinalIgnoreCase));
                    filename = found is null ? "" : Path.GetFileName(found);
                    // Описание и типы пуска берём из CHANGELOG.md, который положила туда
                    // загрузившая машина — заглушка остаётся только там, где файла нет.
                    changelog = ChangelogFile.TryRead(versionDir);
                }
                catch (Exception e)
                {
                    errors.Add($"{versionDir}: {e.Message}");
                }

                candidates.Add(new FwDiskCandidate(target, parsed, versionDir, filename, changelog));
            }
        }

        foreach (var opc in plan.OpcTargets ?? new List<FwOpcSyncTarget>())
        {
            if (!Directory.Exists(opc.OpcPath)) continue;

            IEnumerable<string> versionDirs;
            try { versionDirs = Directory.EnumerateDirectories(opc.OpcPath).ToList(); }
            catch (Exception e) { errors.Add($"{opc.OpcPath}: {e.Message}"); continue; }

            foreach (var versionDir in versionDirs)
            {
                var parsed = FwVersionNumber.Parse(Path.GetFileName(versionDir));
                if (parsed is null) continue;
                if (opc.KnownVersions.Contains(parsed.Raw)) { skipped++; continue; }
                // Контроллер выводится из hw-номера (см. PlanFwSync); не вывелся — пропускаем.
                if (!opc.ControllerByHw.TryGetValue(parsed.HwVersion, out var ctrl)) { skipped++; continue; }

                var target = new FwSyncTarget(opc.SubtypeId, ctrl.ControllerId, opc.GroupName, opc.SubtypeName,
                    ctrl.ControllerName, opc.OpcPath, opc.KnownVersions);
                candidates.Add(new FwDiskCandidate(target, parsed, versionDir,
                    ReadFirmwareFilename(versionDir, errors), ChangelogFile.TryRead(versionDir), IsOpc: true));
                // Две ОПЦ-папки одного подтипа с одним номером версии — теоретически возможны только
                // при ручной правке диска; помечаем номер как известный, чтобы во второй раз он не
                // завёлся ещё одной записью в этом же проходе.
                opc.KnownVersions.Add(parsed.Raw);
            }
        }

        return new FwDiskScan(candidates, skipped, errors);
    }

    /// <summary>Имя файла прошивки в папке версии — первый файл, кроме служебного CHANGELOG.md.</summary>
    private static string ReadFirmwareFilename(string versionDir, List<string> errors)
    {
        try
        {
            var found = Directory.EnumerateFiles(versionDir)
                .FirstOrDefault(f => !string.Equals(Path.GetFileName(f), ChangelogFile.FileName, StringComparison.OrdinalIgnoreCase));
            return found is null ? "" : Path.GetFileName(found);
        }
        catch (Exception e)
        {
            errors.Add($"{versionDir}: {e.Message}");
            return "";
        }
    }

    /// <summary>БД-фаза: заводит записи по тому, что нашёл обход диска.</summary>
    public SyncFromDiskResult ImportFwCandidates(FwDiskScan scan)
    {
        var errors = new List<string>(scan.Errors);
        var addedItems = new List<string>();
        int added = 0;

        foreach (var c in scan.Candidates)
        {
            try
            {
                // Номер заявки и заводской SN нигде, кроме имени файла, на диске не записаны
                // (CHANGELOG.md их не хранит) — вытаскиваем оттуда, иначе нестандартная версия
                // коллеги приехала бы сюда как обычная, без заявки и SN.
                var (requestNum, cabinetSn) = c.IsOpc
                    ? FirmwareNaming.ParseOpcMarkers(c.Filename)
                    : ("", "");

                _db.AddFwVersion(new Domain.FwVersionRecord
                {
                    IsOpc = c.IsOpc,
                    RequestNum = requestNum,
                    CabinetSn = cabinetSn,
                    SubtypeId = c.Target.SubtypeId,
                    ControllerId = c.Target.ControllerId,
                    EqPrefix = c.Version.EqPrefix,
                    SubPrefix = c.Version.SubPrefix,
                    HwVersion = c.Version.HwVersion,
                    SwVersion = c.Version.SwVersion,
                    DtStr = c.Version.DtStr,
                    VersionRaw = c.Version.Raw,
                    Filename = c.Filename,
                    DiskPath = c.VersionDir,
                    Description = string.IsNullOrWhiteSpace(c.Changelog?.Description)
                        ? ChangelogFile.DiskSyncPlaceholder
                        : c.Changelog!.Description,
                    Changelog = c.Changelog?.Description ?? "",
                    LaunchTypes = c.Changelog?.LaunchTypes ?? new List<string>(),
                    // Теги — из того же CHANGELOG.md, что и описание: версия, приехавшая сюда
                    // сканированием диска (а не через конфиг администратора), иначе оставалась бы
                    // вообще без тегов и находилась только по названию папки.
                    Tags = TagString.Join(c.Changelog?.Tags ?? new List<string>()),
                    Status = "active",
                });
                foreach (var tag in c.Changelog?.Tags ?? new List<string>())
                    _db.AddTag(tag);
                added++;
                addedItems.Add(c.Label);
            }
            catch (Exception e)
            {
                errors.Add($"{c.Label}: {e.Message}");
            }
        }

        return new SyncFromDiskResult(errors.Count == 0, added, scan.Skipped, addedItems, errors);
    }
}
