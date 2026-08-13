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

/// <summary>Папка «ОПЦ», которую надо просмотреть. Их теперь два вида, и различаются они ровно одним:
/// знаем ли мы контроллер ИЗ ПУТИ.
/// <list type="bullet">
/// <item><description><b>Новая раскладка</b> (этап 5) — «ОПЦ» внутри папки контроллера. Контроллер
/// известен, <see cref="ControllerId"/> заполнен, <see cref="ControllerByHw"/> не нужен.</description></item>
/// <item><description><b>Прежняя</b> — одна «ОПЦ» на подтип. Контроллера в пути нет, поэтому он, как и
/// раньше, выводится из hw-числа версии по карте <see cref="ControllerByHw"/>; неоднозначный hw
/// по-прежнему означает «пропустить» (завести версию не тому контроллеру хуже, чем не завести).</description></item>
/// </list></summary>
public record FwOpcSyncTarget(int SubtypeId, string GroupName, string SubtypeName, string OpcPath,
    Dictionary<int, (int ControllerId, string ControllerName)> ControllerByHw, HashSet<string> KnownVersions)
{
    public int? ControllerId { get; init; }
    public string ControllerName { get; init; } = "";
}

public record FwSyncPlan(List<FwSyncTarget> Targets, List<FwOpcSyncTarget>? OpcTargets = null);

/// <summary>Найденная на диске папка версии, которой ещё нет в БД, со всем, что удалось вычитать
/// рядом (имя файла прошивки, CHANGELOG.md). Ничего не записывает — запись делает ImportFwCandidates.</summary>
public record FwDiskCandidate(FwSyncTarget Target, FwVersionNumber Version, string VersionDir,
    string Filename, ChangelogContent? Changelog, bool IsOpc = false)
{
    /// <summary>Номер заявки и заводской SN, вычитанные ИЗ ИМЕНИ ПАПКИ (новая раскладка ОПЦ, этап 5).
    /// null — имя папки их не содержит, и они, как раньше, разбираются из имени файла прошивки
    /// (см. ImportFwCandidates): раньше это было единственное место на диске, где они записаны.</summary>
    public (string RequestNum, string CabinetSn)? OpcMarkers { get; init; }

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
    /// <summary>Верхняя папка дерева прошивок — та самая опора, по которой чужой путь переносится на
    /// нашу форму диска (<see cref="FirmwarePathLocalizer"/>) и по которой из пути версии вычисляется
    /// корень (<see cref="VersionDocFolders"/>). Поэтому она открыта наружу: имя «ПО», написанное в
    /// трёх местах строкой, однажды разъедется.</summary>
    public const string FolderPo = "ПО";
    private const string FolderParams = "Параметры";
    private const string FolderConfig = "Конфиг";

    private readonly Database _db;

    public HierarchyService(Database db) => _db = db;

    // ── Path builders ─────────────────────────────────────────────────────────

    /// <summary>Папка типа/подтипа в дереве ПО — общий «родитель» всех папок контроллеров и
    /// «Паспорт». У подтипа-заглушки «—» своего сегмента нет (см. Database.EnsureEveryGroupHasSubtype),
    /// за него стоит папка самого типа.</summary>
    public static string GroupSubFolder(string root, string groupName, string subName)
    {
        var parts = new List<string> { root, FolderPo, groupName };
        if (subName != "—") parts.Add(subName);
        return Path.Combine(parts.ToArray());
    }

    /// <summary>Папка, внутри которой лежат папки версий. Для ОПЦ это больше НЕ подмена контроллера
    /// («ОПЦ» вместо его имени), а подпапка ВНУТРИ него — docs/hierarchy-rework-plan.md, этап 5:
    /// раньше по пути ОПЦ-версии нельзя было понять контроллер, и досмотр диска гадал его по hw-числу,
    /// молча пропуская неоднозначные (жалоба «ОПЦ-версия коллеги не появилась»). Старое место
    /// (<see cref="LegacyOpcFolder"/>) остаётся полностью читаемым — см. PlanFwSync/ScanFwDisk.</summary>
    private string PoCtrlFolder(string root, string groupName, string subName, string controller, bool isOpc)
    {
        var ctrl = Path.Combine(GroupSubFolder(root, groupName, subName), controller);
        return isOpc ? OpcLayout.ControllerOpcFolder(ctrl) : ctrl;
    }

    /// <summary>Прежнее место ОПЦ — одна папка на подтип, рядом с папками контроллеров. Сюда НИЧЕГО
    /// больше не пишется, но всё, что накопилось, читается как раньше, пока не выполнена перестройка
    /// диска (DiskLayoutMigrator) и пока в конторе остаются машины со старым клиентом.</summary>
    public static string LegacyOpcFolder(string root, string groupName, string subName) =>
        OpcLayout.SubtypeOpcFolder(GroupSubFolder(root, groupName, subName));

    /// <summary>Папка версии. У ОПЦ имя папки — это номер заявки/заводской SN шкафа, а не строка
    /// версии (этап 5): ОПЦ заводится под конкретный шкаф, и человек ищет его именно по этим номерам.
    /// Номер версии такой папки хранится в CHANGELOG.md — см. OpcLayout.ResolveVersion.</summary>
    public string FwPath(string root, string groupName, string subName, string controller, string versionStr,
        bool isOpc = false, string requestNum = "", string cabinetSn = "") =>
        Path.Combine(PoCtrlFolder(root, groupName, subName, controller, isOpc),
            isOpc ? OpcLayout.FolderName(requestNum, cabinetSn, versionStr) : versionStr);

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

    /// <summary>Переименовывает папки контроллера во ВСЕХ ветках дерева ПО (контроллер — это лист под
    /// каждым типом/подтипом: ПО\&lt;тип&gt;\&lt;подтип&gt;\&lt;контроллер&gt;) и перекидывает сохранённые пути уже
    /// загруженных прошивок. Как и с типом/подтипом, имя контроллера читается живьём из справочника при
    /// каждом EnsureStructure/FwPath, поэтому правка только в БД осиротила бы старые папки и сломала
    /// «Открыть» для всего, что залито под старым именем. RemapPathPrefix сверяет полный путь-сегмент
    /// (равенство или «old\»), так что имя-префикс другого контроллера (SMH4 vs SMH4X) не заденется.</summary>
    public RenameFolderResult RenameControllerFolders(string root, string oldName, string newName)
    {
        if (oldName == newName) return new RenameFolderResult(true, null, 0);

        var errors = new List<string>();
        int remapped = 0;
        foreach (var g in _db.GetAllEquipmentGroups())
        {
            var subs = _db.GetSubtypesForGroup(g.Id!.Value);
            var subNames = subs.Count == 0 ? new List<string> { "—" } : subs.Select(s => s.Name).ToList();
            foreach (var sn in subNames)
            {
                var oldPath = ControllerFolder(root, g.Name, sn, oldName);
                var newPath = ControllerFolder(root, g.Name, sn, newName);
                var existed = Directory.Exists(oldPath);
                TryRenameFolder(oldPath, newPath, errors);
                if (existed && !Directory.Exists(oldPath) && Directory.Exists(newPath))
                    remapped += _db.RemapPathPrefix(oldPath, newPath);
            }
        }
        return errors.Count > 0
            ? new RenameFolderResult(false, string.Join("\n", errors), remapped)
            : new RenameFolderResult(true, null, remapped);
    }

    // ── Переписывание hw уже загруженных прошивок ────────────────────────────

    public record HwRewriteResult(bool Ok, int UpdatedRows, List<string> Renamed, List<string> Errors);

    /// <summary>Переписывает hw_version всех уже загруженных прошивок ОДНОГО контроллера со старого
    /// значения на новое — «скрипт», который выправляет уже залитые прошивки, когда оператор
    /// прямо на рабочем месте меняет hw модификации (напр. PIXEL2-1321, ошибочно заведённую как
    /// hw 44, надо перевести на настоящую ревизию 1321). hw зашит в строку версии
    /// (FwVersionNumber — 3-й сегмент, дополнен до 4 знаков), а имя папки версии на диске = этой самой
    /// строке, поэтому запись в БД (hw_version/version_raw/disk_path) и физическая папка должны
    /// переехать вместе: правка только БД осиротила бы старую папку, и ближайший обход диска затянул
    /// бы её обратно как отдельную «новую» прошивку. Файлы ВНУТРИ папки имена не меняют (как и
    /// RenameGroupFolder) — открытие идёт по disk_path+filename и не ломается; переименовывается
    /// только сама папка версии и колонки БД. Каждая запись обрабатывается независимо: сбой на одной
    /// (папка занята, конфликт имён) не мешает остальным. oldHw == newHw — пустая операция.</summary>
    public HwRewriteResult RewriteControllerHwVersion(string root, int controllerId, int oldHw, int newHw)
        => RewriteHw(root, controllerId, oldHw, newHw, replay: false);

    /// <summary>Проигрывание hw-переписывания, приехавшего через синхронизацию от коллеги (см.
    /// ConfigSyncService.ReplayHwRewrites, ExportedHwRewrite). От прямого действия оператора отличается
    /// только ТОЛЕРАНТНОСТЬЮ к диску: физическую папку версии/панели у себя уже переименовал тот, кто
    /// правил hw (сетевой диск общий), поэтому «старой папки нет / новая уже на месте» здесь не ошибка,
    /// а ожидаемое состояние — обновляем лишь запись БД, чтобы version_raw/disk_path у этой машины
    /// совпали со снимком и импорт fw_versions не завёл дубликат. Идемпотентно: строк со старым hw уже
    /// нет → пустая операция; целевой version_raw уже занят другой строкой (напр. дубль от старой
    /// версии приложения) → эту строку не трогаем, чтобы не создать два ряда с одним ключом.</summary>
    public HwRewriteResult ReplayControllerHwRewrite(string root, int controllerId, int oldHw, int newHw)
        => RewriteHw(root, controllerId, oldHw, newHw, replay: true);

    private HwRewriteResult RewriteHw(string root, int controllerId, int oldHw, int newHw, bool replay)
    {
        var renamed = new List<string>();
        var errors = new List<string>();
        if (oldHw == newHw) return new HwRewriteResult(true, 0, renamed, errors);

        int updated = 0;
        foreach (var v in _db.GetFwVersionsByControllerAndHw(controllerId, oldHw))
        {
            var parsed = FwVersionNumber.Parse(v.VersionRaw);
            if (parsed is null)
            {
                errors.Add($"{v.VersionRaw}: не разобрать строку версии — пропущено.");
                continue;
            }
            // Пересобираем строку версии с новым hw, сохраняя точный суффикс даты/времени (может быть пуст).
            var core = $"{parsed.EqPrefix}.{parsed.SubPrefix}.{newHw.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)}.{parsed.SwVersion.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)}";
            var newRaw = string.IsNullOrEmpty(parsed.DtStr) ? core : $"{core}.{parsed.DtStr}";

            // Проигрывание: строка с целевым ключом (новым hw) уже есть. Это либо идемпотентный
            // повтор, либо у получателя одновременно остались и старая строка (фантом), и приехавший
            // дублем новый ряд — ровно жалоба «локальная папка старого hw не затирается». Старую в
            // целевой ключ не переименовываем (было бы два ряда с одним version_raw), но если её
            // файлов на реально доступном диске УЖЕ нет (папку переименовал автор правки на общей
            // шаре), то это фантом завершённого переименования — убираем его локально, чтобы дубль
            // исчез. Диск недоступен / папка ещё на месте — не трогаем (не хуже, чем было).
            if (replay && _db.FindFwVersionIdByNaturalKey(v.SubtypeId, controllerId, newRaw, v.Id!.Value) is not null)
            {
                if (IsStaleAfterRewrite(v, root))
                {
                    _db.DeleteFwVersion(v.Id!.Value);
                    renamed.Add($"{v.VersionRaw} → {newRaw} (убран дубль)");
                    updated++;
                }
                continue;
            }

            // disk_path в БД абсолютный и мог быть записан на машине КОЛЛЕГИ (та же шара, но у него
            // буква диска, у нас UNC — или наоборот). Проверяем существование и переименовываем
            // ЛОКАЛЬНУЮ форму пути (FirmwarePathLocalizer перецепляет хвост от «ПО» на наш корень) —
            // иначе Directory.Exists на чужом пути всегда ложь и правка hw падает «папка на диске
            // недоступна», хотя папка на месте (тот же класс бага, что localizer чинит в поиске/
            // истории/обновлениях). Записанный disk_path тоже кладём в локальной форме — она
            // корректно локализуется у всех, а для матчинга при синхронизации disk_path не участвует.
            var localDisk = FirmwarePathLocalizer.Localize(v.DiskPath, root);
            var newDiskPath = localDisk;

            // Запись с файлами на диске: папку надо переименовать. Запись без файлов (disk_path пуст)
            // правится только в БД.
            if (!string.IsNullOrWhiteSpace(v.DiskPath))
            {
                var parent = Path.GetDirectoryName(localDisk);
                var candidate = parent is null ? newRaw : Path.Combine(parent, newRaw);

                // Оператор: старой папки нет — НЕ трогаем и БД, иначе version_raw разъедется с именем
                // папки, когда шара вернётся. При проигрывании старой папки обычно уже нет (её
                // переименовал автор на общей шаре) — это норма, продолжаем.
                if (!replay && !Directory.Exists(localDisk))
                {
                    errors.Add($"{v.VersionRaw}: папка на диске недоступна — пропущено.");
                    continue;
                }

                if (!string.Equals(candidate, localDisk, StringComparison.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(candidate))
                    {
                        // Целевая папка уже на месте. Для оператора это конфликт имён — прерываем;
                        // при проигрывании ожидаемо (её переименовал автор правки) — просто
                        // перецеливаем запись БД на неё.
                        if (!replay)
                        {
                            errors.Add($"«{newRaw}» уже существует на диске — версия {v.VersionRaw} пропущена.");
                            continue;
                        }
                    }
                    else if (Directory.Exists(localDisk))
                    {
                        try
                        {
                            Directory.Move(localDisk, candidate);
                            renamed.Add($"{v.VersionRaw} → {newRaw}");
                        }
                        catch (Exception e)
                        {
                            errors.Add($"{v.VersionRaw}: {e.Message}");
                            continue;
                        }
                    }
                    // else — только проигрывание: ни старой, ни новой папки на этой машине сейчас нет
                    // (offline-шара или локально-закреплённая копия). Обновляем только БД канонической
                    // новой строкой: папка на общей шаре у автора уже под новым именем, запись должна
                    // с ней совпасть, иначе импорт снимка заведёт дубль.
                }
                newDiskPath = candidate;
            }

            // Панель (HMI) лежит НЕ в папке версии, а в общей папке HMI контроллера под именем
            // «{версия}_hmi», поэтому переименование папки версии выше её не задело. Если эта панель
            // принадлежит именно ЭТОЙ версии (имя папки начинается со старой строки версии), её тоже
            // надо переименовать и переписать hmi_path — иначе карточка навсегда покажет «HMI от версии
            // {старый hw}», хотя панель обновлять никто не обновлял (баг pixel2: hw 044→1321, а панель
            // осталась «2.4.044.0005_hmi»). Унаследованную от другой версии панель (имя ≠ этой версии)
            // не трогаем — там пометка «от версии X» верна. RenameOwnHmiFolder толерантен к уже
            // переименованной папке (вернёт новый путь, если он есть) — годится и для проигрывания.
            // Панель тоже могла прийти с чужой формой пути: переименовываем локализованную папку HMI,
            // но перецеливаем hmi_path в БД от ИСХОДНОГО сохранённого значения — RepointHmiPath матчит
            // его у всех записей, унаследовавших эту же панель.
            var localHmi = FirmwarePathLocalizer.Localize(v.HmiPath, root);
            var newHmiPath = RenameOwnHmiFolder(localHmi, v.VersionRaw, newRaw, errors);
            if (!string.Equals(newHmiPath, localHmi, StringComparison.Ordinal))
                _db.RepointHmiPath(v.HmiPath, newHmiPath);

            _db.UpdateFwVersionHw(v.Id!.Value, newHw, newRaw, newDiskPath);
            updated++;
        }
        return new HwRewriteResult(errors.Count == 0, updated, renamed, errors);
    }

    // ── Перенос версии на другую модель контроллера ──────────────────────────

    /// <summary>Результат переноса версии на другой контроллер. <see cref="Moved"/> — папку на диске
    /// действительно перенесли (false, когда переносить было нечего: запись без файлов, ОПЦ-версия
    /// либо проигрывание чужого события, где папку уже перенёс автор правки). При Ok=false в
    /// <see cref="Errors"/> лежит причина отказа, и операция тогда НЕ трогала ни диск, ни БД. При
    /// Ok=true список тоже может быть непуст — туда попадает только незначащий сбой переноса папки
    /// панели (сама версия при этом перенесена, панель осталась на прежнем месте и открывается).</summary>
    public record ReassignResult(bool Ok, bool Moved, string NewDiskPath, List<string> Errors);

    /// <summary>Переносит версию прошивки на другую модель контроллера — и запись в каталоге, И её
    /// папку на диске. Имя контроллера входит в путь (ПО\&lt;тип&gt;\&lt;подтип&gt;\&lt;контроллер&gt;\&lt;версия&gt;),
    /// поэтому правка одной лишь записи (как было раньше — Database.ReassignFwVersionController в
    /// одиночку) осиротила бы папку: ближайший досмотр диска (PlanFwSync/ScanFwDisk) увидел бы её под
    /// СТАРЫМ контроллером, не нашёл бы в известных версиях этой пары и завёл бы ОТДЕЛЬНУЮ запись —
    /// фантом, который тут же попадал в очередь модерации. Ровно та же причина, по которой
    /// переписывание hw двигает папку версии (см. RewriteHw выше), и сделано в той же манере.
    ///
    /// Что двигается: сама папка версии и «своя» папка панели (HMI лежит не внутри папки версии, а в
    /// общей папке HMI контроллера под именем «{версия}_hmi», см. RewriteHw). Что НЕ двигается: Карта
    /// ВВ / Инструкция / Карта modbus — это общие документы контроллера, на них могут ссылаться другие
    /// версии, и унести их за одной означало бы сломать остальные; они открываются по сохранённому
    /// абсолютному пути и продолжают работать.
    ///
    /// Устойчивость: шара недоступна или папки версии на месте нет — операция отменяется ЦЕЛИКОМ
    /// (БД не трогаем, внятная строка в Errors), иначе запись разъехалась бы с диском, когда шара
    /// вернётся. Целевая папка уже существует — для оператора это конфликт (отмена), при
    /// проигрывании чужого события — ожидаемое состояние (её перенёс автор), просто перецеливаем
    /// запись. Повторный запуск — пустая операция: контроллер уже новый, вернётся Ok=false без
    /// ошибок.
    ///
    /// <paramref name="replay"/> — проигрывание переноса, приехавшего от коллеги (см.
    /// ConfigSyncService.ReplayCtrlReassigns): толерантно к тому, что папку на общей шаре уже
    /// перенесли, и к недоступному диску (тогда правится только запись, чтобы натуральный ключ
    /// совпал со снимком и импорт fw_versions не завёл дубль).</summary>
    public ReassignResult ReassignFwVersionToController(string root, int fwVersionId, int newControllerId, bool replay = false)
    {
        var errors = new List<string>();
        var v = _db.GetFwVersionById(fwVersionId);
        if (v is null || newControllerId <= 0 || v.ControllerId == newControllerId)
            return new ReassignResult(false, false, "", errors);

        var newCtrl = _db.GetAllControllerModels().FirstOrDefault(c => c.Id == newControllerId);
        if (newCtrl is null)
        {
            errors.Add("Целевой контроллер не найден в справочнике — перенос отменён.");
            return new ReassignResult(false, false, "", errors);
        }
        var names = _db.GetFwVersionNames(fwVersionId);
        if (names is null)
        {
            errors.Add("Не удалось определить тип/подтип шкафа версии — перенос отменён.");
            return new ReassignResult(false, false, "", errors);
        }

        var localDisk = FirmwarePathLocalizer.Localize(v.DiskPath, root);
        var newDiskPath = localDisk;
        var moved = false;

        // ОПЦ-версия ПРЕЖНЕЙ раскладки лежит в общей папке «ОПЦ» подтипа — её путь от контроллера не
        // зависит, двигать нечего. ОПЦ, уже переехавшая внутрь контроллера (этап 5), наоборот, зависит
        // от него ровно так же, как обычная версия, и обязана переехать вместе с записью. Запись без
        // файлов не двигается в любом случае.
        var pathDependsOnController = !string.IsNullOrWhiteSpace(v.DiskPath)
            && (!v.IsOpc || IsInsideControllerOpc(localDisk));
        if (pathDependsOnController)
        {
            var target = FwPath(root, names.Value.GroupName, names.Value.SubtypeName, newCtrl.Name, v.VersionRaw,
                v.IsOpc, v.RequestNum, v.CabinetSn);

            if (!replay && (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)))
            {
                errors.Add("Сетевой диск недоступен — перенос отменён, запись не изменена.");
                return new ReassignResult(false, false, "", errors);
            }

            if (string.Equals(target, localDisk, StringComparison.OrdinalIgnoreCase))
            {
                newDiskPath = target; // уже там (напр. повторное проигрывание) — двигать нечего
            }
            else if (Directory.Exists(target))
            {
                if (!replay)
                {
                    errors.Add($"«{target}» уже существует на диске — перенос отменён, запись не изменена.");
                    return new ReassignResult(false, false, "", errors);
                }
                newDiskPath = target; // проигрывание: папку уже перенёс автор правки
            }
            else if (Directory.Exists(localDisk))
            {
                try
                {
                    var parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    Directory.Move(localDisk, target);
                    newDiskPath = target;
                    moved = true;
                }
                catch (Exception e)
                {
                    errors.Add($"{v.VersionRaw}: {e.Message}");
                    return new ReassignResult(false, false, "", errors);
                }
            }
            else if (!replay)
            {
                // Оператор: папки нет там, где её ждёт запись. Правка только БД разъехалась бы с
                // диском — отменяем целиком и говорим об этом.
                errors.Add($"Папка версии на диске недоступна ({localDisk}) — перенос отменён, запись не изменена.");
                return new ReassignResult(false, false, "", errors);
            }
            else
            {
                // Проигрывание при offline-шаре: ни старой, ни новой папки здесь сейчас не видно.
                // Запись всё равно приводим к каноническому виду — папка на общей шаре у автора уже
                // под новым контроллером, иначе импорт снимка заведёт дубль.
                newDiskPath = target;
            }
        }

        // «Своя» панель переезжает в папку HMI НОВОГО контроллера; унаследованную от другой версии
        // (имя папки не про эту версию) не трогаем — там пометка «от версии X» остаётся верной.
        var localHmi = FirmwarePathLocalizer.Localize(v.HmiPath, root);
        var newHmiDir = HmiPath(root, names.Value.GroupName, names.Value.SubtypeName, newCtrl.Name);
        var newHmi = MoveOwnHmiFolder(localHmi, v.VersionRaw, newHmiDir, errors);
        if (!string.Equals(newHmi, localHmi, StringComparison.Ordinal))
            _db.RepointHmiPath(v.HmiPath, newHmi);

        _db.ReassignFwVersionController(fwVersionId, newControllerId, newDiskPath);
        return new ReassignResult(true, moved, newDiskPath, errors);
    }

    /// <summary>Лежит ли эта папка версии в «ОПЦ» ВНУТРИ контроллера (новая раскладка, этап 5), а не в
    /// общей «ОПЦ» подтипа. Отличается одним уровнем: у новой над «ОПЦ» стоит папка контроллера, у неё
    /// самой рядом лежат папки документов; у прежней над «ОПЦ» сразу тип/подтип. Ровно та же проверка,
    /// что и в VersionLayout.ControllerFolderOf, — там она и живёт, чтобы правило было одно.</summary>
    private static bool IsInsideControllerOpc(string versionDir) =>
        VersionLayout.ControllerFolderOf(versionDir) is not null
        && string.Equals(Path.GetFileName(Path.GetDirectoryName(
            versionDir.TrimEnd(Path.DirectorySeparatorChar)) ?? ""), HierarchyFolders.Opc, StringComparison.OrdinalIgnoreCase);

    // ── Починка путей ОПЦ после этапа 5 ──────────────────────────────────────

    public record OpcRepairResult(int Repaired, int Unresolved, List<string> Details);

    /// <summary>Локальный, идемпотентный проход «найти, куда переехали мои ОПЦ-версии»
    /// (docs/hierarchy-rework-plan.md, этап 5). Нужен потому, что перенос ОПЦ внутрь контроллера —
    /// единственная операция всей перестройки, которая МЕНЯЕТ <c>disk_path</c>, а он у совпавшей записи
    /// при импорте общего конфига не обновляется никогда (Database.ConfigExchange.cs): у всех, кто
    /// перестройку не запускал, ОПЦ-прошивки иначе молча стали бы «⚠ на диске не найдена».
    ///
    /// Что делает: для каждой своей ОПЦ-записи, чей путь на диске больше не существует, ищет папку в
    /// «ОПЦ» её контроллера с тем же номером версии (OpcLayout.FindMigratedFolder) и переписывает
    /// <c>disk_path</c> на неё. Ничего не двигает и не удаляет. Не нашли — путь не трогаем: выдуманный
    /// путь хуже устаревшего. Диск недоступен — выходим сразу, иначе «шара отвалилась» выглядела бы как
    /// «всё переехало».</summary>
    public OpcRepairResult RepairOpcDiskPaths(string root)
    {
        var details = new List<string>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return new OpcRepairResult(0, 0, details);

        int repaired = 0, unresolved = 0;
        foreach (var v in _db.GetAllFwVersionsWithNames(includeArchived: true))
        {
            if (!v.IsOpc || string.IsNullOrWhiteSpace(v.DiskPath)) continue;
            var local = FirmwarePathLocalizer.Localize(v.DiskPath, root);
            if (Directory.Exists(local)) continue;

            var ctrlFolder = Path.Combine(GroupSubFolder(root, v.GroupName, v.SubtypeName), v.CtrlName);
            var found = OpcLayout.FindMigratedFolder(ctrlFolder, v.VersionRaw);
            if (found is null)
            {
                unresolved++;
                details.Add($"{v.GroupName}/{v.SubtypeName}/{v.CtrlName}/{v.VersionRaw}: новое место не найдено");
                continue;
            }

            _db.RepointFwVersionDiskPath(v.Id!.Value, found);
            repaired++;
            details.Add($"{v.VersionRaw}: {local} → {found}");
        }
        return new OpcRepairResult(repaired, unresolved, details);
    }

    /// <summary>Переносит «свою» папку/файл панели в папку HMI ДРУГОГО контроллера и возвращает новый
    /// hmi_path. Отличается от RenameOwnHmiFolder только тем, что меняется родительская папка, а не
    /// имя: при переносе версии имя панели («{версия}_hmi») остаётся прежним. Правила те же —
    /// унаследованную панель не трогаем, на диске двигаем только если исходное есть, а целевого ещё
    /// нет, иначе hmi_path в базе оставляем прежним, чтобы он не разошёлся с реальностью.</summary>
    private static string MoveOwnHmiFolder(string hmiPath, string versionRaw, string newHmiDir, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(hmiPath) || string.IsNullOrWhiteSpace(newHmiDir)) return hmiPath;

        var trimmed = hmiPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        if (!name.StartsWith($"{versionRaw}_hmi", StringComparison.OrdinalIgnoreCase)) return hmiPath;

        var newPath = Path.Combine(newHmiDir, name);
        if (string.Equals(newPath, trimmed, StringComparison.OrdinalIgnoreCase)) return hmiPath;

        try
        {
            if (Directory.Exists(newPath) || File.Exists(newPath)) return newPath; // уже перенесена
            if (!Directory.Exists(trimmed) && !File.Exists(trimmed)) return hmiPath; // ни там, ни там — путь не выдумываем
            Directory.CreateDirectory(newHmiDir);
            if (Directory.Exists(trimmed)) Directory.Move(trimmed, newPath);
            else File.Move(trimmed, newPath);
        }
        catch (Exception e)
        {
            errors.Add($"HMI {versionRaw}: {e.Message}");
            return hmiPath;
        }
        return newPath;
    }

    /// <summary>Переименовывает папку/файл HMI со старой строки версии на новую при правке hw и
    /// возвращает новый hmi_path. Трогает только «свою» панель — ту, чьё имя начинается с
    /// «{oldRaw}_hmi» (её сделали вместе с этой версией); унаследованную от другой версии возвращает
    /// без изменений. На диске переименовывает, только если исходное есть, а целевого ещё нет — иначе
    /// hmi_path в базе оставляем прежним, чтобы он не разошёлся с тем, что реально лежит на диске.</summary>
    private static string RenameOwnHmiFolder(string hmiPath, string oldRaw, string newRaw, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(hmiPath)) return hmiPath;

        var trimmed = hmiPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        var prefix = $"{oldRaw}_hmi";
        // Панель унаследована от другой версии (имя не про эту версию) либо путь непонятного вида — не трогаем.
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return hmiPath;

        var newName = $"{newRaw}_hmi" + name[prefix.Length..]; // сохраняем расширение файла-панели, если было
        var parent = Path.GetDirectoryName(trimmed);
        var newPath = parent is null ? newName : Path.Combine(parent, newName);
        if (string.Equals(newPath, trimmed, StringComparison.OrdinalIgnoreCase)) return hmiPath;

        try
        {
            if (Directory.Exists(newPath) || File.Exists(newPath)) return newPath; // уже переименовано (напр. общая с другой версией папка)
            if (Directory.Exists(trimmed)) Directory.Move(trimmed, newPath);
            else if (File.Exists(trimmed)) File.Move(trimmed, newPath);
            else return hmiPath; // на диске нет ни старого, ни нового — путь не выдумываем
        }
        catch (Exception e)
        {
            errors.Add($"HMI {oldRaw}: {e.Message}");
            return hmiPath;
        }
        return newPath;
    }

    /// <summary>Старая (до-переписывания) строка версии — фантом завершённого переименования: её
    /// файлов на реально доступном диске уже нет. Используется только проигрыванием, только когда
    /// целевой ключ (новый hw) уже занят, чтобы решить, можно ли безопасно убрать оставшийся дубль.
    /// Диск обязан быть доступен: offline-шара «нет папки» ≠ «папку удалили» — тогда возвращаем false
    /// и ничего не сносим. Запись без файлов (disk_path пуст) — метаданные без диска, тоже фантом.</summary>
    private static bool IsStaleAfterRewrite(Domain.FwVersionRecord v, string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return false;
        if (string.IsNullOrWhiteSpace(v.DiskPath)) return true;
        var localDisk = FirmwarePathLocalizer.Localize(v.DiskPath, root);
        return !Directory.Exists(localDisk);
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

    public EnsureStructureResult EnsureStructure(string root, IInstructionStubWriter? stubs = null) =>
        ApplyStructurePlan(PlanStructure(root), stubs);

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
                    // «ОПЦ» теперь внутри контроллера (этап 5). Прежнюю папку на уровне подтипа в план
                    // БОЛЬШЕ НЕ добавляем: она остаётся на диске со всем содержимым и читается, но
                    // заводить её заново под каждый подтип — плодить пустые папки старой раскладки.
                    folders.Add(OpcLayout.ControllerOpcFolder(ctrlPath));
                }
                // Паспорта — рядом с «ОПЦ», у подтипа: см. HierarchyFolders.Passports. Создаётся
                // всегда, как и остальные служебные папки, чтобы оператору было куда положить файл
                // руками и чтобы обход диска не считал её «неизвестной».
                folders.Add(Path.Combine(groupSubPath, HierarchyFolders.Passports));

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
    /// <param name="stubs">Чем рисовать заглушку «Инструкция в разработке». Задан — в каждую папку
    /// «Инструкция», где нет настоящего документа, кладётся заглушка: пустая папка неотличима от
    /// «инструкцию потеряли». null — папки просто создаются пустыми, как было раньше. См.
    /// InstructionStub.</param>
    public static EnsureStructureResult ApplyStructurePlan(StructurePlan plan, IInstructionStubWriter? stubs = null)
    {
        var errors = new List<string>();
        int created = 0;

        foreach (var path in plan.Folders)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                    created++;
                }

                // Версии у этих папок нет (общая папка «Инструкция» контроллера принадлежит всем его
                // версиям сразу), поэтому заглушка ложится под общим именем — см. InstructionStub.
                if (stubs is not null && string.Equals(Path.GetFileName(path), HierarchyFolders.Instructions, StringComparison.Ordinal))
                    InstructionStub.EnsureIn(path, versionRaw: null, stubs, warnings: null);
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
        names.Add(HierarchyFolders.Passports);
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
            // «Паспорт» — лист: внутри лежат папки самих паспортов, названные оператором свободно,
            // и спуск туда пометил бы каждый паспорт как «неизвестное» (та же причина, что у папок
            // контроллеров с их папками версий).
            HierarchyFolders.Passports,
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

    /// <summary>Убирает осиротевшие ярлыки прошивок. Когда файлы версии удалили прямо на диске (мимо
    /// программы), запись-ярлык дополнительного подтипа («{VersionRaw}.lnk» в его папке контроллера,
    /// см. FirmwareSubtypeLinkService.LinkExtras) раньше повисала навсегда: её убирало только явное
    /// «отвязать подтип» (Apply/RemoveShortcut), а обхода, который подчистил бы её по факту исчезновения
    /// файлов, не было (жалоба «корневой файл исчез, а ярлык не исчезает»). Здесь именно он: для каждой
    /// версии, чьи файлы на диске пропали, ищем в её папке контроллера ярлык с её именем и удаляем.
    ///
    /// Работает, только когда корень реально доступен — иначе offline-шара выглядела бы как «всё
    /// пропало» и снесла бы живые ярлыки. Настоящую папку версии основной записи это не трогает: у неё
    /// в папке контроллера лежит сама версия, а не ярлык, поэтому File.Exists(.lnk) там ложь, и удалять
    /// нечего — искать «кто основной» отдельно не нужно. Всё best-effort: занятый файл или недоступная
    /// папка не роняют обход, а собираются в список ошибок.</summary>
    public (int Removed, List<string> Errors) PruneOrphanedFirmwareShortcuts(string root)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return (0, errors);

        var removed = 0;
        foreach (var t in _db.GetFwVersionShortcutTargets())
        {
            // Файлы версии ещё на месте (папкой или одиночным файлом) — ярлык живой, не трогаем.
            if (Directory.Exists(t.DiskPath) || File.Exists(t.DiskPath)) continue;
            try
            {
                var link = Path.Combine(
                    ControllerFolder(root, t.GroupName, t.SubtypeName, t.ControllerName, t.IsOpc),
                    $"{t.VersionRaw}.lnk");
                if (File.Exists(link)) { File.Delete(link); removed++; }
            }
            catch (Exception ex)
            {
                errors.Add($"{t.GroupName}/{t.SubtypeName}/{t.ControllerName}/{t.VersionRaw}.lnk: {ex.Message}");
            }
        }
        return (removed, errors);
    }

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
                    // Через GetKnownVersionRaws, а НЕ GetFwVersions: сюда обязаны попасть и УДАЛЁННЫЕ
                    // (помеченные надгробием) версии — см. док GetKnownVersionRaws о том, как иначе
                    // досмотр диска воскрешал удалённую прошивку новой строкой прямо в модерацию.
                    var known = _db.GetKnownVersionRaws(sub.Id!.Value, ctrl.Id!.Value);
                    var ctrlPath = Path.Combine(groupSubPath, ctrl.Name);
                    targets.Add(new FwSyncTarget(sub.Id!.Value, ctrl.Id!.Value, g.Name, sub.Name, ctrl.Name,
                        ctrlPath, known));

                    // Новая раскладка ОПЦ (этап 5): «ОПЦ» внутри контроллера — контроллер известен из
                    // пути, гадать по hw не нужно. Тот же набор известных версий, что и у обычных
                    // папок: ОПЦ-версия — такая же строка fw_versions этой пары подтип/контроллер.
                    opcTargets.Add(new FwOpcSyncTarget(sub.Id!.Value, g.Name, sub.Name,
                        OpcLayout.ControllerOpcFolder(ctrlPath),
                        new Dictionary<int, (int, string)>(), known)
                    {
                        ControllerId = ctrl.Id!.Value,
                        ControllerName = ctrl.Name,
                    });
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

                // Та же поправка на надгробия, что и у обычных папок контроллеров выше.
                var knownInSubtype = _db.GetKnownVersionRaws(sub.Id!.Value, null);
                opcTargets.Add(new FwOpcSyncTarget(sub.Id!.Value, g.Name, sub.Name,
                    OpcLayout.SubtypeOpcFolder(groupSubPath), byHw, knownInSubtype));
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

                var filename = ReadFirmwareFilename(versionDir, errors);
                ChangelogContent? changelog = null;
                // Описание и типы пуска берём из CHANGELOG.md, который положила туда
                // загрузившая машина — заглушка остаётся только там, где файла нет.
                try { changelog = ChangelogFile.TryRead(versionDir); }
                catch (Exception e) { errors.Add($"{versionDir}: {e.Message}"); }

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
                // Имя папки ОПЦ больше не обязано быть номером версии (этап 5): номер ищется по
                // цепочке имя папки → CHANGELOG.md → имя файла прошивки. Ничего не дало — пропускаем:
                // запись с выдуманным номером хуже, чем ненайденная папка.
                var parsed = OpcLayout.ResolveVersion(versionDir);
                if (parsed is null) { skipped++; continue; }
                if (opc.KnownVersions.Contains(parsed.Raw)) { skipped++; continue; }

                // Контроллер: из пути (новая раскладка) либо, как раньше, из hw-номера версии.
                int controllerId;
                string controllerName;
                if (opc.ControllerId is { } knownCtrlId)
                {
                    controllerId = knownCtrlId;
                    controllerName = opc.ControllerName;
                }
                else if (opc.ControllerByHw.TryGetValue(parsed.HwVersion, out var ctrl))
                {
                    controllerId = ctrl.ControllerId;
                    controllerName = ctrl.ControllerName;
                }
                else { skipped++; continue; }

                var target = new FwSyncTarget(opc.SubtypeId, controllerId, opc.GroupName, opc.SubtypeName,
                    controllerName, opc.OpcPath, opc.KnownVersions);
                var markers = OpcLayout.ParseFolderName(Path.GetFileName(versionDir.TrimEnd(Path.DirectorySeparatorChar)));
                candidates.Add(new FwDiskCandidate(target, parsed, versionDir,
                    ReadFirmwareFilename(versionDir, errors), ChangelogFile.TryRead(versionDir), IsOpc: true)
                {
                    // Пустая пара «заявка+SN» = имя папки их не несёт (прежняя раскладка) — тогда
                    // разбор идёт по имени файла, как и раньше (см. ImportFwCandidates).
                    OpcMarkers = markers is { RequestNum: "", CabinetSn: "" } ? null : markers,
                });
                // Две ОПЦ-папки одного подтипа с одним номером версии — теоретически возможны только
                // при ручной правке диска; помечаем номер как известный, чтобы во второй раз он не
                // завёлся ещё одной записью в этом же проходе.
                opc.KnownVersions.Add(parsed.Raw);
            }
        }

        return new FwDiskScan(candidates, skipped, errors);
    }

    /// <summary>Имя файла прошивки в папке версии — первый файл, кроме служебных (CHANGELOG.md,
    /// ярлыки). Ищется в «Прошивка\», если версия уже перестроена под новую раскладку, и в самой папке
    /// версии, если ещё нет (VersionLayout — режим совместимости): иначе у переехавшей версии имя файла
    /// оказалось бы пустым, и карточка перестала бы показывать, чем открывать.</summary>
    private static string ReadFirmwareFilename(string versionDir, List<string> errors)
    {
        foreach (var folder in VersionLayout.FirmwareFolders(versionDir))
        {
            try
            {
                var found = Directory.EnumerateFiles(folder)
                    .FirstOrDefault(f => !VersionLayout.IsServiceFile(f));
                if (found is not null) return Path.GetFileName(found);
            }
            catch (Exception e)
            {
                errors.Add($"{folder}: {e.Message}");
            }
        }
        return "";
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
                // Новая раскладка ОПЦ пишет их прямо в ИМЯ ПАПКИ — оттуда и берём, когда есть: имя
                // файла у версии коллеги может быть каким угодно, а имя папки строит сама программа.
                var (requestNum, cabinetSn) = c.IsOpc
                    ? c.OpcMarkers ?? FirmwareNaming.ParseOpcMarkers(c.Filename)
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
