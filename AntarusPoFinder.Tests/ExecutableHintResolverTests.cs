using System.IO;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Подсказка «какой файл в папке исполняемый» (FwVersionRecord.ExecutableHint/
/// HmiExecutableHint) с Раунда 50 может указывать на файл во вложенной папке — раньше это было
/// просто имя файла в корне. Старый формат остаётся частным случаем нового, поэтому здесь явно
/// проверяются оба.</summary>
public class ExecutableHintResolverTests
{
    private static string Touch(string root, params string[] parts)
    {
        var path = Path.Combine(root, Path.Combine(parts));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void Resolve_FindsFileInRoot_LegacyPlainName()
    {
        using var root = new TempRoot();
        var expected = Touch(root.Path, "project.psl");

        Assert.Equal(expected, ExecutableHintResolver.Resolve(root.Path, "project.psl"));
    }

    [Fact]
    public void Resolve_FindsFileInSubfolder()
    {
        using var root = new TempRoot();
        var expected = Touch(root.Path, "Driver", "App.exe");

        Assert.Equal(expected, ExecutableHintResolver.Resolve(root.Path, @"Driver\App.exe"));
        Assert.Equal(expected, ExecutableHintResolver.Resolve(root.Path, "Driver/App.exe"));
    }

    [Fact]
    public void Resolve_ReturnsNull_ForMissingEmptyOrEscapingHints()
    {
        using var root = new TempRoot();
        Touch(root.Path, "project.psl");

        Assert.Null(ExecutableHintResolver.Resolve(root.Path, "нет-такого.psl"));
        Assert.Null(ExecutableHintResolver.Resolve(root.Path, ""));
        Assert.Null(ExecutableHintResolver.Resolve(root.Path, null));
        Assert.Null(ExecutableHintResolver.Resolve("", "project.psl"));
        // Значение могло приехать с другой машины через синхронизацию конфига — выход за пределы
        // папки версии не должен открывать посторонний файл.
        Assert.Null(ExecutableHintResolver.Resolve(root.Path, @"..\project.psl"));
        Assert.Null(ExecutableHintResolver.Resolve(root.Path, @"C:\Windows\notepad.exe"));
    }

    [Fact]
    public void Normalize_TrimsSeparatorsAndUnifiesSlashes()
    {
        Assert.Equal(Path.Combine("Driver", "App.exe"), ExecutableHintResolver.Normalize("/Driver/App.exe"));
        Assert.Equal("App.exe", ExecutableHintResolver.Normalize("  App.exe  "));
        Assert.Null(ExecutableHintResolver.Normalize("   "));
        Assert.Null(ExecutableHintResolver.Normalize(@"..\App.exe"));
    }

    [Fact]
    public void AutoDetect_PicksSingleMatchAnywhereInTree()
    {
        using var root = new TempRoot();
        Touch(root.Path, "readme.txt");
        Touch(root.Path, "Проект", "main.psl");

        Assert.Equal(Path.Combine("Проект", "main.psl"), ExecutableHintResolver.AutoDetect(root.Path, new[] { ".psl", ".lfs" }));
    }

    [Fact]
    public void AutoDetect_ReturnsNull_WhenAmbiguousOrAbsent()
    {
        using var root = new TempRoot();
        Touch(root.Path, "a.psl");
        Touch(root.Path, "sub", "b.psl");
        Assert.Null(ExecutableHintResolver.AutoDetect(root.Path, new[] { ".psl" }));

        using var empty = new TempRoot();
        Touch(empty.Path, "readme.txt");
        Assert.Null(ExecutableHintResolver.AutoDetect(empty.Path, new[] { ".psl" }));
    }

    [Fact]
    public void ListRelativeFiles_IsRecursive_AndShallowFirst()
    {
        using var root = new TempRoot();
        Touch(root.Path, "sub", "deep.txt");
        Touch(root.Path, "top.txt");

        var files = ExecutableHintResolver.ListRelativeFiles(root.Path);

        Assert.Equal(new[] { "top.txt", Path.Combine("sub", "deep.txt") }, files);
        Assert.Empty(ExecutableHintResolver.ListRelativeFiles(Path.Combine(root.Path, "нет-такой-папки")));
    }
}
