using System.IO;
using AntarusPoFinder.Core.Infrastructure;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Services;

/// <summary>Copies a firmware version from the network disk into the naladchik's local cache —
/// shared by the manual "Скачать"/"Обновить" button in Search and the background firmware-update
/// scan/window (see MainWindowViewModel.CheckForFirmwareUpdates, FirmwareUpdatesWindow).</summary>
public static class FirmwareSync
{
    public static string CopyToLocal(HierarchyResult result)
    {
        var localDir = LocalFirmwareCache.SanitizeName(result.Name);
        var dst = Path.Combine(ConfigService.LocalFw, localDir, result.VersionRaw);

        // Копируем из РЕАЛЬНОЙ папки сборки, а не из записанного disk_path вслепую: точную папку могли
        // переименовать/перезалить под другой датой, а disk_path устареть после синхры — файлы тогда
        // лежат в соседней папке той же сборки (см. FirmwareDiskPresence.ResolveVersionDir). Без этого
        // копирование падало «папки нет», хотя прошивка на диске есть — ровно жалоба про залипшую
        // «локальная копия устарела, обновляем» и «папка …\version не найдена». Соседняя папка и папка
        // контроллера (для карт/инструкций) берутся от того же реального источника.
        var srcDir = FirmwareDiskPresence.ResolveVersionDir(result.FirmwareDir, result.VersionRaw) ?? result.FirmwareDir;

        FileSystemHelpers.CopyTree(srcDir, dst, overwrite: true);
        // ⚠️ ЗДЕСЬ БОЛЬШЕ НИЧЕГО НЕ УДАЛЯЕТСЯ. Раньше скачивание сносило остальные локальные версии
        // этой же прошивки (параметр cleanup и CleanupOldLocalVersions); с 2.2 этим занимается
        // настраиваемая уборка по расписанию — Настройки → Общие → «Локальные копии», см.
        // LocalCacheCleanup.
        //
        // Почему не «добавили настройку рядом с этим вызовом»: здесь уборка была жёстко привязана
        // к СОБЫТИЮ скачивания, то есть к моменту, когда человек как раз собирается работать.
        // Ровно отсюда и росла жалоба «диск отвалился и в фаиндере не найти прошивки»: скачал одну
        // версию — остальные по этому шкафу исчезли, причём именно в дорогу. Решение по объёму и
        // возрасту принимает фоновая уборка, которая от скачиваний не зависит вовсе. Обратно сюда
        // удаление не возвращать.

        var ctrlDir = Directory.GetParent(srcDir)?.FullName;
        if (ctrlDir is not null)
        {
            var ioMapSrc = Path.Combine(ctrlDir, "Карта ВВ");
            if (Directory.Exists(ioMapSrc))
                FileSystemHelpers.CopyFileOrFolderShallow(ioMapSrc, Path.Combine(ConfigService.LocalTemplates, "Карта ВВ", localDir));
            var instrSrc = Path.Combine(ctrlDir, "Инструкция");
            if (Directory.Exists(instrSrc))
                FileSystemHelpers.CopyFileOrFolderShallow(instrSrc, Path.Combine(ConfigService.LocalTemplates, "Инструкция", localDir));
            var modbusSrc = Path.Combine(ctrlDir, "Карта Modbus");
            if (Directory.Exists(modbusSrc))
                FileSystemHelpers.CopyFileOrFolderShallow(modbusSrc, Path.Combine(ConfigService.LocalTemplates, "Карта Modbus", localDir));
        }
        if (!string.IsNullOrEmpty(result.IoMapPath) && (File.Exists(result.IoMapPath) || Directory.Exists(result.IoMapPath)))
            FileSystemHelpers.CopyFileOrFolderShallow(result.IoMapPath, Path.Combine(ConfigService.LocalTemplates, "Карта ВВ", localDir));
        if (!string.IsNullOrEmpty(result.InstructionsPath) && (File.Exists(result.InstructionsPath) || Directory.Exists(result.InstructionsPath)))
            FileSystemHelpers.CopyFileOrFolderShallow(result.InstructionsPath, Path.Combine(ConfigService.LocalTemplates, "Инструкция", localDir));
        if (!string.IsNullOrEmpty(result.ModbusMapPath) && (File.Exists(result.ModbusMapPath) || Directory.Exists(result.ModbusMapPath)))
            FileSystemHelpers.CopyFileOrFolderShallow(result.ModbusMapPath, Path.Combine(ConfigService.LocalTemplates, "Карта Modbus", localDir));

        return dst;
    }
}
