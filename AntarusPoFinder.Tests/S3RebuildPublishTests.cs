using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Выкладка на хостинг при «Перестроить структуру диска» и заглушек «в разработке» при
/// загрузке версии (решение Ивана Герасимова: инструкции дублируются на хостинг, а не ярлыками).
///
/// Живой заливки в бакет тут нет и быть не должно — ключей на день сдачи ещё не выдали, а проверяется
/// поведение программы: ЧТО и по какому пути на первом диске уходит на выкладку. Подставной выкладчик
/// запоминает, что ему отдали, и отвечает готовым адресом.</summary>
public class S3RebuildPublishTests
{
    /// <summary>Запоминающий выкладчик: живой сети нет, важно лишь то, какие пути на первом диске
    /// программа отдаёт на выкладку.</summary>
    private sealed class RecordingPublisher : IInstructionPublisher
    {
        public List<string> Published { get; } = new();

        public string? Publish(string actualPath, string pathOnFirstDisk, string firstDiskRoot, List<string> warnings)
        {
            Published.Add(pathOnFirstDisk);
            return "https://fs.elitacompany.ru/опубликовано";
        }
    }

    /// <summary>Заглушку рисует WPF (App/Services/InstructionStubWriter), в Core его нет — подставляем
    /// файл-пустышку. Проверяем не рисование, а то, что заглушка уходит на хостинг.</summary>
    private sealed class FakeStubWriter : IInstructionStubWriter
    {
        public void Write(string path, string text) => File.WriteAllText(path, text);
    }

    // ── Заглушка при загрузке версии ─────────────────────────────────────────

    /// <summary>Инструкции к версии нет — заглушка «в разработке» и кладётся на диск, и уходит на
    /// хостинг под каноническим именем: наклейку с QR печатают и клеят ДО того, как инструкцию
    /// дописали, и по постоянной ссылке должно открываться хотя бы «в разработке».</summary>
    [Fact]
    public void EnsureForVersion_PublishesTheStub_UnderTheCanonicalName()
    {
        using var first = new TempRoot();
        var folder = Path.Combine(first.Path, "ПО", "ПЖ", "SMH5", "Инструкция");
        var publisher = new RecordingPublisher();

        var created = InstructionStub.EnsureForVersion(folder, first.Path,
            "2.1.0042.0001", new FakeStubWriter(), warnings: null, publisher);

        Assert.Equal(1, created);
        Assert.True(File.Exists(Path.Combine(folder, "инструкция_2.1.0042.0001.pdf")));
        var published = Assert.Single(publisher.Published);
        Assert.EndsWith("инструкция_2.1.0042.0001.pdf", published);
    }

    /// <summary>Хостинг не настроен (ключей нет) — выкладчик null, и всё работает как раньше: заглушка
    /// на диск ложится, ничего не падает, выкладки просто нет.</summary>
    [Fact]
    public void EnsureForVersion_WithoutAPublisher_StillPlacesTheStub()
    {
        using var first = new TempRoot();
        var folder = Path.Combine(first.Path, "ПО", "Инструкция");

        var created = InstructionStub.EnsureForVersion(folder, first.Path,
            "2.1.0042.0001", new FakeStubWriter(), warnings: null, publisher: null);

        Assert.Equal(1, created);
        Assert.True(File.Exists(Path.Combine(folder, "инструкция_2.1.0042.0001.pdf")));
    }

    // ── Перестройка диска ────────────────────────────────────────────────────

    /// <summary>«Перестроить структуру диска» с приведением инструкций: у версии, где инструкции нет,
    /// в её папку «Инструкция» ложится заглушка И уходит на хостинг. Ключ считается от пути на первом
    /// диске (см. InstructionPublisher), поэтому в выложенном пути видна папка «Инструкция».</summary>
    [Fact]
    public void Rebuild_PublishesTheInstructionStub_ThatLandsOnTheFirstDisk()
    {
        using var root = new TempRoot();
        var dir = Path.Combine(root.Path, "ПО", "ПЖ", "2.0", "SMH5", "1.0.0004.0003");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "fw.psl"), "x");

        var record = new Core.Domain.FwVersionRecord
        {
            VersionRaw = "1.0.0004.0003",
            DiskPath = dir,
            Filename = "fw.psl",
        };

        // Собрать файлы в «Прошивка\» (создаёт и папку «Инструкция») + привести инструкции к правилам
        // (заглушка там, где документа нет).
        var options = new DiskLayoutMigrator.MigrationOptions(
            RenameFirmwareFiles: false,
            FoldFilesIntoVersion: true, MoveOpcIntoController: false, FixInstructions: true);
        var input = new DiskLayoutMigrator.MigrationInput(root.Path, new[] { record }, options);

        var plan = DiskLayoutMigrator.Plan(input);
        var publisher = new RecordingPublisher();

        DiskLayoutMigrator.Apply(plan, renamed: null, stubs: new FakeStubWriter(),
            publisher: publisher, firstRoot: root.Path);

        // Заглушка легла на диск в папку «Инструкция» версии…
        var instrFolder = Path.Combine(dir, "Инструкция");
        Assert.True(File.Exists(Path.Combine(instrFolder, "инструкция_1.0.0004.0003.pdf")));
        // …и её папка ушла на хостинг (ключ от пути на первом диске — видна «Инструкция»).
        Assert.NotEmpty(publisher.Published);
        Assert.Contains(publisher.Published, p => p.Replace('\\', '/').Contains("/Инструкция"));
    }

    /// <summary>Тот же прогон без выкладчика (ключей нет) — перестройка идёт как раньше, заглушка на
    /// диске появляется, ничего не падает.</summary>
    [Fact]
    public void Rebuild_WithoutAPublisher_StillRebuilds_AndPublishesNothing()
    {
        using var root = new TempRoot();
        var dir = Path.Combine(root.Path, "ПО", "ПЖ", "2.0", "SMH5", "1.0.0004.0003");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "fw.psl"), "x");

        var record = new Core.Domain.FwVersionRecord
        {
            VersionRaw = "1.0.0004.0003",
            DiskPath = dir,
            Filename = "fw.psl",
        };

        var options = new DiskLayoutMigrator.MigrationOptions(
            RenameFirmwareFiles: false,
            FoldFilesIntoVersion: true, MoveOpcIntoController: false, FixInstructions: true);
        var input = new DiskLayoutMigrator.MigrationInput(root.Path, new[] { record }, options);

        var plan = DiskLayoutMigrator.Apply(DiskLayoutMigrator.Plan(input), renamed: null,
            stubs: new FakeStubWriter(), publisher: null, firstRoot: root.Path);

        Assert.True(File.Exists(Path.Combine(dir, "Инструкция", "инструкция_1.0.0004.0003.pdf")));
        Assert.Contains(plan.Ops, o => o.Kind == DiskLayoutMigrator.OpKind.PlaceInstructionStub && o.Status == "ok");
    }
}
