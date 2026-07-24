using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Привязка одной и той же прошивки сразу к нескольким подтипам шкафов — та же идея, что у
/// параметров (<see cref="ParamFileLinkService"/>): файлы на диске лежат ОДИН раз, в папке основного
/// подтипа, а каждому дополнительному заводится своя запись в fw_versions с тем же disk_path (поэтому
/// поиск, «открыть папку» и «скачать локально» ведут к настоящим файлам) плюс ярлык в его папке
/// контроллера — для тех, кто ходит по сетевой папке проводником, минуя программу.
///
/// Раньше это умела только загрузка (FirmwareUploadRequest.ExtraSubtypes) и только в момент загрузки:
/// ошибся с набором подтипов — переделать было нечем. Здесь же и обратная операция (отвязать подтип),
/// поэтому Модерация может править набор у уже загруженной прошивки, а сама загрузка вызывает отсюда
/// же прямой путь и не расходится с ним поведением.</summary>
public static class FirmwareSubtypeLinkService
{
    /// <summary>Одна запись fw_versions этой же прошивки: своя на каждый привязанный подтип.</summary>
    public record SubtypeLink(int FwVersionId, int SubtypeId, bool IsPrimary);

    public record ApplyResult(List<string> Added, List<string> Removed, List<string> Warnings)
    {
        public bool Changed => Added.Count > 0 || Removed.Count > 0;
    }

    /// <summary>Все подтипы, под которыми эта прошивка сейчас видна в поиске. Основной (тот, чья папка
    /// на диске) помечен IsPrimary — его отвязать нельзя: это и есть сама прошивка, а не ссылка на неё.
    /// Дубликаты по подтипу (историческая грязь/повторная загрузка) схлопываются: пользователю важен
    /// набор подтипов, а не сколько строк за ними стоит.</summary>
    public static List<SubtypeLink> CurrentLinks(Database db, FwVersionRecord primary)
    {
        var rows = db.GetFwVersionsSharingFiles(primary.DiskPath, primary.VersionRaw);
        return rows
            .Where(r => r.Id is not null)
            .GroupBy(r => r.SubtypeId)
            .Select(g =>
            {
                // Основная запись — та, чей подтип совпадает с подтипом переданной версии; если её в
                // группе нет, берём самую раннюю (у неё меньший id: копии заводятся после основной).
                var row = g.OrderBy(r => r.Id!.Value).First();
                return new SubtypeLink(row.Id!.Value, g.Key, g.Key == primary.SubtypeId);
            })
            .ToList();
    }

    /// <summary>Приводит набор подтипов прошивки к желаемому: чего нет — заводит (запись + ярлык),
    /// что убрали — помечает удалённым (см. Database.TombstoneFwVersion — обычный DELETE не уехал бы
    /// на другие ПК и запись воскресла бы при следующей синхронизации) и убирает ярлык. Файлы прошивки
    /// на диске не трогаются НИКОГДА: они общие, удаление ссылки не должно уносить саму прошивку.
    ///
    /// Основной подтип в desiredSubtypeIds можно не передавать — он добавляется сам и отвязан быть не
    /// может.</summary>
    public static ApplyResult Apply(Database db, HierarchyService hierarchy, string rootPath,
        FwVersionRecord primary, string groupName, string controllerName,
        IReadOnlyList<EquipmentSubType> groupSubtypes, IReadOnlyCollection<int> desiredSubtypeIds,
        IShortcutCreator? shortcuts)
    {
        var added = new List<string>();
        var removed = new List<string>();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(primary.DiskPath))
            return new ApplyResult(added, removed, warnings);

        var byId = groupSubtypes.Where(s => s.Id is not null).ToDictionary(s => s.Id!.Value);
        var desired = new HashSet<int>(desiredSubtypeIds) { primary.SubtypeId };
        var links = CurrentLinks(db, primary);
        var linked = new HashSet<int>(links.Select(l => l.SubtypeId));

        var toAdd = desired.Where(id => !linked.Contains(id) && byId.ContainsKey(id))
            .Select(id => byId[id]).ToList();
        if (toAdd.Count > 0)
        {
            var primarySubtype = byId.TryGetValue(primary.SubtypeId, out var ps)
                ? ps
                : new EquipmentSubType { Id = primary.SubtypeId };
            var created = LinkExtras(db, hierarchy, rootPath, groupName, controllerName,
                primarySubtype, primary, toAdd, shortcuts, warnings);
            added.AddRange(created.Select(c => DisplayName(c.Subtype)));

            // Копия наследует и состояние модерации: подтип, дописанный к давно выпущенной прошивке,
            // не должен всплывать в «Модерации» как новая непроверенная версия — проверять там нечего,
            // это та же самая прошивка.
            if (primary.Released)
                foreach (var (id, _) in created) db.MarkFwVersionReleased(id);
        }

        foreach (var link in links.Where(l => !l.IsPrimary && !desired.Contains(l.SubtypeId)))
        {
            var name = byId.TryGetValue(link.SubtypeId, out var s) ? DisplayName(s) : link.SubtypeId.ToString();
            db.TombstoneFwVersion(link.FwVersionId);
            removed.Add(name);
            RemoveShortcut(hierarchy, rootPath, groupName, controllerName, name, primary, warnings, byId, link.SubtypeId);
        }

        return new ApplyResult(added, removed, warnings);
    }

    private static string DisplayName(EquipmentSubType s) => s.Name == "—" ? s.FolderName : s.Name;

    /// <summary>Заводит запись fw_versions для каждого дополнительного подтипа и кладёт в папку его
    /// контроллера ярлык на папку версии основного подтипа. Файлы прошивки при этом НЕ копируются —
    /// disk_path у всех записей один и тот же, поэтому и поиск, и «скачать локально», и «открыть
    /// папку» работают с настоящими файлами, а не с ярлыком.
    ///
    /// Номер версии тоже общий (он же — имя файла на диске, внутри самой прошивки вписан именно он):
    /// заводить дополнительным подтипам собственные номера означало бы, что БД показывает один номер,
    /// а файл называется другим. Побочный эффект — следующая загрузка для такого подтипа считает эту
    /// версию своей и берёт следующий sw-номер; это осознанно, подтип действительно её получил.</summary>
    public static List<(int FwVersionId, EquipmentSubType Subtype)> LinkExtras(Database db, HierarchyService hierarchy,
        string rootPath, string groupName, string controllerName, EquipmentSubType primarySubtype,
        FwVersionRecord primary, IEnumerable<EquipmentSubType> extras, IShortcutCreator? shortcuts,
        List<string> warnings)
    {
        var created = new List<(int, EquipmentSubType)>();
        var list = (extras ?? Enumerable.Empty<EquipmentSubType>())
            .Where(s => s.Id is not null && s.Id != primarySubtype.Id)
            .GroupBy(s => s.Id!.Value).Select(g => g.First())
            .ToList();
        if (list.Count == 0) return created;

        foreach (var extra in list)
        {
            var tags = TagString.Parse(primary.Tags);
            if (extra.Name != "—" && !tags.Contains(extra.Name, StringComparer.OrdinalIgnoreCase))
            {
                tags.Add(extra.Name);
                db.AddTag(extra.Name);
            }

            var copy = new FwVersionRecord
            {
                SubtypeId = extra.Id!.Value,
                ControllerId = primary.ControllerId,
                EqPrefix = primary.EqPrefix,
                SubPrefix = primary.SubPrefix,
                HwVersion = primary.HwVersion,
                SwVersion = primary.SwVersion,
                DtStr = primary.DtStr,
                VersionRaw = primary.VersionRaw,
                Filename = primary.Filename,
                DiskPath = primary.DiskPath,
                Description = primary.Description,
                Changelog = primary.Changelog,
                LaunchTypes = primary.LaunchTypes,
                IoMapPath = primary.IoMapPath,
                InstructionsPath = primary.InstructionsPath,
                ModbusMapPath = primary.ModbusMapPath,
                HmiPath = primary.HmiPath,
                ExecutableHint = primary.ExecutableHint,
                HmiExecutableHint = primary.HmiExecutableHint,
                IsOpc = primary.IsOpc,
                RequestNum = primary.RequestNum,
                CabinetSn = primary.CabinetSn,
                AuthorId = primary.AuthorId,
                Status = primary.Status,
                Tags = TagString.Join(tags),
            };
            var id = db.AddFwVersion(copy);
            if (id > 0) created.Add((id, extra));

            try
            {
                var ctrlFolder = hierarchy.ControllerFolder(rootPath, groupName, extra.Name, controllerName, primary.IsOpc);
                Directory.CreateDirectory(ctrlFolder);
                shortcuts?.Create(Path.Combine(ctrlFolder, $"{primary.VersionRaw}.lnk"), primary.DiskPath,
                    $"Прошивка {primary.VersionRaw} — общая с подтипом {primarySubtype.Name}");
            }
            catch (Exception ex)
            {
                // Ярлык — удобство для того, кто смотрит папку проводником; сама запись уже заведена
                // и в программе прошивка под этим подтипом уже находится.
                warnings.Add($"Ярлык для подтипа {extra.Name}: {ex.Message}");
            }
        }
        return created;
    }

    /// <summary>Убирает ярлык отвязанного подтипа. Именно ярлык и только его: настоящие файлы лежат в
    /// папке основного подтипа и принадлежат не этой записи. Если по пути оказался не ярлык (кто-то
    /// положил туда настоящую папку руками) — не трогаем и говорим об этом.</summary>
    private static void RemoveShortcut(HierarchyService hierarchy, string rootPath, string groupName,
        string controllerName, string displayName, FwVersionRecord primary, List<string> warnings,
        Dictionary<int, EquipmentSubType> byId, int subtypeId)
    {
        if (!byId.TryGetValue(subtypeId, out var subtype)) return;
        try
        {
            var link = Path.Combine(
                hierarchy.ControllerFolder(rootPath, groupName, subtype.Name, controllerName, primary.IsOpc),
                $"{primary.VersionRaw}.lnk");
            if (File.Exists(link)) File.Delete(link);
        }
        catch (Exception ex)
        {
            warnings.Add($"Ярлык подтипа {displayName} не удалён: {ex.Message}");
        }
    }
}
