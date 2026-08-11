using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Доп. материалы к прошивке — свободный список файлов у версии: краткое руководство для
/// наладчика, специфика работы объекта, прошивка ПЛК от поставщика (см. FwAttachment и
/// FirmwareExtraFilesService). Здесь проверяется то, что видно на диске и в поиске: куда ложится файл
/// у перестроенной и у неперестроенной версии, что одноимённый не затирается молча, и что вид с
/// комментарием реально находятся поиском — ради этого их и заполняют.</summary>
public class FirmwareExtraFilesTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public FirmwareExtraFilesTests()
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

    private (FwVersionRecord Record, string VersionDir, string GroupName, string SubtypeName, string ControllerName)
        SeedUploadedVersion()
    {
        var group = _db.GetAllEquipmentGroups().Single(g => g.Name == "ТГР");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).Single();
        var mod = _db.GetAllModifications().Single(m => m.ControllerName == "SMH5" && m.DisplayName == "SMH5");

        var src = Path.Combine(Root, "source.psl");
        File.WriteAllText(src, "dummy");
        var upload = FirmwareUploadService.Upload(_db, _hierarchy, new FirmwareUploadRequest
        {
            SourcePath = src,
            Group = group,
            Subtype = subtype,
            Modification = mod,
            LaunchTypes = new() { "УПП" },
            Description = "первая загрузка",
            IncludeDateInVersion = false,
            RootPath = Root,
            AuthorUserName = "tester",
        });
        Assert.Equal(FirmwareUploadOutcome.Success, upload.Outcome);

        return (upload.Record!, upload.DestinationFolder!, group.Name, subtype.Name, mod.ControllerName);
    }

    private string WriteSourceFile(string name, string content = "содержимое")
    {
        var path = Path.Combine(Root, "источники", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private FirmwareExtraFilesResult AddExtra(FwVersionRecord record, string groupName, string subtypeName,
        string controllerName, string sourcePath, string kind, string comment) =>
        FirmwareExtraFilesService.Apply(_db, record, Root, groupName, subtypeName, controllerName, "tester",
            added: new[] { new FirmwareExtraFileAdd(sourcePath, kind, comment) });

    /// <summary>Версия ещё не перестроена — материал ложится в общую папку контроллера, ровно как
    /// инструкция и карты (иначе коллега со старым клиентом файла не увидел бы вовсе).</summary>
    [Fact]
    public void Add_OnOldLayout_GoesIntoControllerFolder()
    {
        var (record, _, g, s, c) = SeedUploadedVersion();
        var src = WriteSourceFile("руководство.pdf");

        var result = AddExtra(record, g, s, c, src, FwAttachmentKinds.SetupGuide, "краткое руководство для наладчика");

        Assert.Empty(result.Warnings);
        var attachment = Assert.Single(_db.GetFwAttachments(record.Id!.Value));
        var expectedFolder = Path.Combine(HierarchyService.GroupSubFolder(Root, g, s), c, VersionLayout.ExtrasFolderName);
        Assert.Equal(Path.Combine(expectedFolder, "руководство.pdf"), attachment.DiskPath);
        Assert.True(File.Exists(attachment.DiskPath));
        Assert.Equal(FwAttachmentKinds.SetupGuide, attachment.Kind);
        Assert.Equal("краткое руководство для наладчика", attachment.Comment);
    }

    /// <summary>Версия перестроена — материал ложится в её собственную папку «Доп. материалы», рядом
    /// с «Инструкция» и «HMI». Развилку целиком держит VersionLayout, отдельного флага здесь нет.</summary>
    [Fact]
    public void Add_OnNewLayout_GoesIntoVersionFolder()
    {
        var (record, versionDir, g, s, c) = SeedUploadedVersion();
        VersionLayout.EnsureFolders(versionDir);
        var src = WriteSourceFile("специфика.docx");

        AddExtra(record, g, s, c, src, FwAttachmentKinds.WorkSpecifics, "объект с двумя вводами");

        var attachment = Assert.Single(_db.GetFwAttachments(record.Id!.Value));
        Assert.Equal(Path.Combine(versionDir, VersionLayout.ExtrasFolderName, "специфика.docx"), attachment.DiskPath);
        Assert.True(File.Exists(attachment.DiskPath));
    }

    /// <summary>Одноимённый файл НЕ затирается молча: у второго к имени приписывается « (2)», и оба
    /// остаются на диске со своим содержимым. У неперестроенной версии папка доп. материалов общая на
    /// весь контроллер, так что совпадение имён там — обычное дело, а не экзотика.</summary>
    [Fact]
    public void Add_SameFilename_DoesNotOverwriteExisting()
    {
        var (record, _, g, s, c) = SeedUploadedVersion();
        var first = WriteSourceFile(Path.Combine("первый", "руководство.pdf"), "первый файл");
        var second = WriteSourceFile(Path.Combine("второй", "руководство.pdf"), "второй файл");

        AddExtra(record, g, s, c, first, FwAttachmentKinds.SetupGuide, "первое");
        AddExtra(record, g, s, c, second, FwAttachmentKinds.SetupGuide, "второе");

        var attachments = _db.GetFwAttachments(record.Id!.Value);
        Assert.Equal(2, attachments.Count);
        Assert.Equal("руководство.pdf", attachments[0].Filename);
        Assert.Equal("руководство (2).pdf", attachments[1].Filename);
        Assert.Equal("первый файл", File.ReadAllText(attachments[0].DiskPath));
        Assert.Equal("второй файл", File.ReadAllText(attachments[1].DiskPath));
    }

    /// <summary>Оператор выбрал файл, который УЖЕ лежит в папке доп. материалов (диалог открывается
    /// там же) — копировать нечего, второй копии заводиться не должно. File.Copy сам в себя Windows
    /// отвергает как «файл занят другим процессом» — на этом уже обжигалась модерация.</summary>
    [Fact]
    public void Add_FileAlreadyInPlace_DoesNotDuplicateOrThrow()
    {
        var (record, versionDir, g, s, c) = SeedUploadedVersion();
        VersionLayout.EnsureFolders(versionDir);
        var folder = Path.Combine(versionDir, VersionLayout.ExtrasFolderName);
        Directory.CreateDirectory(folder);
        var inPlace = Path.Combine(folder, "поставщик.zip");
        File.WriteAllText(inPlace, "прошивка ПЛК поставщика");

        var result = AddExtra(record, g, s, c, inPlace, FwAttachmentKinds.VendorPlcFirmware, "от поставщика");

        Assert.Empty(result.Warnings);
        var attachment = Assert.Single(_db.GetFwAttachments(record.Id!.Value));
        Assert.Equal(inPlace, attachment.DiskPath);
        Assert.Single(Directory.GetFiles(folder));
    }

    /// <summary>Снятие вложения: запись уходит тумбстоуном (иначе удаление не доехало бы до коллег),
    /// файл с диска убирается.</summary>
    [Fact]
    public void Remove_TombstonesRow_AndDeletesFile()
    {
        var (record, _, g, s, c) = SeedUploadedVersion();
        AddExtra(record, g, s, c, WriteSourceFile("лишнее.txt"), FwAttachmentKinds.Other, "ошиблись файлом");
        var attachment = Assert.Single(_db.GetFwAttachments(record.Id!.Value));

        FirmwareExtraFilesService.Apply(_db, record, Root, g, s, c, "tester",
            removedIds: new[] { attachment.Id!.Value });

        Assert.Empty(_db.GetFwAttachments(record.Id!.Value));
        Assert.False(File.Exists(attachment.DiskPath));
        // Строка осталась — снятие обязано продолжить путь к коллегам как положительный сигнал.
        Assert.NotEmpty(_db.GetFwAttachment(attachment.Id!.Value)!.DeletedAt);
    }

    /// <summary>Тот же файл приложен к двум версиям (общая папка контроллера у неперестроенной
    /// раскладки) — снятие у одной не должно уносить файл у другой.</summary>
    [Fact]
    public void Remove_KeepsFileWhenAnotherAttachmentUsesIt()
    {
        var (record, _, g, s, c) = SeedUploadedVersion();
        AddExtra(record, g, s, c, WriteSourceFile("общее.pdf"), FwAttachmentKinds.SetupGuide, "первая");
        var first = Assert.Single(_db.GetFwAttachments(record.Id!.Value));

        // Вторая запись на тот же файл — так выглядит вложение соседней версии контроллера.
        _db.AddFwAttachment(new FwAttachment
        {
            FwVersionId = record.Id!.Value,
            Filename = first.Filename,
            DiskPath = first.DiskPath,
            Kind = FwAttachmentKinds.SetupGuide,
            Comment = "вторая ссылка на тот же файл",
        });

        FirmwareExtraFilesService.Apply(_db, record, Root, g, s, c, "tester",
            removedIds: new[] { first.Id!.Value });

        Assert.True(File.Exists(first.DiskPath));
    }

    /// <summary>Ради чего всё и делалось: «краткое руководство для наладчика» находится поиском по
    /// словам из комментария и из вида, а не только по тегам прошивки.</summary>
    [Fact]
    public void Search_FindsVersionByAttachmentCommentAndKind()
    {
        var (record, _, g, s, c) = SeedUploadedVersion();
        Assert.Empty(SearchService.Search(_db, "руководство наладчика"));

        AddExtra(record, g, s, c, WriteSourceFile("руководство.pdf"),
            FwAttachmentKinds.SetupGuide, "краткое руководство для наладчика по пусконаладке");

        var byComment = SearchService.Search(_db, "пусконаладке");
        Assert.Contains(byComment, r => r.FwVersionId == record.Id!.Value);

        var byKind = SearchService.Search(_db, "руководство наладчика");
        Assert.Contains(byKind, r => r.FwVersionId == record.Id!.Value);
    }

    /// <summary>Снятое вложение из поиска исчезает — иначе версия продолжала бы находиться по словам
    /// файла, которого у неё уже нет.</summary>
    [Fact]
    public void Search_IgnoresRemovedAttachments()
    {
        var (record, _, g, s, c) = SeedUploadedVersion();
        AddExtra(record, g, s, c, WriteSourceFile("алгоритм.pdf"), FwAttachmentKinds.VendorPlcFirmware,
            "внутренний алгоритм поставщика");
        Assert.NotEmpty(SearchService.Search(_db, "алгоритм поставщика"));

        var attachment = Assert.Single(_db.GetFwAttachments(record.Id!.Value));
        FirmwareExtraFilesService.Apply(_db, record, Root, g, s, c, "tester",
            removedIds: new[] { attachment.Id!.Value });

        Assert.Empty(SearchService.Search(_db, "алгоритм поставщика"));
    }

    /// <summary>Справочник видов заполняется при первом запуске (сид «пока таблица пуста» до
    /// установленных копий не доезжает, поэтому это разовая миграция), а свой вид администратор
    /// добавляет сам — в том числе прямо из карточки, вписав его в список.</summary>
    [Fact]
    public void AttachmentKinds_SeededOnce_AndAcceptCustomOnes()
    {
        Assert.Equal(FwAttachmentKinds.Defaults, _db.GetFwAttachmentKinds().ToArray());

        _db.AddFwAttachmentKind("Схема объекта");
        Assert.Contains("Схема объекта", _db.GetFwAttachmentKinds());

        // Кириллический регистр: SQLite COLLATE NOCASE сворачивает только латиницу, поэтому «прочее»
        // рядом с «Прочее» завелось бы второй строкой — и любой словарь по именам с
        // OrdinalIgnoreCase (ImportFlatList) упал бы на дубликате ключа.
        _db.AddFwAttachmentKind("прочее");
        Assert.Equal(FwAttachmentKinds.Defaults.Length + 1, _db.GetFwAttachmentKinds().Count);
    }

    /// <summary>Кириллический регистр при удалении вида. «Схема объекта» и «схема объекта» для SQLite
    /// разные строки (COLLATE NOCASE сворачивает только латиницу), поэтому удаление вторым написанием
    /// раньше и вид бы не убрало, и завело бы в flat_list_state ВТОРУЮ отметку про тот же вид — а
    /// импорт строит по отметкам словарь с OrdinalIgnoreCase и упал бы на дубликате ключа.</summary>
    [Fact]
    public void DeleteKind_MatchesCyrillicCase_AndKeepsSingleStateRow()
    {
        _db.AddFwAttachmentKind("Схема объекта");

        _db.DeleteFwAttachmentKind("схема объекта");

        Assert.DoesNotContain("Схема объекта", _db.GetFwAttachmentKinds());
        Assert.Single(_db.GetFlatListState().Where(s => s.Kind == Database.FlatKindAttachmentKind
            && string.Equals(s.Name, "Схема объекта", StringComparison.OrdinalIgnoreCase)));
        // И импорт снимка после этого проходит, а не падает на построении словаря отметок.
        _db.ImportHierarchyData(_db.ExportHierarchyData());
    }

    /// <summary>Удаление вида из справочника НЕ стирает вид у уже приложенных файлов: подпись «что
    /// это за файл» ценнее чистоты справочника (в отличие от тега, который вычищается везде).</summary>
    [Fact]
    public void DeleteKind_KeepsKindOnExistingAttachments()
    {
        var (record, _, g, s, c) = SeedUploadedVersion();
        AddExtra(record, g, s, c, WriteSourceFile("своё.pdf"), "Схема объекта", "однолинейная");

        _db.DeleteFwAttachmentKind("Схема объекта");

        Assert.DoesNotContain("Схема объекта", _db.GetFwAttachmentKinds());
        Assert.Equal("Схема объекта", Assert.Single(_db.GetFwAttachments(record.Id!.Value)).Kind);
    }
}
