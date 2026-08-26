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

/// <summary>Привязка документа-таблицы к типам и подтипам шкафов (ParamTableBinding).
///
/// Жалоба владельца: «в таблице теряется привязка к типам подтипам и тп это не предусмотрено».
/// Данные лежали правильно, а вот показать их было негде — и, главное, СВОЕЙ привязки у документа
/// быть не должно: у одного файла параметров в param_files по строке на каждый подтип, и второй,
/// независимый список у документа означал бы два разных ответа на один вопрос.</summary>
public class ParamTableBindingTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public ParamTableBindingTests()
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

    private List<ParamFileLinkService.SubtypeTarget> Targets() => ParamTableBinding.Candidates(_db);

    private ParamFile SeedFile(ParamFileLinkService.SubtypeTarget target, string manuf)
    {
        var folder = _hierarchy.ParamsPath(Root, target.GroupName, target.Subtype.Name, manuf);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "params.par"), "parameters");

        var file = new ParamFile
        {
            SubtypeId = target.Subtype.Id,
            Manufacturer = manuf,
            Filename = "params.par",
            DiskPath = folder,
            UploadDate = "2026-08-26 10:00:00",
        };
        file.Id = _db.AddParamFile(file);
        return file;
    }

    private int SeedTable(ParamFile file, string name = "Задание Modbus") =>
        _db.AddParamTable(new ParamTable
        {
            DiskPath = file.DiskPath, Filename = file.Filename, Name = name, Manufacturer = file.Manufacturer,
        });

    [Fact]
    public void Binding_IsDerivedFromTheParameterFile_NotStoredOnTheDocument()
    {
        var targets = Targets();
        var manuf = _db.GetParamManufacturers().First();
        var primary = SeedFile(targets.First(t => t.GroupName == "ПЖ"), manuf);
        SeedTable(primary);

        var binding = ParamTableBinding.For(_db, primary.DiskPath, primary.Filename, _hierarchy, Root);

        Assert.True(binding.Known);
        var link = Assert.Single(binding.Links);
        Assert.Equal(primary.SubtypeId, link.SubtypeId);
        Assert.True(link.IsPrimary);
        Assert.StartsWith("Относится к:", binding.Describe());
    }

    [Fact]
    public void LinkingOneMoreSubtype_ShowsUpInTheDocumentAtOnce()
    {
        // Ровно ради этого привязка и ВЫВОДИТСЯ, а не хранится у документа: правка одна, а видят её
        // оба — и файл, и документ.
        var targets = Targets();
        var manuf = _db.GetParamManufacturers().First();
        var pj = targets.First(t => t.GroupName == "ПЖ");
        var primary = SeedFile(pj, manuf);
        SeedTable(primary);

        var other = targets.First(t => t.GroupName != pj.GroupName);
        ParamFileLinkService.Apply(_db, _hierarchy, Root, primary, targets, new[] { other.Id }, null);

        var binding = ParamTableBinding.For(_db, primary.DiskPath, primary.Filename, _hierarchy, Root);

        Assert.Equal(2, binding.Links.Count);
        Assert.Contains(binding.Links, l => l.SubtypeId == other.Id);
        Assert.Contains(other.GroupName, binding.Describe());
    }

    [Fact]
    public void Primary_IsTheSubtypeWhoseFolderActuallyHoldsTheFile()
    {
        // Основной подтип отвязать нельзя — это и есть сам файл, а не ссылка на него. Ошибись здесь,
        // и человеку запретят снять не ту галочку.
        var targets = Targets();
        var manuf = _db.GetParamManufacturers().First();
        var pj = targets.First(t => t.GroupName == "ПЖ");
        var primary = SeedFile(pj, manuf);

        var other = targets.First(t => t.GroupName != pj.GroupName);
        ParamFileLinkService.Apply(_db, _hierarchy, Root, primary, targets, new[] { other.Id }, null);

        var binding = ParamTableBinding.For(_db, primary.DiskPath, primary.Filename, _hierarchy, Root);

        Assert.Equal(pj.Id, binding.Links.Single(l => l.IsPrimary).SubtypeId);
    }

    [Fact]
    public void FileNotRegistered_IsSaidPlainly_NotShownAsEmpty()
    {
        // Файл на диске есть, а записи о нём нет: её удалили либо документ приехал с машины, где
        // диск смонтирован иначе. Молчать об этом нельзя — наладчик не поймёт, к какому шкафу
        // таблица.
        var binding = ParamTableBinding.For(_db, @"D:\ПО\Параметры\ESQ", "чужой.par");

        Assert.False(binding.Known);
        Assert.Empty(binding.Links);
        Assert.Equal("Ни к одному подтипу шкафа не привязан", binding.Describe());
    }

    [Fact]
    public void Register_CreatesTheMissingFileRecord_WithoutTouchingTheDisk()
    {
        var target = Targets().First();
        var folder = _hierarchy.ParamsPath(Root, target.GroupName, target.Subtype.Name, "ESQ");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "лежит.par"), "parameters");

        var table = new ParamTable { DiskPath = folder, Filename = "лежит.par", Manufacturer = "ESQ", Name = "Док" };
        table.Id = _db.AddParamTable(table);

        var file = ParamTableBinding.Register(_db, table, target.Id);

        Assert.NotNull(file.Id);
        var binding = ParamTableBinding.For(_db, table.DiskPath, table.Filename, _hierarchy, Root);
        Assert.True(binding.Known);
        Assert.Equal(target.Id, Assert.Single(binding.Links).SubtypeId);
        // Файл на диске остался ровно один и там же, где лежал: это регистрация, а не загрузка.
        Assert.Single(Directory.GetFiles(Root, "лежит.par", SearchOption.AllDirectories));
    }

    [Fact]
    public void SameFileWithSeveralRecords_GivesOneLinkPerSubtype_NotPerRecord()
    {
        var targets = Targets();
        var manuf = _db.GetParamManufacturers().First();
        var pj = targets.First(t => t.GroupName == "ПЖ");
        var primary = SeedFile(pj, manuf);

        // Вторая запись на ТОТ ЖЕ подтип — так бывает после перезаливки со старого клиента.
        _db.AddParamFile(new ParamFile
        {
            SubtypeId = primary.SubtypeId, Manufacturer = manuf,
            Filename = primary.Filename, DiskPath = primary.DiskPath, UploadDate = "2026-08-26 11:00:00",
        });

        var binding = ParamTableBinding.For(_db, primary.DiskPath, primary.Filename, _hierarchy, Root);

        Assert.Single(binding.Links);
    }
}
