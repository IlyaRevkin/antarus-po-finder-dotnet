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
            try { if (doc is not null) doc.Close(false); } catch (Exception) { }
            try { if (word is not null) word.Quit(); } catch (Exception) { }
            if (doc is not null) TryRelease(doc);
            if (word is not null) TryRelease(word);
        }
    }

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

    private static string? FindSoffice()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.exe"),
        };
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch (Exception) { }
        }
        return null;
    }
}
