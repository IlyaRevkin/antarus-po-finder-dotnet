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
    [property: JsonPropertyName("db")] IReadOnlyList<DiskSnapshotDbRow> Db)
{
    public const int CurrentSchema = 1;

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
    public static DiskSnapshot Build(
        string machine, DateTime takenAt, string root,
        IEnumerable<(string RelativePath, long Size, DateTime Modified)> files,
        IEnumerable<FwVersionRecord> rows,
        int maxFiles = MaxFiles)
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
                r.Released, r.Archived, r.SwVersion, r.HwVersion, r.Execution ?? ""))
            .OrderBy(r => r.DiskPath, StringComparer.Ordinal)
            .ThenBy(r => r.Version, StringComparer.Ordinal)
            .ToList();

        return new DiskSnapshot(CurrentSchema, machine, Stamp(takenAt), root, HierarchyService.FolderPo,
            all.Count, truncated, kept, db);
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
