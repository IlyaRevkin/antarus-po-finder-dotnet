using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Документы прошивки, привязанной сразу к нескольким подтипам шкафа.
///
/// Жалоба (13.08.2026): «у меня есть ПЖ FD SMH5, и там вместо прошивки ссылка на ПЖ 2.0 SMH5,
/// и инструкция туда прицепилась некорректно». Прошивка у этих двух шкафов действительно одна — файлы
/// лежат на диске один раз, в папке основного подтипа (<see cref="FirmwareSubtypeLinkService"/>), — а
/// вот руководство по эксплуатации пишется на ШКАФ, и «ПЖ 2.0» с «ПЖ FD» это разные шкафы.
///
/// Ломалось же оно потому, что читающая и пишущая стороны отвечали на вопрос «где документ этой
/// записи» по-разному: пишущая — по именам иерархии самой записи, читающая — по пути её файлов на
/// диске, то есть по папке ЧУЖОГО подтипа. Приложенная к «ПЖ FD» инструкция ложилась в свою папку, а
/// карточка, QR-код и адрес на хостинге продолжали показывать документ соседа. Здесь проверяется, что
/// ответ теперь один (<see cref="VersionDocFolders"/>) и что обычным версиям от этого ничего не
/// досталось.</summary>
public class SubtypeLinkDocumentsTests : IDisposable
{
    private sealed class FakeShortcuts : IShortcutCreator
    {
        public void Create(string shortcutPath, string targetPath, string description)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
            File.WriteAllText(shortcutPath, targetPath);
        }
    }

    /// <summary>Заглушку рисует приложение (PDF), ядру достаточно файла с тем же именем — правило
    /// «что считать заглушкой» смотрит на имя и размер, а не на содержимое.</summary>
    private sealed class FakeStubWriter : IInstructionStubWriter
    {
        public void Write(string path, string text) => File.WriteAllText(path, text);
    }

    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private readonly FakeShortcuts _shortcuts = new();
    private string Root => _tempRoot.Path;

    public SubtypeLinkDocumentsTests()
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

    // ── Стенд ────────────────────────────────────────────────────────────────

    /// <summary>Прошивка, загруженная основному подтипу и привязанная ко второму. Возвращает обе
    /// записи: primary — та, в чьей папке лежат файлы, linked — «ПЖ FD» из жалобы.</summary>
    private (EquipmentGroup Group, EquipmentSubType Primary, EquipmentSubType Extra,
        ControllerModification Mod, FwVersionRecord PrimaryRec, FwVersionRecord LinkedRec) SeedLinkedPair()
    {
        var group = _db.GetAllEquipmentGroups().Single(g => g.Name == "ПЖ");
        var subtypes = _db.GetSubtypesForGroup(group.Id!.Value);
        var mod = _db.GetAllModifications().First(m => m.ControllerName == "SMH5");

        var src = Path.Combine(Path.GetTempPath(), $"antarus_link_doc_{Guid.NewGuid():N}.psl");
        File.WriteAllText(src, "dummy bytes");
        FirmwareUploadResult uploaded;
        try
        {
            uploaded = FirmwareUploadService.Upload(_db, _hierarchy, new FirmwareUploadRequest
            {
                SourcePath = src,
                Group = group,
                Subtype = subtypes[0],
                Modification = mod,
                LaunchTypes = new() { "УПП" },
                Description = "одна прошивка на два шкафа",
                IncludeDateInVersion = false,
                RootPath = Root,
                AuthorUserName = "tester",
            });
        }
        finally { File.Delete(src); }

        Assert.Equal(FirmwareUploadOutcome.Success, uploaded.Outcome);

        var primary = uploaded.Record!;
        // Связка СТАРОГО образца — та, из-за которой всё и ломалось: запись второго подтипа ведёт в
        // папку первого. Программа таких больше не создаёт (каждый подтип получает свою копию), но на
        // дисках их накоплено много, и читаться они обязаны как раньше.
        var linked = LegacySubtypeLink.Create(_db, _hierarchy, Root, primary, subtypes[1],
            group.Name, mod.ControllerName, _shortcuts);
        Assert.Equal(primary.DiskPath, linked.DiskPath);

        return (group, subtypes[0], subtypes[1], mod, primary, linked);
    }

    private string ControllerFolderOf(EquipmentGroup group, EquipmentSubType subtype, ControllerModification mod) =>
        Path.Combine(HierarchyService.GroupSubFolder(Root, group.Name, subtype.Name), mod.ControllerName);

    /// <summary>Кладёт документ в папку «Инструкция» контроллера у заданного подтипа.</summary>
    private string PutInstruction(EquipmentGroup group, EquipmentSubType subtype, ControllerModification mod,
        string fileName)
    {
        var folder = Path.Combine(ControllerFolderOf(group, subtype, mod), HierarchyFolders.Instructions);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        File.WriteAllText(path, "документ");
        return path;
    }

    private string OwnFolderOf(FwVersionRecord record, string slot = HierarchyFolders.Instructions)
    {
        var names = _db.GetFwVersionNames(record.Id!.Value)!.Value;
        var own = VersionDocFolders.OwnControllerFolder(Root, names.GroupName, names.SubtypeName, names.ControllerName);
        return VersionDocFolders.BestReadFolder(record.DiskPath, own, slot) ?? "";
    }

    // ── Чтение ───────────────────────────────────────────────────────────────

    [Fact]
    public void LinkedSubtype_WithItsOwnDocument_ReadsItsOwn_NotTheNeighbours()
    {
        var (group, primary, extra, mod, primaryRec, linkedRec) = SeedLinkedPair();
        PutInstruction(group, primary, mod, "руководство_2.0.pdf");
        var ownDoc = PutInstruction(group, extra, mod, "руководство_FD.pdf");

        // Основной подтип читает своё, привязанный — тоже своё, а не документ соседа.
        Assert.Equal(Path.GetDirectoryName(ownDoc), OwnFolderOf(linkedRec));
        Assert.Equal(Path.Combine(ControllerFolderOf(group, primary, mod), HierarchyFolders.Instructions),
            OwnFolderOf(primaryRec));
    }

    [Fact]
    public void LinkedSubtype_WithoutItsOwnDocument_StillReadsTheSharedOne()
    {
        // Обновление не имеет права ничего спрятать: пока своего документа у подтипа нет, он читает
        // ровно то же, что читал вчера, — руководство из папки основного подтипа.
        var (group, primary, _, mod, _, linkedRec) = SeedLinkedPair();
        var shared = PutInstruction(group, primary, mod, "руководство_2.0.pdf");

        Assert.Equal(Path.GetDirectoryName(shared), OwnFolderOf(linkedRec));
    }

    [Fact]
    public void LinkedSubtype_WithOnlyAStubOfItsOwn_StillReadsTheSharedDocument()
    {
        // Заглушка «Инструкция в разработке» заводится сама, стоит появиться папке документа, — а
        // папку привязанному подтипу заводит сама привязка. Считайся заглушка документом, руководство
        // у такого шкафа назавтра сменилось бы на «в разработке»: было что читать — стало нечего.
        var (group, primary, extra, mod, _, linkedRec) = SeedLinkedPair();
        var shared = PutInstruction(group, primary, mod, "руководство_2.0.pdf");

        var ownFolder = Path.Combine(ControllerFolderOf(group, extra, mod), HierarchyFolders.Instructions);
        Directory.CreateDirectory(ownFolder);
        InstructionStub.EnsureForVersion(ownFolder, Root, linkedRec.VersionRaw, new FakeStubWriter());
        Assert.NotNull(InstructionStub.ExistingIn(ownFolder));

        Assert.Equal(Path.GetDirectoryName(shared), OwnFolderOf(linkedRec));

        // А настоящий документ — выигрывает, и с этого мгновения адрес у подтипа собственный.
        File.WriteAllText(Path.Combine(ownFolder, "руководство_FD.pdf"), "документ");
        Assert.Equal(ownFolder, OwnFolderOf(linkedRec));
    }

    [Fact]
    public void OrdinaryVersion_KeepsTheOldAnswer_EvenWhenItsFolderIsRebuilt()
    {
        // Ни один из новых признаков не должен трогать обычную версию: у перестроенной под новую
        // раскладку документ по-прежнему берётся из её собственной папки.
        var (group, primary, _, mod, primaryRec, _) = SeedLinkedPair();
        var inside = VersionLayout.SlotFolder(primaryRec.DiskPath, HierarchyFolders.Instructions);
        Directory.CreateDirectory(inside);
        File.WriteAllText(Path.Combine(inside, "руководство_версии.pdf"), "документ");
        PutInstruction(group, primary, mod, "руководство_контроллера.pdf");

        Assert.Equal(inside, OwnFolderOf(primaryRec));
    }

    [Fact]
    public void LegacyOpcVersion_IsNotMistakenForALink()
    {
        // ОПЦ прежней раскладки лежит в «<подтип>\ОПЦ\<версия>», то есть тоже НЕ в папке своего
        // контроллера. Признака «не в своей папке» одного мало — иначе документы такой версии уехали
        // бы в папку контроллера, которой она никогда не принадлежала.
        var group = _db.GetAllEquipmentGroups().Single(g => g.Name == "ПЖ");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value)[0];
        var mod = _db.GetAllModifications().First(m => m.ControllerName == "SMH5");

        var versionDir = Path.Combine(HierarchyService.LegacyOpcFolder(Root, group.Name, subtype.Name), "1.0.0005.0001");
        Directory.CreateDirectory(versionDir);
        var own = ControllerFolderOf(group, subtype, mod);

        Assert.False(VersionDocFolders.IsLinkedCopy(versionDir, own));
    }

    [Fact]
    public void PathPointingAtAnotherController_IsNotMistakenForALink()
    {
        // Папку версии переименовали или перенесли мимо программы — путь ведёт не туда, но это поломка
        // пути, а не привязка к подтипу. Считать её ссылкой значило бы добавить к одной беде вторую.
        var group = _db.GetAllEquipmentGroups().Single(g => g.Name == "ПЖ");
        var subtypes = _db.GetSubtypesForGroup(group.Id!.Value);
        var smh5 = _db.GetAllModifications().First(m => m.ControllerName == "SMH5");
        var other = _db.GetAllModifications().First(m => m.ControllerName != "SMH5");

        var elsewhere = Path.Combine(
            Path.Combine(HierarchyService.GroupSubFolder(Root, group.Name, subtypes[1].Name), other.ControllerName),
            "1.0.0005.0001");
        Directory.CreateDirectory(elsewhere);

        Assert.False(VersionDocFolders.IsLinkedCopy(elsewhere, ControllerFolderOf(group, subtypes[0], smh5)));
    }

    [Fact]
    public void OwnControllerFolder_IsFoundFromTheVersionPathAlone()
    {
        // Карточка поиска считает папку документа из статических методов фонового обхода, где корня
        // диска под рукой нет: он вычисляется из самого пути по опорной папке «ПО».
        var (group, primary, _, mod, primaryRec, _) = SeedLinkedPair();

        Assert.Equal(ControllerFolderOf(group, primary, mod),
            VersionDocFolders.OwnControllerFolderNear(primaryRec.DiskPath, group.Name, primary.Name, mod.ControllerName));
        // Путь не из иерархии — корень не выдумывается.
        Assert.Null(VersionDocFolders.OwnControllerFolderNear(@"C:\Временное\что-то", group.Name, primary.Name,
            mod.ControllerName));
    }

    // ── Запись ───────────────────────────────────────────────────────────────

    [Fact]
    public void InstructionAttachedToTheLinkedSubtype_LandsInItsOwnFolder_AndIsReadBack()
    {
        var (group, primary, extra, mod, _, linkedRec) = SeedLinkedPair();

        var src = Path.Combine(Path.GetTempPath(), $"antarus_link_instr_{Guid.NewGuid():N}.pdf");
        File.WriteAllText(src, "руководство FD");
        try
        {
            var result = FirmwareAttachmentsService.Apply(_db, _hierarchy, linkedRec, new FirmwareAttachmentsRequest
            {
                RootPath = Root,
                GroupName = group.Name,
                SubtypeName = extra.Name,
                ControllerName = mod.ControllerName,
                InstructionsSourcePath = src,
            });
            Assert.Empty(result.Warnings);
        }
        finally { File.Delete(src); }

        var stored = _db.GetFwVersionById(linkedRec.Id!.Value)!.InstructionsPath;
        Assert.False(string.IsNullOrEmpty(stored));
        // Легло в папку СВОЕГО шкафа...
        Assert.StartsWith(ControllerFolderOf(group, extra, mod), stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.Combine(group.Name, primary.Name), stored, StringComparison.OrdinalIgnoreCase);
        // ...и читается оттуда же, куда легло: ровно то, что расходилось раньше.
        Assert.Equal(Path.GetDirectoryName(stored), OwnFolderOf(_db.GetFwVersionById(linkedRec.Id!.Value)!));
    }

    // ── Хранилище ────────────────────────────────────────────────────────────

    private static S3Settings Settings() =>
        new("https://s3.twcstorage.ru", "amperus", "ru-1", "", "AKIA-ID", "секрет",
            "https://fs.elitacompany.ru", Enabled: true);

    private sealed class SilentStorage : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private HostingSyncService Hosting() => new(_db, new S3Client(new HttpClient(new SilentStorage())));

    [Fact]
    public void HostingPlan_SharedDocument_IsOneRowNamingBothCabinets()
    {
        // Строк было столько же, сколько записей, и каждая называла СВОЙ подтип при общем адресе:
        // «ПЖ / FD / SMH5» с адресом «…/PZH/2.0/…» и читалось как перепутанная ссылка.
        var (group, primary, extra, mod, _, _) = SeedLinkedPair();
        PutInstruction(group, primary, mod, "руководство_2.0.pdf");

        var item = Assert.Single(Hosting().Plan(Settings(), Root));

        Assert.Contains(primary.Name, item.Where, StringComparison.OrdinalIgnoreCase);
        Assert.True(item.Shared);
        Assert.Contains(item.SharedWith, w => w.Contains(extra.Name, StringComparison.OrdinalIgnoreCase));
        // Обе записи перечислены — правка ссылки меняет документ у всех сразу, значит знать надо обе.
        Assert.Equal(2, item.VersionIds.Count);
    }

    [Fact]
    public void HostingPlan_LinkedSubtypeWithItsOwnDocument_GetsItsOwnAddress()
    {
        var (group, primary, extra, mod, _, _) = SeedLinkedPair();
        PutInstruction(group, primary, mod, "руководство_2.0.pdf");
        PutInstruction(group, extra, mod, "руководство_FD.pdf");

        var items = Hosting().Plan(Settings(), Root);

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.False(i.Shared));
        // Ключи разные, и у каждого свой подтип в адресе — то, ради чего всё и затевалось.
        Assert.Equal(2, items.Select(i => i.ObjectKey).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void HostingPlan_ForeignDiskPath_IsBroughtToOurDisk()
    {
        // Путь, записанный на машине коллеги, приводится к нашей форме диска. Без этого версия,
        // приехавшая с машины с иначе смонтированной шарой, в списке не появлялась вовсе — при том,
        // что наклейка с QR на неё уже напечатана.
        var (group, primary, _, mod, primaryRec, _) = SeedLinkedPair();
        PutInstruction(group, primary, mod, "руководство_2.0.pdf");

        var relative = primaryRec.DiskPath[(Root.Length + 1)..];
        _db.RepointFwVersionDiskPath(primaryRec.Id!.Value, Path.Combine(@"\\другой-сервер\шара", relative));

        var items = Hosting().Plan(Settings(), Root);

        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.NotEqual(HostingState.NoSource, i.State));
    }
}
