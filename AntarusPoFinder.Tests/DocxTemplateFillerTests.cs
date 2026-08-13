using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Xml.Linq;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Подстановка названия шкафа в бланк паспорта перед печатью.
///
/// Просьба была такая: «загружался шаблон, и там есть поле, где название шкафа
/// указывается, чтобы программа сама подставляла; нажимаешь — он запрашивает название, ты вставляешь,
/// и он печатает шаблон, подставляя название». Отсюда и проверки: метку надо НАЙТИ в реальном
/// документе Word и заменить, не испортив ни оформление, ни сам общий бланк.
///
/// Главная тонкость, ради которой этот файл и написан: Word режет текст абзаца на прогоны (w:r/w:t)
/// как ему удобно — проверка орфографии, отмеченная правка, смена языка ввода посреди слова. Метка
/// «{{Название}}» почти никогда не лежит в одном прогоне целиком, и наивная замена по содержимому
/// отдельных w:t не нашла бы её вовсе. Поэтому почти все шаблоны здесь собраны РАЗРЕЗАННЫМИ.</summary>
public class DocxTemplateFillerTests : IDisposable
{
    private readonly TempRoot _root = new();
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private const string Mark = "{{Название}}";

    public void Dispose() => _root.Dispose();

    // ── Сборка и разбор подопытного документа ────────────────────────────────────────────────

    /// <summary>Минимальный .docx: zip, внутри word/document.xml. Настоящий Word кладёт туда ещё
    /// десяток частей, но подстановка смотрит только на части с видимым текстом — для проверки замены
    /// хватает этих. Каждый абзац задаётся СПИСКОМ прогонов: именно так Word и режет текст.
    ///
    /// Каждому прогону проставляется свой стиль («run0», «run1», …) — по нему в проверке видно, в
    /// какой именно прогон встало подставленное значение и сохранилось ли оформление соседей.</summary>
    private string MakeDocx(string fileName, string[][] paragraphs, string[]? header = null, string? styles = null)
    {
        var path = Path.Combine(_root.Path, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteXml(zip, "word/document.xml", Part(paragraphs));
        if (header is not null) WriteXml(zip, "word/header1.xml", Part(new[] { header }));
        if (styles is not null)
            WriteXml(zip, "word/styles.xml", new XElement(W + "styles", new XElement(W + "docDefaults", styles)));
        return path;
    }

    private static XElement Part(string[][] paragraphs) =>
        new(W + "document",
            new XElement(W + "body",
                paragraphs.Select(runs => new XElement(W + "p",
                    runs.Select((text, i) => new XElement(W + "r",
                        new XElement(W + "rPr", new XElement(W + "rStyle", new XAttribute(W + "val", $"run{i}"))),
                        new XElement(W + "t", text)))))));

    private static void WriteXml(ZipArchive zip, string entryName, XElement root)
    {
        using var stream = zip.CreateEntry(entryName).Open();
        new XDocument(root).Save(stream);
    }

    /// <summary>Прогоны части документа как есть: стиль прогона и его текст, по порядку.</summary>
    private static List<(string Style, string Text)> Runs(string docx, string part = "word/document.xml")
    {
        using var zip = ZipFile.OpenRead(docx);
        using var stream = zip.GetEntry(part)!.Open();
        return XDocument.Load(stream).Descendants(W + "r")
            .Select(r => (
                Style: r.Element(W + "rPr")?.Element(W + "rStyle")?.Attribute(W + "val")?.Value ?? "",
                Text: string.Concat(r.Elements(W + "t").Select(t => t.Value))))
            .ToList();
    }

    /// <summary>Весь видимый текст части — то, что человек увидит на листе.</summary>
    private static string Text(string docx, string part = "word/document.xml") =>
        string.Concat(Runs(docx, part).Select(r => r.Text));

    private static string RawPart(string docx, string part)
    {
        using var zip = ZipFile.OpenRead(docx);
        using var reader = new StreamReader(zip.GetEntry(part)!.Open());
        return reader.ReadToEnd();
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private string Dst(string name) => Path.Combine(_root.Path, "out", name);

    // ── Замена ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Метка, разрезанная Word'ом на три прогона, находится и заменяется целиком. Значение
    /// встаёт в тот прогон, где метка НАЧАЛАСЬ, — с его оформлением: у абзаца «Наименование:
    /// {{Название}}» жирная подпись слева обязана остаться жирной. Из остальных задетых прогонов
    /// вырезается только их кусок метки, сами прогоны остаются на месте.</summary>
    [Fact]
    public void Fill_ReplacesAMarkSplitAcrossRuns_KeepingTheFormattingOfTheRunItStartedIn()
    {
        var src = MakeDocx("Бланк НКУ.docx", new[]
        {
            new[] { "Наименование: {{Наз", "вание", "}} — щит управления" },
        });
        var dst = Dst("Заполненный.docx");

        Assert.Equal(1, DocxTemplateFiller.Fill(src, dst, Mark, "ЩУН-3"));

        Assert.Equal("Наименование: ЩУН-3 — щит управления", Text(dst));
        var runs = Runs(dst);
        Assert.Equal(("run0", "Наименование: ЩУН-3"), runs[0]);
        Assert.Equal(("run1", ""), runs[1]);          // прогон уцелел, из него вырезан только кусок метки
        Assert.Equal(("run2", " — щит управления"), runs[2]);
    }

    /// <summary>Несколько вхождений в одном абзаце — и тоже разрезанных. Замена идёт справа налево
    /// именно за этим: правка более раннего вхождения сдвинула бы границы следующих, и второе
    /// подставилось бы в середину слова.</summary>
    [Fact]
    public void Fill_ReplacesEveryOccurrenceInTheParagraph()
    {
        var src = MakeDocx("Бланк ШР.docx", new[]
        {
            new[] { "{{Наз", "вание}}, он же {{Наз", "вание}} по схеме" },
        });
        var dst = Dst("ШР заполненный.docx");

        Assert.Equal(2, DocxTemplateFiller.Fill(src, dst, Mark, "ЩУН-5"));
        Assert.Equal("ЩУН-5, он же ЩУН-5 по схеме", Text(dst));
    }

    /// <summary>Пробелы по краям подстановки сохраняются: без xml:space="preserve" Word схлопнул бы
    /// «Шкаф ЩУН-3 готов» в «Шкаф ЩУН-3готов», а пустой остаток прогона утянул бы за собой соседний
    /// пробел.</summary>
    [Fact]
    public void Fill_KeepsTheSpacesAroundTheMark()
    {
        var src = MakeDocx("Бланк.docx", new[] { new[] { "Шкаф ", "{{Название}}", " готов к отгрузке" } });
        var dst = Dst("Пробелы.docx");

        DocxTemplateFiller.Fill(src, dst, Mark, "ЩУН-3");

        Assert.Equal("Шкаф ЩУН-3 готов к отгрузке", Text(dst));
        Assert.Contains("preserve", RawPart(dst, "word/document.xml"));
    }

    /// <summary>Метка ищется без учёта регистра: в бланке её вписывали руками, и «{{НАЗВАНИЕ}}» —
    /// та же метка, а не «другая, которая не нашлась».</summary>
    [Fact]
    public void Fill_MatchesTheMarkRegardlessOfCase()
    {
        var src = MakeDocx("Бланк.docx", new[] { new[] { "Шкаф {{НАЗВАНИЕ}}" } });
        var dst = Dst("Регистр.docx");

        Assert.Equal(1, DocxTemplateFiller.Fill(src, dst, Mark, "ЩУН-3"));
        Assert.Equal("Шкаф ЩУН-3", Text(dst));
    }

    /// <summary>Метку можно поменять в настройках (см. ConfigService.PassportNamePlaceholder) — бланки
    /// у предприятия уже свои, и переписывать их под наши фигурные скобки никто не обязан.</summary>
    [Fact]
    public void Fill_WorksWithAMarkConfiguredByTheAdministrator()
    {
        var src = MakeDocx("Бланк СПЛ.docx", new[] { new[] { "Щит СПЛ <НАЗВАНИЕ ШКА", "ФА>" } });
        var dst = Dst("Своя метка.docx");

        Assert.Equal(1, DocxTemplateFiller.Fill(src, dst, "<НАЗВАНИЕ ШКАФА>", "Щит СПЛ-12"));
        Assert.Equal("Щит СПЛ Щит СПЛ-12", Text(dst));
    }

    /// <summary>Колонтитулы и сноски — тоже видимый текст: в бланках название шкафа сплошь и рядом
    /// стоит в шапке. А вот стили не трогаются вовсе: метки там не бывает, зато испортить их
    /// случайной заменой можно.</summary>
    [Fact]
    public void Fill_ReachesTheHeader_ButNeverTheStyles()
    {
        var src = MakeDocx("Бланк с шапкой.docx",
            new[] { new[] { "Паспорт шкафа {{Название}}" } },
            header: new[] { "Шкаф {{Наз", "вание}}" },
            styles: Mark);
        var dst = Dst("С шапкой.docx");

        Assert.Equal(2, DocxTemplateFiller.Fill(src, dst, Mark, "ЩУН-3"));

        Assert.Equal("Паспорт шкафа ЩУН-3", Text(dst));
        Assert.Equal("Шкаф ЩУН-3", Text(dst, "word/header1.xml"));
        Assert.Contains(Mark, RawPart(dst, "word/styles.xml"));
    }

    // ── Что заменой испортить нельзя ────────────────────────────────────────────────────────

    /// <summary>Правится всегда КОПИЯ: общий бланк на диске обязан остаться нетронутым, иначе первая
    /// же печать испортила бы его для всех — второй раз подставлять было бы уже некуда.</summary>
    [Fact]
    public void Fill_NeverModifiesTheTemplateItself()
    {
        var src = MakeDocx("Общий бланк.docx", new[] { new[] { "Шкаф {{Название}}" } });
        var before = Hash(src);
        var dst = Dst("Копия.docx");

        DocxTemplateFiller.Fill(src, dst, Mark, "ЩУН-3");

        Assert.Equal(before, Hash(src));
        Assert.Equal("Шкаф {{Название}}", Text(src));
        Assert.Equal("Шкаф ЩУН-3", Text(dst));
    }

    /// <summary>Бланк, лежащий на общем диске, обычно приезжает копией «только для чтения» — запись в
    /// неё упала бы правом доступа, и печать не состоялась бы вовсе.</summary>
    [Fact]
    public void Fill_WorksWhenTheTemplateIsReadOnly()
    {
        var src = MakeDocx("Только чтение.docx", new[] { new[] { "Шкаф {{Название}}" } });
        File.SetAttributes(src, FileAttributes.ReadOnly);
        var dst = Dst("Из читалки.docx");

        try
        {
            Assert.Equal(1, DocxTemplateFiller.Fill(src, dst, Mark, "ЩУН-3"));
            Assert.Equal("Шкаф ЩУН-3", Text(dst));
        }
        finally
        {
            File.SetAttributes(src, FileAttributes.Normal); // иначе временную папку не удалить
        }
    }

    /// <summary>Пустая метка не заменяет ничего. Иначе она нашлась бы в каждой точке текста и
    /// расставила бы название шкафа между всеми буквами бланка.</summary>
    [Fact]
    public void Fill_WithAnEmptyMark_ChangesNothing()
    {
        var src = MakeDocx("Бланк.docx", new[] { new[] { "Шкаф {{Название}}" } });
        var dst = Dst("Пустая метка.docx");

        Assert.Equal(0, DocxTemplateFiller.Fill(src, dst, "   ", "ЩУН-3"));
        Assert.Equal("Шкаф {{Название}}", Text(src));
    }

    // ── Подсчёт перед печатью ───────────────────────────────────────────────────────────────

    /// <summary>Count считает вхождения, НЕ трогая файл: окно печати спрашивает «есть ли куда
    /// подставлять» до того, как что-то уйдёт на бумагу.</summary>
    [Fact]
    public void Count_FindsTheMark_WithoutTouchingTheFile()
    {
        var src = MakeDocx("Бланк.docx", new[]
        {
            new[] { "Шкаф {{Наз", "вание}}" },
            new[] { "Он же {{Название}}" },
        });
        var before = Hash(src);

        Assert.Equal(2, DocxTemplateFiller.Count(src, Mark));
        Assert.Equal(before, Hash(src));
    }

    /// <summary>Метки в бланке нет — 0. Это тот самый случай, ради которого счёт и заведён: окно
    /// предупредит, что подставлять некуда, вместо листа с «{{Название}}» вместо названия.</summary>
    [Fact]
    public void Count_OnABlankWithoutTheMark_IsZero_AndFillStillMakesAPrintableCopy()
    {
        var src = MakeDocx("Без метки.docx", new[] { new[] { "Паспорт шкафа. Название: ____________" } });
        var dst = Dst("Как есть.docx");

        Assert.Equal(0, DocxTemplateFiller.Count(src, Mark));
        Assert.Equal(0, DocxTemplateFiller.Fill(src, dst, Mark, "ЩУН-3"));

        // Копия всё равно сделана — такой бланк печатают как есть, название вписывают ручкой.
        Assert.True(File.Exists(dst));
        Assert.Equal("Паспорт шкафа. Название: ____________", Text(dst));
    }

    // ── Форматы ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Подставлять умеем только в .docx: .doc — бинарный формат Word, .pdf вообще не
    /// редактируемый документ. Такие бланки печатаются как есть, и окно об этом спрашивает.</summary>
    [Fact]
    public void OnlyDocxIsSupported()
    {
        Assert.True(DocxTemplateFiller.IsSupported(@"Z:\Конфиг\Паспорта\НКУ\Бланк.docx"));
        Assert.True(DocxTemplateFiller.IsSupported(@"Z:\Конфиг\Паспорта\НКУ\Бланк.DOCX"));
        Assert.False(DocxTemplateFiller.IsSupported(@"Z:\Конфиг\Паспорта\НКУ\Бланк.doc"));
        Assert.False(DocxTemplateFiller.IsSupported(@"Z:\Конфиг\Паспорта\НКУ\Бланк.pdf"));
        Assert.False(DocxTemplateFiller.IsSupported(null));

        var pdf = Path.Combine(_root.Path, "Бланк.pdf");
        File.WriteAllText(pdf, "%PDF-1.4");
        Assert.Equal(0, DocxTemplateFiller.Count(pdf, Mark));
    }

    /// <summary>Файла нет (бланк удалили с общего диска, пока окно было открыто) — 0, а не исключение
    /// на пустом месте: об этом окно скажет само, сверившись со списком.</summary>
    [Fact]
    public void MissingTemplate_IsCountedAsZero_AndMakesNoCopy()
    {
        var missing = Path.Combine(_root.Path, "Нет такого.docx");
        var dst = Dst("Ничего.docx");

        Assert.Equal(0, DocxTemplateFiller.Count(missing, Mark));
        Assert.Equal(0, DocxTemplateFiller.Fill(missing, dst, Mark, "ЩУН-3"));
        Assert.False(File.Exists(dst));
    }

    /// <summary>Часть документа, которая не разобралась как XML (битый файл, чужой формат внутри
    /// zip), не роняет печать целиком: остальные части всё равно могут содержать метку.</summary>
    [Fact]
    public void BrokenPart_DoesNotBreakTheWholePrint()
    {
        var path = Path.Combine(_root.Path, "Полубитый.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteXml(zip, "word/document.xml", Part(new[] { new[] { "Шкаф {{Название}}" } }));
            using var broken = zip.CreateEntry("word/header1.xml").Open();
            using var writer = new StreamWriter(broken);
            writer.Write("<w:hdr><не закрытый тег");
        }
        var dst = Dst("Полубитый заполненный.docx");

        Assert.Equal(1, DocxTemplateFiller.Fill(path, dst, Mark, "ЩУН-3"));
        Assert.Equal("Шкаф ЩУН-3", Text(dst));
    }
}
