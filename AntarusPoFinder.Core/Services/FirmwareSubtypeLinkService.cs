using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Одна и та же прошивка под несколькими подтипами шкафов.
///
/// <b>Как это устроено теперь.</b> Каждому дополнительному подтипу заводится ПОЛНОЦЕННАЯ версия: своя
/// папка в папке его контроллера, скопированные туда файлы и свой номер версии — с префиксом своего
/// подтипа и своим sw-номером. Никаких ярлыков и никакого общего disk_path.
///
/// <b>Как было и почему поменялось.</b> Раньше файлы лежали один раз, у основного подтипа, а
/// остальным заводилась запись с тем же disk_path плюс ярлык в папке контроллера — «чтобы не занимало
/// много памяти». Цена оказалась выше экономии: у копии был тот же номер версии (1.1.0005.0001 и у ПЖ
/// 2.0, и у ПЖ FD, хотя префиксы подтипов 0 и 1), в папке подтипа лежал ярлык вместо прошивки, а
/// документы шкафа приходилось разводить отдельным механизмом (см. VersionDocFolders). Решение:
/// «уходим от ярлыков, кладём всегда саму прошивку, даже если подходит нескольким».
///
/// Прежние записи-ссылки на дисках никуда не делись и продолжают читаться как раньше
/// (<see cref="CurrentLinks"/>); отвязать такую по-прежнему можно, а самостоятельную копию —
/// нет, она обычная версия и убирается как обычная версия (см. <see cref="Coverage"/>).</summary>
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

    /// <summary>Подтип, под которым эта прошивка уже есть, и в каком виде.</summary>
    /// <param name="IsOwnVersion">У подтипа СВОЯ версия — своя папка, свои файлы, свой номер (так
    /// заводятся копии с тех пор, как отказались от ярлыков). Отвязать её нельзя: это самостоятельная
    /// прошивка, а не ссылка, и убирают её как обычную версию — откатом или удалением.</param>
    public record SubtypeCoverage(int SubtypeId, bool IsPrimary, bool IsOwnVersion, string VersionRaw, int FwVersionId);

    /// <summary>Все подтипы, под которыми прошивка сейчас видна, — и старые записи-ссылки (общие
    /// файлы, ярлык в папке подтипа), и новые самостоятельные копии.
    ///
    /// Два источника, потому что и способов два: до отказа от ярлыков копия делила disk_path с
    /// оригиналом (<see cref="CurrentLinks"/>), после — это отдельная версия со своим номером, и
    /// опознаётся она столбцом copy_of (<see cref="Database.GetFwVersionSiblings"/>). Оба вида должны
    /// быть видны в модерации: иначе подтип, у которого копия уже есть, предлагался бы к копированию
    /// снова.</summary>
    public static List<SubtypeCoverage> Coverage(Database db, FwVersionRecord primary)
    {
        var result = CurrentLinks(db, primary)
            .Select(l => new SubtypeCoverage(l.SubtypeId, l.IsPrimary, IsOwnVersion: false,
                primary.VersionRaw, l.FwVersionId))
            .ToList();

        var seen = new HashSet<int>(result.Select(c => c.SubtypeId));
        foreach (var sibling in db.GetFwVersionSiblings(primary.Id ?? 0))
        {
            if (!seen.Add(sibling.SubtypeId)) continue;
            result.Add(new SubtypeCoverage(sibling.SubtypeId, IsPrimary: false, IsOwnVersion: true,
                sibling.VersionRaw, sibling.Id ?? 0));
        }
        return result;
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
        IShortcutCreator? shortcuts = null)
    {
        var added = new List<string>();
        var removed = new List<string>();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(primary.DiskPath))
            return new ApplyResult(added, removed, warnings);

        var byId = groupSubtypes.Where(s => s.Id is not null).ToDictionary(s => s.Id!.Value);
        var desired = new HashSet<int>(desiredSubtypeIds) { primary.SubtypeId };
        var coverage = Coverage(db, primary);
        var links = coverage.Where(c => !c.IsOwnVersion)
            .Select(c => new SubtypeLink(c.FwVersionId, c.SubtypeId, c.IsPrimary)).ToList();
        // Подтип со СВОЕЙ копией считается покрытым: предложить «добавить» его ещё раз значит
        // завести вторую копию той же сборки под тем же шкафом.
        var linked = new HashSet<int>(coverage.Select(c => c.SubtypeId));

        var toAdd = desired.Where(id => !linked.Contains(id) && byId.ContainsKey(id))
            .Select(id => byId[id]).ToList();
        if (toAdd.Count > 0)
        {
            var primarySubtype = byId.TryGetValue(primary.SubtypeId, out var ps)
                ? ps
                : new EquipmentSubType { Id = primary.SubtypeId };
            var created = LinkExtras(db, hierarchy, rootPath, groupName, controllerName,
                primarySubtype, primary, toAdd, warnings);
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

    /// <summary>Заводит дополнительным подтипам ПОЛНОЦЕННЫЕ версии: своя папка в папке контроллера
    /// этого подтипа, свои файлы (папка версии копируется целиком) и свой номер версии — с префиксом
    /// своего подтипа и своим sw-номером в его ряду.
    ///
    /// Раньше здесь заводилась запись с ТЕМ ЖЕ disk_path и ярлык на папку основного подтипа. Обе
    /// претензии Ильи — про это: «уходим от ярлыков, кладём всегда саму прошивку, даже если подходит
    /// нескольким» и «у 2.0 и FD один номер прошивки 1.1.0005.0001, хотя по иерархии 2.0 это 0, а FD
    /// это 1». Номер копии строился копированием полей основной записи, поэтому и совпадал.
    ///
    /// Что копия НЕ наследует: путь на диске, номер версии, имя файла и пути документов, которые
    /// лежали внутри папки версии (они переезжают в копию, см. VersionFolderCopy.RepointPath).
    /// Документ в ОБЩЕЙ папке контроллера остаётся общим — он принадлежит шкафу, а не сборке.</summary>
    public static List<(int FwVersionId, EquipmentSubType Subtype)> LinkExtras(Database db, HierarchyService hierarchy,
        string rootPath, string groupName, string controllerName, EquipmentSubType primarySubtype,
        FwVersionRecord primary, IEnumerable<EquipmentSubType> extras, List<string> warnings)
    {
        var created = new List<(int, EquipmentSubType)>();
        var list = (extras ?? Enumerable.Empty<EquipmentSubType>())
            .Where(s => s.Id is not null && s.Id != primarySubtype.Id)
            .GroupBy(s => s.Id!.Value).Select(g => g.First())
            .ToList();
        if (list.Count == 0) return created;

        var sourceFolder = FirmwareDiskPresence.ResolveVersionDir(primary.DiskPath, primary.VersionRaw);
        // Все копии одной сборки ссылаются на ОДИН корень: копия копии — родня оригиналу, а не
        // отдельная семья, иначе «уже есть своя версия» перестало бы работать через шаг.
        var root = primary.CopyOf.Length > 0 ? primary.CopyOf
            : primary.SyncId.Length > 0 ? primary.SyncId
            : db.GetFwVersionSyncId(primary.Id ?? 0);

        foreach (var extra in list)
        {
            var tags = TagString.Parse(primary.Tags);
            if (extra.Name != "—" && !tags.Contains(extra.Name, StringComparer.OrdinalIgnoreCase))
            {
                tags.Add(extra.Name);
                db.AddTag(extra.Name);
            }
            // Имя подтипа, из папки которого прошивка пришла, тегом копии не остаётся: искать «ПЖ FD»
            // и получать в ответ версию, лежащую под 2.0, — то же самое смешение, от которого уходим.
            if (primarySubtype.Name != "—")
                tags.RemoveAll(t => string.Equals(t, primarySubtype.Name, StringComparison.OrdinalIgnoreCase));

            var number = NextNumberFor(db, primary, extra);
            var targetFolder = hierarchy.FwPath(rootPath, groupName, extra.Name, controllerName, number.Raw,
                primary.IsOpc, primary.RequestNum, primary.CabinetSn);
            var newFilename = FirmwareNaming.BuildFirmwareFilename(number,
                Path.GetExtension(primary.Filename), primary.RequestNum, primary.CabinetSn);

            var copy = VersionFolderCopy.Copy(sourceFolder ?? primary.DiskPath, targetFolder,
                primary.VersionRaw, number.Raw, primary.Filename, newFilename);
            foreach (var problem in copy.Warnings)
                warnings.Add($"Копия для подтипа {DisplayName(extra)}: {problem}");

            // Журнал изменений в копии — про НЕЁ: он и есть то, по чему коллеги со старым клиентом
            // (и досмотр диска) узнают номер версии, лежащей в этой папке.
            try { ChangelogFile.Write(targetFolder, number, primary.LaunchTypes, primary.Description, tags); }
            catch (Exception ex) { warnings.Add($"CHANGELOG.md копии для {DisplayName(extra)}: {ex.Message}"); }

            string Repoint(string? path) => VersionFolderCopy.RepointPath(path, sourceFolder ?? primary.DiskPath,
                targetFolder, primary.VersionRaw, number.Raw);

            var record = new FwVersionRecord
            {
                SubtypeId = extra.Id!.Value,
                ControllerId = primary.ControllerId,
                EqPrefix = primary.EqPrefix,
                SubPrefix = extra.Prefix,
                HwVersion = primary.HwVersion,
                SwVersion = number.SwVersion,
                DtStr = number.DtStr,
                VersionRaw = number.Raw,
                Filename = copy.FirmwareFileName.Length > 0 ? copy.FirmwareFileName : newFilename,
                DiskPath = targetFolder,
                Description = primary.Description,
                Changelog = primary.Changelog,
                LaunchTypes = primary.LaunchTypes,
                IoMapPath = Repoint(primary.IoMapPath),
                InstructionsPath = Repoint(primary.InstructionsPath),
                ModbusMapPath = Repoint(primary.ModbusMapPath),
                HmiPath = Repoint(primary.HmiPath),
                ExecutableHint = VersionFolderCopy.RenameForVersion(primary.ExecutableHint, primary.VersionRaw, number.Raw),
                HmiExecutableHint = primary.HmiExecutableHint,
                IsOpc = primary.IsOpc,
                RequestNum = primary.RequestNum,
                CabinetSn = primary.CabinetSn,
                AuthorId = primary.AuthorId,
                Status = primary.Status,
                Tags = TagString.Join(tags),
                CopyOf = root,
            };
            // Имя файла прошивки в подсказке — то же переименование, что и на диске: подсказка,
            // указывающая на имя соседнего подтипа, открывала бы «файл не найден».
            if (string.Equals(record.ExecutableHint, primary.Filename, StringComparison.OrdinalIgnoreCase))
                record.ExecutableHint = record.Filename;

            var id = db.AddFwVersion(record);
            if (id > 0) created.Add((id, extra));
        }
        return created;
    }

    /// <summary>Номер версии копии: префикс типа тот же, префикс подтипа — СВОЙ, hw тот же (это
    /// свойство контроллера), sw — следующий свободный в ряду этого подтипа. Дата/время берутся у
    /// исходной версии: сборка одна и та же, и разводить их временем копирования значило бы врать про
    /// то, когда программу собрали. У версии без штампа времени копия тоже без штампа.</summary>
    private static FwVersionNumber NextNumberFor(Database db, FwVersionRecord primary, EquipmentSubType extra)
    {
        var sw = db.GetNextSwVersion(extra.Id!.Value, primary.ControllerId, primary.HwVersion);
        if (string.IsNullOrEmpty(primary.DtStr))
            return FwVersionNumber.Build(primary.EqPrefix, extra.Prefix, primary.HwVersion, sw, includeDate: false);

        DateTime? stamp = DateTime.TryParseExact(primary.DtStr, "yyyyMMdd_HHmm",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
        return FwVersionNumber.Build(primary.EqPrefix, extra.Prefix, primary.HwVersion, sw, stamp);
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
