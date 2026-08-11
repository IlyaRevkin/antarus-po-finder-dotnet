using System.Collections.Generic;
using System.IO;

namespace AntarusPoFinder.Core.Services;

public record InspectionCleanupResult(int DeletedCount, List<string> DeletedNames, List<string> Errors);

/// <summary>Deletes files from the Осмотр folder older than a configured age — see
/// ConfigService.InspectionAutoCleanupMinutes (0 = disabled). Pulled out as a plain, folder-in/
/// result-out function (no ConfigService/AppServices dependency) specifically so it's unit-testable
/// from AntarusPoFinder.Tests, which only references Core — the caller (MainWindowViewModel.RunSync)
/// is what actually reads the setting and decides whether/when to call this.
///
/// Round 34: the age unit was widened from whole days to minutes (see ConfigService for the
/// days->minutes migration on upgrade) so the operator can configure e.g. "2 hours" — days alone
/// couldn't express that. Callers building the UI value from separate days/hours/minutes inputs
/// should combine them into one total-minutes int before calling this.</summary>
public static class InspectionCleanupService
{
    /// <summary>Deletes every file directly in <paramref name="folder"/> (non-recursive — matches
    /// how the folder is actually used, flat, see InspectionView) whose last-write time is older
    /// than <paramref name="maxAgeMinutes"/> minutes relative to <paramref name="now"/>. Best-effort
    /// per file: one file failing to delete (locked, permissions) doesn't stop the rest. A
    /// <paramref name="maxAgeMinutes"/> of 0 or less, or a missing/empty folder path, is a no-op —
    /// callers should still gate on ConfigService.InspectionAutoCleanupMinutes() themselves, this is
    /// just an extra safety net against ever wiping a folder with an accidentally-empty path.</summary>
    /// <param name="ledger">Журнал «когда файл впервые увидели в папке»
    /// (<see cref="InspectionSeenLedger"/>). Без него возраст считался по дате изменения файла, и
    /// только что скопированный снаружи файл со старой датой сносило первым же тиком — жалоба «закинул
    /// файл, он и 10 минут не пролежал, а его уже почистили». null — прежнее поведение (только дата
    /// изменения); так зовут тесты, проверяющие саму пороговую логику.</param>
    public static InspectionCleanupResult Cleanup(string folder, int maxAgeMinutes, DateTime now,
        InspectionSeenLedger? ledger = null)
    {
        var deletedNames = new List<string>();
        var errors = new List<string>();

        if (maxAgeMinutes <= 0 || string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return new InspectionCleanupResult(0, deletedNames, errors);

        var threshold = now - TimeSpan.FromMinutes(maxAgeMinutes);
        var seenNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(folder))
        {
            seenNow.Add(file);

            DateTime age;
            try
            {
                // Позднейшее из «когда впервые увидели здесь» и «когда файл менялся»: первое защищает
                // принесённый извне файл со старой датой, второе — файл, который переписали на месте
                // (он снова свежий, и это правильно).
                age = File.GetLastWriteTime(file);
                if (ledger is not null)
                {
                    var firstSeen = ledger.FirstSeen(file, now);
                    if (firstSeen > age) age = firstSeen;
                }
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(file)}: {ex.Message}"); continue; }

            if (age >= threshold) continue;

            try
            {
                File.Delete(file);
                deletedNames.Add(Path.GetFileName(file));
                ledger?.Forget(file);
                seenNow.Remove(file);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (ledger is not null)
        {
            ledger.Prune(seenNow);
            ledger.Save();
        }
        return new InspectionCleanupResult(deletedNames.Count, deletedNames, errors);
    }

    /// <summary>Human-readable "N дн. N ч. N мин." rendering of a total-minutes age, dropping any
    /// zero components except when the whole thing is zero (then just "0 мин.") — used by both the
    /// settings UI (InspectionView) and the background-tick status toast (MainWindowViewModel) so
    /// they describe the same configured age the same way.</summary>
    public static string FormatAge(int totalMinutes)
    {
        if (totalMinutes <= 0) return "0 мин.";

        var days = totalMinutes / 1440;
        var hours = totalMinutes % 1440 / 60;
        var minutes = totalMinutes % 60;

        var parts = new List<string>();
        if (days > 0) parts.Add($"{days} дн.");
        if (hours > 0) parts.Add($"{hours} ч.");
        if (minutes > 0) parts.Add($"{minutes} мин.");
        return string.Join(" ", parts);
    }
}
