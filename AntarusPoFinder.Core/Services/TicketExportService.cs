using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Складывает тикеты в один файл, который можно унести с рабочей машины куда угодно —
/// флешкой, почтой, через страницу загрузки. Нужно это ровно затем, что сетевой диск с тикетами
/// (TicketSyncService) виден только внутри конторы: тот, кто эти тикеты чинит, до него не достаёт и
/// узнать о проблеме иначе не может.
///
/// В архиве лежит одно и то же в двух видах: <c>tickets.md</c> — читать человеку, <c>tickets.json</c>
/// — разбирать программой (полный текст, идентификаторы, время без потерь). Плюс папка
/// <c>attachments/&lt;id тикета&gt;/</c> с тем, что к тикету прикладывали: скриншоты и логи обычно и
/// объясняют проблему лучше самого текста. Разложены они по id, а не по номеру в списке, чтобы
/// ссылка из обоих файлов вела в одну и ту же папку независимо от отбора и порядка сортировки.</summary>
public static class TicketExportService
{
    /// <summary>Обстоятельства выгрузки — их не восстановить по самим тикетам, а чинящему они нужны:
    /// от какой версии программы жалоба и с какой машины/учётки её унесли.</summary>
    public sealed record Meta(string AppVersion, string Machine, string User, string Role, string ScopeLabel, DateTime At);

    public sealed record Result(int Tickets, int Attachments, long Bytes, List<string> Warnings);

    /// <summary>Файл, приложенный к тикету: где лежит сейчас и под каким именем ляжет в архив.</summary>
    public sealed record Attachment(string TicketId, string Path, string FileName, long Size);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Без этого кириллица уезжает в \uXXXX: формально верно, но json тогда нечитаем глазами,
        // а его открывают руками не реже, чем разбирают кодом.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string SuggestedFileName(DateTime at) => $"tickets_{at:yyyyMMdd_HHmm}.zip";

    // ── Отправка через хранилище ──────────────────────────────────────────────
    // Архив можно унести флешкой или почтой, но с рабочей машины проще всего дотянуться до бакета:
    // он уже настроен, уже проходит корпоративный фаервол и уже открыт в самой программе. Отдельная
    // папка, а не корень: тикеты соседствовали бы с инструкциями, которые туда кладёт выкладка.

    /// <summary>Папка в бакете под выгрузки тикетов. Латиницей и без пробелов намеренно — ключ
    /// объекта попадает в адрес, и разбирать его глазами придётся именно там.</summary>
    public const string StorageFolder = "tickets";

    /// <summary>Длина случайного хвоста в имени объекта.</summary>
    private const int TokenLength = 8;

    /// <summary>Случайный хвост для имени. Отдельным методом, чтобы имя можно было проверить тестом:
    /// в <see cref="StorageObjectName"/> он приходит параметром, а не берётся изнутри.</summary>
    public static string NewToken() => Guid.NewGuid().ToString("N")[..TokenLength];

    /// <summary>Имя объекта в хранилище: к обычному имени файла добавляется случайный хвост.
    ///
    /// <b>Хвост здесь не для уникальности, а вместо замка.</b> Бакет отдаётся наружу по публичному
    /// веб-адресу (см. <see cref="S3Client.PublicUrl"/>), и объект с предсказуемым именем вида
    /// «tickets_20260824_1530.zip» открыл бы кто угодно, кто догадается подставить дату. Тикеты —
    /// это внутренняя переписка о том, что в конторе сломано, вместе со скриншотами рабочих
    /// экранов. Случайный хвост делает адрес неугадываемым; полноценная защита — это отдельный
    /// закрытый бакет, и пока его нет, честно считать эту меру именно тем, что она есть.</summary>
    public static string StorageObjectName(DateTime at, string token) =>
        $"tickets_{at:yyyyMMdd_HHmm}_{token}.zip";

    /// <summary>Полный ключ объекта — с папкой и с префиксом, заданным в настройках хранилища.</summary>
    public static string StorageKey(S3Settings settings, DateTime at, string token) =>
        settings.KeyFor($"{StorageFolder}/{StorageObjectName(at, token)}");

    /// <summary>Собирает архив. <paramref name="attachmentsDirFor"/> отдаёт папку вложений тикета
    /// (или null, если сетевого диска сейчас нет) — раскладка вложений на диске известна приложению,
    /// а не ядру, см. TicketSyncService.AttachmentsDir. Недоступное вложение не срывает выгрузку:
    /// тикеты уезжают без него, а причина попадает в Warnings и показывается человеку — молча
    /// отдать неполный архив тому, кто по нему будет чинить, хуже, чем отдать неполный с оговоркой.</summary>
    public static Result Write(string zipPath, Meta meta, IReadOnlyList<Ticket> tickets, Func<string, string?>? attachmentsDirFor)
    {
        var warnings = new List<string>();
        var attachments = CollectAttachments(tickets, attachmentsDirFor, warnings);

        // Пишем во временный файл рядом и переименовываем: прерванная на середине выгрузка (нет
        // места, оборвался сетевой диск) не должна оставить под нужным именем битый архив, который
        // потом уедет почтой как настоящий.
        var tmpPath = zipPath + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(tmpPath, ZipArchiveMode.Create))
            {
                WriteTextEntry(zip, "tickets.md", BuildMarkdown(meta, tickets, attachments), withBom: true);
                WriteTextEntry(zip, "tickets.json", BuildJson(meta, tickets, attachments), withBom: false);

                foreach (var a in attachments.Values.SelectMany(list => list))
                {
                    try { zip.CreateEntryFromFile(a.Path, $"attachments/{a.TicketId}/{a.FileName}"); }
                    catch (Exception ex) { warnings.Add($"Вложение «{a.FileName}» не попало в архив: {ex.Message}"); }
                }
            }

            File.Move(tmpPath, zipPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { /* временный файл не удалился — не повод скрыть настоящую ошибку */ }
            throw;
        }

        var bytes = new FileInfo(zipPath).Length;
        return new Result(tickets.Count, attachments.Values.Sum(l => l.Count), bytes, warnings);
    }

    private static Dictionary<string, List<Attachment>> CollectAttachments(
        IReadOnlyList<Ticket> tickets, Func<string, string?>? attachmentsDirFor, List<string> warnings)
    {
        var result = new Dictionary<string, List<Attachment>>(StringComparer.Ordinal);
        if (attachmentsDirFor is null) return result;

        foreach (var t in tickets)
        {
            string? dir;
            try { dir = attachmentsDirFor(t.Id); }
            catch (Exception ex) { warnings.Add($"Вложения тикета {Short(t.Id)} не прочитаны: {ex.Message}"); continue; }

            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;

            try
            {
                var files = Directory.EnumerateFiles(dir)
                    .Select(f => new Attachment(t.Id, f, Path.GetFileName(f), new FileInfo(f).Length))
                    .OrderBy(a => a.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (files.Count > 0) result[t.Id] = files;
            }
            catch (Exception ex) { warnings.Add($"Вложения тикета {Short(t.Id)} не прочитаны: {ex.Message}"); }
        }
        return result;
    }

    // ── Содержимое ───────────────────────────────────────────────────────────

    public static string BuildMarkdown(Meta meta, IReadOnlyList<Ticket> tickets, IReadOnlyDictionary<string, List<Attachment>>? attachments = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Тикеты — Antarus ПО Finder");
        sb.AppendLine();
        sb.AppendLine($"- Выгружено: {meta.At:dd.MM.yyyy HH:mm}");
        sb.AppendLine($"- Кто выгрузил: {meta.User} ({meta.Role}), компьютер {meta.Machine}");
        sb.AppendLine($"- Версия программы: {meta.AppVersion}");
        sb.AppendLine($"- Отбор: {meta.ScopeLabel} — {tickets.Count} шт.");
        var attachmentCount = attachments?.Values.Sum(l => l.Count) ?? 0;
        if (attachmentCount > 0)
            sb.AppendLine($"- Вложения: {attachmentCount} шт. в папке attachments рядом с этим файлом");
        sb.AppendLine();

        if (tickets.Count == 0)
        {
            sb.AppendLine("Тикетов по этому отбору нет.");
            return sb.ToString();
        }

        var n = 0;
        foreach (var t in tickets)
        {
            n++;
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"## {n}. {Headline(t.Text)}");
            sb.AppendLine();
            sb.AppendLine($"- Тип: {TicketType.Label(t.Type)}");
            sb.AppendLine($"- Статус: {TicketStatus.Label(t.Status)}");
            sb.AppendLine($"- Автор: {t.CreatedBy} ({RoleLabel(t.CreatedByRole)})");
            sb.AppendLine($"- Создан: {DateLabel(t.CreatedAt)}" +
                          (string.Equals(t.UpdatedAt, t.CreatedAt, StringComparison.Ordinal) ? "" : $", изменён: {DateLabel(t.UpdatedAt)}"));
            sb.AppendLine($"- id: {t.Id}");
            if (attachments is not null && attachments.TryGetValue(t.Id, out var files))
            {
                sb.AppendLine($"- Вложения (attachments/{t.Id}/):");
                foreach (var f in files)
                    sb.AppendLine($"  - {f.FileName} — {SizeLabel(f.Size)}");
            }
            sb.AppendLine();
            sb.AppendLine("Текст:");
            sb.AppendLine();
            // Блоком кода, а не цитатой: в тикете попадаются пути, звёздочки и решётки, и как
            // markdown они выглядят не тем, что человек написал.
            sb.AppendLine("```");
            sb.AppendLine(t.Text.Replace("\r\n", "\n").TrimEnd());
            sb.AppendLine("```");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string BuildJson(Meta meta, IReadOnlyList<Ticket> tickets, IReadOnlyDictionary<string, List<Attachment>>? attachments = null)
    {
        var payload = new
        {
            exportedAt = meta.At.ToString("yyyy-MM-ddTHH:mm:ss"),
            appVersion = meta.AppVersion,
            machine = meta.Machine,
            user = meta.User,
            role = meta.Role,
            scope = meta.ScopeLabel,
            tickets = tickets.Select(t => new
            {
                id = t.Id,
                type = t.Type,
                typeLabel = TicketType.Label(t.Type),
                status = t.Status,
                statusLabel = TicketStatus.Label(t.Status),
                text = t.Text,
                createdBy = t.CreatedBy,
                createdByRole = t.CreatedByRole,
                createdAt = t.CreatedAt,
                updatedAt = t.UpdatedAt,
                attachments = attachments is not null && attachments.TryGetValue(t.Id, out var files)
                    ? files.Select(f => new { path = $"attachments/{t.Id}/{f.FileName}", name = f.FileName, size = f.Size }).ToArray()
                    : [],
            }).ToArray(),
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    // ── Мелочи ───────────────────────────────────────────────────────────────

    /// <summary>Первая строка текста — заголовка у тикета нет, а список из двадцати «## Баг» не
    /// читается. Обрезается по слову, чтобы в оглавлении не висело полтекста.</summary>
    public static string Headline(string text)
    {
        var line = (text ?? "").Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        if (line.Length == 0) return "(без текста)";
        if (line.Length <= 80) return line;
        var cut = line[..80];
        var space = cut.LastIndexOf(' ');
        return (space > 40 ? cut[..space] : cut) + "…";
    }

    public static string SizeLabel(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} КБ",
        _ => $"{bytes / (1024.0 * 1024):0.#} МБ",
    };

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    private static string DateLabel(string iso) =>
        DateTime.TryParse(iso, out var dt) ? dt.ToString("dd.MM.yyyy HH:mm") : iso;

    /// <summary>Роли известны ядру только идентификаторами (подписи живут в RolesConfig приложения),
    /// а выгрузку читают глазами — поэтому свой короткий перевод, с запасным вариантом «как есть».</summary>
    private static string RoleLabel(string role) => role switch
    {
        "administrator" => "администратор",
        "programmer" => "программист",
        "naladchik" => "наладчик",
        "" => "роль не указана",
        _ => role,
    };

    private static void WriteTextEntry(ZipArchive zip, string name, string content, bool withBom)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(withBom));
        writer.Write(content);
    }
}
