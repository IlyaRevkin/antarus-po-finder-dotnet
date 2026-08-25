using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Подсчёт «сколько путей в базе указывают на диск, которого на этой машине нет».
///
/// Главное требование — НЕ поднимать ложную тревогу. Чужая буква в базе сама по себе штатна: пути
/// пишутся абсолютными, с буквой заливавшей машины, и FirmwarePathLocalizer приводит их к своему
/// корню по якорным папкам «ПО»/«Параметры». Поломка — только путь без якоря: его приводить не к
/// чему. Разница между этими двумя случаями и есть то, ради чего аудит написан.</summary>
public class StoredPathAuditTests
{
    [Theory]
    [InlineData(@"Z:\Software\Antarus Finder\ПО", "Z:")]
    [InlineData(@"z:\software", "Z:")]
    [InlineData(@"\\ant_srv\Software\Antarus Finder", @"\\ant_srv\Software")]
    [InlineData(@"\\ant_srv\Software", @"\\ant_srv\Software")]
    [InlineData(@"Обновления\подпапка", "")]
    [InlineData("", "")]
    public void RootOf_RecognisesDriveLettersAndShares(string path, string expected) =>
        Assert.Equal(expected, StoredPathAudit.RootOf(path));

    /// <summary>Две разные шары одного сервера — разные корни. Сводить их к «\\ant_srv» нельзя:
    /// права и содержимое у них разные, и в отчёте это увело бы не туда.</summary>
    [Fact]
    public void RootOf_SharesOnSameServer_AreDifferentRoots() =>
        Assert.NotEqual(StoredPathAudit.RootOf(@"\\ant_srv\Soft\a"), StoredPathAudit.RootOf(@"\\ant_srv\Docs\a"));

    /// <summary>Живой случай коллеги (25.08.2026): в базе пути с буквой Z, а рабочий диск у него
    /// подключён по UNC. Все пути лежат под якорными папками, значит программа приводит их к его
    /// корню сама — тревоги здесь быть не должно.</summary>
    [Fact]
    public void Audit_ForeignLetterButAnchoredPaths_CountsAsRescuedNotBroken()
    {
        var groups = new List<StoredPathGroup>
        {
            new(@"Z:\Software\Antarus Finder\ПО\НГР\PIXEL\1.1.0044.0001", 12),
            new(@"Z:\Software\Antarus Finder\Параметры\НГР\Segnetics", 4),
        };

        var result = StoredPathAudit.Audit(groups, @"\\ant_srv\Software\Antarus Finder");

        Assert.Equal(16, result.Records);
        Assert.Equal(16, result.Foreign);
        Assert.Equal(16, result.Rescued);
        Assert.Equal(0, result.Broken);
        Assert.Equal("", result.BrokenSample);
        Assert.Equal("Z:", Assert.Single(result.ForeignRoots).Root);
    }

    /// <summary>А вот это уже поломка: путь с чужой буквой и БЕЗ якорной папки — привести его не к
    /// чему, он откроется дословно, на диске, которого здесь нет.</summary>
    [Fact]
    public void Audit_ForeignLetterWithoutAnchorFolder_CountsAsBroken()
    {
        var groups = new List<StoredPathGroup>
        {
            new(@"Z:\Software\Antarus Finder\ПО\НГР\PIXEL\1.1", 10),
            new(@"Z:\Старое\ручная папка", 3),
        };

        var result = StoredPathAudit.Audit(groups, @"\\ant_srv\Software\Antarus Finder");

        Assert.Equal(13, result.Records);
        Assert.Equal(13, result.Foreign);
        Assert.Equal(10, result.Rescued);
        Assert.Equal(3, result.Broken);
        Assert.Equal(@"Z:\Старое\ручная папка", result.BrokenSample);
    }

    [Fact]
    public void Audit_PathsOnOwnRoot_AreNotForeign()
    {
        var groups = new List<StoredPathGroup> { new(@"Z:\Antarus\ПО\НГР\PIXEL", 7) };

        var result = StoredPathAudit.Audit(groups, @"Z:\Antarus");

        Assert.Equal(7, result.Records);
        Assert.Equal(0, result.Foreign);
        Assert.Empty(result.ForeignRoots);
    }

    /// <summary>Корень не настроен — сравнивать не с чем, и объявлять всё чужим нельзя.</summary>
    [Fact]
    public void Audit_WithoutLocalRoot_CountsRecordsOnly()
    {
        var result = StoredPathAudit.Audit(new List<StoredPathGroup> { new(@"Z:\Antarus\ПО\x", 5) }, "");

        Assert.Equal(5, result.Records);
        Assert.Equal(0, result.Foreign);
        Assert.Equal(0, result.Broken);
    }

    [Fact]
    public void Audit_ForeignRoots_AreOrderedByRecordCount()
    {
        var groups = new List<StoredPathGroup>
        {
            new(@"Y:\Antarus\Старое", 2),
            new(@"Z:\Antarus\Старое", 9),
        };

        var roots = StoredPathAudit.Audit(groups, @"\\srv\Antarus").ForeignRoots;

        Assert.Equal(new[] { "Z:", "Y:" }, roots.Select(r => r.Root).ToArray());
        Assert.Equal("Z: — 9, Y: — 2", StoredPathAudit.DescribeRoots(roots));
    }

    /// <summary>Аудит считает по тому, что реально отдаёт база: прошивки и файлы параметров вместе,
    /// удалённые и архивные — мимо (они и так никуда не открываются, а цифру раздували бы).</summary>
    [Fact]
    public void GetStoredDiskPathGroups_TakesFirmwareAndParams_SkipsArchivedAndDeleted()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First();
        var pixel = db.GetAllModifications().First(m => m.ControllerName == "PIXEL");

        int AddFw(string diskPath)
        {
            return db.AddFwVersion(new FwVersionRecord
            {
                SubtypeId = subtype.Id!.Value,
                ControllerId = pixel.ControllerId,
                EqPrefix = group.Prefix,
                SubPrefix = subtype.Prefix,
                HwVersion = pixel.HwVersion,
                SwVersion = 1,
                DtStr = "20260101_0000",
                VersionRaw = "1.1.0001.0001",
                Filename = "fw.psl",
                DiskPath = diskPath,
                Status = "active",
            });
        }

        // Свежая база не пуста (HierarchyDefaults сеет свои записи) — считаем прирост, а не итог.
        var before = db.GetStoredDiskPathGroups().Sum(g => g.Records);

        AddFw(@"Z:\Antarus\ПО\ПЖ\PIXEL\1.1");
        AddFw(@"Z:\Antarus\ПО\ПЖ\PIXEL\1.1");
        db.TombstoneFwVersion(AddFw(@"Z:\Antarus\ПО\ПЖ\PIXEL\удалённая"));

        db.AddParamFile(new ParamFile
        {
            SubtypeId = subtype.Id!.Value,
            Manufacturer = "TESTVENDOR",
            Filename = "params.dcfx",
            DiskPath = @"Z:\Antarus\Параметры\ПЖ\Danfoss",
            UploadDate = "2026-01-01 00:00:00",
        });
        db.AddParamFile(new ParamFile
        {
            SubtypeId = subtype.Id!.Value,
            Manufacturer = "TESTVENDOR",
            Filename = "params_old.dcfx",
            DiskPath = @"Z:\Antarus\Параметры\ПЖ\архив",
            UploadDate = "2026-01-01 00:00:00",
            Archived = true,
        });

        var groups = db.GetStoredDiskPathGroups();

        // Обе прошивки в одной группе (путь один), файл параметров — своей; архивная не в счёт.
        Assert.Equal(3, groups.Sum(g => g.Records) - before);
        Assert.Contains(groups, g => g.Path == @"Z:\Antarus\ПО\ПЖ\PIXEL\1.1" && g.Records == 2);
        Assert.Contains(groups, g => g.Path == @"Z:\Antarus\Параметры\ПЖ\Danfoss" && g.Records == 1);
        Assert.DoesNotContain(groups, g => g.Path.EndsWith("удалённая"));
        Assert.DoesNotContain(groups, g => g.Path.EndsWith("архив"));
    }
}
