using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AntarusPoFinder.Core.Services;

/// <summary>ЖУРНАЛ ПРИЛОЖЕНИЯ УЕЗЖАЕТ САМ — пачкой, раз в сутки, тихим каналом.
///
/// <b>Почему пачкой, а не по событию.</b> Журнал заливки в ПЛК уезжает поштучно, и это правильно:
/// их единицы в день, и каждая — ответ на конкретный вопрос. Здесь всё наоборот: событий тысячи, и
/// отправка «по событию» означала бы тысячу запросов в день с каждой машины. Поэтому накопленное
/// складывается в один архив и уходит одним запросом.
///
/// <b>Почему не чаще раза в сутки.</b> Журнал нужен для разбора («что было у человека перед тем,
/// как сломалось»), а не для наблюдения в реальном времени. Разбор начинается после жалобы, то есть
/// часами позже. Чаще — значит шесть машин × несколько раз в день в одну папку, где лежат ещё и
/// слепки, и журналы заливок: полезное утонет, и читать перестанут всё.
///
/// <b>Почему только если есть что отправлять.</b> Пустой архив раз в сутки с каждой машины — это
/// ровно тот мусорный поток, которого и боялись, заводя автоматику. Нет архивов — не было и
/// событий; молчим.
///
/// <b>Отправка начинается с ротации.</b> Текущий файл откладывается в архив и уезжает уже он: слать
/// файл, в который прямо сейчас пишут, значит однажды отправить его обрезанным посреди события — и
/// именно того события, ради которого всё затевалось.
///
/// <b>Удаляем только отправленное.</b> Как у журналов заливок: ошибка сети не имеет права стирать
/// то, ради чего всё и заведено.</summary>
public static class AppLogDrop
{
    /// <summary>Приставка имени архива. По ней журналы отделяются от слепков (<c>_disk_</c>) и
    /// тикетов в той же папке <c>diag/</c>.</summary>
    public const string FilePrefix = "log";

    /// <summary>Не чаще раза в сутки — см. рассуждение в заголовке класса.</summary>
    public static TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Потолок размера одной пачки. Журнал ужимается раз в десять, но машина, на которой
    /// что-то зациклилось, способна накопить сотню мегабайт, и отправлять её по конторскому каналу
    /// незачем: беда видна и по первым архивам. Лишнее остаётся лежать и уедет следующей пачкой —
    /// либо будет удалено ротацией, если успеет состариться.</summary>
    public const long MaxBatchBytes = 8L * 1024 * 1024;

    /// <summary>Имя архива: время, приставка, машина — тем же правилом, что у слепка диска. Время
    /// первым: в <c>diag/</c> лежит вперемешку всё со всех машин, и сортировка по имени должна
    /// совпадать с сортировкой по времени.</summary>
    public static string FileName(string machine, DateTime at) =>
        $"{at:yyyyMMdd_HHmmss}_{FilePrefix}_{DiskSnapshotStorage.SanitizeMachine(machine)}.zip";

    /// <summary>Пора ли отправлять. Чистое решение, отдельно от файлов и сети: его и проверяют
    /// тестом.</summary>
    public static bool Due(DateTime lastSentUtc, DateTime nowUtc) =>
        nowUtc - lastSentUtc >= Interval;

    /// <summary>Упаковать пачку: по файлу на архив журнала, имена сохраняются — по ним видно, за
    /// какое время кусок.</summary>
    public static byte[] Pack(IEnumerable<(string Name, string Text)> parts)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var part in parts)
            {
                var entry = zip.CreateEntry(part.Name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                var bytes = new UTF8Encoding(false).GetBytes(part.Text ?? "");
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        return output.ToArray();
    }

    public sealed record Result(bool Ok, string? Error, string FileName, int Files, int Bytes)
    {
        /// <summary>Отправлять было нечего — не ошибка и не удача, а штатная тишина.</summary>
        public static readonly Result Nothing = new(true, null, "", 0, 0);
    }

    /// <summary>Отложить текущий журнал и отправить всё накопленное. Отметку «когда отправляли
    /// последний раз» держит вызывающий: она местная, как и все остальные watermark'и.</summary>
    public static async Task<Result> FlushAsync(
        string machine, string dropUrl, string dropCode,
        HttpClient? http = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dropUrl) || string.IsNullOrWhiteSpace(dropCode))
            return Result.Nothing;

        AppLog.Roll();
        var archives = TakeBatch(AppLog.Archives());
        if (archives.Count == 0) return Result.Nothing;

        var parts = new List<(string Name, string Text)>();
        foreach (var path in archives)
        {
            try { parts.Add((Path.GetFileName(path), File.ReadAllText(path, Encoding.UTF8))); }
            catch (IOException) { /* занят — уедет следующей пачкой */ }
        }
        if (parts.Count == 0) return Result.Nothing;

        // ПРОВЕРКА НА СЕКРЕТЫ — здесь, у самой отправки, и по ВСЕЙ пачке сразу: тем же кодом, что
        // у слепка диска. Нашлось — не отправляем ничего и называем причину в журнале (новом,
        // текущем: этот уже отложен). Файлы при этом НЕ удаляются — их можно посмотреть на машине
        // глазами, а дальше их уберёт обычная ротация по сроку.
        var whole = string.Join("\n", parts.Select(p => p.Text));
        if (DiskSnapshotSafety.RefusalForText(whole) is { } refusal)
        {
            AppLog.Error(AppLog.AreaLog, refusal);
            return new Result(false, refusal, "", parts.Count, 0);
        }

        var name = FileName(machine, DateTime.UtcNow);
        var body = Pack(parts);
        var outcome = await DropChannel.PostAsync(dropUrl, dropCode,
            $"журнал приложения, {machine} (автоматически)", name, body, "application/zip",
            http ?? Shared, ct).ConfigureAwait(false);

        if (!outcome.Ok)
        {
            AppLog.Warn(AppLog.AreaLog, $"журнал не отправлен: {outcome.Error}");
            return new Result(false, outcome.Error, name, parts.Count, body.Length);
        }

        foreach (var path in archives)
            try { File.Delete(path); } catch (IOException) { /* удалится ротацией */ }

        AppLog.Info(AppLog.AreaLog,
            $"журнал отправлен: {name}, кусков {parts.Count}, {body.Length / 1024} КБ");
        AppLog.Prune();
        return new Result(true, null, name, parts.Count, body.Length);
    }

    /// <summary>Сколько архивов влезает в одну пачку, от свежих к старым (см.
    /// <see cref="MaxBatchBytes"/>). Свежие ПЕРВЫМИ: если упёрлись в потолок, уехать должно то, что
    /// ближе к беде, а не то, что ближе к удалению.</summary>
    public static IReadOnlyList<string> TakeBatch(IEnumerable<string> archives, long maxBytes = MaxBatchBytes)
    {
        var batch = new List<string>();
        long total = 0;
        foreach (var path in archives ?? Array.Empty<string>())
        {
            long size;
            try { size = new FileInfo(path).Length; } catch (Exception) { continue; }
            if (batch.Count > 0 && total + size > maxBytes) break;
            batch.Add(path);
            total += size;
        }
        return batch;
    }

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromMinutes(5) };
}
