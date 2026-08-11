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

/// <summary>«Хер поймёшь, выгрузилась она нет» — страница «Хранилище» отвечает на этот вопрос, и
/// отвечает ПРАВДОЙ: состояние берётся из ответа хостинга на запрос по ключу, а не из своей записи о
/// том, что мы когда-то выкладывали. Файл могли удалить руками через S3-клиент, выложить с другой
/// машины, а локальную базу — пересоздать.
///
/// Второе свойство, которое здесь проверяется: список строится БЕЗ обращения к сети. Открытие
/// страницы на сотне версий не должно упираться в сотню запросов к хостингу.</summary>
public class HostingSyncServiceTests
{
    private static S3Settings Settings() =>
        new("https://s3.twcstorage.ru", "amperus", "ru-1", "", "AKIA-ID", "секрет",
            "https://fs.elitacompany.ru", Enabled: true);

    /// <summary>Подставной хостинг: отвечает по заранее заданному списку того, что в нём «лежит».</summary>
    private sealed class FakeStorage : HttpMessageHandler
    {
        private readonly HashSet<string> _present;

        public FakeStorage(params string[] presentKeys) =>
            _present = new HashSet<string>(presentKeys, StringComparer.Ordinal);

        public List<string> Heads { get; } = new();
        public List<string> Puts { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Head)
            {
                Heads.Add(path);
                return Task.FromResult(new HttpResponseMessage(
                    _present.Any(k => path.EndsWith(k, StringComparison.Ordinal))
                        ? HttpStatusCode.OK
                        : HttpStatusCode.NotFound));
            }

            Puts.Add(path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static HostingSyncService Service(Database db, FakeStorage storage) =>
        new(db, new S3Client(new HttpClient(storage)));

    /// <summary>Версия с папкой инструкции на диске. Возвращает путь к папке версии.</summary>
    private static string SeedVersion(Database db, TempRoot root, string versionRaw, string? instructionFile)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "ХП");
        var smh4 = db.GetAllModifications().First(m => m.ControllerName == "SMH4");

        var dir = Path.Combine(root.Path, "ПО", group.Name, subtype.Name, "SMH4", versionRaw);
        Directory.CreateDirectory(VersionLayout.FirmwareFolder(dir));
        var instructions = VersionLayout.SlotFolder(dir, HierarchyFolders.Instructions);
        Directory.CreateDirectory(instructions);

        var stored = "";
        if (instructionFile is not null)
        {
            stored = Path.Combine(instructions, instructionFile);
            File.WriteAllText(stored, "документ");
        }

        db.AddFwVersion(new FwVersionRecord
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
        });
        return dir;
    }

    [Fact]
    public void Plan_ListsWhatShouldBeOnHosting_WithoutTouchingTheNetwork()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();
        SeedVersion(db, root, "1.0.0005.0001", "инструкция_1.0.0005.0001.pdf");

        var storage = new FakeStorage();
        var items = Service(db, storage).Plan(Settings(), root.Path);

        var item = Assert.Single(items);
        Assert.Equal("1.0.0005.0001", item.VersionRaw);
        Assert.Equal(HostingState.Unknown, item.State); // не проверяли — и не соврали, что проверили
        Assert.Equal("PO/PZH/HP/SMH4/1.0.0005.0001/Instrukciya/instrukciya_1.0.0005.0001.pdf", item.ObjectKey);
        Assert.StartsWith("https://fs.elitacompany.ru/", item.Url, StringComparison.Ordinal);
        Assert.Empty(storage.Heads);
        Assert.Empty(storage.Puts);
    }

    [Fact]
    public void Plan_ForDocx_PointsAtThePdfKey()
    {
        // Документ Word уезжает на хостинг собранным PDF — спрашивать у бакета про .docx значило бы
        // всегда получать «нет на хостинге» по документу, который там лежит.
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();
        SeedVersion(db, root, "1.0.0005.0002", "инструкция_1.0.0005.0002.docx");

        var item = Assert.Single(Service(db, new FakeStorage()).Plan(Settings(), root.Path));

        Assert.EndsWith(".pdf", item.ObjectKey, StringComparison.Ordinal);
        Assert.DoesNotContain(".docx", item.ObjectKey, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_VersionWithoutInstruction_StillShowsItsFutureAddress()
    {
        // Наклейку с QR клеят на шкаф задолго до того, как документ напишут: адрес обязан быть
        // известен уже сейчас, а строка — честно говорить, что выкладывать пока нечего.
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();
        SeedVersion(db, root, "1.0.0005.0003", instructionFile: null);

        var item = Assert.Single(Service(db, new FakeStorage()).Plan(Settings(), root.Path));

        Assert.Equal(HostingState.NoSource, item.State);
        Assert.EndsWith("/instrukciya_1.0.0005.0003.pdf", item.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_AsksTheHosting_AndSeparatesMissingFromUnreachable()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();
        SeedVersion(db, root, "1.0.0005.0001", "инструкция_1.0.0005.0001.pdf");
        SeedVersion(db, root, "1.0.0005.0002", "инструкция_1.0.0005.0002.pdf");

        var storage = new FakeStorage("/instrukciya_1.0.0005.0001.pdf");
        var service = Service(db, storage);
        var items = service.Plan(Settings(), root.Path);

        var checkedItems = await service.CheckAsync(Settings(), items);

        Assert.Equal(2, storage.Heads.Count);
        Assert.Equal(HostingState.Published, checkedItems.First(i => i.VersionRaw.EndsWith("0001")).State);
        Assert.Equal(HostingState.Missing, checkedItems.First(i => i.VersionRaw.EndsWith("0002")).State);
    }

    [Fact]
    public async Task Check_ReportsProgress_AndCanBeStopped()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();
        for (var i = 1; i <= 5; i++) SeedVersion(db, root, $"1.0.0005.000{i}", $"инструкция_1.0.0005.000{i}.pdf");

        var service = Service(db, new FakeStorage());
        var items = service.Plan(Settings(), root.Path);

        var seen = new List<HostingProgress>();
        using var cancel = new CancellationTokenSource();
        var progress = new Progress<HostingProgress>(p =>
        {
            seen.Add(p);
            if (p.Done >= 2) cancel.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CheckAsync(Settings(), items, progress, cancel.Token));

        // Отчёт о ходе идёт по одной строке, а не одним «готово» в конце: на сотнях файлов человек
        // обязан видеть, что происходит.
        Assert.NotEmpty(seen);
        Assert.True(seen[0].Total == items.Count, "в отчёте должно быть общее число строк");
    }

    [Fact]
    public async Task Publish_OnlyMissing_LeavesWhatIsAlreadyThereAlone()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();
        SeedVersion(db, root, "1.0.0005.0001", "инструкция_1.0.0005.0001.pdf");
        SeedVersion(db, root, "1.0.0005.0002", "инструкция_1.0.0005.0002.pdf");

        var storage = new FakeStorage("/instrukciya_1.0.0005.0001.pdf");
        var service = Service(db, storage);
        var items = await service.CheckAsync(Settings(), service.Plan(Settings(), root.Path));

        var result = service.Publish(Settings(), root.Path, items, onlyMissing: true);

        Assert.Equal(1, result.Published);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Failed);
        var put = Assert.Single(storage.Puts);
        Assert.EndsWith("/instrukciya_1.0.0005.0002.pdf", put, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_Everything_RepublishesEvenWhatIsThere()
    {
        // Нужно, когда документы правили на диске, а на хостинге осталась прошлая редакция.
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();
        SeedVersion(db, root, "1.0.0005.0001", "инструкция_1.0.0005.0001.pdf");

        var storage = new FakeStorage("/instrukciya_1.0.0005.0001.pdf");
        var service = Service(db, storage);
        var items = await service.CheckAsync(Settings(), service.Plan(Settings(), root.Path));
        Assert.Equal(HostingState.Published, items[0].State);

        var result = service.Publish(Settings(), root.Path, items, onlyMissing: false);

        Assert.Equal(1, result.Published);
        Assert.Single(storage.Puts);
    }

    [Fact]
    public void Publish_VersionWithoutFile_IsSkippedWithAnExplanation()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        using var root = new TempRoot();
        SeedVersion(db, root, "1.0.0005.0003", instructionFile: null);

        var storage = new FakeStorage();
        var service = Service(db, storage);
        var result = service.Publish(Settings(), root.Path, service.Plan(Settings(), root.Path), onlyMissing: true);

        Assert.Equal(0, result.Published);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(storage.Puts);
        Assert.Contains(result.Messages, m => m.Contains("выкладывать нечего", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_WithoutDiskRoot_ReturnsNothingInsteadOfThrowing()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        Assert.Empty(Service(db, new FakeStorage()).Plan(Settings(), ""));
    }
}
