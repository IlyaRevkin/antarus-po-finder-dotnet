using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;

namespace AntarusPoFinder.Tests;

/// <summary>Длинное название шкафа в бланке паспорта.
///
/// Просьба Ильи дословно: «если оно длинное — чтобы текст не уехал на другую страницу и
/// форматирование не сбилось». В бланке под название отведено готовое место — ячейка таблицы или
/// строка титульного листа; «ЩУН-3» встаёт как влитое, а «Щит управления насосной станцией
/// пожаротушения ЩУНП-11/2-А» переносится на вторую строку, строка вырастает и хвост бланка уезжает
/// на следующую страницу.
///
/// Решение: подставленное название получает СВОЙ подобранный кегль (см. DocxNameFit) — ни переносов,
/// ни лишних абзацев, ни правок соседнего текста. Проверяется здесь именно это: короткое название
/// оставляет документ ровно таким, каким он был раньше; длинное — уменьшает кегль ТОЛЬКО у себя;
/// неправдоподобно длинное упирается в пол по читаемости и дожимается штатным w:fitText; и ни в
/// одном из случаев в абзаце не появляется второй строки.</summary>
public class PassportLongNameFitTests : IDisposable
{
    private readonly TempRoot _root = new();
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private const string Mark = "{{Название}}";

    /// <summary>Название на 60+ знаков — такие в номенклатуре есть, именно из-за них и заведена вся
    /// эта подгонка.</summary>
    private const string LongName = "Щит управления насосной станцией пожаротушения ЩУНП-11/2-А";

    public void Dispose() => _root.Dispose();

    // ── Сборка подопытного документа ────────────────────────────────────────────────────────

    /// <summary>Абзац из перечисленных прогонов. size — кегль в половинках пункта (как w:sz); 0 —
    /// не задавать вовсе, тогда он придёт из docDefaults.</summary>
    private static XElement Paragraph(int size, params string[] runs) =>
        new(W + "p", runs.Select(text => new XElement(W + "r",
            size > 0 ? new XElement(W + "rPr", new XElement(W + "sz", new XAttribute(W + "val", size))) : null,
            new XElement(W + "t", text))));

    /// <summary>Абзац внутри ячейки таблицы заданной ширины (твипы) — так название стоит в
    /// большинстве настоящих бланков.</summary>
    private static XElement TableWithCell(int cellWidthTwips, XElement paragraph) =>
        new(W + "tbl",
            new XElement(W + "tr",
                new XElement(W + "tc",
                    new XElement(W + "tcPr", new XElement(W + "tcW",
                        new XAttribute(W + "w", cellWidthTwips), new XAttribute(W + "type", "dxa"))),
                    paragraph)));

    /// <summary>Раздел: A4 книжная с полями по дюйму — то, что Word ставит новому документу.</summary>
    private static XElement SectionA4() =>
        new(W + "sectPr",
            new XElement(W + "pgSz", new XAttribute(W + "w", 11906), new XAttribute(W + "h", 16838)),
            new XElement(W + "pgMar", new XAttribute(W + "left", 1440), new XAttribute(W + "right", 1440)));

    private string MakeDocx(string fileName, params object[] bodyContent)
    {
        var path = Path.Combine(_root.Path, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var stream = zip.CreateEntry("word/document.xml").Open())
            new XDocument(new XElement(W + "document", new XElement(W + "body", bodyContent, SectionA4()))).Save(stream);
        return path;
    }

    private string Dst(string name) => Path.Combine(_root.Path, "out", name);

    // ── Чтение результата ───────────────────────────────────────────────────────────────────

    private static XDocument Read(string docx, string part = "word/document.xml")
    {
        using var zip = ZipFile.OpenRead(docx);
        using var stream = zip.GetEntry(part)!.Open();
        return XDocument.Load(stream);
    }

    private static List<XElement> Runs(XDocument doc) => doc.Descendants(W + "r").ToList();

    private static string RunText(XElement run) => string.Concat(run.Elements(W + "t").Select(t => t.Value));

    private static int? Size(XElement run)
    {
        var raw = run.Element(W + "rPr")?.Element(W + "sz")?.Attribute(W + "val")?.Value;
        return raw is null ? null : int.Parse(raw);
    }

    private static XElement RunWith(XDocument doc, string text) =>
        Runs(doc).Single(r => RunText(r) == text);

    private static string Text(XDocument doc) => string.Concat(doc.Descendants(W + "t").Select(t => t.Value));

    // ── Подстановка в бланк ─────────────────────────────────────────────────────────────────

    /// <summary>Короткое название ничего не ужимает: документ выходит ровно таким же, каким выходил
    /// до появления подгонки. Это главная гарантия обратной совместимости — обычный «ЩУН-3»
    /// печатается как печатался.</summary>
    [Fact]
    public void ShortName_LeavesTheRunExactlyAsItWas()
    {
        var src = MakeDocx("Бланк НКУ.docx", TableWithCell(5000, Paragraph(28, Mark)));
        var dst = Dst("Короткое.docx");

        Assert.Equal(1, DocxTemplateFiller.Fill(src, dst, Mark, "ЩУН-3"));

        var doc = Read(dst);
        var run = Assert.Single(Runs(doc));
        Assert.Equal("ЩУН-3", RunText(run));
        Assert.Equal(28, Size(run));                                  // кегль бланка не тронут
        Assert.Null(run.Element(W + "rPr")?.Element(W + "fitText"));  // и глифы не сжаты
    }

    /// <summary>Длинное название в узкой ячейке — кегль уменьшается. Абзац при этом остаётся ОДНИМ и
    /// без разрывов строк: ровно то, из-за чего бланк и уезжал на вторую страницу.</summary>
    [Fact]
    public void LongName_ShrinksTheFont_WithoutAddingLinesOrParagraphs()
    {
        var src = MakeDocx("Бланк ШР.docx", TableWithCell(4000, Paragraph(28, Mark)));
        var dst = Dst("Длинное.docx");

        DocxTemplateFiller.Fill(src, dst, Mark, LongName);

        var doc = Read(dst);
        var run = RunWith(doc, LongName);
        Assert.True(Size(run) < 28, $"кегль должен был уменьшиться, а остался {Size(run)}");
        Assert.True(Size(run) >= 14, $"кегль не должен падать ниже пола читаемости, а стал {Size(run)}");

        Assert.Single(doc.Descendants(W + "p"));
        Assert.Empty(doc.Descendants(W + "br"));
        Assert.Equal(LongName, Text(doc));   // ничего не обрезано
    }

    /// <summary>Кегль подбирается ПО МЕСТУ, а не «на глаз одним коэффициентом»: чем уже ячейка, тем
    /// мельче. Иначе в широкой рамке название ужималось бы без всякой нужды.</summary>
    [Fact]
    public void TheNarrowerTheCell_TheSmallerTheFont()
    {
        int SizeInCell(int cellWidth, string file)
        {
            var src = MakeDocx(file, TableWithCell(cellWidth, Paragraph(28, Mark)));
            var dst = Dst(file);
            DocxTemplateFiller.Fill(src, dst, Mark, LongName);
            return Size(RunWith(Read(dst), LongName)) ?? 28;
        }

        var wide = SizeInCell(6000, "Широкая.docx");
        var narrow = SizeInCell(3000, "Узкая.docx");

        Assert.True(narrow < wide, $"в узкой ячейке кегль ({narrow}) должен быть меньше, чем в широкой ({wide})");
    }

    /// <summary>Подпись слева от названия («Наименование:») стоит в ТОМ ЖЕ прогоне, что и метка, —
    /// уменьшив прогон целиком, мы ужали бы и её, а это и есть «форматирование сбилось». Прогон
    /// разрезается: подпись остаётся исходного кегля, мельчает только название.</summary>
    [Fact]
    public void LongName_ShrinksOnlyItself_NotTheLabelSharingTheSameRun()
    {
        var src = MakeDocx("Бланк СПЛ.docx", TableWithCell(4000, Paragraph(28, $"Наименование: {Mark}")));
        var dst = Dst("С подписью.docx");

        DocxTemplateFiller.Fill(src, dst, Mark, LongName);

        var doc = Read(dst);
        Assert.Equal($"Наименование: {LongName}", Text(doc));
        Assert.Equal(28, Size(RunWith(doc, "Наименование: ")));
        Assert.True(Size(RunWith(doc, LongName)) < 28);
        Assert.Single(doc.Descendants(W + "p"));
    }

    /// <summary>Название длиной с абзац: до бесконечности мельчить нельзя — паспорт уходит заказчику
    /// и должен читаться. Кегль упирается в пол, а вписывание в ширину доделывает штатный w:fitText
    /// (сжимает сами буквы). Обрезать хвост названия при этом мы не имеем права.</summary>
    [Fact]
    public void AbsurdlyLongName_StopsAtTheReadableFloor_AndCompressesTheGlyphsInstead()
    {
        var absurd = string.Join(" ", Enumerable.Repeat("Щит управления насосной станцией пожаротушения", 6));
        var src = MakeDocx("Бланк.docx", TableWithCell(3000, Paragraph(28, Mark)));
        var dst = Dst("Абсурд.docx");

        DocxTemplateFiller.Fill(src, dst, Mark, absurd);

        var doc = Read(dst);
        var run = RunWith(doc, absurd);
        Assert.Equal(15, Size(run));   // пол: max(7 пт = 14, floor(28 × 0.55) = 15)

        var fitText = run.Element(W + "rPr")?.Element(W + "fitText");
        Assert.NotNull(fitText);
        Assert.True(int.Parse(fitText!.Attribute(W + "val")!.Value) > 0);

        Assert.Equal(absurd, Text(doc));
        Assert.Single(doc.Descendants(W + "p"));
    }

    /// <summary>Кегль абзаца берётся из документа, а не гадается: у бланка с мелким шрифтом ширина
    /// строки другая, и подгонка обязана считать по настоящему размеру. Здесь у прогона своего
    /// размера нет вовсе — он приходит из docDefaults, как в большинстве настоящих бланков.</summary>
    [Fact]
    public void TheFontSizeIsReadFromTheDocumentDefaults_WhenTheRunDoesNotSetItsOwn()
    {
        var path = Path.Combine(_root.Path, "Из стилей.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var stream = zip.CreateEntry("word/document.xml").Open())
                new XDocument(new XElement(W + "document",
                    new XElement(W + "body", TableWithCell(4000, Paragraph(0, Mark)), SectionA4()))).Save(stream);
            using (var stream = zip.CreateEntry("word/styles.xml").Open())
                new XDocument(new XElement(W + "styles",
                    new XElement(W + "docDefaults", new XElement(W + "rPrDefault",
                        new XElement(W + "rPr", new XElement(W + "sz", new XAttribute(W + "val", 32))))))).Save(stream);
        }
        var dst = Dst("Из стилей заполненный.docx");

        DocxTemplateFiller.Fill(path, dst, Mark, LongName);

        var size = Size(RunWith(Read(dst), LongName));
        Assert.NotNull(size);
        // Считали от 32 (16 пт), а не от умолчания «11 пт»: иначе подгонка решила бы, что текст
        // почти влезает, и уменьшила бы кегль заметно слабее нужного.
        Assert.True(size < 32);
        Assert.True(size > 32 * 0.5, $"пол по доле кегля — 0,55, а получилось {size} из 32");
    }

    /// <summary>Подгонку можно выключить — на случай бланка, который её не переживает. Тогда
    /// подстановка ведёт себя ровно так, как вела до её появления.</summary>
    [Fact]
    public void FittingCanBeTurnedOff()
    {
        var src = MakeDocx("Бланк.docx", TableWithCell(3000, Paragraph(28, Mark)));
        var dst = Dst("Без подгонки.docx");

        DocxTemplateFiller.Fill(src, dst, Mark, LongName, NameFitOptions.Off);

        var run = RunWith(Read(dst), LongName);
        Assert.Equal(28, Size(run));
        Assert.Null(run.Element(W + "rPr")?.Element(W + "fitText"));
    }

    /// <summary>Метка в шапке бланка (там название шкафа стоит сплошь и рядом) — раздела и таблицы
    /// рядом нет, мерить не по чему. Подгонка обязана отработать по запасной ширине страницы, а не
    /// свалиться.</summary>
    [Fact]
    public void AHeaderWithoutASection_StillFits()
    {
        var path = Path.Combine(_root.Path, "С шапкой.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var stream = zip.CreateEntry("word/document.xml").Open())
                new XDocument(new XElement(W + "document", new XElement(W + "body", Paragraph(28, "Паспорт"), SectionA4()))).Save(stream);
            using (var stream = zip.CreateEntry("word/header1.xml").Open())
                new XDocument(new XElement(W + "hdr", Paragraph(40, Mark))).Save(stream);
        }
        var dst = Dst("С шапкой заполненный.docx");

        Assert.Equal(1, DocxTemplateFiller.Fill(path, dst, Mark, LongName));

        var header = Read(dst, "word/header1.xml");
        Assert.Equal(LongName, Text(header));
        Assert.True(Size(RunWith(header, LongName)) < 40);
    }

    // ── Арифметика подгонки ─────────────────────────────────────────────────────────────────

    /// <summary>Влезает — не трогаем. Самый частый случай и единственный, в котором документ обязан
    /// остаться байт в байт прежним.</summary>
    [Fact]
    public void Decide_LeavesTheSizeAlone_WhenTheNameAlreadyFits()
    {
        var decision = DocxNameFit.Decide(availableTwips: 5000, otherTwips: 0, valueTwipsAtBase: 2000,
            baseHalfPoints: 28, NameFitOptions.Default);

        Assert.Equal(28, decision.HalfPoints);
        Assert.Null(decision.FitTextTwips);
    }

    /// <summary>Не влезает — кегль считается пропорцией «сколько места есть к тому, сколько просят»,
    /// и округляется ВНИЗ: «почти влезает» — это всё ещё вторая строка.</summary>
    [Fact]
    public void Decide_ScalesTheSizeToTheRoomLeft()
    {
        var decision = DocxNameFit.Decide(availableTwips: 5000, otherTwips: 1000, valueTwipsAtBase: 5000,
            baseHalfPoints: 28, NameFitOptions.Default);

        Assert.Equal((int)Math.Floor(28 * (4000.0 / 5000.0)), decision.HalfPoints);   // 22
        Assert.Null(decision.FitTextTwips);
    }

    /// <summary>Ниже пола читаемости кегль не опускается, а оставшееся дожимается w:fitText — ровно
    /// на ту ширину, что реально осталась под название.</summary>
    [Fact]
    public void Decide_StopsAtTheFloor_AndAsksWordToCompressTheGlyphs()
    {
        var decision = DocxNameFit.Decide(availableTwips: 3000, otherTwips: 0, valueTwipsAtBase: 30000,
            baseHalfPoints: 28, NameFitOptions.Default);

        Assert.Equal(15, decision.HalfPoints);     // max(7 пт = 14, floor(28 × 0,55) = 15)
        Assert.Equal(3000, decision.FitTextTwips);
    }

    /// <summary>Подпись заняла всю строку — считать, что места нет вовсе, нельзя: тогда любое
    /// название падало бы на минимальный кегль. Название всё равно получает свою долю строки.</summary>
    [Fact]
    public void Decide_KeepsRoomForTheName_EvenWhenTheLabelAlreadyFillsTheLine()
    {
        var decision = DocxNameFit.Decide(availableTwips: 4000, otherTwips: 4200, valueTwipsAtBase: 2000,
            baseHalfPoints: 28, NameFitOptions.Default);

        Assert.True(decision.HalfPoints > 0);
        Assert.True(decision.HalfPoints < 28);
    }

    // ── Сколько места есть ──────────────────────────────────────────────────────────────────

    /// <summary>Ширина ячейки берётся из самой ячейки, минус её поля: место под название — это
    /// именно рамка бланка, а не вся страница.</summary>
    [Fact]
    public void AvailableWidth_InATableCell_IsTheCellMinusItsMargins()
    {
        var paragraph = Paragraph(28, Mark);
        var body = new XElement(W + "body", TableWithCell(4000, paragraph), SectionA4());
        _ = new XDocument(new XElement(W + "document", body));

        Assert.Equal(4000 - 216, DocxNameFit.AvailableWidthTwips(paragraph));
    }

    /// <summary>Абзац вне таблицы меряется полосой набора: ширина страницы минус поля.</summary>
    [Fact]
    public void AvailableWidth_OutsideATable_IsThePageTextWidth()
    {
        var paragraph = Paragraph(28, Mark);
        _ = new XDocument(new XElement(W + "document", new XElement(W + "body", paragraph, SectionA4())));

        Assert.Equal(11906 - 1440 - 1440, DocxNameFit.AvailableWidthTwips(paragraph));
    }

    /// <summary>Мерить не по чему (кусок документа без раздела) — запасная ширина A4, а не ноль и не
    /// исключение: подгонка не имеет права ронять печать.</summary>
    [Fact]
    public void AvailableWidth_WithNothingToMeasureBy_FallsBackToA4()
    {
        var paragraph = Paragraph(28, Mark);
        _ = new XDocument(new XElement(W + "hdr", paragraph));

        Assert.Equal(DocxNameFit.FallbackWidthTwips, DocxNameFit.AvailableWidthTwips(paragraph));
    }

    /// <summary>Ширина текста растёт и с длиной, и с кеглем — на этом стоит весь подбор.</summary>
    [Fact]
    public void TextWidth_GrowsWithLengthAndWithFontSize()
    {
        Assert.True(DocxNameFit.TextWidthTwips(LongName, 28) > DocxNameFit.TextWidthTwips("ЩУН-3", 28));
        Assert.True(DocxNameFit.TextWidthTwips("ЩУН-3", 40) > DocxNameFit.TextWidthTwips("ЩУН-3", 20));
        Assert.Equal(0, DocxNameFit.TextWidthTwips("", 28));
    }
}
