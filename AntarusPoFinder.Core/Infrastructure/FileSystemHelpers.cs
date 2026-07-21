using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AntarusPoFinder.Core.Infrastructure;

/// <summary>Pure filesystem utilities, no UI dependency. 1:1 port of app/infrastructure/filesystem.py.</summary>
public static class FileSystemHelpers
{
    public static readonly HashSet<string> ArchiveExts = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar" };
    public static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "Архив", "Старая структура", "__pycache__", "build", "dist", "_archive" };

    public record DiskSnapshot(DateTime Mtime, int FileCount);

    public static DiskSnapshot GetDiskSnapshot(string path)
    {
        if (!Directory.Exists(path)) return new DiskSnapshot(DateTime.MinValue, 0);
        var mtime = Directory.GetLastWriteTime(path);
        var count = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count();
        return new DiskSnapshot(mtime, count);
    }

    /// <summary>Deletes a directory tree, clearing read-only attributes first (common on Windows
    /// for files copied from read-only sources) so the delete doesn't fail partway through.</summary>
    public static void RmtreeSafe(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); }
            catch { /* best effort */ }
        }
        Directory.Delete(path, recursive: true);
    }

    /// <summary>Copies src into dst. If overwrite is true and dst exists, it is deleted first
    /// (matches the Python app's copy_tree semantics: overwrite = replace entirely, not merge).</summary>
    public static void CopyTree(string src, string dst, bool overwrite = true)
    {
        if (overwrite && Directory.Exists(dst))
            RmtreeSafe(dst);

        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, dir);
            Directory.CreateDirectory(Path.Combine(dst, rel));
        }
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var destFile = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(file, destFile, overwrite: true);
            File.SetLastWriteTime(destFile, File.GetLastWriteTime(file));
        }
    }

    public static string CopyFile(string src, string dstDir)
    {
        Directory.CreateDirectory(dstDir);
        var dst = Path.Combine(dstDir, Path.GetFileName(src));
        File.Copy(src, dst, overwrite: true);
        return dst;
    }

    /// <summary>Copies a single file into dstFolder, or (for a folder) copies its direct file
    /// children only — non-recursive, guards against copying a path onto itself. Used for the
    /// io-map/instructions "attachment" fields in Upload and Search, which are one file or one
    /// flat folder of files, never a nested tree. Returns the destination file path (file) or
    /// dstFolder (folder), or "" if src doesn't exist.</summary>
    public static string CopyFileOrFolderShallow(string src, string dstFolder)
    {
        Directory.CreateDirectory(dstFolder);
        if (File.Exists(src))
        {
            var dest = Path.Combine(dstFolder, Path.GetFileName(src));
            if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                File.Copy(src, dest, overwrite: true);
            return dest;
        }
        if (Directory.Exists(src))
        {
            if (!string.Equals(Path.GetFullPath(src).TrimEnd('\\'), Path.GetFullPath(dstFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                foreach (var file in Directory.EnumerateFiles(src))
                    File.Copy(file, Path.Combine(dstFolder, Path.GetFileName(file)), overwrite: true);
            }
            return dstFolder;
        }
        return "";
    }

    /// <summary>Moves every file in dest_dir matching *extension into dest_dir/_archive/, timestamped
    /// with today's date (e.g. report.pdf → report_bak20260714.pdf). No-op if extension is empty.</summary>
    public static void ArchiveOldFiles(string destDir, string extension)
    {
        if (string.IsNullOrEmpty(extension)) return;
        var archiveDir = Path.Combine(destDir, "_archive");
        Directory.CreateDirectory(archiveDir);
        var today = DateTime.Now.ToString("yyyyMMdd");

        if (!Directory.Exists(destDir)) return;
        foreach (var file in Directory.EnumerateFiles(destDir, $"*{extension}", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                var suffix = Path.GetExtension(file);
                var newName = $"{stem}_bak{today}{suffix}";
                File.Move(file, Path.Combine(archiveDir, newName), overwrite: true);
            }
            catch
            {
                // best effort, matches the Python app's silent-skip behavior
            }
        }
    }

    /// <summary>Renames a rolled-back version's file/folder in place, appending a marker suffix —
    /// otherwise a future upload that reuses the version number it just freed up would land on the
    /// exact same path (CopyDirectoryContents/File.Copy overwrite=true) and silently merge into or
    /// clobber whatever the rolled-back version left behind. Returns the new path, or the original
    /// path unchanged if nothing exists there (e.g. no HMI project was ever attached).</summary>
    public static string MarkRolledBackOnDisk(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var isDir = Directory.Exists(path);
        if (!isDir && !File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path) ?? "";
        var ext = isDir ? "" : Path.GetExtension(path);
        var stem = isDir ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) : Path.GetFileNameWithoutExtension(path);

        var candidate = Path.Combine(dir, $"{stem}_ОТКАТАНО{ext}");
        int suffix = 1;
        while (Directory.Exists(candidate) || File.Exists(candidate))
            candidate = Path.Combine(dir, $"{stem}_ОТКАТАНО_{++suffix}{ext}");

        if (isDir) Directory.Move(path, candidate);
        else File.Move(path, candidate);
        return candidate;
    }

    /// <summary>Clears the ReadOnly attribute (if set) so the app itself can rewrite a file it
    /// previously protected with <see cref="ProtectFromExternalEdits"/> — see that method's doc.
    /// Best-effort: some network shares/WebDAV mounts don't support attributes at all, which must
    /// never block the app's own write.</summary>
    public static void UnprotectForOwnWrite(string path)
    {
        try { if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly); }
        catch { /* best effort — see doc */ }
    }

    /// <summary>Sets the ReadOnly attribute on a file the app just wrote, as a speed bump against a
    /// colleague deleting/hand-editing it straight from Explorer/Notepad — asked for alongside
    /// ConfigFileCrypto for the shared network config file. This is NOT a real access-control
    /// boundary (a network share's NTFS ACLs are per-Windows-account, not per-application — there is
    /// no OS mechanism to say "only this .exe may touch this file" on a plain file share/WebDAV
    /// mount), and a determined user can always clear the attribute themselves. Best-effort, like
    /// UnprotectForOwnWrite above.</summary>
    public static void ProtectFromExternalEdits(string path)
    {
        try { if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly); }
        catch { /* best effort — see doc */ }
    }

    /// <summary>Numeric major.minor(.YYMMDD) subfolder scan — returns the path with the highest
    /// (major, minor, date) key, or null if firmwareDir doesn't exist or has no matches.</summary>
    public static string? FindLatestVersionFolder(string firmwareDir)
    {
        if (!Directory.Exists(firmwareDir)) return null;

        string? best = null;
        (int, int, int) bestKey = (-1, -1, -1);

        foreach (var dir in Directory.EnumerateDirectories(firmwareDir))
        {
            var name = Path.GetFileName(dir);
            var m = System.Text.RegularExpressions.Regex.Match(name, @"^(\d+)\.(\d+)(?:\.(\d{6}))?$");
            if (!m.Success) continue;

            var key = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
            if (best is null || key.CompareTo(bestKey) > 0)
            {
                best = dir;
                bestKey = key;
            }
        }
        return best;
    }
}
