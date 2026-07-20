using System;
using System.IO;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

public class InspectionCleanupServiceTests
{
    private static string NewTempFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"antarus_cleanup_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Cleanup_DeletesOnlyFilesOlderThanThreshold()
    {
        var folder = NewTempFolder();
        try
        {
            var now = new DateTime(2026, 7, 20, 12, 0, 0);

            var oldFile = Path.Combine(folder, "old.jpg");
            File.WriteAllText(oldFile, "x");
            File.SetLastWriteTime(oldFile, now.AddDays(-10));

            var newFile = Path.Combine(folder, "new.jpg");
            File.WriteAllText(newFile, "x");
            File.SetLastWriteTime(newFile, now.AddDays(-1));

            var exactlyAtThreshold = Path.Combine(folder, "boundary.jpg");
            File.WriteAllText(exactlyAtThreshold, "x");
            File.SetLastWriteTime(exactlyAtThreshold, now.AddDays(-5)); // == threshold, must survive (>= threshold kept)

            var result = InspectionCleanupService.Cleanup(folder, maxAgeDays: 5, now);

            Assert.Equal(1, result.DeletedCount);
            Assert.Contains("old.jpg", result.DeletedNames);
            Assert.False(File.Exists(oldFile));
            Assert.True(File.Exists(newFile));
            Assert.True(File.Exists(exactlyAtThreshold));
            Assert.Empty(result.Errors);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Cleanup_ZeroDays_IsNoOp_DeletesNothing()
    {
        var folder = NewTempFolder();
        try
        {
            var f = Path.Combine(folder, "ancient.jpg");
            File.WriteAllText(f, "x");
            File.SetLastWriteTime(f, DateTime.Now.AddYears(-5));

            var result = InspectionCleanupService.Cleanup(folder, maxAgeDays: 0, DateTime.Now);

            Assert.Equal(0, result.DeletedCount);
            Assert.True(File.Exists(f));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Cleanup_MissingFolder_ReturnsEmptyResult_DoesNotThrow()
    {
        var result = InspectionCleanupService.Cleanup(Path.Combine(Path.GetTempPath(), "antarus_does_not_exist_" + Guid.NewGuid()), 5, DateTime.Now);
        Assert.Equal(0, result.DeletedCount);
        Assert.Empty(result.Errors);
    }
}
