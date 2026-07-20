using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using SharpCompress.Archives;
using SharpCompress.Common;
using AntarusPoFinder.Core.Infrastructure;

namespace AntarusPoFinder.Core.Infrastructure;

/// <summary>Archive extraction (zip/7z; .rar deliberately unsupported, same as the Python app —
/// RAR extraction was never implemented there either). 1:1 port of app/infrastructure/archive.py.</summary>
public static class ArchiveExtractor
{
    public static (bool Ok, string Message) Extract(string src, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var ext = Path.GetExtension(src).ToLowerInvariant();

        try
        {
            switch (ext)
            {
                case ".zip":
                    ZipFile.ExtractToDirectory(src, destDir, overwriteFiles: true);
                    return (true, "");

                case ".7z":
                    ArchiveFactory.WriteToDirectory(src, destDir, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
                    return (true, "");

                case ".rar":
                    return (false, ".rar требует WinRAR — скопируйте вручную");

                default:
                    return (false, $"Неизвестный формат: {ext}");
            }
        }
        catch (Exception e)
        {
            var msg = e.Message;
            if (msg.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
                return (false, "Архив защищён паролем");
            return (false, msg);
        }
    }

    /// <summary>Recursively finds every zip/7z/rar under dirpath (skipping "Архив"/"__pycache__"
    /// subfolders), extracts each into a sibling folder named after its stem, and — unless keep
    /// is true — deletes the original archive after a successful extraction.</summary>
    public static List<string> ExtractAllInDir(string dirPath, bool keep = false)
    {
        var results = new List<string>();
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Архив", "__pycache__" };

        void Walk(string dir)
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (skip.Contains(Path.GetFileName(sub))) continue;
                Walk(sub);
            }
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (!FileSystemHelpers.ArchiveExts.Contains(Path.GetExtension(file))) continue;
                var dest = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file));
                var (ok, _) = Extract(file, dest);
                if (!ok) continue;
                results.Add(dest);
                if (!keep)
                {
                    try { File.Delete(file); } catch { /* best effort */ }
                }
            }
        }

        if (Directory.Exists(dirPath)) Walk(dirPath);
        return results;
    }
}
