using System.IO;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.Tests.TestHelpers;

/// <summary>Старая связка «одна прошивка — несколько подтипов»: запись с ТЕМ ЖЕ disk_path и тем же
/// номером версии плюс ярлык в папке контроллера подтипа.
///
/// Так это делала загрузка до отказа от ярлыков (см. FirmwareSubtypeLinkService — теперь каждому
/// подтипу заводится своя папка, свои файлы и свой номер). Программа больше таких записей не
/// создаёт, но на дисках их накоплено много, и всё, что их читает, обязано работать как прежде —
/// поэтому тестам нужен способ завести такую связку руками.</summary>
public static class LegacySubtypeLink
{
    public static FwVersionRecord Create(Database db, HierarchyService hierarchy, string root,
        FwVersionRecord primary, EquipmentSubType extra, string groupName, string controllerName,
        IShortcutCreator? shortcuts = null)
    {
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
            Tags = primary.Tags,
        };
        copy.Id = db.AddFwVersion(copy);

        var ctrlFolder = hierarchy.ControllerFolder(root, groupName, extra.Name, controllerName, primary.IsOpc);
        Directory.CreateDirectory(ctrlFolder);
        shortcuts?.Create(Path.Combine(ctrlFolder, $"{primary.VersionRaw}.lnk"), primary.DiskPath,
            $"Прошивка {primary.VersionRaw}");
        return copy;
    }
}
