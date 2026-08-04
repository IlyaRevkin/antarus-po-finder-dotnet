using System;
using System.Collections.Generic;
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
/// абзаца «Наименование: {{Название}}» жирная подпись слева осталась жирной.</summary>
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
        Process(docxPath, null, placeholder, value: null);

    /// <summary>Делает копию <paramref name="srcDocx"/> в <paramref name="dstDocx"/> с подставленным
    /// значением. Возвращает число сделанных замен; 0 — метки в документе не нашлось (копия при этом
    /// всё равно создана и её можно напечатать как есть). Кидает исключения ввода-вывода наверх:
    /// «не смогли записать копию» — это то, о чём оператору надо сказать, а не проглотить.</summary>
    public static int Fill(string srcDocx, string dstDocx, string placeholder, string value) =>
        Process(srcDocx, dstDocx, placeholder, value ?? "");

    /// <summary>Общий проход по документу: с <paramref name="dstDocx"/> = null только считает
    /// вхождения, не трогая файл.</summary>
    private static int Process(string srcDocx, string? dstDocx, string placeholder, string? value)
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

            var replaced = ReplaceInDocument(doc, placeholder, value);
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

    private static int ReplaceInDocument(XDocument doc, string placeholder, string? value)
    {
        var count = 0;
        foreach (var paragraph in doc.Descendants(W + "p"))
            count += ReplaceInParagraph(paragraph, placeholder, value);
        return count;
    }

    /// <summary>Замена в одном абзаце. Абзац — правильная единица: Word не разрывает слово между
    /// абзацами, значит метка целиком лежит внутри одного из них, а склеивать весь документ ради
    /// поиска не нужно.</summary>
    private static int ReplaceInParagraph(XElement paragraph, string placeholder, string? value)
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
                var replacement = pieceStart <= start ? value : "";
                pieces[i] = pieces[i][..from] + replacement + pieces[i][to..];
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
        return matches.Count;
    }
}
