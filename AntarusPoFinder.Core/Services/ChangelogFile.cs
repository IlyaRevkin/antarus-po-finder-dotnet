using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что удалось вычитать из CHANGELOG.md рядом с прошивкой. Пустые поля означают «в файле
/// этого не было» — вызывающий сам решает, чем их заменить.</summary>
public record ChangelogContent(string Description, List<string> LaunchTypes)
{
    /// <summary>Теги версии, если загрузившая машина их записала. Пустой список — файл старого
    /// формата (строки «Теги:» в нём нет) либо тегов не было.</summary>
    public List<string> Tags { get; init; } = new();

    /// <summary>РЕШЕНИЕ МОДЕРАЦИИ, записанное на диск. null — в файле строки «Модерация:» нет вовсе,
    /// и это НЕ то же самое, что «в очереди»: файл мог быть написан старой версией программы, и
    /// принять её молчание за «не выпущена» значило бы снимать чужие решения.</summary>
    public FwModerationMark? Moderation { get; init; }

    /// <summary>РУЧНЫЕ ЗАМЕНЫ ФАЙЛА ПРОШИВКИ в этой версии, от старой к новой. Пустой список —
    /// замен не было (или файл написан версией программы, которая про них не знала).</summary>
    public List<FwFileReplacement> Replacements { get; init; } = new();
}

/// <summary>Одна ручная замена файла прошивки в УЖЕ ВЫПУЩЕННОЙ версии: кто, когда, какой файл
/// заменил и как называется сохранённый прежний.
///
/// Живёт на диске, в CHANGELOG.md папки версии, по той же причине, что и отметка модерации выше:
/// общий диск — единственный канал, который между машинами точно работает, а замена файла в чужой
/// выпущенной версии — ровно то событие, про которое коллега обязан узнать независимо от того,
/// доехал ли до него обмен конфигом.</summary>
public record FwFileReplacement(string At, string By, string File, string Backup);

/// <summary>Отметка модерации из CHANGELOG.md: выпущена ли версия, не в архиве ли она, когда и кем
/// решено.
///
/// Зачем она на диске. Состояние модерации ездило между машинами ТОЛЬКО обменом конфига, а он между
/// двумя конкретными машинами может не работать месяцами и молча: каталоги при этом выглядят
/// одинаковыми, потому что обе машины насканировали их с общего диска сами. Так и вышло — 14.10.2026
/// по слепкам двух машин: 46 общих прошивок, из них у 28 РАЗНЫЕ sync_id, и 16 версий, выпущенных на
/// одной машине, на второй всё ещё висели в очереди модерации. Жалоба дословно: «у коллеги 21
/// прошивок на модерации висит а у меня 4».
///
/// Общий диск — единственный канал, который между этими машинами ТОЧНО работает: файлы прошивок они
/// видят одни и те же. Поэтому решение едет там же, где уже едут описание, тип пуска и теги.</summary>
public record FwModerationMark(bool Released, bool Archived, string At, string By);

/// <summary>CHANGELOG.md, который кладётся в папку каждой версии при загрузке — единственный носитель
/// описания и типов пуска, который живёт НА ДИСКЕ, а не только в локальной базе конкретной машины.
///
/// Почему это важно: fw_versions синхронизируются между машинами двумя независимыми путями —
/// config-обменом (полная запись со всеми полями) и сканированием диска (HierarchyService.
/// SyncFwFromDisk, которое видит только папки). Раньше второй путь вставлял строку с заглушкой
/// «(синхронизировано с диска)» вместо описания, и поскольку config-импорт additive-only и НЕ трогает
/// уже существующую локальную строку, эта заглушка оставалась навсегда — ровно то, на что жаловались:
/// «прошивки, что я добавил на другом компе, появляются с описанием "синхронизировано с диска"».
/// Теперь сканирование диска читает описание и типы пуска из CHANGELOG.md, т.е. из того же источника,
/// который записала загрузившая машина.</summary>
public static class ChangelogFile
{
    public const string FileName = "CHANGELOG.md";

    /// <summary>Заглушка, которой SyncFwFromDisk помечает строки, для которых на диске не нашлось
    /// CHANGELOG.md. Публичная, потому что config-импорт обязан считать такое описание «пустым» и
    /// разрешить входящему настоящему описанию его перезаписать (см. ImportHierarchyDataCore).</summary>
    public const string DiskSyncPlaceholder = "(синхронизировано с диска)";

    public static void Write(string versionFolder, FwVersionNumber fwv, IEnumerable<string> launchTypes, string description,
        IEnumerable<string>? tags = null)
    {
        var lines = new List<string>
        {
            $"# {fwv.Raw}",
            $"Дата: {fwv.DtStr}",
            $"Тип пуска: {string.Join(", ", launchTypes)}",
        };
        // Теги — тот же случай, что описание и тип пуска: без них версия, приехавшая к коллеге
        // сканированием диска (а не через конфиг администратора), находилась бы только по названию
        // папки. Разделитель «; », а не пробел: тег может быть из нескольких слов (см. TagList).
        var tagList = (tags ?? Enumerable.Empty<string>()).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        if (tagList.Count > 0)
            lines.Add($"Теги: {string.Join("; ", tagList)}");
        if (!string.IsNullOrEmpty(description))
        {
            lines.Add("");
            lines.Add(description);
        }
        File.WriteAllText(Path.Combine(versionFolder, FileName), string.Join("\n", lines), new UTF8Encoding(false));
    }

    /// <summary>Строка версии из заголовка «# …» — единственное место НА ДИСКЕ, где номер версии
    /// записан отдельно от имени папки. До этапа 5 это было не нужно (имя папки и есть номер), но у
    /// ОПЦ имя папки теперь — номер заявки/SN, и номер версии восстанавливается отсюда (см.
    /// OpcLayout.ResolveVersion). Разбор снисходительный к тому же, к чему и TryRead: заголовок ищется
    /// среди первых строк, а не строго в первой — файл люди правят руками.
    ///
    /// null — файла нет, он нечитаем или заголовка в нём не оказалось.</summary>
    public static string? TryReadVersionRaw(string versionFolder)
    {
        string[] lines;
        try { lines = File.ReadAllLines(Path.Combine(versionFolder, FileName)); }
        catch { return null; }

        foreach (var line in lines.Take(10))
        {
            if (!line.StartsWith("# ", StringComparison.Ordinal)) continue;
            var value = line[2..].Trim();
            if (value.Length > 0) return value;
        }
        return null;
    }

    /// <summary>Возвращает null, если файла нет или он нечитаем — вызывающий откатывается на заглушку.
    /// Разбор намеренно снисходительный: CHANGELOG.md люди правят руками прямо на сетевом диске, и
    /// потерять описание из-за лишней пустой строки или переставленных местами шапочных полей было бы
    /// хуже, чем принять слегка неканоничный файл.</summary>
    public static ChangelogContent? TryRead(string versionFolder)
    {
        string[] lines;
        try { lines = File.ReadAllLines(Path.Combine(versionFolder, FileName)); }
        catch { return null; }

        var launchTypes = new List<string>();
        var tags = new List<string>();
        var descLines = new List<string>();
        var replacements = new List<FwFileReplacement>();
        FwModerationMark? moderation = null;
        bool inBody = false;

        foreach (var line in lines)
        {
            if (!inBody)
            {
                if (line.StartsWith("# ", StringComparison.Ordinal)) continue;
                if (line.StartsWith("Дата:", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("Тип пуска:", StringComparison.OrdinalIgnoreCase))
                {
                    launchTypes.AddRange(line["Тип пуска:".Length..]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    continue;
                }
                if (line.StartsWith("Теги:", StringComparison.OrdinalIgnoreCase))
                {
                    tags.AddRange(line["Теги:".Length..]
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    continue;
                }
                if (line.StartsWith(ModerationPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    moderation = ParseModeration(line[ModerationPrefix.Length..]);
                    continue;
                }
                if (line.StartsWith(ReplacementPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    // Строк замены может быть СКОЛЬКО УГОДНО, в отличие от одной строки модерации:
                    // там источник истины — последнее решение, здесь каждая строка это отдельное
                    // событие, и затирать предыдущую означало бы терять историю ровно того, ради
                    // чего запись и заводилась.
                    if (ParseReplacement(line[ReplacementPrefix.Length..]) is { } replacement)
                        replacements.Add(replacement);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(line)) continue;
                inBody = true;
            }
            descLines.Add(line);
        }

        // Хвостовые пустые строки в описание не тащим — иначе повторная запись файла давала бы
        // «другое» описание при каждом круге чтение→запись.
        while (descLines.Count > 0 && string.IsNullOrWhiteSpace(descLines[^1]))
            descLines.RemoveAt(descLines.Count - 1);

        return new ChangelogContent(string.Join("\n", descLines).Trim(), launchTypes)
        {
            Tags = tags,
            Moderation = moderation,
            Replacements = replacements,
        };
    }

    /// <summary>Шапка строки о ручной замене файла прошивки. Отдельной константой по той же причине,
    /// что и <see cref="ModerationPrefix"/>: по ней идёт и разбор, и дописывание.</summary>
    public const string ReplacementPrefix = "Замена файла:";

    /// <summary>Строка вида «Замена файла: 2026-11-16T12:10:00; Киселёв; 1.1.0004.0005.lfs;
    /// сохранён: Предыдущие\1.1.0004.0005_20261116_1210.lfs.old». Разбор снисходительный к тому же,
    /// к чему и остальной файл: потерянная дата не должна отменять сам факт замены, поэтому
    /// отсутствующие части остаются пустыми, а строка всё равно считается записью.</summary>
    private static FwFileReplacement? ParseReplacement(string tail)
    {
        var parts = tail.Split(';', StringSplitOptions.TrimEntries);
        // Хотя бы что-то должно быть написано: пустая строка «Замена файла:» не говорит ничего и
        // выдавать её за событие нельзя.
        if (parts.Length == 0 || parts.All(string.IsNullOrWhiteSpace)) return null;

        var backup = parts.Length > 3 ? parts[3] : "";
        if (backup.StartsWith(BackupWord, StringComparison.OrdinalIgnoreCase))
            backup = backup[BackupWord.Length..].Trim();

        return new FwFileReplacement(
            parts.Length > 0 ? parts[0] : "",
            parts.Length > 1 ? parts[1] : "",
            parts.Length > 2 ? parts[2] : "",
            backup);
    }

    private const string BackupWord = "сохранён:";

    private static string FormatReplacement(FwFileReplacement r) =>
        $"{ReplacementPrefix} {r.At}; {r.By}; {r.File}; {BackupWord} {r.Backup}";

    /// <summary>Дописать в CHANGELOG.md запись о ручной замене файла прошивки. Строка встаёт в
    /// шапку, после уже имеющихся шапочных полей и прошлых замен, — описание при этом не
    /// разрывается.
    ///
    /// ⚠️ ФАЙЛА НЕТ — СОЗДАЁМ, в отличие от <see cref="TryMarkModeration"/>, которая в таком случае
    /// молчит. Решение разное, потому что разная цена ошибки: пропущенная отметка модерации
    /// доедет следующим обменом конфига, а запись «кто подменил файл на боевом сервере» взять
    /// больше неоткуда — её никто не восстановит. Пустое описание ничего не ломает: сканирование
    /// диска подставляет вместо него свою заглушку (см. HierarchyService), а вот замену без следа
    /// разбирать потом будет некому.
    ///
    /// false — записать не удалось (нет прав на шаре, диск отвалился). Вызывающий решает сам; саму
    /// замену файла из-за этого откатывать не надо, но сказать человеку — надо.</summary>
    public static bool TryAppendReplacement(string versionFolder, FwFileReplacement replacement)
    {
        var path = Path.Combine(versionFolder, FileName);
        List<string> lines;
        try
        {
            lines = File.Exists(path)
                ? File.ReadAllLines(path).ToList()
                : new List<string> { "# " + Path.GetFileName(versionFolder.TrimEnd(Path.DirectorySeparatorChar)) };
        }
        catch { return false; }

        // Место вставки — сразу после последней шапочной строки (включая прошлые замены и отметку
        // модерации), то есть перед пустой строкой и телом описания.
        var at = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!IsHeadLine(lines[i])
                && !lines[i].StartsWith(ModerationPrefix, StringComparison.OrdinalIgnoreCase)
                && !lines[i].StartsWith(ReplacementPrefix, StringComparison.OrdinalIgnoreCase)) break;
            at = i + 1;
        }
        lines.Insert(at, FormatReplacement(replacement));

        try { File.WriteAllText(path, string.Join("\n", lines), new UTF8Encoding(false)); }
        catch { return false; }
        return true;
    }

    /// <summary>Шапка строки с решением модерации. Отдельной константой, потому что по ней идёт и
    /// разбор, и замена строки при повторной записи.</summary>
    public const string ModerationPrefix = "Модерация:";

    private const string WordReleased = "выпущена";
    private const string WordQueued = "в очереди";
    private const string WordArchived = "в архиве";

    /// <summary>Строка вида «Модерация: выпущена; 2026-10-05T12:00:00; Иванов». Разбор снисходительный
    /// к тому же, к чему и остальной файл: люди правят CHANGELOG.md руками прямо на сетевом диске, и
    /// потерянная дата не должна отменять само решение.</summary>
    private static FwModerationMark? ParseModeration(string tail)
    {
        var parts = tail.Split(';', StringSplitOptions.TrimEntries);
        var state = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        if (state.Length == 0) return null;

        var released = state.Contains(WordReleased, StringComparison.Ordinal);
        var archived = state.Contains(WordArchived, StringComparison.Ordinal);
        // Ни одного знакомого слова — файл правили руками и написали что-то своё. Выдумывать за
        // человека решение модерации нельзя: считаем, что отметки нет.
        if (!released && !archived && !state.Contains(WordQueued, StringComparison.Ordinal)) return null;

        return new FwModerationMark(released, archived,
            parts.Length > 1 ? parts[1] : "",
            parts.Length > 2 ? parts[2] : "");
    }

    /// <summary>Состояния пишутся ВМЕСТЕ, а не одно вместо другого: «выпущена» и «в архиве» —
    /// независимые признаки, и версия вполне бывает и той, и другой сразу. Пока писалось одно слово,
    /// архивирование затирало отметку о выпуске, и на другой машине версия снова всплывала в очереди
    /// модерации — то есть ровно та беда, от которой этот канал и заводился. Разбор обе эти пометки
    /// и так ищет по отдельности (см. ParseModeration), так что формат никуда не «расширяется»,
    /// просто перестаёт терять половину решения.</summary>
    private static string FormatModeration(FwModerationMark m)
    {
        var words = new List<string>();
        if (m.Released) words.Add(WordReleased);
        if (m.Archived) words.Add(WordArchived);
        var state = words.Count > 0 ? string.Join(", ", words) : WordQueued;
        return $"{ModerationPrefix} {state}; {m.At}; {m.By}".TrimEnd(';', ' ');
    }

    /// <summary>Записать решение модерации В ПАПКУ ВЕРСИИ, рядом с самой прошивкой.
    ///
    /// Перезаписывается ТОЛЬКО строка «Модерация:»: всё остальное в файле — описание, тип пуска, теги,
    /// ручные правки инженеров — остаётся как было. Строка ставится в шапку, сразу после последнего
    /// шапочного поля, чтобы не разрывать текст описания.
    ///
    /// Если файла нет вовсе, он НЕ создаётся: пустой CHANGELOG с одной строкой модерации выглядел бы
    /// как потерянное описание. Возвращает false — вызывающий решает, беда это или нет (на чужой
    /// шаре может не быть прав на запись, и ронять из-за этого модерацию нельзя).</summary>
    public static bool TryMarkModeration(string versionFolder, FwModerationMark mark)
    {
        var path = Path.Combine(versionFolder, FileName);
        string[] lines;
        try
        {
            if (!File.Exists(path)) return false;
            lines = File.ReadAllLines(path);
        }
        catch { return false; }

        var next = new List<string>();
        var written = false;
        var inHead = true;
        foreach (var line in lines)
        {
            if (line.StartsWith(ModerationPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // Своя же строка с прошлого раза — заменяем на месте, чтобы файл не обрастал
                // историей решений: источник истины один, последнее решение.
                next.Add(FormatModeration(mark));
                written = true;
                inHead = false;
                continue;
            }
            if (inHead && !written && !IsHeadLine(line))
            {
                next.Add(FormatModeration(mark));
                written = true;
                inHead = false;
            }
            next.Add(line);
        }
        if (!written) next.Add(FormatModeration(mark));

        try { File.WriteAllText(path, string.Join("\n", next), new UTF8Encoding(false)); }
        catch { return false; }
        return true;
    }

    private static bool IsHeadLine(string line) =>
        line.StartsWith("# ", StringComparison.Ordinal) ||
        line.StartsWith("Дата:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Тип пуска:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Теги:", StringComparison.OrdinalIgnoreCase);
}
