using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Разовый перенос накопленных текстовых заданий в документы-таблицы
/// (ParamTableBulkImport).
///
/// Жалоба владельца: «Автоматически файлы не перенёс что были». Требование к решению шло следом:
/// «не молча и не вслепую… результат должен быть виден и обратим» — отсюда раздельные Scan/Import и
/// отдельный Undo, снимающий ровно заведённое.
///
/// Диска здесь нет: чтение и обход папки подменяются, иначе тест не пошёл бы в CI.</summary>
public class ParamTableBulkImportTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly Database _db;

    /// <summary>Подставная папка: путь → содержимое файла.</summary>
    private readonly Dictionary<string, string> _disk = new(StringComparer.OrdinalIgnoreCase);

    public ParamTableBulkImportTests() => _db = new Database(_dbFile.Path);

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
    }

    private byte[] Read(string path) => _disk.TryGetValue(path, out var text)
        ? Encoding.UTF8.GetBytes(text)
        : throw new FileNotFoundException(path);

    private IEnumerable<string> List(string folder) =>
        _disk.Keys.Where(k => string.Equals(Path.GetDirectoryName(k), folder, StringComparison.OrdinalIgnoreCase));

    private List<ParamTableBulkImport.Item> Scan() => ParamTableBulkImport.Scan(_db, Read, List);

    private ParamFile SeedFile(string folder, string filename, string manuf = "ESQ", string tags = "")
    {
        var file = new ParamFile
        {
            SubtypeId = _db.GetAllEquipmentSubtypes().First().Id,
            Manufacturer = manuf,
            Filename = filename,
            DiskPath = folder,
            Tags = tags,
            UploadDate = "2026-08-26 10:00:00",
        };
        file.Id = _db.AddParamFile(file);
        return file;
    }

    private const string Sample = """
        ESQ-230 - КПЧ(Задание Modbus)

        =================[Настройка ШУ]
        P0-02(2) - Выбор канала команды запуска - Протокол связи
        P0-03(9) - Основной канал задания частоты - Протокол связи

        ================[Двигатель]
        P1-01 - Мощность
        """;

    // ── Что можно перенести ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_TakesTheTextFileLyingNextToTheParameterFile()
    {
        // Сам файл параметров — проприетарная выгрузка конфигуратора, разбирать в ней нечего;
        // читаемое задание лежит соседним txt. Документ при этом заводится на ЗАРЕГИСТРИРОВАННЫЙ
        // файл, иначе окно таблицы своего же документа не нашло бы.
        var file = SeedFile(@"D:\ПО\ESQ", "params.par");
        _disk[@"D:\ПО\ESQ\ESQ-230 2025 - КПЧ(Задание Modbus).txt"] = Sample;

        var item = Assert.Single(Scan());

        Assert.True(item.CanImport);
        Assert.Equal("ESQ-230 2025 - КПЧ(Задание Modbus).txt", item.SourceName);
        Assert.Equal("Задание Modbus", item.DocumentName);
        Assert.Equal(3, item.ParamRows);
        Assert.Equal(file.Filename, item.File.Filename);
    }

    [Fact]
    public void Scan_TakesTheRegisteredFileItself_WhenItIsAlreadyText()
    {
        SeedFile(@"D:\ПО\ESQ", "задание.txt");
        _disk[@"D:\ПО\ESQ\задание.txt"] = Sample;

        var item = Assert.Single(Scan());

        Assert.True(item.CanImport);
        Assert.Equal("задание.txt", item.SourceName);
    }

    [Fact]
    public void Scan_SkipsFilesThatAlreadyHaveADocument()
    {
        var file = SeedFile(@"D:\ПО\ESQ", "params.par");
        _disk[@"D:\ПО\ESQ\задание.txt"] = Sample;
        _db.AddParamTable(new ParamTable { DiskPath = file.DiskPath, Filename = file.Filename, Name = "Уже есть" });

        var item = Assert.Single(Scan());

        Assert.False(item.CanImport);
        Assert.Contains("уже есть", item.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_SaysWhyWhenTheTextHasNoParametersAtAll()
    {
        // Readme рядом с параметрами — обычное дело. Перенести его молча значило бы завести документ
        // из десяти строк прозы.
        SeedFile(@"D:\ПО\ESQ", "params.par");
        _disk[@"D:\ПО\ESQ\Readme.txt"] = "Тут лежит бэкап настроек, снятый с объекта.\nЗвонить Иванову.";

        var item = Assert.Single(Scan());

        Assert.False(item.CanImport);
        Assert.Contains("Ни одного параметра", item.Refusal);
        Assert.False(item.Selected);
    }

    [Fact]
    public void Scan_SaysWhyWhenThereIsNoTextAtAll()
    {
        SeedFile(@"D:\ПО\ESQ", "params.par");

        var item = Assert.Single(Scan());

        Assert.False(item.CanImport);
        Assert.Contains("нет текстового файла", item.Refusal);
    }

    [Fact]
    public void Scan_DoesNotWriteAnything()
    {
        SeedFile(@"D:\ПО\ESQ", "params.par");
        _disk[@"D:\ПО\ESQ\задание.txt"] = Sample;

        Scan();

        Assert.Empty(_db.GetParamTables());
    }

    [Fact]
    public void Scan_ReadsOneFileOnce_EvenWithSeveralRecordsBehindIt()
    {
        // У одного файла в param_files по строке на каждый привязанный подтип. Не схлопни их — и
        // одинаковых документов появилось бы пять, все на один и тот же файл.
        var folder = @"D:\ПО\ESQ";
        SeedFile(folder, "params.par");
        var subtypes = _db.GetAllEquipmentSubtypes().Take(2).ToList();
        _db.AddParamFile(new ParamFile
        {
            SubtypeId = subtypes[^1].Id, Manufacturer = "ESQ",
            Filename = "params.par", DiskPath = folder, UploadDate = "2026-08-26 11:00:00",
        });
        _disk[folder + @"\задание.txt"] = Sample;

        Assert.Single(Scan());
    }

    [Fact]
    public void Scan_UsesTheSameTextOnlyOnce_WhenTwoFilesShareAFolder()
    {
        var folder = @"D:\ПО\ESQ";
        SeedFile(folder, "params.par");
        SeedFile(folder, "params2.par");
        _disk[folder + @"\задание.txt"] = Sample;

        var items = Scan();

        Assert.Single(items.Where(i => i.CanImport));
        Assert.Contains(items, i => !i.CanImport && i.Refusal!.Contains("нет текстового файла"));
    }

    // ── Перенос и его отмена ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Import_CreatesTheDocumentWithItsFirstRevision()
    {
        var file = SeedFile(@"D:\ПО\ESQ", "params.par", tags: "modbus, esq");
        _disk[@"D:\ПО\ESQ\задание.txt"] = Sample;
        var items = Scan();

        var result = ParamTableBulkImport.Import(_db, items, "Ilia");

        var created = Assert.Single(result.Created);
        Assert.Empty(result.Failed);

        var table = Assert.Single(_db.GetParamTablesForFile(file.DiskPath, file.Filename));
        Assert.Equal(created.TableId, table.Id);
        // Теги файла достаются документу: искать его человек будет теми же словами.
        Assert.Equal("modbus, esq", table.Tags);

        var revision = Assert.Single(ParamTableNumbering.LiveRevisions(_db, table.Id!.Value));
        Assert.Contains("задание.txt", revision.Reason);
        Assert.NotEmpty(_db.GetParamTableRows(revision.Id!.Value));
    }

    [Fact]
    public void Import_TakesOnlyWhatWasChecked()
    {
        SeedFile(@"D:\ПО\ESQ", "params.par");
        SeedFile(@"D:\ПО\VEDA", "veda.par");
        _disk[@"D:\ПО\ESQ\задание.txt"] = Sample;
        _disk[@"D:\ПО\VEDA\задание.txt"] = Sample;

        var items = Scan();
        Assert.Equal(2, items.Count(i => i.CanImport));
        items.First(i => i.File.DiskPath.EndsWith("VEDA")).Selected = false;

        var result = ParamTableBulkImport.Import(_db, items, "Ilia");

        Assert.Single(result.Created);
        Assert.Empty(_db.GetParamTablesForFile(@"D:\ПО\VEDA", "veda.par"));
    }

    [Fact]
    public void Import_UsesTheNameThePersonCorrected()
    {
        SeedFile(@"D:\ПО\ESQ", "params.par");
        _disk[@"D:\ПО\ESQ\задание.txt"] = Sample;
        var items = Scan();
        items[0].DocumentName = "Пуск по месту";

        ParamTableBulkImport.Import(_db, items, "Ilia");

        Assert.Equal("Пуск по месту", Assert.Single(_db.GetParamTables()).Name);
    }

    [Fact]
    public void Undo_RemovesExactlyWhatWasCreated_AndNothingElse()
    {
        var older = _db.AddParamTable(new ParamTable
        {
            DiskPath = @"D:\ПО\ДРУГОЕ", Filename = "old.par", Name = "Заведён руками",
        });
        SeedFile(@"D:\ПО\ESQ", "params.par");
        _disk[@"D:\ПО\ESQ\задание.txt"] = Sample;

        var result = ParamTableBulkImport.Import(_db, Scan(), "Ilia");
        Assert.Equal(1, ParamTableBulkImport.Undo(_db, result.Created));

        var alive = _db.GetParamTables();
        Assert.Equal(older, Assert.Single(alive).Id);
    }

    [Fact]
    public void Undo_IsATombstone_NotADelete()
    {
        // Иначе документ вернулся бы с первым же снимком конфига с машины, которая об отмене
        // не знает (общее правило: «строки нет во входящем снимке» никогда не значит «её убрали»).
        SeedFile(@"D:\ПО\ESQ", "params.par");
        _disk[@"D:\ПО\ESQ\задание.txt"] = Sample;

        var result = ParamTableBulkImport.Import(_db, Scan(), "Ilia");
        ParamTableBulkImport.Undo(_db, result.Created);

        var table = _db.GetParamTable(result.Created[0].TableId);
        Assert.NotNull(table);
        Assert.NotEqual("", table!.DeletedAt);
    }

    [Fact]
    public void Report_TellsWhatWasTakenAndWhatWasNot()
    {
        SeedFile(@"D:\ПО\ESQ", "params.par");
        SeedFile(@"D:\ПО\VEDA", "veda.par");
        _disk[@"D:\ПО\ESQ\задание.txt"] = Sample;

        var report = ParamTableBulkImport.Report(Scan());

        Assert.Contains("задание.txt", report);
        Assert.Contains("нет текстового файла", report);
    }
}
