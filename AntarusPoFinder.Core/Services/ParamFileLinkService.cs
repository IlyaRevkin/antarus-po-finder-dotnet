using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Привязка одного и того же файла параметров ПЧ/УПП сразу к нескольким подтипам шкафов.
///
/// Файл копируется на диск ОДИН раз — в папку основного подтипа; каждому дополнительному подтипу
/// заводится своя запись в param_files с тем же disk_path (поэтому «Открыть папку» и поиск ведут к
/// настоящему файлу, а не к ярлыку), а в его собственную папку на диске кладётся ярлык на файл —
/// для тех, кто ходит по сетевой папке проводником, минуя программу.
///
/// Тот же приём, что у прошивок (FirmwareUploadService.LinkToExtraSubtypes) и по той же причине:
/// один файл, лежащий под пятью подтипами, не должен занимать место пять раз.</summary>
public static class ParamFileLinkService
{
    public record LinkResult(List<int> CreatedIds, List<string> Warnings);

    /// <param name="primary">Уже сохранённая запись основного подтипа — из неё берутся имя файла,
    /// disk_path, описание и дата; менять их для дополнительных подтипов незачем, файл один и тот же.</param>
    /// <param name="extras">Дополнительные подтипы. Основной, дубли и записи без Id отсеиваются здесь,
    /// а не на стороне вызывающего кода.</param>
    public static LinkResult LinkToExtraSubtypes(Database db, HierarchyService hierarchy, string rootPath,
        EquipmentGroup group, EquipmentSubType primarySubtype, ParamFile primary,
        IEnumerable<EquipmentSubType> extras, IShortcutCreator? shortcuts)
    {
        var created = new List<int>();
        var warnings = new List<string>();

        var list = (extras ?? Enumerable.Empty<EquipmentSubType>())
            .Where(s => s.Id is not null && s.Id != primarySubtype.Id)
            .GroupBy(s => s.Id!.Value).Select(g => g.First())
            .ToList();
        if (list.Count == 0) return new LinkResult(created, warnings);

        foreach (var extra in list)
        {
            var id = db.AddParamFile(new ParamFile
            {
                SubtypeId = extra.Id,
                Manufacturer = primary.Manufacturer,
                Filename = primary.Filename,
                DiskPath = primary.DiskPath,
                Description = primary.Description,
                UploadDate = primary.UploadDate,
            });
            if (id > 0) created.Add(id);

            try
            {
                var folder = hierarchy.ParamsPath(rootPath, group.Name, extra.Name, primary.Manufacturer);
                Directory.CreateDirectory(folder);
                shortcuts?.Create(Path.Combine(folder, primary.Filename + ".lnk"),
                    Path.Combine(primary.DiskPath, primary.Filename),
                    $"Параметры {primary.Filename} — общие с подтипом {primarySubtype.Name}");
            }
            catch (Exception ex)
            {
                // Ярлык — удобство для проводника; запись уже заведена, в программе файл под этим
                // подтипом уже находится, поэтому загрузка из-за неудачного ярлыка не отменяется.
                warnings.Add($"Ярлык для подтипа {extra.Name}: {ex.Message}");
            }
        }
        return new LinkResult(created, warnings);
    }
}
