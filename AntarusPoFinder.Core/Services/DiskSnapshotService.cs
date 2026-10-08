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
            var files = EnumerateSoftwareTree(root);
            // Архивные версии в слепок входят: именно вокруг «куда делась старая прошивка» и бывают
            // вопросы, а на диске она лежит как ни в чём не бывало.
            var rows = db.GetAllFwVersionsWithNames(includeArchived: true);

            var snapshot = DiskSnapshot.Build(machine, takenAt, root, files, rows);
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

    /// <summary>Обход дерева ПО. Недоступную папку пропускаем молча и идём дальше: на сетевом диске
    /// одна закрытая папка — обычное дело, и ронять из-за неё весь слепок незачем.</summary>
    private static IEnumerable<(string RelativePath, long Size, DateTime Modified)> EnumerateSoftwareTree(string root)
    {
        var po = Path.Combine(root, HierarchyService.FolderPo);
        if (!Directory.Exists(po)) yield break;

        var stack = new Stack<string>();
        stack.Push(po);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();

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
                yield return (DiskSnapshot.Relative(root, file), info.Length, info.LastWriteTime);
            }
        }
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
