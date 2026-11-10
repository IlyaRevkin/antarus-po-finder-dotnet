using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Одна найденная дыра: что не так, где и в чём именно.</summary>
/// <param name="Kind">Порода (см. <see cref="DiskAudit.Kind"/>) — по ней дыры группируются.</param>
/// <param name="Path">Папка версии ОТНОСИТЕЛЬНО корня диска, через косую черту.</param>
/// <param name="Detail">Подробность для человека: чего именно не хватает.</param>
public sealed record DiskSnapshotIssue(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("detail")] string Detail);

/// <summary>Сводка по слепку: цифры, ради которых не надо читать весь список.</summary>
public sealed record DiskSnapshotSummary(
    [property: JsonPropertyName("versionsOnDisk")] int VersionsOnDisk,
    [property: JsonPropertyName("versionsInDb")] int VersionsInDb,
    [property: JsonPropertyName("newLayout")] int NewLayout,
    [property: JsonPropertyName("compatLayout")] int CompatLayout,
    [property: JsonPropertyName("folderCount")] int FolderCount,
    [property: JsonPropertyName("bytes")] long Bytes,
    /// <summary>Сколько версий имеют ПУСТУЮ папку раскладки, по каждой папке отдельно. Именно
    /// это число, а не список: у перестроенной версии все пять папок заводятся всегда и пустая
    /// «Карта Modbus» — законное состояние, а вот «пустых HMI 1400 из 1500» — уже разговор.</summary>
    [property: JsonPropertyName("emptySlots")] IReadOnlyDictionary<string, int> EmptySlots);

/// <summary>РАЗБОР СЛЕПКА: где база и диск разошлись.
///
/// <b>Зачем.</b> 06.11.2026 приехали диагностические записи по жалобе «не найден HMI», и они
/// показали не ошибку поиска, а дыру в данных: папка <c>HMI\</c> у версии ЕСТЬ, а файлов в ней НЕТ.
/// Такие дыры до сих пор находились по одной — когда на них кто-нибудь наступал. Этот класс находит
/// их СПИСКОМ, по слепку, не имея доступа к диску.
///
/// <b>Почему отдельный класс и почему без файловой системы.</b> Весь разбор идёт по уже снятой
/// описи (пути, размеры, даты) и по строкам базы. Ни одного обращения к диску здесь нет намеренно:
/// иначе проверить правила можно было бы только с настоящей сетевой шарой, то есть никак. Имена
/// папок раскладки берутся из <see cref="VersionLayout"/> — единственного места, которое их знает.
///
/// <b>Чего здесь нет.</b> Выводов и действий. Класс отвечает на вопрос «что не сходится», а не
/// «что с этим делать»: решение всегда за человеком, и слепок ничем не управляет.</summary>
public static class DiskAudit
{
    /// <summary>Породы дыр. Строками, а не перечислением: список читают глазами в json'е, и
    /// «firmware-missing» там понятнее, чем цифра 0.</summary>
    public static class Kind
    {
        /// <summary>У версии нет НИ ОДНОГО файла прошивки — ни в «Прошивка\», ни (у неперестроенной)
        /// в самой папке версии. Папка есть, CHANGELOG есть, а скачивать нечего.</summary>
        public const string FirmwareMissing = "firmware-missing";

        /// <summary>База обещает документ (в записи версии заполнен путь к инструкции, карте или
        /// проекту панели), а файлов нет ни в папке версии, ни в общей папке контроллера. Ровно эта
        /// порода и стоит за жалобой «не найден HMI».</summary>
        public const string DocMissing = "doc-missing";

        /// <summary>Запись в базе есть, папки версии на диске нет вовсе.</summary>
        public const string DbWithoutDisk = "db-without-disk";

        /// <summary>На диске лежит папка версии (её выдаёт собственная «Прошивка\»), а в базе про неё
        /// никто не знает — такую версию поиском не найти.</summary>
        public const string DiskWithoutDb = "disk-without-db";

        /// <summary>У версии нет журнала изменений. Не беда, а наблюдение: по CHANGELOG досмотр
        /// диска восстанавливает описание и теги, и без него версия приезжает к коллегам немой.</summary>
        public const string ChangelogMissing = "changelog-missing";
    }

    /// <summary>Потолок числа дыр в слепке. Список на десятки тысяч строк не читает никто, а если
    /// дыр СТОЛЬКО — дело не в отдельных версиях, и это видно по первой тысяче. Сверх потолка
    /// добавляется честная строка «показано не всё».</summary>
    public const int MaxIssues = 2000;

    /// <summary>Разобрать слепок. <paramref name="folders"/> — ВСЕ папки дерева, включая пустые:
    /// без них пустую «HMI\» не увидеть вовсе (в описи файлов её попросту нет), а именно она и была
    /// причиной жалобы.</summary>
    public static IReadOnlyList<DiskSnapshotIssue> Find(
        IEnumerable<DiskSnapshotEntry> files,
        IEnumerable<string> folders,
        IEnumerable<DiskSnapshotDbRow> db)
    {
        var tree = new Tree(files, folders);
        var rows = db?.ToList() ?? new List<DiskSnapshotDbRow>();

        var issues = new List<DiskSnapshotIssue>();
        var knownVersionFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows.OrderBy(r => r.DiskPath, StringComparer.Ordinal))
        {
            var version = DiskSnapshot.NormalizePath(row.DiskPath);
            if (version.Length == 0) continue;
            knownVersionFolders.Add(version);

            // Архивную версию на диске не ищем: её папку могли унести законно, и «нет на диске» для
            // неё — не новость, а нормальный конец жизни.
            if (row.Archived) continue;

            if (!tree.HasFolder(version))
            {
                issues.Add(new DiskSnapshotIssue(Kind.DbWithoutDisk, version,
                    $"{Label(row)}: папки версии на диске нет"));
                continue;
            }

            if (!HasFirmware(tree, version))
                issues.Add(new DiskSnapshotIssue(Kind.FirmwareMissing, version,
                    $"{Label(row)}: в папке версии нет ни одного файла прошивки"));

            foreach (var (slot, promised) in PromisedDocs(row))
            {
                if (promised.Length == 0) continue;
                if (HasDoc(tree, version, slot)) continue;
                issues.Add(new DiskSnapshotIssue(Kind.DocMissing, version,
                    $"{Label(row)}: база обещает «{slot}» ({promised}), а файлов нет ни у версии, ни у контроллера"));
            }

            if (!tree.HasFile(version + "/" + ChangelogFile.FileName))
                issues.Add(new DiskSnapshotIssue(Kind.ChangelogMissing, version,
                    $"{Label(row)}: нет {ChangelogFile.FileName}"));
        }

        foreach (var version in tree.VersionFoldersOnDisk())
        {
            if (knownVersionFolders.Contains(version)) continue;
            issues.Add(new DiskSnapshotIssue(Kind.DiskWithoutDb, version,
                "на диске есть папка версии с «" + VersionLayout.FirmwareFolderName + "», в базе записи нет"));
        }

        if (issues.Count <= MaxIssues) return issues;
        var kept = issues.Take(MaxIssues).ToList();
        kept.Add(new DiskSnapshotIssue("truncated", "",
            $"показаны первые {MaxIssues} из {issues.Count}: дальше смотреть надо не по одной"));
        return kept;
    }

    /// <summary>Сводка по тому же слепку. Пустые папки раскладки считаются ЗДЕСЬ, а не попадают в
    /// список дыр: у перестроенной версии все пять папок заводятся всегда и пустыми (так и просили —
    /// человек должен видеть, куда что класть), поэтому каждая пустая папка отдельной строкой
    /// утопила бы список. Цифра же сразу показывает перекос.</summary>
    public static DiskSnapshotSummary Summarize(
        IEnumerable<DiskSnapshotEntry> files,
        IEnumerable<string> folders,
        IEnumerable<DiskSnapshotDbRow> db)
    {
        var entries = files?.ToList() ?? new List<DiskSnapshotEntry>();
        var folderList = (folders ?? Array.Empty<string>())
            .Select(DiskSnapshot.NormalizePath).Where(f => f.Length > 0).ToList();
        var tree = new Tree(entries, folderList);
        var rows = db?.ToList() ?? new List<DiskSnapshotDbRow>();

        var versions = new HashSet<string>(tree.VersionFoldersOnDisk(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var path = DiskSnapshot.NormalizePath(row.DiskPath);
            if (path.Length > 0 && tree.HasFolder(path)) versions.Add(path);
        }

        var empty = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var slot in VersionLayout.SlotFolderNames.Concat(new[] { VersionLayout.FirmwareFolderName }))
            empty[slot] = versions.Count(v => tree.HasFolder(v + "/" + slot) && !tree.HasPayloadUnder(v + "/" + slot));

        var newLayout = versions.Count(v => tree.HasFolder(v + "/" + VersionLayout.FirmwareFolderName));

        return new DiskSnapshotSummary(
            versions.Count,
            rows.Count,
            newLayout,
            versions.Count - newLayout,
            folderList.Count,
            entries.Sum(f => f.Size),
            empty);
    }

    /// <summary>Сколько версий показывать в списке «документа нет нигде» по каждой папке. Список
    /// должен отвечать на вопрос «какие именно», а не заменять собой выгрузку всей базы: если таких
    /// версий больше, это видно по цифре в сводке.</summary>
    public const int MaxNowherePerSlot = 500;

    /// <summary>ДОКУМЕНТА НЕТ НИГДЕ: папка раскладки у версии заведена (значит, документ сюда
    /// полагался), файлов в ней нет, и в общей папке контроллера тоже нет.
    ///
    /// <b>Почему это не попало в список дыр, а вынесено отдельно.</b> Дыра — это расхождение: база
    /// обещает, диск не держит. Здесь расхождения нет: база молчит, и диск пуст, — формально всё
    /// согласовано. Но жалоба «не найден HMI» выглядела именно так: папка <c>HMI\</c> у версии есть,
    /// файлов в ней нет, у контроллера тоже, в записи пусто — и ответ «панели нет» был честным, а
    /// беда всё равно настоящей. Такое надо видеть СПИСКОМ, но не вперемешку с расхождениями:
    /// законных случаев («у этого шкафа панели и не бывает») здесь сколько угодно.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> DocsNowhere(
        IEnumerable<DiskSnapshotEntry> files,
        IEnumerable<string> folders,
        IEnumerable<DiskSnapshotDbRow> db)
    {
        var tree = new Tree(files, folders);
        var versions = new SortedSet<string>(tree.VersionFoldersOnDisk(), StringComparer.Ordinal);
        foreach (var row in db ?? Array.Empty<DiskSnapshotDbRow>())
        {
            var path = DiskSnapshot.NormalizePath(row.DiskPath);
            if (path.Length > 0 && tree.HasFolder(path)) versions.Add(path);
        }

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var slot in VersionLayout.SlotFolderNames)
        {
            result[slot] = versions
                .Where(v => tree.HasFolder(v + "/" + slot) && !HasDoc(tree, v, slot))
                .Take(MaxNowherePerSlot)
                .ToList();
        }
        return result;
    }

    /// <summary>Что база обещает в каждой из папок документов. Четыре поля записи — ровно те, что
    /// заполняет загрузка; пустое поле означает «документа и не обещали», и спрашивать с диска
    /// за него нельзя.</summary>
    private static IEnumerable<(string Slot, string Promised)> PromisedDocs(DiskSnapshotDbRow row)
    {
        yield return (HierarchyFolders.Instructions, row.InstructionsPath ?? "");
        yield return (HierarchyFolders.Modbus, row.ModbusMapPath ?? "");
        yield return (HierarchyFolders.IoMap, row.IoMapPath ?? "");
        yield return (HierarchyFolders.Hmi, row.HmiPath ?? "");
    }

    /// <summary>Есть ли у версии хоть один файл прошивки. Смотрим обе раскладки сразу, а не
    /// «или-или»: у перестроенной версии файлы в «Прошивка\» (вместе с папками исполнений внутри
    /// неё), у прежней — прямо в папке версии, а во время прерванного переезда — и там, и там.
    /// CHANGELOG и служебный мусор файлом прошивки не считаются — иначе версия с одним только
    /// журналом выглядела бы полной.</summary>
    private static bool HasFirmware(Tree tree, string versionFolder)
    {
        var firmware = versionFolder + "/" + VersionLayout.FirmwareFolderName;
        if (tree.HasPayloadUnder(firmware)) return true;

        // В корне версии папки раскладки не считаются: инструкция — не прошивка.
        return tree.DirectFiles(versionFolder).Any(path =>
            !IsNoise(path) &&
            !path.EndsWith("/" + ChangelogFile.FileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Лежит ли документ там, откуда его читают: своя папка версии ИЛИ общая папка
    /// контроллера. Порядок тот же, что у <see cref="VersionLayout.SlotBestReadFolder"/>, и причина
    /// та же: перестройка диска не копирует документы контроллера в каждую версию, поэтому у
    /// переехавшей версии своя «Инструкция» обычно пуста — а документ при этом на месте.</summary>
    private static bool HasDoc(Tree tree, string versionFolder, string slot)
    {
        if (tree.HasPayloadUnder(versionFolder + "/" + slot)) return true;

        var controller = VersionLayout.ControllerFolderOf(versionFolder, tree.HasFolder);
        return controller is not null && tree.HasPayloadUnder(controller + "/" + slot);
    }

    /// <summary>Файл, от которого папка не становится непустой: ярлыки, мусор проводника и метка
    /// хозяина папки «Инструкция». Тот же набор, что у <see cref="VersionLayout"/>: разъехавшись,
    /// эти два списка начали бы давать разный ответ на один вопрос — и слепок говорил бы «документ
    /// есть» там, где программа говорит «нет».</summary>
    private static bool IsNoise(string path) =>
        DocFileResolver.IsShortcut(path) || JunkFiles.IsJunk(path) || InstructionOwnership.IsMarker(path);

    private static string Label(DiskSnapshotDbRow row)
    {
        var name = string.Join(" / ", new[] { row.Subtype, row.Controller, row.Version }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        return name.Length == 0 ? row.SyncId : name;
    }

    /// <summary>Дерево диска, собранное из описи один раз. Отдельным типом, потому что каждый из
    /// вопросов выше («есть ли папка», «есть ли под ней файлы») задаётся тысячи раз, и считать его
    /// перебором всей описи значило бы квадрат от числа версий.</summary>
    private sealed class Tree
    {
        private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _direct = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Папки, под которыми (на любой глубине) лежит хоть один НЕ служебный файл.</summary>
        private readonly HashSet<string> _payload = new(StringComparer.OrdinalIgnoreCase);

        public Tree(IEnumerable<DiskSnapshotEntry> files, IEnumerable<string> folders)
        {
            foreach (var folder in folders ?? Array.Empty<string>())
            {
                var normalized = DiskSnapshot.NormalizePath(folder);
                if (normalized.Length > 0) _folders.Add(normalized);
            }

            foreach (var entry in files ?? Array.Empty<DiskSnapshotEntry>())
            {
                var path = DiskSnapshot.NormalizePath(entry.Path);
                if (path.Length == 0) continue;
                _files.Add(path);

                var parent = Parent(path);
                if (parent.Length > 0)
                {
                    if (!_direct.TryGetValue(parent, out var list)) _direct[parent] = list = new List<string>();
                    list.Add(path);
                }

                // Папка, в которой лежит файл, на диске существует — даже если обход почему-то не
                // перечислил её отдельно. Опись файлов и опись папок приходят из разных вызовов
                // файловой системы, и верить надо обеим.
                var noise = IsNoise(path);
                for (var dir = parent; dir.Length > 0; dir = Parent(dir))
                {
                    _folders.Add(dir);
                    if (!noise && !_payload.Add(dir)) break;    // выше уже отмечено — и дальше тоже
                }
            }
        }

        public bool HasFolder(string path) => _folders.Contains(DiskSnapshot.NormalizePath(path));

        public bool HasFile(string path) => _files.Contains(DiskSnapshot.NormalizePath(path));

        public bool HasPayloadUnder(string folder) => _payload.Contains(DiskSnapshot.NormalizePath(folder));

        public IReadOnlyList<string> DirectFiles(string folder) =>
            _direct.TryGetValue(DiskSnapshot.NormalizePath(folder), out var list)
                ? list
                : Array.Empty<string>();

        /// <summary>Папки версий, как их видно С ДИСКА: признак — собственная «Прошивка\». Это
        /// единственный надёжный признак без базы: имя папки версии ничем не выделено, а у
        /// неперестроенной версии нет и его.</summary>
        public IEnumerable<string> VersionFoldersOnDisk() =>
            _folders
                .Where(f => f.EndsWith("/" + VersionLayout.FirmwareFolderName, StringComparison.OrdinalIgnoreCase))
                .Select(f => f[..(f.Length - VersionLayout.FirmwareFolderName.Length - 1)])
                .OrderBy(f => f, StringComparer.Ordinal);

        private static string Parent(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash <= 0 ? "" : path[..slash];
        }
    }
}
