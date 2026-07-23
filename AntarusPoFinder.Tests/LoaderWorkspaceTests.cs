using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Loader;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Локальная рабочая область лоадера. Главное, что здесь проверяется — приложение не
/// клиент-серверное: исходник копируется НА локальную машину, работа идёт локально, и на сетевой
/// диск уезжает только результат, ничего там не снося.</summary>
public class LoaderWorkspaceTests
{
    [Fact]
    public void Create_MakesSourceAndOutputFolders()
    {
        using var root = new TempRoot();
        using var ws = LoaderWorkspace.Create(root.Path, "2.1.042");

        Assert.True(Directory.Exists(ws.SourceDir));
        Assert.True(Directory.Exists(ws.OutputDir));
        Assert.StartsWith(root.Path, ws.Dir);
        Assert.Contains("2.1.042", Path.GetFileName(ws.Dir));
    }

    [Fact]
    public void Create_TwiceInSameSecond_DoesNotCollide()
    {
        using var root = new TempRoot();
        var now = new DateTime(2026, 7, 23, 14, 15, 30);

        using var first = LoaderWorkspace.Create(root.Path, "ver", now);
        using var second = LoaderWorkspace.Create(root.Path, "ver", now);

        Assert.NotEqual(first.Dir, second.Dir);
        Assert.True(Directory.Exists(first.Dir) && Directory.Exists(second.Dir));
    }

    [Fact]
    public void Import_CopiesFileLocally_AndLeavesSourceUntouched()
    {
        using var root = new TempRoot();
        var disk = Path.Combine(root.Path, "disk");
        Directory.CreateDirectory(disk);
        var src = Path.Combine(disk, "prog.lfs");
        File.WriteAllText(src, "firmware");

        using var ws = LoaderWorkspace.Create(Path.Combine(root.Path, "loader"), "v1");
        var local = ws.Import(src);

        Assert.Equal(Path.Combine(ws.SourceDir, "prog.lfs"), local);
        Assert.Equal("firmware", File.ReadAllText(local));
        Assert.True(File.Exists(src), "исходник на диске трогать нельзя");
    }

    [Fact]
    public void Import_CopiesWholeFolder()
    {
        using var root = new TempRoot();
        var srcDir = Path.Combine(root.Path, "disk", "версия");
        Directory.CreateDirectory(Path.Combine(srcDir, "Driver"));
        File.WriteAllText(Path.Combine(srcDir, "proj.psl"), "psl");
        File.WriteAllText(Path.Combine(srcDir, "Driver", "lib.dll"), "dll");

        using var ws = LoaderWorkspace.Create(Path.Combine(root.Path, "loader"), "v1");
        var local = ws.Import(srcDir);

        Assert.True(File.Exists(Path.Combine(local, "proj.psl")));
        Assert.True(File.Exists(Path.Combine(local, "Driver", "lib.dll")));
    }

    [Fact]
    public void Import_MissingSource_Throws()
    {
        using var root = new TempRoot();
        using var ws = LoaderWorkspace.Create(root.Path, "v1");

        Assert.Throws<FileNotFoundException>(() => ws.Import(Path.Combine(root.Path, "нет-такого.lfs")));
    }

    [Fact]
    public void Publish_CopiesArtifactsToDisk_WithoutWipingWhatIsAlreadyThere()
    {
        using var root = new TempRoot();
        var versionDir = Path.Combine(root.Path, "disk", "версия");
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "старая_прошивка.lfs"), "существующий файл");

        using var ws = LoaderWorkspace.Create(Path.Combine(root.Path, "loader"), "v1");
        File.WriteAllText(Path.Combine(ws.OutputDir, "prog.lfs"), "собрано");
        Directory.CreateDirectory(Path.Combine(ws.OutputDir, "logs"));
        File.WriteAllText(Path.Combine(ws.OutputDir, "logs", "build.log"), "лог");

        var published = ws.Publish(versionDir);

        Assert.Equal(2, published.Count);
        Assert.Equal("собрано", File.ReadAllText(Path.Combine(versionDir, "prog.lfs")));
        Assert.True(File.Exists(Path.Combine(versionDir, "logs", "build.log")));
        // Ключевое: публикация ДОКЛАДЫВАЕТ файлы, а не заменяет папку версии целиком.
        Assert.True(File.Exists(Path.Combine(versionDir, "старая_прошивка.lfs")));
    }

    [Fact]
    public void Publish_NothingBuilt_PublishesNothing()
    {
        using var root = new TempRoot();
        var versionDir = Path.Combine(root.Path, "disk", "версия");
        using var ws = LoaderWorkspace.Create(Path.Combine(root.Path, "loader"), "v1");

        Assert.Empty(ws.Publish(versionDir));
        Assert.False(Directory.Exists(versionDir), "пустой результат не должен даже создавать папку на диске");
    }

    [Fact]
    public void CleanupOlderThan_RemovesOnlyStaleWorkspaces()
    {
        using var root = new TempRoot();
        var old = Path.Combine(root.Path, "старая");
        var fresh = Path.Combine(root.Path, "свежая");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(fresh);
        Directory.SetLastWriteTime(old, DateTime.Now.AddDays(-30));

        var removed = LoaderWorkspace.CleanupOlderThan(root.Path, TimeSpan.FromDays(7));

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void CollectArtifacts_ListsEverythingInOut()
    {
        using var root = new TempRoot();
        using var ws = LoaderWorkspace.Create(root.Path, "v1");
        File.WriteAllText(Path.Combine(ws.OutputDir, "a.lfs"), "a");
        Directory.CreateDirectory(Path.Combine(ws.OutputDir, "sub"));
        File.WriteAllText(Path.Combine(ws.OutputDir, "sub", "b.txt"), "b");

        var artifacts = ws.CollectArtifacts();

        Assert.Equal(2, artifacts.Count);
        Assert.Contains(artifacts, a => a.EndsWith("a.lfs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(artifacts, a => a.EndsWith(Path.Combine("sub", "b.txt"), StringComparison.OrdinalIgnoreCase));
    }
}
