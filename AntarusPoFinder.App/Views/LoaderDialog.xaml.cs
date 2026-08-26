using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Loader;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Интерактивная загрузка проекта в ПЛК и отдельная сборка .psl → .lfs через Segnetics
/// Loader Automation. Searcher хранит только UI операции и локальную копию исходника; подключение к
/// ПЛК, сборку PSL и загрузку выполняет production-пайплайн Loader.
///
/// Окно КОМПАКТНОЕ и запускается САМО. Раньше оператор, нажав на карточке «Загрузить в ПЛК»,
/// попадал в окно 760×610 с пятью кнопками внизу и обязан был нажать вторую кнопку «Загрузить» —
/// притом что файл уже выбран карточкой и выбирать было нечего. Теперь работа стартует при
/// открытии, всё необязательное (сменить файл, подготовка ПЛК, журнал) убрано под «Дополнительно» и
/// «Подробности», а внизу максимум две кнопки: «Свернуть» и «Остановить» во время работы, «Закрыть»
/// после.
///
/// Недоступность Automation проверяется ДО открытия окна (см. <see cref="EnsureAvailable"/>):
/// пустое окно с красным баннером и неработающей кнопкой оператору ничего не объясняло.
///
/// ⚠️ Окно НЕ модальное (жалоба владельца: «когда идёт загрузка LFS или форматирование, окно
/// блокирует работу основного окна — не хочу чтобы так было»). Заливка идёт минутами, и всё это
/// время наладчик был отрезан от поиска, карточек и подготовки следующей версии. Что из этого
/// следует:
///
/// • Ход операции дублируется ВНИЗ главного окна (<see cref="IAppHost.BeginBusy"/>) — свернув окно,
///   человек всё равно видит, что программа занята и чем.
/// • Итог уходит уведомлением с категорией (колокольчик хранит историю), потому что окна перед
///   глазами может уже не быть. Провал вдобавок разворачивает окно и поднимает его — ошибку нельзя
///   потерять из-за выключенной категории уведомлений.
/// • Защиту «второй раз не запустишь», которую раньше давала сама модальность, теперь держит
///   <see cref="LongOperationRegistry"/>: Segnetics Loader на машине один, и вторая операция
///   получает внятный отказ вместо гонки за один USB.
/// • Крестик во время работы СВОРАЧИВАЕТ окно, а не отменяет операцию: закрытое окно раньше молча
///   рвало заливку, а при немодальном окне промахнуться по крестику стало гораздо проще.</summary>
public partial class LoaderDialog : Window
{
    private readonly ConfigService _cfg;
    private readonly IFirmwareLoaderBackend _backend;
    private readonly bool _isBuild;
    private readonly Stopwatch _operationStopwatch = new();
    private readonly DispatcherTimer _operationElapsedTimer;

    /// <summary>Оболочка: индикатор внизу главного окна и уведомления. null в тестах/отладке —
    /// тогда окно работает как раньше, просто без дублирования хода наружу.</summary>
    private readonly IAppHost? _host;

    /// <summary>Реестр долгих операций: пока право не отпущено, вторую загрузку не пустят.</summary>
    private readonly LongOperationRegistry? _registry;

    /// <summary>Что сообщить вызывающему по итогу. Раньше вызывающий просто читал Succeeded после
    /// ShowDialog(); немодальное окно возвращается из метода сразу, поэтому итог — обратным вызовом.</summary>
    private readonly Action<bool>? _onFinished;

    private LoaderJob _job;
    private CancellationTokenSource? _cts;
    private LoaderWorkspace? _workspace;
    private readonly List<string> _logLines = new();
    private string? _lastLogMessage;
    private LoaderLogLevel _lastLogLevel;
    private DateTime _lastLogAtUtc;
    private bool _running;
    private bool _everStarted;
    private ILongOperationLease? _lease;
    private IBusyScope? _busy;

    /// <summary>Текущий запуск форматирует проект и обновляет ядро ПЛК — от этого зависит, безопасно
    /// ли его обрывать (см. LongOperationRules.SafeToCancel).</summary>
    private bool _formatsController;

    /// <summary>Оператор нажал «Остановить». Нужно, чтобы отличить остановку от провала в итоге:
    /// пайплайн Loader на отмену отвечает обычным неуспехом, без OperationCanceledException.</summary>
    private bool _cancelRequested;

    /// <summary>Вид операции для реестра и правил отмены.</summary>
    private LongOperationKind Kind => _isBuild ? LongOperationKind.LfsBuild : LongOperationKind.PlcDeploy;

    /// <summary>Как операция называется человеку — в индикаторе, в уведомлении, в чужом отказе
    /// («Segnetics Loader уже занят: …»).</summary>
    private string OperationTitle =>
        (_isBuild ? "Сборка LFS: " : "Загрузка в ПЛК: ") + _job.VersionName;

    /// <summary>Папка версии, в которую операция пишет. Пока она занята, версию нельзя откатить,
    /// удалить или перезалить (см. LongOperationRules.SubjectBusyReason).</summary>
    private string SubjectKey => SubjectKeyFor(_job);

    private static readonly TimeSpan WorkspaceRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan ImmediateDuplicateWindow = TimeSpan.FromSeconds(1);

    /// <summary>Операция завершилась успехом. Для сборки это значит, что .lfs уже лежит в папке
    /// версии (см. <see cref="PublishedLfs"/>) — вызывающий код может обновить свою выдачу.</summary>
    public bool Succeeded { get; private set; }

    /// <summary>Пути, по которым реально сохранён собранный .lfs.</summary>
    public IReadOnlyList<string> PublishedLfs { get; private set; } = Array.Empty<string>();

    /// <summary>Ответ на вопрос «форматировать ли ПЛК», заданный ДО открытия этого окна (см.
    /// <see cref="PlcPreparationDialog"/>). null — вопрос не задавали (сборка LFS).</summary>
    private readonly PlcPreparationAnswer? _preparation;

    public LoaderDialog(
        ConfigService cfg,
        LoaderJob job,
        PlcPreparationAnswer? preparation = null,
        IAppHost? host = null,
        LongOperationRegistry? registry = null,
        ILongOperationLease? lease = null,
        Action<bool>? onFinished = null)
    {
        InitializeComponent();
        _cfg = cfg;
        _job = job;
        _preparation = preparation;
        _host = host;
        _registry = registry;
        _lease = lease;
        _onFinished = onFinished;
        _isBuild = job.Operation == LoaderOperation.Build;
        _backend = FirmwareLoaderFactory.Create(cfg.LoaderExePath());

        _operationElapsedTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _operationElapsedTimer.Tick += (_, _) => UpdateOperationElapsedText();

        var version = string.IsNullOrEmpty(_backend.DisplayVersion) ? "" : $" v{_backend.DisplayVersion}";
        Title = _isBuild ? $"Сборка LFS через Segnetics Loader{version}" : $"Загрузка через Segnetics Loader{version}";
        HeaderLabel.Text = _isBuild
            ? $"Сборка LFS из PSL: {job.VersionName}"
            : $"Загрузка в ПЛК: {job.VersionName}";

        // Галочка теперь ПОКАЗЫВАЕТ уже принятое решение (и позволяет передумать перед повтором), а
        // не принимает его молча за наладчика: вопрос задан отдельным окном до открытия этого.
        PrepareControllerCheck.IsChecked = _preparation is { } answer
            ? PlcPreparation.FormatFor(answer)
            : cfg.LoaderFormatAndUpdateDefault();
        // Подготовка ПЛК относится только к заливке: сборка к контроллеру не подключается вообще.
        if (_isBuild) PrepareControllerCheck.Visibility = Visibility.Collapsed;

        AdvancedExpander.Expanded += (_, _) => AdvancedArrow.Text = "▾";
        AdvancedExpander.Collapsed += (_, _) => AdvancedArrow.Text = "▸";
        DetailsExpander.Expanded += (_, _) => { DetailsArrow.Text = "▾"; ScrollLogToEnd(); };
        DetailsExpander.Collapsed += (_, _) => DetailsArrow.Text = "▸";

        RefreshSourceLabels();
        RefreshPreparationLabel();
        SetRunning(false);

        if (!_backend.IsAvailable)
        {
            ShowUnavailable();
            return;
        }

        if (_preparation is { } chosen) AppendLog(PlcPreparation.LogLine(chosen));

        // Взяли не тот Loader, что прописан в настройках (или настройки нет вовсе) — наладчик обязан
        // видеть, чем именно грузит. Раньше это была ошибка «Loader не найден»; теперь — строка в
        // журнале и рабочая загрузка встроенной копией.
        if (SegneticsLoaderResolver.Resolve(cfg.LoaderExePath()) is { } resolvedExe &&
            SegneticsLoaderResolver.UsesFallback(cfg.LoaderExePath(), resolvedExe))
            AppendLog($"Указанный в настройках Loader недоступен — работаем встроенным: {resolvedExe}",
                LoaderLogLevel.Warning);

        AppendLog(_isBuild
            ? "Исходник будет скопирован в локальную рабочую область; на диск уедет только готовый LFS."
            : "Файл будет скопирован в локальную рабочую область перед запуском Loader.");

        // Старт сразу после первой отрисовки: оператор видит окно с прогрессом, а не пустую форму,
        // которую надо «завести» второй кнопкой.
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = RunAsync()));

        Task.Run(() =>
        {
            try { LoaderWorkspace.CleanupOlderThan(ConfigService.LocalLoader, WorkspaceRetention); }
            catch (Exception)
            {
                // Уборка старых рабочих областей — обслуживание, а не часть загрузки в ПЛК: файл мог
                // быть занят предыдущим запуском Loader. Сообщать оператору не о чем, а прервать
                // из-за этого саму загрузку тем более нельзя — уберём при следующем открытии окна.
            }
        });
    }

    /// <summary>Проверка доступности Automation ОДНИМ местом на всех вызывающих: причина
    /// показывается до открытия окна, обычным сообщением, а не красным баннером внутри окна, из
    /// которого всё равно ничего не запустить.</summary>
    public static bool EnsureAvailable(Window? owner, ConfigService cfg)
    {
        var backend = FirmwareLoaderFactory.Create(cfg.LoaderExePath());
        if (backend.IsAvailable) return true;
        AppMessageBox.Show(
            backend.UnavailableReason ?? "Segnetics Loader Automation недоступен.",
            "Segnetics Loader", MessageBoxButton.OK, MessageBoxImage.Error);
        return false;
    }

    /// <summary>Папка версии, которую займёт задание. Сетевая папка — главная: именно в неё уезжает
    /// собранный .lfs, и именно её нельзя трогать, пока операция идёт. Её может не быть (сборка из
    /// произвольного файла) — тогда берём папку самого исходника, чтобы ключ всё равно был не пустым
    /// и двойной запуск по тому же файлу отсекался.</summary>
    private static string SubjectKeyFor(LoaderJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.NetworkFolder)) return LongOperationSubject.Folder(job.NetworkFolder);
        var source = job.SourcePath ?? "";
        if (source.Length == 0) return LongOperationSubject.None;
        try { return LongOperationSubject.Folder(Path.GetDirectoryName(source)); }
        catch (Exception) { return LongOperationSubject.None; }
    }

    /// <summary>Загрузка в ПЛК с карточки версии. Возвращается СРАЗУ: окно немодальное, работа идёт
    /// сама, итог приходит уведомлением и (если попросили) в <paramref name="onFinished"/>.
    /// Результат метода — «запустилось ли», а не «получилось ли».</summary>
    public static bool StartDeploy(
        Window? owner, ConfigService cfg, LoaderJob job,
        IAppHost? host = null, LongOperationRegistry? registry = null, Action<bool>? onFinished = null) =>
        Start(owner, cfg, job with { Operation = LoaderOperation.Deploy }, host, registry, onFinished);

    /// <summary>Сборка .lfs из .psl без подключения к ПЛК (модерация, догрузка после выкладки).</summary>
    public static bool StartBuild(
        Window? owner, ConfigService cfg, LoaderJob job,
        IAppHost? host = null, LongOperationRegistry? registry = null, Action<bool>? onFinished = null) =>
        Start(owner, cfg, job with { Operation = LoaderOperation.Build }, host, registry, onFinished);

    private static bool Start(
        Window? owner, ConfigService cfg, LoaderJob job,
        IAppHost? host, LongOperationRegistry? registry, Action<bool>? onFinished)
    {
        if (!EnsureAvailable(owner, cfg)) return false;

        // Право на операцию берём ДО всех вопросов: спрашивать про форматирование, чтобы потом
        // сказать «Loader занят», — издевательство. Отказ показывается обычным сообщением, а не
        // молча гасит кнопку: человек должен понять, почему ничего не произошло.
        var kind = job.Operation == LoaderOperation.Build
            ? LongOperationKind.LfsBuild
            : LongOperationKind.PlcDeploy;
        var title = (job.Operation == LoaderOperation.Build ? "Сборка LFS: " : "Загрузка в ПЛК: ") + job.VersionName;
        ILongOperationLease? lease = null;
        if (registry is not null &&
            !registry.TryBegin(kind, SubjectKeyFor(job), title, out lease, out var refusal))
        {
            AppMessageBox.Show(refusal, LongOperationRules.Caption(kind),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        // Вопрос про форматирование — ДО открытия окна операции: окно стартует загрузку само, и
        // спрашивать внутри него было бы уже поздно (см. PlcPreparation).
        PlcPreparationAnswer? preparation = null;
        if (PlcPreparation.ShouldAsk(job.Operation))
        {
            var answer = PlcPreparationDialog.Ask(owner, job.VersionName, cfg.LoaderFormatAndUpdateDefault());
            if (PlcPreparation.IsCancelled(answer))
            {
                lease?.Dispose();
                return false;
            }
            preparation = answer;
            cfg.SetLoaderFormatAndUpdateDefault(PlcPreparation.FormatFor(answer));
        }

        var dialog = new LoaderDialog(cfg, job, preparation, host, registry, lease, onFinished)
        {
            // Хозяин — ГЛАВНОЕ окно, а не то, откуда нажали. Кнопка «Собрать LFS» живёт в модальном
            // окне модерации, и повесив окно операции на него, мы бы снова заперли программу: закрыть
            // модерацию, не оборвав сборку, стало бы нельзя. С главным окном в хозяевах окно операции
            // переживает закрытие модерации и не теряется за главным.
            Owner = MainWindowOwner(owner),
        };
        dialog.Show();
        return true;
    }

    /// <summary>Главное окно приложения, если оно есть и это не оно само нас открыло. Отдельный
    /// метод, потому что Application.Current в тестах и отладочных прогонах бывает null.</summary>
    private static Window? MainWindowOwner(Window? fallback)
    {
        var main = Application.Current?.MainWindow;
        if (main is not null && main.IsLoaded) return main;
        return fallback;
    }

    // ── Запуск операции ───────────────────────────────────────────────────

    private async Task RunAsync()
    {
        if (_running) return;

        if (!_backend.IsAvailable)
        {
            ShowUnavailable();
            return;
        }

        // Повтор («Запустить заново» / «Повторить попытку») — это НОВАЯ операция: право на прошлую
        // уже отпущено в finally, и его надо взять снова. За это время коллега мог начать заливку
        // с другой карточки — тогда честный отказ, а не тихая гонка за один и тот же Loader.
        if (_lease is null && _registry is not null)
        {
            if (!_registry.TryBegin(Kind, SubjectKey, OperationTitle, out _lease, out var refusal))
            {
                AppendLog(refusal, LoaderLogLevel.Warning);
                StageLabel.Text = "Не запускалось";
                AppMessageBox.Show(refusal, LongOperationRules.Caption(Kind),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        var source = _job.SourcePath?.Trim() ?? "";
        if (string.IsNullOrEmpty(source) || !File.Exists(source))
        {
            ReleaseLease();
            FinishWithError(string.IsNullOrEmpty(source)
                ? "Файл для загрузки не выбран."
                : $"Файл не найден:\n{source}");
            return;
        }

        var prepareController = !_isBuild && PrepareControllerCheck.IsChecked == true;
        if (!_isBuild && prepareController != _cfg.LoaderFormatAndUpdateDefault())
            _cfg.SetLoaderFormatAndUpdateDefault(prepareController);
        RefreshPreparationLabel();

        _everStarted = true;
        _formatsController = prepareController;
        // Итоги ПРОШЛОЙ попытки к этой не относятся: «Повторить попытку» после остановки иначе
        // отчиталась бы об остановке снова, даже если на этот раз всё прошло.
        Succeeded = false;
        _cancelRequested = false;
        SetRunning(true);
        _cts = new CancellationTokenSource();
        var cancellationToken = _cts.Token;
        var progress = new Progress<LoaderProgress>(OnProgress);

        // Ход операции дублируется вниз главного окна: окно можно свернуть и уйти в поиск, но
        // «программа чем-то занята и чем именно» должно оставаться на виду.
        _busy = _host?.BeginBusy(OperationTitle);
        _host?.ShowStatus(
            (_isBuild ? "Запущена сборка LFS: " : "Запущена загрузка в ПЛК: ") + _job.VersionName +
            ". Программой можно пользоваться — ход виден внизу.",
            6000, NotificationCategory.FirmwareAndParams, Reveal);

        var outcome = LoaderOutcome.Failed;
        var outcomeMessage = "";
        try
        {
            if (_isBuild) await RunBuildAsync(progress, cancellationToken);
            else await RunDeployAsync(source, prepareController, progress, cancellationToken);
            outcome = Succeeded ? LoaderOutcome.Succeeded
                : _cancelRequested ? LoaderOutcome.Cancelled
                : LoaderOutcome.Failed;
            outcomeMessage = _lastLogMessage ?? "";
        }
        catch (OperationCanceledException ex)
        {
            var message = string.IsNullOrWhiteSpace(ex.Message) || ex.Message == "The operation was canceled."
                ? "Операция отменена."
                : ex.Message;
            AppendLog(message, LoaderLogLevel.Warning);
            Progress.IsIndeterminate = false;
            PercentLabel.Text = "";
            StageLabel.Text = "Остановлено";
            outcome = LoaderOutcome.Cancelled;
            outcomeMessage = message;
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message, LoaderLogLevel.Error);
            ShowFailedState("Ошибка");
            outcome = LoaderOutcome.Failed;
            outcomeMessage = ex.Message;
        }
        finally
        {
            SaveLogToWorkspace();
            SetRunning(false);
            _cts?.Dispose();
            _cts = null;
            _busy?.Dispose();
            _busy = null;
            // Право отпускаем ЗДЕСЬ, а не при закрытии окна: операция кончилась, версия свободна, и
            // держать её занятой только потому, что оператор не закрыл окно с журналом, нельзя.
            ReleaseLease();
            ReportOutcome(outcome, outcomeMessage);
        }
    }

    private enum LoaderOutcome { Succeeded, Cancelled, Failed }

    /// <summary>Сказать человеку, чем всё кончилось, ДАЖЕ ЕСЛИ окна перед глазами уже нет.
    ///
    /// ⚠️ Провал вдобавок разворачивает и поднимает окно. Уведомление одно спасти не может: категорию
    /// «Прошивки и параметры» разрешено выключить в настройках, и тогда ShowStatus не покажет ничего
    /// вовсе (см. MainWindowViewModel.ShowStatus). Модальное окно раньше показывало отказ в лицо —
    /// потерять ошибку заливки из-за настройки уведомлений было бы прямым ухудшением.</summary>
    private void ReportOutcome(LoaderOutcome outcome, string message)
    {
        var what = _isBuild ? "Сборка LFS" : "Загрузка в ПЛК";
        switch (outcome)
        {
            case LoaderOutcome.Succeeded:
                _host?.ShowStatus($"✓ {what} завершена: {_job.VersionName}",
                    8000, NotificationCategory.FirmwareAndParams, Reveal);
                break;

            case LoaderOutcome.Cancelled:
                _host?.ShowStatus($"{what} остановлена: {_job.VersionName}",
                    8000, NotificationCategory.FirmwareAndParams, Reveal);
                break;

            default:
                var tail = string.IsNullOrWhiteSpace(message) ? "" : " — " + message;
                _host?.ShowStatus($"⚠ {what} не удалась: {_job.VersionName}{tail}",
                    12000, NotificationCategory.FirmwareAndParams, Reveal);
                Reveal();
                break;
        }

        _onFinished?.Invoke(outcome == LoaderOutcome.Succeeded);
    }

    /// <summary>Показать окно операции и поднять его. Это же действие уезжает кнопкой «Показать» в
    /// запись истории уведомлений — свёрнутое окно оттуда достаётся одним нажатием.</summary>
    private void Reveal()
    {
        // Запись в истории уведомлений живёт дольше окна: её «Показать» могут нажать через час,
        // когда окно давно закрыто. Show() у закрытого окна бросает InvalidOperationException —
        // ловим, а не рассчитываем только на IsLoaded.
        if (!IsLoaded) return;
        try
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Activate();
        }
        catch (InvalidOperationException)
        {
            // Окно уже закрыто — показывать нечего, а падать из-за нажатия в истории тем более.
        }
    }

    private void ReleaseLease()
    {
        _lease?.Dispose();
        _lease = null;
    }

    /// <summary>Готовит ПОДКЛЮЧЕНИЕ перед заливкой: переносит выбор наладчика (USB/Ethernet, адрес,
    /// сетевой адаптер) в настройки самого Loader и, если попросили, проверяет связь заранее.
    ///
    /// Почему перенос, а не параметр запроса — см. <see cref="LoaderConnectionSettings"/>: Automation
    /// параметры подключения в запросе не принимает вовсе. Почему проверка связи — см.
    /// <see cref="PlcLinkCheck"/>: минута ожидания ради «CONNECTION_FAILED» из-за невоткнутого шнурка.
    ///
    /// Возвращает false, только если наладчик сам отказался продолжать после «связи нет»: запретить
    /// заливку из-за неудачной пробы программа не вправе — он может знать лучше (ПЛК ещё грузится,
    /// адрес временный).</summary>
    private async Task<bool> PrepareConnectionAsync(CancellationToken cancellationToken)
    {
        var mode = LoaderConnectionSettings.ParseMode(_cfg.LoaderConnectionMode());
        var ip = _cfg.LoaderPlcIp();
        var adapter = _cfg.LoaderNetworkAdapter();

        var applied = LoaderConnectionSettings.Apply(mode, ip, adapter);
        if (applied.Applied)
            AppendLog($"Подключение: {LoaderConnectionSettings.ModeCaption(mode)} — перенесено в настройки Loader " +
                      $"({string.Join(", ", applied.ChangedKeys)}).");
        else if (!string.IsNullOrEmpty(applied.Message))
            AppendLog(applied.Message, LoaderLogLevel.Warning);

        // Проверять нечего, пока не выбран Ethernet с адресом: у USB адреса нет, а «как в Loader»
        // означает, что подключение нам вообще неизвестно.
        if (mode != PlcConnectionMode.Ethernet || !_cfg.LoaderCheckLink() || string.IsNullOrWhiteSpace(ip))
            return true;

        StageLabel.Text = "Проверяем связь с ПЛК…";
        AppendLog($"Проверяем связь с {ip}…");
        var link = await PlcLinkCheck.CheckAsync(ip, _cfg.LoaderLinkTimeoutMs(), cancellationToken);
        AppendLog($"{link.Message} ({link.ElapsedMs} мс)", link.Reachable ? LoaderLogLevel.Success : LoaderLogLevel.Warning);
        if (link.Reachable) return true;

        var answer = AppMessageBox.Show(
            link.Message + "\n\nЗапустить загрузку всё равно?",
            "Связь с ПЛК", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return answer == MessageBoxResult.Yes;
    }

    private async Task RunDeployAsync(
        string source, bool prepareController, IProgress<LoaderProgress> progress, CancellationToken cancellationToken)
    {
        if (!await PrepareConnectionAsync(cancellationToken))
        {
            AppendLog("Загрузка не запущена: наладчик отменил её после проверки связи.", LoaderLogLevel.Warning);
            Progress.IsIndeterminate = false;
            PercentLabel.Text = "";
            StageLabel.Text = "Не запускалось";
            return;
        }

        var workspace = LoaderWorkspace.Create(ConfigService.LocalLoader, _job.VersionName);
        _workspace = workspace;
        AppendLog($"Рабочая область: {workspace.Dir}");

        var localSource = await Task.Run(() => workspace.Import(source), cancellationToken);
        AppendLog($"Локальная копия готова: {localSource}", LoaderLogLevel.Success);

        var isPslSource = string.Equals(
            Path.GetExtension(source), LoaderFiles.PslExtension, StringComparison.OrdinalIgnoreCase);
        var outputLfsPath = isPslSource
            ? Path.Combine(workspace.OutputDir, LoaderFiles.LfsNameFor(source))
            : null;

        var request = new LoaderRequest
        {
            Operation = LoaderOperation.Deploy,
            SourcePath = localSource,
            WorkspaceDir = workspace.Dir,
            OutputPath = outputLfsPath,
            VersionName = _job.VersionName,
            Options = new LoaderOptions { FormatAndUpdateFirmware = prepareController },
        };

        var result = await _backend.RunAsync(request, progress, cancellationToken);

        // Собранный по ходу заливки LFS сохраняем В ПАПКУ ВЕРСИИ НА ДИСКЕ, а не только в локальной
        // копии — иначе следующий наладчик на другой машине снова увидит один .psl. Неудача
        // публикации — предупреждение, а не провал операции: прошивка в контроллере уже лежит, и
        // рисовать «Ошибка» после успешной заливки было бы прямой ложью.
        if (result.Success && outputLfsPath is not null)
        {
            if (!File.Exists(outputLfsPath) || !result.Artifacts.Any(path => PathsEqual(path, outputLfsPath)))
                AppendLog("Loader не вернул собранный LFS — в папке версии он не появится.", LoaderLogLevel.Warning);
            else
                await PublishBuiltLfsAsync(outputLfsPath);
        }

        AppendLog(result.Message, result.Success ? LoaderLogLevel.Success : LoaderLogLevel.Error);
        if (result.Success)
        {
            Succeeded = true;
            Progress.IsIndeterminate = false;
            Progress.Value = 100;
            PercentLabel.Text = "100%";
            StageLabel.Text = "Загрузка завершена";
            StageTone("SuccessBrush");
        }
        else
        {
            ShowFailedState("Ошибка");
        }
    }

    private async Task RunBuildAsync(IProgress<LoaderProgress> progress, CancellationToken cancellationToken)
    {
        var plan = new LfsConversionPlan(_job.SourcePath, LfsPublisher.Plan(_job.NetworkFolder, _job.LocalFolder));
        var result = await LfsConversionService.BuildAndPublishAsync(
            _backend, plan, ConfigService.LocalLoader, _job.VersionName, progress, cancellationToken,
            workspace => _workspace = workspace);

        foreach (var warning in result.Warnings) AppendLog(warning, LoaderLogLevel.Warning);
        PublishedLfs = result.Published;

        switch (result.Status)
        {
            case LfsConversionStatus.Built:
                Succeeded = true;
                AppendLog(result.Message, LoaderLogLevel.Success);
                Progress.IsIndeterminate = false;
                Progress.Value = 100;
                PercentLabel.Text = "100%";
                StageLabel.Text = "LFS собран";
                StageTone("SuccessBrush");
                break;

            case LfsConversionStatus.Cancelled:
                AppendLog(result.Message, LoaderLogLevel.Warning);
                Progress.IsIndeterminate = false;
                PercentLabel.Text = "";
                StageLabel.Text = "Остановлено";
                break;

            default:
                AppendLog(result.Message, LoaderLogLevel.Error);
                ShowFailedState("Ошибка сборки");
                break;
        }
    }

    private async Task PublishBuiltLfsAsync(string builtLfs)
    {
        var plan = LfsPublisher.Plan(_job.NetworkFolder, _job.LocalFolder);
        var published = await Task.Run(() => LfsPublisher.PublishAll(builtLfs, plan));
        PublishedLfs = published.Published;
        foreach (var path in published.Published)
            AppendLog($"Собранный LFS сохранён: {path}", LoaderLogLevel.Success);
        foreach (var warning in published.Warnings)
            AppendLog(warning, LoaderLogLevel.Warning);
    }

    // ── Состояние окна ────────────────────────────────────────────────────

    private void SetRunning(bool running)
    {
        _running = running;
        StopBtn.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StopBtn.IsEnabled = running;
        MinimizeBtn.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CloseBtn.Visibility = running ? Visibility.Collapsed : Visibility.Visible;

        // Что будет, если нажать «Остановить» — видно ровно пока есть что останавливать.
        CancelPolicyBox.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (running)
        {
            CancelPolicyLabel.Text = LongOperationRules.CancelHint(Kind, _formatsController);
            CancelPolicyLabel.SetResourceReference(
                System.Windows.Controls.TextBlock.ForegroundProperty,
                LongOperationRules.SafeToCancel(Kind, _formatsController) ? "TextMutedBrush" : "WarningBrush");
            CancelPolicyBox.SetResourceReference(
                System.Windows.Controls.Border.BorderBrushProperty,
                LongOperationRules.SafeToCancel(Kind, _formatsController) ? "BorderBrush2" : "WarningBrush");
        }
        // «Рабочая папка» и «Сохранить журнал…» нужны только по итогу — до первого запуска их
        // показывать нечему, во время работы они только отвлекают.
        MoreBtn.Visibility = !running && _everStarted ? Visibility.Visible : Visibility.Collapsed;
        OpenWorkspaceItem.IsEnabled = _workspace is not null && Directory.Exists(_workspace.Dir);
        SaveLogItem.IsEnabled = _logLines.Count > 0;

        ChangeSourceBtn.IsEnabled = !running;
        RestartBtn.IsEnabled = !running && _backend.IsAvailable;
        PrepareControllerCheck.IsEnabled = !running;

        if (running)
        {
            // Итог прошлой попытки больше не про текущую: и кнопку повтора, и подсказку убираем.
            HideRetry();
            StartOperationElapsedTimer();
            Progress.IsIndeterminate = false;
            Progress.Value = 0;
            PercentLabel.Text = "0%";
            StageLabel.Text = "Запуск…";
            StageTone(null);
        }
        else
        {
            StopOperationElapsedTimer();
            // Операция закончилась — активной становится «Закрыть», а не то, на чём остался фокус
            // (после авто-старта это «Запустить заново» в «Дополнительно»: Enter/пробел по привычке
            // запускал загрузку ВТОРОЙ раз вместо закрытия окна — жалоба «после загрузки выделенной
            // должна быть кнопка Закрыть»). IsDefault=true даёт и подсветку, и реакцию на Enter;
            // IsCancel на кнопке остаётся, поэтому Esc закрывает окно как раньше.
            CloseBtn.IsDefault = true;
            // В конструкторе SetRunning(false) зовётся ДО показа окна — там Focus() вернул бы false
            // и ничего не сделал, поэтому его там и не пробуем: важен только вызов по итогу
            // операции, когда окно уже открыто.
            if (IsLoaded) CloseBtn.Focus();
        }
    }

    private void ShowUnavailable()
    {
        UnavailableBanner.Visibility = Visibility.Visible;
        UnavailableReasonLabel.Text = _backend.UnavailableReason ?? "";
        if (_backend.UnavailableReason is { Length: > 0 } reason) AppendLog(reason, LoaderLogLevel.Error);
        StageLabel.Text = "Не запускалось";
        PercentLabel.Text = "";
        SetRunning(false);
    }

    /// <summary>Провал показывается сразу с раскрытым журналом: разбираться без него всё равно
    /// невозможно, а лишний клик по «Подробности» в этот момент — издевательство. Вместе с журналом
    /// появляются «Повторить попытку» внизу и подсказка, что проверить руками.</summary>
    private void ShowFailedState(string stage)
    {
        Progress.IsIndeterminate = false;
        PercentLabel.Text = "";
        StageLabel.Text = stage;
        StageTone(null);
        DetailsExpander.IsExpanded = true;
        ShowRetry();
    }

    /// <summary>Кнопка повтора и подсказка по обвязке. Подсказка — не украшение: отказ Loader чаще
    /// оказывался не программным, а в том, что к ПЛК разом подключены несколько шнурков (панель,
    /// модем, USB) и Automation цепляется не за тот интерфейс — после снятия питания и повторного
    /// запуска с одним USB та же прошивка уходила нормально. Для сборки LFS её не показываем:
    /// контроллер там не участвует вовсе.</summary>
    private void ShowRetry()
    {
        RetryBtn.Visibility = _backend.IsAvailable ? Visibility.Visible : Visibility.Collapsed;
        if (_isBuild) return;
        FailureHintLabel.Text =
            "Если Loader не увидел ПЛК: оставьте подключённым только шнур загрузки — панель, модем и " +
            "прочие кабели на время заливки лучше отсоединить, — снимите и подайте питание на " +
            "контроллер и повторите. Режим подключения (USB/Ethernet, адрес, адаптер) проверяется в " +
            "«Настройки → Лоадер».";
        FailureHintBox.Visibility = Visibility.Visible;
    }

    private void HideRetry()
    {
        RetryBtn.Visibility = Visibility.Collapsed;
        FailureHintBox.Visibility = Visibility.Collapsed;
    }

    private void FinishWithError(string message)
    {
        AppendLog(message, LoaderLogLevel.Error);
        ShowFailedState("Ошибка");
        SetRunning(false);
    }

    private void RefreshSourceLabels()
    {
        var source = _job.SourcePath ?? "";
        var name = string.IsNullOrEmpty(source) ? "не выбран" : Path.GetFileName(source);
        SourceLabel.Text = $"Файл: {name}";
        SourceLabel.ToolTip = string.IsNullOrEmpty(source) ? null : source;
        AdvancedSourceLabel.Text = string.IsNullOrEmpty(source) ? "Файл не выбран." : $"Файл: {source}";
    }

    /// <summary>Строка про подготовку ПЛК. Авто-старт применяет ЗАПОМНЕННОЕ значение галки, поэтому
    /// оператор обязан видеть его до и во время работы, а не узнавать по факту форматирования.</summary>
    private void RefreshPreparationLabel()
    {
        if (_isBuild)
        {
            PreparationLabel.Visibility = Visibility.Collapsed;
            return;
        }

        var prepare = PrepareControllerCheck.IsChecked == true;
        PreparationLabel.Visibility = Visibility.Visible;
        PreparationLabel.Text = prepare
            ? "Подготовка ПЛК: форматирование проекта и обновление ядра — включено (запомненная настройка)."
            : "Подготовка ПЛК: без форматирования и обновления ядра (запомненная настройка).";
        PreparationLabel.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty, prepare ? "WarningBrush" : "TextMutedBrush");
    }

    // ── Кнопки ────────────────────────────────────────────────────────────

    /// <summary>Остановка. Для заливки с форматированием сперва спрашиваем: оборвать её посреди
    /// обновления ядра — оставить контроллер без рабочей прошивки. Кнопку при этом не прячем и не
    /// гасим: бывает, что оборвать всё равно надо (не тот файл, не тот ПЛК), — но нажать её человек
    /// должен осознанно, а не потому, что «Остановить» стоит там же, где всегда.</summary>
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!LongOperationRules.SafeToCancel(Kind, _formatsController))
        {
            var answer = AppMessageBox.Show(
                LongOperationRules.CancelConfirmation(OperationTitle),
                "Остановить операцию", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }

        _cancelRequested = true;
        _cts?.Cancel();
        StopBtn.IsEnabled = false;
        StageLabel.Text = "Отправляю команду отмены…";
    }

    /// <summary>Убрать окно с глаз, не трогая операцию. Свёрнутое окно остаётся кнопкой на панели
    /// задач, ход виден внизу главного окна, а по итогу придёт уведомление — потерять операцию,
    /// свернув её, нельзя.</summary>
    private void Minimize_Click(object sender, RoutedEventArgs e) => MinimizeAndExplain();

    private void MinimizeAndExplain()
    {
        WindowState = WindowState.Minimized;
        _host?.ShowStatus(
            $"{OperationTitle} продолжается — ход внизу, окно на панели задач.",
            6000, NotificationCategory.FirmwareAndParams, Reveal);
    }

    private void Restart_Click(object sender, RoutedEventArgs e) => _ = RunAsync();

    private void ChangeSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите проект для загрузки",
            Filter = _isBuild
                ? "Исходник Segnetics (*.psl)|*.psl|Все файлы (*.*)|*.*"
                : "Проекты Segnetics (*.lfs;*.psl)|*.lfs;*.psl|Все файлы (*.*)|*.*",
        };
        var current = _job.SourcePath ?? "";
        if (!string.IsNullOrEmpty(current))
        {
            var directory = Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                dialog.InitialDirectory = directory;
        }
        if (dialog.ShowDialog() != true) return;

        _job = _job with { SourcePath = dialog.FileName };
        RefreshSourceLabels();
        AppendLog($"Выбран другой файл: {dialog.FileName}");
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (MoreBtn.ContextMenu is not { } menu) return;
        menu.PlacementTarget = MoreBtn;
        menu.IsOpen = true;
    }

    private void OpenWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || !Directory.Exists(_workspace.Dir)) return;
        try { Process.Start(new ProcessStartInfo(_workspace.Dir) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            AppendLog($"Не удалось открыть папку: {ex.Message}", LoaderLogLevel.Warning);
        }
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить журнал операции",
            Filter = "Текстовый файл (*.txt)|*.txt|Все файлы (*.*)|*.*",
            FileName = $"loader_{LoaderFileStem()}.txt",
        };
        if (dialog.ShowDialog() != true) return;

        try { File.WriteAllLines(dialog.FileName, _logLines); }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось сохранить файл:\n{ex.Message}", "Segnetics Loader",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ── Прогресс и журнал ─────────────────────────────────────────────────

    private void OnProgress(LoaderProgress value)
    {
        if (value.Percent >= 0)
        {
            Progress.IsIndeterminate = false;
            Progress.Value = Math.Clamp(value.Percent, 0, 100);
            PercentLabel.Text = $"{Progress.Value:0}%";
            // Тот же процент — в индикатор внизу главного окна: свёрнутое окно операции не должно
            // означать «непонятно, сколько ещё ждать».
            _busy?.Report((int)Progress.Value, 100);
        }
        else if (Progress.Value == 0)
        {
            Progress.IsIndeterminate = true;
            PercentLabel.Text = "";
        }

        if (value.UpdatesStage)
        {
            StageLabel.Text = value.Stage;
            if (_busy is not null && !string.IsNullOrWhiteSpace(value.Stage))
                _busy.Text = $"{OperationTitle} — {value.Stage}";
        }
        if (value.Percent < 100 && !string.IsNullOrWhiteSpace(value.Message))
            AppendLog(value.Message, value.Level);
    }

    private void StartOperationElapsedTimer()
    {
        _operationElapsedTimer.Stop();
        _operationStopwatch.Restart();
        ElapsedLabel.Text = FormatOperationElapsed(TimeSpan.Zero);
        ElapsedLabel.Visibility = Visibility.Visible;
        _operationElapsedTimer.Start();
    }

    private void StopOperationElapsedTimer()
    {
        if (_operationStopwatch.IsRunning) _operationStopwatch.Stop();
        _operationElapsedTimer.Stop();
        UpdateOperationElapsedText();
    }

    private void UpdateOperationElapsedText() =>
        ElapsedLabel.Text = FormatOperationElapsed(_operationStopwatch.Elapsed);

    private static string FormatOperationElapsed(TimeSpan elapsed)
    {
        var totalMinutes = Math.Max(0, (int)elapsed.TotalMinutes);
        return $"{totalMinutes:00}:{elapsed.Seconds:00}";
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void AppendLog(string message, LoaderLogLevel level = LoaderLogLevel.Info)
    {
        var normalizedMessage = message.TrimEnd();
        var nowUtc = DateTime.UtcNow;
        if (level == _lastLogLevel &&
            string.Equals(normalizedMessage, _lastLogMessage, StringComparison.Ordinal) &&
            nowUtc - _lastLogAtUtc <= ImmediateDuplicateWindow)
        {
            return;
        }

        _lastLogMessage = normalizedMessage;
        _lastLogLevel = level;
        _lastLogAtUtc = nowUtc;

        var line = $"{DateTime.Now:HH:mm:ss}  {normalizedMessage}";
        _logLines.Add(line);

        var paragraph = new Paragraph(new Run(line))
        {
            Margin = new Thickness(0, 0, 0, 2),
        };
        paragraph.SetResourceReference(TextElement.ForegroundProperty, level switch
        {
            LoaderLogLevel.Success => "SuccessBrush",
            LoaderLogLevel.Warning => "WarningBrush",
            LoaderLogLevel.Error => "ErrorBrush",
            _ => "TextBrush",
        });
        LogDocument.Blocks.Add(paragraph);
        ScrollLogToEnd();
        SaveLogItem.IsEnabled = true;
    }

    /// <summary>Прокрутить лог к последней строке.
    ///
    /// Почему через Dispatcher, а не сразу после Blocks.Add: ScrollToEnd отталкивается от высоты
    /// содержимого, а она на этот момент ещё не пересчитана — прокрутка уезжает на конец
    /// ПРЕДЫДУЩЕЙ строки, и свежее сообщение остаётся под нижней кромкой. Отсюда и жалоба: лог
    /// отстаёт ровно на одну строку, а при заливке ПЛК важна как раз последняя. Отложенный на
    /// DispatcherPriority.Loaded вызов приходит уже после перерасчёта вёрстки.
    ///
    /// Пока «Подробности» свёрнуты, LogBox не измерен вовсе и прокручивать нечего — поэтому
    /// прокрутка повторяется при раскрытии панели: иначе оператор, открывший её посреди работы,
    /// видит начало лога вместо того, что происходит сейчас.</summary>
    private void ScrollLogToEnd()
    {
        if (!DetailsExpander.IsExpanded) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => LogBox.ScrollToEnd()));
    }

    private void SaveLogToWorkspace()
    {
        if (_workspace is null || _logLines.Count == 0) return;
        try { File.WriteAllLines(_workspace.LogPath, _logLines); }
        catch (Exception ex)
        {
            AppendLog($"Не удалось сохранить журнал в рабочую область: {ex.Message}", LoaderLogLevel.Warning);
        }
    }

    private string LoaderFileStem()
    {
        var stem = string.Join("_", _job.VersionName.Split(
            Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrEmpty(stem) ? DateTime.Now.ToString("yyyyMMdd_HHmmss") : stem;
    }

    /// <summary>Крестик во время работы СВОРАЧИВАЕТ окно, а не рвёт операцию.
    ///
    /// Раньше окно было модальным и стояло перед глазами до конца — закрыть его посреди заливки
    /// можно было только намеренно. Немодальное окно живёт среди остальных, промахнуться по крестику
    /// вместо «Свернуть» стало легко, и молча оборванная на середине заливка контроллера — слишком
    /// дорогая цена за такой промах. Оборвать по-прежнему можно, но кнопкой «Остановить», которая
    /// про форматирование ещё и переспросит.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_running)
        {
            e.Cancel = true;
            MinimizeAndExplain();
            return;
        }

        _cts?.Cancel();
        _operationElapsedTimer.Stop();
        ReleaseLease();
        base.OnClosing(e);
    }

    /// <summary>Цвет строки состояния лоадера. Заливка в контроллер — операция, за которой человек
    /// стоит и ждёт: «Загрузка завершена» серым, тем же цветом, что и «Проверяем связь с ПЛК…»,
    /// приходится ЧИТАТЬ, чтобы понять, чем всё кончилось. Зелёный виден боковым зрением.
    ///
    /// Цвет обязательно СБРАСЫВАЕТСЯ на каждом новом шаге (Stage ниже), иначе зелёное «завершена»
    /// осталось бы висеть над следующим запуском и врало бы о нём.</summary>
    private void StageTone(string? brushKey)
    {
        StageLabel.Foreground = brushKey is null
            ? (System.Windows.Media.Brush)FindResource("TextMutedBrush")
            : (System.Windows.Media.Brush)FindResource(brushKey);
    }
}
