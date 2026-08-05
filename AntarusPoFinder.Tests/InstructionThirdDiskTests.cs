using System;
using System.Collections.Generic;
using System.IO;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Третий диск — отдельное хранилище только под инструкции. Проверяем два обещания, на
/// которых держится вся затея:
///   • раскладка на нём ЗЕРКАЛЬНА первому диску и считается заменой префикса (никаких таблиц);
///   • в базу пишется путь на ПЕРВОМ диске, даже когда файл уехал на третий — иначе буква третьего
///     диска, у каждой машины своя, разъехалась бы синхронизацией и сломала карточку у всех.
/// Плюс поведение «третий диск не настроен/недоступен» — оно обязано быть ровно прежним.</summary>
public class InstructionThirdDiskTests
{
    private static string TouchFile(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "содержимое");
        return path;
    }

    [Fact]
    public void Mirror_ReplacesRootPrefix()
    {
        var first = Path.Combine(Path.GetTempPath(), "first");
        var third = Path.Combine(Path.GetTempPath(), "third");
        var folder = Path.Combine(first, "ПО", "ПЖ", "2.0", "SMH5", "Инструкция");

        var mirror = InstructionDiskResolver.Mirror(first, third, folder);

        Assert.Equal(Path.Combine(third, "ПО", "ПЖ", "2.0", "SMH5", "Инструкция"), mirror);
    }

    [Fact]
    public void Mirror_PathOutsideFirstDisk_HasNoMirror()
    {
        var first = Path.Combine(Path.GetTempPath(), "first");
        var third = Path.Combine(Path.GetTempPath(), "third");

        // Локальный кэш — не первый диск: зеркалить его нельзя, иначе файл уехал бы в чужую папку.
        Assert.Null(InstructionDiskResolver.Mirror(first, third, Path.Combine(Path.GetTempPath(), "cache", "Инструкция")));
        // Третий диск не настроен — зеркала нет по определению.
        Assert.Null(InstructionDiskResolver.Mirror(first, "", Path.Combine(first, "Инструкция")));
        // Третий диск указан тем же путём, что и первый — считаем, что его нет (иначе «зеркало»
        // совпало бы с оригиналом и всё поведение стало бы бессмысленным).
        Assert.Null(InstructionDiskResolver.Mirror(first, first, Path.Combine(first, "Инструкция")));
    }

    [Fact]
    public void Copy_ThirdDiskConfigured_FileGoesThere_ButDbPathStaysOnFirstDisk()
    {
        using var first = new TempRoot();
        using var third = new TempRoot();
        using var source = new TempRoot();

        var instrFolder = Path.Combine(first.Path, "ПО", "ПЖ", "SMH5", "Инструкция");
        var src = TouchFile(source.Path, "инструкция.docx");
        var warnings = new List<string>();

        var placement = InstructionStorage.Copy(src, instrFolder, first.Path, third.Path,
            duplicateOnFirstDisk: true, warnings);

        var expectedActual = Path.Combine(third.Path, "ПО", "ПЖ", "SMH5", "Инструкция", "инструкция.docx");
        Assert.True(placement.WentToThirdDisk);
        Assert.Equal(expectedActual, placement.ActualPath);
        Assert.True(File.Exists(expectedActual));
        // В БД — путь на первом диске: он и разъезжается по машинам синхронизацией.
        Assert.Equal(Path.Combine(instrFolder, "инструкция.docx"), placement.StoredPath);
        Assert.Empty(warnings);

        // Рядом с прошивкой — НАСТОЯЩИЙ документ, а не ярлык: папку версии можно скопировать на
        // флешку или открыть с машины без третьего диска, и путь из БД ведёт на существующий файл.
        var copy = Path.Combine(instrFolder, "инструкция.docx");
        Assert.True(File.Exists(copy));
        Assert.Equal(File.ReadAllText(expectedActual), File.ReadAllText(copy));
        Assert.False(File.Exists(copy + ".lnk"));
    }

    [Fact]
    public void Copy_DuplicateDisabled_LeavesFirstDiskEmpty()
    {
        using var first = new TempRoot();
        using var third = new TempRoot();
        using var source = new TempRoot();

        var instrFolder = Path.Combine(first.Path, "ПО", "Инструкция");
        var src = TouchFile(source.Path, "инструкция.pdf");
        var warnings = new List<string>();

        var placement = InstructionStorage.Copy(src, instrFolder, first.Path, third.Path,
            duplicateOnFirstDisk: false, warnings);

        Assert.True(placement.WentToThirdDisk);
        Assert.False(File.Exists(Path.Combine(instrFolder, "инструкция.pdf")));
        Assert.Empty(warnings);
    }

    /// <summary>Перезаливка инструкции обновляет копию рядом с прошивкой, а не плодит вторую, и
    /// убирает ярлык, оставшийся от прежних версий программы: иначе в папке лежал бы документ и
    /// ярлык на этот же документ.</summary>
    [Fact]
    public void Copy_Again_OverwritesDuplicate_AndRemovesOldShortcut()
    {
        using var first = new TempRoot();
        using var third = new TempRoot();
        using var source = new TempRoot();

        var instrFolder = Path.Combine(first.Path, "ПО", "Инструкция");
        Directory.CreateDirectory(instrFolder);
        File.WriteAllText(Path.Combine(instrFolder, "инструкция.docx.lnk"), "старый ярлык");

        var src = TouchFile(source.Path, "инструкция.docx");
        var warnings = new List<string>();
        InstructionStorage.Copy(src, instrFolder, first.Path, third.Path, true, warnings);

        File.WriteAllText(src, "новая редакция");
        InstructionStorage.Copy(src, instrFolder, first.Path, third.Path, true, warnings);

        var copy = Path.Combine(instrFolder, "инструкция.docx");
        Assert.Equal("новая редакция", File.ReadAllText(copy));
        Assert.False(File.Exists(copy + ".lnk"));
        Assert.Single(Directory.GetFiles(instrFolder));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Copy_ThirdDiskNotConfigured_BehavesExactlyAsBefore()
    {
        using var first = new TempRoot();
        using var source = new TempRoot();

        var instrFolder = Path.Combine(first.Path, "ПО", "ПЖ", "SMH5", "Инструкция");
        var src = TouchFile(source.Path, "инструкция.pdf");
        var warnings = new List<string>();

        var placement = InstructionStorage.Copy(src, instrFolder, first.Path, thirdRoot: "",
            duplicateOnFirstDisk: true, warnings);

        Assert.False(placement.WentToThirdDisk);
        Assert.Equal(Path.Combine(instrFolder, "инструкция.pdf"), placement.StoredPath);
        Assert.True(File.Exists(placement.StoredPath));
        // Дублировать нечего — файл и так на первом диске, второй копии рядом быть не должно.
        Assert.Single(Directory.GetFiles(instrFolder));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Copy_ThirdDiskUnreachable_FallsBackToFirstDisk()
    {
        using var first = new TempRoot();
        using var source = new TempRoot();
        var missingThird = Path.Combine(Path.GetTempPath(), "нет-такого-диска-" + Guid.NewGuid().ToString("N"));

        var instrFolder = Path.Combine(first.Path, "Инструкция");
        var src = TouchFile(source.Path, "инструкция.docx");
        var warnings = new List<string>();

        var placement = InstructionStorage.Copy(src, instrFolder, first.Path, missingThird,
            duplicateOnFirstDisk: true, warnings);

        // Недоступный третий диск — не ошибка: файл ложится на первый, как до появления затеи.
        Assert.False(placement.WentToThirdDisk);
        Assert.True(File.Exists(Path.Combine(instrFolder, "инструкция.docx")));
        Assert.Empty(warnings);
    }

    [Fact]
    public void ReadFolder_PrefersExistingMirror_AndShortcutIsNotADocument()
    {
        using var first = new TempRoot();
        using var third = new TempRoot();

        var instrFolder = Path.Combine(first.Path, "ПО", "Инструкция");
        var mirror = Path.Combine(third.Path, "ПО", "Инструкция");
        TouchFile(instrFolder, "инструкция.docx.lnk");   // на первом только ярлык
        var real = TouchFile(mirror, "инструкция.docx"); // сам документ уехал на третий

        var readFolder = InstructionDiskResolver.PreferredReadFolder(first.Path, third.Path, instrFolder);
        Assert.Equal(mirror, readFolder);

        // Ярлык документом не считается ни одним из резолверов — иначе «самым свежим файлом» папки
        // на первом диске вечно оказывался бы он.
        Assert.True(DocFileResolver.IsShortcut(Path.Combine(instrFolder, "инструкция.docx.lnk")));
        Assert.Null(DocFileResolver.LatestFileIn(instrFolder));
        Assert.Equal(real, InstructionDocResolver.Resolve(storedPath: null, readFolder).Docx);
    }
}
