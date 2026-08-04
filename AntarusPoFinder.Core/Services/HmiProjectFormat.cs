using System;
using System.IO;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что такое «HMI-проект» на диске. У части панелей проект — это ОДИН файл, и его можно
/// копировать как любой другой документ. У FStudio (.fsprj) — нет: сам .fsprj это только точка входа,
/// а модель панели, драйверы и ресурсы лежат РЯДОМ, в той же папке. Скопировав такой файл в одиночку
/// (да ещё и переименовав его в «{версия}_hmi.fsprj», как это делалось со всеми вложениями), мы
/// получали проект, который открывается пустым с руганью «модель HMI не соответствует текущему
/// программному обеспечению» — ровно то, на что жаловались и у нас, и у коллег, при том что исходная
/// папка на машине программиста открывалась нормально.
///
/// Отсюда два правила, которые знает этот класс: (1) выбор .fsprj означает выбор ПАПКИ, в которой он
/// лежит; (2) уже сохранённый одиночный .fsprj — это заведомо испорченный проект, и открывать его
/// молча нельзя.</summary>
public static class HmiProjectFormat
{
    /// <summary>Форматы, у которых проект — папка, а выбранный файл лишь точка входа в неё. Список
    /// намеренно короткий: попадание сюда меняет копирование с «файл» на «вся папка целиком», и
    /// добавлять формат стоит только тогда, когда точно известно, что он так устроен.</summary>
    public static readonly string[] FolderProjectExtensions = { ".fsprj" };

    public static bool IsFolderProjectFile(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && FolderProjectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Папка проекта для выбранного файла-точки входа, или null — копировать надо сам файл.
    ///
    /// null возвращается, когда папка вокруг файла — не папка проекта, а общая свалка: если рядом с
    /// .fsprj лежат файлы прошивки ПЛК, то это папка версии (кто-то положил проект панели прямо в неё),
    /// и утаскивать её целиком в «HMI\» значило бы продублировать туда всю прошивку.</summary>
    public static string? ProjectFolderOf(string? filePath)
    {
        if (!IsFolderProjectFile(filePath) || !SafeFileExists(filePath)) return null;
        var folder = SafeParent(filePath!);
        if (folder is null || !SafeDirExists(folder)) return null;
        return ContainsPlcFirmware(folder) ? null : folder;
    }

    /// <summary>Суффикс папки, в которую программа кладёт проект панели — «{версия}_hmi»
    /// (см. FirmwareAttachmentsService.CopyHmiProject).</summary>
    public const string StoredFolderSuffix = "_hmi";

    /// <summary>Что сказать оператору прямо в момент выбора, или null — выбор нормальный.
    ///
    /// Единственный оставшийся случай, когда проект-папку всё равно придётся копировать одним файлом:
    /// .fsprj лежит не в своей папке, а вповалку с прошивкой ПЛК — забрать такую папку целиком нельзя
    /// (см. <see cref="ProjectFolderOf"/>), а один файл снова даст пустой проект. Молчать тут нельзя:
    /// именно так и появились уже лежащие на диске обрубки — загрузка прошла «успешно», а панель
    /// открылась пустой через неделю у наладчика.
    ///
    /// Ходит на диск — звать по клику.</summary>
    public static string? SelectionWarning(string? path)
    {
        if (!IsFolderProjectFile(path) || !SafeFileExists(path)) return null;
        var folder = SafeParent(path!);
        if (folder is null || !SafeDirExists(folder) || !ContainsPlcFirmware(folder)) return null;
        return "Проект панели выбран из папки, где лежит и прошивка ПЛК. Проект этого формата работает " +
               "только вместе с соседними файлами (модель панели, драйверы), но забрать такую папку " +
               "целиком нельзя — вместе с ней уехала бы и прошивка.\n\n" +
               "Сложите проект панели в отдельную папку и выберите её — иначе среда откроет его пустым " +
               "(«модель HMI не соответствует текущему программному обеспечению»).";
    }

    /// <summary>Сохранённый проект лежит без своего окружения — открывать его бессмысленно. Признак:
    /// файл формата «проект-папка» лежит в папке, где нет ровно того, без чего он не работает, —
    /// ни одного файла другого формата и ни одной подпапки-окружения (драйверы, ресурсы).
    ///
    /// Соседями по несчастью не считаются проекты ДРУГИХ версий: общая папка «HMI» контроллера — это
    /// как раз то место, куда старый код складывал такие одиночные файлы, и рядом с нашим там лежат и
    /// «2.1.040_hmi.fsprj» (такой же обрубок), и «2.1.041_hmi\» (версия, загруженная уже целиком).
    /// Не отсеяв их, мы бы молча открывали пустой проект ровно в том случае, ради которого проверка и
    /// писалась.
    ///
    /// Ходит на диск (в т.ч. сетевой) — звать по клику, не из отрисовки.</summary>
    public static bool LooksStrippedOfCompanions(string? path)
    {
        if (!IsFolderProjectFile(path) || !SafeFileExists(path)) return false;
        var folder = SafeParent(path!);
        if (folder is null) return false;
        try
        {
            if (!Directory.EnumerateFiles(folder).All(IsFolderProjectFile)) return false;
            return Directory.EnumerateDirectories(folder).All(IsStoredProjectFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не смогли посмотреть — значит и утверждать, что проект испорчен, не можем.
            return false;
        }
    }

    /// <summary>Проект панели, лежащий на диске обрубком, — по любому из двух признаков: либо имя
    /// файла целиком наше («{версия}_hmi.fsprj»), либо рядом с ним нет ничего, кроме таких же
    /// одиночных проектов.
    ///
    /// Одного второго признака мало. Общая папка «HMI» контроллера — это не только наши обрубки: у
    /// кого-то там же лежит документ или карта соседней версии, и от одного постороннего файла
    /// проверка замолкала, хотя проект открылся бы ровно так же пустым. Имя же файла задавала сама
    /// программа (см. <see cref="IsOurSingleFileCopy"/>) — по нему обрубок виден независимо от соседей.
    ///
    /// Файл ВНУТРИ нашей папки проекта под этот признак не попадает, как бы он ни назывался: раз мы
    /// забрали папку целиком, окружение рядом с ним есть.
    ///
    /// Ходит на диск — звать по клику.</summary>
    public static bool IsStrippedCopy(string? path, string versionRaw)
    {
        if (!IsFolderProjectFile(path) || !SafeFileExists(path)) return false;
        var folder = SafeParent(path!);
        var insideOurProjectFolder = folder is not null && IsStoredProjectFolder(folder);
        if (IsOurSingleFileCopy(path, versionRaw) && !insideOurProjectFolder) return true;
        return LooksStrippedOfCompanions(path);
    }

    /// <summary>Что программа ВИДИТ рядом с файлом — строкой для показа оператору. Нужно ровно там,
    /// где она утверждает «проект лежит без сопутствующих файлов»: у оператора рядом с ОРИГИНАЛОМ на
    /// его машине всё на месте, и без этой строки предупреждение выглядит враньём — непонятно, что
    /// речь про другую папку (нашу копию на сетевом диске). Показав путь и содержимое, спор
    /// заканчивается за секунду.
    ///
    /// Ходит на диск — звать по клику.</summary>
    public static string Neighbourhood(string? path, int maxNames = 8)
    {
        var folder = string.IsNullOrWhiteSpace(path) ? null : SafeParent(path!);
        if (folder is null || !SafeDirExists(folder)) return "папку прочитать не удалось";
        try
        {
            var names = Directory.EnumerateFileSystemEntries(folder)
                .Where(e => !string.Equals(e, path, StringComparison.OrdinalIgnoreCase))
                .Select(e => Directory.Exists(e) ? Path.GetFileName(e.TrimEnd(Path.DirectorySeparatorChar)) + "\\" : Path.GetFileName(e))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(maxNames + 1)
                .ToList();
            if (names.Count == 0) return "кроме него — ничего";
            return names.Count > maxNames
                ? string.Join(", ", names.Take(maxNames)) + " и другие"
                : string.Join(", ", names);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "папку прочитать не удалось";
        }
    }

    /// <summary>Имя, под которым программа кладёт проект-папку ОДНИМ файлом — то, что она делала до
    /// исправления. Знать его надо, чтобы такой обрубок снести, когда на его место лёг нормальный
    /// проект-папка: имя целиком наше, пользовательский файл так называться не может.</summary>
    public static bool IsOurSingleFileCopy(string? path, string versionRaw)
    {
        if (!IsFolderProjectFile(path) || string.IsNullOrWhiteSpace(versionRaw)) return false;
        var name = Path.GetFileNameWithoutExtension(path!);
        return string.Equals(name, versionRaw + StoredFolderSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStoredProjectFolder(string folder) =>
        Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar))
            .EndsWith(StoredFolderSuffix, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsPlcFirmware(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Any(f => PlcOpenResolver.PlcExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
                          || Path.GetExtension(f).Equals(".lfs", StringComparison.OrdinalIgnoreCase)
                          || Path.GetExtension(f).Equals(".psl", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Папку не прочитать — считаем её обычной папкой проекта: копирование всё равно упадёт
            // с внятной ошибкой, а вот тихо скопировать один файл (и снова получить пустой проект)
            // хуже.
            return false;
        }
    }

    private static string? SafeParent(string path)
    {
        try { return Path.GetDirectoryName(path); }
        catch (Exception) { return null; }
    }

    private static bool SafeFileExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(path); }
        catch (Exception) { return false; }
    }

    private static bool SafeDirExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Directory.Exists(path); }
        catch (Exception) { return false; }
    }
}
