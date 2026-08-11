using System.IO;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что именно лежит в папке инструкции конкретной прошивки: исходный документ Word для правки
/// (.docx/.doc), готовый PDF для печати, и не устарел ли PDF относительно docx. Оба файла живут в
/// ОДНОЙ папке (общая папка «Инструкция» рядом с папкой контроллера) — docx это исходник, pdf собирается
/// из него, чтобы печатать сразу, без открытия редактора. Когда docx правят, pdf надо пересобрать —
/// признак <see cref="InstructionDoc.PdfStale"/> отвечает на этот вопрос по времени изменения файлов.
///
/// Ходит на диск (в т.ч. сетевой) — звать из фонового обхода или по клику, не из отрисовки.</summary>
public sealed record InstructionDoc(string? Folder, string? Docx, string? Pdf, string? Newest, bool PdfStale)
{
    /// <summary>Есть ли вообще какой-то файл инструкции (docx, pdf или легаси-файл иного формата).</summary>
    public bool HasAny => Newest is not null;

    /// <summary>Есть что печатать: готовый pdf либо docx, из которого его можно собрать.</summary>
    public bool CanPrint => Pdf is not null || Docx is not null;

    /// <summary>Куда положить собранный из docx PDF — рядом с docx, тем же именем. Так оба файла
    /// оказываются в одной папке конвертации, как и требуется.</summary>
    public string? ExpectedPdfPath => Docx is null ? null : Path.ChangeExtension(Docx, ".pdf");
}

public static class InstructionDocResolver
{
    private static readonly string[] DocxExts = { ".docx", ".doc" };

    /// <summary>storedPath — путь, записанный к версии в БД (файл или папка, может уже не существовать);
    /// sharedFolder — общая папка «Инструкция» рядом с папкой контроллера (каноническая папка конвертации).
    /// Папкой инструкции считается общая папка, если она есть, — именно в ней лежат docx и его pdf.</summary>
    /// <param name="excludeSubfolder">Подпапка, которую не считать частью документа — «Прежние
    /// редакции» паспорта шкафа (см. DocFileResolver.Resolve). null — вся папка целиком, как было.</param>
    public static InstructionDoc Resolve(string? storedPath, string? sharedFolder, string? excludeSubfolder = null)
    {
        var newest = DocFileResolver.Resolve(storedPath, sharedFolder, excludeSubfolder);
        var folder = FolderOf(newest, storedPath, sharedFolder);
        if (folder is null || !Directory.Exists(folder))
            return new InstructionDoc(folder, null, null, newest, false);

        var docx = NewestWithExt(folder, DocxExts, excludeSubfolder);
        var pdf = NewestWithExt(folder, new[] { ".pdf" }, excludeSubfolder);
        var stale = docx is not null && (pdf is null || WrittenAt(docx) > WrittenAt(pdf));
        return new InstructionDoc(folder, docx, pdf, newest ?? docx ?? pdf, stale);
    }

    /// <summary>Общая папка инструкции — предпочтительна (это и есть папка конвертации). Иначе — папка
    /// сохранённого пути (сам путь, если это папка, или каталог файла). Иначе — каталог самого свежего
    /// найденного файла. null — открывать нечего.</summary>
    private static string? FolderOf(string? newestFile, string? storedPath, string? sharedFolder)
    {
        if (!string.IsNullOrEmpty(sharedFolder) && Directory.Exists(sharedFolder)) return sharedFolder;
        if (!string.IsNullOrEmpty(storedPath))
        {
            if (Directory.Exists(storedPath)) return storedPath;
            if (File.Exists(storedPath)) return Path.GetDirectoryName(storedPath);
        }
        return newestFile is not null ? Path.GetDirectoryName(newestFile) : null;
    }

    private static string? NewestWithExt(string folder, string[] exts, string? excludeSubfolder)
    {
        try
        {
            // .lnk отсеивается вместе с прочим неподходящим расширением — ярлык на уехавшую на
            // третий диск инструкцию документом не является. Заглушка «Инструкция в разработке» —
            // тоже .pdf, и её приходится отсеивать явно: иначе «инструкция есть, можно печатать»
            // включалось бы ровно там, где инструкции ещё нет (см. DocFileResolver.IsNotADocument).
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant())
                            && !DocFileResolver.IsNotADocument(f)
                            && !DocFileResolver.IsUnder(folder, f, excludeSubfolder))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DateTime WrittenAt(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }
}
