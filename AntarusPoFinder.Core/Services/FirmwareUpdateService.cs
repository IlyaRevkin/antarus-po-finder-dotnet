using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>One locally cached firmware that the server now has a newer version of.</summary>
public class FirmwareUpdateInfo
{
    public string Name { get; init; } = "";
    public string LocalDir { get; init; } = "";
    public string? CurrentLocalVersion { get; init; }
    public FwVersionRecord Latest { get; init; } = null!;
}

/// <summary>Scans the naladchik's locally cached firmware (ConfigService.LocalFw) against the latest
/// active server versions to find ones that have been superseded — feeds the startup "update
/// available" banner and its details window (see MainWindowViewModel, FirmwareUpdatesWindow).</summary>
public static class FirmwareUpdateService
{
    public static List<FirmwareUpdateInfo> GetAvailableUpdates(Database db)
    {
        var result = new List<FirmwareUpdateInfo>();
        foreach (var row in db.GetLatestActiveFwVersions())
        {
            var name = ($"{(!string.IsNullOrEmpty(row.SubtypeFolder) ? row.SubtypeFolder : row.SubtypeName)} {row.CtrlName}").Trim();
            var baseDir = LocalFirmwareCache.DirFor(name);
            if (!Directory.Exists(baseDir)) continue; // never downloaded — not an "update", just an available sync
            if (LocalFirmwareCache.HasVersion(name, row.VersionRaw)) continue; // already current

            var localVersions = Directory.EnumerateDirectories(baseDir)
                .Select(Path.GetFileName)
                .Where(v => !string.IsNullOrEmpty(v))
                .OrderByDescending(v => v)
                .ToList();
            if (localVersions.Count == 0) continue; // cache dir exists but empty — nothing to "update"

            result.Add(new FirmwareUpdateInfo
            {
                Name = name,
                LocalDir = LocalFirmwareCache.SanitizeName(name),
                CurrentLocalVersion = localVersions[0],
                Latest = row,
            });
        }
        return result;
    }
}
