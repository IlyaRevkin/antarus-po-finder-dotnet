using System.Collections.Generic;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Насколько всё плохо. Между <see cref="Warning"/> и <see cref="Problem"/> проходит
/// главная граница этой проверки: «недоступно, и это ожидаемо» против «настроено противоречиво».
/// У наладчика вне офиса сетевой диск недоступен всегда, и красить это в красный — значит приучить
/// смотреть мимо настоящих отказов; ровно поэтому предложение завести тикет появляется только на
/// <see cref="Problem"/>.</summary>
public enum SelfCheckSeverity
{
    /// <summary>Работает.</summary>
    Ok,
    /// <summary>Не настроено / проверять нечего. Не отказ.</summary>
    Info,
    /// <summary>Не работает, но объяснимо: нет сети, выключено человеком. Тикет не нужен.</summary>
    Warning,
    /// <summary>Не работает и не должно: настройки противоречат друг другу или окружению.</summary>
    Problem,
}

/// <param name="Title">Что проверяли — «Рабочий диск», «Пути в базе», …</param>
/// <param name="Target">Куда смотрели: путь, адрес. Пусто, если смотреть было некуда.</param>
/// <param name="Reason">Что происходит и ПОЧЕМУ — человеческим языком, а не «Error 0x80070005».</param>
/// <param name="Fix">Что сделать. Пусто — делать нечего (всё хорошо либо от человека тут ничего
/// не зависит).</param>
public sealed record SelfCheckFinding(
    string Title,
    SelfCheckSeverity Severity,
    string Target,
    string Reason,
    string Fix = "");

/// <summary>Весь разбор снимка машины (<see cref="SelfCheckFacts"/>) в одном месте и без единого
/// обращения к диску, сети и реестру — чистая функция, которую можно прогнать тестом на любой
/// выдуманной машине.
///
/// Проверки отвечают на живые жалобы, а не на абстрактное «всё ли хорошо»:
/// <list type="bullet">
/// <item>«диск в проводнике виден, а программа его не находит» — две разные причины (чужая буква в
/// базе и запуск от имени администратора), которые надо уметь различать;</item>
/// <item>«прошивки открываются, а параметры нет» — <see cref="StoredPathAudit"/>;</item>
/// <item>«обновления не ставятся сами» — правило <see cref="UpdateFolderResolver"/>, доступность
/// папки и отдельно галочка автоустановки.</item>
/// </list></summary>
public static class SelfCheckAnalyzer
{
    public static IReadOnlyList<SelfCheckFinding> Analyze(SelfCheckFacts f)
    {
        var findings = new List<SelfCheckFinding>
        {
            Disk(f),
            Elevation(f),
            StoredPaths(f),
            UpdateSource(f),
            AutoInstall(f),
            InstallLocation(f),
        };
        var history = UpdateHistory(f);
        if (history is not null) findings.Add(history);
        findings.Add(Sync(f));
        findings.Add(Auth(f));
        findings.Add(SecondDisk(f));
        findings.Add(Storage(f));
        return findings;
    }

    public static bool HasProblems(IEnumerable<SelfCheckFinding> findings) =>
        findings.Any(x => x.Severity == SelfCheckSeverity.Problem);

    public static string SeverityLabel(SelfCheckSeverity severity) => severity switch
    {
        SelfCheckSeverity.Ok => "[ ОК ]",
        SelfCheckSeverity.Warning => "[ ! ]",
        SelfCheckSeverity.Problem => "[ ПРОБЛЕМА ]",
        _ => "[ — ]",
    };

    /// <summary>«До офисной сети не достучались вообще» — единственное основание смягчить отказ до
    /// предупреждения. null (не проверяли) намеренно НЕ смягчает: молча прощать отказ на основании
    /// того, чего мы не знаем, — это ровно тот способ пропустить поломку, ради которого вся проверка
    /// и затевалась.</summary>
    private static bool NetworkDown(SelfCheckFacts f) => f.OfficeNetworkReachable == false;

    private static SelfCheckSeverity Expected(SelfCheckFacts f) =>
        NetworkDown(f) ? SelfCheckSeverity.Warning : SelfCheckSeverity.Problem;

    private const string OutOfOffice =
        "До офисной сети сейчас не достучаться ни одним способом — вне офиса и пока не поднялся VPN это нормально, а не поломка.";

    // ── Рабочий диск ─────────────────────────────────────────────────────────

    private static SelfCheckFinding Disk(SelfCheckFacts f)
    {
        const string title = "Рабочий диск";

        if (f.RootPath.Length == 0)
            return new SelfCheckFinding(title, SelfCheckSeverity.Info, "",
                "Путь к рабочему диску не задан — программе неоткуда брать прошивки и параметры.",
                "Настройки → Подключение → «Корень рабочего диска».");

        var how = f.RootKind switch
        {
            DiskAttachKind.DriveLetter when f.RootUnc.Length > 0 => $"подключён буквой диска (за ней {f.RootUnc})",
            DiskAttachKind.DriveLetter => "подключён буквой диска",
            DiskAttachKind.Unc => "подключён сетевым адресом напрямую, без буквы диска",
            _ => "локальная папка",
        };

        if (f.RootExists && f.RootReadable)
            return new SelfCheckFinding(title, SelfCheckSeverity.Ok, f.RootPath, $"Доступен и читается, {how}.");

        if (f.RootExists)
            return new SelfCheckFinding(title, SelfCheckSeverity.Problem, f.RootPath,
                $"Папка находится, но её содержимое не читается{Because(f.RootError)}. Обычно это значит, что учётной записи не выдали прав на эту сетевую папку.",
                $"Показать это ИТ: пользователю Windows «{f.WindowsUser}» нужен доступ на чтение к {f.RootPath}.");

        // ── Папки нет. Дальше — за что именно её нет, и это главный вопрос всей проверки. ──

        var mapped = f.RootMappedInSession;

        // Классика Windows, и вторая живая версия жалобы «в проводнике диск есть, программа не
        // видит»: буквы, подключённые в обычном сеансе, процессу с повышенными правами НЕ видны —
        // у него отдельная таблица подключений. Diagnostics ловит это тем, что список подключённых
        // букв берётся из ветки реестра самого пользователя, а не из видимых процессу дисков.
        if (mapped is not null && f.Elevated)
            return new SelfCheckFinding(title, Expected(f), f.RootPath,
                $"Программа запущена от имени администратора, а буква {mapped.Letter} подключена в вашем обычном сеансе Windows (к {mapped.RemotePath}). " +
                "Windows специально не показывает такие диски программам с повышенными правами — поэтому в проводнике диск виден, а программа его не находит." +
                // Сети нет вовсе — значит диск не нашёлся бы и без повышенных прав, и утверждать,
                // что дело именно в них, нельзя: это было бы ровно та ложная тревога, которой здесь
                // не место. Но сказать про права всё равно надо — иначе после подъёма VPN человек
                // упрётся в то же самое и начнёт разбираться с нуля.
                (NetworkDown(f) ? " " + OutOfOffice + " Пока сети нет, точную причину не различить — проверьте снова в офисе." : ""),
                "Закройте программу и запустите её обычным двойным щелчком, без «Запуск от имени администратора» " +
                "(если так настроен сам ярлык — снимите галочку в его свойствах, вкладка «Совместимость»). " +
                $"Если запускать нужно именно с правами администратора, впишите в настройках вместо {mapped.Letter} сетевой адрес {mapped.RemotePath} — он виден в любом режиме.");

        if (f.Elevated && f.RootKind == DiskAttachKind.DriveLetter)
            return new SelfCheckFinding(title, Expected(f), f.RootPath,
                $"Папка {f.RootPath} не найдена. Программа запущена от имени администратора, а путь задан буквой диска — " +
                "в этом режиме Windows не показывает программе сетевые диски, подключённые в обычном сеансе. Это самая частая причина «в проводнике диск есть, а программа его не видит»." +
                (NetworkDown(f) ? " " + OutOfOffice + " Пока сети нет, точную причину не различить — проверьте снова в офисе." : ""),
                "Запустите программу без «Запуск от имени администратора». Если не поможет — замените в настройках букву на сетевой адрес вида \\\\сервер\\шара: он работает в любом режиме.");

        if (mapped is not null)
            return new SelfCheckFinding(title, Expected(f), f.RootPath,
                $"Буква {mapped.Letter} числится подключённой к {mapped.RemotePath}, но папка сейчас недоступна. " +
                (NetworkDown(f) ? OutOfOffice : "Сеть при этом работает — значит дело в самом сервере или в правах доступа."),
                NetworkDown(f) ? "" : $"Проверьте у ИТ, открывается ли {mapped.RemotePath} с этого компьютера.");

        if (NetworkDown(f))
            return new SelfCheckFinding(title, SelfCheckSeverity.Warning, f.RootPath,
                $"Папка {f.RootPath} не найдена. {OutOfOffice}",
                "Подключитесь к сети предприятия и нажмите «Проверить снова».");

        return new SelfCheckFinding(title, SelfCheckSeverity.Problem, f.RootPath,
            $"Папка {f.RootPath} не найдена{Because(f.RootError)}, хотя сеть работает. Либо диск на этом компьютере не подключён, либо путь в настройках указывает не туда, либо нет прав.",
            $"Откройте {f.RootPath} в проводнике. Открывается — приложите этот отчёт к тикету: путь верный, а программа его не видит. " +
            "Не открывается — подключите диск заново или укажите в настройках сетевой адрес вида \\\\сервер\\шара.");
    }

    // ── Права запуска ────────────────────────────────────────────────────────

    private static SelfCheckFinding Elevation(SelfCheckFacts f)
    {
        const string title = "Права запуска";

        if (!f.Elevated)
            return new SelfCheckFinding(title, SelfCheckSeverity.Ok, "",
                "Программа запущена с обычными правами — сетевые диски, подключённые в вашем сеансе Windows, ей видны.");

        if (!f.RootExists && f.RootKind == DiskAttachKind.DriveLetter)
            return new SelfCheckFinding(title, SelfCheckSeverity.Warning, "",
                "Программа запущена от имени администратора, и рабочий диск задан буквой — скорее всего, отсюда все остальные отказы. См. пункт «Рабочий диск».",
                "Перезапустить без «Запуск от имени администратора».");

        return new SelfCheckFinding(title, SelfCheckSeverity.Info, "",
            "Программа запущена от имени администратора. Сейчас на работу это не влияет, но в таком режиме Windows не показывает программе сетевые диски, подключённые буквой, — если диск однажды «пропадёт», начинать разбираться надо отсюда.",
            "Прав администратора программе не требуется — обычный запуск надёжнее.");
    }

    // ── Пути в базе ──────────────────────────────────────────────────────────

    private static SelfCheckFinding StoredPaths(SelfCheckFacts f)
    {
        const string title = "Пути к файлам в базе";
        var a = f.StoredPaths;

        if (!f.StoredPathsChecked)
            return new SelfCheckFinding(title, SelfCheckSeverity.Info, "", "База не открыта — пути не проверялись.");
        if (a.Records == 0)
            return new SelfCheckFinding(title, SelfCheckSeverity.Info, "", "В базе пока нет записей с путями к файлам.");
        if (f.RootPath.Length == 0)
            return new SelfCheckFinding(title, SelfCheckSeverity.Info, "",
                $"Записей с путями: {a.Records}. Корень рабочего диска не задан, сравнивать не с чем.",
                "Настройки → Подключение → «Корень рабочего диска».");

        var roots = StoredPathAudit.DescribeRoots(a.ForeignRoots);

        if (a.Broken > 0)
            return new SelfCheckFinding(title, SelfCheckSeverity.Problem, f.RootPath,
                $"Записей всего {a.Records}, из них {a.Foreign} записаны с чужого корня ({roots}). " +
                $"{a.Rescued} программа приводит к вашему диску сама, а {a.Broken} — не может: в пути нет папки «ПО» или «Параметры», по которой определяется корень, " +
                $"и он откроется дословно — на диске, которого на этой машине нет. Пример такого пути: {a.BrokenSample}",
                "Показать администратору: эти записи заливали с машины, где диск подключён иначе, и лежат они мимо обычной раскладки. " +
                "Лечится перезаливкой этих файлов через обычную загрузку — тогда путь встанет на место и приведётся к любому диску.");

        if (a.Foreign > 0)
            return new SelfCheckFinding(title, SelfCheckSeverity.Ok, f.RootPath,
                $"Записей всего {a.Records}, из них {a.Foreign} записаны с чужого корня ({roots}) — и это штатно: " +
                "пути пишутся в базу абсолютными, с буквой той машины, которая файл заливала. " +
                $"Все {a.Foreign} программа приводит к вашему корню {f.RootPath} сама. Делать ничего не нужно.");

        return new SelfCheckFinding(title, SelfCheckSeverity.Ok, f.RootPath,
            $"Все {a.Records} записей указывают на ваш корень диска.");
    }

    // ── Обновления ───────────────────────────────────────────────────────────

    /// <summary>Какое из трёх правил <see cref="UpdateFolderResolver"/> сработало на этой машине —
    /// то, что в жалобе «обновления не приходят» выясняется первым, а сегодня не видно нигде.</summary>
    public static string UpdateRuleText(SelfCheckFacts f)
    {
        if (f.UpdatePathLocal.Length > 0) return "папка обновлений этой машины (перебивает общую)";
        if (f.UpdatePathShared.Length == 0) return "GitHub — папка обновлений не настроена";
        if (f.UpdatePathEffective.Length == 0) return "общая папка задана относительным путём, но корень диска не задан — разворачивать не от чего";
        return string.Equals(f.UpdatePathEffective, f.UpdatePathShared, System.StringComparison.OrdinalIgnoreCase)
            ? "общая папка, записанная абсолютным путём — берётся как есть"
            : "общая папка, развёрнутая от корня рабочего диска этой машины";
    }

    private static SelfCheckFinding UpdateSource(SelfCheckFacts f)
    {
        const string title = "Откуда приходят обновления";
        var rule = UpdateRuleText(f);

        if (f.UpdatePathEffective.Length == 0)
        {
            // Общий путь есть, а развернуть его не от чего — обновления молча идут мимо папки.
            if (f.UpdatePathShared.Length > 0)
                return new SelfCheckFinding(title, SelfCheckSeverity.Problem, f.UpdatePathShared,
                    $"Общая папка обновлений задана относительным путём «{f.UpdatePathShared}», но корень рабочего диска на этой машине не задан — развернуть его не от чего, и обновления идут мимо папки.",
                    "Указать корень рабочего диска: Настройки → Подключение.");

            if (f.GitHubReachable)
                return new SelfCheckFinding(title, SelfCheckSeverity.Ok, "GitHub",
                    "Папка обновлений не настроена, обновления берутся с GitHub — он доступен.");

            return new SelfCheckFinding(title, Expected(f), "GitHub",
                $"Папка обновлений не настроена, а GitHub недоступен{Because(f.GitHubProblem)}. Новые версии на эту машину не придут никак." +
                (NetworkDown(f) ? " " + OutOfOffice : ""),
                "Администратору — задать общую папку обновлений (Настройки → Общие): тогда обновления пойдут с сетевого диска и GitHub не понадобится.");
        }

        if (f.UpdateFolderReachable)
            return new SelfCheckFinding(title, SelfCheckSeverity.Ok, f.UpdatePathEffective,
                $"Правило: {rule}. Папка доступна.");

        // Гипотеза, ради которой это писалось: общий путь уехал синхронизацией АБСОЛЮТНЫМ, с буквой
        // той машины, где его задавали. Настройка одна на всех, а буква у каждого своя — и на всех
        // машинах, кроме исходной, папка обновлений просто не находится.
        var sharedRoot = StoredPathAudit.RootOf(f.UpdatePathShared);
        var localRoot = StoredPathAudit.RootOf(f.RootPath);
        var foreignShared = f.UpdatePathLocal.Length == 0
            && sharedRoot.Length > 0
            && localRoot.Length > 0
            && !string.Equals(sharedRoot, localRoot, System.StringComparison.OrdinalIgnoreCase);

        if (foreignShared)
            return new SelfCheckFinding(title, SelfCheckSeverity.Problem, f.UpdatePathEffective,
                $"Общая папка обновлений записана абсолютным путём {f.UpdatePathShared} — с корнем {sharedRoot}. " +
                $"Эта настройка приезжает синхронизацией на все машины дословно, а рабочий диск здесь подключён как {localRoot}: " +
                $"корня {sharedRoot} на этом компьютере нет, и в папку обновлений программа не попадает. " +
                (f.GitHubReachable
                    ? "Пока выручает GitHub — но обновления берутся не оттуда, откуда задумано, и в день, когда GitHub закроют, они перестанут приходить молча."
                    : $"GitHub тоже недоступен{Because(f.GitHubProblem)} — новые версии на эту машину не придут никак."),
                "Администратору — переписать общую папку ОТНОСИТЕЛЬНО корня диска (например просто «Обновления»): такой путь разворачивается на каждой машине от её собственного корня, независимо от буквы. " +
                "Как быстрое решение на этом компьютере — заполнить «Папка обновлений этой машины», она перебивает общую.");

        if (f.UpdatePathLocal.Length > 0)
            return new SelfCheckFinding(title, SelfCheckSeverity.Problem, f.UpdatePathLocal,
                $"На этой машине задана своя папка обновлений {f.UpdatePathLocal}, и она перебивает общую" +
                (f.UpdatePathShared.Length > 0 ? $" ({f.UpdatePathShared})" : "") +
                $". Но её нет{Because(f.UpdateFolderProblem)}.",
                "Очистить поле «Папка обновлений этой машины» (Настройки → Общие) — тогда заработает общая настройка, — либо указать существующую папку.");

        return new SelfCheckFinding(title, Expected(f), f.UpdatePathEffective,
            $"Правило: {rule}. Папка недоступна{Because(f.UpdateFolderProblem)}. " +
            (NetworkDown(f)
                ? OutOfOffice
                : f.GitHubReachable
                    ? "Пока обновления пойдут с GitHub, но задумано было брать их с диска."
                    : $"GitHub тоже недоступен{Because(f.GitHubProblem)} — новые версии на эту машину не придут никак."));
    }

    private static SelfCheckFinding AutoInstall(SelfCheckFacts f) =>
        f.UpdateAutoInstall
            ? new SelfCheckFinding("Автоустановка обновлений", SelfCheckSeverity.Ok, "",
                "Включена: найденная новая версия ставится сама при запуске.")
            : new SelfCheckFinding("Автоустановка обновлений", SelfCheckSeverity.Warning, "",
                "Выключена. Программа найдёт новую версию и сообщит о ней, но сама не поставит — обновляться придётся кнопкой. " +
                "Если жалоба именно в том, что «обновления не устанавливаются сами», причина, скорее всего, здесь.",
                "Настройки → Общие → «Устанавливать найденные обновления автоматически при запуске».");

    /// <summary>Третья, до сих пор не проверявшаяся причина «обновление не ставится само» — и самая
    /// незаметная: источник обновлений цел, галочка автоустановки стоит, новая версия найдена, а
    /// самоустановка молча падает, потому что .exe запущен из папки, куда у пользователя нет прав на
    /// запись. Самоустановка перезаписывает свой .exe НА МЕСТЕ (AppUpdateService.InstallAndRestart:
    /// копия «*.update» рядом + перенос поверх оригинала) — без права записи в свою же папку это
    /// невозможно, и человеку «приходится ставить руками». В отчёте это раньше не всплывало ничем:
    /// провал переноса виден один раз уведомлением на следующем запуске и стирается, а «Проверка
    /// компьютера» смотрела только на источник и галочку.</summary>
    private static SelfCheckFinding InstallLocation(SelfCheckFacts f)
    {
        const string title = "Куда ставится обновление";

        if (f.InstallDir.Length == 0)
            return new SelfCheckFinding(title, SelfCheckSeverity.Info, "",
                "Папку, из которой запущена программа, определить не удалось — права на запись не проверялись.");

        if (f.InstallDirWritable)
            return new SelfCheckFinding(title, SelfCheckSeverity.Ok, f.InstallDir,
                "В папку программы есть права на запись — самоустановка сможет перезаписать .exe и обновиться сама.");

        var reason =
            $"Программа запущена из папки {f.InstallDir}, куда у пользователя «{f.WindowsUser}» нет прав на запись{Because(f.InstallDirWriteError)}. " +
            "Обновление ставится так: программа копирует новую версию рядом со своим .exe и переносит её поверх старого — " +
            "без права записи в эту папку перенос молча срывается, и обновляться приходится вручную. " +
            "Это и есть причина «у всех обновляется само, а у меня нет».";

        var fix = f.InstallUnderProgramFiles
            ? "Переустановить программу штатным установщиком (MSI): он ставит её per-user в вашу папку %LocalAppData%\\Programs\\AntarusPoFinder, куда права на запись есть всегда. " +
              "Portable-.exe в Program Files попал вручную — там самоустановка работать не будет без прав администратора при каждом обновлении."
            : "Перенести программу в папку, куда есть права на запись (проще всего — переустановить штатным установщиком MSI, он ставит её в %LocalAppData%\\Programs\\AntarusPoFinder), " +
              "либо обновлять вручную. Если .exe лежит на сетевом диске/в общей папке — запускать его надо из локальной установки, а не с сетевого диска.";

        return new SelfCheckFinding(title, SelfCheckSeverity.Problem, f.InstallDir, reason, fix);
    }

    private static SelfCheckFinding? UpdateHistory(SelfCheckFacts f) =>
        f.LastUpdateFailure.Length == 0
            ? null
            : new SelfCheckFinding("Прошлая проверка обновлений", SelfCheckSeverity.Info, "",
                $"В журнале записана неудача: {f.LastUpdateFailure}",
                "Если пункты выше зелёные — это уже прошло. Если нет, разбираться надо с ними.");

    // ── Синхронизация ────────────────────────────────────────────────────────

    private static SelfCheckFinding Sync(SelfCheckFacts f)
    {
        const string title = "Синхронизация настроек и тикетов";

        if (f.SyncTransport == "server")
            return f.SyncReachable
                ? new SelfCheckFinding(title, SelfCheckSeverity.Ok, f.SyncTarget, f.SyncDetails)
                : new SelfCheckFinding(title, Expected(f), f.SyncTarget,
                    $"Обмен идёт через службу, и она не отвечает. {f.SyncDetails}" + (NetworkDown(f) ? " " + OutOfOffice : ""),
                    NetworkDown(f) ? "" : "Настройки → Подключение → «Служба обмена» → «Проверить связь».");

        if (f.RootPath.Length == 0)
            return new SelfCheckFinding(title, SelfCheckSeverity.Info, "",
                "Обмен идёт через общую папку на рабочем диске, а диск не задан — обмениваться не через что.",
                "Настройки → Подключение → «Корень рабочего диска».");

        if (f.SyncReachable)
            return new SelfCheckFinding(title, SelfCheckSeverity.Ok, f.SyncTarget,
                "Общая папка доступна: справочник, настройки и тикеты ходят между машинами.");

        if (f.RootExists)
            return new SelfCheckFinding(title, SelfCheckSeverity.Warning, f.SyncTarget,
                "Рабочий диск доступен, а общей папки обмена на нём нет. Данные с других машин сюда не приходят; созданные здесь тикеты копятся и уйдут сами, когда папка появится.",
                "Администратору: на своей машине нажать «Отправить сейчас» (Настройки → Подключение) — папка создастся при первой отправке.");

        return new SelfCheckFinding(title, Expected(f), f.SyncTarget,
            "Общая папка обмена недоступна вместе с рабочим диском — см. пункт «Рабочий диск». Созданные здесь тикеты не потеряются: они уйдут, когда диск снова появится." +
            (NetworkDown(f) ? " " + OutOfOffice : ""));
    }

    // ── Вход ─────────────────────────────────────────────────────────────────

    private static SelfCheckFinding Auth(SelfCheckFacts f)
    {
        const string title = "Проверка входа";
        if (!f.AuthConfigured)
            return new SelfCheckFinding(title, SelfCheckSeverity.Info, "", "Вход по учётной записи не настроен — вход по общему паролю роли работает всегда.");
        return f.AuthReachable
            ? new SelfCheckFinding(title, SelfCheckSeverity.Ok, f.AuthTarget, f.AuthDetails)
            : new SelfCheckFinding(title, Expected(f), f.AuthTarget,
                f.AuthDetails + (NetworkDown(f) ? " " + OutOfOffice : ""),
                "Вход по общему паролю роли при этом работает — программа не заблокирована.");
    }

    // ── Второй диск и хранилище ──────────────────────────────────────────────

    private static SelfCheckFinding SecondDisk(SelfCheckFacts f)
    {
        const string title = "Второй диск (схемы)";
        if (f.SecondDiskPath.Length == 0)
            return new SelfCheckFinding(title, SelfCheckSeverity.Info, "", "Не задан — штатно, если схемы с этого компьютера не открывают.");
        if (f.SecondDiskExists)
            return new SelfCheckFinding(title, SelfCheckSeverity.Ok, f.SecondDiskPath, "Доступен.");
        return new SelfCheckFinding(title, Expected(f), f.SecondDiskPath,
            "Недоступен — схемы отсюда не откроются." + (NetworkDown(f) ? " " + OutOfOffice : ""),
            NetworkDown(f) ? "" : "Настройки → Подключение → «Второй диск»: проверить путь.");
    }

    private static SelfCheckFinding Storage(SelfCheckFacts f)
    {
        const string title = "Хранилище на хостинге";
        if (!f.StorageEnabled)
            return new SelfCheckFinding(title, SelfCheckSeverity.Info, "", "Выкладка инструкций на хостинг выключена — штатное состояние.");
        if (!f.StorageHasAddress)
            return new SelfCheckFinding(title, SelfCheckSeverity.Problem, "",
                "Выкладка включена, но адрес хранилища не задан — инструкции никуда не уходят, и QR на наклейке с телефона не откроется.",
                "Хранилище → «Реквизиты»: задать адрес и загрузить файл с ключами.");
        if (!f.StorageHasCredentials)
            return new SelfCheckFinding(title, SelfCheckSeverity.Problem, f.StorageTarget,
                "Выкладка включена и адрес задан, а ключи доступа не загружены — выкладка молча пропускается, и QR на наклейке с телефона не откроется.",
                "Хранилище → «Реквизиты»: перетащить файл с ключами в зону загрузки.");
        return new SelfCheckFinding(title, SelfCheckSeverity.Ok, f.StorageTarget, "Настроено: адрес и ключи на месте.");
    }

    // ── Мелочь ───────────────────────────────────────────────────────────────

    /// <summary>Системный текст ошибки — приложением к человеческому объяснению, а не вместо него, и
    /// только если он вообще есть.</summary>
    private static string Because(string? error) =>
        string.IsNullOrWhiteSpace(error) ? "" : $" ({error.Trim()})";
}
