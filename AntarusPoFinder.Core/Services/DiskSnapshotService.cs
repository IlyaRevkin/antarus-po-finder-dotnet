using System.IO.Compression;
using System.Text;
using AntarusPoFinder.Core.Data;

namespace AntarusPoFinder.Core.Services;

/// <summary>Снимает слепок дерева ПО и выкладывает его в хранилище.
///
/// Здесь живёт всё, что трогает диск и сеть; решения — что внутри слепка и как он называется —
/// вынесены в <see cref="DiskSnapshot"/> и <see cref="DiskSnapshotStorage"/>, где их можно
/// проверить тестами. Поэтому в этом классе намеренно нет ни одной «умной» строки.
///
/// Всё делается молча и best effort: слепок — удобство для того, кто чинит, а не работа оператора.
/// Нет ключей, нет диска, не отвечает бакет — просто не в этот раз (ровно как выкладка инструкций,
/// см. S3Settings.CanPublish).</summary>
public sealed class DiskSnapshotService
{
    private readonly S3Settings _settings;
    private readonly S3Client _client;

    public DiskSnapshotService(S3Settings settings, S3Client? client = null)
    {
        _settings = settings;
        _client = client ?? new S3Client();
    }

    public bool CanPublish => _settings.CanPublish;

    public sealed record Outcome(bool Ok, string? Error, int Files, int Bytes, bool Truncated)
    {
        public static Outcome Skipped() => new(true, null, 0, 0, false);
        public static Outcome Fail(string error) => new(false, error, 0, 0, false);
    }

    /// <summary>Снять слепок и выложить. <paramref name="machine"/> — имя компьютера: слепки разных
    /// машин одного диска лежат рядом и не перетирают друг друга (у них может расходиться даже
    /// видимость шары).</summary>
    public async Task<Outcome> RunAsync(Database db, string root, string machine, CancellationToken ct = default)
    {
        if (!CanPublish) return Outcome.Skipped();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return Outcome.Skipped();

        try
        {
            var takenAt = DateTime.UtcNow;
            var snapshot = Collect(db, root, machine, appVersion: "", takenAtUtc: takenAt, ct: ct);
            var payload = Gzip(snapshot.ToJson());

            var latest = await _client.PutBytesAsync(_settings, DiskSnapshotStorage.LatestKey(machine),
                payload, "application/gzip", ct);
            if (!latest.Ok) return Outcome.Fail(latest.Error ?? "не удалось выложить слепок");

            // Датированная копия — вторым запросом тем же телом. Если он не прошёл, слепок всё равно
            // уже лежит под «последним», и это главное: история приятна, свежесть обязательна.
            await _client.PutBytesAsync(_settings, DiskSnapshotStorage.DatedKey(machine, takenAt),
                payload, "application/gzip", ct);

            await PruneAsync(machine, ct);

            return new Outcome(true, null, snapshot.FileCount, payload.Length, snapshot.Truncated);
        }
        catch (Exception ex)
        {
            return Outcome.Fail(S3Client.Explain(ex));
        }
    }

    /// <summary>СНЯТЬ слепок, никуда его не отправляя: обход диска плюс строки базы. Отдельно от
    /// отправки, потому что адресатов у одного и того же слепка двое (страница приёма и бакет), а
    /// обход диска — самая долгая часть, и делать его дважды было бы странно.
    ///
    /// <paramref name="progress"/> — строка для окна хода («обошли 3400 файлов»). Обход сетевого
    /// диска идёт минутами, и молчащее окно от зависшего не отличить.</summary>
    public static DiskSnapshot Collect(
        Database db, string root, string machine, string appVersion, DateTime takenAtUtc,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var folders = new List<string>();
        var files = EnumerateSoftwareTree(root, folders, progress, ct).ToList();

        // Архивные версии в слепок входят: именно вокруг «куда делась старая прошивка» и бывают
        // вопросы, а на диске она лежит как ни в чём не бывало.
        var rows = db.GetAllFwVersionsWithNames(includeArchived: true);

        progress?.Report($"обход закончен: файлов {files.Count}, папок {folders.Count}; сверяю с базой");
        return DiskSnapshot.Build(machine, takenAtUtc, root, files, rows,
            folders: folders, appVersion: appVersion);
    }

    /// <summary>Обход дерева ПО. Недоступную папку пропускаем молча и идём дальше: на сетевом диске
    /// одна закрытая папка — обычное дело, и ронять из-за неё весь слепок незачем.
    ///
    /// Папки собираются ОТДЕЛЬНЫМ списком, а не выводятся из путей файлов. Это не придирка к
    /// полноте: папка, в которой файлов нет, в описи файлов не оставляет следа вовсе — а жалоба
    /// «не найден HMI» (06.11.2026) оказалась именно такой, папка <c>HMI\</c> на месте, внутри
    /// пусто. Ради того, чтобы такие дыры были видны списком, слепок и затевался.</summary>
    private static IEnumerable<(string RelativePath, long Size, DateTime Modified)> EnumerateSoftwareTree(
        string root, List<string> folders, IProgress<string>? progress, CancellationToken ct)
    {
        var po = Path.Combine(root, HierarchyService.FolderPo);
        if (!Directory.Exists(po)) yield break;

        var stack = new Stack<string>();
        stack.Push(po);
        var seen = 0;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            folders.Add(DiskSnapshot.Relative(root, dir));

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var sub in subdirs) stack.Push(sub);

            string[] entries;
            try { entries = Directory.GetFiles(dir); }
            catch { continue; }

            foreach (var file in entries)
            {
                FileInfo info;
                try { info = new FileInfo(file); }
                catch { continue; }
                // Отчёт раз в тысячу файлов, а не на каждый: на сетевом диске их десятки тысяч, и
                // перерисовывать строку состояния чаще, чем человек успевает прочитать, — только
                // тратить его же время.
                if (++seen % 1000 == 0) progress?.Report($"обошли файлов: {seen}");
                yield return (DiskSnapshot.Relative(root, file), info.Length, info.LastWriteTime);
            }
        }
    }

    /// <summary>СНЯТЬ И ОТПРАВИТЬ через тихий канал диагностики — тот путь, которым слепок доезжает
    /// до того, кто чинит программу (почему не бакет — см. <see cref="DiskSnapshotDrop"/>).
    ///
    /// Статический метод, а не ещё один объект: ключей от хранилища этому пути не нужно вовсе,
    /// нужны только адрес страницы приёма и код — те же, что у журналов заливок.</summary>
    public static async Task<DiskSnapshotDrop.Result> SendToDropAsync(
        Database db, string root, string machine, string appVersion,
        string dropUrl, string dropCode,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var takenAt = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return new DiskSnapshotDrop.Result(false, "рабочий диск недоступен", "", 0);

        var snapshot = Collect(db, root, machine, appVersion, takenAt, progress, ct);
        progress?.Report("упаковываю и отправляю");
        var result = await DiskSnapshotDrop.SendAsync(snapshot.ToJson(), machine, takenAt,
            dropUrl, dropCode, ct: ct).ConfigureAwait(false);

        progress?.Report(result.Ok
            ? $"отправлено: {result.FileName}, {result.Bytes / 1024} КБ, расхождений {snapshot.Issues.Count}"
            : $"не отправлено: {result.Error}");
        return result;
    }

    private static byte[] Gzip(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(raw, 0, raw.Length);
        return output.ToArray();
    }

    private async Task PruneAsync(string machine, CancellationToken ct)
    {
        var page = await _client.ListAsync(_settings, DiskSnapshotStorage.MachineFolder(machine),
            grouped: false, ct: ct);
        if (!page.Ok) return;

        foreach (var key in DiskSnapshotStorage.ToPrune(page.Objects.Select(o => o.Key)))
            await _client.DeleteAsync(_settings, key, ct);
    }
}
