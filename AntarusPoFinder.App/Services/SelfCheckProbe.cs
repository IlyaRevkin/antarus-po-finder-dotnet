using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Services;

/// <summary>Собирает снимок машины для <see cref="SelfCheckAnalyzer"/> — и НИЧЕГО не решает сам.
/// Здесь только то, что физически нельзя сделать в Core: реестр текущего пользователя, права
/// запуска процесса, разворот буквы диска в сетевой адрес (mpr.dll) и походы на диск/в сеть с
/// таймаутом. Весь разбор — в Core, там он и покрыт тестами.
///
/// Ничего не бросает: проверка обязана довести до конца даже на машине, где половина обращений
/// падает, — иначе она бесполезна ровно тогда, когда нужна.</summary>
public static class SelfCheckProbe
{
    /// <summary>Тот же таймаут, что и у «Состояния подключения»: отвалившаяся SMB-шара отвечает
    /// секундами, а ждать неотвечающую дольше смысла нет.</summary>
    public static readonly TimeSpan DefaultTimeout = ConnectionStatusService.DefaultTimeout;

    /// <param name="db">null — окно открыли до загрузки базы (из окна входа); тогда проверка путей
    /// молча пропускается, а не выдумывает цифры.</param>
    public static async Task<SelfCheckFacts> CollectAsync(ConfigService cfg, Database? db, string appUser, TimeSpan timeout)
    {
        var root = cfg.RootPath().Trim();
        var secondDisk = cfg.SecondDiskPath().Trim();

        // Всё, что ходит наружу, стартует разом: последовательный запуск на трёх неотвечающих целях
        // сложил бы три таймаута подряд, а человек всё это время смотрел бы в пустое окно.
        var rootTask = ProbeFolderAsync(root, timeout);
        var secondTask = ProbeFolderAsync(secondDisk, timeout);
        var authTask = ConnectionStatusService.CheckAuthTargetAsync(cfg, timeout);
        var updatesTask = AppUpdateService.ProbeSourcesAsync(cfg.EffectiveAppUpdatePath(), timeout);
        var syncTask = ProbeSyncAsync(cfg, root, timeout);

        await Task.WhenAll(rootTask, secondTask, authTask, updatesTask, syncTask);

        var rootProbe = rootTask.Result;
        var secondProbe = secondTask.Result;
        var auth = authTask.Result;
        var updates = updatesTask.Result;
        var sync = syncTask.Result;

        var s3 = cfg.S3();
        var mapped = MappedDriveFor(root);

        // «Офисная сеть доступна» = дозвонились хоть куда-то внутрь конторы. Именно этим отличается
        // «наладчик вне офиса» (нормально, предупреждение) от «настроено противоречиво» (проблема).
        // GitHub сюда НЕ входит намеренно: с телефонного модема он открывается прекрасно, а до
        // конторы связи при этом нет никакой.
        bool? officeNetwork = null;
        var officeTargets = new List<bool>();
        if (root.Length > 0) officeTargets.Add(rootProbe.Exists);
        if (secondDisk.Length > 0) officeTargets.Add(secondProbe.Exists);
        if (auth.State != ConnectionState.NotConfigured) officeTargets.Add(auth.State == ConnectionState.Ok);
        if (sync.Configured) officeTargets.Add(sync.Reachable);
        if (officeTargets.Count > 0) officeNetwork = officeTargets.Any(x => x);

        var storedPaths = StoredPathAuditResult.Empty;
        var storedChecked = false;
        if (db is not null)
        {
            try
            {
                storedPaths = StoredPathAudit.Audit(db.GetStoredDiskPathGroups(), root);
                storedChecked = true;
            }
            catch
            {
                // База заблокирована другим процессом/повреждена — остальные проверки от этого
                // терять смысла не должны, поэтому просто «не проверялись».
            }
        }

        return new SelfCheckFacts
        {
            AppVersion = AppUpdateService.CurrentVersionText,
            MachineName = System.Environment.MachineName,
            WindowsUser = System.Environment.UserName,
            AppUser = appUser,
            RoleLabel = RolesConfig.RoleLabel(cfg.CurrentRole()),
            Elevated = IsElevated(),

            RootPath = root,
            RootKind = KindOf(root),
            RootExists = rootProbe.Exists,
            RootReadable = rootProbe.Readable,
            RootError = rootProbe.Error,
            RootUnc = UncBehind(root),
            RootMappedInSession = mapped,

            SecondDiskPath = secondDisk,
            SecondDiskExists = secondProbe.Exists,

            OfficeNetworkReachable = officeNetwork,
            AuthTarget = auth.Target,
            AuthDetails = auth.Details,
            AuthConfigured = auth.State != ConnectionState.NotConfigured,
            AuthReachable = auth.State == ConnectionState.Ok,

            StoredPaths = storedPaths,
            StoredPathsChecked = storedChecked,

            UpdatePathLocal = cfg.AppUpdatePath().Trim(),
            UpdatePathShared = cfg.AppUpdatePathShared().Trim(),
            UpdatePathEffective = cfg.EffectiveAppUpdatePath().Trim(),
            UpdateFolderReachable = updates.Folder is { Configured: true, Available: true },
            UpdateFolderProblem = updates.Folder.Problem ?? "",
            GitHubReachable = updates.GitHub.Available,
            GitHubProblem = updates.GitHub.Problem ?? "",
            UpdateAutoInstall = cfg.AppAutoUpdate(),
            LastUpdateFailure = LastUpdateFailure(),

            SyncTransport = cfg.SyncTransport(),
            SyncTarget = sync.Target,
            SyncReachable = sync.Reachable,
            SyncDetails = sync.Details,

            StorageEnabled = s3.Enabled,
            StorageHasAddress = s3.HasAddress,
            StorageHasCredentials = s3.HasCredentials,
            StorageTarget = s3.HasAddress ? $"{s3.Endpoint}/{s3.Bucket}" : "",
        };
    }

    // ── Диск ─────────────────────────────────────────────────────────────────

    private record FolderProbe(bool Exists, bool Readable, string Error);

    /// <summary>Существует ли папка и читается ли её содержимое. Второе отдельно от первого:
    /// «папка есть, а прав на чтение нет» — совсем другой разговор с ИТ, чем «папки нет», а
    /// Directory.Exists эти случаи не различает.</summary>
    private static Task<FolderProbe> ProbeFolderAsync(string path, TimeSpan timeout)
    {
        if (path.Length == 0) return Task.FromResult(new FolderProbe(false, false, ""));

        return WithTimeoutAsync(() =>
        {
            try
            {
                if (!Directory.Exists(path)) return new FolderProbe(false, false, "");
                try
                {
                    // Достаточно первой записи: перечислять целиком сетевую папку с тысячами файлов
                    // ради ответа «читается ли» — дорого и незачем. Пустая папка тоже читается.
                    _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToList();
                    return new FolderProbe(true, true, "");
                }
                catch (Exception ex)
                {
                    return new FolderProbe(true, false, ex.Message);
                }
            }
            catch (Exception ex)
            {
                return new FolderProbe(false, false, ex.Message);
            }
        }, timeout, () => new FolderProbe(false, false, $"не ответила за {timeout.TotalSeconds:0} с"));
    }

    private static DiskAttachKind KindOf(string path)
    {
        if (path.Length == 0) return DiskAttachKind.NotConfigured;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return DiskAttachKind.Unc;
        return path.Length >= 2 && path[1] == ':' ? DiskAttachKind.DriveLetter : DiskAttachKind.NotConfigured;
    }

    private static string UncBehind(string path)
    {
        if (KindOf(path) != DiskAttachKind.DriveLetter) return "";
        return NetworkPathHelper.TryResolveUnc(path) ?? "";
    }

    /// <summary>Числится ли буква корня подключённой в СЕАНСЕ Windows этого пользователя.
    ///
    /// ⚠️ Именно здесь ловится вторая живая версия жалобы «в проводнике диск виден, а программа его
    /// не находит»: подключённые буквы принадлежат маркеру доступа, и процесс, запущенный с
    /// повышенными правами, чужие (обычного сеанса) подключения НЕ видит — Directory.Exists("Z:\…")
    /// возвращает false при том, что диск на месте. Поэтому список берётся не из DriveInfo и не из
    /// WNetGetConnection (они отвечают про ТЕКУЩИЙ маркер и в этом случае соврут), а из ветки
    /// реестра самого пользователя: HKCU одна на оба маркера, и запись о постоянном подключении
    /// видна из процесса с любыми правами.
    ///
    /// Ограничение честное: непостоянное подключение (net use без /persistent:yes) в реестре не
    /// оседает, и тогда вернётся null — проверка просто не сможет назвать причину так же уверенно,
    /// но соседняя ветка разбора «запущено от администратора + путь буквой» это подхватит.</summary>
    private static MappedDrive? MappedDriveFor(string rootPath)
    {
        if (KindOf(rootPath) != DiskAttachKind.DriveLetter) return null;
        var letter = rootPath[..2].ToUpperInvariant(); // «Z:»

        try
        {
            using var network = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Network");
            using var drive = network?.OpenSubKey(letter[..1]);
            var remote = drive?.GetValue("RemotePath") as string;
            return string.IsNullOrWhiteSpace(remote) ? null : new MappedDrive(letter, remote.Trim());
        }
        catch
        {
            // Доступа к ветке нет / политика запрещает — считаем, что сказать нечего.
            return null;
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // ── Канал обмена ─────────────────────────────────────────────────────────

    private record SyncProbe(bool Configured, bool Reachable, string Target, string Details);

    private static async Task<SyncProbe> ProbeSyncAsync(ConfigService cfg, string root, TimeSpan timeout)
    {
        if (cfg.SyncTransport() == "server")
        {
            var settings = cfg.SyncServer();
            if (!settings.IsConfigured)
                return new SyncProbe(false, false, "", "Адрес службы обмена не задан.");
            var probe = await SyncServerProbe.CheckAsync(settings);
            return new SyncProbe(true, probe.Ok, settings.Root, probe.Message);
        }

        if (root.Length == 0) return new SyncProbe(false, false, "", "");

        var dir = Path.GetDirectoryName(ConfigSyncService.ConfigPathFor(root)) ?? root;
        var folder = await ProbeFolderAsync(dir, timeout);
        return new SyncProbe(true, folder.Exists, dir, "");
    }

    // ── Обновления: почему не сработало в прошлый раз ────────────────────────

    /// <summary>Последняя записанная неудача проверки обновлений. Журнал ведётся давно
    /// (AppUpdateService.LogSourceFailure), но прочитать его до сих пор мог только тот, кто знает
    /// про этот файл, — а вопрос «почему обновление не пришло в прошлый раз» задают все.</summary>
    private static string LastUpdateFailure()
    {
        try
        {
            var path = AppUpdateService.UpdateCheckLogPath;
            if (!File.Exists(path)) return "";
            // Файл открыт на дозапись самим приложением — читаем с общим доступом, иначе на живой
            // машине эта строчка сама себе и была бы ошибкой.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? last = null;
            while (reader.ReadLine() is { } line)
                if (line.Trim().Length > 0) last = line.Trim();
            return last ?? "";
        }
        catch
        {
            return "";
        }
    }

    // ── Мелочь ───────────────────────────────────────────────────────────────

    /// <summary>Блокирующая проверка в фоне с жёстким таймаутом. Сама зависшая операция остаётся
    /// висеть в пуле потоков — прервать системный вызов к неотвечающей шаре нельзя в принципе, — но
    /// окно её больше не ждёт, а это единственное, что требуется (тот же приём, что и в
    /// ConnectionStatusService.RunWithTimeoutAsync).</summary>
    private static async Task<T> WithTimeoutAsync<T>(Func<T> work, TimeSpan timeout, Func<T> onTimeout)
    {
        var task = Task.Run(work);
        var finished = await Task.WhenAny(task, Task.Delay(timeout));
        if (finished != task) return onTimeout();
        try { return await task; }
        catch { return onTimeout(); }
    }
}
