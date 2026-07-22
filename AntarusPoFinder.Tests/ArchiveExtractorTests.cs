using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using AntarusPoFinder.Core.Infrastructure;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ArchiveExtractor had zero test coverage before this — used on every firmware/param-file
/// upload that lands as a .zip/.7z (see UploadView), so a regression here silently breaks uploads.
/// Covers the main extraction paths and, per the sprint's catch-block audit, the corrupted/malformed
/// archive path specifically: Extract() already returns (Ok:false, Message) instead of throwing/
/// swallowing (see its own catch(Exception e) block), these tests lock that contract in.</summary>
public class ArchiveExtractorTests
{
    private static void CreateZip(string zipPath, params (string EntryName, string Content)[] entries)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }

    [Fact]
    public void Extract_ValidZip_ExtractsAllFilesAndReturnsOk()
    {
        using var root = new TempRoot();
        var zipPath = Path.Combine(root.Path, "firmware.zip");
        CreateZip(zipPath, ("firmware.psl", "plc program"), ("readme.txt", "notes"));
        var dest = Path.Combine(root.Path, "extracted");

        var (ok, message) = ArchiveExtractor.Extract(zipPath, dest);

        Assert.True(ok);
        Assert.Equal("", message);
        Assert.Equal("plc program", File.ReadAllText(Path.Combine(dest, "firmware.psl")));
        Assert.Equal("notes", File.ReadAllText(Path.Combine(dest, "readme.txt")));
    }

    [Fact]
    public void Extract_ValidZip_WithNestedFolders_PreservesStructure()
    {
        using var root = new TempRoot();
        var zipPath = Path.Combine(root.Path, "hmi.zip");
        CreateZip(zipPath, ("project/screen1.fsprj", "screen data"), ("project/driver/driver.dll", "binary-ish"));
        var dest = Path.Combine(root.Path, "extracted");

        var (ok, _) = ArchiveExtractor.Extract(zipPath, dest);

        Assert.True(ok);
        Assert.True(File.Exists(Path.Combine(dest, "project", "screen1.fsprj")));
        Assert.True(File.Exists(Path.Combine(dest, "project", "driver", "driver.dll")));
    }

    [Fact]
    public void Extract_CorruptedZip_ReturnsFailureWithMessage_DoesNotThrow()
    {
        // Root scenario: a truncated/interrupted upload, or a file that just isn't actually a zip
        // despite the .zip extension. Extract() must come back with Ok=false and an explainable
        // message for the caller to surface (see UploadView), never throw past this method.
        using var root = new TempRoot();
        var brokenZipPath = Path.Combine(root.Path, "broken.zip");
        File.WriteAllBytes(brokenZipPath, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 }); // not a real zip
        var dest = Path.Combine(root.Path, "extracted");

        var (ok, message) = ArchiveExtractor.Extract(brokenZipPath, dest);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(message));
    }

    [Fact]
    public void Extract_CorruptedSevenZip_ReturnsFailureWithMessage_DoesNotThrow()
    {
        using var root = new TempRoot();
        var broken7zPath = Path.Combine(root.Path, "broken.7z");
        File.WriteAllBytes(broken7zPath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01 }); // not a real 7z
        var dest = Path.Combine(root.Path, "extracted");

        var (ok, message) = ArchiveExtractor.Extract(broken7zPath, dest);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(message));
    }

    [Fact]
    public void Extract_RarArchive_ReturnsFriendlyUnsupportedMessage()
    {
        using var root = new TempRoot();
        var rarPath = Path.Combine(root.Path, "legacy.rar");
        File.WriteAllBytes(rarPath, new byte[] { 0x52, 0x61, 0x72, 0x21 }); // "Rar!" magic, content irrelevant
        var dest = Path.Combine(root.Path, "extracted");

        var (ok, message) = ArchiveExtractor.Extract(rarPath, dest);

        Assert.False(ok);
        Assert.Contains("WinRAR", message);
    }

    [Fact]
    public void Extract_UnknownExtension_ReturnsFailureNamingTheExtension()
    {
        using var root = new TempRoot();
        var path = Path.Combine(root.Path, "file.tar.gz");
        File.WriteAllText(path, "irrelevant");
        var dest = Path.Combine(root.Path, "extracted");

        var (ok, message) = ArchiveExtractor.Extract(path, dest);

        Assert.False(ok);
        Assert.Contains(".gz", message);
    }

    [Fact]
    public void ExtractAllInDir_FindsNestedArchives_ExtractsAndDeletesOriginalByDefault()
    {
        using var root = new TempRoot();
        var subDir = Path.Combine(root.Path, "ПО", "НГР", "КНС");
        Directory.CreateDirectory(subDir);
        var zipPath = Path.Combine(subDir, "1.0.001.0001.20260101_0000.zip");
        CreateZip(zipPath, ("firmware.psl", "plc program"));

        var results = ArchiveExtractor.ExtractAllInDir(root.Path);

        Assert.Single(results);
        var extractedDir = results[0];
        Assert.True(File.Exists(Path.Combine(extractedDir, "firmware.psl")));
        Assert.False(File.Exists(zipPath)); // original archive deleted after successful extraction (keep: false)
    }

    [Fact]
    public void ExtractAllInDir_KeepTrue_LeavesOriginalArchiveInPlace()
    {
        using var root = new TempRoot();
        var zipPath = Path.Combine(root.Path, "params.zip");
        CreateZip(zipPath, ("params.dcfx", "device params"));

        var results = ArchiveExtractor.ExtractAllInDir(root.Path, keep: true);

        Assert.Single(results);
        Assert.True(File.Exists(zipPath));
    }

    [Fact]
    public void ExtractAllInDir_SkipsArchiveFolder_AndNonArchiveFiles()
    {
        using var root = new TempRoot();
        var archiveSubDir = Path.Combine(root.Path, "Архив");
        Directory.CreateDirectory(archiveSubDir);
        var zipInArchiveFolder = Path.Combine(archiveSubDir, "old.zip");
        CreateZip(zipInArchiveFolder, ("old.psl", "old firmware"));

        File.WriteAllText(Path.Combine(root.Path, "readme.txt"), "not an archive");

        var results = ArchiveExtractor.ExtractAllInDir(root.Path);

        Assert.Empty(results);
        Assert.True(File.Exists(zipInArchiveFolder)); // untouched — "Архив" is explicitly skipped
    }

    [Fact]
    public void ExtractAllInDir_OneCorruptedAmongValidArchives_StillExtractsTheValidOnes()
    {
        // A single corrupted archive in a batch upload must not stop the others from being
        // extracted — ExtractAllInDir just skips the failed one (Extract returns Ok=false) and
        // keeps walking, rather than aborting the whole directory scan.
        using var root = new TempRoot();
        var goodZip = Path.Combine(root.Path, "good.zip");
        CreateZip(goodZip, ("firmware.psl", "good firmware"));
        var badZip = Path.Combine(root.Path, "bad.zip");
        File.WriteAllBytes(badZip, new byte[] { 0x00, 0x01, 0x02 });

        var results = ArchiveExtractor.ExtractAllInDir(root.Path);

        Assert.Single(results);
        Assert.EndsWith("good", results[0]);
        // The corrupted archive is left in place (extraction failed, nothing to delete/keep).
        Assert.True(File.Exists(badZip));
    }
}
