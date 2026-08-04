using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Шаблоны паспортов шкафов: раскладка на диске, перезаливка и поиск.
///
/// Что просил владелец дословно: «чтобы можно было шаблоны паспортов на печать прикреплять; есть
/// некоторые шкафы без прошивок, для которых только паспорт, а есть и прошивка и паспорт — по типу
/// ПЖ ПИ». Отсюда два требования, которые тут и проверяются: паспорт привязан к ШКАФУ (тип+подтип), а
/// не к версии прошивки, и шкаф, у которого прошивки нет вовсе, обязан находиться поиском по одному
/// только паспорту.</summary>
public class PassportTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public PassportTests()
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

    private (int Id, string GroupName, string SubtypeName) PickSubtype(string groupName = "ПЖ")
    {
        var group = _db.GetAllEquipmentGroups().First(g => g.Name == groupName);
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name != "—");
        return (subtype.Id!.Value, group.Name, subtype.Name);
    }

    private (int Id, string Folder) UploadPassport(string name, string filename, string content, DateTime when,
        string description = "")
    {
        var (subtypeId, groupName, subtypeName) = PickSubtype();
        var folder = PassportService.Folder(_hierarchy, Root, groupName, subtypeName, name);
        Directory.CreateDirectory(folder);
        var archived = ParamFileUploadService.ArchivePreviousOnDisk(folder, filename, when);
        File.WriteAllText(Path.Combine(folder, filename), content);

        var outcome = PassportService.SaveRecord(_db, new PassportTemplate
        {
            SubtypeId = subtypeId,
            Name = name,
            Filename = filename,
            DiskPath = folder,
            Description = description,
        }, archived, when);
        return (outcome.RecordId, folder);
    }

    // ── Раскладка на диске ──────────────────────────────────────────────────────────────────

    /// <summary>Папка паспортов принадлежит ТИПУ/ПОДТИПУ шкафа и лежит рядом с «ОПЦ», а не внутри
    /// папки контроллера: у шкафа без прошивки папки контроллера может не быть вовсе. Создаётся
    /// обходом EnsureStructure, как остальные служебные папки.</summary>
    [Fact]
    public void PassportsFolder_BelongsToTheSubtype_AndIsCreatedByEnsureStructure()
    {
        var (_, groupName, subtypeName) = PickSubtype();

        Assert.Equal(Path.Combine(Root, "ПО", groupName, subtypeName, HierarchyFolders.Passports),
            _hierarchy.PassportsPath(Root, groupName, subtypeName));
        Assert.True(Directory.Exists(_hierarchy.PassportsPath(Root, groupName, subtypeName)));

        // У подтипа-заглушки «—» своего сегмента в дереве нет — папка стоит у самого типа.
        Assert.Equal(Path.Combine(Root, "ПО", groupName, HierarchyFolders.Passports),
            _hierarchy.PassportsPath(Root, groupName, "—"));
    }

    /// <summary>Название паспорта становится именем подпапки: недопустимые для файловой системы
    /// символы заменяются, точка на конце убирается (Windows её всё равно отбрасывает, и записанный
    /// в БД путь разошёлся бы с реальным), пустое название превращается в «Паспорт».</summary>
    [Fact]
    public void FolderName_SanitizesTheOperatorsName()
    {
        Assert.Equal("Паспорт ПЖ_ПИ", PassportService.FolderName("Паспорт ПЖ/ПИ"));
        Assert.Equal("Паспорт 2 насоса", PassportService.FolderName("  Паспорт 2 насоса.  "));
        Assert.Equal(HierarchyFolders.Passports, PassportService.FolderName("   "));
    }

    /// <summary>Два паспорта одного подтипа (разные исполнения шкафа) живут в разных подпапках и не
    /// затирают друг друга, даже когда файл в обоих называется одинаково.</summary>
    [Fact]
    public void TwoPassportsOfOneSubtype_DoNotShareAFolder()
    {
        var (subtypeId, _, _) = PickSubtype();
        var one = UploadPassport("2 насоса", "Паспорт.docx", "два насоса", new DateTime(2026, 8, 1, 10, 0, 0));
        var two = UploadPassport("2 насоса + жокей", "Паспорт.docx", "с жокеем", new DateTime(2026, 8, 1, 10, 5, 0));

        Assert.NotEqual(one.Folder, two.Folder);
        Assert.Equal("два насоса", File.ReadAllText(Path.Combine(one.Folder, "Паспорт.docx")));
        Assert.Equal("с жокеем", File.ReadAllText(Path.Combine(two.Folder, "Паспорт.docx")));
        Assert.Equal(2, _db.GetPassports(subtypeId).Count);
    }

    // ── Перезаливка ─────────────────────────────────────────────────────────────────────────

    /// <summary>Перезаливка под тем же названием ОБНОВЛЯЕТ запись, а не плодит вторую: прежняя
    /// редакция уезжает в подпапку, дата освежается, описание ведёт датированный журнал изменений —
    /// ровно то же правило, что у файлов параметров.</summary>
    [Fact]
    public void Reupload_UpdatesTheRecord_AndKeepsThePreviousEdition()
    {
        var (subtypeId, _, _) = PickSubtype();
        var first = UploadPassport("Паспорт ПЖ ПИ", "Паспорт.docx", "редакция 1",
            new DateTime(2026, 7, 1, 9, 0, 0), "исходный шаблон");
        var second = UploadPassport("Паспорт ПЖ ПИ", "Паспорт.docx", "редакция 2",
            new DateTime(2026, 8, 4, 12, 0, 0), "добавил графу по насосам");

        Assert.Equal(first.Id, second.Id);
        var row = Assert.Single(_db.GetPassports(subtypeId));
        Assert.Equal("2026-08-04 12:00:00", row.UploadDate);

        Assert.Equal("редакция 2", File.ReadAllText(Path.Combine(second.Folder, "Паспорт.docx")));
        Assert.Equal("редакция 1", File.ReadAllText(Path.Combine(second.Folder,
            ParamFileUploadService.ArchiveFolderName, "Паспорт (до 2026-08-04).docx")));

        Assert.Contains("исходный шаблон", row.Description);
        Assert.Contains("[2026-08-04]", row.Description);
        Assert.Contains("добавил графу по насосам", row.Description);
    }

    /// <summary>Открывается всегда АКТУАЛЬНАЯ редакция, даже если у убранной в подпапку время
    /// изменения оказалось свежее. Так бывает сплошь и рядом: файл копируется на диск со своей датой
    /// правки, а не с датой загрузки, — и без явного исключения подпапки резолвер документа
    /// (общий с инструкцией) вернул бы прежнюю редакцию.</summary>
    [Fact]
    public void ResolveDoc_IgnoresArchivedEditions_EvenWhenTheyLookFresher()
    {
        UploadPassport("Паспорт ПЖ ПИ", "Паспорт.docx", "редакция 1", new DateTime(2026, 7, 1, 9, 0, 0));
        var (_, folder) = UploadPassport("Паспорт ПЖ ПИ", "Паспорт.docx", "редакция 2", new DateTime(2026, 8, 4, 12, 0, 0));

        var archived = Path.Combine(folder, ParamFileUploadService.ArchiveFolderName, "Паспорт (до 2026-08-04).docx");
        File.SetLastWriteTimeUtc(archived, DateTime.UtcNow.AddDays(1));

        var row = _db.GetPassports().Single(p => p.Name == "Паспорт ПЖ ПИ");
        var doc = PassportService.ResolveDoc(row, Root);

        Assert.Equal(Path.Combine(folder, "Паспорт.docx"), doc.Docx);
        Assert.Equal(Path.Combine(folder, "Паспорт.docx"), doc.Newest);
        // PDF рядом ещё не собран — печать обязана его сделать.
        Assert.Null(doc.Pdf);
        Assert.True(doc.PdfStale);
        Assert.Equal(Path.Combine(folder, "Паспорт.pdf"), doc.ExpectedPdfPath);
    }

    /// <summary>Загруженный сразу в PDF паспорт печатается как есть: собирать нечего, устаревшим он
    /// не считается.</summary>
    [Fact]
    public void ResolveDoc_PdfOnlyPassport_IsReadyToPrint()
    {
        var (_, folder) = UploadPassport("Паспорт ПИ", "Паспорт.pdf", "%PDF-1.4", new DateTime(2026, 8, 4, 12, 0, 0));
        var doc = PassportService.ResolveDoc(_db.GetPassports().Single(p => p.Name == "Паспорт ПИ"), Root);

        Assert.Equal(Path.Combine(folder, "Паспорт.pdf"), doc.Pdf);
        Assert.False(doc.PdfStale);
        Assert.True(doc.CanPrint);
    }

    // ── Поиск ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Шкаф, у которого прошивки нет вовсе, находится по паспорту — по названию, по тегу (в
    /// теги как раз и пишут названия конкретных шкафов) и по типу/подтипу. Ради этого паспорт и
    /// сделан самостоятельной записью, а не вложением версии прошивки.</summary>
    [Fact]
    public void Search_FindsCabinetThatHasNoFirmwareAtAll()
    {
        var (subtypeId, groupName, subtypeName) = PickSubtype();
        var id = _db.AddPassport(new PassportTemplate
        {
            SubtypeId = subtypeId, Name = "Паспорт ПЖ ПИ", Filename = "Паспорт.docx",
            DiskPath = @"Z:\Antarus\ПО\ПЖ\ХП\Паспорт\Паспорт ПЖ ПИ", UploadDate = "2026-08-04 12:00:00",
        });
        _db.UpdatePassportTags(id, "ЩУН-3 ЩУН-4");

        // Прошивок у этого шкафа нет ни одной — найтись он может только паспортом.
        Assert.Empty(_db.SearchFwVersionsByTokens(new[] { "ЩУН-3" }));

        Assert.Single(_db.SearchPassportsByTokens(new[] { "ЩУН-4" }));
        Assert.Single(_db.SearchPassportsByTokens(new[] { "ПАСПОРТ" }));
        Assert.Single(_db.SearchPassportsByTokens(new[] { subtypeName.ToUpperInvariant() }));
        Assert.Single(_db.SearchPassportsByTokens(new[] { groupName.ToUpperInvariant() }));
        Assert.Empty(_db.SearchPassportsByTokens(new[] { "ЩУН-9" }));
    }

    /// <summary>Совпадение по тегу весит вдвое против совпадения по названию — та же схема, что у
    /// поиска прошивок и файлов параметров: тег оператор ставит осмысленно.</summary>
    [Fact]
    public void Search_RanksTagMatchAboveNameMatch()
    {
        var (subtypeId, _, _) = PickSubtype();
        _db.AddPassport(new PassportTemplate
        {
            SubtypeId = subtypeId, Name = "ЩУН-3 общий", Filename = "Паспорт.docx",
            DiskPath = @"Z:\1", UploadDate = "2026-08-04 10:00:00",
        });
        var tagged = _db.AddPassport(new PassportTemplate
        {
            SubtypeId = subtypeId, Name = "Насосная станция", Filename = "Паспорт.docx",
            DiskPath = @"Z:\2", UploadDate = "2026-08-04 10:00:00", Tags = "ЩУН-3",
        });

        var hits = _db.SearchPassportsByTokens(new[] { "ЩУН-3" });
        Assert.Equal(2, hits.Count);
        Assert.Equal(tagged, hits[0].Id);
    }

    /// <summary>Снятая запись (архивация — мягкое удаление, файл на диске цел) исчезает и из поиска,
    /// и из признака «у этого шкафа есть паспорт» на карточке прошивки.</summary>
    [Fact]
    public void ArchivedPassport_LeavesSearchAndTheFirmwareCardHint()
    {
        var (subtypeId, _, _) = PickSubtype();
        var id = _db.AddPassport(new PassportTemplate
        {
            SubtypeId = subtypeId, Name = "Паспорт ПЖ ПИ", Filename = "Паспорт.docx",
            DiskPath = @"Z:\Antarus", UploadDate = "2026-08-04 12:00:00",
        });
        Assert.Contains(subtypeId, _db.GetSubtypeIdsWithPassports());

        _db.DeletePassport(id);

        Assert.Empty(_db.SearchPassportsByTokens(new[] { "ПАСПОРТ" }));
        Assert.DoesNotContain(subtypeId, _db.GetSubtypeIdsWithPassports());
        Assert.Empty(_db.GetPassports(subtypeId));
    }

    // ── Переезд диска ───────────────────────────────────────────────────────────────────────

    /// <summary>Паспорта лежат в дереве ПО, поэтому переименование папки типа/подтипа (и смена корня)
    /// обязано двигать и их путь — иначе «Открыть» ведёт в никуда, как это было с прошивками.</summary>
    [Fact]
    public void RemapPathPrefix_MovesPassportPathsToo()
    {
        var (subtypeId, _, _) = PickSubtype();
        _db.AddPassport(new PassportTemplate
        {
            SubtypeId = subtypeId, Name = "Паспорт ПЖ ПИ", Filename = "Паспорт.docx",
            DiskPath = @"Z:\Antarus\ПО\ПЖ\ХП\Паспорт\Паспорт ПЖ ПИ", UploadDate = "2026-08-04 12:00:00",
        });

        Assert.True(_db.RemapPathPrefix(@"Z:\Antarus\ПО\ПЖ\ХП", @"Z:\Antarus\ПО\ПЖ\Хозпитьевая") > 0);

        Assert.Equal(@"Z:\Antarus\ПО\ПЖ\Хозпитьевая\Паспорт\Паспорт ПЖ ПИ",
            _db.GetPassports(subtypeId).Single().DiskPath);
    }
}
