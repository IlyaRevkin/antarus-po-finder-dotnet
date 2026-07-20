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
    public static string CopyToLocal(HierarchyResult result)
    {
        var localDir = LocalFirmwareCache.SanitizeName(result.Name);
        var dst = Path.Combine(ConfigService.LocalFw, localDir, result.VersionRaw);

        FileSystemHelpers.CopyTree(result.FirmwareDir, dst, overwrite: true);
        CleanupOldLocalVersions(localDir, result.VersionRaw);

        var ctrlDir = Directory.GetParent(result.FirmwareDir)?.FullName;
        if (ctrlDir is not null)
        {
            var ioMapSrc = Path.Combine(ctrlDir, "Карта ВВ");
            if (Directory.Exists(ioMapSrc))
                FileSystemHelpers.CopyFileOrFolderShallow(ioMapSrc, Path.Combine(ConfigService.LocalTemplates, "Карта ВВ", localDir));
            var instrSrc = Path.Combine(ctrlDir, "Инструкция");
            if (Directory.Exists(instrSrc))
                FileSystemHelpers.CopyFileOrFolderShallow(instrSrc, Path.Combine(ConfigService.LocalTemplates, "Инструкция", localDir));
        }
        if (!string.IsNullOrEmpty(result.IoMapPath) && (File.Exists(result.IoMapPath) || Directory.Exists(result.IoMapPath)))
            FileSystemHelpers.CopyFileOrFolderShallow(result.IoMapPath, Path.Combine(ConfigService.LocalTemplates, "Карта ВВ", localDir));
        if (!string.IsNullOrEmpty(result.InstructionsPath) && (File.Exists(result.InstructionsPath) || Directory.Exists(result.InstructionsPath)))
            FileSystemHelpers.CopyFileOrFolderShallow(result.InstructionsPath, Path.Combine(ConfigService.LocalTemplates, "Инструкция", localDir));

        return dst;
    }

    /// <summary>Removes locally cached version subfolders other than the one just downloaded, so the
    /// local cache doesn't accumulate superseded versions after an update.</summary>
    private static void CleanupOldLocalVersions(string localDir, string keepVersionRaw)
    {
        var baseDir = Path.Combine(ConfigService.LocalFw, localDir);
        if (!Directory.Exists(baseDir)) return;

        foreach (var sub in Directory.EnumerateDirectories(baseDir))
        {
            if (string.Equals(Path.GetFileName(sub), keepVersionRaw, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(sub, recursive: true); }
            catch { /* best effort — don't fail the sync over a stale cache folder */ }
        }
    }
}
