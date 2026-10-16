using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AntarusPoFinder.App.ViewModels;

/// <summary>История уведомлений и счётчик на колокольчике — одним местом, поверх таблицы
/// notifications.
///
/// Появился по тикету kiselyov.a («практически всегда уведомления остаются пустыми; счётчик
/// убавлять ТОЛЬКО на прочитанных; историю сохранять; удалять поштучно»). До него всё это было
/// размазано: список — ObservableCollection прямо в MainWindowViewModel (в памяти, до перезапуска),
/// счётчик — отдельное число, которое обнулялось по факту ОТКРЫТИЯ окна, а признак «прочитано»
/// ставился всему списку разом при его закрытии.
///
/// Правила, ради которых это один класс:
/// <list type="bullet">
/// <item>база — источник правды, коллекция её зеркалит; после перезапуска история на месте;</item>
/// <item>счётчик СЧИТАЕТСЯ по коллекции, а не накапливается отдельным числом — рассинхронизироваться
/// с содержимым он теперь не может в принципе;</item>
/// <item>в счётчик идут только непрочитанные и только тех категорий, которым это разрешено
/// (ConfigService.IsNotificationCategoryCountedUnread). Категорию переключили — счётчик
/// пересчитается, для этого есть <see cref="Refresh"/>.</item>
/// </list>
///
/// Потокобезопасности здесь нет и не задумано: зовётся только из потока интерфейса, как и прежний
/// код в MainWindowViewModel.</summary>
public class NotificationCenter : ObservableObject
{
    /// <summary>Сколько уведомлений держим. Было 100 на память одного запуска; теперь история живёт
    /// между запусками, и на этом же числе она перестала бы доживать до конца недели. Верхнюю
    /// границу всё равно оставляем: список, который нельзя прокрутить до конца, — не история.</summary>
    public const int HistoryLimit = 200;

    private readonly Database _db;
    private readonly ConfigService _cfg;

    public NotificationCenter(Database db, ConfigService cfg)
    {
        _db = db;
        _cfg = cfg;
        foreach (var stored in _db.GetNotifications(HistoryLimit))
            History.Add(new NotificationEntry(stored));
    }

    /// <summary>Вся история, свежие сверху. Прочитанные из неё НЕ вычищаются — см.
    /// NotificationEntry.IsRead: именно скрытие прочитанных и делало окно пустым.</summary>
    public ObservableCollection<NotificationEntry> History { get; } = new();

    /// <summary>Число на колокольчике. Считается, а не хранится.</summary>
    public int UnreadCount => History.Count(e => !e.IsRead && _cfg.IsNotificationCategoryCountedUnread(e.Category));

    /// <summary>Пересчитать счётчик — после того как снаружи поменяли настройку категорий.</summary>
    public void Refresh() => OnPropertyChanged(nameof(UnreadCount));

    /// <summary>Записать уведомление. Повтор того же текста поднимает существующую строку наверх и
    /// увеличивает её счётчик повторов (правило живёт в Database.SaveNotification — там же, где сама
    /// история, иначе после перезапуска повтор заводил бы вторую строку).</summary>
    public NotificationEntry Add(string text, NotificationCategory category, Action? reopen = null, bool reopenIsModal = false)
    {
        var saved = _db.SaveNotification(text, category, DateTime.Now, HistoryLimit);
        var existing = History.FirstOrDefault(e => e.Id == saved.Id);

        NotificationEntry entry;
        if (existing is not null)
        {
            existing.When = saved.When;
            existing.Repeats = saved.Repeats;
            existing.IsRead = false;
            existing.IsNew = true;
            // Действие «Показать» обновляем только если пришло новое: у повтора баннера оно живое, а
            // у обычного ShowStatus его нет вовсе — и затирать им прежнее нельзя.
            if (reopen is not null)
            {
                existing.Reopen = reopen;
                existing.ReopenIsModal = reopenIsModal;
            }
            var at = History.IndexOf(existing);
            if (at > 0) History.Move(at, 0);
            entry = existing;
        }
        else
        {
            entry = new NotificationEntry(saved, reopen, reopenIsModal);
            History.Insert(0, entry);
            while (History.Count > HistoryLimit)
                History.RemoveAt(History.Count - 1);
        }

        Refresh();
        MirrorToEmail(text, category);
        return entry;
    }

    /// <summary>Дубль на почту (бета). Просьба владельца: «чтобы уведомление о тикетах, выходе новой
    /// прошивки и тп дублировались на почту».
    ///
    /// Три правила, и все три важны:
    ///
    /// 1. В ФОНЕ. Отправка письма — это сеть, а значит секунды ожидания на молчащем сервере. Здесь
    ///    поток интерфейса: синхронная отправка подвешивала бы окно на каждом уведомлении.
    /// 2. ОДИН РАЗ НА ПЕРЕХОДЕ. Раньше здесь был глухой `Task.Run(...)` без разбора ответа: почта
    ///    могла не работать месяцами, и узнать об этом было негде. Теперь о поломке говорится —
    ///    но ровно один раз, на переходе в «не работает», как у автоотправки конфига и проверки
    ///    обновлений. Зацикливания нет: уведомление о неудаче помечается флагом и само почтой
    ///    не дублируется (см. <see cref="_reportingMailFailure"/>).
    /// 3. ЭТО ДУБЛЬ. Уведомление всегда остаётся в программе. Поэтому и накопление в сводку
    ///    безопасно: ничто не пропадает, откладывается только письмо.</summary>
    /// <summary>Пропускает ли ЛИЧНАЯ настройка этого адреса такую категорию.
    ///
    /// Касается ровно одного адреса — того, что человек указал своим (ConfigService.MyEmail). Все
    /// прочие адресаты получают письма по общим правилам, как и раньше: личная настройка на этой
    /// машине не вправе решать за коллег.
    ///
    /// Пустой список категорий — «всё подряд»: настройка, появившаяся пустой, обязана означать
    /// «как было».</summary>
    private bool PersonalFilterAllows(string recipient, string category)
    {
        var mine = _cfg.MyEmail();
        if (mine.Length == 0 || !string.Equals(mine, recipient, StringComparison.OrdinalIgnoreCase)) return true;

        var allowed = _cfg.MyEmailCategories();
        return allowed.Count == 0
            || allowed.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));
    }

    private void MirrorToEmail(string text, NotificationCategory category)
    {
        // Уведомление «почта не работает» само по почте не уходит — иначе неудачная отправка
        // порождала бы уведомление, которое снова пыталось бы уйти почтой, и так по кругу.
        if (_reportingMailFailure) return;

        try
        {
            if (!_cfg.Smtp().CanSend) return;

            var schedule = _cfg.EmailDelivery();
            // Категория, выключенная в настройках «что слать», отсекается ЗДЕСЬ, до накопителя:
            // правила решают кому, эта настройка — что. Складывать в очередь то, что всё равно не
            // уйдёт, значит однажды прислать это сводкой после смены настроек.
            if (!schedule.CategoryAllowed(category.ToString())) return;

            lock (_pendingLock)
                _pending.Add(new PendingEmail(DateTime.Now, category.ToString(), text));
        }
        catch
        {
            // Настройки не прочитались — это не повод уронить само уведомление, ради которого сюда пришли.
            return;
        }

        PumpEmailQueue();
    }

    // ── Очередь писем и расписание ────────────────────────────────────
    //
    // Вся арифметика «слать сейчас или ждать» живёт в Core.Domain.EmailSchedule — чистой функцией,
    // которую можно проверить тестом. Здесь остаётся только то, что тестом не проверяется: таймер,
    // блокировка списка и собственно сеть.
    //
    // Очередь живёт В ПАМЯТИ и переживает перезапуск не больше, чем окно программы. Это осознанно:
    // почта — ДУБЛЬ, событие уже лежит в истории уведомлений и никуда не делось, а очередь в базе
    // означала бы пачку писем недельной давности после починки сервера — ровно то, от чего
    // отказались в EmailNotifier.

    private readonly object _pendingLock = new();
    private readonly List<PendingEmail> _pending = new();
    private System.Threading.Timer? _emailTimer;
    private bool _emailSending;
    private bool _mailLastFailed;
    private bool _reportingMailFailure;

    /// <summary>Точка отсчёта для режима «раз в N часов». Момент старта программы, а не
    /// DateTime.MinValue: иначе первая же строка улетела бы мгновенно, и режим сводок выглядел бы
    /// невключившимся.</summary>
    private DateTime _lastEmailSentAt = DateTime.Now;

    /// <summary>Как часто спрашиваем расписание. Минута — не точность доставки, а цена опоздания:
    /// сводка «в 9:00», ушедшая в 9:00:40, человеку неотличима от точной, а тик раз в секунду ради
    /// этого будит процесс 86 тысяч раз в сутки.</summary>
    private static readonly TimeSpan EmailTick = TimeSpan.FromMinutes(1);

    /// <summary>Разобрать очередь: спросить расписание и либо отправить, либо продолжить ждать.
    /// Зовётся и из <see cref="MirrorToEmail"/> (новая строка — режим «сразу» уедет без задержки),
    /// и по таймеру.</summary>
    private void PumpEmailQueue()
    {
        EnsureEmailTimer();

        if (_emailSending) return;

        EmailScheduleDecision decision;
        SmtpSettings smtp;
        List<EmailRule> rules;
        try
        {
            smtp = _cfg.Smtp();
            if (!smtp.CanSend) return;
            rules = _db.GetEmailRules();

            PendingEmail[] snapshot;
            lock (_pendingLock) snapshot = _pending.ToArray();
            decision = Core.Domain.EmailSchedule.Decide(_cfg.EmailDelivery(), DateTime.Now, snapshot, _lastEmailSentAt);
        }
        catch
        {
            return;
        }

        if (!decision.SendNow || decision.Items.Count == 0) return;

        // Из очереди убираем СРАЗУ, до отправки: иначе следующий тик (или новое уведомление) успел
        // бы подхватить те же строки и прислать их вторым письмом.
        lock (_pendingLock)
            foreach (var item in decision.Items) _pending.Remove(item);

        _lastEmailSentAt = DateTime.Now;
        _emailSending = true;

        // Письма режутся по адресатам, а не шлются одним на всех: категории у строк разные, а
        // правила решают, кому какая положена. Сводка «всё подряд» в адрес того, кто подписан
        // только на прошивки, — это утечка лишнего, а не удобство.
        var byRecipient = new Dictionary<string, List<PendingEmail>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in decision.Items)
            foreach (var to in EmailRouting.RecipientsFor(item.Category, rules))
            {
                // ЛИЧНАЯ НАСТРОЙКА ЧЕЛОВЕКА СИЛЬНЕЕ ОБЩЕГО ПРАВИЛА — но только в сторону «меньше».
                //
                // Жалоба: «наладчик который заведен не может настроить себе рассылку и опять весь
                // спам получает». Общий список заводит администратор, и наладчику туда нельзя: он
                // менял бы рассылку коллегам. Зато на СВОЙ адрес он вправе сказать «мне только
                // прошивки» — и это обязано работать поверх общего «всё подряд».
                //
                // Расширять себе рассылку личной настройкой нельзя: не подписанный администратором
                // адрес не начнёт получать лишнее (см. ConfigService.MyEmail).
                if (!PersonalFilterAllows(to, item.Category)) continue;

                if (!byRecipient.TryGetValue(to, out var list)) byRecipient[to] = list = new List<PendingEmail>();
                list.Add(item);
            }

        _ = Task.Run(() =>
        {
            var error = "";
            foreach (var (to, items) in byRecipient)
            {
                var (subject, body) = Core.Domain.EmailSchedule.Compose(items);
                var result = EmailNotifier.Send(smtp, new[] { to }, subject, body);
                if (result.Length > 0 && error.Length == 0) error = result;
            }
            return error;
        }).ContinueWith(task =>
        {
            _emailSending = false;
            var error = task.IsCompletedSuccessfully ? task.Result : "не удалось отправить письмо";
            // Некому было слать (все адресаты — группы AD) — это не поломка почты, об этом говорит
            // сводка готовности на вкладке «Почта», а не уведомление на каждое событие.
            if (byRecipient.Count == 0) return;
            ReportMailResult(error);
        }, TaskScheduler.Default);
    }

    /// <summary>Сказать о поломке почты один раз — на переходе в «не работает», и один раз о
    /// починке. Правило то же, что у автоотправки конфига и проверки обновлений: тихо молчащая
    /// почта уже стоила месяцев «а я думал, оно работает», а сообщение на каждую неудачу превратило
    /// бы колокольчик в журнал сетевых таймаутов.</summary>
    private void ReportMailResult(string error)
    {
        if (error.Length == 0)
        {
            if (!_mailLastFailed) return;
            _mailLastFailed = false;
            PostNotification("Почта снова работает — дублирование уведомлений восстановилось.", NotificationCategory.General);
            return;
        }

        if (_mailLastFailed) return;
        _mailLastFailed = true;
        PostNotification($"Уведомления не уходят на почту: {error} Настройки → Почта.", NotificationCategory.General);
    }

    /// <summary>Уведомление из фонового потока. Add трогает ObservableCollection, а значит обязан
    /// идти через диспетчер интерфейса — иначе падение «объект принадлежит другому потоку» прямо
    /// в момент, когда мы сообщаем о чужой поломке.</summary>
    private void PostNotification(string text, NotificationCategory category)
    {
        void Report()
        {
            _reportingMailFailure = true;
            try { Add(text, category); }
            finally { _reportingMailFailure = false; }
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Report();
        else dispatcher.BeginInvoke(Report);
    }

    private void EnsureEmailTimer()
    {
        // Таймер заводится лениво, при первом же письме: у машины, где почту не настраивали (а это
        // большинство), фонового тика быть не должно вовсе.
        // Тик уходит в поток интерфейса намеренно: PumpEmailQueue читает правила из SQLite, а весь
        // остальной класс ходит в базу только отсюда. Разбор очереди на потоке таймера завёл бы
        // второй поток у соединения, которое на это не рассчитано, — и падало бы оно редко и не там.
        _emailTimer ??= new System.Threading.Timer(_ =>
        {
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is null) return;
                dispatcher.BeginInvoke(() =>
                {
                    try { PumpEmailQueue(); }
                    catch { /* очередь писем не повод ронять окно */ }
                });
            }
            catch { /* фоновому тику некуда падать — иначе он убьёт процесс целиком */ }
        }, null, EmailTick, EmailTick);
    }

    /// <summary>Пометить одно уведомление прочитанным — зовётся, когда строка ДЕЙСТВИТЕЛЬНО показана
    /// человеку в окне истории (см. NotificationHistoryWindow.NotificationRow_Loaded), а не по факту
    /// открытия окна. Ровно этого требует тикет: «убирать из счётчика количество ТОЛЬКО
    /// прочитанные».</summary>
    public void MarkRead(NotificationEntry entry)
    {
        if (entry.IsRead) return;
        entry.IsRead = true;
        _db.MarkNotificationRead(entry.Id);
        Refresh();
    }

    /// <summary>«Отметить все прочитанными» — явная кнопка. Именно кнопка, а не побочный эффект
    /// открытия окна: разгрести счётчик одним движением надо уметь, но это решение человека.</summary>
    public void MarkAllRead()
    {
        var changed = false;
        foreach (var entry in History)
        {
            if (!entry.IsRead)
            {
                entry.IsRead = true;
                changed = true;
            }
            // Пометку «новое» снимаем тоже: человек нажал «всё прочитано» — значит и подсветке
            // новизны взяться неоткуда (см. NotificationEntry.IsNew).
            entry.IsNew = false;
        }
        if (!changed) return;
        _db.MarkAllNotificationsRead();
        Refresh();
    }

    /// <summary>Удалить одно уведомление.</summary>
    public void Delete(NotificationEntry entry)
    {
        History.Remove(entry);
        _db.DeleteNotification(entry.Id);
        Refresh();
    }

    public void Clear()
    {
        History.Clear();
        _db.ClearNotifications();
        Refresh();
    }
}
