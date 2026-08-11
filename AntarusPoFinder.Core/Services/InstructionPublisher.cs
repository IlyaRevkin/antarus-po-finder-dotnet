using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AntarusPoFinder.Core.Services;

/// <summary>Кто выкладывает инструкцию на хостинг. Интерфейсом — чтобы загрузка версии не зависела
/// ни от сети, ни от HttpClient: в тестах подставляется запоминающая заглушка, а на машине без
/// заполненных ключей не подставляется вовсе (null), и весь этот слой просто не участвует.</summary>
public interface IInstructionPublisher
{
    /// <summary>Выложить файл (или папку постраничных сканов) по пути, который в БАЗЕ записан как
    /// путь на первом диске. Возвращает адрес выложенного файла или null, если выкладка не
    /// настроена; о неудачах сообщает через <paramref name="warnings"/> — загрузку версии они не
    /// отменяют.</summary>
    string? Publish(string actualPath, string pathOnFirstDisk, string firstDiskRoot, List<string> warnings);
}

/// <summary>Выкладка инструкции в бакет хостинга (см. <see cref="S3Settings"/>).
///
/// Ключ объекта считается от пути НА ПЕРВОМ ДИСКЕ, а не от того места, где файл физически лежит:
/// на третьем диске у него другой корень, а у коллеги третий диск вообще подключён под другой
/// буквой. Путь на первом диске — единственное, что одинаково у всех машин (именно он и пишется в
/// <c>fw_versions.instructions_path</c>), поэтому только от него и можно считать адрес, по которому
/// файл будет виден снаружи. Из того же пути строится ссылка под QR-кодом (LabelLinkBuilder) — так
/// наклейка, напечатанная на одной машине, ведёт на файл, выложенный с другой.
///
/// Инструкция ПАПКОЙ (постраничные сканы) выкладывается пофайлово, сохраняя вложенность: у бакета
/// нет каталогов, «папка» в нём — это общий префикс ключа, и ничего специально создавать не нужно.
///
/// Неудача выкладки — предупреждение, а не ошибка: файл к этому моменту уже лежит на диске, версия
/// уже создана, и отменять всё это из-за недоступного хостинга нельзя.</summary>
public sealed class InstructionPublisher : IInstructionPublisher
{
    private readonly S3Settings _settings;
    private readonly S3Client _client;
    private readonly IDocumentToPdf? _pdf;

    /// <summary>Сколько файлов из одной папки сканов выкладываем за раз — защита от того, чтобы
    /// случайно выбранная папка на тысячу файлов не превратила загрузку версии в получасовое
    /// ожидание. Превышение — предупреждение, а не молчаливое усечение.</summary>
    public const int MaxFilesPerFolder = 200;

    /// <summary>Документы Word, которые на хостинг в исходном виде не уходят: вместо них
    /// выкладывается собранный PDF. Причина простая — ссылка под QR открывается на ТЕЛЕФОНЕ у
    /// наладчика в цеху, а .docx там либо не откроется вовсе, либо откроется криво и потянет за собой
    /// установку офисного приложения. PDF открывается везде. Плюс docx — это исходник, который правят,
    /// и выкладывать наружу редактируемый документ незачем.</summary>
    private static readonly string[] WordExtensions = { ".docx", ".doc", ".rtf", ".odt" };

    public InstructionPublisher(S3Settings settings, S3Client? client = null, IDocumentToPdf? pdf = null)
    {
        _settings = settings;
        _client = client ?? new S3Client();
        _pdf = pdf;
    }

    /// <summary>Выкладчик по текущим настройкам — или null, если выкладывать некуда (ключи ещё не
    /// выданы, выкладка выключена, адрес не заполнен). Одно место, где принимается это решение:
    /// иначе каждый вызывающий проверял бы «настроено ли» по-своему, и рано или поздно один из них
    /// проверил бы не то. null здесь — штатное состояние, а не отсутствие возможности.</summary>
    public static IInstructionPublisher? For(S3Settings settings, IDocumentToPdf? pdf = null) =>
        settings.CanPublish ? new InstructionPublisher(settings, client: null, pdf) : null;

    /// <summary>Адрес, по которому файл БУДЕТ доступен после выкладки, — без самой выкладки. Нужен
    /// там, где адрес требуется знать заранее: на наклейке с QR, в списке «что должно лежать на
    /// хостинге». Учитывает подмену docx на pdf, иначе напечатанная ссылка вела бы на файл, которого
    /// на хостинге никогда не будет.</summary>
    public static string? PlannedUrl(S3Settings settings, string pathOnFirstDisk, string firstDiskRoot)
    {
        var relative = LabelLinkBuilder.RelativeTo(firstDiskRoot, pathOnFirstDisk);
        if (relative is null || !settings.HasAddress) return null;
        return S3Client.PublicUrl(settings, settings.KeyFor(AsPublishedName(relative)));
    }

    /// <summary>Под каким именем путь ляжет на хостинг: у документа Word расширение меняется на
    /// «.pdf», всё остальное — как есть.</summary>
    public static string AsPublishedName(string relative) =>
        IsWordDocument(relative) ? Path.ChangeExtension(relative, ".pdf") : relative;

    public static bool IsWordDocument(string path) =>
        WordExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public string? Publish(string actualPath, string pathOnFirstDisk, string firstDiskRoot, List<string> warnings)
    {
        if (!_settings.CanPublish) return null;

        var relative = LabelLinkBuilder.RelativeTo(firstDiskRoot, pathOnFirstDisk);
        if (relative is null)
        {
            // Файл лежит вне диска прошивок — считать его адрес на хостинге не от чего, и
            // выкладывать «куда-нибудь» нельзя: ссылка под QR всё равно указывала бы не туда.
            warnings.Add("Инструкция: файл вне диска прошивок, на хостинг не выложен");
            return null;
        }

        if (Directory.Exists(actualPath)) return PublishFolder(actualPath, relative, warnings);
        return PublishFile(actualPath, relative, warnings);
    }

    private string? PublishFile(string filePath, string relative, List<string> warnings)
    {
        // Документ Word уезжает не собой, а собранным из него PDF — и ключ считается уже от PDF-имени.
        if (IsWordDocument(filePath))
        {
            var converted = ToPdf(filePath, warnings);
            if (converted is null) return null;

            using var temp = converted;
            return Put(temp.Path, _settings.KeyFor(AsPublishedName(relative)), warnings);
        }

        return Put(filePath, _settings.KeyFor(relative), warnings);
    }

    /// <summary>Собственно отправка одного файла, с проверкой размера перед ней. Размер проверяется
    /// ЗДЕСЬ, а не у вызывающего: через это место проходят все пути выкладки — и документ, и
    /// постраничные сканы, и собранный из docx PDF (который может оказаться толще исходника).</summary>
    private string? Put(string filePath, string key, List<string> warnings)
    {
        if (!SizeAllowed(filePath, warnings)) return null;

        var result = _client.PutFileAsync(_settings, key, filePath, CancellationToken.None)
            .GetAwaiter().GetResult();

        if (result.Ok) return result.Url;
        warnings.Add($"Инструкция: на хостинг не выложена — {result.Error}");
        return null;
    }

    /// <summary>Проверка предела размера. Мягкий режим ничего не запрещает, но и не молчит: файл на
    /// сотню мегабайт по ссылке из цеха качать никто не будет, и человек должен об этом знать в тот
    /// момент, когда ещё может что-то поменять.</summary>
    private bool SizeAllowed(string filePath, List<string> warnings)
    {
        long size;
        try { size = new FileInfo(filePath).Length; }
        catch (Exception) { return true; } // размер не прочитался — не повод отказываться от выкладки

        if (size <= _settings.MaxFileBytes) return true;

        var actual = Megabytes(size);
        var limit = Megabytes(_settings.MaxFileBytes);
        warnings.Add(_settings.HardSizeLimit
            ? $"«{Path.GetFileName(filePath)}»: {actual} МБ при пределе {limit} МБ — на хостинг не выложен. " +
              "Предел и режим меняются в настройках хранилища."
            : $"«{Path.GetFileName(filePath)}»: {actual} МБ при пределе {limit} МБ — выложен, но по ссылке с телефона будет качаться долго.");

        return !_settings.HardSizeLimit;
    }

    private static string Megabytes(long bytes) => (bytes / 1024d / 1024d).ToString("0.#");

    /// <summary>Собранный PDF во временной папке. Живёт ровно до конца выкладки — рядом с исходником
    /// на диске его класть нельзя: там канонические имена документов, и лишний файл выглядел бы как
    /// вторая инструкция.</summary>
    private TempPdf? ToPdf(string documentPath, List<string> warnings)
    {
        if (_pdf is null || !_pdf.IsSupported)
        {
            warnings.Add($"«{Path.GetFileName(documentPath)}»: на хостинг кладётся PDF, а собрать его на этой машине нечем " +
                         "(нет ни Word, ни LibreOffice) — документ не выложен. Приложите PDF или выложите с машины, где офис есть.");
            return null;
        }

        var folder = Path.Combine(Path.GetTempPath(), "antarus-pdf-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, Path.GetFileNameWithoutExtension(documentPath) + ".pdf");
            if (_pdf.Convert(documentPath, target) is { } produced && File.Exists(produced))
                return new TempPdf(produced, folder);

            warnings.Add($"«{Path.GetFileName(documentPath)}»: не удалось собрать PDF, на хостинг не выложен.");
        }
        catch (Exception ex)
        {
            warnings.Add($"«{Path.GetFileName(documentPath)}»: не удалось собрать PDF — {ex.Message}");
        }

        TempPdf.Cleanup(folder);
        return null;
    }

    private sealed class TempPdf : IDisposable
    {
        private readonly string _folder;
        public string Path { get; }

        public TempPdf(string path, string folder)
        {
            Path = path;
            _folder = folder;
        }

        public void Dispose() => Cleanup(_folder);

        public static void Cleanup(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (Exception) { /* временная папка — уберётся уборкой Windows */ }
        }
    }

    private string? PublishFolder(string folderPath, string relative, List<string> warnings)
    {
        string[] files;
        try { files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories); }
        catch (Exception ex)
        {
            warnings.Add($"Инструкция: папку не прочитать — {ex.Message}");
            return null;
        }

        if (files.Length > MaxFilesPerFolder)
        {
            warnings.Add($"Инструкция: в папке {files.Length} файлов — на хостинг не выкладываем " +
                         $"(предел {MaxFilesPerFolder}), проверьте, ту ли папку выбрали");
            return null;
        }

        var failed = 0;
        foreach (var file in files)
        {
            var inside = LabelLinkBuilder.RelativeTo(folderPath, file);
            if (inside is null) continue;
            var key = _settings.KeyFor(relative + "/" + AsPublishedName(inside.Replace('\\', '/')));
            if (PublishFileInsideFolder(file, key, warnings) is null) failed++;
        }

        if (failed > 0) return null;
        return S3Client.PublicUrl(_settings, _settings.KeyFor(relative));
    }

    /// <summary>Один файл из папки сканов. Отдельным методом, чтобы страница с документом Word внутри
    /// папки тоже уехала как PDF: ключ для неё уже посчитан с .pdf, осталось подменить содержимое.</summary>
    private string? PublishFileInsideFolder(string file, string key, List<string> warnings)
    {
        if (!IsWordDocument(file)) return Put(file, key, warnings);

        var converted = ToPdf(file, warnings);
        if (converted is null) return null;
        using var temp = converted;
        return Put(temp.Path, key, warnings);
    }
}
