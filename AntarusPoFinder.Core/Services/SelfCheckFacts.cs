namespace AntarusPoFinder.Core.Services;

/// <summary>Как рабочий диск задан в настройках. Различать нужно ровно потому, что от этого зависит
/// диагноз: путь буквой (Z:\…) ломается и от чужой буквы в базе, и от запуска с правами
/// администратора; путь по UNC (\\сервер\шара\…) от обоих защищён.</summary>
public enum DiskAttachKind
{
    /// <summary>Путь не задан.</summary>
    NotConfigured,
    /// <summary>Буква диска: «Z:\Software\…».</summary>
    DriveLetter,
    /// <summary>Сетевой адрес: «\\ant_srv\Software\…».</summary>
    Unc,
}

/// <summary>Буква, которая числится подключённой в СЕАНСЕ Windows этого пользователя, и к чему.
/// Берётся из его собственной ветки реестра, а не из списка видимых процессу дисков, — в этом весь
/// смысл: процесс с повышенными правами подключённых букв не видит, а запись о них в сеансе есть.</summary>
public sealed record MappedDrive(string Letter, string RemotePath);

/// <summary>Снимок машины: всё, что удалось узнать про окружение, БЕЗ единого вывода о том, хорошо
/// это или плохо. Выводы делает <see cref="SelfCheckAnalyzer"/> — так вся смысловая часть проверки
/// оказывается чистой функцией над этой записью и покрывается тестами без окна, диска и сети
/// (собирает снимок App-овый SelfCheckProbe: реестр, права запуска и WNetGetConnection — вещи
/// сугубо виндовые и в тесте не воспроизводимые).</summary>
public sealed record SelfCheckFacts
{
    // ── Кто и где ────────────────────────────────────────────────────────────

    public string AppVersion { get; init; } = "";
    public string MachineName { get; init; } = "";
    public string WindowsUser { get; init; } = "";
    /// <summary>Кем человек значится в программе (AD-логин, если вход был через AD).</summary>
    public string AppUser { get; init; } = "";
    /// <summary>Роль в программе — «Наладчик», «Администратор» и т.п., уже подписью.</summary>
    public string RoleLabel { get; init; } = "";

    /// <summary>Программа запущена «от имени администратора». Само по себе не ошибка, но у Windows
    /// на этот счёт есть известная подлость — см. SelfCheckAnalyzer.</summary>
    public bool Elevated { get; init; }

    // ── Рабочий диск ─────────────────────────────────────────────────────────

    public string RootPath { get; init; } = "";
    public DiskAttachKind RootKind { get; init; } = DiskAttachKind.NotConfigured;
    /// <summary>Папка нашлась.</summary>
    public bool RootExists { get; init; }
    /// <summary>Содержимое папки удалось прочитать. Отдельно от <see cref="RootExists"/>: «папка
    /// есть, а прав на чтение нет» — совсем другой разговор с ИТ, чем «папки нет».</summary>
    public bool RootReadable { get; init; }
    /// <summary>Текст системной ошибки, если она была. Показывается только вместе с человеческим
    /// объяснением, никогда вместо него.</summary>
    public string RootError { get; init; } = "";
    /// <summary>Сетевой адрес за буквой диска, если корень задан буквой и её удалось развернуть.</summary>
    public string RootUnc { get; init; } = "";
    /// <summary>Буква корня числится подключённой в сеансе Windows — даже если процесс её не видит.
    /// null — не буква, либо в сеансе такой записи нет.</summary>
    public MappedDrive? RootMappedInSession { get; init; }

    public string SecondDiskPath { get; init; } = "";
    public bool SecondDiskExists { get; init; }

    /// <summary>Дозвонились ли хоть куда-то в офисную сеть (домен/сервер входа/сетевой диск).
    /// null — не проверяли. false — не дозвонились никуда, и тогда недоступный диск это не поломка,
    /// а «человек не в офисе» или «VPN ещё не поднялся». Ради этого различия поле и существует.</summary>
    public bool? OfficeNetworkReachable { get; init; }

    /// <summary>Что показала проверка цели входа (домен/веб-сервер) — фраза уже человеческая.</summary>
    public string AuthTarget { get; init; } = "";
    public string AuthDetails { get; init; } = "";
    public bool AuthConfigured { get; init; }
    public bool AuthReachable { get; init; }

    // ── Пути в базе ──────────────────────────────────────────────────────────

    public StoredPathAuditResult StoredPaths { get; init; } = StoredPathAuditResult.Empty;
    /// <summary>Базы не было (окно открыли до её загрузки) — проверку путей молча пропустили.</summary>
    public bool StoredPathsChecked { get; init; }

    // ── Обновления ───────────────────────────────────────────────────────────

    /// <summary>app_update_path — локальный перебив этой машины.</summary>
    public string UpdatePathLocal { get; init; } = "";
    /// <summary>app_update_path_shared — общий путь, приезжающий синхронизацией на все машины.</summary>
    public string UpdatePathShared { get; init; } = "";
    /// <summary>Что из этого получилось на ЭТОЙ машине (UpdateFolderResolver.Resolve).</summary>
    public string UpdatePathEffective { get; init; } = "";
    public bool UpdateFolderReachable { get; init; }
    public string UpdateFolderProblem { get; init; } = "";
    public bool GitHubReachable { get; init; }
    public string GitHubProblem { get; init; } = "";
    /// <summary>Настройка «Устанавливать найденные обновления автоматически при запуске».</summary>
    public bool UpdateAutoInstall { get; init; }
    /// <summary>Последняя записанная неудача из update-check.log — ответ на «почему не сработало в
    /// прошлый раз», который иначе виден только тому, кто знает про этот файл.</summary>
    public string LastUpdateFailure { get; init; } = "";

    /// <summary>Папка, в которой лежит запущенный .exe — та самая, куда самоустановка обязана
    /// перезаписать себя (AppUpdateService.InstallAndRestart копирует «*.update» рядом и переносит
    /// поверх оригинала). Пусто — путь определить не удалось, тогда проверку прав молча пропускаем.</summary>
    public string InstallDir { get; init; } = "";
    /// <summary>В папку с .exe удалось создать и удалить файл — значит самоустановка сможет
    /// перезаписать себя. false + непустой <see cref="InstallDir"/> — вот она, тихая причина
    /// «обновление не ставится само, приходится руками»: источник цел, версия найдена, а записать
    /// некуда.</summary>
    public bool InstallDirWritable { get; init; }
    /// <summary>Текст системной ошибки записи, если она была — приложением к человеческому объяснению.</summary>
    public string InstallDirWriteError { get; init; } = "";
    /// <summary>.exe лежит под Program Files — самая частая причина «некуда писать»: штатная установка
    /// идёт per-user в %LocalAppData% (installer/Package.wxs), а в Program Files .exe попадает, только
    /// если портативную сборку скопировали туда руками. Меняет совет по починке, поэтому — отдельным
    /// фактом.</summary>
    public bool InstallUnderProgramFiles { get; init; }

    // ── Синхронизация конфига и тикетов ──────────────────────────────────────

    /// <summary>«fileshare» — общая папка на диске, «server» — служба обмена.</summary>
    public string SyncTransport { get; init; } = "fileshare";
    public string SyncTarget { get; init; } = "";
    public bool SyncReachable { get; init; }
    /// <summary>Готовая человеческая фраза от проверки канала (у службы обмена её выдаёт
    /// SyncServerProbe, и переписывать её здесь незачем).</summary>
    public string SyncDetails { get; init; } = "";

    // ── Хранилище на хостинге ────────────────────────────────────────────────

    public bool StorageEnabled { get; init; }
    public bool StorageHasAddress { get; init; }
    public bool StorageHasCredentials { get; init; }
    public string StorageTarget { get; init; } = "";
}
