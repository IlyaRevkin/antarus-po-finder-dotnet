using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>В каком состоянии на хостинге находится один документ.</summary>
public enum HostingState
{
    /// <summary>Ещё не смотрели. Список умеет строиться БЕЗ обращения к сети — обход сотен версий с
    /// запросом на каждую занимает минуты, и открытие страницы не должно этого ждать.</summary>
    Unknown,

    /// <summary>Объект в бакете есть — проверено запросом, а не выведено из своей записи о выкладке.</summary>
    Published,

    /// <summary>Объекта в бакете нет. Это ровно то состояние, ради которого страница и заводится:
    /// «хер поймёшь, выгрузилась она или нет».</summary>
    Missing,

    /// <summary>Файла нет на диске — выкладывать нечего. Отдельно от <see cref="Missing"/>: чинится
    /// это не кнопкой «Выложить», а разбирательством с диском.</summary>
    NoSource,

    /// <summary>Не смогли ни проверить, ни выложить. Причина — в <see cref="HostingItem.Error"/>.</summary>
    Failed,
}

/// <summary>Одна строка списка «что должно лежать на хостинге».</summary>
public sealed record HostingItem(
    int VersionId,
    string VersionRaw,
    string Where,
    string Kind,
    string SourcePath,
    string ObjectKey,
    string Url)
{
    public HostingState State { get; init; } = HostingState.Unknown;
    public string? Error { get; init; }
    public long? Size { get; init; }
    public DateTime? CheckedAt { get; init; }

    /// <summary>Есть ли смысл предлагать «Выложить» по этой строке.</summary>
    public bool CanPublish => State is HostingState.Missing or HostingState.Failed or HostingState.Unknown or HostingState.Published;

    public string StateLabel => State switch
    {
        HostingState.Published => "на хостинге",
        HostingState.Missing => "нет на хостинге",
        HostingState.NoSource => "нет файла на диске",
        HostingState.Failed => "ошибка",
        _ => "не проверено",
    };
}

/// <summary>Ход длинной операции — для строки состояния и полосы прогресса.</summary>
public sealed record HostingProgress(int Done, int Total, string What)
{
    public int Percent => Total > 0 ? (int)Math.Round(Done * 100.0 / Total) : 0;
}

/// <summary>Итог прогона.</summary>
public sealed record HostingRunResult(int Published, int Skipped, int Failed, IReadOnlyList<string> Messages);

/// <summary>Что должно лежать на хостинге, что там лежит на самом деле и как привести одно к другому.
///
/// <b>Зачем это вообще.</b> До сих пор выкладка была побочным действием загрузки версии: получилось —
/// хорошо, не получилось — строчка в предупреждениях, которую никто не читал. Дословная жалоба
/// владельца: «Хер поймёшь, выгрузилась она нет, надо добавить механизм чтобы было видно отгружена ли
/// она на внешнее хранилище». Отсюда три свойства этого сервиса:
///
/// <list type="number">
/// <item><description><b>Список строится без сети.</b> <see cref="Plan"/> ходит только в базу и на
/// диск. Открытие страницы на сотне версий не должно упираться в сотню запросов к хостингу.</description></item>
/// <item><description><b>Состояние проверяется правдой.</b> «Выложено» — это ответ хостинга на HEAD
/// по ключу, а не наша запись о том, что мы когда-то выкладывали. Файл могли удалить руками через
/// S3-клиент, выложить с другой машины, а локальная база могла быть пересоздана.</description></item>
/// <item><description><b>Прогон прерываемый и продолжаемый.</b> Обход идёт по одному объекту с
/// отчётом о ходе и проверкой отмены: на сотнях файлов человек обязан видеть, что происходит, и мочь
/// это остановить, ничего не сломав.</description></item>
/// </list></summary>
public sealed class HostingSyncService
{
    private readonly Database _db;
    private readonly S3Client _client;
    private readonly IDocumentToPdf? _pdf;

    public HostingSyncService(Database db, S3Client? client = null, IDocumentToPdf? pdf = null)
    {
        _db = db;
        _client = client ?? new S3Client();
        _pdf = pdf;
    }

    /// <summary>Список того, что ДОЛЖНО лежать на хостинге, по данным базы и диска. Сеть не трогается.
    ///
    /// Сейчас на хостинг уходят инструкции — настоящие документы и заглушки «в разработке» (заглушка
    /// там не для красоты: ссылка под QR обязана открываться с первого дня, наклейку клеят на шкаф
    /// задолго до того, как документ напишут). Версии без папки инструкции в список не попадают
    /// вовсе — им там нечего делать.</summary>
    public IReadOnlyList<HostingItem> Plan(S3Settings settings, string diskRoot)
    {
        var items = new List<HostingItem>();
        if (string.IsNullOrWhiteSpace(diskRoot)) return items;

        foreach (var version in _db.GetAllFwVersionsWithNames())
        {
            if (version.Id is not int id) continue;

            var file = ResolveInstructionFile(version);
            var pathOnDisk = file ?? PlannedInstructionPath(version);
            if (pathOnDisk is null) continue;

            var relative = LabelLinkBuilder.RelativeTo(diskRoot, pathOnDisk);
            if (relative is null) continue; // файл вне диска прошивок — адреса на хостинге у него нет

            // Ключ считается от ИТОГОВОГО имени: документ Word уезжает на хостинг собранным PDF (см.
            // InstructionPublisher), и спрашивать у бакета про «.docx» значило бы гарантированно
            // получать «нет на хостинге» по документу, который там лежит.
            var key = settings.KeyFor(InstructionPublisher.AsPublishedName(relative));
            var url = S3Client.PublicUrl(settings, key);

            items.Add(new HostingItem(
                VersionId: id,
                VersionRaw: version.VersionRaw,
                Where: $"{version.GroupName} / {version.SubtypeName} / {version.CtrlName}",
                Kind: file is null
                    ? "инструкции нет — нужна заглушка"
                    : InstructionStub.IsStub(file) ? "заглушка «в разработке»" : "инструкция",
                SourcePath: file ?? "",
                ObjectKey: key,
                Url: url)
            {
                State = file is null ? HostingState.NoSource : HostingState.Unknown,
                Size = SizeOf(file),
            });
        }

        return items
            .OrderBy(i => i.Where, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.VersionRaw, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Спросить у хостинга, что из списка там действительно есть. По одному объекту, с
    /// отчётом о ходе и проверкой отмены — на сотнях файлов это минуты.</summary>
    public async Task<IReadOnlyList<HostingItem>> CheckAsync(S3Settings settings, IReadOnlyList<HostingItem> items,
        IProgress<HostingProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new List<HostingItem>(items.Count);
        var done = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new HostingProgress(done, items.Count, item.VersionRaw));
            done++;

            if (item.State == HostingState.NoSource) { result.Add(item); continue; }

            var presence = await _client.HeadAsync(settings, item.ObjectKey, ct).ConfigureAwait(false);
            result.Add(item with
            {
                State = presence switch
                {
                    { Ok: true, Exists: true } => HostingState.Published,
                    { Ok: true } => HostingState.Missing,
                    _ => HostingState.Failed,
                },
                Error = presence.Ok ? null : presence.Error,
                Size = presence.Length ?? item.Size,
                CheckedAt = DateTime.Now,
            });
        }

        progress?.Report(new HostingProgress(done, items.Count, "готово"));
        return result;
    }

    /// <summary>Выложить перечисленное. <paramref name="onlyMissing"/> — обычный режим «догнать
    /// недостающее»; false перезаливает всё подряд (нужно, когда документы правили на диске, а на
    /// хостинге осталась прошлая редакция).
    ///
    /// Ошибка на одном файле не роняет прогон: остальные всё равно уезжают, а причина копится в
    /// сообщениях — иначе один битый документ в середине списка отменял бы работу по всем остальным.</summary>
    public HostingRunResult Publish(S3Settings settings, string diskRoot, IEnumerable<HostingItem> items,
        bool onlyMissing, IProgress<HostingProgress>? progress = null, CancellationToken ct = default)
    {
        var list = items.ToList();
        var publisher = new InstructionPublisher(settings, _client, _pdf);
        var messages = new List<string>();
        int published = 0, skipped = 0, failed = 0, done = 0;

        foreach (var item in list)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new HostingProgress(done, list.Count, item.VersionRaw));
            done++;

            if (item.State == HostingState.NoSource || string.IsNullOrEmpty(item.SourcePath))
            {
                skipped++;
                messages.Add($"{item.VersionRaw}: файла инструкции на диске нет — выкладывать нечего");
                continue;
            }
            if (onlyMissing && item.State == HostingState.Published) { skipped++; continue; }

            var warnings = new List<string>();
            var url = publisher.Publish(item.SourcePath, item.SourcePath, diskRoot, warnings);
            foreach (var w in warnings) messages.Add($"{item.VersionRaw}: {w}");

            if (url is null) failed++;
            else published++;
        }

        progress?.Report(new HostingProgress(done, list.Count, "готово"));
        return new HostingRunResult(published, skipped, failed, messages);
    }

    /// <summary>Файл инструкции этой версии на диске — настоящий документ или заглушка. Путь из базы
    /// может указывать на папку (постраничные сканы) — тогда возвращается она сама: выкладывается
    /// такая инструкция пофайлово, и это забота <see cref="InstructionPublisher"/>.</summary>
    private static string? ResolveInstructionFile(FwVersionRecord version)
    {
        var stored = version.InstructionsPath;
        if (!string.IsNullOrWhiteSpace(stored))
        {
            try
            {
                if (File.Exists(stored) || Directory.Exists(stored)) return stored;
            }
            catch (Exception) { /* недоступный путь — считаем, что файла нет */ }
        }

        var folder = InstructionFolderOf(version);
        if (folder is null) return null;

        try
        {
            if (!Directory.Exists(folder)) return null;
            return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => !DocFileResolver.IsShortcut(f));
        }
        catch (Exception) { return null; }
    }

    /// <summary>Где инструкция этой версии ДОЛЖНА лежать, даже если её ещё нет. Нужно, чтобы строка
    /// «инструкции нет» всё равно показывала конечный адрес: наклейку с QR печатают заранее, и адрес
    /// обязан быть известен до появления документа.</summary>
    private static string? PlannedInstructionPath(FwVersionRecord version)
    {
        var folder = InstructionFolderOf(version);
        if (folder is null) return null;
        var name = InstructionNaming.BuildFileName(version.VersionRaw, ".pdf");
        return name.Length > 0 ? Path.Combine(folder, name) : null;
    }

    private static string? InstructionFolderOf(FwVersionRecord version)
    {
        if (string.IsNullOrWhiteSpace(version.DiskPath)) return null;
        try
        {
            var controller = VersionLayout.ControllerFolderOf(version.DiskPath);
            return VersionLayout.SlotBestReadFolder(version.DiskPath, controller, HierarchyFolders.Instructions)
                   ?? VersionLayout.SlotFolder(version.DiskPath, HierarchyFolders.Instructions);
        }
        catch (Exception) { return null; }
    }

    private static long? SizeOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return File.Exists(path) ? new FileInfo(path).Length : null; }
        catch (Exception) { return null; }
    }
}
