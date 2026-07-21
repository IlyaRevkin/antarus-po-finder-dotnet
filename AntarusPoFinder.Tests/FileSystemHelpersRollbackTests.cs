using System.IO;
using AntarusPoFinder.Core.Infrastructure;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Covers FileSystemHelpers.MarkRolledBackOnDisk — renames a rolled-back version's
/// firmware/HMI path in place so a later upload reusing the freed-up version number doesn't land on
/// (and silently merge into or overwrite) the same path. See Database.RollbackFwVersion, which calls
/// this for both DiskPath and HmiPath.</summary>
public class FileSystemHelpersRollbackTests
{
    private static string NewTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"antarus_rollback_test_{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void MarkRolledBackOnDisk_File_AppendsMarkerBeforeExtension()
    {
        var root = NewTempRoot();
        try
        {
            var file = Path.Combine(root, "1.2.3.psl");
            File.WriteAllText(file, "data");

            var result = FileSystemHelpers.MarkRolledBackOnDisk(file);

            Assert.Equal(Path.Combine(root, "1.2.3_ОТКАТАНО.psl"), result);
            Assert.True(File.Exists(result));
            Assert.False(File.Exists(file));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MarkRolledBackOnDisk_Directory_AppendsMarkerSuffix()
    {
        var root = NewTempRoot();
        try
        {
            var dir = Path.Combine(root, "1.2.3_hmi");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "project.fsprj"), "data");

            var result = FileSystemHelpers.MarkRolledBackOnDisk(dir);

            Assert.Equal(Path.Combine(root, "1.2.3_hmi_ОТКАТАНО"), result);
            Assert.True(Directory.Exists(result));
            Assert.True(File.Exists(Path.Combine(result, "project.fsprj")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MarkRolledBackOnDisk_CollidingName_AppendsNumericSuffix()
    {
        var root = NewTempRoot();
        try
        {
            var file = Path.Combine(root, "1.2.3.psl");
            File.WriteAllText(file, "second rollback");
            // Simulate a version that was already rolled back once before.
            File.WriteAllText(Path.Combine(root, "1.2.3_ОТКАТАНО.psl"), "first rollback");

            var result = FileSystemHelpers.MarkRolledBackOnDisk(file);

            Assert.Equal(Path.Combine(root, "1.2.3_ОТКАТАНО_2.psl"), result);
            Assert.True(File.Exists(result));
            // The earlier rollback's file must survive untouched, not get overwritten.
            Assert.Equal("first rollback", File.ReadAllText(Path.Combine(root, "1.2.3_ОТКАТАНО.psl")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MarkRolledBackOnDisk_NonExistentPath_ReturnsUnchanged()
    {
        var missing = Path.Combine(NewTempRoot(), "does-not-exist.psl");

        var result = FileSystemHelpers.MarkRolledBackOnDisk(missing);

        Assert.Equal(missing, result);
    }

    [Fact]
    public void MarkRolledBackOnDisk_EmptyPath_ReturnsUnchanged()
    {
        Assert.Equal("", FileSystemHelpers.MarkRolledBackOnDisk(""));
    }
}
