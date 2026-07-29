using System;
using System.IO;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Инструкция на карточке поиска — набор действий (папка / правка docx / PDF для печати /
/// печать) вместо одного «Открыть». Резолвер отвечает, что из этого доступно: где папка, есть ли
/// исходный docx для правки, есть ли pdf для печати, и не устарел ли pdf относительно docx (тогда его
/// надо пересобрать перед печатью).</summary>
public class InstructionDocResolverTests
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
    public void DocxWithoutPdf_ReportedStaleAndPrintable()
    {
        using var root = new TempRoot();
        var shared = Path.Combine(root.Path, "Инструкция");
        var docx = Touch(shared, "инструкция.docx");

        var doc = InstructionDocResolver.Resolve(storedPath: null, shared);

        Assert.Equal(shared, doc.Folder);
        Assert.Equal(docx, doc.Docx);
        Assert.Null(doc.Pdf);
        Assert.True(doc.PdfStale);          // pdf ещё нет — собрать перед печатью
        Assert.True(doc.CanPrint);          // печатать есть чем (соберём из docx)
        Assert.True(doc.HasAny);
        Assert.Equal(Path.ChangeExtension(docx, ".pdf"), doc.ExpectedPdfPath);
    }

    [Fact]
    public void FreshPdf_NotStale()
    {
        using var root = new TempRoot();
        var shared = Path.Combine(root.Path, "Инструкция");
        Touch(shared, "инструкция.docx", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var pdf = Touch(shared, "инструкция.pdf", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var doc = InstructionDocResolver.Resolve(storedPath: null, shared);

        Assert.Equal(pdf, doc.Pdf);
        Assert.False(doc.PdfStale);         // pdf свежее docx — пересборка не нужна
    }

    [Fact]
    public void PdfOlderThanDocx_IsStale()
    {
        using var root = new TempRoot();
        var shared = Path.Combine(root.Path, "Инструкция");
        Touch(shared, "инструкция.docx", new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        Touch(shared, "инструкция.pdf", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var doc = InstructionDocResolver.Resolve(storedPath: null, shared);

        Assert.True(doc.PdfStale);          // docx правили после сборки pdf — пересобрать
    }

    [Fact]
    public void PdfOnly_NoDocx_PrintableAndNotStale()
    {
        using var root = new TempRoot();
        var shared = Path.Combine(root.Path, "Инструкция");
        var pdf = Touch(shared, "инструкция.pdf");

        var doc = InstructionDocResolver.Resolve(storedPath: null, shared);

        Assert.Null(doc.Docx);
        Assert.Equal(pdf, doc.Pdf);
        Assert.False(doc.PdfStale);         // docx нет — из чего пересобирать, нечего сравнивать
        Assert.True(doc.CanPrint);
        Assert.Null(doc.ExpectedPdfPath);   // собирать не из чего
    }

    [Fact]
    public void LegacyFile_NotPrintableNotEditable_ButHasFolder()
    {
        using var root = new TempRoot();
        var shared = Path.Combine(root.Path, "Инструкция");
        Touch(shared, "инструкция.html");

        var doc = InstructionDocResolver.Resolve(storedPath: null, shared);

        Assert.True(doc.HasAny);            // файл инструкции есть — «Открыть папку»/«Открыть» доступны
        Assert.Null(doc.Docx);
        Assert.Null(doc.Pdf);
        Assert.False(doc.CanPrint);         // печатать/править нечем
        Assert.Equal(shared, doc.Folder);
    }

    [Fact]
    public void PicksNewestDocxAndPdfSeparately()
    {
        using var root = new TempRoot();
        var shared = Path.Combine(root.Path, "Инструкция");
        Touch(shared, "старая.docx", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newDocx = Touch(shared, "новая.docx", new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var newPdf = Touch(shared, "новая.pdf", new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc));

        var doc = InstructionDocResolver.Resolve(storedPath: null, shared);

        Assert.Equal(newDocx, doc.Docx);
        Assert.Equal(newPdf, doc.Pdf);
        Assert.False(doc.PdfStale);
    }

    [Fact]
    public void SharedFolderPreferredOverStoredPath()
    {
        using var root = new TempRoot();
        var stored = Touch(root.Path, "версия/старая-инструкция.docx", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var shared = Path.Combine(root.Path, "Инструкция");
        Touch(shared, "актуальная.docx", new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        var doc = InstructionDocResolver.Resolve(stored, shared);

        // Папка конвертации — общая папка «Инструкция», docx берётся из неё, а не из пути версии.
        Assert.Equal(shared, doc.Folder);
    }

    [Fact]
    public void NothingAtAll_EmptyDoc()
    {
        using var root = new TempRoot();

        var doc = InstructionDocResolver.Resolve(
            Path.Combine(root.Path, "нет.docx"), Path.Combine(root.Path, "нет-папки"));

        Assert.False(doc.HasAny);
        Assert.Null(doc.Folder);
        Assert.Null(doc.Docx);
        Assert.Null(doc.Pdf);
        Assert.False(doc.CanPrint);
        Assert.False(doc.PdfStale);
    }
}
