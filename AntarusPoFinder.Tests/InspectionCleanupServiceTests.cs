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
            var fiveDaysInMinutes = 5 * 24 * 60;

            var oldFile = Path.Combine(folder, "old.jpg");
            File.WriteAllText(oldFile, "x");
            File.SetLastWriteTime(oldFile, now.AddDays(-10));

            var newFile = Path.Combine(folder, "new.jpg");
            File.WriteAllText(newFile, "x");
            File.SetLastWriteTime(newFile, now.AddDays(-1));

            var exactlyAtThreshold = Path.Combine(folder, "boundary.jpg");
            File.WriteAllText(exactlyAtThreshold, "x");
            File.SetLastWriteTime(exactlyAtThreshold, now.AddDays(-5)); // == threshold, must survive (>= threshold kept)

            var result = InspectionCleanupService.Cleanup(folder, maxAgeMinutes: fiveDaysInMinutes, now);

            Assert.Equal(1, result.DeletedCount);
            Assert.Contains("old.jpg", result.DeletedNames);
            Assert.False(File.Exists(oldFile));
            Assert.True(File.Exists(newFile));
            Assert.True(File.Exists(exactlyAtThreshold));
            Assert.Empty(result.Errors);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    /// <summary>The whole point of round 34's days->minutes change: an age below one day (e.g. "2
    /// hours") must actually take effect, not round down to "0 days" / disabled.</summary>
    [Fact]
    public void Cleanup_SubDayThreshold_HoursGranularityWorks()
    {
        var folder = NewTempFolder();
        try
        {
            var now = new DateTime(2026, 7, 20, 12, 0, 0);

            var threeHoursOld = Path.Combine(folder, "old.jpg");
            File.WriteAllText(threeHoursOld, "x");
            File.SetLastWriteTime(threeHoursOld, now.AddHours(-3));

            var oneHourOld = Path.Combine(folder, "new.jpg");
            File.WriteAllText(oneHourOld, "x");
            File.SetLastWriteTime(oneHourOld, now.AddHours(-1));

            var result = InspectionCleanupService.Cleanup(folder, maxAgeMinutes: 2 * 60, now); // "2 hours"

            Assert.Equal(1, result.DeletedCount);
            Assert.Contains("old.jpg", result.DeletedNames);
            Assert.False(File.Exists(threeHoursOld));
            Assert.True(File.Exists(oneHourOld));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Cleanup_ZeroMinutes_IsNoOp_DeletesNothing()
    {
        var folder = NewTempFolder();
        try
        {
            var f = Path.Combine(folder, "ancient.jpg");
            File.WriteAllText(f, "x");
            File.SetLastWriteTime(f, DateTime.Now.AddYears(-5));

            var result = InspectionCleanupService.Cleanup(folder, maxAgeMinutes: 0, DateTime.Now);

            Assert.Equal(0, result.DeletedCount);
            Assert.True(File.Exists(f));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Cleanup_MissingFolder_ReturnsEmptyResult_DoesNotThrow()
    {
        var result = InspectionCleanupService.Cleanup(Path.Combine(Path.GetTempPath(), "antarus_does_not_exist_" + Guid.NewGuid()), 5 * 24 * 60, DateTime.Now);
        Assert.Equal(0, result.DeletedCount);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(0, "0 мин.")]
    [InlineData(45, "45 мин.")]
    [InlineData(120, "2 ч.")]
    [InlineData(130, "2 ч. 10 мин.")]
    [InlineData(1440, "1 дн.")]
    [InlineData(1440 + 60 + 5, "1 дн. 1 ч. 5 мин.")]
    public void FormatAge_RendersExpectedText(int minutes, string expected)
    {
        Assert.Equal(expected, InspectionCleanupService.FormatAge(minutes));
    }
}
