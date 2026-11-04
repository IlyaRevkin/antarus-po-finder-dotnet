using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что лежит в копии — файлом <c>manifest.json</c> внутри архива. Нужен не программе, а
/// человеку, который через полгода откроет архив и должен понять, что это такое, какой машиной и
/// какой версией программы снято и что с этим делать. Поэтому поля — текстом, а не кодами.</summary>
public sealed record BackupManifest(
    [property: JsonPropertyName("takenAt")] string TakenAt,
    [property: JsonPropertyName("machine")] string Machine,
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("beta")] bool Beta,
    [property: JsonPropertyName("dbBytes")] long DbBytes,
    [property: JsonPropertyName("configBytes")] long ConfigBytes,
    [property: JsonPropertyName("configSource")] string ConfigSource,
    [property: JsonPropertyName("howToRestore")] string HowToRestore = BackupArchive.RestoreHint)
{
    public string ToJson() => JsonSerializer.Serialize(this,
        new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

/// <summary>РАСКЛАДКА И ИМЕНА РЕЗЕРВНЫХ КОПИЙ. Отдельно от того, кто их делает и куда кладёт:
/// имена и правило «сколько держать» читают и люди (глазами в папке), и код удаления старого, и
/// разойтись эти два прочтения не имеют права — отсюда и тесты именно на этот класс.
///
/// Решение по составу архива принято здесь и записано в <see cref="RestoreHint"/>: внутри ровно две
/// вещи плюс манифест —
///   • <see cref="DbEntryName"/> — база ЭТОЙ машины, снятая через VACUUM INTO (см. Database.BackupTo);
///   • <see cref="ConfigEntryName"/> — общий конфиг с сетевого диска, если он в этот момент доступен.
/// Каталога прошивок в копии НЕТ и не будет: это десятки гигабайт чужой интеллектуальной
/// собственности, и в публично читаемом бакете им не место ни в каком виде. Копируется то, что
/// нигде больше не лежит в единственном экземпляре, — метаданные и настройки.</summary>
public static class BackupArchive
{
    /// <summary>Имя файла базы внутри архива. Совпадает с рабочим (po_finder.db) намеренно: при
    /// восстановлении его кладут обратно под тем же именем, и лишний шаг «переименовать» — это
    /// лишний шанс ошибиться в неудачный день.</summary>
    public const string DbEntryName = "po_finder.db";

    public const string ConfigEntryName = "po_finder_config.json";

    public const string ManifestEntryName = "manifest.json";

    /// <summary>Порядок восстановления руками, вложенный в сам архив. Копия, к которой не приложена
    /// инструкция, в нужный момент всегда оказывается копией, которую никто не знает, как
    /// разворачивать. Подробнее — docs/backups.md.</summary>
    public const string RestoreHint =
        "1) Закрыть Antarus ПО Finder полностью (проверить в диспетчере задач). " +
        "2) Папку профиля %LocalAppData%\\AntarusPOFinder переименовать в ...-old — НЕ удалять. " +
        "3) Создать папку заново и положить в неё po_finder.db из этого архива. " +
        "4) Запустить программу и проверить поиск и список прошивок. " +
        "5) po_finder_config.json — это общий конфиг с сетевого диска на момент копии; " +
        "класть его в Конфиг\\ на диске только осознанно, он перетрёт общий для всех.";

    /// <summary>Расширение копии в папке — обычный zip. Намеренно: папка это внутренний ресурс, и
    /// копия, которую можно открыть двойным щелчком без программы и пароля, в аварии стоит дороже,
    /// чем защита от коллеги, у которого и так есть доступ к этой папке.</summary>
    public const string PlainExtension = ".zip";

    /// <summary>Расширение копии, зашифрованной паролем (см. ConfigFileCrypto.EncryptWithPassword).
    /// Своё, не .zip: файл, который не открывается архиватором, обязан и выглядеть как не-архив,
    /// иначе первое же «у меня битый зип» будет потрачено впустую.</summary>
    public const string EncryptedExtension = ".apofbak";

    /// <summary>Верхняя папка копий — и в выбранной папке, и в бакете. Своя, не вперемешку с
    /// инструкциями и слепками: у копий другой срок жизни и другой смысл.</summary>
    public const string RootFolder = "backup";

    public const int DefaultKeepDays = 30;

    private const string Stamp = "yyyyMMdd_HHmmss";

    /// <summary>Имя файла копии: <c>po-finder-20261104_031500.zip</c>. Время — UTC, как и у слепков
    /// диска: копии разных машин складываются рядом, и два разных представления о «сейчас» в одной
    /// папке — это готовая путаница в том, какая из них свежее.</summary>
    public static string FileName(DateTime takenAtUtc, bool encrypted) =>
        $"po-finder-{takenAtUtc.ToString(Stamp, CultureInfo.InvariantCulture)}" +
        (encrypted ? EncryptedExtension : PlainExtension);

    /// <summary>Имя машины так, как оно попадает в путь, — и в ключ бакета, и в имя подпапки.
    ///
    /// База у каждого компьютера СВОЯ, поэтому разделение по машинам здесь не удобство, а условие
    /// правильности: свалившись в одну кучу, копии трёх компьютеров выглядели бы как история одного,
    /// уборка старого считала бы их общей очередью, а «восстановить вчерашнюю» подставило бы чужую
    /// базу.
    ///
    /// Правила — те же, что у слепка диска (<see cref="DiskSnapshotStorage.SanitizeMachine"/>):
    /// только латиница, цифры, дефис и подчёркивание, потому что путь должен одинаково годиться и
    /// для ключа в бакете, и для имени папки на любой файловой системе.
    ///
    /// С ОДНОЙ ДОБАВКОЙ, и она здесь обязательна. Имя компьютера человек задаёт как хочет, и в
    /// конторе оно запросто целиком кириллическое («КОМП-ИЛЬИ»): такое имя чистится в пустоту, и у
    /// слепка диска превращается в общее «unknown». Слепку это ничем не грозит — он перетирается и
    /// нужен поштучно. Резервным копиям грозит прямо: ВСЕ машины с кириллическими именами сложили бы
    /// копии в одну папку «unknown» и чистили бы там общую очередь, то есть выносили бы копии друг
    /// друга. Поэтому, когда чистить нечего, в имя идёт короткий устойчивый отпечаток исходного
    /// имени: он у каждой машины свой и не меняется от запуска к запуску.</summary>
    public static string MachineKey(string? machine)
    {
        var sanitized = DiskSnapshotStorage.SanitizeMachine(machine);
        if (sanitized != "unknown") return sanitized;

        var original = (machine ?? "").Trim();
        if (original.Length == 0) return "unknown";

        // FNV-1a: нужна не криптография, а стабильные восемь знаков, одинаковые на этой машине
        // сегодня и через год (string.GetHashCode рандомизирован между запусками и не годится).
        uint hash = 2166136261;
        foreach (var ch in original)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return "pc-" + hash.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>Ключ объекта в бакете.</summary>
    public static string ObjectKey(string machine, DateTime takenAtUtc) =>
        $"{RootFolder}/{MachineKey(machine)}/{FileName(takenAtUtc, encrypted: true)}";

    public static string MachineFolder(string machine) => $"{RootFolder}/{MachineKey(machine)}/";

    /// <summary>Вытащить из имени файла момент снятия. null — имя не наше: такие файлы в папке копий
    /// не трогаем вовсе (см. <see cref="ToPrune"/>), потому что положить в эту папку что-то своё
    /// руками человек имеет полное право, а автоматика, удаляющая чужое, — нет.</summary>
    public static DateTime? ParseTaken(string fileNameOrKey)
    {
        var name = fileNameOrKey.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];

        if (!name.StartsWith("po-finder-", StringComparison.Ordinal)) return null;
        var rest = name["po-finder-".Length..];
        if (rest.Length < Stamp.Length) return null;
        var stamp = rest[..Stamp.Length];

        var tail = rest[Stamp.Length..];
        if (tail != PlainExtension && tail != EncryptedExtension) return null;

        return DateTime.TryParseExact(stamp, Stamp, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var taken)
            ? taken
            : null;
    }

    /// <summary>Какие копии пора удалить. Правило — ПО ВОЗРАСТУ, а не «держать последние N штук»:
    /// машина, которую две недели не включали, при счёте штук выбросила бы всю историю первым же
    /// запуском, а по возрасту она просто донесёт то, что есть.
    ///
    /// Самая свежая копия не удаляется НИКОГДА, даже если по возрасту уже должна: остаться совсем
    /// без копии хуже, чем держать одну старую. Это тот случай, когда буквальное исполнение правила
    /// противоречит смыслу правила.
    ///
    /// Имена, которые не разбираются (<see cref="ParseTaken"/>), не трогаются вовсе.</summary>
    public static IReadOnlyList<string> ToPrune(IEnumerable<string> names, int keepDays, DateTime nowUtc)
    {
        if (keepDays <= 0) return Array.Empty<string>(); // 0 — «не чистить»; см. ConfigService.BackupKeepDays

        var known = names
            .Select(n => (Name: n, Taken: ParseTaken(n)))
            .Where(x => x.Taken.HasValue)
            .Select(x => (x.Name, Taken: x.Taken!.Value))
            .OrderByDescending(x => x.Taken)
            .ToList();
        if (known.Count <= 1) return Array.Empty<string>();

        var cutoff = nowUtc - TimeSpan.FromDays(keepDays);
        return known
            .Skip(1) // самую свежую не трогаем ни при каких условиях
            .Where(x => x.Taken < cutoff)
            .Select(x => x.Name)
            .ToList();
    }

    /// <summary>Размер по-человечески — для списка копий. Своя, а не чужая утилита из UI-слоя:
    /// список копий читают и из диалога, и из журнала.</summary>
    public static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} МБ",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} ГБ",
    };
}
