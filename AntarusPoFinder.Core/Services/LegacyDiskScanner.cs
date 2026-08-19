using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что нашлось на старом диске и куда это, похоже, относится. Все «угаданные» поля — только
/// предложение: решает человек, поэтому пустое значение здесь нормально и означает «не понял».</summary>
/// <param name="RelativePath">Путь от выбранной папки — то, что видит оператор в таблице.</param>
/// <param name="InArchiveFolder">Файл лежит в папке «Архив» (или подобной) — на старом диске так
/// помечали заведомо устаревшее. Не повод не показывать, повод не отмечать по умолчанию.</param>
public sealed record LegacyFinding(
    string FullPath,
    string RelativePath,
    long Size,
    DateTime Modified,
    bool IsArchive,
    bool InArchiveFolder,
    string GroupName,
    string SubtypeName,
    string ControllerName,
    string VersionHint,
    string RequestNum,
    string CabinetSn,
    bool LooksLikeDocument)
{
    /// <summary>Узнали всё, что нужно для переноса: тип шкафа, подтип и контроллер. Версию
    /// программа считает сама (следующий свободный номер), поэтому в это условие она не входит.</summary>
    public bool FullyRecognized => GroupName.Length > 0 && SubtypeName.Length > 0 && ControllerName.Length > 0;

    /// <summary>Стоит ли отмечать эту строку по умолчанию: узнали всё, лежит не в «Архиве» и не
    /// похоже на документ. Всё остальное оператор отмечает сам — это и есть «сам выбираю, что куда».</summary>
    public bool WorthTakingByDefault => FullyRecognized && !InArchiveFolder && !LooksLikeDocument;
}

/// <summary>Справочник, по которому узнаются имена в путях старого диска. Передаётся снаружи, из БД:
/// у каждой конторы свой набор шкафов, и зашивать его в код нельзя.</summary>
/// <param name="Subtypes">Подтипы с именем ТИПА, к которому относятся: одно и то же имя подтипа
/// («2.0», «ПИ») встречается у нескольких типов, и без типа выбрать не из чего.</param>
public sealed record LegacyCatalog(
    IReadOnlyList<string> Groups,
    IReadOnlyList<(string GroupName, string SubtypeName)> Subtypes,
    IReadOnlyList<string> Controllers);

/// <summary>Разбор «дофайндеровского» диска: обход выбранной папки, поиск прошивок и попытка понять
/// из пути и имени файла, какому шкафу они принадлежат.
///
/// <b>Зачем.</b> Всё, что копилось до Финдера, лежит как сложилось: «1. ПЖ\1.1. Антарус 2.0\SMH4\
/// пж_smh4_v4.31.16.pass.psl.zip». Правила раскладки Финдера жёсткие, и разложить такое одной кнопкой
/// нельзя — слишком много исключений. Поэтому здесь только ЧТЕНИЕ и ПРЕДПОЛОЖЕНИЯ: что нашли, на что
/// это похоже. Решение «куда именно и какими файлами» принимает человек в окне разбора, а сам перенос
/// делает обычная загрузка (FirmwareUploadService) — чтобы у перенесённого были ровно те же имя,
/// раскладка и запись в базе, что и у залитого руками.
///
/// Прообраз — tools/legacy-disk-inventory.ps1 (только отчёт в CSV): им уже смотрели, что на диске
/// вообще есть. Правила распознавания те же, но справочник имён берётся из базы, а не из списка в
/// скрипте.</summary>
public static class LegacyDiskScanner
{
    /// <summary>Расширения файлов прошивок и проектов панелей.</summary>
    public static readonly string[] FirmwareExtensions =
        { ".psl", ".lfs", ".kpr", ".kpj", ".dpj", ".fsprj", ".bk0" };

    /// <summary>Архивы: на старом диске прошивку почти всегда клали архивом («…pass.psl.7z»).
    /// Внутрь заглядывает уже окно разбора — распаковкой во временную папку.</summary>
    public static readonly string[] ArchiveExtensions = { ".zip", ".7z", ".rar" };

    /// <summary>Папки, которые на старом диске означают «заведомо устаревшее».</summary>
    private static readonly string[] ArchiveFolderNames = { "Архив", "Архивы", "Old", "Старое" };

    /// <summary>Как называли то же самое до Финдера. Ключ ищется в сегменте пути целиком или как
    /// часть слова, значение — имя подтипа в справочнике.
    ///
    /// Список короткий намеренно: он покрывает ровно то, что реально встретилось на диске
    /// («1.1. Антарус 2.0», «1.2. F-Drive», «1.3. ПЖ-ХП»), а не пытается угадать всё на свете.
    /// Не угадали — оператор выберет подтип руками, для этого окно разбора и сделано.</summary>
    private static readonly (string Legacy, string Subtype)[] SubtypeAliases =
    {
        ("антарус 2.0", "2.0"),
        ("f-drive", "FD"),
        ("fdrive", "FD"),
        ("ф-драйв", "FD"),
    };

    /// <summary>Точка после номера — обычное дело в старых именах («…v4.31.16.pass.psl»), поэтому
    /// «дальше не цифра» проверяется и через точку: иначе номер не находился бы вовсе.</summary>
    private static readonly Regex VersionPattern =
        new(@"(?<![\d.])(\d{1,5}(?:\.\d{1,5}){2,4})(?!\d)(?!\.\d)", RegexOptions.Compiled);

    /// <summary>Слова, по которым видно, что архив — это документ, а не программа: «Инструкция.zip»
    /// лежит на старом диске рядом с прошивками и по расширению от них не отличается.</summary>
    private static readonly string[] DocumentWords =
        { "инструкц", "руководств", "карта", "карты", "схем", "паспорт", "описание", "modbus" };

    private static readonly Regex RequestInName = new(@"_\((\d+)\)", RegexOptions.Compiled);
    private static readonly Regex SnInName = new(@"_SN(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Номер заявки первым числом в имени папки: «13948 (7526) 3 уровня», «40289»,
    /// «8854(6958) Кукуевицкого». Второе число в скобках — заводской номер шкафа.</summary>
    private static readonly Regex RequestInFolder = new(@"^(\d{3,6})\s*(?:\((\d{3,6})\))?", RegexOptions.Compiled);

    /// <summary>Обход папки. Ошибки чтения отдельных папок (нет прав, оборвалась сеть) не валят
    /// разбор: недоступная ветка просто не попадает в список.</summary>
    public static List<LegacyFinding> Scan(string root, LegacyCatalog catalog, int maxFiles = 20000)
    {
        var result = new List<LegacyFinding>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return result;

        var known = new HashSet<string>(FirmwareExtensions.Concat(ArchiveExtensions), StringComparer.OrdinalIgnoreCase);

        void Walk(string dir, bool inArchiveFolder)
        {
            IEnumerable<string> files;
            IEnumerable<string> dirs;
            try
            {
                files = Directory.EnumerateFiles(dir).ToList();
                dirs = Directory.EnumerateDirectories(dir).ToList();
            }
            catch (Exception) { return; }

            foreach (var file in files)
            {
                if (result.Count >= maxFiles) return;
                var ext = Path.GetExtension(file);
                if (!known.Contains(ext)) continue;
                if (JunkFiles.IsJunk(file)) continue;
                result.Add(Describe(file, root, catalog, inArchiveFolder));
            }

            foreach (var sub in dirs)
            {
                if (result.Count >= maxFiles) return;
                var name = Path.GetFileName(sub);
                Walk(sub, inArchiveFolder || ArchiveFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase));
            }
        }

        Walk(root, inArchiveFolder: false);
        return result
            .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Что можно сказать про один найденный файл. Публичный — им же пользуется окно разбора,
    /// когда оператор добавляет файл руками, минуя обход.</summary>
    public static LegacyFinding Describe(string fullPath, string root, LegacyCatalog catalog, bool inArchiveFolder = false)
    {
        var relative = SafeRelative(fullPath, root);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        var fileName = Path.GetFileName(fullPath);

        var group = GuessGroup(segments, catalog);
        var subtype = GuessSubtype(segments, catalog, group);
        // Тип шкафа в старом пути называли не всегда («2. КПЧ\…» вместо «ПЖ\КПЧ\…»), а вот подтип
        // назвали. Если такой подтип есть ровно у одного типа — вопрос снят. У нескольких — молчим:
        // выбрать за оператора наугад хуже, чем оставить поле пустым.
        if (group.Length == 0 && subtype.Length > 0)
        {
            var owners = catalog.Subtypes
                .Where(s => string.Equals(s.SubtypeName, subtype, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.GroupName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (owners.Count == 1) group = owners[0];
        }
        var controller = GuessController(segments, catalog);
        var (request, sn) = GuessOpcMarkers(segments, fileName);

        long size = -1;
        var modified = DateTime.MinValue;
        try
        {
            var info = new FileInfo(fullPath);
            size = info.Length;
            modified = info.LastWriteTime;
        }
        catch (Exception) { /* исчез или недоступен — покажем без размера и даты */ }

        return new LegacyFinding(
            fullPath, relative, size, modified,
            IsArchive: ArchiveExtensions.Contains(Path.GetExtension(fullPath), StringComparer.OrdinalIgnoreCase),
            InArchiveFolder: inArchiveFolder,
            GroupName: group,
            SubtypeName: subtype,
            ControllerName: controller,
            VersionHint: GuessVersion(fileName),
            RequestNum: request,
            CabinetSn: sn,
            LooksLikeDocument: LooksLikeDocument(fileName));
    }

    // ── распознавание ────────────────────────────────────────────────────────

    /// <summary>Тип шкафа ищется по всем сегментам пути, начиная с ближайшего к файлу: «ПО\ПЖ\…» и
    /// «1. ПЖ\…» дают одно и то же. Побеждает самое длинное совпавшее имя — иначе «НГР-ВЗУ» считался
    /// бы НГР, хотя это ВЗУ.</summary>
    private static string GuessGroup(IReadOnlyList<string> segments, LegacyCatalog catalog)
    {
        for (var i = segments.Count - 1; i >= 0; i--)
        {
            var best = catalog.Groups
                .Where(g => g.Length > 0 && ContainsWord(segments[i], g))
                .OrderByDescending(g => g.Length)
                .FirstOrDefault();
            if (best is not null) return best;
        }
        return "";
    }

    private static string GuessSubtype(IReadOnlyList<string> segments, LegacyCatalog catalog, string groupName)
    {
        var candidates = catalog.Subtypes
            .Where(s => groupName.Length == 0 || string.Equals(s.GroupName, groupName, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.SubtypeName)
            .Where(n => n.Length > 0 && n != "—")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = segments.Count - 1; i >= 0; i--)
        {
            var segment = segments[i];

            var alias = SubtypeAliases
                .Where(a => segment.Contains(a.Legacy, StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Subtype)
                .FirstOrDefault(a => candidates.Contains(a, StringComparer.OrdinalIgnoreCase));
            if (alias is not null) return candidates.First(c => string.Equals(c, alias, StringComparison.OrdinalIgnoreCase));

            var best = candidates
                .Where(n => ContainsWord(segment, n))
                .OrderByDescending(n => n.Length)
                .FirstOrDefault();
            if (best is not null) return best;
        }

        // Единственный подтип у типа (обычно это «—») — выбирать не из чего, и молчать незачем.
        var only = catalog.Subtypes
            .Where(s => string.Equals(s.GroupName, groupName, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.SubtypeName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return only.Count == 1 ? only[0] : "";
    }

    /// <summary>Контроллер ищется и в пути, и в имени файла («пж_smh4_v4.31.16.psl»). Самое длинное
    /// совпадение — иначе PIXEL2 везде читался бы как PIXEL.</summary>
    private static string GuessController(IReadOnlyList<string> segments, LegacyCatalog catalog)
    {
        for (var i = segments.Count - 1; i >= 0; i--)
        {
            var best = catalog.Controllers
                .Where(c => c.Length > 0 && ContainsWord(segments[i], c))
                .OrderByDescending(c => c.Length)
                .FirstOrDefault();
            if (best is not null) return best;
        }
        return "";
    }

    /// <summary>Номер версии, как он записан у старого файла («v4.31.16», «4.31.16.7084»). Это НЕ
    /// номер версии Финдера: он строится по иерархии и берётся следующим свободным. Показывается
    /// оператору как подсказка «что это была за сборка» и уходит в описание переноса.</summary>
    public static string GuessVersion(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var match = VersionPattern.Match(name);
        return match.Success ? match.Groups[1].Value : "";
    }

    private static (string RequestNum, string CabinetSn) GuessOpcMarkers(IReadOnlyList<string> segments, string fileName)
    {
        var request = RequestInName.Match(fileName);
        var sn = SnInName.Match(fileName);
        if (request.Success || sn.Success)
            return (request.Success ? request.Groups[1].Value : "", sn.Success ? sn.Groups[1].Value : "");

        // Заявка часто зашита только в имя папки: «1.4. ОПЦ\13948 (7526) 3 уровня (АНУ, ВУ, АВУ)».
        // Смотрим от ближайшей к файлу папки к корню и берём первую подходящую.
        for (var i = segments.Count - 2; i >= 0; i--)
        {
            // Папка-год («…\ОПЦ\SMH5\…») — не заявка. Только целиком: «2025 наполнение бака»
            // это уже заявка, а не год.
            if (IsYearFolder(segments[i])) continue;

            var folder = RequestInFolder.Match(segments[i]);
            if (folder.Success)
                return (folder.Groups[1].Value, folder.Groups[2].Success ? folder.Groups[2].Value : "");
        }
        return ("", "");
    }

    private static bool IsYearFolder(string segment)
    {
        var name = segment.Trim();
        return name.Length == 4 && int.TryParse(name, out var year) && year is >= 1990 and <= 2100;
    }

    private static bool LooksLikeDocument(string fileName) =>
        DocumentWords.Any(w => fileName.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Имя встречается в тексте как отдельное слово: «SMH4» находится в «ПЖ-ХП_SMH4» и в
    /// «1.1. Антарус 2.0», но «ПИ» не находится в «ПИКСЕЛЬ». Границей считается всё, что не буква и не
    /// цифра, — точки и дефисы в старых именах разделяют слова, а не склеивают их.</summary>
    private static bool ContainsWord(string text, string word)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(word)) return false;

        var index = 0;
        while (true)
        {
            index = text.IndexOf(word, index, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;

            var before = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var afterAt = index + word.Length;
            var after = afterAt >= text.Length || !char.IsLetterOrDigit(text[afterAt]);
            if (before && after) return true;
            index += 1;
        }
    }

    private static string SafeRelative(string fullPath, string root)
    {
        try { return Path.GetRelativePath(root, fullPath); }
        catch (Exception) { return fullPath; }
    }

    /// <summary>Справочник из базы — один и тот же для обхода и для выпадающих списков в окне.</summary>
    public static LegacyCatalog CatalogFrom(IEnumerable<EquipmentGroup> groups,
        IEnumerable<(string GroupName, EquipmentSubType Subtype)> subtypes,
        IEnumerable<string> controllers) =>
        new(groups.Select(g => g.Name).ToList(),
            subtypes.Select(s => (s.GroupName, s.Subtype.Name)).ToList(),
            controllers.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
}
