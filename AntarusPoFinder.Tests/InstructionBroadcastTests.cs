using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Один документ-инструкция — сразу нескольким версиям шкафа. «Вдруг ошибку нашли, а QR
/// ведёт на старую с ошибкой и нужно перезалить новую без ошибки» (Илья): исправленная инструкция
/// касается всех версий сразу, а прикладывать её приходилось по одной.</summary>
public class InstructionBroadcastTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public InstructionBroadcastTests()
    {
        _db = new Database(_dbFile.Path);
        _hierarchy = new HierarchyService(_db);
        _hierarchy.EnsureStructure(Root);
    }

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
        _tempRoot.Dispose();
    }

    /// <summary>Три версии одного шкафа, загруженные подряд, — от старой к новой.</summary>
    private List<FwVersionRecord> SeedVersions(int count)
    {
        var group = _db.GetAllEquipmentGroups().Single(g => g.Name == "ТГР");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).Single();
        var mod = _db.GetAllModifications().Single(m => m.ControllerName == "SMH5" && m.DisplayName == "SMH5");

        var versions = new List<FwVersionRecord>();
        for (var i = 0; i < count; i++)
        {
            var src = Path.Combine(Root, $"source{i}.psl");
            File.WriteAllText(src, $"прошивка {i}");
            var upload = FirmwareUploadService.Upload(_db, _hierarchy, new FirmwareUploadRequest
            {
                SourcePath = src,
                Group = group,
                Subtype = subtype,
                Modification = mod,
                LaunchTypes = new() { "УПП" },
                Description = $"загрузка {i}",
                IncludeDateInVersion = false,
                RootPath = Root,
                AuthorUserName = "tester",
            });
            Assert.Equal(FirmwareUploadOutcome.Success, upload.Outcome);
            versions.Add(upload.Record!);
        }
        return versions;
    }

    private string WriteDocument(string name, string text)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Версии_шкафа_видны_списком_и_более_ранние_отмечены()
    {
        var versions = SeedVersions(3);
        var newest = versions[^1];

        var targets = InstructionBroadcast.Targets(_db, newest, Root);

        Assert.Equal(3, targets.Count);
        Assert.Single(targets.Where(t => t.IsSelf));
        Assert.Equal(2, targets.Count(t => t.IsEarlier));
        Assert.All(targets, t => Assert.False(t.IsSelf && t.IsEarlier));
        // Сама версия «более ранней» себе не считается — иначе набор «эта и более ранние» и набор
        // «только эта» отличались бы на пустом месте.
        Assert.DoesNotContain(targets.Where(t => t.IsEarlier), t => t.Version.Id == newest.Id);
    }

    [Fact]
    public void Документ_ложится_каждой_версии_под_её_собственным_именем()
    {
        var versions = SeedVersions(3);
        var doc = WriteDocument("инструкция исправленная.pdf", "без ошибки");

        var result = InstructionBroadcast.Apply(_db, Root, doc, versions);

        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.Applied.Count);
        foreach (var version in versions)
        {
            var stored = _db.GetFwVersionById(version.Id!.Value)!.InstructionsPath;
            Assert.True(File.Exists(stored));
            Assert.Equal("без ошибки", File.ReadAllText(stored));
            // Имя каноническое и своё у каждой версии: именно по этому адресу ведёт наклеенный QR.
            Assert.True(InstructionNaming.IsCanonical(stored, version.VersionRaw),
                $"{stored} должен называться по версии {version.VersionRaw}");
        }
    }

    [Fact]
    public void Прежняя_инструкция_версии_уезжает_в_архив_а_по_прежнему_адресу_лежит_новая()
    {
        var versions = SeedVersions(2);
        var withError = WriteDocument("инструкция с ошибкой.pdf", "редакция с ошибкой");
        InstructionBroadcast.Apply(_db, Root, withError, versions);

        var addressBefore = _db.GetFwVersionById(versions[0].Id!.Value)!.InstructionsPath;

        var fixedDoc = WriteDocument("инструкция исправленная.pdf", "редакция без ошибки");
        var result = InstructionBroadcast.Apply(_db, Root, fixedDoc, versions);

        Assert.Equal(2, result.Replaced.Count);

        // Адрес не изменился — значит уже напечатанный и наклеенный QR открывает НОВЫЙ документ.
        var addressAfter = _db.GetFwVersionById(versions[0].Id!.Value)!.InstructionsPath;
        Assert.Equal(addressBefore, addressAfter);
        Assert.Equal("редакция без ошибки", File.ReadAllText(addressAfter));

        // А прежняя редакция никуда не делась — она в «Прежних редакциях» рядом. Редакций там две:
        // версии загружены по ПРЕЖНЕЙ раскладке, у которой папка «Инструкция» общая на весь
        // контроллер (см. VersionLayout), и архив у них, соответственно, тоже общий — но документы
        // в нём разные, каждый под своим каноническим именем.
        var archive = Path.Combine(Path.GetDirectoryName(addressAfter)!, InstructionStorage.PreviousFolderName);
        var kept = Directory.GetFiles(archive);
        Assert.Equal(versions.Count, kept.Length);
        Assert.All(kept, f => Assert.Equal("редакция с ошибкой", File.ReadAllText(f)));
    }

    [Fact]
    public void Несуществующий_документ_ничего_не_трогает()
    {
        var versions = SeedVersions(2);

        var result = InstructionBroadcast.Apply(_db, Root, Path.Combine(Root, "нет такого.pdf"), versions);

        Assert.Empty(result.Applied);
        Assert.Single(result.Warnings);
        Assert.All(versions, v => Assert.Equal("", _db.GetFwVersionById(v.Id!.Value)!.InstructionsPath));
    }
}
