using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Уборка локальных копий прошивок. Тикет прямым текстом отменил отказ от локального
/// хранения: «была жалоба о том что диск отвалился и в фаиндере не найти прошивки, так что
/// функционал что у нас создаются локальные копии всех прошивок оставь, просто добавим настройку
/// частоту очистки». Поэтому главное, что здесь проверяется, — чего уборка НЕ уносит: без этого
/// настройка «чистить почаще» ровно воспроизводит ту самую жалобу.</summary>
public class LocalCacheCleanupTests
{
    private sealed class TempCache : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "antarus-cache-" + Guid.NewGuid().ToString("N"));

        public TempCache() => Directory.CreateDirectory(Path);

        /// <summary>Кладёт копию версии: папка с одним файлом нужного размера и нужного возраста.</summary>
        public string Add(string firmware, string version, int ageDays, int sizeKb = 1, bool kept = false)
        {
            var dir = System.IO.Path.Combine(Path, firmware, version);
            Directory.CreateDirectory(dir);
            var file = System.IO.Path.Combine(dir, "firmware.psl");
            File.WriteAllBytes(file, new byte[sizeKb * 1024]);

            var at = DateTime.Now.AddDays(-ageDays);
            File.SetLastWriteTime(file, at);
            File.SetLastAccessTime(file, at);

            if (kept) File.WriteAllText(System.IO.Path.Combine(dir, LocalFirmwareCache.KeepMarkerName), "");
            return dir;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (Exception) { }
        }
    }

    private static LocalCacheCleanup.Policy ByAge(int keepDays) =>
        new(LocalCacheCleanup.Mode.Monthly, keepDays, 0);

    [Fact]
    public void Build_NeverMode_PlansNothing()
    {
        using var cache = new TempCache();
        cache.Add("НГР КНС SMH5", "1.0", ageDays: 1000);

        var plan = LocalCacheCleanup.Build(cache.Path,
            new LocalCacheCleanup.Policy(LocalCacheCleanup.Mode.Never, 1, 1), DateTime.Now);

        Assert.True(plan.Empty);
    }

    [Fact]
    public void Build_NewestCopyOfFirmware_NeverDeleted()
    {
        using var cache = new TempCache();
        // Обе копии древние, обе под правило по возрасту — но самая свежая из них это и есть «чем
        // работать без диска», и унести её нельзя ни при каких настройках.
        cache.Add("НГР КНС SMH5", "1.0", ageDays: 400);
        var newest = cache.Add("НГР КНС SMH5", "2.0", ageDays: 300);

        var plan = LocalCacheCleanup.Build(cache.Path, ByAge(30), DateTime.Now);

        Assert.DoesNotContain(newest, plan.Delete.Select(c => c.Path));
        Assert.Single(plan.Delete);
    }

    [Fact]
    public void Build_SingleCopyFirmware_NeverDeleted()
    {
        using var cache = new TempCache();
        cache.Add("НГР КНС SMH5", "1.0", ageDays: 1000);

        var plan = LocalCacheCleanup.Build(cache.Path, ByAge(1), DateTime.Now);

        Assert.True(plan.Empty);
    }

    [Fact]
    public void Build_KeptCopy_NeverDeleted()
    {
        using var cache = new TempCache();
        cache.Add("НГР КНС SMH5", "3.0", ageDays: 1);                    // свежая — защищена сама собой
        var kept = cache.Add("НГР КНС SMH5", "1.0", ageDays: 900, kept: true);
        var plain = cache.Add("НГР КНС SMH5", "2.0", ageDays: 900);

        var plan = LocalCacheCleanup.Build(cache.Path, ByAge(30), DateTime.Now);

        Assert.DoesNotContain(kept, plan.Delete.Select(c => c.Path));
        Assert.Contains(plain, plan.Delete.Select(c => c.Path));
    }

    [Fact]
    public void Build_YoungCopy_NotDeletedByAge()
    {
        using var cache = new TempCache();
        cache.Add("НГР КНС SMH5", "3.0", ageDays: 0);
        var young = cache.Add("НГР КНС SMH5", "2.0", ageDays: 10);

        var plan = LocalCacheCleanup.Build(cache.Path, ByAge(90), DateTime.Now);

        Assert.DoesNotContain(young, plan.Delete.Select(c => c.Path));
    }

    [Fact]
    public void Build_KeepDaysZero_AgeRuleOff()
    {
        using var cache = new TempCache();
        cache.Add("НГР КНС SMH5", "3.0", ageDays: 0);
        cache.Add("НГР КНС SMH5", "2.0", ageDays: 5000);

        var plan = LocalCacheCleanup.Build(cache.Path, ByAge(0), DateTime.Now);

        Assert.True(plan.Empty);
    }

    [Fact]
    public void Build_SizeCap_RemovesLeastRecentlyUsedUntilUnderCap()
    {
        using var cache = new TempCache();
        // Потолок 0 ГБ не ставим (это «не чистить по объёму»), поэтому берём 1 ГБ и кладём больше.
        cache.Add("НГР КНС SMH5", "4.0", ageDays: 1, sizeKb: 400 * 1024);   // свежая — защищена
        var old1 = cache.Add("НГР КНС SMH5", "1.0", ageDays: 300, sizeKb: 400 * 1024);
        var old2 = cache.Add("НГР КНС SMH5", "2.0", ageDays: 200, sizeKb: 400 * 1024);

        // Возрастное правило выключено — проверяем ровно потолок объёма.
        var plan = LocalCacheCleanup.Build(cache.Path,
            new LocalCacheCleanup.Policy(LocalCacheCleanup.Mode.Monthly, 0, 1), DateTime.Now);

        // 1.2 ГБ при потолке 1 ГБ — уходит самая давняя, и ровно одна: после неё уже уложились.
        Assert.Contains(old1, plan.Delete.Select(c => c.Path));
        Assert.DoesNotContain(old2, plan.Delete.Select(c => c.Path));
    }

    [Fact]
    public void Apply_RemovesPlannedFoldersOnly()
    {
        using var cache = new TempCache();
        var keep = cache.Add("НГР КНС SMH5", "3.0", ageDays: 0);
        var drop = cache.Add("НГР КНС SMH5", "1.0", ageDays: 900);

        var removed = LocalCacheCleanup.Apply(LocalCacheCleanup.Build(cache.Path, ByAge(30), DateTime.Now));

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(drop));
        Assert.True(Directory.Exists(keep));
    }

    [Fact]
    public void IsDue_NeverRunBefore_IsDue() =>
        Assert.True(LocalCacheCleanup.IsDue(ByAge(90), "", DateTime.Now));

    [Fact]
    public void IsDue_RanYesterdayMonthly_NotDue() =>
        Assert.False(LocalCacheCleanup.IsDue(ByAge(90),
            DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss"), DateTime.Now));

    [Fact]
    public void IsDue_WeeklyAfterEightDays_IsDue() =>
        Assert.True(LocalCacheCleanup.IsDue(new LocalCacheCleanup.Policy(LocalCacheCleanup.Mode.Weekly, 90, 0),
            DateTime.Now.AddDays(-8).ToString("yyyy-MM-dd HH:mm:ss"), DateTime.Now));

    [Fact]
    public void IsDue_NeverMode_NeverDue() =>
        Assert.False(LocalCacheCleanup.IsDue(new LocalCacheCleanup.Policy(LocalCacheCleanup.Mode.Never, 90, 0),
            "", DateTime.Now));
}
