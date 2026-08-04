using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Типовые паспорта — бланки, которые не ложатся ни на один тип и подтип шкафа.
///
/// Что просил владелец дословно: «шаблоны паспортов чаще всего не попадают под категории типов и
/// подтипов: есть НКУ, есть Щит СПЛ, есть ШР — по факту это та же папка с наклейками, только там
/// паспорт, к которому можно подцепить тег с названием для поиска». Отсюда устройство: у записи нет
/// подтипа, файл лежит в ОБЩЕЙ папке диска (рядом с наклейками), а находится бланк по тегам — в них
/// как раз и пишут названия конкретных шкафов.
///
/// Всё остальное у него ровно то же, что у паспорта, привязанного к шкафу: перезаливка обновляет
/// запись, архивация — мягкое удаление, теги ищутся. Здесь проверяется, что «нет подтипа» нигде не
/// прочиталось как «подтип потерялся» — это и есть единственное место, где типовой бланк отличается
/// от обычного паспорта (см. PassportTests про привязанные к шкафу).</summary>
public class GeneralPassportTests : IDisposable
{
    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly ConfigService _cfg;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public GeneralPassportTests()
    {
        _db = new Database(_dbFile.Path);
        _cfg = new ConfigService(_db);
        _hierarchy = new HierarchyService(_db);
        _hierarchy.EnsureStructure(Root);
        _cfg.SetRootPath(Root);
    }

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
        _tempRoot.Dispose();
    }

    private string TemplatesFolder() => PassportService.TemplatesFolder(Root, _cfg.PassportTemplatesFolder())!;

    /// <summary>Загрузка типового бланка так, как её делает страница «Паспорта шкафов» с отмеченным
    /// флажком «типовой»: файл в общую папку, запись без подтипа.</summary>
    private (int Id, string Folder) UploadGeneral(string name, string filename, string content, DateTime when,
        string description = "")
    {
        var folder = PassportService.GeneralFolder(TemplatesFolder(), name);
        Directory.CreateDirectory(folder);
        var archived = ParamFileUploadService.ArchivePreviousOnDisk(folder, filename, when);
        File.WriteAllText(Path.Combine(folder, filename), content);

        var outcome = PassportService.SaveRecord(_db, new PassportTemplate
        {
            SubtypeId = null,
            Name = name,
            Filename = filename,
            DiskPath = folder,
            Description = description,
        }, archived, when);
        return (outcome.RecordId, folder);
    }

    private int PickSubtype()
    {
        var group = _db.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        return _db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name != "—").Id!.Value;
    }

    // ── Где лежат бланки ────────────────────────────────────────────────────────────────────

    /// <summary>По умолчанию бланки лежат в «Конфиг\Паспорта» — рядом с «Конфиг\Наклейки», по которым
    /// эта папка и списана: она уже доступна всем машинам, раздавать её отдельно не нужно. Правило
    /// разбора пути у обеих общих папок ОДНО (см. SharedFolderPath), и разъехаться оно не должно.</summary>
    [Fact]
    public void TemplatesFolder_DefaultsToConfigPassports_ByTheSameRuleAsStickers()
    {
        Assert.Equal(Path.Combine(Root, @"Конфиг\Паспорта"), PassportService.TemplatesFolder(Root, ""));
        Assert.Equal(Path.Combine(Root, @"Конфиг\Наклейки"), StickerTemplates.FolderFor(Root, ""));

        // Относительный путь — от корня диска: буква шары у каждой машины своя, а путь всё равно сойдётся.
        Assert.Equal(Path.Combine(Root, @"Общее\Бланки"), PassportService.TemplatesFolder(Root, @"Общее\Бланки"));

        // Абсолютный — как есть: администратор мог увести бланки на совсем другой ресурс.
        Assert.Equal(@"\\server\share\Бланки", PassportService.TemplatesFolder(Root, @"\\server\share\Бланки"));

        // Диск не настроен и путь не абсолютный — показывать нечего, и это не пустая строка, а «нет».
        Assert.Null(PassportService.TemplatesFolder(null, ""));
        Assert.Null(PassportService.TemplatesFolder("", "  "));
        Assert.Equal(@"D:\Бланки", PassportService.TemplatesFolder(null, @"D:\Бланки"));
    }

    /// <summary>У каждого бланка своя подпапка внутри общей — затем же, зачем у паспорта шкафа:
    /// рядом с документом ложится собранный из него PDF, и одноимённые «Паспорт.docx» не сталкиваются.
    /// Имя папки чистится теми же правилами, что у обычного паспорта.</summary>
    [Fact]
    public void GeneralFolder_GivesEachBlankItsOwnSubfolder()
    {
        var shared = TemplatesFolder();

        Assert.Equal(Path.Combine(shared, "НКУ"), PassportService.GeneralFolder(shared, "НКУ"));
        Assert.Equal(Path.Combine(shared, "Щит СПЛ"), PassportService.GeneralFolder(shared, "Щит СПЛ"));
        Assert.NotEqual(PassportService.GeneralFolder(shared, "НКУ"), PassportService.GeneralFolder(shared, "ШР"));
        Assert.Equal(Path.Combine(shared, "Щит СПЛ_1"), PassportService.GeneralFolder(shared, "Щит СПЛ/1"));
    }

    /// <summary>Загруженный бланк ложится в общую папку, а не внутрь «ПО»: там раскладка начинается с
    /// типа шкафа, а типа у него не бывает.</summary>
    [Fact]
    public void UploadedBlank_LandsInTheSharedFolder_WithNoSubtype()
    {
        var (id, folder) = UploadGeneral("НКУ", "Паспорт НКУ.docx", "бланк", new DateTime(2026, 8, 4, 12, 0, 0));

        Assert.Equal(Path.Combine(Root, @"Конфиг\Паспорта", "НКУ"), folder);
        Assert.False(folder.Contains(Path.Combine(Root, "ПО"), StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(folder, "Паспорт НКУ.docx")));

        var row = Assert.Single(_db.GetGeneralPassports());
        Assert.Equal(id, row.Id);
        Assert.Null(row.SubtypeId);
        Assert.Equal("НКУ", row.Name);

        // В общем списке паспортов он тоже есть — страница «Паспорта шкафов» показывает и такие.
        Assert.Contains(_db.GetPassports(), p => p.Id == id);
    }

    /// <summary>Бланк не делает вид, что у какого-то шкафа появился паспорт: подсказка «у этого шкафа
    /// есть паспорт» на карточке прошивки — про паспорта, привязанные к подтипу.</summary>
    [Fact]
    public void BlankDoesNotClaimAnyCabinetHasAPassport()
    {
        UploadGeneral("НКУ", "Паспорт.docx", "бланк", new DateTime(2026, 8, 4, 12, 0, 0));

        Assert.Empty(_db.GetSubtypeIdsWithPassports());
        Assert.Empty(_db.GetPassports(PickSubtype()));
    }

    /// <summary>Только типовые: паспорт, привязанный к шкафу, в список бланков не попадает — иначе в
    /// окне печати оказались бы все паспорта предприятия вперемешку.</summary>
    [Fact]
    public void GeneralList_HoldsOnlyBlanks_AndDropsArchivedOnes()
    {
        _db.AddPassport(new PassportTemplate
        {
            SubtypeId = PickSubtype(), Name = "Паспорт ПЖ ПИ", Filename = "Паспорт.docx",
            DiskPath = @"Z:\Antarus\ПО\ПЖ\ХП\Паспорт", UploadDate = "2026-08-04 12:00:00",
        });
        var blank = UploadGeneral("НКУ", "Паспорт.docx", "бланк", new DateTime(2026, 8, 4, 12, 0, 0));
        UploadGeneral("Щит СПЛ", "Паспорт.docx", "бланк", new DateTime(2026, 8, 4, 12, 5, 0));

        Assert.Equal(new[] { "НКУ", "Щит СПЛ" }, _db.GetGeneralPassports().Select(p => p.Name).ToArray());

        // Архивация — мягкое удаление: запись уходит из списка, файл на диске цел.
        _db.DeletePassport(blank.Id);
        Assert.Equal(new[] { "Щит СПЛ" }, _db.GetGeneralPassports().Select(p => p.Name).ToArray());
        Assert.True(File.Exists(Path.Combine(blank.Folder, "Паспорт.docx")));
    }

    // ── Перезаливка ─────────────────────────────────────────────────────────────────────────

    /// <summary>Перезаливка бланка под тем же названием ОБНОВЛЯЕТ запись, а не заводит вторую: два
    /// бланка «НКУ» — это один бланк, залитый заново. Ключ у типового тот же по смыслу, только вместо
    /// подтипа — «подтипа нет»; сравнение через «= NULL» в SQL не сработало бы никогда, и без явной
    /// ветки IS NULL каждая перезаливка плодила бы дубль.</summary>
    [Fact]
    public void Reupload_OfABlank_UpdatesTheSameRecord_AndKeepsThePreviousEdition()
    {
        var first = UploadGeneral("НКУ", "Паспорт.docx", "редакция 1", new DateTime(2026, 7, 1, 9, 0, 0), "исходный бланк");
        var second = UploadGeneral("НКУ", "Паспорт.docx", "редакция 2", new DateTime(2026, 8, 4, 12, 0, 0), "добавил графу");

        Assert.Equal(first.Id, second.Id);
        var row = Assert.Single(_db.GetGeneralPassports());
        Assert.Equal("2026-08-04 12:00:00", row.UploadDate);
        Assert.Contains("исходный бланк", row.Description);
        Assert.Contains("[2026-08-04]", row.Description);

        Assert.Equal("редакция 2", File.ReadAllText(Path.Combine(second.Folder, "Паспорт.docx")));
        Assert.Equal("редакция 1", File.ReadAllText(Path.Combine(second.Folder,
            ParamFileUploadService.ArchiveFolderName, "Паспорт (до 2026-08-04).docx")));
    }

    /// <summary>Бланк и паспорт шкафа с ОДНИМ названием — разные записи: «нет подтипа» — это половина
    /// ключа, а не пустое место, в которое подойдёт любой шкаф.</summary>
    [Fact]
    public void ABlankAndACabinetPassportWithTheSameName_StayTwoRecords()
    {
        var subtypeId = PickSubtype();
        _db.AddPassport(new PassportTemplate
        {
            SubtypeId = subtypeId, Name = "НКУ", Filename = "Паспорт.docx",
            DiskPath = @"Z:\Antarus\ПО\ПЖ\ХП\Паспорт\НКУ", UploadDate = "2026-08-01 10:00:00",
        });

        UploadGeneral("НКУ", "Паспорт.docx", "бланк", new DateTime(2026, 8, 4, 12, 0, 0));

        Assert.Equal(2, _db.GetPassports().Count(p => p.Name == "НКУ"));

        var blank = Assert.Single(_db.GetGeneralPassports());
        Assert.Equal("НКУ", blank.Name);
        Assert.Null(blank.SubtypeId);
        Assert.Equal(subtypeId, _db.GetPassports(subtypeId).Single().SubtypeId);
    }

    // ── Поиск ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Ради этого бланк и заведён записью, а не просто файлом в папке: к нему цепляют тег с
    /// названием шкафа, и дальше шкаф находится обычным поиском — как просил владелец.</summary>
    [Fact]
    public void Search_FindsABlankByItsTag_AndByItsName()
    {
        var (id, _) = UploadGeneral("НКУ", "Паспорт НКУ.docx", "бланк", new DateTime(2026, 8, 4, 12, 0, 0));
        _db.UpdatePassportTags(id, "ЩУН-5 ВРУ-1");

        Assert.Equal(id, Assert.Single(_db.SearchPassportsByTokens(new[] { "ЩУН-5" })).Id);
        Assert.Equal(id, Assert.Single(_db.SearchPassportsByTokens(new[] { "ВРУ-1" })).Id);
        Assert.Equal(id, Assert.Single(_db.SearchPassportsByTokens(new[] { "НКУ" })).Id);
        Assert.Empty(_db.SearchPassportsByTokens(new[] { "ЩУН-9" }));

        // Типа и подтипа у найденного нет — карточка результата обязана это пережить и подписать его
        // «Типовой бланк», а не начать строку с пустого разделителя (см. SearchView.MakePassportCard).
        var found = _db.SearchPassportsByTokens(new[] { "ЩУН-5" }).Single();
        Assert.Null(found.SubtypeId);
        Assert.Equal("", found.GroupName);
        Assert.Equal("", found.SubtypeName);
    }

    // ── Настройки ───────────────────────────────────────────────────────────────────────────

    /// <summary>Метка подстановки по умолчанию — «{{Название}}»; пустое поле в настройках означает
    /// «вернуть значение по умолчанию», а не «подставлять везде»: пустая метка нашлась бы в каждой
    /// точке текста бланка.</summary>
    [Fact]
    public void NamePlaceholder_FallsBackToTheDefault_WhenCleared()
    {
        Assert.Equal(DocxTemplateFiller.DefaultPlaceholder, _cfg.PassportNamePlaceholder());

        _cfg.SetPassportNamePlaceholder("<НАЗВАНИЕ ШКАФА>");
        Assert.Equal("<НАЗВАНИЕ ШКАФА>", _cfg.PassportNamePlaceholder());

        _cfg.SetPassportNamePlaceholder("   ");
        Assert.Equal(DocxTemplateFiller.DefaultPlaceholder, _cfg.PassportNamePlaceholder());
    }

    /// <summary>Папка бланков и метка — общая политика предприятия: настраиваются один раз и едут ко
    /// всем. А «какой бланк печатали в прошлый раз» — привычка конкретного наладчика, и навязывать её
    /// коллегам незачем.</summary>
    [Fact]
    public void SharedPolicySettingsTravel_ButTheLastUsedBlankStaysLocal()
    {
        var field = typeof(ConfigSyncService).GetField("SkipSettingsKeys", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field); // поле переименовали — тест должен упасть явно, а не стать пустышкой
        var skipped = (HashSet<string>)field!.GetValue(null)!;

        Assert.Contains("passport_template_last", skipped);
        Assert.DoesNotContain("passport_templates_folder", skipped);
        Assert.DoesNotContain("passport_name_placeholder", skipped);
    }

    // ── Адрес бланка на чужой машине ────────────────────────────────────────────────────────

    /// <summary>Бланк, залитый коллегой, обязан открываться и у нас — а шара у него смонтирована
    /// другой буквой. Обычному паспорту это чинит FirmwarePathLocalizer, но он якорится на «ПО», а
    /// бланк лежит вне этого дерева: якоря в его пути нет, и чужой путь остался бы чужим. Поэтому
    /// адрес бланка собирается заново из общей папки (настройка синхронизируемая — одна на всех) и
    /// названия бланка.</summary>
    [Fact]
    public void ResolveDoc_FindsABlankUploadedOnAMachineWithADifferentDriveLetter()
    {
        var (_, folder) = UploadGeneral("НКУ", "Паспорт.docx", "бланк", new DateTime(2026, 8, 4, 12, 0, 0));
        var row = _db.GetGeneralPassports().Single();
        row.DiskPath = @"Z:\Antarus\Конфиг\Паспорта\НКУ"; // так путь записал тот, кто грузил

        Assert.Equal(folder, PassportService.FolderFor(row, Root, TemplatesFolder()));
        Assert.Equal(Path.Combine(folder, "Паспорт.docx"),
            PassportService.ResolveDoc(row, Root, TemplatesFolder()).Docx);

        // Без общей папки собирать адрес не из чего — путь остаётся чужим, и файл не находится.
        Assert.Null(PassportService.ResolveDoc(row, Root).Docx);
    }

    /// <summary>Записанный путь не выбрасывается: бланк могли залить, когда настройка указывала в
    /// другое место, — если собранной папки нет, а записанная на месте, открывается она.</summary>
    [Fact]
    public void ResolveDoc_FallsBackToTheStoredFolder_WhenTheSharedOneHasNothing()
    {
        var (_, folder) = UploadGeneral("НКУ", "Паспорт.docx", "бланк", new DateTime(2026, 8, 4, 12, 0, 0));
        var row = _db.GetGeneralPassports().Single();

        var movedAway = Path.Combine(Root, "Прежняя папка бланков");
        Assert.Equal(folder, PassportService.FolderFor(row, Root, movedAway));   // папки нет — берём записанную
        Assert.Equal(Path.Combine(folder, "Паспорт.docx"), PassportService.ResolveDoc(row, Root, movedAway).Docx);
    }

    /// <summary>Паспорт, привязанный к шкафу, эта ветка не касается: он лежит в дереве «ПО» и
    /// переприкрепляется к нашему корню как раньше, даже если папку бланков передали.</summary>
    [Fact]
    public void ResolveDoc_OfACabinetPassport_StillGoesThroughTheSoftwareTree()
    {
        var row = new PassportTemplate
        {
            SubtypeId = PickSubtype(), Name = "Паспорт ПЖ ПИ", Filename = "Паспорт.docx",
            DiskPath = @"Z:\Antarus\ПО\ПЖ\ХП\Паспорт\Паспорт ПЖ ПИ", UploadDate = "2026-08-04 12:00:00",
        };

        Assert.Equal(Path.Combine(Root, @"ПО\ПЖ\ХП\Паспорт\Паспорт ПЖ ПИ"),
            PassportService.FolderFor(row, Root, TemplatesFolder()));
    }

    // ── Название шкафа из поискового запроса ──────────────────────────────────

    /// <summary>Искали шкаф, нашёлся бланк — название шкафа берётся прямо из запроса, набирать его
    /// второй раз не нужно.</summary>
    [Fact]
    public void CabinetName_ComesFromTheSearchQuery()
    {
        Assert.Equal("ЩУН-3", PassportService.CabinetNameFromQuery("ЩУН-3", "НКУ"));
    }

    /// <summary>Главный случай: бланк нашёлся по тегу, и этот тег — как раз название шкафа (для того
    /// теги на бланк и цепляют). Такой запрос подставляется целиком.</summary>
    [Fact]
    public void CabinetName_KeepsTheQuery_WhenItMatchedATagOfTheBlank()
    {
        Assert.Equal("ЩУН-9", PassportService.CabinetNameFromQuery("ЩУН-9", "НКУ"));
    }

    /// <summary>Название самого бланка из подстановки выпадает: «НКУ» и «Щит СПЛ» — это вид
    /// документа, а не шкаф.</summary>
    [Fact]
    public void CabinetName_DropsTheNameOfTheBlankItself()
    {
        Assert.Equal("ЩУН-3", PassportService.CabinetNameFromQuery("НКУ ЩУН-3", "НКУ"));
        Assert.Equal("ЩУН-3", PassportService.CabinetNameFromQuery("щит спл ЩУН-3", "Щит СПЛ"));
    }

    /// <summary>Запрос был про сам бланк — названия шкафа в нём не было вовсе, и подставлять нечего:
    /// пустое поле честнее, чем «НКУ» в графе шкафа.</summary>
    [Fact]
    public void CabinetName_IsEmpty_WhenTheQueryWasAboutTheBlankItself()
    {
        Assert.Equal("", PassportService.CabinetNameFromQuery("НКУ", "НКУ"));
        Assert.Equal("", PassportService.CabinetNameFromQuery("  Щит   СПЛ  ", "Щит СПЛ"));
        Assert.Equal("", PassportService.CabinetNameFromQuery("", "НКУ"));
        Assert.Equal("", PassportService.CabinetNameFromQuery(null, "НКУ"));
    }

    /// <summary>Регистр в запросе никакой роли не играет — как и везде в поиске этой программы.</summary>
    [Fact]
    public void CabinetName_IgnoresCase()
    {
        Assert.Equal("", PassportService.CabinetNameFromQuery("нку", "НКУ"));
        Assert.Equal("ЩУН-3", PassportService.CabinetNameFromQuery("нку ЩУН-3", "НКУ"));
    }
}
