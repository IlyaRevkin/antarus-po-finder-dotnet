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

    /// <summary>Другие версии, у которых ЭТОТ ЖЕ документ и, стало быть, тот же адрес на хостинге, —
    /// «тип / подтип / контроллер», как их видит человек. Так бывает у прошивки, привязанной сразу к
    /// нескольким подтипам шкафа (<see cref="FirmwareSubtypeLinkService"/>): файлы у них общие, папка
    /// на диске одна — основного подтипа, и адрес ссылки у всех ведёт именно в неё.
    ///
    /// Пока этого поля не было, список показывал такую прошивку двумя отдельными строками с
    /// ОДИНАКОВЫМ адресом, и строка «ПЖ / FD / SMH5» с адресом «…/ПЖ/2.0/SMH5/…» читалась как
    /// перепутанная ссылка — жалоба «вместо прошивки ссылка на ПЖ 2.0, и инструкция туда прицепилась
    /// некорректно». Теперь строка одна, а общий документ назван общим прямым текстом.</summary>
    public IReadOnlyList<string> SharedWith { get; init; } = Array.Empty<string>();

    /// <summary>Все записи fw_versions, которым принадлежит этот документ (первая — та, чья папка на
    /// диске). Нужно правке ссылки вручную: менять её надо у всех сразу, иначе половина версий
    /// осталась бы с прежним адресом.</summary>
    public IReadOnlyList<int> VersionIds { get; init; } = Array.Empty<int>();

    /// <summary>Документ общий у нескольких версий.</summary>
    public bool Shared => SharedWith.Count > 0;

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
/// хорошо, не получилось — строчка в предупреждениях, которую никто не читал. Жалоба была такая:
/// «Хер поймёшь, выгрузилась она нет, надо добавить механизм чтобы было видно отгружена ли
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
        var rows = new List<(HostingItem Item, string Path)>();
        if (string.IsNullOrWhiteSpace(diskRoot)) return Array.Empty<HostingItem>();

        foreach (var version in _db.GetAllFwVersionsWithNames())
        {
            if (version.Id is not int id) continue;

            // Путь, записанный на машине коллеги, приводим к нашей форме диска — тем же способом, что
            // и всё остальное приложение (см. FirmwarePathLocalizer). Без этого версия, загруженная с
            // машины, где шара смонтирована иначе, в список не попадала вовсе: относительный путь от
            // нашего корня не считался. Наклейка при этом печаталась и вела на хостинг по нормальному
            // адресу — то есть страница «Хранилище» молчала ровно о тех документах, которые уже обещаны
            // QR-кодом.
            var versionDir = FirmwarePathLocalizer.Localize(version.DiskPath, diskRoot);
            var ownControllerFolder = VersionDocFolders.OwnControllerFolder(
                diskRoot, version.GroupName, version.SubtypeName, version.CtrlName);
            var folder = InstructionFolderOf(versionDir, ownControllerFolder);

            var file = ResolveInstructionFile(version, folder,
                linked: VersionDocFolders.IsLinkedCopy(versionDir, ownControllerFolder));
            var pathOnDisk = file ?? PlannedInstructionPath(version, folder);
            if (pathOnDisk is null) continue;

            var relative = LabelLinkBuilder.RelativeTo(diskRoot, pathOnDisk);
            if (relative is null) continue; // файл вне диска прошивок — адреса на хостинге у него нет

            // Ключ считается от ИТОГОВОГО имени: документ Word уезжает на хостинг собранным PDF (см.
            // InstructionPublisher), и спрашивать у бакета про «.docx» значило бы гарантированно
            // получать «нет на хостинге» по документу, который там лежит.
            var key = settings.KeyFor(InstructionPublisher.AsPublishedName(relative));
            var url = S3Client.PublicUrl(settings, key);

            rows.Add((new HostingItem(
                VersionId: id,
                VersionRaw: version.VersionRaw,
                Where: $"{version.GroupName} / {version.SubtypeName} / {version.CtrlName}",
                Kind: file is null
                    ? "инструкции нет — нужна заглушка"
                    // Видов страницы-заглушки теперь три, и подписывать их все «в разработке» значит
                    // врать в списке ровно там, где человек и проверяет, что именно лежит на хостинге.
                    : InstructionStub.KindOf(file) is { } stubKind ? $"заглушка: {stubKind.Label().ToLowerInvariant()}" : "инструкция",
                SourcePath: file ?? "",
                ObjectKey: key,
                Url: url)
            {
                State = file is null ? HostingState.NoSource : HostingState.Unknown,
                Size = SizeOf(file),
            }, pathOnDisk));
        }

        return Collapse(rows)
            .OrderBy(i => i.Where, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.VersionRaw, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Схлопывает строки, за которыми стоит ОДИН И ТОТ ЖЕ документ. Так выходит у прошивки,
    /// привязанной к нескольким подтипам шкафа: записей в базе несколько, а папка на диске одна — та,
    /// что у основного подтипа, — значит и файл, и его адрес на хостинге общие.
    ///
    /// Строк было столько же, сколько записей, и каждая называла СВОЙ подтип при общем адресе: строка
    /// «ПЖ / FD / SMH5» показывала адрес «…/ПЖ/2.0/SMH5/…» и выглядела перепутанной, хотя всё было
    /// ровно так, как есть на диске. Заодно выкладка перестала гонять один и тот же файл по разу на
    /// каждую запись.
    ///
    /// Главной берётся запись ТОГО подтипа, в чьей папке документ и лежит (её имя есть в пути), —
    /// именно её раскладку показывает адрес. Не нашли такую (путь чужой, папку переименовали) — самая
    /// ранняя по номеру: копии подтипов заводятся после основной.</summary>
    private static IEnumerable<HostingItem> Collapse(IEnumerable<(HostingItem Item, string Path)> rows) =>
        rows
            .GroupBy(r => r.Item.ObjectKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var all = group.ToList();
                if (all.Count == 1) return all[0].Item with { VersionIds = new[] { all[0].Item.VersionId } };

                var main = all.FirstOrDefault(r => PathNames(r.Path).Contains(SubtypeOf(r.Item.Where))).Item
                           ?? all.OrderBy(r => r.Item.VersionId).First().Item;
                return main with
                {
                    SharedWith = all.Select(r => r.Item).Where(i => i != main).Select(i => i.Where)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    VersionIds = all.Select(r => r.Item).OrderBy(i => i == main ? 0 : 1).ThenBy(i => i.VersionId)
                        .Select(i => i.VersionId).ToList(),
                };
            });

    /// <summary>Подтип из подписи «тип / подтип / контроллер» — средняя часть.</summary>
    private static string SubtypeOf(string where)
    {
        var parts = where.Split('/');
        return parts.Length >= 2 ? parts[1].Trim() : "";
    }

    private static IReadOnlyCollection<string> PathNames(string path) =>
        new HashSet<string>(
            (path ?? "").Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);

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
    /// <param name="stubs">Рисовальщик заглушек. Задан — перед отправкой каждая заглушка сверяется с
    /// действующим макетом и, если нарисована по прошлому, перерисовывается на диске (см.
    /// <see cref="InstructionStub.Refresh"/>). Без этого «Перезалить всё» после правки макета делало
    /// ровно то, на что жаловались: аккуратно отправляло в бакет те же самые устаревшие байты с диска,
    /// и заглушки «не менялись, хоть перезаливай, хоть удаляй и заливай». null — прежнее поведение
    /// (например, на машине, где рисовать нечем).</param>
    public HostingRunResult Publish(S3Settings settings, string diskRoot, IEnumerable<HostingItem> items,
        bool onlyMissing, IProgress<HostingProgress>? progress = null, CancellationToken ct = default,
        IInstructionStubWriter? stubs = null)
    {
        var list = items.ToList();
        // Рисовальщик отдаётся и выкладчику: им он рисует страницу обращения в сервис, которая
        // вшивается в выкладываемый PDF последней страницей (см. ServicePageStitcher).
        var publisher = new InstructionPublisher(settings, _client, _pdf, stubs);
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
            // Заглушка, нарисованная по прошлому макету, перерисовывается ДО отправки — иначе наверх
            // уедет прежняя картинка. Перерисовка меняет файл на диске, поэтому она идёт и в режиме
            // «догнать недостающее»: устаревшая на диске заглушка устарела и в бакете.
            // Причину неудачной перерисовки собираем в те же сообщения, что и всё остальное: молча
            // отправить наверх прежнюю картинку — это ровно то поведение, из-за которого «макет меняю,
            // а заглушки прежние» и осталось незамеченным.
            var redraw = new List<string>();
            var refreshed = stubs is not null && InstructionStub.Refresh(item.SourcePath, stubs, redraw) == StubAction.Refreshed;
            foreach (var m in redraw) messages.Add($"{item.VersionRaw}: {m}");
            if (refreshed) messages.Add($"{item.VersionRaw}: заглушка перерисована по новому макету");

            // Уже лежащая в бакете и не изменившаяся заглушка второй раз не отправляется; изменившаяся —
            // отправляется всегда, иначе правка макета так и не доехала бы до хостинга.
            if (onlyMissing && item.State == HostingState.Published && !refreshed) { skipped++; continue; }

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
    /// <param name="linked">Запись привязывает прошивку основного подтипа к дополнительному
    /// (<see cref="VersionDocFolders"/>). Такой записи путь к инструкции достаётся ПО НАСЛЕДСТВУ от
    /// основной — её заводят копией (см. FirmwareSubtypeLinkService.LinkExtras), — то есть указывает на
    /// документ соседнего шкафа. Поэтому свой документ, если он появился, важнее сохранённого пути:
    /// иначе «ПЖ / FD» до скончания века показывал бы руководство от «ПЖ / 2.0».</param>
    private static string? ResolveInstructionFile(FwVersionRecord version, string? folder, bool linked)
    {
        var own = FirstDocumentIn(folder);
        if (linked && own is not null) return own;

        var stored = version.InstructionsPath;
        if (!string.IsNullOrWhiteSpace(stored))
        {
            try
            {
                if (File.Exists(stored) || Directory.Exists(stored)) return stored;
            }
            catch (Exception) { /* недоступный путь — считаем, что файла нет */ }
        }

        return own;
    }

    /// <summary>Настоящий документ в папке, а если его нет — заглушка-«вместо».
    ///
    /// Порядок именно такой, и он не косметика: рядом с настоящей инструкцией теперь лежит
    /// страница-дополнение «если остались вопросы» (см. <see cref="StubKind.ServiceNote"/>), и она
    /// тоже проходит проверку «это заглушка». Возьми мы просто первый подходящий файл, порядок обхода
    /// папки решал бы, что считать инструкцией этой версии, — и на хостинг под адресом документа
    /// иногда уезжала бы страница с одним телефоном.</summary>
    private static string? FirstDocumentIn(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            if (!Directory.Exists(folder)) return null;
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).ToList();
            return files.FirstOrDefault(f => !DocFileResolver.IsNotADocument(f))
                   ?? files.FirstOrDefault(f => InstructionStub.KindOf(f)?.ReplacesInstruction() == true);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Где инструкция этой версии ДОЛЖНА лежать, даже если её ещё нет. Нужно, чтобы строка
    /// «инструкции нет» всё равно показывала конечный адрес: наклейку с QR печатают заранее, и адрес
    /// обязан быть известен до появления документа.</summary>
    private static string? PlannedInstructionPath(FwVersionRecord version, string? folder)
    {
        if (folder is null) return null;
        var name = InstructionNaming.BuildFileName(version.VersionRaw, ".pdf");
        return name.Length > 0 ? Path.Combine(folder, name) : null;
    }

    /// <summary>Папка инструкции этой записи — ровно та, куда эта же запись свою инструкцию и КЛАДЁТ
    /// (см. <see cref="VersionDocFolders"/>). Считать её от одного лишь пути версии нельзя: у записи,
    /// привязывающей прошивку к дополнительному подтипу шкафа, путь ведёт в папку основного, и адрес на
    /// хостинге получался бы от чужого шкафа.</summary>
    private static string? InstructionFolderOf(string? versionDir, string? ownControllerFolder)
    {
        if (string.IsNullOrWhiteSpace(versionDir)) return null;
        try
        {
            return VersionDocFolders.BestReadFolder(versionDir, ownControllerFolder, HierarchyFolders.Instructions)
                   ?? VersionLayout.SlotFolder(versionDir, HierarchyFolders.Instructions);
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
