using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Подстановка названия шкафа в бланк паспорта: в шаблоне стоит метка (по умолчанию
/// «{{Название}}»), оператор вводит название — программа делает копию документа с подставленным
/// текстом и отправляет её на печать. Сам шаблон при этом не меняется: правится всегда КОПИЯ, иначе
/// первая же печать испортила бы общий бланк для всех.
///
/// Почему не через Word/COM: конвертер уже поднимает Word ради PDF, и второй проход через
/// автоматизацию удвоил бы самую медленную часть; docx — это zip с XML внутри, и текстовая замена в
/// нём делается без единой внешней зависимости.
///
/// Главная тонкость — Word режет текст абзаца на «прогоны» (w:r/w:t) как ему удобно: проверка
/// орфографии, отмеченная правка, смена языка ввода посреди слова. Поэтому метка «{{Название}}»
/// почти никогда не лежит в одном w:t целиком, и наивная замена по содержимому отдельных w:t не
/// находит её вовсе. Здесь текст абзаца сначала склеивается, метка ищется в склейке, а замена
/// раскладывается обратно по тем прогонам, которые она задела — остальные не трогаются, чтобы у
/// абзаца «Наименование: {{Название}}» жирная подпись слева осталась жирной.
///
/// Вторая тонкость — ДЛИНА подставляемого. Название шкафа бывает и «ЩУН-3», и «Щит управления
/// насосной станцией пожаротушения ЩУНП-11/2-А», а место под него в бланке отведено готовое: строка
/// таблицы или строка титульного листа. Длинное название переносится на вторую строку, строка
/// вырастает, и хвост бланка уезжает на следующую страницу. Поэтому подставленное значение получает
/// свой подобранный кегль — см. <see cref="DocxNameFit"/>, там же разобрано, почему именно кегль, а
/// не перенос или обрезание.</summary>
public static class DocxTemplateFiller
{
    /// <summary>Метка по умолчанию. Двойные фигурные скобки — чтобы её нельзя было спутать с обычным
    /// текстом бланка и чтобы незаполненная метка бросалась в глаза, если печать пойдёт как есть.</summary>
    public const string DefaultPlaceholder = "{{Название}}";

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Xml = "http://www.w3.org/XML/1998/namespace";

    /// <summary>Умеем ли подставлять в такой файл. Только .docx: .doc — это бинарный формат Word,
    /// а .pdf вообще не редактируемый документ, их печатаем как есть.</summary>
    public static bool IsSupported(string? path) =>
        !string.IsNullOrEmpty(path) && Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase);

    /// <summary>Сколько раз метка встречается в шаблоне. 0 — подставлять некуда: либо метку в бланк
    /// не вписали, либо она написана иначе, чем задано в настройках. Нужно, чтобы сказать об этом
    /// ДО печати, а не отправить на бумагу лист с «{{Название}}» вместо названия.</summary>
    public static int Count(string docxPath, string placeholder) =>
        Process(docxPath, null, placeholder, value: null, fit: NameFitOptions.Off);

    /// <summary>Делает копию <paramref name="srcDocx"/> в <paramref name="dstDocx"/> с подставленным
    /// значением. Возвращает число сделанных замен; 0 — метки в документе не нашлось (копия при этом
    /// всё равно создана и её можно напечатать как есть). Кидает исключения ввода-вывода наверх:
    /// «не смогли записать копию» — это то, о чём оператору надо сказать, а не проглотить.</summary>
    /// <param name="fit">Подгонка длинного названия под отведённое место (см. <see cref="DocxNameFit"/>).
    /// null — правило по умолчанию: подгонять. Короткое название под него не попадает вовсе, так что
    /// для обычного «ЩУН-3» документ выходит ровно тем же, что и без подгонки.</param>
    public static int Fill(string srcDocx, string dstDocx, string placeholder, string value, NameFitOptions? fit = null) =>
        Process(srcDocx, dstDocx, placeholder, value ?? "", fit ?? NameFitOptions.Default);

    /// <summary>Общий проход по документу: с <paramref name="dstDocx"/> = null только считает
    /// вхождения, не трогая файл.</summary>
    private static int Process(string srcDocx, string? dstDocx, string placeholder, string? value, NameFitOptions fit)
    {
        if (string.IsNullOrWhiteSpace(placeholder)) return 0;
        if (!IsSupported(srcDocx) || !File.Exists(srcDocx)) return 0;

        var path = srcDocx;
        if (dstDocx is not null)
        {
            var dir = Path.GetDirectoryName(dstDocx);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(srcDocx, dstDocx, overwrite: true);
            // Копия шаблона с общего диска приезжает с его атрибутом «только для чтения» — без
            // снятия запись в неё упадёт правом доступа.
            try { File.SetAttributes(dstDocx, FileAttributes.Normal); } catch (Exception) { }
            path = dstDocx;
        }

        var mode = dstDocx is null ? ZipArchiveMode.Read : ZipArchiveMode.Update;
        using var zip = ZipFile.Open(path, mode);

        var documentDefaultSize = ReadDefaultFontSize(zip);

        var total = 0;
        foreach (var entry in zip.Entries.Where(e => IsTextPart(e.FullName)).ToList())
        {
            XDocument doc;
            try
            {
                using var read = entry.Open();
                doc = XDocument.Load(read);
            }
            catch (Exception)
            {
                // Часть документа не разобралась как XML — пропускаем её, а не роняем всю печать:
                // остальные части всё равно могут содержать метку.
                continue;
            }

            var replaced = ReplaceInDocument(doc, placeholder, value, fit, documentDefaultSize);
            total += replaced;
            if (replaced == 0 || value is null) continue;

            using var write = entry.Open();
            write.SetLength(0);
            doc.Save(write);
        }
        return total;
    }

    /// <summary>Части документа, где вообще бывает видимый текст. Стили, шрифты и настройки не
    /// трогаем: метки там не бывает, а вот испортить их случайной заменой можно.</summary>
    private static bool IsTextPart(string entryName)
    {
        if (!entryName.StartsWith("word/", StringComparison.OrdinalIgnoreCase)) return false;
        var name = entryName["word/".Length..];
        if (name.Contains('/')) return false;
        return name.Equals("document.xml", StringComparison.OrdinalIgnoreCase)
            || name.Equals("footnotes.xml", StringComparison.OrdinalIgnoreCase)
            || name.Equals("endnotes.xml", StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith("header", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            || (name.StartsWith("footer", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Кегль, заданный документу целиком (word/styles.xml → docDefaults). Нужен подгонке
    /// длинного названия: в бланке у абзаца с меткой размер шрифта чаще всего не проставлен, он
    /// наследуется — и без этого значения ширину пришлось бы считать по «11 пунктов наугад».</summary>
    private static int ReadDefaultFontSize(ZipArchive zip)
    {
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("word/styles.xml", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return DocxNameFit.DefaultHalfPoints;
        try
        {
            using var read = entry.Open();
            return DocxNameFit.DocumentDefaultHalfPoints(XDocument.Load(read));
        }
        catch (Exception)
        {
            return DocxNameFit.DefaultHalfPoints;
        }
    }

    private static int ReplaceInDocument(XDocument doc, string placeholder, string? value,
        NameFitOptions fit, int documentDefaultSize)
    {
        var count = 0;
        foreach (var paragraph in doc.Descendants(W + "p").ToList())
            count += ReplaceInParagraph(paragraph, placeholder, value, fit, documentDefaultSize);
        return count;
    }

    /// <summary>Замена в одном абзаце. Абзац — правильная единица: Word не разрывает слово между
    /// абзацами, значит метка целиком лежит внутри одного из них, а склеивать весь документ ради
    /// поиска не нужно.</summary>
    private static int ReplaceInParagraph(XElement paragraph, string placeholder, string? value,
        NameFitOptions fit, int documentDefaultSize)
    {
        var texts = paragraph.Descendants(W + "t").ToList();
        if (texts.Count == 0) return 0;

        // Куски текста абзаца с их положением в склейке — чтобы найденное в склейке место потом
        // разложить обратно по конкретным w:t.
        var pieces = new List<string>(texts.Count);
        var starts = new List<int>(texts.Count);
        var at = 0;
        foreach (var t in texts)
        {
            starts.Add(at);
            pieces.Add(t.Value);
            at += t.Value.Length;
        }
        var full = string.Concat(pieces);

        var matches = new List<int>();
        for (var i = full.IndexOf(placeholder, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = full.IndexOf(placeholder, i + placeholder.Length, StringComparison.OrdinalIgnoreCase))
            matches.Add(i);
        if (matches.Count == 0 || value is null) return matches.Count;

        // Куда встало подставленное значение — нужно подгонке кегля ниже. Считаем только когда
        // вхождение в абзаце ОДНО: при нескольких «сколько места осталось под каждое» — это уже
        // другая задача, а в бланках паспортов название в строке стоит один раз.
        int? hostIndex = null;
        var hostOffset = 0;

        // С конца: замена меняет длину текста, и правка более раннего вхождения сдвинула бы границы
        // следующих. Идя справа налево, уже посчитанные позиции остаются верными.
        for (var m = matches.Count - 1; m >= 0; m--)
        {
            var start = matches[m];
            var end = start + placeholder.Length;

            for (var i = texts.Count - 1; i >= 0; i--)
            {
                var pieceStart = starts[i];
                var pieceEnd = pieceStart + pieces[i].Length;
                if (pieceEnd <= start || pieceStart >= end) continue;

                var from = Math.Max(start, pieceStart) - pieceStart;
                var to = Math.Min(end, pieceEnd) - pieceStart;
                // Значение целиком уходит в тот прогон, где метка НАЧИНАЛАСЬ — с его оформлением;
                // из остальных задетых вырезается только их кусок метки.
                var isHost = pieceStart <= start;
                if (isHost && matches.Count == 1) { hostIndex = i; hostOffset = from; }
                pieces[i] = pieces[i][..from] + (isHost ? value : "") + pieces[i][to..];
            }
        }

        for (var i = 0; i < texts.Count; i++)
        {
            if (texts[i].Value == pieces[i]) continue;
            texts[i].Value = pieces[i];
            // Без xml:space="preserve" Word срежет пробелы по краям — «ЩУН-3 » превратилось бы в
            // «ЩУН-3», а пустой остаток прогона схлопнулся бы вместе с соседним пробелом.
            texts[i].SetAttributeValue(Xml + "space", "preserve");
        }

        if (fit.Enabled && value.Length > 0 && hostIndex is { } host)
            FitValue(paragraph, texts[host], hostOffset, value, fit, documentDefaultSize);

        return matches.Count;
    }

    // ── Подгонка длинного названия ───────────────────────────────────────────────────────────

    /// <summary>Уменьшает кегль подставленного названия ровно настолько, чтобы строка уложилась в
    /// отведённую ширину. Всё, что решает «надо ли и насколько», живёт в <see cref="DocxNameFit"/>;
    /// здесь только работа с XML: посчитать ширину соседнего текста абзаца, выделить значение в
    /// собственный прогон (иначе вместе с ним ужалась бы и подпись «Наименование:», стоящая в том же
    /// прогоне) и проставить размер.</summary>
    private static void FitValue(XElement paragraph, XElement host, int offset, string value,
        NameFitOptions fit, int documentDefaultSize)
    {
        var run = host.Ancestors(W + "r").FirstOrDefault();
        if (run is null) return;

        var baseSize = DocxNameFit.EffectiveHalfPoints(run, paragraph, documentDefaultSize);
        var available = DocxNameFit.AvailableWidthTwips(paragraph);

        var other = 0.0;
        foreach (var t in paragraph.Descendants(W + "t"))
        {
            var size = DocxNameFit.EffectiveHalfPoints(t.Ancestors(W + "r").FirstOrDefault(), paragraph, documentDefaultSize);
            // Из прогона-хозяина вычитаем само значение: его ширину считаем отдельно, она и есть
            // то, чем подгонка распоряжается.
            var text = ReferenceEquals(t, host) ? t.Value[..offset] + t.Value[(offset + value.Length)..] : t.Value;
            other += DocxNameFit.TextWidthTwips(text, size);
        }

        var decision = DocxNameFit.Decide(available, other, DocxNameFit.TextWidthTwips(value, baseSize), baseSize, fit);
        if (decision.HalfPoints >= baseSize && decision.FitTextTwips is null) return;

        var target = IsolateValueRun(run, host, offset, value);
        ApplySize(target, decision);
    }

    /// <summary>Прогон, в котором лежит ТОЛЬКО подставленное значение. Если в исходном прогоне кроме
    /// значения ничего нет — это он сам. Иначе прогон разрезается на «до», «значение» и «после» с
    /// тем же оформлением: у абзаца «Наименование: {{Название}}», где подпись и метка оказались в
    /// одном прогоне, ужать надо название, а не подпись.
    ///
    /// Прогон с чем-то кроме текста (картинка, разрыв строки, табуляция) не режем: клонирование
    /// задвоило бы это «что-то». Такой прогон уменьшается целиком — хуже, чем точечно, но всё равно
    /// лучше листа, уехавшего на другую страницу.</summary>
    private static XElement IsolateValueRun(XElement run, XElement host, int offset, string value)
    {
        var texts = run.Elements(W + "t").ToList();
        var prefix = host.Value[..offset];
        var suffix = host.Value[(offset + value.Length)..];

        if (prefix.Length == 0 && suffix.Length == 0 && texts.Count == 1) return run;
        if (run.Elements().Any(e => e.Name != W + "rPr" && e.Name != W + "t")) return run;

        var hostIndex = texts.IndexOf(host);
        if (hostIndex < 0) return run;

        var before = texts.Take(hostIndex).Select(t => t.Value).ToList();
        var after = texts.Skip(hostIndex + 1).Select(t => t.Value).ToList();
        if (prefix.Length > 0) before.Add(prefix);
        if (suffix.Length > 0) after.Insert(0, suffix);

        var valueRun = CloneRun(run, new[] { value });
        if (before.Count > 0) run.AddBeforeSelf(CloneRun(run, before));
        run.AddBeforeSelf(valueRun);
        if (after.Count > 0) run.AddAfterSelf(CloneRun(run, after));
        run.Remove();
        return valueRun;
    }

    private static XElement CloneRun(XElement run, IEnumerable<string> texts)
    {
        var clone = new XElement(run.Name, run.Attributes().Select(a => new XAttribute(a)));
        if (run.Element(W + "rPr") is { } rPr) clone.Add(new XElement(rPr));
        foreach (var text in texts)
            clone.Add(new XElement(W + "t", new XAttribute(Xml + "space", "preserve"), text));
        return clone;
    }

    private static void ApplySize(XElement run, NameFitDecision decision)
    {
        var rPr = run.Element(W + "rPr");
        if (rPr is null)
        {
            rPr = new XElement(W + "rPr");
            run.AddFirst(rPr);
        }

        var size = decision.HalfPoints.ToString(CultureInfo.InvariantCulture);
        SetRunProperty(rPr, "sz", new XAttribute(W + "val", size));
        // szCs — тот же кегль для «сложных» письменностей. Word держит их парой, и оставленный
        // прежним szCs местами перебивает sz.
        SetRunProperty(rPr, "szCs", new XAttribute(W + "val", size));

        if (decision.FitTextTwips is { } width)
            SetRunProperty(rPr, "fitText",
                new XAttribute(W + "val", width.ToString(CultureInfo.InvariantCulture)),
                new XAttribute(W + "id", "1"));
    }

    /// <summary>Порядок элементов внутри w:rPr задан схемой как последовательность, а не как
    /// произвольный набор: дописанный «куда попало» w:sz Word прочитает, а строгий валидатор — нет.
    /// Поэтому свойство ставится на своё место в этом ряду.</summary>
    private static readonly string[] RunPropertyOrder =
    [
        "rStyle", "rFonts", "b", "bCs", "i", "iCs", "caps", "smallCaps", "strike", "dstrike", "outline",
        "shadow", "emboss", "imprint", "noProof", "snapToGrid", "vanish", "webHidden", "color", "spacing",
        "w", "kern", "position", "sz", "szCs", "highlight", "u", "effect", "bdr", "shd", "fitText",
        "vertAlign", "rtl", "cs", "em", "lang", "eastAsianLayout", "specVanish", "oMath",
    ];

    private static void SetRunProperty(XElement rPr, string name, params XAttribute[] attributes)
    {
        rPr.Elements(W + name).Remove();
        var element = new XElement(W + name, attributes);
        var index = Array.IndexOf(RunPropertyOrder, name);
        // Незнакомые элементы (индекс -1) считаем идущими раньше: их настоящее место нам неизвестно,
        // и вставлять перед ними наугад значило бы портить порядок ещё и им.
        var next = rPr.Elements().FirstOrDefault(e => Array.IndexOf(RunPropertyOrder, e.Name.LocalName) > index);
        if (next is not null) next.AddBeforeSelf(element);
        else rPr.Add(element);
    }
}
