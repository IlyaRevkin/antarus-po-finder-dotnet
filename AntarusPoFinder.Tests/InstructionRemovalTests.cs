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

/// <summary>Удаление инструкции. До сих пор можно было снять только ССЫЛКУ: документ оставался лежать
/// и на диске, и на хостинге, карточка продолжала показывать «инструкция ✓» (она смотрит на файлы в
/// папке, а не в базу), а по QR со шкафа открывался «удалённый» документ.
///
/// Проверяется здесь то, что глазами в живом прогоне не увидеть:
///   • удаление доходит до ВСЕХ трёх мест — диск, бакет, база — и в правильном порядке;
///   • на освободившееся место возвращается заглушка «в разработке»: наклейка уже на шкафу, и ссылка
///     обязана открываться и после удаления документа;
///   • общий документ не удаляется. У версии, не переехавшей на новую раскладку, папка «Инструкция»
///     общая на весь контроллер, и «удалить у одной» там означает «удалить у всех»;
///   • неудачное удаление НЕ снимает ссылку: запись «инструкции нет» при лежащем на диске документе —
///     худшее из состояний, потому что карточка всё равно покажет «инструкция ✓».</summary>
public class InstructionRemovalTests
{
    private const string Version = "1.0.0005.0001";

    private static S3Settings Settings() =>
        new("https://s3.twcstorage.ru", "amperus", "ru-1", "", "AK", "SK",
            "https://fs.elitacompany.ru", Enabled: true);

    /// <summary>Заглушку рисует WPF (App/Services/InstructionStubWriter), в Core его нет — здесь ту же
    /// роль играет файл-пустышка: проверяется не рисование, а то, кладётся ли она вообще.</summary>
    private sealed class FakeStubWriter : IInstructionStubWriter
    {
        public void Write(string path, string text) => File.WriteAllText(path, text);
    }

    private sealed class FakePublisher : IInstructionPublisher
    {
        public List<string> Published { get; } = new();

        public string? Publish(string actualPath, string pathOnFirstDisk, string firstDiskRoot, List<string> warnings)
        {
            Published.Add(actualPath);
            return "https://fs.elitacompany.ru/" + Path.GetFileName(actualPath);
        }
    }

    /// <summary>Подставная «обратная сторона» выкладки: ключ считает так же, как настоящая (от пути
    /// относительно корня диска), но в сеть не ходит.</summary>
    private sealed class FakeUnpublisher : IInstructionUnpublisher
    {
        public List<string> Removed { get; } = new();

        public string? KeyOf(string pathOnFirstDisk, string firstDiskRoot) =>
            LabelLinkBuilder.RelativeTo(firstDiskRoot, pathOnFirstDisk) is { } relative
                ? Settings().KeyFor(InstructionPublisher.AsPublishedName(relative))
                : null;

        public IReadOnlyList<string> Unpublish(string pathOnFirstDisk, string firstDiskRoot, bool folder,
            List<string> warnings)
        {
            var key = KeyOf(pathOnFirstDisk, firstDiskRoot);
            if (key is null) return Array.Empty<string>();
            Removed.Add(key);
            return new[] { key };
        }
    }

    /// <summary>Версия на диске. <paramref name="newLayout"/> = true — версия уже перестроена, папка
    /// «Инструкция» своя; false — прежняя раскладка, папка «Инструкция» общая у контроллера, и это
    /// ровно тот случай, в котором удалять файл нельзя.</summary>
    private static FwVersionRecord SeedVersion(Database db, TempRoot root, string versionRaw,
        string? instructionFile, bool newLayout = true)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "ХП");
        var smh4 = db.GetAllModifications().First(m => m.ControllerName == "SMH4");

        var controller = Path.Combine(root.Path, "ПО", group.Name, subtype.Name, "SMH4");
        var dir = Path.Combine(controller, versionRaw);
        Directory.CreateDirectory(dir);
        if (newLayout) Directory.CreateDirectory(VersionLayout.FirmwareFolder(dir));

        var instructions = newLayout
            ? VersionLayout.SlotFolder(dir, HierarchyFolders.Instructions)
            : Path.Combine(controller, HierarchyFolders.Instructions);
        Directory.CreateDirectory(instructions);

        var stored = "";
        if (instructionFile is not null)
        {
            stored = Path.Combine(instructions, instructionFile);
            File.WriteAllText(stored, "документ");
        }

        var record = new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = smh4.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = smh4.HwVersion,
            SwVersion = 1,
            VersionRaw = versionRaw,
            Filename = versionRaw + ".psl",
            DiskPath = dir,
            InstructionsPath = stored,
            Status = "active",
        };
        record.Id = db.AddFwVersion(record);
        return record;
    }

    // ── Что считается инструкцией ─────────────────────────────────────────────

    /// <summary>Заглушка — не инструкция, и удалять её нечего: она и должна лежать там, где документа
    /// нет. Иначе кнопка «удалить» уносила бы заглушку, оставляя папку пустой, а ссылку с наклейки —
    /// битой.</summary>
    [Fact]
    public void Plan_DoesNotMistakeTheStubForADocument()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();

        var record = SeedVersion(db, root, Version, instructionFile: null);
        var folder = VersionLayout.SlotFolder(record.DiskPath, HierarchyFolders.Instructions);
        InstructionStub.EnsureIn(folder, Version, new FakeStubWriter());

        var plan = InstructionRemoval.Plan(db, record, root.Path);

        Assert.Null(plan.DiskPath);
        Assert.True(plan.OnlyStub);
        Assert.True(plan.NothingToDo);
    }

    /// <summary>Документ, положенный в папку руками мимо программы, — тоже инструкция: именно его
    /// показывает карточка (она смотрит на файлы), и «удалить» обязано работать и с ним.</summary>
    [Fact]
    public void Plan_FindsADocumentPutThereByHand_WithoutALinkInTheDatabase()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();

        var record = SeedVersion(db, root, Version, instructionFile: null);
        var folder = VersionLayout.SlotFolder(record.DiskPath, HierarchyFolders.Instructions);
        var byHand = Path.Combine(folder, "Инструкция по эксплуатации.pdf");
        File.WriteAllText(byHand, "документ");

        var plan = InstructionRemoval.Plan(db, record, root.Path);

        Assert.Equal(byHand, plan.DiskPath);
        Assert.True(plan.DeletesFile);
        Assert.False(plan.HasLink); // ссылки в базе нет — снимать нечего, а удалять есть что
    }

    // ── Удаление ──────────────────────────────────────────────────────────────

    /// <summary>Полный путь удаления: файл уходит с диска, объект — с хостинга, ссылка снимается, а на
    /// освободившееся место ложится заглушка и уезжает на хостинг под своим ключом. Документ здесь
    /// назван НЕ канонически — тогда ключ заглушки отличается от ключа документа, и видно, что
    /// наблюдения «лежит ли на хостинге» правятся оба и в разные стороны.</summary>
    [Fact]
    public void Remove_TakesTheDocumentOffDiskAndHosting_AndPutsTheStubBack()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();

        var record = SeedVersion(db, root, Version, "Инструкция по эксплуатации.pdf");
        var document = record.InstructionsPath;
        var folder = Path.GetDirectoryName(document)!;

        var publisher = new FakePublisher();
        var unpublisher = new FakeUnpublisher();
        var plan = InstructionRemoval.Plan(db, record, root.Path);
        var result = InstructionRemoval.Apply(db, record, root.Path, plan,
            new FakeStubWriter(), publisher, unpublisher);

        Assert.True(result.Removed);
        Assert.Empty(result.Warnings);
        Assert.False(File.Exists(document));

        // Ссылка снята и в записи, и в базе.
        Assert.Equal("", record.InstructionsPath);
        Assert.Equal("", db.GetFwVersionById(record.Id!.Value)!.InstructionsPath);

        // На месте документа — заглушка под каноническим именем, и она выложена на хостинг: ссылка с
        // наклейки продолжает открываться, просто теперь показывает «в разработке».
        var stub = InstructionStub.ExistingIn(folder);
        Assert.NotNull(stub);
        Assert.Equal(InstructionStub.FileNameFor(Version), Path.GetFileName(stub));
        Assert.Equal(stub, Assert.Single(publisher.Published));

        var documentKey = Assert.Single(unpublisher.Removed);
        Assert.EndsWith("Instrukciya_po_ekspluatacii.pdf", documentKey);
        Assert.False(db.GetHostingCheck(documentKey)!.Value.Present);
        Assert.True(db.GetHostingCheck(unpublisher.KeyOf(stub!, root.Path)!)!.Value.Present);
    }

    /// <summary>Хостинг не настроен — штатное состояние машины без ключей, а не отказ: файл всё равно
    /// удаляется с диска, ссылка снимается, заглушка ложится. Ничего, что связано с бакетом, просто не
    /// происходит.
    ///
    /// Заодно виден главный смысл канонического имени: документ назывался «инструкция_&lt;версия&gt;.pdf»,
    /// и заглушка встаёт РОВНО НА ЕГО ПУТЬ. Файл по этому адресу есть и после удаления — только теперь
    /// он говорит «в разработке», и напечатанный QR продолжает открываться.</summary>
    [Fact]
    public void Remove_WorksWithoutHosting_AndTheStubTakesTheSamePath()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();

        var record = SeedVersion(db, root, Version, $"инструкция_{Version}.pdf");
        var document = record.InstructionsPath;

        var plan = InstructionRemoval.Plan(db, record, root.Path);
        var result = InstructionRemoval.Apply(db, record, root.Path, plan, new FakeStubWriter());

        Assert.True(result.Removed);
        Assert.Equal("", record.InstructionsPath);
        Assert.True(File.Exists(document));
        Assert.True(InstructionStub.IsStub(document)); // это уже не документ, а заглушка
        Assert.Equal(document, InstructionStub.ExistingIn(Path.GetDirectoryName(document)));
    }

    /// <summary>Папка постраничных сканов удаляется целиком, и с хостинга у неё уходит не один
    /// объект, а всё, что лежало под префиксом (об этом — отдельный тест ниже, здесь важно, что
    /// удаление вообще идёт «папкой»).</summary>
    [Fact]
    public void Remove_DeletesAFolderOfScans()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();

        var record = SeedVersion(db, root, Version, instructionFile: null);
        var scans = Path.Combine(VersionLayout.SlotFolder(record.DiskPath, HierarchyFolders.Instructions), "сканы");
        Directory.CreateDirectory(scans);
        File.WriteAllText(Path.Combine(scans, "стр1.jpg"), "1");
        File.WriteAllText(Path.Combine(scans, "стр2.jpg"), "2");
        db.UpdateFwVersionAttachments(record.Id!.Value, instructionsPath: scans);
        record.InstructionsPath = scans;

        var unpublisher = new FakeUnpublisher();
        var plan = InstructionRemoval.Plan(db, record, root.Path);
        Assert.True(plan.IsFolder);

        var result = InstructionRemoval.Apply(db, record, root.Path, plan, new FakeStubWriter(), null, unpublisher);

        Assert.True(result.Removed);
        Assert.False(Directory.Exists(scans));
        Assert.Single(unpublisher.Removed);
    }

    // ── Чего делать нельзя ────────────────────────────────────────────────────

    /// <summary>У версии, не переехавшей на новую раскладку, папка «Инструкция» общая на весь
    /// контроллер. Удалить оттуда файл — значит удалить инструкцию у ВСЕХ версий контроллера сразу,
    /// поэтому файл остаётся, снимается только ссылка, и человеку говорится, кто ещё его читает.</summary>
    [Fact]
    public void Remove_KeepsADocumentThatOtherVersionsRead()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();

        var first = SeedVersion(db, root, "1.0.0005.0001", "инструкция.pdf", newLayout: false);
        SeedVersion(db, root, "1.0.0005.0002", instructionFile: null, newLayout: false);
        var document = first.InstructionsPath;

        var unpublisher = new FakeUnpublisher();
        var plan = InstructionRemoval.Plan(db, first, root.Path);

        Assert.True(plan.Shared);
        Assert.False(plan.DeletesFile);
        Assert.Contains("1.0.0005.0002", string.Join(" ", plan.UsedAlsoBy));

        var result = InstructionRemoval.Apply(db, first, root.Path, plan, new FakeStubWriter(), null, unpublisher);

        Assert.False(result.Removed);
        Assert.True(File.Exists(document));           // документ соседей цел
        Assert.Empty(unpublisher.Removed);            // и с хостинга не убран
        Assert.Equal("", db.GetFwVersionById(first.Id!.Value)!.InstructionsPath);
        Assert.Contains(result.Warnings, w => w.Contains("остался на диске"));
        // Заглушки рядом с живым документом быть не должно — это была бы прямая ложь.
        Assert.Null(InstructionStub.ExistingIn(Path.GetDirectoryName(document)));
    }

    /// <summary>Файл не удалился (открыт в просмотрщике) — ссылку снимать НЕЛЬЗЯ. Иначе получается
    /// худшее из состояний: в базе «инструкции нет», на диске документ лежит, а карточка (она смотрит
    /// на файлы, а не в базу) продолжает показывать «инструкция ✓».</summary>
    [Fact]
    public void Remove_DoesNotClearTheLink_WhenTheFileSurvives()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();

        var record = SeedVersion(db, root, Version, $"инструкция_{Version}.pdf");
        var document = record.InstructionsPath;

        var plan = InstructionRemoval.Plan(db, record, root.Path);
        InstructionRemovalResult result;
        using (new FileStream(document, FileMode.Open, FileAccess.Read, FileShare.None))
            result = InstructionRemoval.Apply(db, record, root.Path, plan, new FakeStubWriter());

        Assert.False(result.Removed);
        Assert.True(File.Exists(document));
        Assert.Equal(document, db.GetFwVersionById(record.Id!.Value)!.InstructionsPath);
        Assert.NotEmpty(result.Warnings);
    }

    /// <summary>Копии версии под другие подтипы шкафа и её конфигурации делят один и тот же файл на
    /// диске: ссылку надо снять у всех, иначе у соседних записей она указывала бы на удалённый
    /// документ. И блокировать удаление такие записи не должны — это та же самая версия.</summary>
    [Fact]
    public void Remove_ClearsTheLinkOfEveryRecordSharingTheSameFiles()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();

        var record = SeedVersion(db, root, Version, $"инструкция_{Version}.pdf");
        var copyId = db.DuplicateFwVersion(record.Id!.Value);

        var plan = InstructionRemoval.Plan(db, record, root.Path);
        Assert.False(plan.Shared);
        Assert.Equal(2, plan.UnlinkIds.Count);

        InstructionRemoval.Apply(db, record, root.Path, plan, new FakeStubWriter());

        Assert.Equal("", db.GetFwVersionById(record.Id!.Value)!.InstructionsPath);
        Assert.Equal("", db.GetFwVersionById(copyId)!.InstructionsPath);
    }

    // ── Снятие с хостинга по-настоящему ───────────────────────────────────────

    private const string Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    private sealed class FakeStorage : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _answers;

        public FakeStorage(params (HttpStatusCode Status, string Body)[] answers) =>
            _answers = new Queue<(HttpStatusCode, string)>(answers);

        public List<(string Method, string Url)> Seen { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.Method.Method, request.RequestUri!.AbsoluteUri));
            var (status, body) = _answers.Count > 0 ? _answers.Dequeue() : (HttpStatusCode.NoContent, "");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    /// <summary>Ключ считается от ИТОГОВОГО имени: документ Word лежит на хостинге собранным PDF (см.
    /// InstructionPublisher.AsPublishedName), и удалять по «.docx» значило бы оставить в бакете ровно
    /// тот файл, который открывается по ссылке с наклейки.</summary>
    [Fact]
    public void Unpublish_DeletesTheObjectUnderThePublishedName()
    {
        using var root = new TempRoot();
        var document = Path.Combine(root.Path, "ПО", "ПЖ", "Инструкция", "инструкция_1.0.docx");
        Directory.CreateDirectory(Path.GetDirectoryName(document)!);
        File.WriteAllText(document, "документ");

        var storage = new FakeStorage((HttpStatusCode.NoContent, ""));
        var publisher = new InstructionPublisher(Settings(), new S3Client(new HttpClient(storage)));

        var warnings = new List<string>();
        var removed = publisher.Unpublish(document, root.Path, folder: false, warnings);

        Assert.Empty(warnings);
        Assert.EndsWith("instrukciya_1.0.pdf", Assert.Single(removed));
        var (method, url) = Assert.Single(storage.Seen);
        Assert.Equal("DELETE", method);
        Assert.EndsWith("instrukciya_1.0.pdf", url);
    }

    /// <summary>У папки сканов ключей столько, сколько в ней файлов: папок у S3 нет, и что там лежит
    /// на самом деле, известно только из перечисления — часть страниц могла уехать с другой машины
    /// или остаться от прошлой редакции.</summary>
    [Fact]
    public void Unpublish_OfAFolder_RemovesEveryObjectUnderThePrefix()
    {
        using var root = new TempRoot();
        var scans = Path.Combine(root.Path, "ПО", "ПЖ", "Инструкция", "сканы");
        Directory.CreateDirectory(scans);

        var listing = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ListBucketResult xmlns="{Ns}">
              <Name>amperus</Name>
              <IsTruncated>false</IsTruncated>
              <Contents><Key>PO/PZH/Instrukciya/skany/str1.jpg</Key><Size>1</Size></Contents>
              <Contents><Key>PO/PZH/Instrukciya/skany/str2.jpg</Key><Size>2</Size></Contents>
            </ListBucketResult>
            """;

        var storage = new FakeStorage(
            (HttpStatusCode.OK, listing),
            (HttpStatusCode.NoContent, ""),
            (HttpStatusCode.NoContent, ""));
        var publisher = new InstructionPublisher(Settings(), new S3Client(new HttpClient(storage)));

        var warnings = new List<string>();
        var removed = publisher.Unpublish(scans, root.Path, folder: true, warnings);

        Assert.Empty(warnings);
        Assert.Equal(2, removed.Count);
        Assert.Equal(new[] { "GET", "DELETE", "DELETE" }, storage.Seen.Select(s => s.Method).ToArray());
        // Перечисление идёт по префиксу с хвостовым слешем — иначе под него попали бы соседние
        // ключи, начинающиеся с того же имени («сканы2»).
        Assert.Contains("prefix=PO%2FPZH%2FInstrukciya%2Fskany%2F", storage.Seen[0].Url);
    }
}
