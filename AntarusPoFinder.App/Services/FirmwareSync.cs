using System;
using System.IO;
using AntarusPoFinder.Core.Infrastructure;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Services;

/// <summary>Copies a firmware version from the network disk into the naladchik's local cache —
/// shared by the manual "Скачать"/"Обновить" button in Search and the background firmware-update
/// scan/window (see MainWindowViewModel.CheckForFirmwareUpdates, FirmwareUpdatesWindow).</summary>
public static class FirmwareSync
{
    /// <param name="cleanup">Снести ли остальные локальные версии этой прошивки (кроме закреплённых).
    /// true — обычная авто-синхронизация из поиска (локально держим только актуальную). false —
    /// ручное «Скачать на этот ПК»/«Закрепить» из истории версий: там наладчик осознанно тянет
    /// КОНКРЕТНУЮ (часто старую) версию и не хочет, чтобы это стёрло уже лежащую под рукой текущую;
    /// не закреплённую разовую копию потом всё равно уберёт ближайшая авто-синхронизация.</param>
    public static string CopyToLocal(HierarchyResult result, bool cleanup = true)
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
        if (cleanup) CleanupOldLocalVersions(localDir, result.VersionRaw);

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

    /// <summary>Removes locally cached version subfolders other than the one just downloaded, so the
    /// local cache doesn't accumulate superseded versions after an update. Версии, помеченные
    /// «закреплено локально» (LocalFirmwareCache — метка .keep в папке версии), НЕ трогаются: наладчик
    /// сознательно оставил их под рукой, даже когда на сетевом диске они уже неактуальны/удалены (#12).</summary>
    private static void CleanupOldLocalVersions(string localDir, string keepVersionRaw)
    {
        var baseDir = Path.Combine(ConfigService.LocalFw, localDir);
        if (!Directory.Exists(baseDir)) return;

        foreach (var sub in Directory.EnumerateDirectories(baseDir))
        {
            if (string.Equals(Path.GetFileName(sub), keepVersionRaw, StringComparison.OrdinalIgnoreCase)) continue;
            if (LocalFirmwareCache.IsKeptDir(sub)) continue; // закреплённую версию уборка не удаляет
            try { Directory.Delete(sub, recursive: true); }
            catch { /* best effort — don't fail the sync over a stale cache folder */ }
        }
    }
}
