namespace AntarusPoFinder.Core.Domain;

/// <summary>Минимум, нужный обходу диска, чтобы найти и убрать осиротевший ярлык версии: где на диске
/// лежат её файлы (DiskPath — общий у основной записи и всех записей-ярлыков дополнительных подтипов),
/// имя версии (оно же имя файла ярлыка «{VersionRaw}.lnk») и имена группы/подтипа/контроллера ИМЕННО
/// этой записи — по ним строится папка контроллера, в которой лежит её ярлык (HierarchyService.
/// PruneOrphanedFirmwareShortcuts). У основной записи в её папке контроллера лежит настоящая папка
/// версии, а не ярлык, поэтому она под удаление ярлыка не попадает сама собой — искать «кто основной»
/// отдельно не нужно.</summary>
public record FwShortcutTarget(string VersionRaw, string DiskPath, string GroupName,
    string SubtypeName, string ControllerName, bool IsOpc);
