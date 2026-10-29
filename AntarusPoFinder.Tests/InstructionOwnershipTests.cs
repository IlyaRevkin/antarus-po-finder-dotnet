using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>«Прошивки пропали, загрузили заново, нумерация пошла по новой» — что при этом происходит
/// с инструкцией. Папка версии названа самим номером, поэтому новая прошивка получает РОВНО ТОТ ЖЕ
/// путь, что носила прежняя, и документ прежней жизни номера лежит в ней как ни в чём не бывало.
/// Проверяем оба требования Ильи сразу: прежняя инструкция не исчезает и к новой прошивке молча не
/// приписывается.</summary>
public class InstructionOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "owner-" + Guid.NewGuid().ToString("N"));

    private string VersionDir(string version)
    {
        var dir = Path.Combine(_root, "ПЖ", "SMH5", version);
        VersionLayout.EnsureFolders(dir);
        return dir;
    }

    private static string InstructionFolder(string versionDir) =>
        VersionLayout.SlotFolder(versionDir, HierarchyFolders.Instructions);

    private static string PutDocument(string folder, string name, string text)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* временная папка */ }
    }

    [Fact]
    public void Инструкция_прежней_жизни_номера_уезжает_в_архив_а_не_стирается()
    {
        var dir = VersionDir("2.1.0004.0002");
        var folder = InstructionFolder(dir);
        var warnings = new List<string>();

        // Прежняя жизнь номера: версия загружена, инструкция положена, папка помечена.
        InstructionOwnership.Claim(dir, folder, "2.1.0004.0002", folderOutlivedItsVersion: false, warnings);
        PutDocument(folder, "инструкция_2.1.0004.0002.pdf", "инструкция прежней прошивки");

        // Прошивки потеряли, завели заново — тот же номер, но это уже другая версия.
        var claim = InstructionOwnership.Claim(dir, folder, "2.1.0004.0001",
            folderOutlivedItsVersion: true, warnings);

        Assert.True(claim.Archived);
        Assert.False(File.Exists(Path.Combine(folder, "инструкция_2.1.0004.0002.pdf")),
            "чужой документ не должен остаться на месте действующей инструкции");

        var archive = Path.Combine(folder, InstructionStorage.PreviousFolderName);
        var kept = Directory.GetFiles(archive).Single();
        Assert.Equal("инструкция прежней прошивки", File.ReadAllText(kept));
        Assert.Contains(warnings, w => w.Contains("прежней версии"));
    }

    [Fact]
    public void Перезаливка_той_же_версии_документа_не_трогает()
    {
        var dir = VersionDir("2.1.0004.0002");
        var folder = InstructionFolder(dir);
        var warnings = new List<string>();

        InstructionOwnership.Claim(dir, folder, "2.1.0004.0002", folderOutlivedItsVersion: false, warnings);
        var doc = PutDocument(folder, "инструкция_2.1.0004.0002.pdf", "та же самая инструкция");

        var claim = InstructionOwnership.Claim(dir, folder, "2.1.0004.0002",
            folderOutlivedItsVersion: false, warnings);

        Assert.False(claim.Archived);
        Assert.Equal("та же самая инструкция", File.ReadAllText(doc));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Без_метки_документ_той_же_версии_остаётся_но_о_нём_предупреждают()
    {
        var dir = VersionDir("2.1.0004.0002");
        var folder = InstructionFolder(dir);
        var warnings = new List<string>();

        // Папку завёл прежний клиент — метки нет, имя документа называет этот же номер. Доказательств
        // чужого происхождения нет, а стирать человеку инструкцию по догадке нельзя.
        var doc = PutDocument(folder, "инструкция_2.1.0004.0002.pdf", "неизвестно чья");

        var claim = InstructionOwnership.Claim(dir, folder, "2.1.0004.0002",
            folderOutlivedItsVersion: true, warnings);

        Assert.False(claim.Archived);
        Assert.True(File.Exists(doc));
        Assert.Contains(warnings, w => w.Contains("уже лежал документ"));
        Assert.NotNull(InstructionOwnership.Read(folder));
    }

    [Fact]
    public void Общая_папка_контроллера_не_прибирается_никогда()
    {
        var dir = VersionDir("2.1.0004.0002");
        var controllerFolder = Path.Combine(Path.GetDirectoryName(dir)!, HierarchyFolders.Instructions);
        Directory.CreateDirectory(controllerFolder);
        var doc = PutDocument(controllerFolder, "инструкция_2.1.0003.0001.pdf", "общая на весь контроллер");
        var warnings = new List<string>();

        var claim = InstructionOwnership.Claim(dir, controllerFolder, "2.1.0004.0002",
            folderOutlivedItsVersion: true, warnings);

        Assert.False(claim.Archived);
        Assert.True(File.Exists(doc), "в общей папке документ принадлежит всем версиям контроллера");
        Assert.Empty(warnings);
    }

    [Fact]
    public void Метка_не_считается_файлом_в_папке_и_не_считается_документом()
    {
        var dir = VersionDir("2.1.0004.0002");
        var folder = InstructionFolder(dir);
        InstructionOwnership.Mark(folder, InstructionOwnership.NewOwnerKey(), "2.1.0004.0002");

        // Иначе своя пустая папка версии стала бы «непустой» и перебила общую папку контроллера —
        // ровно тот случай, что и Thumbs.db.
        var controllerDir = Path.GetDirectoryName(dir)!;
        var sharedFolder = Path.Combine(controllerDir, HierarchyFolders.Instructions);
        Directory.CreateDirectory(sharedFolder);
        PutDocument(sharedFolder, "инструкция.pdf", "документ контроллера");

        Assert.Equal(sharedFolder,
            VersionLayout.SlotBestReadFolder(dir, controllerDir, HierarchyFolders.Instructions));
        Assert.True(DocFileResolver.IsNotADocument(Path.Combine(folder, InstructionOwnership.MarkerFileName)));
    }

    [Fact]
    public void Прежняя_редакция_из_архива_не_выдаётся_за_действующую_инструкцию()
    {
        var dir = VersionDir("2.1.0004.0002");
        var folder = InstructionFolder(dir);
        var warnings = new List<string>();

        var first = Path.Combine(_root, "старая.pdf");
        File.WriteAllText(first, "редакция с ошибкой");
        InstructionStorage.Copy(first, folder, _root, warnings, "2.1.0004.0002");

        var second = Path.Combine(_root, "новая.pdf");
        File.WriteAllText(second, "исправленная редакция");
        var placed = InstructionStorage.Copy(second, folder, _root, warnings, "2.1.0004.0002");

        // Архивная копия лежит в подпапке и по времени изменения может оказаться свежее (файл
        // копируется со своей датой). Резолвер обязан её игнорировать, иначе по QR открывалась бы
        // та самая редакция с ошибкой, ради замены которой всё и делалось.
        var archive = Path.Combine(folder, InstructionStorage.PreviousFolderName);
        foreach (var file in Directory.GetFiles(archive))
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(1));

        var doc = InstructionDocResolver.Resolve(placed.StoredPath, folder);
        Assert.Equal("исправленная редакция", File.ReadAllText(doc.Newest!));
    }
}
