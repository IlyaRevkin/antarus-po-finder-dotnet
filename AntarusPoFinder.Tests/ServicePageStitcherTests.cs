using System;
using System.Collections.Generic;
using System.IO;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Страница «обратитесь в сервис» вшивается ПОСЛЕДНЕЙ страницей в сам PDF инструкции, а не
/// кладётся файлом рядом: заказчик открывает по QR один файл, и файл-спутник в папке на сетевой шаре
/// он не увидит никогда.
///
/// Главный риск здесь один и назван прямо: чтобы повторная выкладка не дописывала вторую, третью,
/// десятую такую же страницу. Он закрыт двумя независимыми способами, и оба проверяются ниже —
/// сшивка всегда идёт от чистого оригинала, а перед добавлением из документа выбрасываются все
/// страницы с нашей меткой.</summary>
public class ServicePageStitcherTests : IDisposable
{
    private readonly TempRoot _root = new();
    public void Dispose() => _root.Dispose();

    private string Path_(string name) => System.IO.Path.Combine(_root.Path, name);

    /// <summary>Настоящий PDF на нужное число страниц — сшивать текстовый файл-пустышку нельзя, тут
    /// проверяется именно работа с форматом.</summary>
    private static string MakePdf(string path, int pages, string title)
    {
        using var doc = new PdfDocument();
        for (var i = 0; i < pages; i++)
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawRectangle(XBrushes.White, 0, 0, page.Width.Point, page.Height.Point);
        }
        doc.Info.Title = title;
        doc.Save(path);
        return path;
    }

    [Fact]
    public void Append_PutsThePageLast_AndMarksItAsOurs()
    {
        var doc = MakePdf(Path_("инструкция.pdf"), 3, "Инструкция");
        var page = MakePdf(Path_("service.pdf"), 1, "Сервис");

        var result = ServicePageStitcher.Append(doc, page, Path_("out.pdf"), "stamp-1");

        Assert.True(result.Stitched);
        Assert.Equal(0, result.Replaced);
        Assert.Equal(4, ServicePageStitcher.PageCount(result.Path));
        Assert.Equal(1, ServicePageStitcher.CountStitchedPages(result.Path));
    }

    /// <summary>ГЛАВНАЯ проверка. Сшитый документ легко возвращается в оборот — его скачивают с
    /// хостинга и прикладывают к версии как инструкцию. Если бы вшивание просто дописывало страницу,
    /// с каждой такой ходкой документ обрастал бы одинаковыми последними страницами.</summary>
    [Fact]
    public void Append_ToAnAlreadyStitchedDocument_DoesNotAccumulatePages()
    {
        var doc = MakePdf(Path_("инструкция.pdf"), 3, "Инструкция");
        var page = MakePdf(Path_("service.pdf"), 1, "Сервис");

        var once = ServicePageStitcher.Append(doc, page, Path_("out1.pdf"), "stamp-1");
        var twice = ServicePageStitcher.Append(once.Path, page, Path_("out2.pdf"), "stamp-1");
        var thrice = ServicePageStitcher.Append(twice.Path, page, Path_("out3.pdf"), "stamp-1");

        Assert.Equal(1, twice.Replaced);
        Assert.Equal(1, thrice.Replaced);
        foreach (var path in new[] { once.Path, twice.Path, thrice.Path })
        {
            Assert.Equal(4, ServicePageStitcher.PageCount(path));
            Assert.Equal(1, ServicePageStitcher.CountStitchedPages(path));
        }
    }

    /// <summary>Правка макета заменяет вшитую страницу, а не добавляет вторую рядом с прежней.</summary>
    [Fact]
    public void Append_WithANewStamp_ReplacesThePageInsteadOfAddingASecond()
    {
        var doc = MakePdf(Path_("инструкция.pdf"), 2, "Инструкция");
        var page = MakePdf(Path_("service.pdf"), 1, "Сервис");

        var old = ServicePageStitcher.Append(doc, page, Path_("out1.pdf"), "stamp-old");
        var fresh = ServicePageStitcher.Append(old.Path, page, Path_("out2.pdf"), "stamp-new");

        Assert.Equal(1, fresh.Replaced);
        Assert.Equal(3, ServicePageStitcher.PageCount(fresh.Path));
        Assert.Equal(1, ServicePageStitcher.CountStitchedPages(fresh.Path));
    }

    /// <summary>Оригинал на сетевой шаре не трогаем: его правят, пересылают и сверяют с бумажной
    /// копией. Сшивается временная копия, наверх уезжает она.</summary>
    [Fact]
    public void Append_LeavesTheSourceDocumentByteForByteUntouched()
    {
        var doc = MakePdf(Path_("инструкция.pdf"), 3, "Инструкция");
        var page = MakePdf(Path_("service.pdf"), 1, "Сервис");
        var before = File.ReadAllBytes(doc);

        ServicePageStitcher.Append(doc, page, Path_("out.pdf"), "stamp-1");

        Assert.Equal(before, File.ReadAllBytes(doc));
        Assert.Equal(3, ServicePageStitcher.PageCount(doc));
        Assert.Equal(0, ServicePageStitcher.CountStitchedPages(doc));
    }

    /// <summary>Битый или зашифрованный PDF — не повод отменять выкладку: документ уезжает как есть,
    /// а причина ложится в предупреждения. Инструкция без страницы сервиса лучше, чем ненайденная.</summary>
    [Fact]
    public void Append_BrokenDocument_FallsBackToPublishingItAsIs()
    {
        var broken = Path_("битая.pdf");
        File.WriteAllText(broken, "это вообще не PDF");
        var page = MakePdf(Path_("service.pdf"), 1, "Сервис");
        var warnings = new List<string>();

        var result = ServicePageStitcher.Append(broken, page, Path_("out.pdf"), "stamp-1", warnings);

        Assert.False(result.Stitched);
        Assert.Equal(broken, result.Path);
        Assert.NotEmpty(warnings);
        Assert.False(File.Exists(Path_("out.pdf")));
    }

    /// <summary>Метка живёт в самом файле и переживает сохранение — на этом стоит вся идемпотентность,
    /// поэтому проверяется отдельно, а не только через счётчики выше.</summary>
    [Fact]
    public void TheMarker_SurvivesASaveAndReopen()
    {
        var doc = MakePdf(Path_("инструкция.pdf"), 1, "Инструкция");
        var page = MakePdf(Path_("service.pdf"), 1, "Сервис");

        var result = ServicePageStitcher.Append(doc, page, Path_("out.pdf"), "stamp-xyz");

        using var reopened = PdfReader.Open(result.Path, PdfDocumentOpenMode.Import);
        var last = reopened.Pages[reopened.PageCount - 1];
        Assert.True(last.Elements.ContainsKey(ServicePageStitcher.MarkerKey));
        Assert.Contains("stamp-xyz", last.Elements[ServicePageStitcher.MarkerKey]!.ToString());
    }
}
