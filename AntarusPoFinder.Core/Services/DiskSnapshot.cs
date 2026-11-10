using System.Text.Json;
using System.Text.Json.Serialization;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Один файл на сетевом диске так, как он попадает в слепок: путь ОТНОСИТЕЛЬНО корня,
/// размер и время правки. Содержимого здесь нет и не будет — прошивки это собственность конторы, и
/// в облаке им делать нечего. Имени, размера и даты хватает, чтобы увидеть структуру и заметить
/// неладное.</summary>
public sealed record DiskSnapshotEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("modified")] string Modified);

/// <summary>Строка базы в слепке — ровно те поля, по которым база сверяется с диском. Весь
/// fw_versions сюда не едет: в слепке он нужен не как резервная копия, а как ВТОРОЕ МНЕНИЕ о том,
/// что лежит на диске.</summary>
public sealed record DiskSnapshotDbRow(
    [property: JsonPropertyName("syncId")] string SyncId,
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("subtype")] string Subtype,
    [property: JsonPropertyName("controller")] string Controller,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("diskPath")] string DiskPath,
    [property: JsonPropertyName("isOpc")] bool IsOpc,
    [property: JsonPropertyName("requestNum")] string RequestNum,
    [property: JsonPropertyName("cabinetSn")] string CabinetSn,
    [property: JsonPropertyName("status")] string Status,
    // Что база ОБЕЩАЕТ в папках документов. Без этих четырёх полей слепок не умел отличить
    // «документа и не обещали» (карты Modbus у шкафа просто нет) от «обещали, а на диске пусто» —
    // а это и есть жалоба «не найден HMI» от 06.11.2026: папка HMI\ у версии ЕСТЬ, файлов в ней
    // НЕТ. Пути относительные, как и всё остальное в слепке.
    [property: JsonPropertyName("instructionsPath")] string InstructionsPath,
    [property: JsonPropertyName("modbusMapPath")] string ModbusMapPath,
    [property: JsonPropertyName("ioMapPath")] string IoMapPath,
    [property: JsonPropertyName("hmiPath")] string HmiPath,
    // Состояние МОДЕРАЦИИ. Добавлено по конкретному вопросу: «у коллеги 20 на модерации, а у меня
    // 3». Очередь модерации — это строки с released = 0 (плюс не архивные и не заменённые более
    // свежей), и расхождение между машинами бывает трёх разных пород: решение не доехало, строки
    // нет вовсе, или она скрыта как заменённая. Без этих двух полей в слепке отличить их нельзя,
    // и остаётся только переспрашивать.
    [property: JsonPropertyName("released")] bool Released,
    [property: JsonPropertyName("archived")] bool Archived,
    [property: JsonPropertyName("swVersion")] int SwVersion,
    [property: JsonPropertyName("hwVersion")] int HwVersion,
    [property: JsonPropertyName("execution")] string Execution);

/// <summary>СЛЕПОК ДЕРЕВА ПРОШИВОК — то, что машина с подключённым сетевым диском выкладывает в
/// хранилище, чтобы структуру диска можно было разбирать, НЕ имея к этому диску доступа.
///
/// Зачем. Диск виден только из конторы; тот, кто чинит программу, видит лишь то, что ему перескажут.
/// Между тем почти все наши беды живут в зазоре между двумя картинами мира: база говорит одно, а на
/// диске лежит другое — ОПЦ не в своей папке, обрубок проекта панели, версия без папки «Прошивка»,
/// пропавший CHANGELOG. Увидеть такой зазор можно, только имея обе картины сразу, поэтому в слепке
/// их две: <see cref="Files"/> — что РЕАЛЬНО лежит на диске, <see cref="Db"/> — что об этом ДУМАЕТ
/// база.
///
/// Чего слепок НЕ делает. Он ничего не меняет и ничем не управляет: это снимок на момент времени,
/// а не канал команд. Поэтому <see cref="Machine"/> и <see cref="TakenAt"/> обязательны — рассуждать
/// о диске по недельной давности снимку, не зная об этом, хуже, чем не рассуждать вовсе.</summary>
public sealed record DiskSnapshot(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("machine")] string Machine,
    [property: JsonPropertyName("takenAt")] string TakenAt,
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("subtree")] string Subtree,
    [property: JsonPropertyName("fileCount")] int FileCount,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("files")] IReadOnlyList<DiskSnapshotEntry> Files,
    [property: JsonPropertyName("db")] IReadOnlyList<DiskSnapshotDbRow> Db,
    /// <summary>ВСЕ папки дерева, включая пустые. Казалось бы, лишнее — папки видны по путям файлов;
    /// но ровно наоборот: папка, в которой файлов НЕТ, в описи файлов не оставляет следа вовсе. А
    /// жалоба «не найден HMI» (06.11.2026) оказалась именно такой — папка <c>HMI\</c> есть, файлов в
    /// ней нет. Без этого списка такую дыру по слепку не увидеть.</summary>
    [property: JsonPropertyName("folders")] IReadOnlyList<string> Folders,
    /// <summary>Что не сходится — посчитано на машине, которая диск видит (см. <see cref="DiskAudit"/>).
    /// Считать это можно было бы и при разборе, но тогда каждый читающий считал бы по-своему, а
    /// правила раскладки живут в программе.</summary>
    [property: JsonPropertyName("issues")] IReadOnlyList<DiskSnapshotIssue> Issues,
    [property: JsonPropertyName("summary")] DiskSnapshotSummary Summary,
    /// <summary>По каждой папке документов — версии, у которых папка заведена, а документа нет
    /// НИГДЕ: ни у версии, ни у контроллера. Расхождением это не считается (база его и не обещала),
    /// но выглядела жалоба «не найден HMI» именно так, и видеть такое надо списком — см.
    /// <see cref="DiskAudit.DocsNowhere"/>.</summary>
    [property: JsonPropertyName("docsNowhere")] IReadOnlyDictionary<string, IReadOnlyList<string>> DocsNowhere,
    /// <summary>Версия программы, снявшей слепок: «на диске не хватает папок» и «программа ещё не
    /// умеет их заводить» — разные диагнозы, и отличить их можно только по версии.</summary>
    [property: JsonPropertyName("appVersion")] string AppVersion)
{
    /// <summary>2 — добавлены папки (включая пустые), разбор расхождений и версия программы.
    /// Читающая сторона обязана смотреть на это число: в слепке схемы 1 пустых папок нет вовсе, и
    /// «дыр не найдено» там означает «их не искали».</summary>
    public const int CurrentSchema = 2;

    /// <summary>Потолок числа файлов в одном слепке. Дерево ПО конторы — тысячи файлов, но на диске
    /// рядом случается и чужое добро; слепок на сотню мегабайт никто читать не станет, а платит за
    /// место хостинг. Упёрлись — честно ставим <see cref="Truncated"/>, а не делаем вид, что это
    /// весь диск.</summary>
    public const int MaxFiles = 60_000;

    /// <summary>Собирает слепок. Обхода диска здесь нет НАМЕРЕННО: файлы и строки базы передаются
    /// готовыми — иначе проверить сборку можно было бы только с настоящим сетевым диском, то есть
    /// никак.
    ///
    /// Порядок файлов строго по пути: два слепка подряд должны отличаться ровно тем, чем отличается
    /// диск, а не порядком обхода файловой системы.</summary>
    /// <param name="folders">Все папки дерева, включая пустые (см. <see cref="Folders"/>). Не
    /// передали — слепок получится схемы 2 с пустым списком папок, и разбор честно не найдёт
    /// пустых папок, потому что ему их не показали.</param>
    /// <param name="appVersion">Версия снявшей слепок программы.</param>
    public static DiskSnapshot Build(
        string machine, DateTime takenAt, string root,
        IEnumerable<(string RelativePath, long Size, DateTime Modified)> files,
        IEnumerable<FwVersionRecord> rows,
        int maxFiles = MaxFiles,
        IEnumerable<string>? folders = null,
        string appVersion = "")
    {
        var all = files
            .Select(f => new DiskSnapshotEntry(NormalizePath(f.RelativePath), f.Size, Stamp(f.Modified)))
            .Where(f => f.Path.Length > 0)
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();

        var truncated = all.Count > maxFiles;
        var kept = truncated ? all.Take(maxFiles).ToList() : all;

        var db = rows
            .Select(r => new DiskSnapshotDbRow(
                r.SyncId ?? "",
                r.GroupName ?? "", r.SubtypeName ?? "", r.CtrlName ?? "",
                r.VersionRaw ?? "",
                NormalizePath(Relative(root, r.DiskPath ?? "")),
                r.IsOpc, r.RequestNum ?? "", r.CabinetSn ?? "", r.Status ?? "",
                NormalizePath(Relative(root, r.InstructionsPath ?? "")),
                NormalizePath(Relative(root, r.ModbusMapPath ?? "")),
                NormalizePath(Relative(root, r.IoMapPath ?? "")),
                NormalizePath(Relative(root, r.HmiPath ?? "")),
                r.Released, r.Archived, r.SwVersion, r.HwVersion, r.Execution ?? ""))
            .OrderBy(r => r.DiskPath, StringComparer.Ordinal)
            .ThenBy(r => r.Version, StringComparer.Ordinal)
            .ToList();

        var dirs = (folders ?? Array.Empty<string>())
            .Select(NormalizePath)
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        // Разбор считается ЗДЕСЬ, на машине, которая диск видит. Не потому, что иначе нельзя, а
        // потому, что правила раскладки живут в программе: посчитай их читающая сторона — и у неё
        // появилась бы вторая, своя версия этих правил, расходящаяся с первой по мере изменений.
        // Разбор идёт по УСЕЧЁННОЙ описи (kept), чтобы его выводы не ссылались на то, чего в слепке
        // нет: «в базе есть — на диске нет» про файл, не влезший в потолок, было бы враньём.
        var issues = DiskAudit.Find(kept, dirs, db);
        var summary = DiskAudit.Summarize(kept, dirs, db);
        var nowhere = DiskAudit.DocsNowhere(kept, dirs, db);

        return new DiskSnapshot(CurrentSchema, machine, Stamp(takenAt), root, HierarchyService.FolderPo,
            all.Count, truncated, kept, db, dirs, issues, summary, nowhere, appVersion ?? "");
    }

    /// <summary>Путь внутри слепка — всегда через косую черту и без ведущего разделителя. Диск
    /// виндовый, но слепок читают инструменты, которым обратная косая черта — знак экранирования;
    /// расхождение «одна и та же папка записана двумя способами» дороже привычности.</summary>
    public static string NormalizePath(string path) =>
        (path ?? "").Replace('\\', '/').Trim('/');

    /// <summary>Отрезает корень: в слепке пути относительные. Абсолютный путь привязан к тому, как
    /// ИМЕННО эта машина видит шару («Z:\Software» против «\\ant_srv\Software»), и слепки двух машин
    /// одного и того же диска иначе не сравнить. Путь не из-под корня возвращается как есть — врать,
    /// что он внутри, нельзя.</summary>
    public static string Relative(string root, string path)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)) return path ?? "";
        var r = root.Replace('/', '\\').TrimEnd('\\');
        if (!path.Replace('/', '\\').StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) return path;
        return path[(r.Length + 1)..];
    }

    private static string Stamp(DateTime at) => at.ToString("yyyy-MM-ddTHH:mm:ss");

    private static readonly JsonSerializerOptions Json = new()
    {
        // Кириллица в путях — норма этого диска, и уезжать она должна читаемой: слепок смотрят
        // глазами не реже, чем программой.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static DiskSnapshot? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<DiskSnapshot>(json, Json); }
        catch { return null; }
    }
}
