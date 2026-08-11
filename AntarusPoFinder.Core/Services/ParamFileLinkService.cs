using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Привязка одного и того же файла параметров ПЧ/УПП сразу к нескольким подтипам шкафов —
/// и, в отличие от прошивок, к подтипам ЛЮБЫХ типов шкафа, а не только «своего»: один и тот же файл
/// параметров частотника подходит сразу нескольким типам шкафов, тогда как прошивка ПЛК привязана к
/// конкретному контроллеру внутри своего типа.
///
/// Файл копируется на диск ОДИН раз — в папку основного подтипа; каждому дополнительному подтипу
/// заводится своя запись в param_files с тем же disk_path (поэтому «Открыть папку» и поиск ведут к
/// настоящему файлу, а не к ярлыку), а в его собственную папку на диске кладётся ярлык на файл —
/// для тех, кто ходит по сетевой папке проводником, минуя программу.
///
/// Тот же приём, что у прошивок (<see cref="FirmwareSubtypeLinkService"/>) и по той же причине:
/// один файл, лежащий под пятью подтипами, не должен занимать место пять раз. Как и там, набор
/// подтипов правится не только в момент загрузки, но и у уже загруженного файла (см. Apply).</summary>
public static class ParamFileLinkService
{
    /// <summary>Подтип вместе с именем своего типа шкафа: путь на диске у параметров —
    /// «Параметры\{тип}\{подтип}\{производитель}», поэтому одного подтипа для адресации мало, а
    /// брать тип из формы нельзя — подтипы могут быть из разных типов.</summary>
    public record SubtypeTarget(EquipmentSubType Subtype, string GroupName)
    {
        public int Id => Subtype.Id ?? 0;
        public string Display => Subtype.Name == "—" ? Subtype.FolderName : Subtype.Name;
        public string FullDisplay => $"{GroupName} / {Display}";
    }

    /// <summary>Одна запись param_files этого же файла: своя на каждый привязанный подтип.</summary>
    public record SubtypeLink(int ParamFileId, int SubtypeId, bool IsPrimary);

    public record LinkResult(List<int> CreatedIds, List<string> Warnings);

    public record ApplyResult(List<string> Added, List<string> Removed, List<string> Warnings)
    {
        public bool Changed => Added.Count > 0 || Removed.Count > 0;
    }

    /// <summary>Все подтипы, под которыми этот файл сейчас виден. Основной (тот, в чьей папке файл
    /// реально лежит) помечен IsPrimary — отвязать его нельзя: это и есть сам файл, а не ссылка на
    /// него. Дубликаты по подтипу схлопываются: важен набор подтипов, а не сколько строк за ним стоит.</summary>
    public static List<SubtypeLink> CurrentLinks(Database db, ParamFile primary)
    {
        var rows = db.GetParamFilesSharingFile(primary.DiskPath, primary.Filename);
        return rows
            .Where(r => r.Id is not null && r.SubtypeId is not null)
            .GroupBy(r => r.SubtypeId!.Value)
            .Select(g =>
            {
                var row = g.OrderBy(r => r.Id!.Value).First();
                return new SubtypeLink(row.Id!.Value, g.Key, g.Key == primary.SubtypeId);
            })
            .ToList();
    }

    /// <summary>Приводит набор подтипов файла к желаемому: чего нет — заводит (запись + ярлык), что
    /// убрали — архивирует запись и убирает ярлык. Сам файл на диске не трогается НИКОГДА: он общий,
    /// снятие галочки не должно уносить параметры у остальных подтипов.
    ///
    /// Основной подтип в desiredSubtypeIds можно не передавать — он добавляется сам и отвязан быть
    /// не может.</summary>
    public static ApplyResult Apply(Database db, HierarchyService hierarchy, string rootPath,
        ParamFile primary, IReadOnlyList<SubtypeTarget> candidates,
        IReadOnlyCollection<int> desiredSubtypeIds, IShortcutCreator? shortcuts)
    {
        var added = new List<string>();
        var removed = new List<string>();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(primary.DiskPath) || primary.SubtypeId is null)
            return new ApplyResult(added, removed, warnings);

        var byId = candidates.Where(c => c.Subtype.Id is not null).ToDictionary(c => c.Id);
        var desired = new HashSet<int>(desiredSubtypeIds) { primary.SubtypeId.Value };
        var links = CurrentLinks(db, primary);
        var linked = new HashSet<int>(links.Select(l => l.SubtypeId));

        var toAdd = desired.Where(id => !linked.Contains(id) && byId.ContainsKey(id))
            .Select(id => byId[id]).ToList();
        if (toAdd.Count > 0)
        {
            var result = LinkToExtraSubtypes(db, hierarchy, rootPath, primary.SubtypeId.Value, primary,
                toAdd, shortcuts);
            warnings.AddRange(result.Warnings);
            added.AddRange(toAdd.Select(t => t.FullDisplay));
        }

        foreach (var link in links.Where(l => !l.IsPrimary && !desired.Contains(l.SubtypeId)))
        {
            db.DeleteParamFile(link.ParamFileId);
            if (!byId.TryGetValue(link.SubtypeId, out var target))
            {
                removed.Add(link.SubtypeId.ToString());
                continue;
            }
            removed.Add(target.FullDisplay);
            RemoveShortcut(hierarchy, rootPath, primary, target, warnings);
        }

        return new ApplyResult(added, removed, warnings);
    }

    /// <param name="primarySubtypeId">Подтип, в чьей папке файл реально лежит — он же отсеивается из
    /// списка дополнительных, если попал туда.</param>
    /// <param name="primary">Уже сохранённая запись основного подтипа — из неё берутся имя файла,
    /// disk_path, описание и дата; менять их для дополнительных подтипов незачем, файл один и тот же.</param>
    /// <param name="extras">Дополнительные подтипы (возможно из других типов шкафа). Основной, дубли
    /// и записи без Id отсеиваются здесь, а не на стороне вызывающего кода.</param>
    public static LinkResult LinkToExtraSubtypes(Database db, HierarchyService hierarchy, string rootPath,
        int primarySubtypeId, ParamFile primary, IEnumerable<SubtypeTarget> extras, IShortcutCreator? shortcuts)
    {
        var created = new List<int>();
        var warnings = new List<string>();

        var list = (extras ?? Enumerable.Empty<SubtypeTarget>())
            .Where(t => t.Subtype.Id is not null && t.Subtype.Id != primarySubtypeId)
            .GroupBy(t => t.Id).Select(g => g.First())
            .ToList();
        if (list.Count == 0) return new LinkResult(created, warnings);

        foreach (var extra in list)
        {
            // Запись под этот подтип уже может существовать — так бывает при ПЕРЕЗАЛИВКЕ файла с теми
            // же отметками подтипов: сюда приходит тот же набор, что и в прошлый раз. Раньше это был
            // безусловный INSERT, и каждая перезаливка плодила ещё по одной строке на каждый
            // дополнительный подтип (потом их схлопывал DedupeParamFiles, теряя свежие дату/описание).
            // Теперь существующая строка ОБНОВЛЯЕТСЯ — и, что важнее, сохраняет свой sync_id, то есть
            // для коллег остаётся той же самой записью.
            var existing = extra.Subtype.Id is null
                ? null
                : db.FindLiveParamFile(extra.Subtype.Id.Value, primary.Manufacturer, primary.Filename);
            if (existing?.Id is not null)
            {
                db.UpdateParamFileUpload(existing.Id.Value, primary.DiskPath, primary.Description, primary.UploadDate);
            }
            else
            {
                var id = db.AddParamFile(new ParamFile
                {
                    SubtypeId = extra.Subtype.Id,
                    Manufacturer = primary.Manufacturer,
                    Filename = primary.Filename,
                    DiskPath = primary.DiskPath,
                    Description = primary.Description,
                    UploadDate = primary.UploadDate,
                });
                if (id > 0) created.Add(id);
            }

            try
            {
                var folder = ShortcutFolder(hierarchy, rootPath, primary, extra);
                var original = Path.Combine(primary.DiskPath, primary.Filename);

                // Подтип, чья папка на диске совпадает с папкой основного (тот же тип шкафа + тот же
                // производитель, разные подтипы с общей папкой), — ярлык на файл в той же папке был
                // бы ярлыком «сам на себя». Запись заведена, файл под этим подтипом уже находится.
                if (string.Equals(Path.GetFullPath(folder), Path.GetFullPath(primary.DiskPath), StringComparison.OrdinalIgnoreCase))
                    continue;

                Directory.CreateDirectory(folder);

                // Страховка от главной жалобы: физический файл должен быть РОВНО ОДИН, в папке
                // основного подтипа, а у остальных — только ярлык. Если в папке дополнительного
                // подтипа уже лежит полная копия того же файла (её сюда мог положить старый клиент,
                // ручное «сохранить как» или разрешение конфликта облачной синхронизацией диска),
                // убираем её — но ТОЛЬКО при доказанном побайтовом совпадении, иначе оставляем и
                // говорим об этом вслух.
                var strayCopy = Path.Combine(folder, primary.Filename);
                if (File.Exists(strayCopy) &&
                    !ParamFileDuplicateCleanup.TryRemoveIdenticalCopy(original, strayCopy, out var strayReason) &&
                    strayReason is not null)
                    warnings.Add(strayReason);

                shortcuts?.Create(Path.Combine(folder, primary.Filename + ".lnk"), original,
                    $"Параметры {primary.Filename} — файл общий с другим подтипом");
            }
            catch (Exception ex)
            {
                // Ярлык — удобство для проводника; запись уже заведена, в программе файл под этим
                // подтипом уже находится, поэтому загрузка из-за неудачного ярлыка не отменяется.
                warnings.Add($"Ярлык для подтипа {extra.FullDisplay}: {ex.Message}");
            }
        }
        return new LinkResult(created, warnings);
    }

    private static string ShortcutFolder(HierarchyService hierarchy, string rootPath, ParamFile primary,
        SubtypeTarget target) =>
        hierarchy.ParamsPath(rootPath, target.GroupName, target.Subtype.Name, primary.Manufacturer);

    /// <summary>Убирает ярлык отвязанного подтипа. Именно ярлык и только его: настоящий файл лежит в
    /// папке основного подтипа и принадлежит не этой записи.</summary>
    private static void RemoveShortcut(HierarchyService hierarchy, string rootPath, ParamFile primary,
        SubtypeTarget target, List<string> warnings)
    {
        try
        {
            var link = Path.Combine(ShortcutFolder(hierarchy, rootPath, primary, target), primary.Filename + ".lnk");
            if (File.Exists(link)) File.Delete(link);
        }
        catch (Exception ex)
        {
            warnings.Add($"Ярлык подтипа {target.FullDisplay} не удалён: {ex.Message}");
        }
    }
}
