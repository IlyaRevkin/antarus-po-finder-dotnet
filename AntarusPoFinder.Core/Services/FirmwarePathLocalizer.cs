using System;
using System.IO;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Rewrites a firmware/parameter path that was stored on ANOTHER machine so it points at the
/// SAME share as seen from THIS machine — the fix for "у коллеги диск смонтирован как Z:\Software, а у
/// меня как \\ant_srv\Software, и прошивка/обновление не находятся".
///
/// Why the stored path can't be trusted verbatim: fw_versions.disk_path (and io_map/instructions/hmi/
/// modbus paths, and param_files.disk_path) are stored ABSOLUTE, with the uploading machine's whole
/// root_path prefix baked in (see HierarchyService.PoCtrlFolder / FwPath). root_path is per-machine and
/// excluded from config sync, and the same physical share is routinely mounted under different local
/// forms — a mapped drive letter on one machine, a UNC path on another. So a path saved as
/// "\\ant_srv\Software\Antarus Finder\ПО\..." is meaningless on a machine that only knows the share as
/// "Z:\Software\Antarus Finder\...". The config-sync prefix remap (Database.RemapFwPaths) is a literal
/// string-prefix swap with no UNC↔mapped-drive awareness and only fires when the peer stamped a
/// matching source_root_path, so it silently misses this exact case.
///
/// The trick: every firmware/parameter path lives under a well-known, machine-independent anchor folder
/// that HierarchyService always inserts directly under the root — "ПО" for firmware, "Параметры" for
/// parameter files. The tail from that anchor onward (ПО\&lt;группа&gt;\&lt;подтип&gt;\&lt;контроллер&gt;\&lt;версия&gt;) is
/// identical on every machine because the hierarchy itself is synced. So we drop whatever root prefix
/// the foreign path carried and re-root the tail on THIS machine's root_path.</summary>
public static class FirmwarePathLocalizer
{
    private static readonly char[] Separators = { '\\', '/' };

    // Keep in sync with HierarchyService.FolderPo / FolderParams — the two top-level folders the
    // hierarchy builder puts directly under root_path. Everything the app opens/downloads sits under
    // one of them, so anchoring on either re-roots the whole firmware and parameters tree.
    private static readonly string[] AnchorFolders = { "ПО", "Параметры" };

    /// <summary>Returns <paramref name="storedPath"/> re-rooted onto <paramref name="localRoot"/> by
    /// anchoring on the first "ПО"/"Параметры" segment. Returns the path UNCHANGED when it can't be
    /// localized — an empty stored path or local root, or a path that has no anchor segment (so we
    /// never fabricate a bogus path; a non-hierarchy path is left exactly as-is). Idempotent for the
    /// same-machine case: a path that already sits under <paramref name="localRoot"/> comes back
    /// byte-identical.</summary>
    public static string Localize(string storedPath, string localRoot)
    {
        if (string.IsNullOrWhiteSpace(storedPath) || string.IsNullOrWhiteSpace(localRoot))
            return storedPath;

        var segments = storedPath.Split(Separators, StringSplitOptions.None);
        var anchor = Array.FindIndex(segments, s =>
            AnchorFolders.Any(a => string.Equals(s, a, StringComparison.OrdinalIgnoreCase)));
        if (anchor < 0) return storedPath; // not a hierarchy path — leave it alone

        var tail = segments[anchor..].Where(s => s.Length > 0).ToArray();
        var parts = new string[tail.Length + 1];
        parts[0] = localRoot;
        Array.Copy(tail, 0, parts, 1, tail.Length);
        return Path.Combine(parts);
    }
}
