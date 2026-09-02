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

/// <summary>Три правила про файл инструкции, которых до сих пор не было:
///
/// • <b>имя файла = «инструкция_&lt;версия&gt;»</b> — как у прошивки («имя файла = имя папки версии»),
///   иначе по документу, вырванному из контекста, не понять, к какой версии он относится;
/// • <b>пустых папок «Инструкция» не бывает</b> — там, где документа ещё нет, лежит заглушка
///   «Инструкция в разработке»;
/// • <b>заглушка занимает то же самое каноническое имя</b>, что и будущий документ: путь не меняется,
///   меняется только файл, поэтому напечатанный QR остаётся верным. При этом заглушка НЕ считается
///   инструкцией — признак «инструкция ✓» и печать обязаны относиться к настоящему документу.</summary>
public class InstructionNamingAndStubTests : IDisposable
{
    /// <summary>Заглушку рисует WPF (см. App/Services/InstructionStubWriter), в Core его нет —
    /// подставляем ту же роль файлом-пустышкой. Тестируем не рисование, а логику «когда класть,
    /// когда убирать и чем это считать».</summary>
    private sealed class FakeStubWriter : IInstructionStubWriter
    {
        public List<string> Written { get; } = new();
        public void Write(string path, string text)
        {
            Written.Add(path);
            File.WriteAllText(path, text);
        }
    }

    private sealed class NoShortcuts : IShortcutCreator
    {
        public void Create(string shortcutPath, string targetPath, string description) =>
            File.WriteAllText(shortcutPath, "lnk");
    }

    /// <summary>Настоящий .lnk в тестах не создать (это COM Windows), но проверять надо именно то,
    /// КУДА он указывает — запоминаем цель.</summary>
    private sealed class RecordingShortcuts : IShortcutCreator
    {
        public Dictionary<string, string> Targets { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Create(string shortcutPath, string targetPath, string description)
        {
            Targets[shortcutPath] = targetPath;
            File.WriteAllText(shortcutPath, targetPath);
        }
    }

    private readonly TempDb _dbFile = new();
    private readonly TempRoot _tempRoot = new();
    private readonly Database _db;
    private readonly HierarchyService _hierarchy;
    private string Root => _tempRoot.Path;

    public InstructionNamingAndStubTests()
    {
        _db = new Database(_dbFile.Path);
        _hierarchy = new HierarchyService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _dbFile.Dispose();
        _tempRoot.Dispose();
    }

    private static string Touch(string folder, string name, string content = "документ")
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    private const string Version = "2.1.0042.0001.20260422_1348";

    // ── Имя файла ────────────────────────────────────────────────────────────

    [Fact]
    public void BuildFileName_IsPrefixPlusVersion_WithLowerCasedExtension()
    {
        Assert.Equal($"инструкция_{Version}.pdf", InstructionNaming.BuildFileName(Version, ".PDF"));
        Assert.Equal($"инструкция_{Version}.docx", InstructionNaming.BuildFileName(Version, "docx"));
        // Версии нет — строить имя не из чего, и выдумывать его нельзя.
        Assert.Equal("", InstructionNaming.BuildFileName("", ".pdf"));
    }

    [Fact]
    public void CanonicalNameFor_LeavesAloneWhatMustNotBeRenamed()
    {
        var folder = Path.Combine(Root, "Инструкция");

        // Уже каноническое — трогать нечего.
        Assert.Null(InstructionNaming.CanonicalNameFor(Path.Combine(folder, $"инструкция_{Version}.pdf"), Version));
        // Ярлык на уехавший на третий диск файл — не документ.
        Assert.Null(InstructionNaming.CanonicalNameFor(Path.Combine(folder, "инструкция.pdf.lnk"), Version));
        // Заглушка в папке без версии живёт под общим именем — переименовывать её не во что.
        Assert.Null(InstructionNaming.CanonicalNameFor(Path.Combine(folder, InstructionStub.GenericFileName), Version));
        // Версия неизвестна — переименовывать не во что.
        Assert.Null(InstructionNaming.CanonicalNameFor(Path.Combine(folder, "скан.pdf"), ""));

        Assert.Equal($"инструкция_{Version}.pdf",
            InstructionNaming.CanonicalNameFor(Path.Combine(folder, "Инструкция по эксплуатации.pdf"), Version));
    }

    [Fact]
    public void EnsureCanonicalName_RenamesOnDisk_IncludingCaseOnlyDifference()
    {
        var folder = Path.Combine(Root, "Инструкция");
        var wrong = Touch(folder, "Инструкция по эксплуатации.pdf");

        var after = InstructionNaming.EnsureCanonicalName(wrong, Version);

        Assert.Equal(Path.Combine(folder, $"инструкция_{Version}.pdf"), after);
        Assert.True(File.Exists(after));
        Assert.False(File.Exists(wrong));

        // Различие только в регистре расширения: обычный File.Move на таком падает «файл уже
        // существует», поэтому переименование идёт через временное имя.
        var upper = Touch(folder, $"инструкция_{Version}.DOCX");
        var fixedCase = InstructionNaming.EnsureCanonicalName(upper, Version);
        Assert.Equal($"инструкция_{Version}.docx", Path.GetFileName(fixedCase));
        Assert.True(File.Exists(fixedCase));
    }

    [Fact]
    public void EnsureCanonicalName_NeverClobbersSomeoneElsesFile()
    {
        var folder = Path.Combine(Root, "Инструкция");
        Touch(folder, $"инструкция_{Version}.pdf", "правильный");
        var other = Touch(folder, "второй скан.pdf", "чужой");

        var after = InstructionNaming.EnsureCanonicalName(other, Version);

        // Имя занято другим файлом — свой остаётся под своим именем, а чужой цел.
        Assert.Equal(other, after);
        Assert.Equal("правильный", File.ReadAllText(Path.Combine(folder, $"инструкция_{Version}.pdf")));
        Assert.Equal("чужой", File.ReadAllText(other));
    }

    [Fact]
    public void Copy_GivesTheFileItsCanonicalName_NextToFirmware()
    {
        using var source = new TempRoot();

        var folder = Path.Combine(Root, "ПО", "ПЖ", "SMH5", "Инструкция");
        var src = Touch(source.Path, "Инструкция по эксплуатации.docx");
        var warnings = new List<string>();

        var placement = InstructionStorage.Copy(src, folder, Root, warnings, Version);

        var expected = $"инструкция_{Version}.docx";
        Assert.Equal(expected, Path.GetFileName(placement.ActualPath));
        Assert.Equal(expected, Path.GetFileName(placement.StoredPath));
        Assert.True(File.Exists(placement.ActualPath));
        // Файл лежит рядом с прошивкой, на первом диске.
        Assert.StartsWith(Root, placement.StoredPath, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(warnings);
    }

    /// <summary>Перезаливка обязана заменить документ ПО ТОМУ ЖЕ адресу, а не лечь рядом под именем
    /// источника: по каноническому пути ведёт напечатанный QR, и оставить под ним прошлую редакцию —
    /// худшее из возможного (наклейка молча выдаёт устаревшую инструкцию).</summary>
    [Fact]
    public void Copy_ReplacesThePreviousRevisionUnderTheCanonicalName()
    {
        using var source = new TempRoot();
        var folder = Path.Combine(Root, "Инструкция");

        InstructionStorage.Copy(Touch(source.Path, "Инструкция ред.1.pdf", "первая редакция"), folder, Root,
            new List<string>(), Version);
        var placement = InstructionStorage.Copy(Touch(source.Path, "Инструкция ред.2.pdf", "вторая редакция"),
            folder, Root, new List<string>(), Version);

        var canonical = Path.Combine(folder, $"инструкция_{Version}.pdf");
        Assert.Equal(canonical, placement.StoredPath);
        Assert.Equal("вторая редакция", File.ReadAllText(canonical));
        // Ровно один файл: второй редакции незачем ложиться рядом под своим именем.
        Assert.Single(Directory.EnumerateFiles(folder));
    }

    [Fact]
    public void Copy_WithoutVersion_KeepsTheSourceName()
    {
        using var source = new TempRoot();
        var folder = Path.Combine(Root, "Инструкция");
        var src = Touch(source.Path, "скан.pdf");

        var placement = InstructionStorage.Copy(src, folder, Root, new List<string>());

        Assert.Equal("скан.pdf", Path.GetFileName(placement.StoredPath));
    }

    // ── Заглушка ─────────────────────────────────────────────────────────────

    [Fact]
    public void Stub_IsNotAnInstruction_ForAnyResolver()
    {
        var folder = Path.Combine(Root, "Инструкция");
        Directory.CreateDirectory(folder);
        InstructionStub.EnsureIn(folder, Version, new FakeStubWriter());
        var stub = InstructionStub.PathFor(folder, Version);

        // Ни «самый свежий документ», ни docx/pdf для печати — иначе «инструкция ✓» на карточке и
        // печать начали бы врать ровно там, где документа ещё нет. И это при том, что имя файла у
        // заглушки ровно то же самое, что будет у настоящего документа.
        Assert.True(File.Exists(stub));
        Assert.Null(DocFileResolver.LatestFileIn(folder));
        Assert.Null(DocFileResolver.Resolve(storedPath: null, folder));

        var doc = InstructionDocResolver.Resolve(storedPath: null, folder);
        Assert.False(doc.HasAny);
        Assert.False(doc.CanPrint);
        Assert.Null(doc.Pdf);

        // А записанный к версии путь, указывающий прямо на заглушку, тоже не документ.
        Assert.Null(DocFileResolver.Resolve(stub, sharedFolder: null));
    }

    /// <summary>Главное свойство заглушки: она занимает ТО ЖЕ каноническое имя, под которым потом
    /// ляжет настоящий документ. Наклейку с QR печатают и клеят на шкаф до того, как инструкцию
    /// дописали, и переклеивать её потом никто не пойдёт.</summary>
    [Fact]
    public void Stub_TakesTheCanonicalName_SoThePrintedQrKeepsWorking()
    {
        using var source = new TempRoot();
        var folder = Path.Combine(Root, "Инструкция");
        Directory.CreateDirectory(folder);

        InstructionStub.EnsureIn(folder, Version, new FakeStubWriter());
        var linkTarget = InstructionStub.ExistingIn(folder);
        Assert.Equal(Path.Combine(folder, $"инструкция_{Version}.pdf"), linkTarget);

        // Инструкцию дописали и приложили — под ЛЮБЫМ именем.
        InstructionStorage.Copy(Touch(source.Path, "Инструкция по эксплуатации.pdf"), folder, Root,
            new List<string>(), Version);

        // Тот же самый путь, но теперь это настоящий документ: QR, напечатанный вчера, открывает его.
        Assert.True(File.Exists(linkTarget!));
        Assert.False(InstructionStub.IsStub(linkTarget));
        Assert.Equal(linkTarget, DocFileResolver.Resolve(storedPath: null, folder));
        Assert.Single(Directory.EnumerateFiles(folder));
    }

    /// <summary>Раз имя совпадает с настоящим документом, отличать их приходится по содержимому:
    /// метка внутри файла. Иначе «инструкция ✓» загоралась бы от заглушки, а настоящий документ с
    /// каноническим именем, наоборот, считался бы заглушкой.</summary>
    [Fact]
    public void IsStub_TellsTheStubFromARealDocumentWithTheSameName()
    {
        var folder = Path.Combine(Root, "Инструкция");
        Directory.CreateDirectory(folder);
        InstructionStub.EnsureIn(folder, Version, new FakeStubWriter());
        Assert.True(InstructionStub.IsStub(InstructionStub.PathFor(folder, Version)));

        var real = Path.Combine(Root, "Другая");
        Touch(real, $"инструкция_{Version}.pdf", "настоящий документ без метки");
        Assert.False(InstructionStub.IsStub(Path.Combine(real, $"инструкция_{Version}.pdf")));

        // Файла нет вовсе / шара отвалилась — «не знаем» значит «не заглушка».
        Assert.False(InstructionStub.IsStub(Path.Combine(real, "нет-такого.pdf")));
        Assert.False(InstructionStub.IsStub(null));
    }

    [Fact]
    public void EnsureIn_FillsEmptyFolder_IsIdempotent_AndStepsAsideForARealDocument()
    {
        var writer = new FakeStubWriter();
        var folder = Path.Combine(Root, "Инструкция");
        Directory.CreateDirectory(folder);

        Assert.True(InstructionStub.EnsureIn(folder, Version, writer));
        var stub = InstructionStub.PathFor(folder, Version);
        Assert.True(File.Exists(stub));
        Assert.StartsWith(InstructionStub.Text, File.ReadAllText(stub));

        // Повторный вызов ничего не пишет — иначе каждая перестройка диска переписывала бы файл.
        Assert.False(InstructionStub.EnsureIn(folder, Version, writer));
        Assert.Single(writer.Written);

        // Документ положили руками, мимо программы — заглушка обязана уйти сама.
        Touch(folder, "Инструкция по эксплуатации.pdf");
        Assert.False(InstructionStub.EnsureIn(folder, Version, writer));
        Assert.False(File.Exists(stub));
    }

    /// <summary>У общей папки «Инструкция» контроллера версии нет — она принадлежит всем его версиям
    /// сразу. Там заглушка ложится под общим именем, одна на папку.</summary>
    [Fact]
    public void EnsureIn_WithoutAVersion_UsesTheGenericName()
    {
        var writer = new FakeStubWriter();
        var folder = Path.Combine(Root, "Инструкция");

        Assert.True(InstructionStub.EnsureIn(folder, versionRaw: null, writer));
        Assert.True(File.Exists(Path.Combine(folder, InstructionStub.GenericFileName)));
        // Вторая версия той же папки второй заглушки не добавляет.
        Assert.False(InstructionStub.EnsureIn(folder, Version, writer));
        Assert.Single(Directory.EnumerateFiles(folder));
    }

    [Fact]
    public void EnsureForVersion_PutsTheStubNextToFirmware()
    {
        var writer = new FakeStubWriter();
        var folder = Path.Combine(Root, "ПО", "ПЖ", "SMH5", "Инструкция");

        var created = InstructionStub.EnsureForVersion(folder, Root, Version, writer);

        Assert.Equal(1, created);
        Assert.True(File.Exists(InstructionStub.PathFor(folder, Version)));
    }

    [Fact]
    public void Copy_RemovesTheStub_WhenTheRealDocumentArrives()
    {
        using var source = new TempRoot();
        var writer = new FakeStubWriter();

        var folder = Path.Combine(Root, "ПО", "ПЖ", "SMH5", "Инструкция");
        InstructionStub.EnsureForVersion(folder, Root, Version, writer);

        InstructionStorage.Copy(Touch(source.Path, "готовая.pdf"), folder, Root, new List<string>(), Version);

        // Под каноническим именем рядом с прошивкой лежит настоящий документ, а не заглушка.
        var copy = Path.Combine(folder, $"инструкция_{Version}.pdf");
        Assert.True(File.Exists(copy));
        Assert.False(InstructionStub.IsStub(copy));
        Assert.Null(InstructionStub.ExistingIn(folder));
    }

    /// <summary>В папке лежит ярлык-пережиток на документ — заглушке «в разработке» рядом с ним не
    /// место: это прямая ложь.</summary>
    [Fact]
    public void EnsureForVersion_PlacesNothing_WhenAShortcutPointsToTheDocument()
    {
        var writer = new FakeStubWriter();
        var folder = Path.Combine(Root, "ПО", "ПЖ", "SMH5", "Инструкция");
        Touch(folder, $"инструкция_{Version}.pdf.lnk", "lnk"); // указатель на документ

        Assert.Equal(0, InstructionStub.EnsureForVersion(folder, Root, Version, writer));
        Assert.Empty(writer.Written);
        Assert.Null(InstructionStub.ExistingIn(folder));
        Assert.False(InstructionStub.EnsureIn(folder, Version, writer));
    }

    [Fact]
    public void ApplyStructurePlan_FillsEveryInstructionFolder()
    {
        var writer = new FakeStubWriter();
        var plan = _hierarchy.PlanStructure(Root);

        HierarchyService.ApplyStructurePlan(plan, writer);

        var instructionFolders = plan.Folders
            .Where(f => string.Equals(Path.GetFileName(f), HierarchyFolders.Instructions, StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(instructionFolders);
        Assert.All(instructionFolders, f => Assert.True(File.Exists(Path.Combine(f, InstructionStub.GenericFileName)), f));

        // Без писателя заглушек поведение ровно прежнее — папки создаются пустыми.
        using var clean = new TempRoot();
        HierarchyService.ApplyStructurePlan(_hierarchy.PlanStructure(clean.Path));
        Assert.Empty(Directory.EnumerateFiles(clean.Path, "*.pdf", SearchOption.AllDirectories));
    }

    [Fact]
    public void ApplyStructurePlan_DoesNotShoutOverAnExistingInstruction()
    {
        var writer = new FakeStubWriter();
        var plan = _hierarchy.PlanStructure(Root);
        HierarchyService.ApplyStructurePlan(plan);

        var folder = plan.Folders.First(f =>
            string.Equals(Path.GetFileName(f), HierarchyFolders.Instructions, StringComparison.Ordinal));
        Touch(folder, "уже лежит.pdf");

        HierarchyService.ApplyStructurePlan(plan, writer);

        // Документ уже лежит — заглушке рядом с ним места нет НИ ОДНОЙ. Страница с обращением в
        // сервис тоже не кладётся файлом: она вшивается в сам документ при выкладке на хостинг
        // (см. ServicePageStitcher), а на диске рядом с инструкцией ничего лишнего не появляется.
        Assert.Null(InstructionStub.ExistingIn(folder));
        Assert.True(File.Exists(Path.Combine(folder, "уже лежит.pdf")), "документ не тронут");
        Assert.Single(Directory.EnumerateFiles(folder));
    }

    // ── Перестройка диска ────────────────────────────────────────────────────

    /// <summary>Перестройка правит имена только там, где инструкция принадлежит КОНКРЕТНОЙ версии
    /// (своя папка внутри версии) — в общей папке контроллера документ принадлежит всем его версиям
    /// сразу, и приписать его одной из них было бы выдумкой.</summary>
    [Fact]
    public void Migrator_RenamesInstructionsInsideVersionFolders_AndPlacesStubs()
    {
        var versionDir = Path.Combine(Root, "ПО", "ПЖ", "SMH5", Version);
        VersionLayout.EnsureFolders(versionDir);
        var ownInstructions = VersionLayout.SlotFolder(versionDir, HierarchyFolders.Instructions);
        Touch(ownInstructions, "Инструкция по эксплуатации.pdf");

        // Вторая версия — без инструкции вовсе: ей полагается заглушка.
        var emptyVersionDir = Path.Combine(Root, "ПО", "ПЖ", "SMH5", "2.1.0042.0002");
        VersionLayout.EnsureFolders(emptyVersionDir);

        var input = new DiskLayoutMigrator.MigrationInput(Root,
            new List<FwVersionRecord>
            {
                new() { VersionRaw = Version, DiskPath = versionDir },
                new() { VersionRaw = "2.1.0042.0002", DiskPath = emptyVersionDir },
            },
            new DiskLayoutMigrator.MigrationOptions(RenameFirmwareFiles: false, FixInstructions: true));

        var writer = new FakeStubWriter();
        var plan = DiskLayoutMigrator.Apply(DiskLayoutMigrator.Plan(input), null, stubs: writer);

        Assert.True(File.Exists(Path.Combine(ownInstructions, $"инструкция_{Version}.pdf")));
        Assert.False(File.Exists(Path.Combine(ownInstructions, "Инструкция по эксплуатации.pdf")));
        // У версии с документом заглушки нет, у пустой — есть, под именем СВОЕЙ версии: ссылка на неё
        // уже такая же, как будет у готового документа.
        Assert.Null(InstructionStub.ExistingIn(ownInstructions));
        Assert.True(File.Exists(InstructionStub.PathFor(
            VersionLayout.SlotFolder(emptyVersionDir, HierarchyFolders.Instructions), "2.1.0042.0002")));

        Assert.Contains(plan.Ops, o => o.Kind == DiskLayoutMigrator.OpKind.RenameInstruction && o.Status == "ok");

        // Повторный прогон не делает ничего — идемпотентность, на которой стоит вся перестройка.
        var again = DiskLayoutMigrator.Apply(DiskLayoutMigrator.Plan(input), null, stubs: writer);
        Assert.DoesNotContain(again.Ops, o => o.Status == "ok");
    }

    /// <summary>Галочка снята — ни одной операции по инструкциям, поведение ровно прежнее.</summary>
    [Fact]
    public void Migrator_WithoutTheOption_PlansNothingAboutInstructions()
    {
        var versionDir = Path.Combine(Root, "ПО", "ПЖ", "SMH5", Version);
        VersionLayout.EnsureFolders(versionDir);

        var input = new DiskLayoutMigrator.MigrationInput(Root,
            new List<FwVersionRecord> { new() { VersionRaw = Version, DiskPath = versionDir } },
            new DiskLayoutMigrator.MigrationOptions(RenameFirmwareFiles: false));

        var plan = DiskLayoutMigrator.Plan(input);

        Assert.DoesNotContain(plan.Ops, o => o.Kind is DiskLayoutMigrator.OpKind.RenameInstruction
            or DiskLayoutMigrator.OpKind.PlaceInstructionStub);
    }
}
