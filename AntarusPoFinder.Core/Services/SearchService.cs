using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>A single search hit, replacing the Python app's duck-typed _HierarchyRule/_HierarchyVersion.</summary>
public class HierarchyResult
{
    public int SubtypeId { get; init; }
    public int ControllerId { get; init; }
    public string Name { get; init; } = "";
    public string Controller { get; init; } = "";
    public string EquipmentType { get; init; } = "";
    public string WorkType { get; init; } = "";
    public string IoMapPath { get; init; } = "";
    public string InstructionsPath { get; init; } = "";

    /// <summary>Absolute path to the version folder on the network disk.</summary>
    public string FirmwareDir { get; init; } = "";

    public string VersionRaw { get; init; } = "";
    public string Description { get; init; } = "";
    public string Tags { get; init; } = "";
    public DateTime? UploadDate { get; init; }
    public int Score { get; init; }
    public int FwVersionId { get; init; }
}

public static class SearchService
{
    private static readonly Regex Separators = new(@"[,;\-/\\]+", RegexOptions.Compiled);

    public static string Normalize(string q)
    {
        var collapsed = Separators.Replace(q, " ");
        return Regex.Replace(collapsed, @"\s+", " ").Trim().ToUpperInvariant();
    }

    public static List<HierarchyResult> Search(Database db, string query, bool exactWord = false)
    {
        var normalized = Normalize(query);
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return new();

        var rows = db.SearchFwVersionsByTokens(tokens, exactWord);

        return rows.Select((row, idx) => ToHierarchyResult(row, rows.Count - idx)).ToList();
    }

    /// <summary>Maps a joined fw_versions row (group/subtype/controller names already populated by the
    /// caller's query) to a HierarchyResult — the same shape Search() returns, reused by the firmware-
    /// update scan so it can hand rows straight to FirmwareSync.CopyToLocal.</summary>
    public static HierarchyResult ToHierarchyResult(FwVersionRecord row, int score = 0)
    {
        var sub = !string.IsNullOrEmpty(row.SubtypeFolder) ? row.SubtypeFolder : row.SubtypeName;
        var name = $"{sub} {row.CtrlName}".Trim();

        DateTime? uploadDate = null;
        if (DateTime.TryParse(row.UploadDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            uploadDate = d;

        return new HierarchyResult
        {
            SubtypeId = row.SubtypeId,
            ControllerId = row.ControllerId,
            FwVersionId = row.Id ?? 0,
            Name = name,
            Controller = row.CtrlName,
            EquipmentType = row.GroupName,
            WorkType = string.Join(", ", row.LaunchTypes),
            IoMapPath = row.IoMapPath,
            InstructionsPath = row.InstructionsPath,
            FirmwareDir = row.DiskPath,
            VersionRaw = row.VersionRaw,
            Description = row.Description,
            Tags = row.Tags,
            UploadDate = uploadDate,
            Score = score,
        };
    }
}
