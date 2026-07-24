using System;
using System.IO;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Карта ВВ / карта Modbus / инструкция на карточке поиска. Две жалобы разом: пункт меню
/// висел по одному лишь заполненному пути в базе (файла на диске уже нет — «зачем она»), и открывал
/// документ, приложенный к конкретной версии, вместо актуального. Оба ответа даёт этот резолвер:
/// null → пункта в меню нет; иначе → самый свежий файл общей папки документа.</summary>
public class DocFileResolverTests
{
    private static string Touch(string root, string name, DateTime? written = null)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        if (written is { } w) File.SetLastWriteTimeUtc(path, w);
        return path;
    }

    [Fact]
    public void StoredFile_UsedWhenItStillExists()
    {
        using var root = new TempRoot();
        var stored = Touch(root.Path, "map.pdf");

        Assert.Equal(stored, DocFileResolver.Resolve(stored, sharedFolder: null));
    }

    [Fact]
    public void MissingStoredFile_FallsBackToSharedFolder()
    {
        using var root = new TempRoot();
        var shared = Path.Combine(root.Path, "Карта ВВ");
        var actual = Touch(shared, "map-v3.xlsx");

        // Ровно тот случай из жалобы: путь в базе есть, файла по нему нет — открываем актуальный.
        Assert.Equal(actual, DocFileResolver.Resolve(Path.Combine(root.Path, "gone.xlsx"), shared));
    }

    [Fact]
    public void SharedFolder_ReturnsNewestFile()
    {
        using var root = new TempRoot();
        var shared = Path.Combine(root.Path, "Инструкция");
        Touch(shared, "instruction-2024.docx", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newest = Touch(shared, "instruction-2026.docx", new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        Touch(shared, "old/instruction-2020.docx", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(newest, DocFileResolver.Resolve(storedPath: null, shared));
    }

    [Fact]
    public void StoredFolder_ReturnsNewestFileInIt()
    {
        using var root = new TempRoot();
        Touch(root.Path, "map-old.xlsx", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newest = Touch(root.Path, "map-new.xlsx", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(newest, DocFileResolver.Resolve(root.Path, sharedFolder: null));
    }

    [Fact]
    public void EmptySharedFolder_ResolvesToNull()
    {
        using var root = new TempRoot();
        var shared = Path.Combine(root.Path, "Карта Modbus");
        Directory.CreateDirectory(shared);

        // Папка есть, файлов нет — пункт меню показывать нечем.
        Assert.Null(DocFileResolver.Resolve(storedPath: null, shared));
    }

    [Fact]
    public void NothingAtAll_ResolvesToNull()
    {
        using var root = new TempRoot();

        Assert.Null(DocFileResolver.Resolve(Path.Combine(root.Path, "gone.pdf"),
            Path.Combine(root.Path, "нет такой папки")));
        Assert.Null(DocFileResolver.Resolve(null, null));
    }
}
