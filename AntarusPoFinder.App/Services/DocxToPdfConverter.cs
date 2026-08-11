using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace AntarusPoFinder.App.Services;

/// <summary>Собирает PDF из документа Word (.docx/.doc). Нужен, чтобы инструкцию можно было печатать
/// сразу, не открывая редактор: docx — исходник, pdf — то, что уходит на принтер.
///
/// Два способа, по очереди: сначала установленный Microsoft Word через позднее связывание COM
/// (жёсткой ссылки на Interop-сборку нет — на машине без Word приложение просто не найдёт ProgID и
/// пойдёт дальше), затем LibreOffice в headless-режиме. Нет ни того, ни другого — вернётся null, и
/// вызывающий предложит открыть исходный документ вручную. Ходит на диск и поднимает внешний процесс —
/// звать из фонового потока, не из UI.</summary>
public static class DocxToPdfConverter
{
    /// <summary>Есть ли на машине чем конвертировать (Word или LibreOffice). Для подсказок/диагностики;
    /// сам Convert всё равно проверяет доступность по ходу.</summary>
    public static bool IsSupported => WordAvailable() || FindSoffice() is not null;

    /// <summary>Путь к собранному PDF при успехе, null — конвертация не удалась (нет конвертера, документ
    /// битый, папка недоступна для записи). outputPdfPath — куда положить результат.</summary>
    public static string? Convert(string docxPath, string outputPdfPath)
    {
        if (string.IsNullOrEmpty(docxPath) || !File.Exists(docxPath)) return null;
        if (TryWord(docxPath, outputPdfPath)) return outputPdfPath;
        if (TrySoffice(docxPath, outputPdfPath)) return outputPdfPath;
        return null;
    }

    private static bool WordAvailable()
    {
        try { return Type.GetTypeFromProgID("Word.Application") is not null; }
        catch (Exception) { return false; }
    }

    private static bool TryWord(string docx, string outPdf)
    {
        Type? wordType;
        try { wordType = Type.GetTypeFromProgID("Word.Application"); }
        catch (Exception) { return false; }
        if (wordType is null) return false;

        dynamic? word = null;
        dynamic? doc = null;
        try
        {
            word = Activator.CreateInstance(wordType);
            if (word is null) return false;
            word.Visible = false;
            try { word.DisplayAlerts = 0; } catch (Exception) { /* wdAlertsNone — не критично */ }

            doc = word.Documents.Open(docx);
            // wdExportFormatPDF = 17
            doc.ExportAsFixedFormat(outPdf, 17);
            return File.Exists(outPdf);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            // Уборка за Word: он мог уже упасть или быть закрыт человеком, и тогда каждый из этих
            // вызовов кидает COM-ошибку. Результат конверсии от этого не зависит (он уже определён
            // выше), а исключение отсюда затёрло бы его собой — поэтому глушим оба молча.
            try { if (doc is not null) doc.Close(false); } catch (Exception) { }
            try { if (word is not null) word.Quit(); } catch (Exception) { }
            if (doc is not null) TryRelease(doc);
            if (word is not null) TryRelease(word);
        }
    }

    /// <summary>Отпустить COM-обёртку. Бросает, если объект уже отпущен или процесс Word умер;
    /// делать с этим нечего — освобождение и так best-effort, а падение здесь только помешало бы
    /// вернуть уже готовый результат конверсии.</summary>
    private static void TryRelease(object comObject)
    {
        try { Marshal.FinalReleaseComObject(comObject); } catch (Exception) { }
    }

    private static bool TrySoffice(string docx, string outPdf)
    {
        var soffice = FindSoffice();
        if (soffice is null) return false;
        var outDir = Path.GetDirectoryName(outPdf);
        if (string.IsNullOrEmpty(outDir)) return false;

        try
        {
            var psi = new ProcessStartInfo(soffice)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--headless");
            psi.ArgumentList.Add("--convert-to");
            psi.ArgumentList.Add("pdf");
            psi.ArgumentList.Add("--outdir");
            psi.ArgumentList.Add(outDir);
            psi.ArgumentList.Add(docx);

            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(120_000))
            {
                // Не уложился в две минуты — снимаем. Kill бросает, если процесс успел завершиться
                // сам между таймаутом и этой строкой; ответ всё равно «конвертация не удалась».
                try { p.Kill(true); } catch (Exception) { }
                return false;
            }

            // soffice кладёт <имя-без-расширения>.pdf в outdir — не обязательно с нужным нам именем.
            var produced = Path.Combine(outDir, Path.GetFileNameWithoutExtension(docx) + ".pdf");
            if (!File.Exists(produced)) return false;
            if (!string.Equals(produced, outPdf, StringComparison.OrdinalIgnoreCase))
                File.Copy(produced, outPdf, overwrite: true);
            return File.Exists(outPdf);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Этот же конвертер, но как <see cref="Core.Services.IDocumentToPdf"/> — ядру нужен PDF
    /// на пути выкладки на хостинг (инструкция в docx туда уходит собранным PDF, см.
    /// InstructionPublisher), а ссылаться из ядра на приложение нельзя.</summary>
    public sealed class Adapter : Core.Services.IDocumentToPdf
    {
        public bool IsSupported => DocxToPdfConverter.IsSupported;

        public string? Convert(string documentPath, string outputPdfPath) =>
            DocxToPdfConverter.Convert(documentPath, outputPdfPath);
    }

    private static string? FindSoffice()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.exe"),
        };
        foreach (var c in candidates)
        {
            // Недоступный путь (нет прав на Program Files, перенаправленная папка) — не повод падать
            // на подборе кандидата: просто идём к следующему, а не нашли ни одного — вернём null.
            try { if (File.Exists(c)) return c; } catch (Exception) { }
        }
        return null;
    }
}
