using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Набор перетащенных файлов собирается в одну временную папку, чтобы дальше по коду это
/// был обычный сценарий «перетащили папку» (см. DropStagingService). Раньше из набора молча брался
/// первый файл — остальные исчезали без единого сообщения.</summary>
public class DropStagingServiceTests : IDisposable
{
    private readonly TempRoot _root = new();
    private string Src(string name, string content = "x")
    {
        var path = Path.Combine(_root.Path, "src", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => _root.Dispose();

    private string TempParent => Path.Combine(_root.Path, "temp");

    [Fact]
    public void Stage_SinglePath_ReturnsItUnchanged()
    {
        var file = Src("only.psl");
        Assert.Equal(file, DropStagingService.Stage(new[] { file }, TempParent));
        Assert.False(Directory.Exists(TempParent)); // временная папка ради одного файла не создаётся
    }

    [Fact]
    public void Stage_SeveralFiles_CollectsAllIntoOneFolderNamedAfterTheFirst()
    {
        var a = Src("Project.psl", "psl");
        var b = Src("driver.dll", "dll");
        var c = Src("readme.txt", "txt");

        var staged = DropStagingService.Stage(new[] { a, b, c }, TempParent);

        Assert.Equal("Project", Path.GetFileName(staged));
        Assert.Equal(new[] { "Project.psl", "driver.dll", "readme.txt" }.OrderBy(x => x),
                     Directory.GetFiles(staged).Select(Path.GetFileName).OrderBy(x => x));
        Assert.Equal("psl", File.ReadAllText(Path.Combine(staged, "Project.psl")));
    }

    [Fact]
    public void Stage_FolderAmongFiles_IsCopiedWithItsTree()
    {
        var file = Src("Main.psl");
        var dir = Path.Combine(_root.Path, "src", "Driver");
        Directory.CreateDirectory(Path.Combine(dir, "nested"));
        File.WriteAllText(Path.Combine(dir, "nested", "lib.bin"), "bin");

        var staged = DropStagingService.Stage(new[] { file, dir }, TempParent);

        Assert.True(File.Exists(Path.Combine(staged, "Main.psl")));
        Assert.True(File.Exists(Path.Combine(staged, "Driver", "nested", "lib.bin")));
    }

    [Fact]
    public void Stage_SameFileNameFromDifferentFolders_KeepsBoth()
    {
        var a = Path.Combine(_root.Path, "one", "app.exe");
        var b = Path.Combine(_root.Path, "two", "app.exe");
        foreach (var p in new[] { a, b })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, p);
        }

        var staged = DropStagingService.Stage(new[] { a, b }, TempParent);

        Assert.Equal(2, Directory.GetFiles(staged).Length);
        Assert.True(File.Exists(Path.Combine(staged, "app.exe")));
        Assert.True(File.Exists(Path.Combine(staged, "app_2.exe")));
    }

    [Fact]
    public void Cleanup_RemovesStagedFolder_ButNeverAnOriginalPath()
    {
        var a = Src("Project.psl");
        var b = Src("driver.dll");
        var staged = DropStagingService.Stage(new[] { a, b }, TempParent);

        DropStagingService.Cleanup(staged);
        Assert.False(Directory.Exists(staged));

        // Оригинальные (не временные) пути Cleanup обязан игнорировать — вызывающий код зовёт его на
        // любом выбранном пути, не разбираясь, временный он или настоящий файл оператора.
        DropStagingService.Cleanup(Path.GetDirectoryName(a));
        Assert.True(File.Exists(a));
    }

    [Fact]
    public void Stage_EmptyList_Throws() =>
        Assert.Throws<ArgumentException>(() => DropStagingService.Stage(Array.Empty<string>(), TempParent));
}
