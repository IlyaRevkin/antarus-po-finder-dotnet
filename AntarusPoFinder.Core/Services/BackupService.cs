using System.IO.Compression;
using System.Text;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Infrastructure;

namespace AntarusPoFinder.Core.Services;

/// <summary>Куда и на каких условиях складывать копию. Собирается из настроек одним местом
/// (ConfigService.Backup()), чтобы «настроено ли резервное копирование» считалось одинаково и в
/// интерфейсе, и в фоновом тике.</summary>
public sealed record BackupTargets(
    string Folder,
    bool ToS3,
    string Password,
    int KeepDays,
    S3Settings S3)
{
    /// <summary>Папка задана. Пустая — штатное состояние «в папку не кладём», а не ошибка.</summary>
    public bool HasFolder => !string.IsNullOrWhiteSpace(Folder);

    /// <summary>Можно ли выкладывать копию в бакет. Три условия И, и среднее из них — главное: БЕЗ
    /// ПАРОЛЯ В S3 НЕ УХОДИТ НИЧЕГО. Бакет публичен на чтение (на этом держится QR с наклейки), то
    /// есть любой объект в нём открыт всему интернету; незашифрованная копия базы там — это выдача
    /// наружу всего каталога предприятия. Поэтому пустой пароль выключает выкладку целиком и молча,
    /// ровно как пустые ключи выключают выкладку инструкций (S3Settings.CanPublish): это штатное
    /// состояние «ещё не настроено», а не авария.</summary>
    public bool CanUploadToS3 => ToS3 && !string.IsNullOrEmpty(Password) && S3.CanPublish;

    /// <summary>Есть ли вообще смысл сегодня шевелиться.</summary>
    public bool Any => HasFolder || CanUploadToS3;
}

/// <summary>Чем кончилась попытка. Skipped — «делать было нечего/некуда», это не ошибка и в журнал
/// как ошибка не попадает.</summary>
public sealed record BackupOutcome(
    bool Ok,
    string? Error,
    string? FolderFile,
    bool Uploaded,
    long Bytes,
    bool Skipped = false)
{
    public static BackupOutcome Skip() => new(true, null, null, false, 0, Skipped: true);
    public static BackupOutcome Fail(string error) => new(false, error, null, false, 0);
}

/// <summary>Одна лежащая копия — строка списка «Показать копии». <paramref name="S3Key"/> не пуст
/// только у копий из бакета: по нему их можно скачать, чтобы распаковать тем же путём, что и
/// лежащие в папке.</summary>
public sealed record BackupEntry(string Name, string Location, DateTime TakenUtc, long Bytes,
    bool Encrypted, string? S3Key = null)
{
    /// <summary>Время показываем МЕСТНОЕ, хотя хранится и в имени файла оно UTC: имя файла читает
    /// код, а список — человек, и «копия от 06:15» должна совпадать с тем, что было на его часах.</summary>
    public string TakenLocal => TakenUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    public string Size => BackupArchive.HumanSize(Bytes);

    public string LockLabel => Encrypted ? "нужен" : "—";

    public bool InCloud => !string.IsNullOrEmpty(S3Key);
}

/// <summary>ЕЖЕСУТОЧНАЯ РЕЗЕРВНАЯ КОПИЯ.
///
/// Зачем вообще. У Finder'а копий не было ни одной, и каждый из трёх кусков состояния жил в
/// единственном экземпляре: общий конфиг — ОДИН файл на шаре, перезаписываемый при каждой отправке
/// (истории версий нет); база — своя у каждой машины, и «эталонная синхронизация» умеет затереть
/// записи у всех разом; в бакете лежат только инструкции для QR. То есть одна неудачная кнопка или
/// один сдохший диск — и восстанавливать неоткуда.
///
/// Что внутри — см. <see cref="BackupArchive"/>. Коротко: база этой машины и общий конфиг, если он
/// в этот момент доступен. Каталога прошивок нет и не будет.
///
/// Как себя ведёт. Молча и best effort, как вся остальная фоновая работа (правило проекта: службы
/// работают без всплывающих окон). Любая беда — строка в журнале и повтор завтра; окон, диалогов и
/// исключений наружу отсюда не вылетает. Копия, мешающая работать, перестаёт сниматься через
/// неделю — поэтому она не мешает.
///
/// Два адресата и почему они разные:
///   • ПАПКА — обычный zip, открывается двойным щелчком без программы и без пароля. Папка (сетевая
///     или локальная) — внутренний ресурс с собственными правами доступа, и в аварийный день
///     возможность открыть копию чем угодно стоит дороже, чем защита от коллеги, у которого и так
///     есть к ней доступ.
///   • S3 — ТОЛЬКО зашифрованная паролем человека (<see cref="ConfigFileCrypto.EncryptWithPassword"/>).
///     Бакет публичен на чтение; незашифрованная копия там читается из интернета кем угодно.</summary>
public sealed class BackupService
{
    private readonly S3Client _client;

    public BackupService(S3Client? client = null) => _client = client ?? new S3Client();

    /// <summary>Журнал. Отдельный файл рядом с профилем, как crash.log и update-check.log: фоновая
    /// работа обязана где-то оставлять след, иначе «почему копий нет» выясняется гаданием.</summary>
    public static string LogPath => Path.Combine(ConfigService.AppData, "backup.log");

    private const int LogKeepLines = 400;

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(ConfigService.AppData);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);

            // Подрезаем, чтобы журнал не рос вечно: раз в сутки это немного, но программа живёт годами.
            var lines = File.ReadAllLines(LogPath);
            if (lines.Length > LogKeepLines * 2)
                File.WriteAllLines(LogPath, lines[^LogKeepLines..], Encoding.UTF8);
        }
        catch { /* журнал, который мешает работать, хуже отсутствующего журнала */ }
    }

    /// <summary>Снять копию и разложить по адресатам. Блокирует поток на время VACUUM INTO и
    /// упаковки — вызывать только из Task.Run (см. MainWindowViewModel.RunDailyBackupAsync).</summary>
    public async Task<BackupOutcome> RunAsync(
        Database db,
        string machine,
        string? sharedConfigPath,
        BackupTargets targets,
        CancellationToken ct = default)
    {
        if (!targets.Any) return BackupOutcome.Skip();

        var takenAt = DateTime.UtcNow;
        var staging = Path.Combine(Path.GetTempPath(), "antarus-backup-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(staging);

            // 1. База — ТОЛЬКО через VACUUM INTO, см. Database.BackupTo (копия .db файлом из-под
            //    работающего приложения даёт битый снимок: часть транзакций лежит в WAL).
            var dbCopy = Path.Combine(staging, BackupArchive.DbEntryName);
            db.BackupTo(dbCopy);
            var dbBytes = new FileInfo(dbCopy).Length;

            // 2. Общий конфиг — если доступен. Недоступная шара это обычное дело (ноутбук вне
            //    офиса), и копия базы без конфига куда лучше, чем отсутствие копии вовсе.
            long configBytes = 0;
            var configSource = "не доступен";
            if (!string.IsNullOrWhiteSpace(sharedConfigPath))
            {
                try
                {
                    if (File.Exists(sharedConfigPath))
                    {
                        var target = Path.Combine(staging, BackupArchive.ConfigEntryName);
                        File.Copy(sharedConfigPath, target);
                        configBytes = new FileInfo(target).Length;
                        configSource = sharedConfigPath;
                    }
                }
                catch (Exception ex)
                {
                    configSource = "ошибка чтения: " + ex.Message;
                }
            }

            // 3. Манифест — чтобы через полгода было понятно, что это за файл и как его разворачивать.
            var manifest = new BackupManifest(
                TakenAt: takenAt.ToString("O"),
                Machine: machine,
                AppVersion: AppVersionString(),
                Beta: AppIdentity.IsBeta,
                DbBytes: dbBytes,
                ConfigBytes: configBytes,
                ConfigSource: configSource);
            File.WriteAllText(Path.Combine(staging, BackupArchive.ManifestEntryName), manifest.ToJson(), Encoding.UTF8);

            // 4. Упаковка. В памяти: содержимое — база с метаданными и текстовый конфиг, десятки
            //    мегабайт в худшем случае (прошивок внутри нет), а дальше те же байты нужны сразу
            //    двум адресатам, и читать файл по второму разу незачем.
            byte[] zip;
            using (var buffer = new MemoryStream())
            {
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                    foreach (var file in Directory.GetFiles(staging))
                        archive.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
                zip = buffer.ToArray();
            }

            string? folderFile = null;
            string? folderError = null;
            if (targets.HasFolder)
            {
                try { folderFile = WriteToFolder(targets, machine, takenAt, zip); }
                catch (Exception ex) { folderError = "папка: " + ex.Message; }
            }

            var uploaded = false;
            string? s3Error = null;
            if (targets.CanUploadToS3)
            {
                var encrypted = ConfigFileCrypto.EncryptWithPassword(zip, targets.Password);
                var put = await _client.PutBytesAsync(targets.S3,
                    BackupArchive.ObjectKey(machine, takenAt), encrypted,
                    "application/octet-stream", ct);
                uploaded = put.Ok;
                if (!put.Ok) s3Error = "хранилище: " + (put.Error ?? "не удалось выложить копию");
                else await PruneS3Async(targets, machine, ct);
            }

            // Удача, если ХОТЬ ОДИН адресат принял копию: отметка «сегодня сделано» не должна
            // срываться из-за недоступного бакета, когда копия в папке уже лежит.
            var ok = folderFile is not null || uploaded;
            var error = string.Join("; ", new[] { folderError, s3Error }.Where(e => e is not null));

            Log(ok
                ? $"копия снята: {BackupArchive.HumanSize(zip.Length)}" +
                  (folderFile is not null ? $", папка: {folderFile}" : "") +
                  (uploaded ? ", хранилище: выложена (зашифрована)" : "") +
                  (error.Length > 0 ? $"; частично не удалось — {error}" : "")
                : $"копия НЕ снята: {(error.Length > 0 ? error : "адресаты не приняли")}");

            return new BackupOutcome(ok, error.Length > 0 ? error : null, folderFile, uploaded, zip.Length);
        }
        catch (Exception ex)
        {
            Log("копия НЕ снята: " + ex.Message);
            return BackupOutcome.Fail(ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static string WriteToFolder(BackupTargets targets, string machine, DateTime takenAt, byte[] zip)
    {
        var dir = MachineFolder(targets.Folder, machine);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, BackupArchive.FileName(takenAt, encrypted: false));
        File.WriteAllBytes(path, zip);
        PruneFolder(dir, targets.KeepDays);
        return path;
    }

    /// <summary>Подпапка по имени машины — см. BackupArchive.MachineKey, там же и почему она вообще
    /// нужна. Папка названа тем же ключом, что и папка в бакете: один и тот же компьютер должен
    /// узнаваться в обоих местах одинаково.</summary>
    public static string MachineFolder(string folder, string machine) =>
        Path.Combine(folder, BackupArchive.MachineKey(machine));

    private static void PruneFolder(string dir, int keepDays)
    {
        try
        {
            var names = Directory.GetFiles(dir).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!);
            foreach (var name in BackupArchive.ToPrune(names, keepDays, DateTime.UtcNow))
            {
                try { File.Delete(Path.Combine(dir, name)); }
                catch { /* занят антивирусом — переживём, завтра попробуем снова */ }
            }
        }
        catch { /* папка исчезла или недоступна — не повод валить уже снятую копию */ }
    }

    private async Task PruneS3Async(BackupTargets targets, string machine, CancellationToken ct)
    {
        try
        {
            var page = await _client.ListAsync(targets.S3, BackupArchive.MachineFolder(machine), grouped: false, ct: ct);
            if (!page.Ok) return;
            foreach (var key in BackupArchive.ToPrune(page.Objects.Select(o => o.Key), targets.KeepDays, DateTime.UtcNow))
                await _client.DeleteAsync(targets.S3, key, ct);
        }
        catch { /* уборка — не повод считать неудачной саму выкладку */ }
    }

    // ── Что уже лежит ─────────────────────────────────────────────────────────────────────────

    /// <summary>Копии в папке. Чужие файлы (не наши имена) в список не попадают — их мы не делали и
    /// за них не отвечаем.</summary>
    public static IReadOnlyList<BackupEntry> ListFolder(string folder, string machine)
    {
        var dir = MachineFolder(folder, machine);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(dir)) return Array.Empty<BackupEntry>();

        try
        {
            return Directory.GetFiles(dir)
                .Select(path => (path, taken: BackupArchive.ParseTaken(path)))
                .Where(x => x.taken.HasValue)
                .Select(x => new BackupEntry(
                    Path.GetFileName(x.path), x.path, x.taken!.Value, SafeLength(x.path),
                    Encrypted: Path.GetExtension(x.path) == BackupArchive.EncryptedExtension))
                .OrderByDescending(e => e.TakenUtc)
                .ToList();
        }
        catch { return Array.Empty<BackupEntry>(); }
    }

    /// <summary>Копии в бакете. Отдельным вызовом, а не вместе с папкой: сеть может молчать минутами,
    /// и список локальных копий не должен этого ждать.</summary>
    public async Task<IReadOnlyList<BackupEntry>> ListS3Async(S3Settings s3, string machine, CancellationToken ct = default)
    {
        if (!s3.CanPublish) return Array.Empty<BackupEntry>();
        try
        {
            var page = await _client.ListAsync(s3, BackupArchive.MachineFolder(machine), grouped: false, ct: ct);
            if (!page.Ok) return Array.Empty<BackupEntry>();
            return page.Objects
                .Select(o => (o, taken: BackupArchive.ParseTaken(o.Key)))
                .Where(x => x.taken.HasValue)
                .Select(x => new BackupEntry(
                    Path.GetFileName(x.o.Key.Replace('/', Path.DirectorySeparatorChar)),
                    "хранилище на хостинге: " + x.o.Key, x.taken!.Value, x.o.Size,
                    Encrypted: true, S3Key: x.o.Key))
                .OrderByDescending(e => e.TakenUtc)
                .ToList();
        }
        catch { return Array.Empty<BackupEntry>(); }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    // ── Распаковать рядом ─────────────────────────────────────────────────────────────────────

    /// <summary>Скачать копию из бакета во временную папку, чтобы её можно было распаковать тем же
    /// путём, что и лежащую в папке. Отдельным шагом, а не внутри распаковки: скачивание идёт по
    /// сети и может занять минуту, а распаковка — мгновенная операция над файлом, и мешать их в
    /// одном методе значит заставлять интерфейс ждать неизвестно чего.</summary>
    public async Task<(string? File, string? Error)> DownloadAsync(
        S3Settings s3, string key, CancellationToken ct = default)
    {
        var (bytes, error) = await _client.GetBytesAsync(s3, key, ct);
        if (bytes is null) return (null, error ?? "копии с таким именем в хранилище нет");

        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "antarus-backup-download");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, Path.GetFileName(key.Replace('/', Path.DirectorySeparatorChar)));
            File.WriteAllBytes(file, bytes);
            return (file, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public sealed record ExtractResult(bool Ok, string? Error, string? Folder);

    /// <summary>РАСПАКОВАТЬ КОПИЮ В ОТДЕЛЬНУЮ ПАПКУ. Намеренно не «восстановить»: класть базу поверх
    /// работающей программы одной кнопкой — это способ потерять и то, что ещё оставалось. Здесь
    /// копия только раскладывается рядом; что с ней делать дальше, решает человек, и порядок описан
    /// в манифесте внутри (BackupArchive.RestoreHint) и в docs/backups.md.
    ///
    /// Зашифрованный файл (из бакета) расшифровывается тем же паролем, которым был зашифрован.
    /// Не тот пароль — внятный отказ, а не «битый архив».</summary>
    public static ExtractResult ExtractBeside(string backupFile, string password, string? destination = null)
    {
        try
        {
            if (!File.Exists(backupFile)) return new ExtractResult(false, "Файл копии не найден", null);

            var bytes = File.ReadAllBytes(backupFile);
            if (ConfigFileCrypto.IsPasswordEncrypted(bytes))
            {
                var plain = ConfigFileCrypto.TryDecryptWithPassword(bytes, password);
                if (plain is null)
                    return new ExtractResult(false,
                        "Копия зашифрована, и этим паролем она не открывается. Нужен тот пароль, который стоял в настройках в день, когда копия снималась.",
                        null);
                bytes = plain;
            }

            var dir = destination ?? Path.Combine(
                Path.GetDirectoryName(backupFile) ?? Path.GetTempPath(),
                Path.GetFileNameWithoutExtension(backupFile) + "-распаковано");
            Directory.CreateDirectory(dir);

            using var buffer = new MemoryStream(bytes);
            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // папка
                // Имена внутри архива кладём мы сами, но путь всё равно проверяем: архив мог быть
                // подменён в папке, а распаковка «..\..\» — это запись куда угодно.
                var target = Path.GetFullPath(Path.Combine(dir, entry.Name));
                if (!target.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    continue;
                entry.ExtractToFile(target, overwrite: true);
            }

            return new ExtractResult(true, null, dir);
        }
        catch (Exception ex)
        {
            return new ExtractResult(false, ex.Message, null);
        }
    }

    private static string AppVersionString()
    {
        try
        {
            return System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?";
        }
        catch { return "?"; }
    }
}
