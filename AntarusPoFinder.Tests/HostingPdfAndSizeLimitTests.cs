using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Два правила выкладки на хостинг, которые требовались.
///
/// <b>Word не уезжает собой.</b> «Если инструкция загружена в docx, то в хранилище на неё не должно
/// быть пути, должен быть конверт в pdf и потом уже путь на pdf файл». Причина простая: ссылку под
/// QR открывают на телефоне в цеху, там .docx либо не откроется, либо откроется криво.
///
/// <b>Предел размера.</b> «Проверка на то чтобы не был файл больше 20 МБ» — с настраиваемым пределом
/// и переключателем «жёсткий запрет / предупреждение». Ограничение только на хостинг: на диск
/// большой проект ПЛК класть можно.</summary>
public class HostingPdfAndSizeLimitTests
{
    private static S3Settings Settings(long? maxBytes = null, bool hard = true) =>
        new("https://s3.twcstorage.ru", "amperus", "ru-1", "", "AKIA-ID", "секрет",
            "https://fs.elitacompany.ru", Enabled: true)
        {
            MaxFileBytes = maxBytes ?? S3Settings.DefaultMaxFileBytes,
            HardSizeLimit = hard,
        };

    private sealed class FakeStorage : HttpMessageHandler
    {
        public List<(string Url, long Length)> Puts { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var bytes = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Puts.Add((request.RequestUri!.ToString(), bytes.Length));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>Подставной конвертер: пишет «PDF» туда, куда попросили. Настоящий поднимает Word по
    /// COM или процесс LibreOffice — в тестах этого быть не должно.</summary>
    private sealed class FakePdf : IDocumentToPdf
    {
        private readonly bool _works;
        public FakePdf(bool works = true, bool supported = true)
        {
            _works = works;
            IsSupported = supported;
        }

        public bool IsSupported { get; }
        public List<string> Converted { get; } = new();

        public string? Convert(string documentPath, string outputPdfPath)
        {
            Converted.Add(documentPath);
            if (!_works) return null;
            File.WriteAllText(outputPdfPath, "PDF из " + Path.GetFileName(documentPath));
            return outputPdfPath;
        }
    }

    private static InstructionPublisher Publisher(FakeStorage storage, S3Settings settings, IDocumentToPdf? pdf = null) =>
        new(settings, new S3Client(new HttpClient(storage)), pdf);

    private static string Doc(TempRoot root, string name, int sizeBytes = 32)
    {
        var folder = Path.Combine(root.Path, "ПО", "ПЖ", "SMH5", "Инструкция");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        return path;
    }

    // ── Word → PDF ────────────────────────────────────────────────────────────

    [Fact]
    public void Docx_IsPublishedAsPdf_AndTheLinkPointsAtThePdf()
    {
        using var root = new TempRoot();
        var docx = Doc(root, "инструкция_2.1.docx");
        var storage = new FakeStorage();
        var pdf = new FakePdf();
        var warnings = new List<string>();

        var url = Publisher(storage, Settings(), pdf).Publish(docx, docx, root.Path, warnings);

        Assert.Empty(warnings);
        Assert.Equal(docx, Assert.Single(pdf.Converted));
        Assert.Equal("https://fs.elitacompany.ru/PO/PZH/SMH5/Instrukciya/instrukciya_2.1.pdf", url);

        // На хостинг ушёл ровно один объект, и это PDF — исходного .docx там нет вовсе.
        var put = Assert.Single(storage.Puts);
        Assert.EndsWith(".pdf", put.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(".docx", put.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlannedUrl_ForDocx_AlreadyShowsThePdfAddress()
    {
        // Наклейку печатают и клеят раньше, чем документ доедет до хостинга, — адрес обязан быть
        // окончательным уже в этот момент.
        using var root = new TempRoot();
        var docx = Doc(root, "инструкция_2.1.docx");

        var planned = InstructionPublisher.PlannedUrl(Settings(), docx, root.Path);

        Assert.Equal("https://fs.elitacompany.ru/PO/PZH/SMH5/Instrukciya/instrukciya_2.1.pdf", planned);
    }

    [Fact]
    public void Pdf_IsPublishedAsIs()
    {
        using var root = new TempRoot();
        var file = Doc(root, "инструкция_2.1.pdf");
        var storage = new FakeStorage();
        var pdf = new FakePdf();

        var url = Publisher(storage, Settings(), pdf).Publish(file, file, root.Path, new List<string>());

        Assert.Empty(pdf.Converted); // конвертировать нечего
        Assert.EndsWith("/instrukciya_2.1.pdf", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Docx_WithoutAConverter_SaysSoInsteadOfPublishingTheSource()
    {
        // Худший исход — положить на хостинг .docx: ссылка есть, а с телефона не открывается.
        using var root = new TempRoot();
        var docx = Doc(root, "инструкция_2.1.docx");
        var storage = new FakeStorage();
        var warnings = new List<string>();

        var url = Publisher(storage, Settings(), new FakePdf(supported: false)).Publish(docx, docx, root.Path, warnings);

        Assert.Null(url);
        Assert.Empty(storage.Puts);
        Assert.Contains(warnings, w => w.Contains("собрать его на этой машине нечем", StringComparison.Ordinal));
    }

    [Fact]
    public void Docx_WhenConversionFails_IsReportedAndNotPublished()
    {
        using var root = new TempRoot();
        var docx = Doc(root, "инструкция_2.1.docx");
        var storage = new FakeStorage();
        var warnings = new List<string>();

        var url = Publisher(storage, Settings(), new FakePdf(works: false)).Publish(docx, docx, root.Path, warnings);

        Assert.Null(url);
        Assert.Empty(storage.Puts);
        Assert.Contains(warnings, w => w.Contains("не удалось собрать PDF", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("и.docx", true)]
    [InlineData("и.DOC", true)]
    [InlineData("и.rtf", true)]
    [InlineData("и.odt", true)]
    [InlineData("и.pdf", false)]
    [InlineData("и.png", false)]
    public void IsWordDocument_RecognisesEditableFormats(string name, bool expected) =>
        Assert.Equal(expected, InstructionPublisher.IsWordDocument(name));

    // ── Предел размера ────────────────────────────────────────────────────────

    [Fact]
    public void FileOverTheLimit_HardMode_IsNotPublished()
    {
        using var root = new TempRoot();
        var file = Doc(root, "инструкция_2.1.pdf", sizeBytes: 3000);
        var storage = new FakeStorage();
        var warnings = new List<string>();

        var url = Publisher(storage, Settings(maxBytes: 1024, hard: true)).Publish(file, file, root.Path, warnings);

        Assert.Null(url);
        Assert.Empty(storage.Puts);
        var warning = Assert.Single(warnings);
        Assert.Contains("при пределе", warning, StringComparison.Ordinal);
        Assert.Contains("не выложен", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void FileOverTheLimit_SoftMode_IsPublishedButReported()
    {
        using var root = new TempRoot();
        var file = Doc(root, "инструкция_2.1.pdf", sizeBytes: 3000);
        var storage = new FakeStorage();
        var warnings = new List<string>();

        var url = Publisher(storage, Settings(maxBytes: 1024, hard: false)).Publish(file, file, root.Path, warnings);

        Assert.NotNull(url);
        Assert.Single(storage.Puts);
        Assert.Contains(warnings, w => w.Contains("будет качаться долго", StringComparison.Ordinal));
    }

    [Fact]
    public void FileUnderTheLimit_GoesQuietly()
    {
        using var root = new TempRoot();
        var file = Doc(root, "инструкция_2.1.pdf", sizeBytes: 100);
        var storage = new FakeStorage();
        var warnings = new List<string>();

        Assert.NotNull(Publisher(storage, Settings(maxBytes: 1024)).Publish(file, file, root.Path, warnings));
        Assert.Empty(warnings);
    }

    [Fact]
    public void TheLimitIsCheckedOnTheBuiltPdf_NotOnTheSource()
    {
        // Собранный PDF бывает толще исходника — проверять надо то, что реально уезжает.
        using var root = new TempRoot();
        var docx = Doc(root, "инструкция_2.1.docx", sizeBytes: 10);
        var storage = new FakeStorage();
        var warnings = new List<string>();

        // Конвертер пишет текст длиннее одного байта, предел ставим в один байт.
        var url = Publisher(storage, Settings(maxBytes: 1, hard: true), new FakePdf()).Publish(docx, docx, root.Path, warnings);

        Assert.Null(url);
        Assert.Empty(storage.Puts);
        Assert.Contains(warnings, w => w.Contains("при пределе", StringComparison.Ordinal));
    }

    [Fact]
    public void Defaults_AreTwentyMegabytesAndHardStop()
    {
        Assert.Equal(20L * 1024 * 1024, S3Settings.DefaultMaxFileBytes);
        var settings = new S3Settings("e", "b", "r", "", "id", "secret", "web", true);
        Assert.Equal(S3Settings.DefaultMaxFileBytes, settings.MaxFileBytes);
        Assert.True(settings.HardSizeLimit);
    }
}
