using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ОБХОД НАСТОЯЩЕГО ДЕРЕВА — на временной папке, изображающей сетевой диск.
///
/// Разбор слепка проверяется отдельно и без файловой системы (DiskAuditTests), и это правильно: его
/// правила не должны требовать шары. Но ровно одно свойство на описи не проверишь — что обход вообще
/// ЗАМЕЧАЕТ пустую папку. Именно её отсутствие и было дырой: папка HMI есть, файлов нет, а в описи
/// файлов такая папка следа не оставляет.</summary>
public class DiskSnapshotWalkTests
{
    private static string NewTempDb() =>
        Path.Combine(Path.GetTempPath(), $"antarus_snapshot_test_{Guid.NewGuid():N}.db");

    [Fact]
    public void Обход_замечает_пустую_папку_раскладки()
    {
        var dbPath = NewTempDb();
        var root = Path.Combine(Path.GetTempPath(), "antarus_disk_" + Guid.NewGuid().ToString("N"));
        try
        {
            var version = Path.Combine(root, HierarchyService.FolderPo, "ПЖ", "2.0", "SMH5", "1.0");
            Directory.CreateDirectory(Path.Combine(version, VersionLayout.FirmwareFolderName));
            foreach (var slot in VersionLayout.SlotFolderNames)
                Directory.CreateDirectory(Path.Combine(version, slot));
            File.WriteAllText(Path.Combine(version, VersionLayout.FirmwareFolderName, "1.0.psl"), "x");

            using var db = new Database(dbPath);
            var snapshot = DiskSnapshotService.Collect(db, root, "ILIA-PC", "2.2.2.0", DateTime.UtcNow);

            Assert.Contains("ПО/ПЖ/2.0/SMH5/1.0/HMI", snapshot.Folders);
            Assert.Contains("ПО/ПЖ/2.0/SMH5/1.0/Прошивка/1.0.psl", snapshot.Files.Select(f => f.Path));
            // Пустых папок у этой версии четыре (все папки документов), «Прошивка» не пуста.
            Assert.Equal(1, snapshot.Summary.EmptySlots["HMI"]);
            Assert.Equal(0, snapshot.Summary.EmptySlots[VersionLayout.FirmwareFolderName]);
        }
        finally
        {
            TempDbFiles.Delete(dbPath);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Пути в слепке относительные и через косую черту — иначе слепки двух машин одного
    /// диска не сравнить: одна видит его как Z:\Software, другая как \\ant_srv\Software.</summary>
    [Fact]
    public void Папки_в_слепке_относительные_и_через_косую_черту()
    {
        var dbPath = NewTempDb();
        var root = Path.Combine(Path.GetTempPath(), "antarus_disk_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, HierarchyService.FolderPo, "ПЖ"));

            using var db = new Database(dbPath);
            var snapshot = DiskSnapshotService.Collect(db, root, "ILIA-PC", "2.2.2.0", DateTime.UtcNow);

            Assert.All(snapshot.Folders, f =>
            {
                Assert.DoesNotContain('\\', f);
                Assert.StartsWith(HierarchyService.FolderPo, f);
            });
        }
        finally
        {
            TempDbFiles.Delete(dbPath);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
