using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Перезаливка файла параметров под тем же именем (ParamFileUploadService) и разовая чистка
/// файлов-двойников на диске (ParamFileDuplicateCleanup).
///
/// Что просил владелец дословно: «старые файлы сохранять для просмотра, новые для открытия. Без
/// полной истории как у прошивок — просто дату загрузки добавлять, всегда открывать свежую, а кому
/// нужна старая — пусть откроет папку и найдёт. И поле описание чтобы вело учёт изменений».</summary>
public class ParamFileReuploadTests : IDisposable
{
    private sealed class FakeShortcuts : IShortcutCreator
    {
        public System.Collections.Generic.List<string> Created { get; } = new();
        public void Create(string shortcutPath, string targetPath, string description)
        {
            Created.Add(shortcutPath);
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
            File.WriteAllText(shortcutPath, targetPath);
        }
    }

    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public ParamFileReuploadTests()
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

    private System.Collections.Generic.List<ParamFileLinkService.SubtypeTarget> AllTargets()
    {
        var groups = _db.GetAllEquipmentGroups().ToDictionary(g => g.Id ?? 0, g => g.Name);
        return _db.GetAllEquipmentSubtypes()
            .Where(s => s.Id is not null)
            .Select(s => new ParamFileLinkService.SubtypeTarget(s, groups.TryGetValue(s.GroupId, out var n) ? n : ""))
            .ToList();
    }

    // ── Жалоба 3: перезаливка ────────────────────────────────────────────────────────────────

    /// <summary>Перезаливка сохраняет прежнюю редакцию рядом («имя (до ГГГГ-ММ-ДД).ext»), кладёт
    /// свежий файл под исходным именем и ОБНОВЛЯЕТ запись, а не плодит вторую. Раньше File.Copy с
    /// overwrite затирал прежний файл насовсем, а в БД появлялся дубль.</summary>
    [Fact]
    public void Reupload_KeepsPreviousEditionAndUpdatesTheRecord()
    {
        var target = AllTargets().First(t => t.GroupName == "ПЖ");
        var manuf = _db.GetParamManufacturers().First();
        var folder = _hierarchy.ParamsPath(Root, target.GroupName, target.Subtype.Name, manuf);
        Directory.CreateDirectory(folder);

        var first = new DateTime(2026, 7, 1, 9, 0, 0);
        File.WriteAllText(Path.Combine(folder, "params.par"), "версия 1");
        var record1 = new ParamFile
        {
            SubtypeId = target.Id, Manufacturer = manuf, Filename = "params.par",
            DiskPath = folder, Description = "исходные параметры",
        };
        var outcome1 = ParamFileUploadService.SaveRecord(_db, record1, null, first);
        Assert.False(outcome1.Updated);

        // Вторая заливка того же имени.
        var second = new DateTime(2026, 8, 3, 14, 30, 0);
        var archived = ParamFileUploadService.ArchivePreviousOnDisk(folder, "params.par", second);
        File.WriteAllText(Path.Combine(folder, "params.par"), "версия 2");
        var record2 = new ParamFile
        {
            SubtypeId = target.Id, Manufacturer = manuf, Filename = "params.par",
            DiskPath = folder, Description = "поправил уставки разгона",
        };
        var outcome2 = ParamFileUploadService.SaveRecord(_db, record2, archived, second);

        // Прежняя редакция цела и лежит в подпапке; свежая — под исходным именем в самой папке.
        Assert.Equal(Path.Combine(ParamFileUploadService.ArchiveFolderName, "params (до 2026-08-03).par"), archived);
        Assert.Equal("версия 1", File.ReadAllText(Path.Combine(folder, ParamFileUploadService.ArchiveFolderName, "params (до 2026-08-03).par")));
        Assert.Equal("версия 2", File.ReadAllText(Path.Combine(folder, "params.par")));

        // Запись ОДНА и та же, обновлённая.
        Assert.True(outcome2.Updated);
        Assert.Equal(outcome1.RecordId, outcome2.RecordId);
        var row = Assert.Single(_db.GetParamFiles(target.Id));
        Assert.Equal("2026-08-03 14:30:00", row.UploadDate);

        // Описание ведёт учёт изменений: старый текст цел, снизу датированная строка-лог.
        Assert.Contains("исходные параметры", row.Description);
        Assert.Contains("[2026-08-03]", row.Description);
        Assert.Contains("поправил уставки разгона", row.Description);
        Assert.Contains("params (до 2026-08-03).par", row.Description);
    }

    /// <summary>Две перезаливки в один день не затирают друг друга: у второй сохранённой редакции
    /// добавляется порядковый номер. Прежняя редакция не теряется никогда — в этом весь смысл.</summary>
    [Fact]
    public void ArchivePrevious_TwiceInOneDay_DoesNotOverwrite()
    {
        var folder = Path.Combine(Root, "две_перезаливки");
        Directory.CreateDirectory(folder);
        var day = new DateTime(2026, 8, 3);

        var archiveDir = Path.Combine(folder, ParamFileUploadService.ArchiveFolderName);

        File.WriteAllText(Path.Combine(folder, "p.par"), "1");
        Assert.Equal(Path.Combine(ParamFileUploadService.ArchiveFolderName, "p (до 2026-08-03).par"),
            ParamFileUploadService.ArchivePreviousOnDisk(folder, "p.par", day));

        File.WriteAllText(Path.Combine(folder, "p.par"), "2");
        Assert.Equal(Path.Combine(ParamFileUploadService.ArchiveFolderName, "p (до 2026-08-03, 2).par"),
            ParamFileUploadService.ArchivePreviousOnDisk(folder, "p.par", day));

        Assert.Equal("1", File.ReadAllText(Path.Combine(archiveDir, "p (до 2026-08-03).par")));
        Assert.Equal("2", File.ReadAllText(Path.Combine(archiveDir, "p (до 2026-08-03, 2).par")));
    }

    /// <summary>Редакции, накопленные версиями ДО появления подпапки (лежат прямо рядом с рабочим
    /// файлом), переезжают в «Прежние редакции» — это и есть та «россыпь дубликатов», на которую
    /// жаловались. Рабочий файл и посторонние файлы не трогаются.</summary>
    [Fact]
    public void TidyArchives_MovesLegacyRevisionsIntoSubfolder()
    {
        var folder = Path.Combine(Root, "старые_редакции");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "p.par"), "актуальный");
        File.WriteAllText(Path.Combine(folder, "p (до 2026-07-01).par"), "старый 1");
        File.WriteAllText(Path.Combine(folder, "p (до 2026-07-02).par"), "старый 2");
        File.WriteAllText(Path.Combine(folder, "заметка.txt"), "не трогать");

        var skipped = new List<string>();
        var moved = ParamFileDuplicateCleanup.TidyArchives(folder, skipped);

        Assert.Equal(2, moved.Count);
        Assert.Empty(skipped);
        var archiveDir = Path.Combine(folder, ParamFileUploadService.ArchiveFolderName);
        Assert.Equal("старый 1", File.ReadAllText(Path.Combine(archiveDir, "p (до 2026-07-01).par")));
        Assert.Equal("старый 2", File.ReadAllText(Path.Combine(archiveDir, "p (до 2026-07-02).par")));
        Assert.Equal("актуальный", File.ReadAllText(Path.Combine(folder, "p.par")));
        Assert.True(File.Exists(Path.Combine(folder, "заметка.txt")));

        // Идемпотентность: второй прогон уже нечего переносить и не на что жаловаться.
        Assert.Empty(ParamFileDuplicateCleanup.TidyArchives(folder, skipped));
        Assert.Empty(skipped);
    }

    /// <summary>Первая загрузка (в папке ещё пусто) остаётся ровно тем, чем была: никаких «до …»,
    /// новая запись, а не обновление.</summary>
    [Fact]
    public void FirstUpload_ArchivesNothing()
    {
        var folder = Path.Combine(Root, "первая_загрузка");
        Directory.CreateDirectory(folder);
        Assert.Null(ParamFileUploadService.ArchivePreviousOnDisk(folder, "p.par", DateTime.Now));
    }

    /// <summary>Перезаливка освежает дату у ВСЕХ записей этого файла — и у основного подтипа, и у
    /// дополнительных, привязанных ярлыком: файл-то один и тот же.</summary>
    [Fact]
    public void Reupload_RefreshesLinkedSubtypeRowsToo()
    {
        var targets = AllTargets();
        var manuf = _db.GetParamManufacturers().First();
        var primaryTarget = targets.First(t => t.GroupName == "ПЖ");
        var extraTarget = targets.First(t => t.GroupName != primaryTarget.GroupName);

        var folder = _hierarchy.ParamsPath(Root, primaryTarget.GroupName, primaryTarget.Subtype.Name, manuf);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "params.par"), "версия 1");

        var record = new ParamFile
        {
            SubtypeId = primaryTarget.Id, Manufacturer = manuf, Filename = "params.par",
            DiskPath = folder, Description = "исходные",
        };
        ParamFileUploadService.SaveRecord(_db, record, null, new DateTime(2026, 7, 1, 9, 0, 0));
        ParamFileLinkService.LinkToExtraSubtypes(_db, _hierarchy, Root, primaryTarget.Id, record,
            new[] { extraTarget }, new FakeShortcuts());

        var second = new DateTime(2026, 8, 3, 14, 0, 0);
        var archived = ParamFileUploadService.ArchivePreviousOnDisk(folder, "params.par", second);
        File.WriteAllText(Path.Combine(folder, "params.par"), "версия 2");
        var reuploaded = new ParamFile
        {
            SubtypeId = primaryTarget.Id, Manufacturer = manuf, Filename = "params.par",
            DiskPath = folder, Description = "",
        };
        ParamFileUploadService.SaveRecord(_db, reuploaded, archived, second);
        // Страница «Параметры» при каждой загрузке заново применяет отмеченные дополнительные
        // подтипы — повторное применение того же набора не должно плодить вторую строку.
        var link = ParamFileLinkService.LinkToExtraSubtypes(_db, _hierarchy, Root, primaryTarget.Id,
            reuploaded, new[] { extraTarget }, new FakeShortcuts());
        Assert.Empty(link.CreatedIds);

        var extraRow = Assert.Single(_db.GetParamFiles(extraTarget.Id));
        Assert.Equal("2026-08-03 14:00:00", extraRow.UploadDate);
        Assert.Contains("[2026-08-03]", extraRow.Description);
    }

    // ── Жалоба 1: ровно один физический файл ────────────────────────────────────────────────

    /// <summary>Привязка к дополнительному подтипу даёт ровно ОДИН физический файл на диске плюс
    /// ярлык. Проверяется по всему дереву корня, а не по одной папке: жалоба была именно про лишние
    /// текстовые файлы, расплодившиеся рядом.</summary>
    [Fact]
    public void LinkToExtraSubtype_LeavesExactlyOnePhysicalFile()
    {
        var targets = AllTargets();
        var manuf = _db.GetParamManufacturers().First();
        var primaryTarget = targets.First(t => t.GroupName == "ПЖ");
        var extraTarget = targets.First(t => t.GroupName != primaryTarget.GroupName);

        var folder = _hierarchy.ParamsPath(Root, primaryTarget.GroupName, primaryTarget.Subtype.Name, manuf);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "params.par"), "parameters");
        var record = new ParamFile
        {
            SubtypeId = primaryTarget.Id, Manufacturer = manuf, Filename = "params.par", DiskPath = folder,
            UploadDate = "2026-08-03 10:00:00",
        };
        record.Id = _db.AddParamFile(record);

        var shortcuts = new FakeShortcuts();
        var result = ParamFileLinkService.LinkToExtraSubtypes(_db, _hierarchy, Root, primaryTarget.Id,
            record, new[] { extraTarget }, shortcuts);

        Assert.Empty(result.Warnings);
        Assert.Single(result.CreatedIds);
        Assert.Single(Directory.GetFiles(Root, "params.par", SearchOption.AllDirectories));
        Assert.Single(Directory.GetFiles(Root, "*.lnk", SearchOption.AllDirectories));
        // Записей две (по одной на подтип), а файл — один.
        Assert.Equal(2, ParamFileLinkService.CurrentLinks(_db, record).Count);
    }

    /// <summary>Полная копия файла, уже лежащая в папке дополнительного подтипа (её мог оставить
    /// старый клиент, ручное «сохранить как» или разрешение конфликта облачной синхронизацией
    /// диска), убирается при привязке — остаётся один файл и ярлык.</summary>
    [Fact]
    public void LinkToExtraSubtype_RemovesIdenticalStrayCopy()
    {
        var targets = AllTargets();
        var manuf = _db.GetParamManufacturers().First();
        var primaryTarget = targets.First(t => t.GroupName == "ПЖ");
        var extraTarget = targets.First(t => t.GroupName != primaryTarget.GroupName);

        var folder = _hierarchy.ParamsPath(Root, primaryTarget.GroupName, primaryTarget.Subtype.Name, manuf);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "params.par"), "parameters");

        var extraFolder = _hierarchy.ParamsPath(Root, extraTarget.GroupName, extraTarget.Subtype.Name, manuf);
        Directory.CreateDirectory(extraFolder);
        File.WriteAllText(Path.Combine(extraFolder, "params.par"), "parameters"); // та самая лишняя копия

        var record = new ParamFile
        {
            SubtypeId = primaryTarget.Id, Manufacturer = manuf, Filename = "params.par", DiskPath = folder,
            UploadDate = "2026-08-03 10:00:00",
        };
        record.Id = _db.AddParamFile(record);

        var result = ParamFileLinkService.LinkToExtraSubtypes(_db, _hierarchy, Root, primaryTarget.Id,
            record, new[] { extraTarget }, new FakeShortcuts());

        Assert.Empty(result.Warnings);
        Assert.Single(Directory.GetFiles(Root, "params.par", SearchOption.AllDirectories));
    }

    /// <summary>А вот ОТЛИЧАЮЩИЙСЯ одноимённый файл не удаляется никогда — только предупреждение.
    /// Ничего с диска не сносится, пока не доказано, что это ровно та же копия.</summary>
    [Fact]
    public void LinkToExtraSubtype_KeepsDifferentFileAndWarns()
    {
        var targets = AllTargets();
        var manuf = _db.GetParamManufacturers().First();
        var primaryTarget = targets.First(t => t.GroupName == "ПЖ");
        var extraTarget = targets.First(t => t.GroupName != primaryTarget.GroupName);

        var folder = _hierarchy.ParamsPath(Root, primaryTarget.GroupName, primaryTarget.Subtype.Name, manuf);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "params.par"), "parameters");

        var extraFolder = _hierarchy.ParamsPath(Root, extraTarget.GroupName, extraTarget.Subtype.Name, manuf);
        Directory.CreateDirectory(extraFolder);
        File.WriteAllText(Path.Combine(extraFolder, "params.par"), "ЧУЖИЕ параметры, не копия");

        var record = new ParamFile
        {
            SubtypeId = primaryTarget.Id, Manufacturer = manuf, Filename = "params.par", DiskPath = folder,
            UploadDate = "2026-08-03 10:00:00",
        };
        record.Id = _db.AddParamFile(record);

        var result = ParamFileLinkService.LinkToExtraSubtypes(_db, _hierarchy, Root, primaryTarget.Id,
            record, new[] { extraTarget }, new FakeShortcuts());

        Assert.Single(result.Warnings);
        Assert.Equal(2, Directory.GetFiles(Root, "params.par", SearchOption.AllDirectories).Length);
        Assert.Equal("ЧУЖИЕ параметры, не копия", File.ReadAllText(Path.Combine(extraFolder, "params.par")));
    }

    // ── Разовая чистка уже наплодившихся двойников ──────────────────────────────────────────

    /// <summary>Двойник «имя (что-то).ext», побайтово совпадающий с основным файлом, удаляется, а
    /// его запись в БД архивируется. Отличающийся файл и сохранённая прежняя редакция «имя (до …)»
    /// остаются на месте — их трогать нельзя.</summary>
    [Fact]
    public void CleanAll_RemovesOnlyProvenCopies()
    {
        var target = AllTargets().First(t => t.GroupName == "ПЖ");
        var manuf = _db.GetParamManufacturers().First();
        var folder = _hierarchy.ParamsPath(Root, target.GroupName, target.Subtype.Name, manuf);
        Directory.CreateDirectory(folder);

        File.WriteAllText(Path.Combine(folder, "параметры X2.par"), "содержимое");
        File.WriteAllText(Path.Combine(folder, "параметры X2 (XL).par"), "содержимое");       // точная копия
        File.WriteAllText(Path.Combine(folder, "параметры X2 (черновик).par"), "другое");     // НЕ копия
        File.WriteAllText(Path.Combine(folder, "параметры X2 (до 2026-07-01).par"), "содержимое"); // прежняя редакция

        var mainId = _db.AddParamFile(new ParamFile
        {
            SubtypeId = target.Id, Manufacturer = manuf, Filename = "параметры X2.par",
            DiskPath = folder, UploadDate = "2026-08-03 10:00:00",
        });
        var dupId = _db.AddParamFile(new ParamFile
        {
            SubtypeId = target.Id, Manufacturer = manuf, Filename = "параметры X2 (XL).par",
            DiskPath = folder, UploadDate = "2026-08-03 10:00:00",
        });

        var result = ParamFileDuplicateCleanup.CleanAll(_db, Path.Combine(Root, "Параметры"));

        Assert.Single(result.Removed);
        Assert.False(File.Exists(Path.Combine(folder, "параметры X2 (XL).par")));
        // Всё сомнительное осталось на диске.
        Assert.True(File.Exists(Path.Combine(folder, "параметры X2.par")));
        Assert.True(File.Exists(Path.Combine(folder, "параметры X2 (черновик).par")));
        // Прежняя редакция не удалена — она переехала в подпапку (см. TidyArchives): удалять её
        // нельзя никогда, но и лежать рядом с актуальным файлом она больше не должна.
        Assert.False(File.Exists(Path.Combine(folder, "параметры X2 (до 2026-07-01).par")));
        Assert.True(File.Exists(Path.Combine(folder, ParamFileUploadService.ArchiveFolderName, "параметры X2 (до 2026-07-01).par")));
        Assert.Single(result.Tidied);
        Assert.Contains(result.Skipped, s => s.Contains("черновик"));

        var live = _db.GetParamFiles(target.Id).Select(f => f.Id).ToList();
        Assert.Contains(mainId, live);
        Assert.DoesNotContain(dupId, live); // запись двойника архивирована
    }

    /// <summary>Повторный прогон чистки ничего не делает — операция идемпотентна.</summary>
    [Fact]
    public void CleanAll_IsIdempotent()
    {
        var target = AllTargets().First(t => t.GroupName == "ПЖ");
        var manuf = _db.GetParamManufacturers().First();
        var folder = _hierarchy.ParamsPath(Root, target.GroupName, target.Subtype.Name, manuf);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "p.par"), "x");
        File.WriteAllText(Path.Combine(folder, "p (2).par"), "x");
        _db.AddParamFile(new ParamFile
        {
            SubtypeId = target.Id, Manufacturer = manuf, Filename = "p.par",
            DiskPath = folder, UploadDate = "2026-08-03 10:00:00",
        });

        Assert.Single(ParamFileDuplicateCleanup.CleanAll(_db).Removed);
        Assert.Empty(ParamFileDuplicateCleanup.CleanAll(_db).Removed);
    }
}
