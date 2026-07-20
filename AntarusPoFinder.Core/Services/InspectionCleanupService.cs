using System;
using System.Collections.Generic;
using System.IO;

namespace AntarusPoFinder.Core.Services;

public record InspectionCleanupResult(int DeletedCount, List<string> DeletedNames, List<string> Errors);

/// <summary>Deletes files from the Осмотр folder older than a configured age — see
/// ConfigService.InspectionAutoCleanupDays (0 = disabled). Pulled out as a plain, folder-in/result-out
/// function (no ConfigService/AppServices dependency) specifically so it's unit-testable from
/// AntarusPoFinder.Tests, which only references Core — the caller (MainWindowViewModel.RunSync)
/// is what actually reads the setting and decides whether/when to call this.</summary>
public static class InspectionCleanupService
{
    /// <summary>Deletes every file directly in <paramref name="folder"/> (non-recursive — matches
    /// how the folder is actually used, flat, see InspectionView) whose last-write time is older
    /// than <paramref name="maxAgeDays"/> days relative to <paramref name="now"/>. Best-effort per
    /// file: one file failing to delete (locked, permissions) doesn't stop the rest. A
    /// <paramref name="maxAgeDays"/> of 0 or less, or a missing/empty folder path, is a no-op —
    /// callers should still gate on ConfigService.InspectionAutoCleanupDays() themselves, this is
    /// just an extra safety net against ever wiping a folder with an accidentally-empty path.</summary>
    public static InspectionCleanupResult Cleanup(string folder, int maxAgeDays, DateTime now)
    {
        var deletedNames = new List<string>();
        var errors = new List<string>();

        if (maxAgeDays <= 0 || string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return new InspectionCleanupResult(0, deletedNames, errors);

        var threshold = now - TimeSpan.FromDays(maxAgeDays);
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            DateTime lastWrite;
            try { lastWrite = File.GetLastWriteTime(file); }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(file)}: {ex.Message}"); continue; }

            if (lastWrite >= threshold) continue;

            try
            {
                File.Delete(file);
                deletedNames.Add(Path.GetFileName(file));
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return new InspectionCleanupResult(deletedNames.Count, deletedNames, errors);
    }
}
